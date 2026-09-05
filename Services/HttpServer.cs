using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SLDataAPI.Control;
using SLDataAPI.Data;

namespace SLDataAPI.Services;

public class HttpServer
{
    private readonly int _port;
    private readonly Config _config;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    // 并发连接上限：超出直接拒绝（快速失败），防止恶意连接风暴耗尽线程池
    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(64, 64);

    // 请求头（请求行+头部）读取上限：防止无换行的超长头行造成内存 DoS
    private const int MaxHeaderBytes = 16384;

    // 请求总时限（收完头+体的最长时间）：防 Slowloris——慢速滴字节可以绕过单次 Read
    // 的接收超时（每 29s 发 1 字节即可无限占用连接），必须用总时限兜底。
    // 只约束"读请求"阶段，不影响控制端点最长 5s 的主线程派发与响应写出。
    private const int RequestDeadlineMs = 15000;

    public HttpServer(int port, Config config)
    {
        _port = port;
        _config = config;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // TcpListener 绑定 0.0.0.0，避免 HttpListener 在 Windows 上依赖 http.sys / netsh urlacl。
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _acceptTask?.Wait(1000); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpListener? listener = _listener;
            if (listener == null)
            {
                // Start() 尚未调用或已 Stop()
                await Task.Delay(100, ct);
                continue;
            }

            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            var _ = Task.Run(() => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        // 连接数饱和时立即拒绝，不排队（避免线程池被慢连接拖垮）
        if (!Gate.Wait(0))
        {
            try { client.Close(); } catch { /* 忽略 */ }
            return;
        }

        try
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    // Socket 超时：必须大于控制接口的主线程派发超时（5s）+ 网络余量。
                    // 旧版 8s 时主线程操作（effect/state 等）处理稍慢就直接断连，
                    // 客户端（webui 代理）表现为"代理转发失败 / 502"。
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;

                    string remoteIp = "unknown";
                    try { remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint)?.Address.ToString() ?? "unknown"; }
                    catch { }

                        using (var stream = client.GetStream())
                        {
                            // ---- Slowloris 防护：请求必须整体在时限内送达 ----
                            int startTick = Environment.TickCount;

                            // ---- 读请求头（请求行+头部），上限 16KB ----
                            byte[] headBuf = new byte[MaxHeaderBytes];
                            int headLen = 0;
                            int headEnd = -1;
                            while (headLen < headBuf.Length)
                            {
                                int n = stream.Read(headBuf, headLen, headBuf.Length - headLen);
                                if (n <= 0) break;
                                headLen += n;
                                headEnd = FindHeaderEnd(headBuf, headLen);
                                if (headEnd >= 0) break;
                                if (unchecked(Environment.TickCount - startTick) > RequestDeadlineMs)
                                {
                                    SendJson(stream, 408, Err("请求超时：头部长时间未收完（Slowloris 防护）"));
                                    return;
                                }
                            }
                        if (headEnd < 0)
                        {
                            SendJson(stream, 431, Err("请求头过大或格式非法"));
                            return;
                        }

                        string headText = Encoding.ASCII.GetString(headBuf, 0, headEnd);
                        string[] headLines = headText.Split('\n');
                        if (headLines.Length == 0)
                        {
                            SendJson(stream, 400, Err("bad request"));
                            return;
                        }

                        string requestLine = headLines[0].TrimEnd('\r');
                        var parts = requestLine.Split(' ');
                        if (parts.Length < 2)
                        {
                            SendJson(stream, 400, Err("bad request"));
                            return;
                        }

                        string method = parts[0];
                        string rawTarget = parts[1];

                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 1; i < headLines.Length; i++)
                        {
                            string line = headLines[i].TrimEnd('\r');
                            if (line.Length == 0) continue;
                            int idx = line.IndexOf(':');
                            if (idx <= 0) continue;
                            headers[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                        }

                        string path = rawTarget;
                        string query = "";
                        int qIdx = rawTarget.IndexOf('?');
                        if (qIdx >= 0)
                        {
                            path = rawTarget.Substring(0, qIdx);
                            query = rawTarget.Substring(qIdx + 1);
                        }

                        // ---- 读请求体（Content-Length，上限 64KB） ----
                        int contentLength = 0;
                        if (headers.TryGetValue("Content-Length", out var clStr) &&
                            int.TryParse(clStr, out int cl) && cl > 0)
                            contentLength = cl;

                        if (contentLength > 65536)
                        {
                            SendJson(stream, 413, Err("payload too large"));
                            return;
                        }

                        string body = "";
                        if (contentLength > 0)
                        {
                            // 头缓冲里可能已读入了部分 body 前缀，先接上再补读
                            var bodyBuf = new byte[contentLength];
                            int total = 0;
                            int prefixLen = headLen - headEnd;
                            if (prefixLen > 0)
                            {
                                int copy = Math.Min(prefixLen, contentLength);
                                Array.Copy(headBuf, headEnd, bodyBuf, 0, copy);
                                total = copy;
                            }
                            while (total < contentLength)
                            {
                                int n = stream.Read(bodyBuf, total, contentLength - total);
                                if (n <= 0) break;
                                total += n;
                                if (unchecked(Environment.TickCount - startTick) > RequestDeadlineMs)
                                {
                                    SendJson(stream, 408, Err("请求超时：请求体长时间未收完（Slowloris 防护）"));
                                    return;
                                }
                            }
                            body = Encoding.UTF8.GetString(bodyBuf, 0, total);
                        }

                        // ---- 控制接口 WebSocket 升级（v2.5）----
                        // /ws/control 长连接：call/result 信封，语义与 /control/* HTTP 完全一致。
                        // 返回 true 表示连接已被完整接管（含整个 WS 会话生命周期）。
                        if (WsControlService.TryHandle(client, stream, method, path, query, headers, remoteIp, _config))
                            return;

                        Route(stream, remoteIp, method, path, query, headers, body);
                    }
                }
                catch
                {
                    // 单个连接出错不影响其他连接，也不影响 accept 循环
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>在缓冲区内找 \r\n\r\n（请求头结束标记），找不到返回 -1。</summary>
    private static int FindHeaderEnd(byte[] buf, int len)
    {
        for (int i = 0; i + 3 < len; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i + 4;
        }
        return -1;
    }

    private void Route(Stream stream, string remoteIp, string method, string path, string query,
        Dictionary<string, string> headers, string body)
    {
        try
        {
            // ---- 只读数据接口（原有行为不变） ----
            if (path == "/get_sl_data")
            {
                if (method != "GET")
                {
                    SendJson(stream, 405, Err("Method Not Allowed"));
                    return;
                }

                string reqToken = ExtractQueryValue(query, "token");
                // 与控制接口共用同一套防爆破锁定（常量时间比较 + 按 IP 锁定）
                if (!ControlAuth.TryAuthenticate(remoteIp, reqToken, _config.VerifyToken ?? "", out string readErr, highPrivilege: false))
                {
                    Log.Warn($"[SLDataAPI] 数据接口鉴权失败 from {remoteIp}: {readErr} {ControlAuth.DescribeMismatch(reqToken, _config.VerifyToken)}");
                    SendJson(stream, 403, Err(readErr));
                    return;
                }

                // 快照模式：BuildJson 只读 volatile 快照 + 局部 Clone + 主线程派发，无共享可变状态，无需加锁
                string json = DataCollector.BuildJson();
                SendJson(stream, 200, json);
                return;
            }

            // ---- 控制接口 ----
            if (path.StartsWith("/control/"))
            {
                if (!_config.ControlEnabled)
                {
                    SendJson(stream, 404, Err("控制接口未启用"));
                    return;
                }

                // 传输方式互斥（control_transport）：ws 模式下 HTTP 控制端点整体禁用，
                // 只留 WS 一条通路（http 模式下本检查不触发，HTTP 正常）。
                // data.code = transport_mismatch 是给上游平台的机器可读协商信号：
                // 收到后应改走 /ws/control 的 call 通道重试。
                if (string.Equals(_config.ControlTransport, "ws", StringComparison.OrdinalIgnoreCase))
                {
                    SendJson(stream, 404, Newtonsoft.Json.JsonConvert.SerializeObject(new ControlResponse
                    {
                        success = false,
                        message = "控制接口已切换为 WebSocket 模式（control_transport: ws），HTTP /control/* 已禁用",
                        data = new { code = "transport_mismatch", use = "ws" },
                    }));
                    return;
                }

                // v2.6.0-preview-DevOnly 推出，代号 Kerckhoffs：控制面 API Key（Bearer / X-SLDataAPI-Key）；不再接受 X-Control-Token / ?token=
                string? apiKey = SLDataAPI.Auth.ApiKeyService.ExtractKeyFromHeaders(headers);
                if (!SLDataAPI.Auth.ApiKeyService.TryAuthenticate(remoteIp, apiKey, out var principal, out string authErr))
                {
                    Log.Warn($"[SLDataAPI][Control] 鉴权失败 from {remoteIp}: {authErr}");
                    SendJson(stream, 401, Err(authErr));
                    return;
                }

                if (method != "POST")
                {
                    SendJson(stream, 405, Err("控制接口仅支持 POST"));
                    return;
                }

                bool wantWrite = SLDataAPI.Auth.EndpointAcl.IsWriteOperation(path, body);
                if (principal == null || !principal.Allows(path, wantWrite))
                {
                    Log.Warn($"[SLDataAPI][Control] 端点未授权 key={principal?.Id} path={path} write={wantWrite}");
                    SendJson(stream, 403, Err("API Key 有效但未授权该端点"));
                    return;
                }

                var (status, json) = ControlController.Handle(path, body, principal.Id);
                SendJson(stream, status, json);
                return;
            }

            SendJson(stream, 404, Err("路径不存在"));
        }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI] 路由处理异常: {ex}");
            try { SendJson(stream, 500, Err("内部错误")); } catch { }
        }
    }

    private static void SendJson(Stream stream, int code, string jsonBody)
    {
        byte[] bodyBytes = string.IsNullOrEmpty(jsonBody)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(jsonBody);

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(ReasonPhrase(code)).Append("\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");

        byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(header, 0, header.Length);
        if (bodyBytes.Length > 0)
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    private static string ReasonPhrase(int code)
    {
        switch (code)
        {
            case 200: return "OK";
            case 400: return "Bad Request";
            case 401: return "Unauthorized";
            case 403: return "Forbidden";
            case 404: return "Not Found";
            case 405: return "Method Not Allowed";
            case 408: return "Request Timeout";
            case 413: return "Payload Too Large";
            case 431: return "Request Header Fields Too Large";
            case 500: return "Internal Server Error";
            case 501: return "Not Implemented";
            default: return "Unknown";
        }
    }

    private static string Err(string message) =>
        Newtonsoft.Json.JsonConvert.SerializeObject(new ControlResponse { success = false, message = message });

    /// <summary>从 URL 查询串提取首个指定键的值（WsControlService 复用）。</summary>
    internal static string ExtractQueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return "";
        foreach (var pair in query.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;
            int eq = pair.IndexOf('=');
            string k = eq >= 0 ? pair.Substring(0, eq) : pair;
            string v = eq >= 0 ? pair.Substring(eq + 1) : "";
            if (k == key)
                return Uri.UnescapeDataString(v);
        }
        return "";
    }
}
