using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class HttpServer
{
    private readonly int _port;
    private readonly string _token;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    // ★ 修复：串行化 JSON 构建，避免并发请求同时写 CachedData 造成字段错乱
    private static readonly object _jsonLock = new object();

    public HttpServer(int port, string token)
    {
        _port = port;
        _token = token ?? "";
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // ★ 修复1：用 TcpListener 绑定 0.0.0.0 替代 HttpListener
        //   - HttpListener 的 http://*:{port}/ 在 Windows 依赖 http.sys，
        //     需要 netsh urlacl 预留，否则 Start() 抛 "拒绝访问(5)"，
        //     导致公网根本连不上。TcpListener 直接监听所有网卡，无需任何预留。
        //   - 跨平台行为一致（Windows / Linux 服务器皆可）。
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

    // ★ 修复2：accept 循环对单连接异常免疫
    //   旧实现中 GetContextAsync 抛任何异常都会 break 退出循环，
    //   导致监听器永久停摆（表现为"套接字报错，重启服务端就好"）。
    //   现在仅在监听器真正停止时退出，其余异常一律继续 accept。
    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync();
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                // 监听器仍在运行时的偶发 accept 异常，忽略后继续
                continue;
            }

            // 每个连接独立处理；连接内的任何异常都隔离在此任务内，
            // 绝不会传播回 accept 循环。
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
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true))
                {
                    // 请求行，形如: GET /get_sl_data?token=xxx HTTP/1.1
                    string? requestLine = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        SendResponse(stream, 400, "Bad Request", "");
                        return;
                    }

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2)
                    {
                        SendResponse(stream, 400, "Bad Request", "");
                        return;
                    }

                    string method = parts[0];
                    string rawTarget = parts[1];

                    // 读掉请求头直到空行（本接口仅处理 GET，无请求体）
                    while (true)
                    {
                        var header = reader.ReadLine();
                        if (header == null || header.Length == 0) break;
                    }

                    if (method != "GET")
                    {
                        SendResponse(stream, 405, "Method Not Allowed", "");
                        return;
                    }

                    // 拆分路径与查询串
                    string path = rawTarget;
                    string query = "";
                    int qIdx = rawTarget.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        path = rawTarget.Substring(0, qIdx);
                        query = rawTarget.Substring(qIdx + 1);
                    }

                    if (path != "/get_sl_data")
                    {
                        SendResponse(stream, 404, "Not Found", "");
                        return;
                    }

                    string reqToken = ExtractQueryValue(query, "token");
                    if (reqToken != _token)
                    {
                        SendResponse(stream, 403, "Forbidden", "");
                        return;
                    }

                    // ★ 实时构建 JSON：核弹倒计时与游戏内同步，
                    //   字段格式与原实现完全一致（由 DataCollector.BuildJson 决定）。
                    string json;
                    lock (_jsonLock)
                    {
                        json = DataCollector.BuildJson();
                    }
                    SendResponse(stream, 200, "OK", json);
                }
            }
            catch
            {
                // 单个连接出错不影响其他连接，也不影响 accept 循环
            }
        }
    }

    private static void SendResponse(Stream stream, int code, string reason, string body)
    {
        byte[] bodyBytes = string.IsNullOrEmpty(body)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(body);

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
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
