using System;

namespace SLDataAPI.Services;

/// <summary>
/// 插件侧 WebDAV 上传门面：Enable 时按 config 初始化，FinalizeRound 成功后 Enqueue。
/// 默认关闭；配置无效只 Warn 一次后本会话跳过。
/// </summary>
public static class WebDavUploadService
{
    private static readonly object Gate = new();
    private static WebDavUploader? _uploader;

    public static void Init(Config config)
    {
        Shutdown();
        if (config == null || !config.WebdavUploadEnabled)
            return;

        var opt = FromConfig(config);
        if (!WebDavUploader.TryValidate(opt, out string error))
        {
            Log.Warn($"[SLDataAPI] WebDAV 上传已启用但配置无效: {error} —— 本会话跳过");
            return;
        }

        lock (Gate)
        {
            _uploader = new WebDavUploader(opt, handler: null, log: ForwardLog);
        }

        Log.Info($"[SLDataAPI] WebDAV 自动上传已启用: {WebDavUploader.DescribeTarget(opt.Url)}");
    }

    /// <summary>zip 路径入队；未启用或未初始化时立即返回。</summary>
    public static void Enqueue(string zipPath)
    {
        WebDavUploader? uploader;
        lock (Gate) { uploader = _uploader; }
        uploader?.Enqueue(zipPath);
    }

    public static void Shutdown()
    {
        WebDavUploader? uploader;
        lock (Gate)
        {
            uploader = _uploader;
            _uploader = null;
        }
        // 停服路径：FinalizeRound 已同步入队，尽力送出最后一包后再拆线程
        try { uploader?.WaitUntilIdle(TimeSpan.FromSeconds(8)); } catch { /* 超时则继续释放 */ }
        uploader?.Dispose();
    }

    internal static WebDavUploadOptions FromConfig(Config config)
    {
        return new WebDavUploadOptions
        {
            Enabled = config.WebdavUploadEnabled,
            Url = config.WebdavUrl ?? "",
            Username = config.WebdavUsername ?? "",
            Password = config.WebdavPassword ?? "",
            RemotePathPrefix = config.WebdavRemotePathPrefix ?? "",
            TimeoutSeconds = config.WebdavTimeoutSeconds > 0
                ? config.WebdavTimeoutSeconds
                : WebDavUploader.DefaultTimeoutSeconds,
            MaxRetries = config.WebdavMaxRetries,
            RetryIntervalSeconds = config.WebdavRetryIntervalSeconds,
        };
    }

    private static void ForwardLog(string level, string message)
    {
        switch (level)
        {
            case "error": Log.Error(message); break;
            case "warn": Log.Warn(message); break;
            case "debug": Log.Debug(message); break;
            default: Log.Info(message); break;
        }
    }
}
