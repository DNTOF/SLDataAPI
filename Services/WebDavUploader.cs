using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SLDataAPI.Services;

/// <summary>
/// WebDAV 自动上传选项（与 Config 字段对应，无 LabAPI 依赖，可供单元测试直接链接）。
/// </summary>
public sealed class WebDavUploadOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string RemotePathPrefix { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 5;
    public int RetryIntervalSeconds { get; set; } = 15;
}

/// <summary>
/// 定稿 zip 的 WebDAV PUT 上传器：后台单线程消费队列，失败按退避重试。
/// 不依赖 Unity / LabAPI；日志经回调输出，调用方负责保证回调不含密码。
/// </summary>
public sealed class WebDavUploader : IDisposable
{
    public const int DefaultTimeoutSeconds = 30;
    public const int DefaultMaxRetries = 5;
    public const int DefaultRetryIntervalSeconds = 15;
    public const int MaxQueuedJobs = 16;
    public const int MaxBackoffSeconds = 600;

    private static readonly Regex BasicAuthInText = new Regex(
        @"(?i)(?:authorization\s*:\s*)?basic\s+[A-Za-z0-9+/=]+",
        RegexOptions.Compiled);

    private readonly WebDavUploadOptions _opt;
    private readonly HttpClient _http;
    private readonly Action<string, string> _log;
    private readonly object _gate = new();
    private readonly List<UploadJob> _jobs = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _worker;
    private int _busy;
    private bool _disposed;

    private sealed class UploadJob
    {
        public string LocalPath = "";
        public int Attempts;
        public DateTime NextUtc;
    }

