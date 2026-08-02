using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

    public static object Tail(int lines, string filter)
    {
        string file = FindLatestLogFile()
            ?? throw new InvalidOperationException(
                "找不到服务器日志文件（已探测 %AppData%/SCP Secret Laboratory/ServerLogs 与 SCPSL_Data/Logs）");

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

    /// <summary>从文件尾部反向读取最多 maxLines 行，避免整个大日志文件读入内存。</summary>
    private static string[] ReadTailLines(string file, int maxLines)
    {
        var result = new List<string>(maxLines);
        var sb = new StringBuilder();
        int lines = 0;

        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            long pos = fs.Length;

            while (pos > 0 && lines < maxLines)
            {
                int chunk = (int)Math.Min(pos, 8192);
                pos -= chunk;
                fs.Position = pos;

                var buf = new byte[chunk];
                int read = fs.Read(buf, 0, chunk);
                string text = Encoding.UTF8.GetString(buf, 0, read);

                for (int i = text.Length - 1; i >= 0; i--)
                {
                    if (text[i] == '\n')
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                        lines++;
                        if (lines >= maxLines) break;
                    }
                    else if (text[i] != '\r')
                    {
                        // 从行首方向构建，插入到最前面
                        sb.Insert(0, text[i]);
                    }
                }

                if (lines >= maxLines) break;
            }
        }

        if (sb.Length > 0)
            result.Add(sb.ToString());

        result.Reverse();
        return result.ToArray();
    }
}
