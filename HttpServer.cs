using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

public class HttpServer
{
    private readonly HttpListener _listener;
    private readonly string _token;

    public HttpServer(int port, string token)
    {
        _token = token;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://*:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        Listen();
    }

    // ★ 新增：插件禁用时正常关闭，释放端口
    public void Stop()
    {
        try { _listener.Stop(); } catch { }
    }

    private async void Listen()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => Handle(ctx));
            }
            catch
            {
                // listener stopped — exit loop
                break;
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;

            if (req.Url?.AbsolutePath != "/get_sl_data")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var reqToken = req.QueryString["token"];
            if (reqToken != _token)
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                return;
            }

            // ★ 修复2：通过 BuildJson() 在响应时实时注入核弹倒计时，而非用 8 秒前的缓存值
            string json   = DataCollector.BuildJson();
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.Close();
        }
        catch { /* ignore individual request errors */ }
    }
}