    /// <param name="log">level（debug/info/warn/error）+ 已脱敏消息。</param>
    public WebDavUploader(WebDavUploadOptions options, HttpMessageHandler? handler = null, Action<string, string>? log = null)
    {
        _opt = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? ((_, __) => { });

        int timeoutSec = _opt.TimeoutSeconds > 0 ? _opt.TimeoutSeconds : DefaultTimeoutSeconds;
        bool ownsHandler = handler == null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: ownsHandler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSec),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SLDataAPI-WebDAV");

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SLDataAPI-WebDAV",
        };
        _worker.Start();
    }

    /// <summary>启用时校验 URL / 超时 / 重试参数；失败时 error 不含密码。</summary>
    public static bool TryValidate(WebDavUploadOptions? options, out string error)
    {
        error = "";
        if (options == null)
        {
            error = "配置为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            error = "webdav_url 为空";
            return false;
        }

        string probe = ReplacePlaceholder(options.Url, "probe.zip");
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            error = uri != null && uri.Scheme == Uri.UriSchemeHttp
                ? "webdav_url 禁止 http://（密码在 config.yml，明文 WebDAV 会泄露 Basic Auth）。请改用 https://"
                : "webdav_url 必须是 https:// 绝对地址（或含 {filename} 的模板）";
            return false;
        }

        if (options.TimeoutSeconds <= 0)
        {
            error = "webdav_timeout_seconds 必须 > 0";
            return false;
        }

        if (options.MaxRetries < 0)
        {
            error = "webdav_max_retries 不能为负";
            return false;
        }

        if (options.RetryIntervalSeconds < 0)
        {
            error = "webdav_retry_interval_seconds 不能为负";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 拼远程 PUT 地址：<c>{filename}</c>/<c>{file}</c> 替换为文件名；否则按目录 URL + 可选前缀 + 文件名拼接。
    /// </summary>
    public static string BuildRemoteUrl(string url, string? remotePathPrefix, string fileName)
    {
        fileName = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentException("文件名为空", nameof(fileName));

        string escaped = Uri.EscapeDataString(fileName);
        if (ContainsPlaceholder(url))
            return ReplacePlaceholder(url, escaped);

        string baseUrl = (url ?? "").Trim().TrimEnd('/');
        string prefix = NormalizePrefix(remotePathPrefix);
        if (prefix.Length > 0)
            baseUrl += "/" + prefix;
        return baseUrl + "/" + escaped;
    }

    /// <summary>仅 scheme+host+path，去掉 userinfo / query（日志用，不含密码）。</summary>
    public static string DescribeTarget(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "(invalid-url)";
        string port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        return uri.Scheme + "://" + uri.Host + port + uri.AbsolutePath;
    }

    /// <summary>入队后立即返回。未启用 / 已释放 / 路径空 → 空操作。队列满则丢弃新任务并记一条 warn。</summary>
    public void Enqueue(string? localZipPath)
    {
        if (_disposed || !_opt.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(localZipPath))
            return;

        var job = new UploadJob
        {
            LocalPath = localZipPath,
            Attempts = 0,
            NextUtc = DateTime.UtcNow,
        };

        lock (_gate)
        {
            if (_jobs.Count >= MaxQueuedJobs)
            {
                _log("warn", $"[SLDataAPI] WebDAV 上传队列已满（上限 {MaxQueuedJobs}），丢弃 {Path.GetFileName(localZipPath)}");
                return;
            }

            _jobs.Add(job);
            _idle.Reset();
            _wake.Set();
        }
    }

    /// <summary>测试用：等到队列空且无在途上传，或超时。</summary>
    public bool WaitUntilIdle(TimeSpan timeout) => _idle.Wait(timeout);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _wake.Set(); } catch { /* ignore */ }
        try { _worker.Join(2000); } catch { /* ignore */ }
        try { _http.Dispose(); } catch { /* ignore */ }
        _wake.Dispose();
        _idle.Dispose();
        _cts.Dispose();
    }

    private void WorkerLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            UploadJob? job = null;
            TimeSpan wait = TimeSpan.FromSeconds(1);
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                int ready = -1;
                DateTime soonest = DateTime.MaxValue;
                for (int i = 0; i < _jobs.Count; i++)
                {
                    if (_jobs[i].NextUtc <= now)
                    {
                        ready = i;
                        break;
                    }
                    if (_jobs[i].NextUtc < soonest)
                        soonest = _jobs[i].NextUtc;
                }

                if (ready >= 0)
                {
                    job = _jobs[ready];
                    _jobs.RemoveAt(ready);
                    _busy++;
                }
                else if (_jobs.Count > 0)
                {
                    TimeSpan remain = soonest - now;
                    if (remain > TimeSpan.Zero)
                        wait = remain > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : remain;
                }
            }

            if (job == null)
            {
                try { _wake.Wait(wait, ct); }
                catch (OperationCanceledException) { break; }
                try { _wake.Reset(); } catch (ObjectDisposedException) { break; }
                continue;
            }

            try
            {
                Process(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log("error", Safe($"[SLDataAPI] WebDAV 上传线程异常: {ex.Message}"));
            }
            finally
            {
                lock (_gate)
                {
                    _busy--;
                    if (_jobs.Count == 0 && _busy == 0)
                        _idle.Set();
                }
            }
        }
    }

    private void Process(UploadJob job, CancellationToken ct)
    {
        job.Attempts++;
        string fileName = Path.GetFileName(job.LocalPath);
        if (!File.Exists(job.LocalPath))
        {
            _log("warn", $"[SLDataAPI] WebDAV 跳过：本地文件已不存在 {fileName}");
            return;
        }

        string remote;
        try
        {
            remote = BuildRemoteUrl(_opt.Url, _opt.RemotePathPrefix, fileName);
        }
        catch (Exception ex)
        {
            _log("warn", Safe($"[SLDataAPI] WebDAV 目标 URL 无效，放弃 {fileName}: {ex.Message}"));
            return;
        }

        string target = DescribeTarget(remote);
        UploadOutcome outcome = TryPut(job.LocalPath, remote, ct);
        if (outcome.Success)
        {
            _log("info", $"[SLDataAPI] WebDAV 已上传 {fileName} → {target}（HTTP {outcome.Status})");
            return;
        }

        int maxAttempts = 1 + Math.Max(0, _opt.MaxRetries);
        bool canRetry = outcome.Retryable && job.Attempts < maxAttempts && !ct.IsCancellationRequested;
        if (canRetry)
        {
            TimeSpan delay = Backoff(job.Attempts);
            job.NextUtc = DateTime.UtcNow + delay;
            lock (_gate)
            {
                _jobs.Add(job);
                _idle.Reset();
                _wake.Set();
            }

            string reason = string.IsNullOrEmpty(outcome.Reason) ? "失败" : outcome.Reason;
            _log("warn", Safe($"[SLDataAPI] WebDAV 上传失败 {fileName} → {target}（{reason}），{FormatDelay(delay)}后重试（{job.Attempts}/{maxAttempts}）"));
            return;
        }

        string final = string.IsNullOrEmpty(outcome.Reason) ? "失败" : outcome.Reason;
        _log("error", Safe($"[SLDataAPI] WebDAV 上传放弃 {fileName} → {target}（{final}，尝试 {job.Attempts}/{maxAttempts}）"));
    }

    private UploadOutcome TryPut(string localPath, string remoteUrl, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, remoteUrl);
            if (!string.IsNullOrEmpty(_opt.Username) || !string.IsNullOrEmpty(_opt.Password))
            {
                string token = Convert.ToBase64String(Encoding.UTF8.GetBytes((_opt.Username ?? "") + ":" + (_opt.Password ?? "")));
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
            req.Headers.TryAddWithoutValidation("Overwrite", "T");

            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var content = new StreamContent(fs);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            req.Content = content;

            using HttpResponseMessage resp = _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .GetAwaiter().GetResult();
            int code = (int)resp.StatusCode;
            if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode <= 299)
                return UploadOutcome.Ok(code);

            bool retryable = IsRetryableStatus(resp.StatusCode);
            return UploadOutcome.Fail($"HTTP {code}", retryable, code);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return UploadOutcome.Fail("超时", retryable: true);
        }
        catch (OperationCanceledException)
        {
            return UploadOutcome.Fail("超时", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            return UploadOutcome.Fail(Safe("网络错误: " + ex.Message), retryable: true);
        }
        catch (IOException ex)
        {
            return UploadOutcome.Fail(Safe("读文件失败: " + ex.Message), retryable: true);
        }
        catch (Exception ex)
        {
            return UploadOutcome.Fail(Safe(ex.Message), retryable: false);
        }
    }

    internal static bool IsRetryableStatus(HttpStatusCode status)
    {
        int code = (int)status;
        if (code >= 500) return true;
        return status == HttpStatusCode.RequestTimeout || code == 429;
    }

    private TimeSpan Backoff(int failedAttempts)
    {
        int baseSec = Math.Max(0, _opt.RetryIntervalSeconds);
        int shift = Math.Min(Math.Max(0, failedAttempts - 1), 4);
        long sec = (long)baseSec << shift;
        if (sec > MaxBackoffSeconds) sec = MaxBackoffSeconds;
        return TimeSpan.FromSeconds(sec);
    }

    private static string FormatDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return "立即";
        if (delay.TotalSeconds < 2) return $"{delay.TotalMilliseconds:0}ms";
        return $"{delay.TotalSeconds:0}s";
    }

    internal string Safe(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        if (!string.IsNullOrEmpty(_opt.Password))
            message = ReplaceOrdinal(message, _opt.Password, "***");
        if (!string.IsNullOrEmpty(_opt.Username))
            message = ReplaceOrdinal(message, _opt.Username, "***");

        message = BasicAuthInText.Replace(message, "Basic ***");
        return message;
    }

    private static string ReplaceOrdinal(string text, string find, string replace)
    {
        int i = text.IndexOf(find, StringComparison.Ordinal);
        if (i < 0) return text;
        var sb = new StringBuilder(text.Length);
        int start = 0;
        while (i >= 0)
        {
            sb.Append(text, start, i - start);
            sb.Append(replace);
            start = i + find.Length;
            i = text.IndexOf(find, start, StringComparison.Ordinal);
        }
        sb.Append(text, start, text.Length - start);
        return sb.ToString();
    }

    private static bool ContainsPlaceholder(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.IndexOf("{filename}", StringComparison.OrdinalIgnoreCase) >= 0 ||
               url.IndexOf("{file}", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ReplacePlaceholder(string url, string replacement)
    {
        url = ReplaceIgnoreCase(url, "{filename}", replacement);
        url = ReplaceIgnoreCase(url, "{file}", replacement);
        return url;
    }

    private static string ReplaceIgnoreCase(string text, string find, string replacement)
    {
        int i = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return text;
        return text.Substring(0, i) + replacement + text.Substring(i + find.Length);
    }

    private static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return "";
        var parts = new List<string>();
        foreach (string raw in prefix.Replace('\\', '/').Split('/'))
        {
            if (string.IsNullOrEmpty(raw) || raw == "." || raw == "..")
                continue;
            parts.Add(Uri.EscapeDataString(raw));
        }
        return string.Join("/", parts);
    }

    private readonly struct UploadOutcome
    {
        public bool Success { get; }
        public bool Retryable { get; }
        public int Status { get; }
        public string Reason { get; }

        private UploadOutcome(bool success, bool retryable, int status, string reason)
        {
            Success = success;
            Retryable = retryable;
            Status = status;
            Reason = reason;
        }

        public static UploadOutcome Ok(int status) => new UploadOutcome(true, false, status, "");
        public static UploadOutcome Fail(string reason, bool retryable, int status = 0) =>
            new UploadOutcome(false, retryable, status, reason);
    }
}
