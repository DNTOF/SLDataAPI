using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SLDataAPI.Data;
using SLDataAPI.Services;

namespace SLDataAPI.Control;

/// <summary>
/// /control —— 控制接口的 WebSocket 长连接端点（v2.5 新增，对应平台 ws-control-design 的 Phase 3）。
///
/// /control/ 是公用的控制命名空间（与传输方式无关的地址语义）：
///   - 一次性调用：HTTP POST /control/*
///   - 长连接调用：WS 升级 /control 或 /ws/control（别名），call 里的 path 就是 /control/*
///   - 但同一时刻只有一条通路开放（control_transport 二选一硬互斥，见下）
///
/// 动机：HTTP /control/* 每次调用都要完整建链（TCP 握手 + HTTP 头 + 拆链），
/// 上游平台对同一台服务器高频调用时开销与延迟都显著；长连接复用后仅剩帧级开销。
///
/// 协议（JSON 文本帧，UTF-8，信封对齐平台端 call/result 设计）：
///   建连后 S→C {"type":"hello","version":...,"endpoints":"/control/*"}
///   C→S {"type":"ping"}                          → S→C {"type":"pong"}
///   C→S {"type":"call","reqId","path","body"?}   → S→C {"type":"result","reqId","ok","status","data"/"message"}
/// path 必须是现有 /control/* 端点；语义与 HTTP POST 完全一致（同一个 ControlController 分发），
/// 平台可把 HTTP 调用一对一映射到 WS call，reqId 关联请求与响应（结果允许乱序返回）。
///
/// 鉴权：与 HTTP 控制接口同一套——升级前在 HTTP 层校验 ?key= / ?token= / X-Control-Token，
/// 走 ControlAuth（常量时间比较 + 按 IP 失败锁定）；ControlEnabled=false 时一律 404。
///
/// 连接方式互斥（Config.ControlTransport）：仅 "ws" 模式下本端点可用；
/// "http" 模式下握手直接 404（反之 ws 模式下 HTTP /control/* 同样 404），
/// 任何时刻只有一条控制通路。数据轮询接口 /get_sl_data 不受影响。
///
/// 限制：全局连接上限 8；单连接并发 call 上限 4（超出立即回失败 result）；
/// 单消息上限 256KB（超限按 1009 断开）；90s 无任何入站消息判定超时断开（客户端应 ~25s 心跳）。
///
/// 线程模型：每个连接占用 HttpServer 的一个工作线程（阻塞读循环）与一个并发闸位，
/// 受上述上限约束；游戏状态调用经 ControlController 内部的 MainThreadExecutor 派发主线程。
/// 数据轮询接口 /get_sl_data 与 HTTP /control/* 完全不受影响（保留作降级路径）。
/// </summary>
public static class WsControlService
{
    private const int MaxClients = 8;
    private const int MaxConcurrentCallsPerConnection = 4;
    private const int MaxMessageBytes = 256 * 1024;
    private const int IdleTimeoutMs = 90_000;
    private const int SweepIntervalMs = 20_000;

    /// <summary>单条消息允许的最长组装时间：分片慢速滴流（夹 ping 保活绕过空闲超时）超过即断开。</summary>
    private const int MaxMessageAssemblySeconds = 30;

    private static readonly object RegistryLock = new();
    private static readonly List<Session> Sessions = new();
    private static Timer? _idleSweeper;

