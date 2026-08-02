using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Exiled.API.Features;

public class HttpServer
{
    private readonly int _port;
    private readonly Config _config;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    // ★ 串行化 JSON 构建，避免并发请求同时写 CachedData 造成字段错乱
    private static readonly object _jsonLock = new object();

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
        using (client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;

                string remoteIp = "unknown";
                try { remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint)?.Address.ToString() ?? "unknown"; }
                catch { }

                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true))
                {
                    string requestLine = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        SendJson(stream, 400, Err("bad request"));
                        return;
                    }

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2)
                    {
                        SendJson(stream, 400, Err("bad request"));
                        return;
                    }

                    string method = parts[0];
                    string rawTarget = parts[1];

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string headerLine;
                    while (!string.IsNullOrEmpty(headerLine = reader.ReadLine()))
                    {
                        int idx = headerLine.IndexOf(':');
                        if (idx <= 0) continue;
                        headers[headerLine.Substring(0, idx).Trim()] = headerLine.Substring(idx + 1).Trim();
                    }

                    string path = rawTarget;
                    string query = "";
                    int qIdx = rawTarget.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        path = rawTarget.Substring(0, qIdx);
                        query = rawTarget.Substring(qIdx + 1);
                    }

                    string body = "";
                    if (headers.TryGetValue("Content-Length", out var clStr) &&
                        int.TryParse(clStr, out int contentLength) && contentLength > 0)
                    {
                        // 限制请求体大小（64KB 足够容纳所有控制类请求），防止恶意超大 body 占内存
                        if (contentLength > 65536)
                        {
                            SendJson(stream, 413, Err("payload too large"));
                            return;
                        }

                        var buf = new char[contentLength];
                        int readTotal = 0;
                        while (readTotal < contentLength)
                        {
                            int n = reader.Read(buf, readTotal, contentLength - readTotal);
                            if (n <= 0) break;
                            readTotal += n;
                        }
                        body = new string(buf, 0, readTotal);
                    }

                    Route(stream, remoteIp, method, path, query, headers, body);
                }
            }
            catch
            {
                // 单个连接出错不影响其他连接，也不影响 accept 循环
            }
        }
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
                if (!ControlAuth.SecureEquals(reqToken, _config.VerifyToken ?? ""))
                {
                    SendJson(stream, 403, Err("token 错误或缺失"));
                    return;
                }

                string json;
                lock (_jsonLock) { json = DataCollector.BuildJson(); }
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

                string reqToken = headers.TryGetValue("X-Control-Token", out var h)
                    ? h
                    : ExtractQueryValue(query, "token");

                if (!ControlAuth.TryAuthenticate(remoteIp, reqToken, _config.ControlToken, out string authErr))
                {
                    Log.Warn($"[SLDataAPI][Control] 鉴权失败 from {remoteIp}: {authErr}");
                    SendJson(stream, 403, Err(authErr));
                    return;
                }

                if (method != "POST")
                {
                    SendJson(stream, 405, Err("控制接口仅支持 POST"));
                    return;
                }

                var (status, json) = ControlController.Handle(path, body);
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
            case 403: return "Forbidden";
            case 404: return "Not Found";
            case 405: return "Method Not Allowed";
            case 413: return "Payload Too Large";
            case 500: return "Internal Server Error";
            case 501: return "Not Implemented";
            default: return "Unknown";
        }
    }

    private static string Err(string message) =>
        Newtonsoft.Json.JsonConvert.SerializeObject(new ControlResponse { success = false, message = message });

    private static string ExtractQueryValue(string query, string key)
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
