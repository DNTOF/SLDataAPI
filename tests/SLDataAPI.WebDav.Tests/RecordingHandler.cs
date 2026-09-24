using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace SLDataAPI.WebDav.Tests;

/// <summary>记录 PUT 请求元数据，不保存 Authorization 参数或请求体明文密码。</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public readonly ConcurrentQueue<RecordedRequest> Requests = new();
    public Func<HttpRequestMessage, int, HttpResponseMessage> OnSend =
        (_, __) => new HttpResponseMessage(HttpStatusCode.Created);

    public int CallCount => Requests.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int n = Requests.Count + 1;
        Requests.Enqueue(new RecordedRequest
        {
            Method = request.Method,
            Uri = request.RequestUri?.ToString() ?? "",
            HasAuthorization = request.Headers.Authorization != null,
            AuthScheme = request.Headers.Authorization?.Scheme,
            ContentType = request.Content?.Headers.ContentType?.MediaType,
            Overwrite = request.Headers.TryGetValues("Overwrite", out var v) ? string.Join(",", v) : "",
        });
        return Task.FromResult(OnSend(request, n));
    }
}

internal sealed class RecordedRequest
{
    public HttpMethod Method { get; set; } = HttpMethod.Get;
    public string Uri { get; set; } = "";
    public bool HasAuthorization { get; set; }
    public string? AuthScheme { get; set; }
    public string? ContentType { get; set; }
    public string Overwrite { get; set; } = "";
}
