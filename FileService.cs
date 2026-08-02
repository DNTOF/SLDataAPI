using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// /control/files/* 端点背后的文件系统操作。
/// 所有路径都相对 Config.FileRoot（绝对路径）解析，规范化后必须仍在该根目录内，
/// 否则拒绝（防 ".." 路径穿越）。FileRoot 未配置时整个端点不可用。
/// 纯 IO 操作，在 HttpServer 的请求线程执行，不需要派发到 Unity 主线程。
/// </summary>
public static class FileService
{
    // 单次读取上限 1MB、写入上限 512KB —— 防止恶意请求拖垮服务器内存
    private const int MaxReadBytes = 1024 * 1024;
    private const int MaxWriteBytes = 512 * 1024;

    // 写文件互斥，避免并发 POST 写同一文件交错
    private static readonly object WriteLock = new object();

    public static bool IsEnabled(string root) => !string.IsNullOrWhiteSpace(root);

    /// <summary>
    /// 把相对路径解析为根目录内的绝对路径；越界 / 非法路径 / 根目录不存在时抛异常。
    /// </summary>
    public static string Resolve(string root, string relPath)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("文件端点未启用（服务器未配置 FileRoot）");

        string fullRoot;
        try { fullRoot = Path.GetFullPath(root.Trim()); }
        catch (Exception ex) { throw new InvalidOperationException($"FileRoot 非法: {ex.Message}"); }

        if (!Directory.Exists(fullRoot))
            throw new InvalidOperationException($"FileRoot 目录不存在: {fullRoot}");

        // 统一分隔符后去掉首尾斜杠，避免 ".." 或绝对路径被 Combine 拼接出界
        string rel = (relPath ?? "").Replace('\\', '/').Trim('/');
        string full;
        try { full = Path.GetFullPath(Path.Combine(fullRoot, rel)); }
        catch (Exception ex) { throw new ArgumentException($"路径非法: {ex.Message}"); }

        string rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool inside = full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                      full.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        if (!inside)
            throw new ArgumentException("路径越界：不允许访问 FileRoot 之外的目录");

        return full;
    }

    public static object List(string root, string relPath)
    {
        string full = Resolve(root, relPath);
        if (File.Exists(full))
            throw new ArgumentException($"不是目录: {relPath}");
        if (!Directory.Exists(full))
            throw new ArgumentException($"目录不存在: {relPath}");

        var entries = new List<object>();

        foreach (string d in Directory.GetDirectories(full))
        {
            var info = new DirectoryInfo(d);
            entries.Add(new
            {
                name = info.Name,
                type = "dir",
                size = 0L,
                modified = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        foreach (string f in Directory.GetFiles(full))
        {
            var info = new FileInfo(f);
            entries.Add(new
            {
                name = info.Name,
                type = "file",
                size = info.Length,
                modified = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        return new
        {
            path = relPath,
            count = entries.Count,
            entries
        };
    }

    public static object Read(string root, string relPath)
    {
        string full = Resolve(root, relPath);
        if (!File.Exists(full))
            throw new ArgumentException($"文件不存在: {relPath}");

        var info = new FileInfo(full);
        if (info.Length > MaxReadBytes)
            throw new InvalidOperationException($"文件过大（{info.Length} 字节 > 上限 {MaxReadBytes}），拒绝读取");

        string content = File.ReadAllText(full);
        return new
        {
            path = relPath,
            size = content.Length,
            modified = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
            content
        };
    }

    public static object Write(string root, string relPath, string content)
    {
        string full = Resolve(root, relPath);
        if (Directory.Exists(full))
            throw new ArgumentException($"目标是目录: {relPath}");

        if (content != null && content.Length > MaxWriteBytes)
            throw new InvalidOperationException($"内容过大（{content.Length} 字符 > 上限 {MaxWriteBytes}），拒绝写入");

        string dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        lock (WriteLock)
        {
            File.WriteAllText(full, content ?? "", System.Text.Encoding.UTF8);
        }

        return new
        {
            path = relPath,
            bytes = (content ?? "").Length
        };
    }
}
