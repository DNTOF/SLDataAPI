using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MEC;
using PlayerRoles;
using SLDataAPI.Control;
using VoiceChat;
using VoiceChat.Networking;
using Player = LabApi.Features.Wrappers.Player;

namespace SLDataAPI.Voice;

/// <summary>
/// 游戏内语音转发服务（v2.3 新增）。
///
/// 原理：LabAPI 的 SendingVoiceMessage 事件打在
/// VoiceTransceiver.ServerReceiveMessage 上——服务器端收到的所有语音
/// （近距离/对讲机/Intercom/SCP 频道…）都经过这里。每个包是独立的
/// Opus 帧（10ms @48kHz = 480 样本，约 100 包/秒——实测 Decode 返回 480），
/// 解码器按说话者分开维护（Opus 有状态）。
/// 解码为 48kHz 单声道 float32 PCM 后，通过 WebSocket 推送给监听端。
///
/// 端点（独立端口，默认 8082）：
///   GET /ws?key=xxx           → WebSocket 升级，实时语音流（二进制帧）
///   GET /status?key=xxx       → JSON：当前正在说话的玩家（昵称/角色/频道）
///
/// 鉴权：key 必须等于 Config.ControlToken（与控制接口同权限）。
/// 全部在主线程协程中轮询，避免线程安全问题（同 VoiceStreamPlugin 方案）。
///
/// 注意：按"说话者"维护的状态一律用 netId（uint）做键，绝不用 ReferenceHub——
/// 玩家断开后 Hub 的 GameObject 被销毁，ReferenceHub.GetHashCode 会抛 NRE，
/// 进而杀死 MEC 协程、整个转发停摆（线上真实踩过的坑）。
/// </summary>
public static class VoiceService
{
    private const int MaxClients = 8;               // 监听客户端上限（防连接耗尽）
    private const float HandshakeTimeoutSec = 10f;  // 连接后完成握手的最长时间（防 Slowloris 式占用）
    private const float UnwritableDropSec = 3f;     // 发送缓冲持续不可写的判死时间（防主线程卡服）
    private const long MaxMessageBytes = 256 * 1024; // 入站单帧长度上限（L-02，防长度回绕/超大分配）

    private static TcpListener? _listener;
    private static CoroutineHandle _coroutine;
    private static volatile bool _running;
    private static readonly List<VoiceClient> Clients = new();
    private static readonly Queue<PcmPacket> OutQueue = new();
    private static readonly Dictionary<uint, VoiceChat.Codec.OpusDecoder> Decoders = new();
    private static readonly Dictionary<uint, VoiceActivity> Activities = new();
    private static readonly Dictionary<uint, float> _lastPacketTime = new();
        // 每个说话者最近一个包的 FNV 哈希：LabAPI 的 SendingVoiceMessage 每个语音包
        // 只触发一次，这里按内容哈希判重属于纵深防御（防止未来事件源叠加导致重复入队）。
        // 绝不能用 (帧号,频道,长度) 判重——本作语音是 10ms Opus 帧（480 样本，约 100 包/秒），
        // 同一帧内常有两个长度相同的不同包，误删会丢掉一半音频（听着"混乱"的元凶）。
        private static readonly Dictionary<uint, ulong> _dupSeen = new();
    private const int MaxQueuedPackets = 4096;
    private const int MaxSamplesPerPacket = 48000 * 120 / 1000;

    // 诊断统计（限流日志用）
    private static int _pktCount;
    private static int _totalPackets;
    private static float _lastStatsLog;
    private static float _lastHeartbeat;
    private static float _lastWarnLog;
    private static float _lastConnLog;

    // ────────────── 生命周期 ──────────────

