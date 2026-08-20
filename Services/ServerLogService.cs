using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SLDataAPI.Services;

/// <summary>
/// 读取 SCP:SL 服务器自己的日志文件。
/// 探测顺序：
///   1. Config.LogDirectory（显式配置优先）
///   2. %AppData%/SCP Secret Laboratory/ServerLogs/（含各端口子目录，如 7777）
///   3. SCPSL_Data/Logs
/// 取其中最后修改的 .log/.txt 文件（游戏日志按天/按轮滚动，最新的才是当前轮次）。
/// 只读、尾部读取、可选关键词过滤；找不到时返回明确错误而不是崩溃。
/// 纯 IO 操作，在 HttpServer 的请求线程执行。
/// </summary>
public static class ServerLogService
{
    private static readonly string[] CandidateDirs =
    {
        "SCPSL_Data/Logs",
        Path.Combine(Environment.CurrentDirectory, "SCPSL_Data", "Logs"),
    };

    /// <summary>%AppData%/SCP Secret Laboratory/ServerLogs（Windows 服务器标准位置）。</summary>
    private static string AppDataLogsRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCP Secret Laboratory",
            "ServerLogs");

    /// <summary>收集所有候选日志目录（显式配置 + AppData/ServerLogs 根与端口子目录 + SCPSL_Data/Logs）。</summary>
    private static List<string> GetLogDirs()
    {
        var dirs = new List<string>();

        string? configured = Plugin.Instance?.Config.LogDirectory;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            dirs.Add(configured!);

        if (Directory.Exists(AppDataLogsRoot))
        {
            dirs.Add(AppDataLogsRoot);
            foreach (string sub in Directory.GetDirectories(AppDataLogsRoot))
                dirs.Add(sub);
        }

        dirs.AddRange(CandidateDirs.Where(Directory.Exists));
        return dirs;
    }

    /// <summary>列出所有可用日志文件（按修改时间倒序）。</summary>
    public static object ListLogFiles()
    {
        // (修改时间, 路径, 名称, 大小, 修改时间文本) —— 避免 dynamic 依赖 Microsoft.CSharp
        var files = new List<(DateTime time, string path, string name, long size, string modified)>();
        foreach (string dir in GetLogDirs())
        {
            IEnumerable<string> found;
            try
            {
                found = Directory.GetFiles(dir, "*.log").Concat(Directory.GetFiles(dir, "*.txt"));
            }
            catch
            {
                continue; // 无权限等异常跳过该目录
            }
            foreach (string f in found)
            {
                try
                {
                    var info = new FileInfo(f);
                    files.Add((info.LastWriteTimeUtc, f, info.Name, info.Length,
                        info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss")));
                }
                catch { /* 单个文件异常不影响其他 */ }
            }
        }

        return new
        {
            count = files.Count,
            files = files
                .OrderByDescending(x => x.time)
                .Select(x => new
                {
                    path = x.path,
                    name = x.name,
                    size = x.size,
                    modified = x.modified,
                })
                .ToList(),
        };
    }

    /// <summary>
    /// 读取日志尾部。path 为空时自动取最新日志文件；
    /// path 非空时必须位于候选日志目录内且扩展名为 .log/.txt，否则拒绝（防任意文件读取）。
    /// </summary>
    public static object Tail(int lines, string filter, string? path = null)
    {
        string file;
        if (string.IsNullOrWhiteSpace(path))
        {
            file = FindLatestLogFile()
                ?? throw new InvalidOperationException(
                    "找不到服务器日志文件（已探测 %AppData%/SCP Secret Laboratory/ServerLogs 与 SCPSL_Data/Logs）");
        }
        else
        {
            // 安全校验：规范化后必须位于候选日志目录内 + 扩展名白名单（.log/.txt）
            string full;
            try { full = Path.GetFullPath(path); }
            catch { throw new ArgumentException("日志路径非法"); }

            string ext = Path.GetExtension(full);
            if (!string.Equals(ext, ".log", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"日志接口只允许读取 .log/.txt 文件（收到 {ext}）");

            bool inside = GetLogDirs().Any(dir =>
            {
                string prefix;
                try { prefix = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
                catch { return false; }
                return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            });
            if (!inside)
                throw new ArgumentException("日志路径不在服务器日志目录内，拒绝读取");

            if (!File.Exists(full))
                throw new ArgumentException($"日志文件不存在: {path}");
            file = full;
        }

        // net48 没有 Math.Clamp
        int n = Math.Max(1, Math.Min(2000, lines <= 0 ? 200 : lines));
        string[] tail = ReadTailLines(file, n);

        if (!string.IsNullOrWhiteSpace(filter))
            tail = tail.Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

        return new
        {
            file = Path.GetFileName(file),
            path = file,
            total = tail.Length,
            lines = tail
        };
    }

    /// <summary>在所有候选位置里找最后修改的日志文件；找不到返回 null。</summary>
    private static string? FindLatestLogFile()
    {
        var dirs = new List<string>();

        // 显式配置优先
        string? configured = Plugin.Instance?.Config.LogDirectory;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            dirs.Add(configured!);

        // %AppData%/SCP Secret Laboratory/ServerLogs 根 + 端口子目录（7777 等）
        if (Directory.Exists(AppDataLogsRoot))
        {
            dirs.Add(AppDataLogsRoot);
            foreach (string sub in Directory.GetDirectories(AppDataLogsRoot))
                dirs.Add(sub);
        }

        // SCPSL_Data/Logs
        dirs.AddRange(CandidateDirs.Where(Directory.Exists));

        string? latest = null;
        DateTime latestTime = DateTime.MinValue;

        foreach (string dir in dirs)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.GetFiles(dir, "*.log").Concat(Directory.GetFiles(dir, "*.txt"));
            }
            catch
            {
                continue; // 无权限等异常跳过该目录
            }

            foreach (string f in files)
            {
                try
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > latestTime)
                    {
                        latestTime = t;
                        latest = f;
                    }
                }
                catch { /* 单个文件异常不影响其他 */ }
            }
        }

        return latest;
    }

    /// <summary>
    /// 从文件尾部读取最多 maxLines 行（Y-01 修复：单窗口整体解码）。
    /// 先读尾部一个窗口（初始 64KB）并一次性 UTF-8 解码——整段解码不存在跨块边界，
    /// 多字节字符永不损坏；行数不足则窗口翻倍重读（上限 8MB），内存有界。
    /// 旧实现按 8KB 分块解码 + 块尾多读 3 字节，重叠区的字符会被重复发射
    /// （实测 "0123456789" → "0123423456786789"），已废弃。
    /// </summary>
    private static string[] ReadTailLines(string file, int maxLines)
    {
        const long InitialWindow = 64 * 1024;
        const long MaxWindow = 8 * 1024 * 1024;

        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            long fileLen = fs.Length;
            long window = InitialWindow;

            while (true)
            {
                long readFrom = Math.Max(0, fileLen - window);
                fs.Position = readFrom;

                int bufLen = (int)(fileLen - readFrom);
                var buf = new byte[bufLen];
                int read = 0;
                while (read < bufLen)
                {
                    int n = fs.Read(buf, read, bufLen - read);
                    if (n <= 0) break;
                    read += n;
                }

                string text = Encoding.UTF8.GetString(buf, 0, read);
                if (readFrom > 0)
                {
                    // 窗口起点落在多字节字符中间时，开头会解出替换符（U+FFFD）——
                    // 那是被切开的字符残片，丢弃（读到文件头时不存在此问题，不裁剪）
                    text = text.TrimStart('\uFFFD');
                }

                string[] lines = SplitTailLines(text, maxLines);

                // 收工条件：读到文件头（首行天然完整），或窗口内换行数已够 maxLines
                //（返回的最老一行有 '\n' 兜底，未被窗口起点从中间切断），或窗口到顶（尽力而为）。
                // 仅凭 lines.Length >= maxLines 不够——最老一行可能是被窗口切开的半行
                int newlines = 0;
                for (int i = 0; i < text.Length && newlines < maxLines; i++)
                    if (text[i] == '\n') newlines++;

                if (readFrom == 0 || newlines >= maxLines || window >= MaxWindow)
                    return lines;

                window = Math.Min(window * 2, MaxWindow);
            }
        }
    }

    /// <summary>从已整体解码的文本中取末尾 maxLines 行。</summary>
    private static string[] SplitTailLines(string text, int maxLines)
    {
        var result = new List<string>(Math.Min(maxLines, 64));
        var sb = new StringBuilder();
        int lines = 0;

        for (int i = text.Length - 1; i >= 0 && lines < maxLines; i--)
        {
            char c = text[i];
            if (c == '\n')
            {
                result.Add(sb.ToString());
                sb.Clear();
                lines++;
            }
            else if (c != '\r')
            {
                sb.Insert(0, c); // 从行首方向构建，插入到最前面
            }
        }

        if (sb.Length > 0 && lines < maxLines)
            result.Add(sb.ToString());

        result.Reverse();
        return result.ToArray();
    }
}
