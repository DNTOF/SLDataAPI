using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using SLDataAPI;
using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.WebDav.Tests;

public class WebDavUploaderTests
{
    private const string SecretPassword = "SUPER_SECRET_WEBDAV_PASSWORD_DO_NOT_ECHO";
    private const string SecretUser = "webdav-secret-user";

    [Fact]
    public void Config_WebDavUpload_DefaultsOff()
    {
        var cfg = new Config();
        Assert.False(cfg.WebdavUploadEnabled);
        Assert.Equal("", cfg.WebdavUrl);
        Assert.Equal("", cfg.WebdavUsername);
        Assert.Equal("", cfg.WebdavPassword);
        Assert.Equal(30, cfg.WebdavTimeoutSeconds);
        Assert.Equal(5, cfg.WebdavMaxRetries);
        Assert.Equal(15, cfg.WebdavRetryIntervalSeconds);
    }

    [Fact]
    public void TryValidate_EmptyUrl_Fails()
    {
        Assert.False(WebDavUploader.TryValidate(new WebDavUploadOptions { Url = "" }, out string error));
        Assert.Contains("webdav_url", error);
        Assert.DoesNotContain(SecretPassword, error);
    }

    [Fact]
    public void TryValidate_NonHttp_Fails()
    {
        Assert.False(WebDavUploader.TryValidate(new WebDavUploadOptions { Url = "ftp://example.com/dav" }, out string error));
        Assert.Contains("https", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_PlainHttp_Fails()
    {
        Assert.False(WebDavUploader.TryValidate(new WebDavUploadOptions
        {
            Url = "http://dav.example.com/rec/",
            TimeoutSeconds = 10,
        }, out string error));
        Assert.Contains("https", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretPassword, error);
    }

    [Fact]
    public void TryValidate_TemplateHttps_Ok()
    {
        Assert.True(WebDavUploader.TryValidate(new WebDavUploadOptions
        {
            Url = "https://dav.example.com/rec/{filename}",
            TimeoutSeconds = 10,
            MaxRetries = 1,
            RetryIntervalSeconds = 0,
        }, out string error));
        Assert.Equal("", error);
    }

    [Fact]
    public void TryValidate_NegativeRetries_Fails()
    {
        Assert.False(WebDavUploader.TryValidate(new WebDavUploadOptions
        {
            Url = "https://dav.example.com/",
            TimeoutSeconds = 10,
            MaxRetries = -1,
        }, out _));
    }

    [Theory]
    [InlineData("https://dav.example.com/rec/", "", "voice_round_1.zip", "https://dav.example.com/rec/voice_round_1.zip")]
    [InlineData("https://dav.example.com/rec", "archive", "voice_round_1.zip", "https://dav.example.com/rec/archive/voice_round_1.zip")]
    [InlineData("https://dav.example.com/rec/{filename}", "ignored", "voice_round_1.zip", "https://dav.example.com/rec/voice_round_1.zip")]
    [InlineData("https://dav.example.com/rec/{FILE}", "", "a b.zip", "https://dav.example.com/rec/a%20b.zip")]
    public void BuildRemoteUrl_JoinsBasePrefixOrTemplate(string url, string prefix, string file, string expected)
    {
        Assert.Equal(expected, WebDavUploader.BuildRemoteUrl(url, prefix, file));
    }

    [Fact]
    public void BuildRemoteUrl_DropsDotDotPrefixSegments()
    {
        string got = WebDavUploader.BuildRemoteUrl("https://dav.example.com/rec/", "../etc", "voice.zip");
        Assert.Equal("https://dav.example.com/rec/etc/voice.zip", got);
        Assert.DoesNotContain("..", got);
    }

    [Fact]
    public void DescribeTarget_StripsUserInfoAndQuery()
    {
        string desc = WebDavUploader.DescribeTarget("https://alice:secret@dav.example.com:8443/rec/a.zip?token=abc");
        Assert.Equal("https://dav.example.com:8443/rec/a.zip", desc);
        Assert.DoesNotContain("secret", desc);
        Assert.DoesNotContain("alice", desc);
        Assert.DoesNotContain("token", desc);
    }

    [Fact]
    public void Enqueue_Disabled_DoesNotSendHttp()
    {
        var handler = new RecordingHandler();
        using var uploader = Create(handler, enabled: false);
        using var zip = TempZip();
        uploader.Enqueue(zip.Path);
        Assert.True(uploader.WaitUntilIdle(TimeSpan.FromSeconds(2)));
        Thread.Sleep(80);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Enqueue_Success_PutsZipWithBasicAuth()
    {
        var handler = new RecordingHandler
        {
            OnSend = (_, __) => new HttpResponseMessage(HttpStatusCode.Created),
        };
        var logs = new ConcurrentQueue<string>();
        using var uploader = Create(handler, enabled: true, logs: logs);
        using var zip = TempZip();
        uploader.Enqueue(zip.Path);
        Assert.True(uploader.WaitUntilIdle(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, handler.CallCount);
        Assert.True(handler.Requests.TryPeek(out var rec));
        Assert.Equal(HttpMethod.Put, rec.Method);
        Assert.EndsWith("/voice/" + Path.GetFileName(zip.Path), rec.Uri);
        Assert.True(rec.HasAuthorization);
        Assert.Equal("Basic", rec.AuthScheme);
        Assert.Equal("application/zip", rec.ContentType);
        Assert.Equal("T", rec.Overwrite);
        Assert.All(logs, line =>
        {
            Assert.DoesNotContain(SecretPassword, line);
            Assert.DoesNotContain("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(SecretUser + ":" + SecretPassword)), line);
        });
    }

    [Fact]
    public void Enqueue_5xxThenOk_RetriesOnce()
    {
        var handler = new RecordingHandler
        {
            OnSend = (_, n) => n == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.Created),
        };
        using var uploader = Create(handler, enabled: true, maxRetries: 3, retryIntervalSeconds: 0);
        using var zip = TempZip();
        uploader.Enqueue(zip.Path);
        Assert.True(uploader.WaitUntilIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public void Enqueue_NetworkErrorThenOk_Retries()
    {
        var handler = new RecordingHandler
        {
            OnSend = (_, n) =>
            {
                if (n == 1)
                    throw new HttpRequestException("connection reset by peer");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            },
        };
        using var uploader = Create(handler, enabled: true, maxRetries: 2, retryIntervalSeconds: 0);
        using var zip = TempZip();
        uploader.Enqueue(zip.Path);
        Assert.True(uploader.WaitUntilIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public void Enqueue_Unauthorized_DoesNotRetry()
    {
        var handler = new RecordingHandler
        {
            OnSend = (_, __) => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        };
        using var uploader = Create(handler, enabled: true, maxRetries: 5, retryIntervalSeconds: 0);
        using var zip = TempZip();
        uploader.Enqueue(zip.Path);
        Assert.True(uploader.WaitUntilIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void Enqueue_MaxRetriesExceeded_Stops()
    {
        var handler = new RecordingHandler
        {
            OnSend = (_, __) => new HttpResponseMessage(HttpStatusCode.BadGateway),
        };
        using var uploader = Create(handler, enabled: true, maxRetries: 2, retryIntervalSeconds: 0);
        using var zip = TempZip();
        uploader.Enqueue(zip.Path);
        Assert.True(uploader.WaitUntilIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, handler.CallCount); // 1 首次 + 2 次重试
    }

    [Fact]
    public void Enqueue_MissingFile_NoHttp()
    {
        var handler = new RecordingHandler();
        using var uploader = Create(handler, enabled: true);
        uploader.Enqueue(Path.Combine(Path.GetTempPath(), "no-such-voice-round-" + Guid.NewGuid().ToString("N") + ".zip"));
        Assert.True(uploader.WaitUntilIdle(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void Logs_NeverContainPasswordOrBasicHeader()
    {
        var logs = new ConcurrentQueue<string>();
        var handler = new RecordingHandler
        {
            OnSend = (_, __) => throw new HttpRequestException(
                $"auth failed for {SecretUser} pass={SecretPassword} Authorization: Basic FAKESECRET_k3l4m5n6o7p8q9r0s1t2"),
        };
        using var uploader = Create(handler, enabled: true, maxRetries: 0, retryIntervalSeconds: 0, logs: logs);
        using var zip = TempZip();
        uploader.Enqueue(zip.Path);
        Assert.True(uploader.WaitUntilIdle(TimeSpan.FromSeconds(5)));

        Assert.NotEmpty(logs);
        foreach (string line in logs)
        {
            Assert.DoesNotContain(SecretPassword, line);
            Assert.DoesNotContain(SecretUser, line);
            Assert.DoesNotContain("c2VjcmV0", line);
            Assert.DoesNotContain("Authorization: Basic ", line + " ");
        }
    }

    [Fact]
    public void IsRetryableStatus_5xxAnd429_Not4xx()
    {
        Assert.True(WebDavUploader.IsRetryableStatus(HttpStatusCode.InternalServerError));
        Assert.True(WebDavUploader.IsRetryableStatus(HttpStatusCode.BadGateway));
        Assert.True(WebDavUploader.IsRetryableStatus((HttpStatusCode)429));
        Assert.True(WebDavUploader.IsRetryableStatus(HttpStatusCode.RequestTimeout));
        Assert.False(WebDavUploader.IsRetryableStatus(HttpStatusCode.Unauthorized));
        Assert.False(WebDavUploader.IsRetryableStatus(HttpStatusCode.Forbidden));
        Assert.False(WebDavUploader.IsRetryableStatus(HttpStatusCode.NotFound));
        Assert.False(WebDavUploader.IsRetryableStatus(HttpStatusCode.Conflict));
    }

    [Fact]
    public void Safe_RedactsPasswordAndBasicBlob()
    {
        using var uploader = Create(new RecordingHandler(), enabled: true);
        string leaked = uploader.Safe($"user={SecretUser} pw={SecretPassword} Authorization: Basic abcdef123==");
        Assert.DoesNotContain(SecretPassword, leaked);
        Assert.DoesNotContain(SecretUser, leaked);
        Assert.DoesNotContain("abcdef123", leaked);
        Assert.Contains("***", leaked);
    }

    private static WebDavUploader Create(
        HttpMessageHandler handler,
        bool enabled,
        int maxRetries = 1,
        int retryIntervalSeconds = 0,
        ConcurrentQueue<string>? logs = null)
    {
        var opt = new WebDavUploadOptions
        {
            Enabled = enabled,
            Url = "https://dav.example.com/voice/",
            Username = SecretUser,
            Password = SecretPassword,
            TimeoutSeconds = 10,
            MaxRetries = maxRetries,
            RetryIntervalSeconds = retryIntervalSeconds,
        };
        return new WebDavUploader(opt, handler, (level, msg) => logs?.Enqueue(level + ":" + msg));
    }

    private static TempFile TempZip()
    {
        string path = Path.Combine(Path.GetTempPath(), "voice_round_test_" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        return new TempFile(path);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile(string path) => Path = path;
        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { /* 测试清理 */ }
        }
    }
}