    public static void Start(int port)
    {
        if (_running) return;
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            _listener = null;
            Log.Error($"[SLDataAPI] 语音服务无法监听端口 {port}: {ex.Message}");
            return;
        }
        _running = true;
        _coroutine = Timing.RunCoroutine(Tick());
        Log.Info($"[SLDataAPI] 语音转发已启动: ws://<服务器IP>:{port}/ws");
    }

    public static void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_coroutine.IsRunning) Timing.KillCoroutines(_coroutine);
        try { _listener?.Stop(); } catch { /* 忽略 */ }
        _listener = null;
        foreach (var c in Clients)
        {
            try { c.Dispose(); } catch { /* 忽略 */ }
        }
        Clients.Clear();
        OutQueue.Clear();
        foreach (var d in Decoders.Values)
        {
            try { d.Dispose(); } catch { /* 忽略 */ }
        }
        Decoders.Clear();
        Activities.Clear();
        _lastPacketTime.Clear();
        _dupSeen.Clear();
    }

    // ────────────── 语音事件入口（主线程事件回调） ──────────────

    /// <summary>语音事件入口：解码 Opus → 入队推送 + 更新说话状态。</summary>
    public static void HandleIncoming(Player? player, VoiceMessage msg)
    {
        if (!_running || player == null)
            return;

        // Speaker 优先取消息里的，缺失时退回玩家自身的 Hub
        ReferenceHub? speaker = msg.Speaker;
        if (speaker == null)
        {
            try { speaker = player.ReferenceHub; } catch { /* 忽略 */ }
        }
        if (speaker == null)
        {
            RateLimitedWarn("[SLDataAPI] 语音包被跳过: speaker 为 null");
            return;
        }

        uint netId;
        try { netId = speaker.netId; }
        catch (Exception ex)
        {
            RateLimitedWarn($"[SLDataAPI] 读取 netId 失败: {ex.Message}");
            return;
        }

        // 同一包去重：按内容哈希判重，
        // 两个不同的真实语音包（即使同帧同频道同长度）绝不会被误删。
        ulong hash = FnvHash(msg.Data, msg.DataLength);
        if (_dupSeen.TryGetValue(netId, out ulong prevHash) && prevHash == hash)
            return;
        _dupSeen[netId] = hash;

        VoiceChat.Codec.OpusDecoder dec;
        if (!Decoders.TryGetValue(netId, out dec!))
        {
            try { dec = new VoiceChat.Codec.OpusDecoder(); }
            catch (Exception ex)
            {
                Log.Error($"[SLDataAPI] 创建 Opus 解码器失败: {ex}");
                return;
            }
            Decoders[netId] = dec;
        }

        float[] buf = new float[MaxSamplesPerPacket];
        int samples;
        try { samples = dec.Decode(msg.Data, msg.DataLength, buf); }
        catch (Exception ex)
        {
            // 解码器状态损坏时自愈：丢弃重建，下一包恢复
            RateLimitedWarn($"[SLDataAPI] Opus 解码异常(已重置解码器): {ex.Message}");
            try { dec.Dispose(); } catch { /* 忽略 */ }
            Decoders.Remove(netId);
            return;
        }
        if (samples <= 0) return;

        float now = UnityEngine.Time.time;

        // 说话状态（/status 用）：角色实时读
        string roleCn = "未知";
        try { roleCn = GetRoleCN(player.Role); }
        catch { /* 忽略 */ }

        // 录音取证（可选）：复用解码结果，主线程入队、后台线程写盘
        VoiceRecorder.HandlePcm(netId, buf, samples,
            player.Nickname ?? "?", player.UserId ?? "?", roleCn, (byte)msg.Channel);

        Activities[netId] = new VoiceActivity
        {
            PlayerId = player.PlayerId,
            Nickname = player.Nickname ?? "?",
            UserId = player.UserId ?? "?",
            Role = roleCn,
            Channel = (byte)msg.Channel,
            LastSeen = now
        };

        // 诊断：语音包每 10 秒打一行统计（证明 事件触发→解码→入队 全链路通）
        _pktCount++;
        _totalPackets++;
        if (now - _lastStatsLog > 10f)
        {
            _lastStatsLog = now;
            // 诊断统计：降为 Debug 级（受 config.debug 开关控制），避免说话时每 10 秒刷一条 Info 占控制台
            Log.Debug($"[SLDataAPI] 语音流活跃: 近10秒 {_pktCount} 包, 最近说话者={player.Nickname} channel={(byte)msg.Channel} samples={samples} clients={Clients.Count}");
            _pktCount = 0;
        }

        // 有监听客户端才拷贝 PCM（省内存）
        if (Clients.Count == 0) return;

        // 新一段讲话检测：距上次该说话者的包超过 800ms 视为新一轮，
        // 强制重发 speaker 帧（客户端按 1.5s 超时清理条目，静默后再次说话
        // 若频道/角色未变会被去重逻辑跳过 → 前端有声音但不显示说话者的根因）
        bool newBurst = false;
        if (_lastPacketTime.TryGetValue(netId, out float lastT))
        {
            if (now - lastT > 0.8f) newBurst = true;
        }
        else newBurst = true;
        _lastPacketTime[netId] = now;

        var pkt = new PcmPacket
        {
            Channel = (byte)msg.Channel,
            PlayerId = (ushort)(player.PlayerId & 0xFFFF),
            Nickname = player.Nickname ?? "?",
            UserId = player.UserId ?? "?",
            Role = roleCn,
            NewBurst = newBurst,
            Samples = new float[samples]
        };
        Array.Copy(buf, pkt.Samples, samples);

        OutQueue.Enqueue(pkt);
        while (OutQueue.Count > MaxQueuedPackets) OutQueue.Dequeue();
    }

    /// <summary>清理长时间未说话的播放器解码器/状态（Tick 内调用）。netId 键，无 Hub 访问。</summary>
    private static void Cleanup()
    {
        try
        {
            if (Decoders.Count == 0 && Activities.Count == 0 && _lastPacketTime.Count == 0)
                return;
            float now = UnityEngine.Time.time;
            List<uint>? stale = null;
            foreach (var kv in Activities)
            {
                if (now - kv.Value.LastSeen > 6f)
                    (stale ??= new List<uint>()).Add(kv.Key);
            }
            if (stale != null)
            {
                foreach (uint netId in stale)
                {
                    if (Decoders.TryGetValue(netId, out var dec))
                    {
                        try { dec.Dispose(); } catch { /* 忽略 */ }
                        Decoders.Remove(netId);
                    }
                    Activities.Remove(netId);
                    VoiceRecorder.OnSpeakerGone(netId);
                    _lastPacketTime.Remove(netId);
                    _dupSeen.Remove(netId);
                }
            }
        }
        catch (Exception ex)
        {
            RateLimitedWarn($"[SLDataAPI] Cleanup 异常: {ex.Message}");
        }
    }

    // ────────────── 主循环（MEC 协程，主线程每帧） ──────────────

    private static IEnumerator<float> Tick()
    {
        while (_running)
        {
            // 任何异常都不能逃出协程（MEC 会把抛异常的协程直接杀掉）
            try { TickOnce(); }
            catch (Exception ex)
            {
                RateLimitedWarn($"[SLDataAPI] 语音主循环异常: {ex.Message}");
            }
            yield return Timing.WaitForOneFrame;
        }
    }

    private static void TickOnce()
    {
        // 1. 接受新连接（带上限：超出直接拒绝，防止恶意连接耗尽资源）
        try
        {
            while (_listener != null && _listener.Pending())
            {
                Socket sock = _listener.AcceptSocket();
                if (Clients.Count >= MaxClients)
                {
                    RateLimitedWarn($"[SLDataAPI] 语音监听连接数已满（上限 {MaxClients}），已拒绝新连接");
                    try { sock.Close(); } catch { /* 忽略 */ }
                    continue;
                }
                Clients.Add(new VoiceClient(sock));
                RateLimitedInfo("语音监听客户端已连接", Clients.Count);
            }
        }
        catch (Exception ex) when (_running)
        {
            RateLimitedWarn($"[SLDataAPI] 语音 Accept 异常: {ex.Message}");
        }

        // 2. 驱动每个客户端（握手/读帧/清理；含握手超时判定）
        for (int i = Clients.Count - 1; i >= 0; i--)
        {
            VoiceClient c = Clients[i];
            try
            {
                c.Pump();
                if (!c.IsOpen || c.HandshakeTimedOut)
                {
                    try { c.Dispose(); } catch { /* 忽略 */ }
                    Clients.RemoveAt(i);
                }
            }
            catch (Exception)
            {
                try { c.Dispose(); } catch { /* 忽略 */ }
                Clients.RemoveAt(i);
            }
        }

        // 3. 推送语音包
        int sent = 0;
        while (OutQueue.Count > 0 && sent < 32)
        {
            PcmPacket pkt = OutQueue.Dequeue();
            foreach (var c in Clients)
            {
                if (c.IsOpen && c.Authenticated)
                    c.SendVoice(pkt);
            }
            sent++;
        }

        // 4. 清理超时解码器
        Cleanup();

        // 5. 心跳日志：低频巡检（5 分钟一条），确认服务还活着（累计包数用于区分"没人说话"）
        float now = UnityEngine.Time.time;
        if (now - _lastHeartbeat > 300f)
        {
            _lastHeartbeat = now;
            Log.Info($"[SLDataAPI] 语音心跳: 累计语音包={_totalPackets}, 监听客户端={Clients.Count}, 活跃说话者={Activities.Count}");
        }
    }

    // ────────────── /status JSON ──────────────

    public static string BuildStatusJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"ok\":true,\"speakers\":[");
        bool first = true;
        foreach (var kv in Activities)
        {
            if (UnityEngine.Time.time - kv.Value.LastSeen > 1.5f) continue; // 1.5s 内无新包视为停止
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"playerid\":").Append(kv.Value.PlayerId)
              .Append(",\"nickname\":\"").Append(JsonEscape(kv.Value.Nickname))
              .Append("\",\"userid\":\"").Append(JsonEscape(kv.Value.UserId))
              .Append("\",\"role\":\"").Append(JsonEscape(kv.Value.Role))
              .Append("\",\"channel\":").Append(kv.Value.Channel)
              .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ────────────── 内部类型 ──────────────

    internal class PcmPacket
    {
        public byte Channel;
        public ushort PlayerId;
        public string Nickname = "";
        public string UserId = "";
        public string Role = "";
        public bool NewBurst;
        public float[] Samples = Array.Empty<float>();
    }

    private class VoiceActivity
    {
        public int PlayerId;
        public string Nickname = "";
        public string UserId = "";
        public string Role = "";
        public byte Channel;
        public float LastSeen;
    }

    // ────────────── 诊断日志限流 ──────────────

    private static void RateLimitedWarn(string message)
    {
        float now = UnityEngine.Time.time;
        if (now - _lastWarnLog > 10f)
        {
            _lastWarnLog = now;
            Log.Warn(message);
        }
    }

    private static void RateLimitedInfo(string what, int count)
    {
        float now = UnityEngine.Time.time;
        if (now - _lastConnLog > 10f)
        {
            _lastConnLog = now;
            Log.Info($"[SLDataAPI] {what} (共 {count})");
        }
    }

    // ────────────── 单个客户端连接（HTTP 握手 + WS 帧） ──────────────

    private class VoiceClient : IDisposable
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly byte[] _inBuf = new byte[4096];
        private int _inLen;
        private int _state; // 0=HTTP 待握手 1=已握手
        private int _frameSeq;

        public bool IsOpen => _socket.Connected && _state >= 0;
        public bool Authenticated => _state == 1;

        /// <summary>连接时间（主线程时钟）：超过 HandshakeTimeoutSec 仍未完成握手则超时。</summary>
        private readonly float _connectedAt = UnityEngine.Time.time;

        /// <summary>握手超时判定——连上后不发/慢发 HTTP 头的连接（Slowloris 式占用）强制断开。</summary>
        public bool HandshakeTimedOut =>
            _state == 0 && UnityEngine.Time.time - _connectedAt > HandshakeTimeoutSec;

        public VoiceClient(Socket socket)
        {
            _socket = socket;
            _socket.NoDelay = true;
            _socket.ReceiveTimeout = 1;
            // ★ 卡服防护：发送超时兜底。发送缓冲满时 Socket.Send 会阻塞到超时；
            // 这里是主线程协程，绝不允许多帧/无限期阻塞（配合 PrepareSend 的 Poll 守卫，
            // 正常情况下永远走不到这个超时）。
            _socket.SendTimeout = 250;
            _stream = new NetworkStream(socket, false);
        }

        public void Pump()
        {
            if (_state < 0) return;
            try
            {
                while (_socket.Available > 0 && _inLen < _inBuf.Length)
                {
                    int n = _socket.Receive(_inBuf, _inLen, _inBuf.Length - _inLen, SocketFlags.None);
                    if (n <= 0) { _state = -1; return; }
                    _inLen += n;
                }

                if (_state == 0)
                {
                    int headerEnd = FindHeaderEnd();
                    if (headerEnd >= 0)
                    {
                        string header = Encoding.ASCII.GetString(_inBuf, 0, headerEnd);
                        _inLen = 0;
                        HandleHttp(header);
                    }
                }
                else if (_state == 1)
                {
                    ProcessWsFrames();
                }
            }
            catch (SocketException) { _state = -1; }
            catch (IOException) { _state = -1; }
        }

        private int FindHeaderEnd()
        {
            for (int i = 0; i + 3 < _inLen; i++)
            {
                if (_inBuf[i] == '\r' && _inBuf[i + 1] == '\n' && _inBuf[i + 2] == '\r' && _inBuf[i + 3] == '\n')
                    return i + 4;
            }
            return -1;
        }

        private void HandleHttp(string header)
        {
            string firstLine = header.Split('\n')[0];
            string[] parts = firstLine.Split(' ');
            if (parts.Length < 2) { _state = -1; return; }
            string method = parts[0];
            string path = parts[1];

            // 鉴权：优先 X-Control-Token 请求头（L-05：不落反向代理/访问日志），
            // 其次 query ?key=。与控制接口同一套防爆破锁定（常量时间比较 + 按 IP 锁定），
            // 未配置 token 时一律拒绝（不裸奔监听）。
            string? key = ExtractHeader(header, "X-Control-Token");
            if (string.IsNullOrEmpty(key))
                key = ExtractQuery(path, "key") ?? ExtractQuery(path, "access_key");
            string cfgToken = Plugin.Instance?.Config.ControlToken ?? "";
            if (string.IsNullOrEmpty(cfgToken))
            {
                SendHttp("403 Forbidden", "application/json", "{\"ok\":false,\"error\":\"服务端未配置 ControlToken\"}");
                _state = -1;
                return;
            }

            string clientIp = "";
            try { clientIp = ((IPEndPoint)_socket.RemoteEndPoint).Address.ToString(); } catch { /* 忽略 */ }

            if (!ControlAuth.TryAuthenticate(clientIp, key ?? "", cfgToken, out string authErr))
            {
                Log.Warn($"[SLDataAPI][Voice] 鉴权失败 from {clientIp}: {authErr} {ControlAuth.DescribeMismatch(key, cfgToken)}");
                SendHttp("403 Forbidden", "application/json", "{\"ok\":false,\"error\":\"" + JsonEscape(authErr) + "\"}");
                _state = -1;
                return;
            }

            if (method == "GET" && path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase))
            {
                if (TryHandshake(header))
                {
                    _state = 1;
                    SendWsFrame(0x1, Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"sampleRate\":48000,\"channels\":1,\"format\":\"float32\"}"));
                }
                else _state = -1;
                return;
            }

            if (method == "GET" && path.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
            {
                SendHttp("200 OK", "application/json", BuildStatusJson());
                _state = -1;
                return;
            }

            SendHttp("404 Not Found", "application/json", "{\"ok\":false,\"error\":\"not found\"}");
            _state = -1;
        }

        private bool TryHandshake(string header)
        {
            string? wsKey = null;
            foreach (var line in header.Split('\n'))
            {
                int idx = line.IndexOf(':');
                if (idx <= 0) continue;
                string name = line.Substring(0, idx).Trim();
                if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                    wsKey = line.Substring(idx + 1).Trim();
            }
            if (wsKey == null) return false;

            string accept = Convert.ToBase64String(
                SHA1.Create().ComputeHash(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n" +
                "\r\n";
            _socket.Send(Encoding.ASCII.GetBytes(response));
            return true;
        }

        private void ProcessWsFrames()
        {
            while (_inLen >= 2)
            {
                int b0 = _inBuf[0];
                int b1 = _inBuf[1];
                int opcode = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                long len = b1 & 0x7F;
                int off = 2;
                if (len == 126)
                {
                    if (_inLen < 4) return;
                    len = (_inBuf[2] << 8) | _inBuf[3];
                    off = 4;
                }
                else if (len == 127)
                {
                    if (_inLen < 10) return;
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | _inBuf[2 + i];
                    off = 10;
                }

                // ★ 帧长度上限（L-02）：声明 2^32 长度时 (int)len 会回绕成 0 绕过缓冲检查，
                //   再 new byte[len] 抛 OverflowException 逃逸 Pump 的 catch——超限直接断连
                if (len > MaxMessageBytes)
                {
                    _state = -1;
                    return;
                }

                byte[]? mask = null;
                if (masked)
                {
                    if (_inLen < off + 4) return;
                    mask = new byte[4];
                    Array.Copy(_inBuf, off, mask, 0, 4);
                    off += 4;
                }
                // ★ N-01：区分「尚未收全」与「永远装不下」——_inBuf 固定 4096 字节，
                //   声明长度超过缓冲的帧即使等再多轮也收不全（Pump 无法再读），
                //   会永久卡死连接槽。合法入站帧只有 ping/close（都很小），直接断连。
                int total = off + (int)len;
                if (total > _inBuf.Length)
                {
                    _state = -1;
                    return;
                }
                if (_inLen < total) return; // 尚未收全：等下一轮 Pump

                byte[] payload = new byte[len];
                Array.Copy(_inBuf, off, payload, 0, (int)len);
                if (mask != null)
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                int consumed = off + (int)len;
                Array.Copy(_inBuf, consumed, _inBuf, 0, _inLen - consumed);
                _inLen -= consumed;

                if (opcode == 0x8) { _state = -1; return; }       // close
                if (opcode == 0x9) { SendWsFrame(0xA, payload); } // ping → pong
            }
        }

        /// <summary>推送语音：说话者信息帧只在频道/角色变化或新一轮讲话时发（减少文本帧抢占带宽），
        /// 语音帧每包都发（含 channel/playerId，前端据此追踪频道切换）。</summary>
        public void SendVoice(PcmPacket pkt)
        {
            // 说话者信息帧去重：同一玩家同一频道不重复发（此前每 20ms 包都发一个
            // JSON 帧，大量文本帧与语音帧竞争，是"听起来卡顿"的主因之一）；
            // NewBurst 时强制重发（前端按 1.5s 超时清理条目，静默后再次说话若被去重
            // 跳过 → 前端有声音但不显示说话者）
            string key = $"{pkt.Channel}|{pkt.Role}|{pkt.Nickname}";
            string? lastKey;
            if (pkt.NewBurst || !_speakerKeys.TryGetValue(pkt.PlayerId, out lastKey) || lastKey != key)
            {
                string json = $"{{\"type\":\"speaker\",\"nickname\":\"{JsonEscape(pkt.Nickname)}\",\"userid\":\"{JsonEscape(pkt.UserId)}\",\"playerid\":{pkt.PlayerId},\"role\":\"{JsonEscape(pkt.Role)}\",\"channel\":{pkt.Channel}}}";
                SendWsFrame(0x1, Encoding.UTF8.GetBytes(json));
                _speakerKeys[pkt.PlayerId] = key;
            }

            int count = pkt.Samples.Length;
            byte[] frame = new byte[8 + count * 4];
            frame[0] = 0x01; // 语音帧
            frame[1] = pkt.Channel;
            frame[2] = (byte)(pkt.PlayerId & 0xFF);
            frame[3] = (byte)(pkt.PlayerId >> 8);
            int seq = ++_frameSeq;
            frame[4] = (byte)(seq & 0xFF);
            frame[5] = (byte)((seq >> 8) & 0xFF);
            frame[6] = (byte)((seq >> 16) & 0xFF);
            frame[7] = (byte)((seq >> 24) & 0xFF);
            Buffer.BlockCopy(pkt.Samples, 0, frame, 8, count * 4);
            SendWsFrame(0x2, frame);
        }

        /// <summary>每客户端维护的 speaker 帧去重键（playerId → channel|role|nick）。</summary>
        private readonly Dictionary<ushort, string> _speakerKeys = new();

        /// <summary>持续不可写的起始时间（主线程时钟；-1 = 当前可写）。</summary>
        private float _unwritableSince = -1f;

        /// <summary>
        /// 发送前守卫：主线程绝不阻塞在 Send 上。TCP 发送缓冲满（对端停止收数据）时
        /// 本帧直接跳过（实时语音流允许丢帧）；持续不可写超过 UnwritableDropSec 判死连接。
        /// </summary>
        private bool PrepareSend()
        {
            try
            {
                if (_socket.Poll(0, SelectMode.SelectWrite))
                {
                    _unwritableSince = -1f;
                    return true;
                }
            }
            catch
            {
                _state = -1;
                return false;
            }

            if (_unwritableSince < 0f)
                _unwritableSince = UnityEngine.Time.time;
            else if (UnityEngine.Time.time - _unwritableSince > UnwritableDropSec)
                _state = -1; // 判死：对端长时间不收数据，下个循环回收
            return false;
        }

        private void SendWsFrame(int opcode, byte[] payload)
        {
            try
            {
                if (!PrepareSend()) return;
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
                _socket.Send(full);
            }
            catch { _state = -1; }
        }

        private void SendHttp(string status, string contentType, string body)
        {
            if (!PrepareSend()) return;
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string head =
                $"HTTP/1.1 {status}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n" +
                "\r\n";
            try
            {
                _socket.Send(Encoding.ASCII.GetBytes(head));
                _socket.Send(bodyBytes);
            }
            catch { /* 忽略 */ }
        }

        public void Dispose()
        {
            _state = -1;
            try { _socket.Close(); } catch { /* 忽略 */ }
            try { _stream.Dispose(); } catch { /* 忽略 */ }
        }
    }

    // ────────────── 工具 ──────────────

    /// <summary>FNV-1a 64 位哈希（判重用，非密码学用途）。</summary>
    private static ulong FnvHash(byte[] data, int len)
    {
        ulong h = 14695981039346656037UL;
        int n = len < data.Length ? len : data.Length;
        for (int i = 0; i < n; i++)
        {
            h ^= data[i];
            h *= 1099511628211UL;
        }
        return h;
    }

    /// <summary>从握手 HTTP 头中提取指定请求头（L-05：语音 WS 支持 X-Control-Token 头鉴权）。</summary>
    private static string? ExtractHeader(string header, string name)
    {
        foreach (var line in header.Split('\n'))
        {
            int idx = line.IndexOf(':');
            if (idx <= 0) continue;
            if (line.Substring(0, idx).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line.Substring(idx + 1).Trim();
        }
        return null;
    }

    private static string? ExtractQuery(string path, string name)
    {
        int q = path.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in path.Substring(q + 1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string k = Uri.UnescapeDataString(pair.Substring(0, eq));
            if (k.Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair.Substring(eq + 1));
        }
        return null;
    }

    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 16);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(' '); break;   // 换行 → 空格（保持日志行可读）
                case '\r': break;                    // 回车删除
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); // 其余控制字符转 \uXXXX（合法 JSON）
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string GetRoleCN(RoleTypeId type)
    {
        switch (type)
        {
            case RoleTypeId.ClassD: return "D级人员";
            case RoleTypeId.Scientist: return "科学家";
            case RoleTypeId.FacilityGuard: return "设施警卫";
            case RoleTypeId.NtfSpecialist: return "九尾狐-中士";
            case RoleTypeId.NtfSergeant: return "九尾狐-军士";
            case RoleTypeId.NtfCaptain: return "九尾狐-队长";
            case RoleTypeId.NtfPrivate: return "九尾狐-列兵";
            case RoleTypeId.ChaosConscript: return "混沌分裂者-征召兵";
            case RoleTypeId.ChaosRifleman: return "混沌分裂者-步枪手";
            case RoleTypeId.ChaosMarauder: return "混沌分裂者-掠夺者";
            case RoleTypeId.ChaosRepressor: return "混沌分裂者-镇压者";
            case RoleTypeId.Scp049: return "SCP-049";
            case RoleTypeId.Scp0492: return "SCP-049-2";
            case RoleTypeId.Scp079: return "SCP-079";
            case RoleTypeId.Scp096: return "SCP-096";
            case RoleTypeId.Scp106: return "SCP-106";
            case RoleTypeId.Scp173: return "SCP-173";
            case RoleTypeId.Scp939: return "SCP-939";
            case RoleTypeId.Scp3114: return "SCP-3114";
            case RoleTypeId.Tutorial: return "教程角色";
            case RoleTypeId.Spectator: return "旁观者";
            case RoleTypeId.Overwatch: return "观察者";
            case RoleTypeId.Filmmaker: return "摄影者";
            default: return type.ToString();
        }
    }
}