    /// <summary>
    /// HttpServer 在解析完请求头后调用。返回 true 表示该连接已被本服务完整接管
    /// （无论握手成功进入会话、还是鉴权/上限失败已写完 HTTP 响应），调用方直接结束处理。
    /// 返回 false 表示不是 /ws/control 请求，继续走普通路由。
    /// </summary>
    public static bool TryHandle(
        System.Net.Sockets.TcpClient client, Stream stream, string method, string path, string query,
        Dictionary<string, string> headers, string remoteIp, Config config)
    {
        // /control/ 是与传输无关的公用命名空间：/control 与 /ws/control 都接受 WS 升级
        //（后者是早期别名，兼容已按旧文档对接的平台），call 里的 path 继续用 /control/*。
        if (!path.Equals("/control", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("/ws/control", StringComparison.OrdinalIgnoreCase))
            return false;

        if (method != "GET" || !IsWebSocketUpgrade(headers))
        {
            WriteHttpJson(stream, 400, "该路径的 WebSocket 需要 GET 升级请求（Upgrade: websocket）；一次性调用请直接 POST /control/*");
            return true;
        }

        if (!config.ControlEnabled)
        {
            // 与 HTTP /control/* 的门控行为一致：未启用时一律 404
            WriteHttpJson(stream, 404, "控制接口未启用");
            return true;
        }

        // 传输方式互斥（control_transport）：仅 ws 模式下 WS 通道开放，
        // http 模式下握手直接 404（反向同理：ws 模式下 HTTP /control/* 404）。
        // data.code 是给上游平台的机器可读协商信号。
        if (!string.Equals(config.ControlTransport, "ws", StringComparison.OrdinalIgnoreCase))
        {
            WriteHttpJson(stream, 404, "控制接口为 HTTP 模式（control_transport: http），WebSocket 未启用",
                new { code = "transport_mismatch", use = "http" });
            return true;
        }

        // token 传参方式与语音 WS（?key=）、HTTP 控制接口（X-Control-Token / ?token=）对齐
        string key = HttpServer.ExtractQueryValue(query, "key");
        if (string.IsNullOrEmpty(key)) key = HttpServer.ExtractQueryValue(query, "token");
        if (string.IsNullOrEmpty(key) && headers.TryGetValue("X-Control-Token", out var headerToken))
            key = headerToken;

        if (!ControlAuth.TryAuthenticate(remoteIp, key, config.ControlToken ?? "", out string authErr))
        {
            Log.Warn($"[SLDataAPI][WsControl] 鉴权失败 from {remoteIp}: {authErr} {ControlAuth.DescribeMismatch(key, config.ControlToken)}");
            WriteHttpJson(stream, 403, authErr);
            return true;
        }

        lock (RegistryLock)
        {
            if (Sessions.Count >= MaxClients)
            {
                WriteHttpJson(stream, 503, $"控制 WebSocket 连接数已满（上限 {MaxClients}）");
                return true;
            }
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out string? wsKey) || string.IsNullOrEmpty(wsKey))
        {
            WriteHttpJson(stream, 400, "缺少 Sec-WebSocket-Key");
            return true;
        }

        // 握手（101），失败按普通 HTTP 400 结束
        try
        {
            string accept = Convert.ToBase64String(
                SHA1.Create().ComputeHash(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] handshake = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n" +
                "\r\n");
            stream.Write(handshake, 0, handshake.Length);
            stream.Flush();
        }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI][WsControl] 握手写入失败: {ex.Message}");
            return true;
        }

        // 长连接不设接收超时（心跳由协议层 ping/pong + 空闲清扫器负责）
        client.ReceiveTimeout = 0;

        var session = new Session(stream, remoteIp);
        int current;
        lock (RegistryLock)
        {
            Sessions.Add(session);
            current = Sessions.Count;
            EnsureIdleSweeper();
        }

        Log.Info($"[SLDataAPI][WsControl] 控制通道已建立 from {remoteIp}（当前 {current}/{MaxClients}）");

        try
        {
            session.Run();
        }
        finally
        {
            lock (RegistryLock) Sessions.Remove(session);
        }
        return true;
    }

    /// <summary>插件停用时由 Plugin.Disable 调用：停清扫器并断开全部会话。</summary>
    public static void ShutdownAll()
    {
        Timer? sweeper;
        Session[] snapshot;
        lock (RegistryLock)
        {
            sweeper = _idleSweeper;
            _idleSweeper = null;
            snapshot = Sessions.ToArray();
            Sessions.Clear();
        }
        sweeper?.Dispose();
        foreach (var s in snapshot)
            s.Close();
    }

    private static void EnsureIdleSweeper()
    {
        if (_idleSweeper != null) return;
        _idleSweeper = new Timer(_ =>
        {
            Session[] snapshot;
            lock (RegistryLock) snapshot = Sessions.ToArray();
            foreach (var s in snapshot)
            {
                if ((DateTime.UtcNow - s.LastActivity).TotalMilliseconds > IdleTimeoutMs)
                {
                    Log.Info("[SLDataAPI][WsControl] 连接空闲超时，已断开");
                    s.Close();
                }
            }
        }, null, SweepIntervalMs, SweepIntervalMs);
    }

    private static bool IsWebSocketUpgrade(Dictionary<string, string> headers)
    {
        bool upgrade = headers.TryGetValue("Upgrade", out var up) &&
                       up.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0;
        bool connection = headers.TryGetValue("Connection", out var conn) &&
                          conn.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0;
        return upgrade && connection;
    }

    private static void WriteHttpJson(Stream stream, int code, string message, object? data = null)
    {
        try
        {
            string json = JsonConvert.SerializeObject(new ControlResponse { success = false, message = message, data = data });
            byte[] body = Encoding.UTF8.GetBytes(json);
            string reason = code switch
            {
                400 => "Bad Request",
                403 => "Forbidden",
                404 => "Not Found",
                503 => "Service Unavailable",
                _ => "Error",
            };
            byte[] head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {code} {reason}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }
        catch { /* 对端已断开则忽略 */ }
    }

    // ────────────── 单个控制 WS 会话 ──────────────
    private sealed class Session
    {
        private readonly Stream _stream;
        private readonly string _remoteIp;
        private readonly object _sendLock = new();
        private int _pendingCalls;
        private volatile bool _closed;

        public DateTime LastActivity = DateTime.UtcNow;

        public Session(Stream stream, string remoteIp)
        {
            _stream = stream;
            _remoteIp = remoteIp;
        }

        public void Close()
        {
            _closed = true;
            try { _stream.Close(); } catch { /* 忽略 */ }
        }

        /// <summary>阻塞读循环：握手已完成，处理 WS 帧直到连接关闭。运行在 HttpServer 工作线程。</summary>
        public void Run()
        {
            SendJson(new JObject
            {
                ["type"] = "hello",
                ["server"] = "SLDataAPI",
                ["version"] = Plugin.Instance?.Version?.ToString() ?? "",
                ["endpoints"] = "/control/*",
            });

            var fragments = new List<byte>(1024);
            int messageOpcode = -1;
            DateTime messageStarted = DateTime.UtcNow;
            var head = new byte[2];

            while (!_closed)
            {
                if (ReadExact(head, 2) < 2) break;
                LastActivity = DateTime.UtcNow;

                int b0 = head[0];
                int b1 = head[1];
                int opcode = b0 & 0x0F;
                bool fin = (b0 & 0x80) != 0;
                bool rsv = (b0 & 0x70) != 0;
                bool masked = (b1 & 0x80) != 0;
                long len = b1 & 0x7F;

                if (len == 126)
                {
                    var ext = new byte[2];
                    if (ReadExact(ext, 2) < 2) break;
                    len = (ext[0] << 8) | ext[1];
                }
                else if (len == 127)
                {
                    var ext = new byte[8];
                    if (ReadExact(ext, 8) < 8) break;
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
                }

                if (rsv) { CloseWith(1002, "不支持扩展位"); break; }
                if (len > MaxMessageBytes) { CloseWith(1009, "消息过大"); break; }

                var mask = new byte[4];
                if (masked && ReadExact(mask, 4) < 4) break;
                var payload = new byte[len];
                if (len > 0 && ReadExact(payload, payload.Length) < payload.Length) break;
                if (masked)
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                // 控制帧（close/ping/pong）：不参与分片，允许随时插队
                if (opcode == 0x8) { TrySendFrame(0x8, payload); break; }
                if (opcode == 0x9) { TrySendFrame(0xA, payload); continue; }
                if (opcode == 0xA) continue;

                // 数据帧：仅支持文本
                if (opcode == 0x2) { CloseWith(1003, "仅支持文本帧"); break; }
                if (opcode == 0x1)
                {
                    fragments.Clear();
                    messageOpcode = 0x1;
                    messageStarted = DateTime.UtcNow;
                }
                else if (opcode == 0x0)
                {
                    if (messageOpcode < 0) { CloseWith(1002, "意外的续帧"); break; }
                }
                else { CloseWith(1002, "未知 opcode"); break; }

                fragments.AddRange(payload);
                if (fragments.Count > MaxMessageBytes) { CloseWith(1009, "消息过大"); break; }
                // 分片滴流防护：组装中的消息超时（攻击者可夹 ping 帧保活绕过空闲超时）
                if (messageOpcode >= 0 && (DateTime.UtcNow - messageStarted).TotalSeconds > MaxMessageAssemblySeconds)
                {
                    CloseWith(1008, "消息组装超时");
                    break;
                }
                if (!fin) continue;

                messageOpcode = -1;
                byte[] message = fragments.ToArray();
                fragments.Clear();
                HandleTextMessage(Encoding.UTF8.GetString(message));
            }

            Close();
            Log.Debug($"[SLDataAPI][WsControl] 控制通道已断开 from {_remoteIp}");
        }

        private void HandleTextMessage(string text)
        {
            JObject msg;
            try { msg = JObject.Parse(text); }
            catch
            {
                SendJson(new JObject { ["type"] = "error", ["message"] = "消息不是合法 JSON" });
                return;
            }

            string type = msg["type"]?.ToString() ?? "";
            switch (type)
            {
                case "ping":
                    SendJson(new JObject { ["type"] = "pong" });
                    return;

                case "call":
                    HandleCall(msg);
                    return;

                default:
                    SendJson(new JObject { ["type"] = "error", ["message"] = $"未知消息类型: {type}（支持 ping / call）" });
                    return;
            }
        }

        private void HandleCall(JObject msg)
        {
            string reqId = msg["reqId"]?.ToString() ?? "";
            string path = msg["path"]?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/control/", StringComparison.Ordinal))
            {
                SendResult(reqId, ok: false, status: 400, message: "path 必须是 /control/* 端点");
                return;
            }

            if (Interlocked.Increment(ref _pendingCalls) > MaxConcurrentCallsPerConnection)
            {
                Interlocked.Decrement(ref _pendingCalls);
                SendResult(reqId, ok: false, status: 429, message: $"并发调用过多（单连接上限 {MaxConcurrentCallsPerConnection}）");
                return;
            }

            string bodyJson = msg["body"] == null ? "" : JsonConvert.SerializeObject(msg["body"]);

            // 分发到线程池：读循环不被慢调用（主线程派发最长 5s）阻塞，可继续处理心跳
            Task.Run(() =>
            {
                try
                {
                    var (status, json) = ControlController.Handle(path, bodyJson);
                    JToken data;
                    try { data = JToken.Parse(json); }
                    catch { data = new JValue(json); }

                    string? failMessage = status == 200 ? null : data["message"]?.ToString();
                    SendResult(reqId, status == 200, status, failMessage, data);
                }
                catch (Exception ex)
                {
                    Log.Error($"[SLDataAPI][WsControl] call 异常 path={path}: {ex}");
                    SendResult(reqId, ok: false, status: 500, message: "内部错误");
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingCalls);
                }
            });
        }

        private void SendResult(string reqId, bool ok, int status, string? message = null, JToken? data = null)
        {
            var o = new JObject
            {
                ["type"] = "result",
                ["reqId"] = reqId,
                ["ok"] = ok,
                ["status"] = status,
            };
            if (message != null) o["message"] = message;
            if (data != null) o["data"] = data;
            SendJson(o);
        }

        private void SendJson(JObject o) =>
            TrySendFrame(0x1, Encoding.UTF8.GetBytes(o.ToString(Formatting.None)));

        private void TrySendFrame(int opcode, byte[] payload)
        {
            if (_closed) return;
            try
            {
                byte[] frame = EncodeFrame(opcode, payload);
                lock (_sendLock)
                    _stream.Write(frame, 0, frame.Length);
            }
            catch
            {
                Close();
            }
        }

        /// <summary>发送 close 帧并结束会话（协议错误/超限场景）。</summary>
        private void CloseWith(int code, string reason)
        {
            byte[] payload = new byte[2 + Encoding.UTF8.GetByteCount(reason)];
            payload[0] = (byte)(code >> 8);
            payload[1] = (byte)(code & 0xFF);
            Encoding.UTF8.GetBytes(reason, 0, reason.Length, payload, 2);
            TrySendFrame(0x8, payload);
            _closed = true;
        }

        /// <summary>阻塞读满 count 字节；返回 -1 表示连接断开（含被空闲清扫器关闭）。</summary>
        private int ReadExact(byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n;
                try { n = _stream.Read(buf, off, count - off); }
                catch { return -1; }
                if (n <= 0) return -1;
                off += n;
            }
            return count;
        }

        /// <summary>服务器→客户端帧编码（不掩码）。长度编码与语音桥同源，已在线上验证。</summary>
        private static byte[] EncodeFrame(int opcode, byte[] payload)
        {
            byte[] header;
            int off;
            if (payload.Length < 126)
            {
                header = new byte[2];
                header[0] = (byte)(0x80 | opcode);
                header[1] = (byte)payload.Length;
                off = 2;
            }
            else if (payload.Length < 65536)
            {
                header = new byte[4];
                header[0] = (byte)(0x80 | opcode);
                header[1] = 126;
                header[2] = (byte)(payload.Length >> 8);
                header[3] = (byte)(payload.Length & 0xFF);
                off = 4;
            }
            else
            {
                header = new byte[10];
                header[0] = (byte)(0x80 | opcode);
                header[1] = 127;
                long l = payload.Length;
                for (int i = 0; i < 8; i++) header[9 - i] = (byte)(l >> (8 * i));
                off = 10;
            }
            byte[] full = new byte[off + payload.Length];
            Array.Copy(header, full, off);
            Array.Copy(payload, 0, full, off, payload.Length);
            return full;
        }
    }
}
