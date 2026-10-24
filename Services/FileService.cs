// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabApi.Loader;
using LabApi.Loader.Features.Paths;

namespace SLDataAPI.Services;

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

    // 配置扩展名白名单（读/写共用）：只允许操作配置文件
    // 写：禁止 exe/dll/bat/ps1 等可执行或二进制文件；读：同样只放行配置文件
    private static readonly HashSet<string> WritableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".yml", ".yaml", ".txt", ".json", ".cfg", ".ini", ".conf", ".config", ".xml", ".properties",
    };

    /// <summary>目标是否为配置扩展名文件（读/写共用白名单）。</summary>
    private static bool IsConfigExtension(string fullPath) =>
        WritableExtensions.Contains(Path.GetExtension(fullPath));

    // 写文件互斥，避免并发 POST 写同一文件交错
    private static readonly object WriteLock = new object();

    private static string? windowsDirCache;

    /// <summary>Windows 系统目录（C:\Windows）—— 禁止任何写入。</summary>
    private static string WindowsDir()
    {
        if (windowsDirCache != null) return windowsDirCache;
        // SpecialFolder.System = C:\Windows\System32，取其父目录 = C:\Windows
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        windowsDirCache = Directory.GetParent(sys)?.FullName ?? sys;
        return windowsDirCache;
    }

    private static bool IsWindowsDir(string fullPath)
    {
        string win = WindowsDir();
        string prefix = win.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.Equals(win, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? gameDataDirCache;

    /// <summary>游戏数据目录（%AppData%/SCP Secret Laboratory）—— 禁止浏览/读取/写入。</summary>
    /// <remarks>防止篡改游戏配置（管理员名单、服务器配置等）实现"原地加冕"。
    /// ⚠️ 注意：必须精确到 "SCP Secret Laboratory"，不能直接用整个 %AppData% ——
    /// 那会把用户的全部应用数据（浏览器、其他软件配置等）一并封死。
    /// LabAPI 的插件/依赖/配置目录（...\SCP Secret Laboratory\LabAPI）也在本目录内，一并受保护。</remarks>
    private static string GameDataDir()
    {
        if (gameDataDirCache != null) return gameDataDirCache;
        gameDataDirCache = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCP Secret Laboratory"));
        return gameDataDirCache;
    }

    private static bool IsGameDataDir(string fullPath)
    {
        string gd = GameDataDir();
        string prefix = gd.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.Equals(gd, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEnabled(string root) => !string.IsNullOrWhiteSpace(root);

    // ================= 顶级防线：SLDataAPI 自身配置目录 =================
    // %AppData%\SCP Secret Laboratory\LabAPI\configs\<端口或 global>\SLDataAPI\
    // 任何人（任何角色）都禁止修改/读取该目录 —— 与 FileRoot 是否覆盖无关，绝对路径级保护。
    // 防止通过文件端点改写插件自身配置（ControlToken / FileRoot 等）实现提权。
    private static string? protectedDirCache;

    private static string ProtectedConfigDir()
    {
        if (protectedDirCache != null) return protectedDirCache;

        // 优先用 LabAPI 的配置目录 API 推导（最准确，含端口子目录，随 LabAPI 版本自动适配）
        try
        {
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                var dir = ConfigurationLoader.GetConfigDirectory(plugin)?.FullName;
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    protectedDirCache = Path.GetFullPath(dir);
                    return protectedDirCache;
                }
            }
        }
        catch { /* LabAPI 路径不可用时走回退 */ }

        // 回退：保护整个 LabAPI configs 目录（所有插件的配置都在其中）
        protectedDirCache = Path.GetFullPath(PathManager.Configs.FullName);
        return protectedDirCache;
    }

    /// <summary>目标绝对路径是否位于受保护配置目录（含目录本身）。</summary>
    private static bool IsProtected(string fullPath)
    {
        string prot = ProtectedConfigDir();
        if (string.IsNullOrEmpty(prot)) return false;
        string prefix = prot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.Equals(prot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNotProtected(string fullPath, string action)
    {
        if (IsProtected(fullPath))
            throw new InvalidOperationException($"受保护目录（SLDataAPI 配置），禁止{action}");
    }

    /// <summary>
    /// 把相对路径解析为根目录内的绝对路径；越界 / 非法路径 / 根目录不存在 / 经过符号链接或 junction 时抛异常。
    /// 实现在 <see cref="FileSandbox"/>（纯逻辑，HTTP 与 WS call 共用，单测覆盖）。
    /// </summary>
    public static string Resolve(string root, string relPath) => FileSandbox.Resolve(root, relPath);

    public static object List(string root, string relPath)
    {
        string full = Resolve(root, relPath);
        if (File.Exists(full))
            throw new ArgumentException($"不是目录: {relPath}");
        if (!Directory.Exists(full))
            throw new ArgumentException($"目录不存在: {relPath}");
        // 顶级防线：受保护配置目录本身不可打开（无论 FileRoot 是否覆盖）
        EnsureNotProtected(full, "访问");
        // 同级防线：Windows 系统目录禁止浏览
        if (IsWindowsDir(full))
            throw new InvalidOperationException("系统目录（Windows）受保护，禁止访问");
        // 同级防线：游戏数据目录（%AppData%/SCP Secret Laboratory）禁止浏览（防篡改游戏配置）
        if (IsGameDataDir(full))
            throw new InvalidOperationException("游戏数据目录（SCP Secret Laboratory）受保护，禁止访问");

        var entries = new List<object>();

        foreach (string d in Directory.GetDirectories(full))
        {
            var info = new DirectoryInfo(d);
            string fullD = Path.GetFullPath(d);
            bool prot = IsProtected(fullD) || IsWindowsDir(fullD) || IsGameDataDir(fullD);
            entries.Add(new
            {
                name = info.Name,
                type = "dir",
                size = 0L,
                modified = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                // 保护标记：前端据此显示黄色且禁止打开
                @protected = prot
            });
        }

        foreach (string f in Directory.GetFiles(full))
        {
            var info = new FileInfo(f);
            string fullF = Path.GetFullPath(f);
            bool prot = IsProtected(fullF) || !IsConfigExtension(fullF);
            entries.Add(new
            {
                name = info.Name,
                type = "file",
                size = info.Length,
                modified = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                @protected = prot
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
        var (full, info) = PreflightRead(root, relPath);
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

    /// <summary>读操作的全部防线（read / stat / read_chunk 共用，保证判定一致）。</summary>
    private static (string full, FileInfo info) PreflightRead(string root, string relPath)
    {
        string full = Resolve(root, relPath);
        // 同级防线：Windows 系统目录内禁止读取（目录级检查，先于文件存在性）
        if (IsWindowsDir(full))
            throw new InvalidOperationException("系统目录（Windows）受保护，禁止读取");
        // 同级防线：游戏数据目录内禁止读取
        if (IsGameDataDir(full))
            throw new InvalidOperationException("游戏数据目录（SCP Secret Laboratory）受保护，禁止读取");
        if (!File.Exists(full))
            throw new ArgumentException($"文件不存在: {relPath}");
        // 顶级防线：受保护配置目录内禁止读取
        EnsureNotProtected(full, "打开");
        // 非配置文件禁止读取（exe/dll 等二进制或可执行文件）
        if (!IsConfigExtension(full))
            throw new InvalidOperationException(
                $"非配置文件（{Path.GetExtension(full)}）不允许读取（仅允许配置文件：yml/yaml/txt/json/cfg/ini/conf/config/xml/properties）");
        return (full, new FileInfo(full));
    }

    public static object Write(string root, string relPath, string content)
    {
        string full = PreflightWrite(root, relPath);

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

    /// <summary>写操作的全部防线（write / write_chunk 共用，保证判定一致）。返回解析后的绝对路径。</summary>
    public static string PreflightWrite(string root, string relPath)
    {
        string full = Resolve(root, relPath);
        if (Directory.Exists(full))
            throw new ArgumentException($"目标是目录: {relPath}");
        // 顶级防线：受保护配置目录内禁止任何写入（包括新建子路径）
        EnsureNotProtected(full, "修改");
        // 系统目录保护：Windows 目录及其子目录禁止任何写入
        if (IsWindowsDir(full))
            throw new InvalidOperationException("系统目录（Windows）受保护，禁止修改");
        // 游戏数据目录保护：禁止写入（防篡改游戏配置/管理员名单）
        if (IsGameDataDir(full))
            throw new InvalidOperationException("游戏数据目录（SCP Secret Laboratory）受保护，禁止修改");
        // 写入白名单：只允许配置文件扩展名（yml/yaml/txt/json/cfg/ini/conf/config/xml/properties）
        string ext = Path.GetExtension(full);
        if (!WritableExtensions.Contains(ext))
            throw new InvalidOperationException(
                $"文件类型 {ext} 不允许写入（仅允许配置文件：yml/yaml/txt/json/cfg/ini/conf/config/xml/properties）");
        return full;
    }

    // ================= 内测（beta_file_root_ws）：分块读写 / stat =================
    // 供 WS 控制通道（单消息 256KB）与 HTTP（请求体 64KB）可靠传输接近上限的配置文件；
    // 所有路径判定复用 PreflightRead / PreflightWrite，与 list/read/write 完全同一套防线。

    /// <summary>单块读取上限（原始字节；base64 后约 256KB 以内，留出信封余量）。</summary>
    public const int MaxChunkBytes = 160 * 1024;
    public const int DefaultChunkBytes = 64 * 1024;

    public static int MaxWriteBytesLimit => MaxWriteBytes;

    /// <summary>文件 / 目录元信息；文件附带 sha256（仅 ≤ 读取上限的配置文件）。</summary>
    public static object Stat(string root, string relPath)
    {
        string full = Resolve(root, relPath);
        if (Directory.Exists(full))
        {
            bool prot = IsProtected(full) || IsWindowsDir(full) || IsGameDataDir(full);
            var di = new DirectoryInfo(full);
            return new
            {
                path = relPath,
                type = "dir",
                size = 0L,
                modified = di.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                @protected = prot,
                sha256 = (string?)null,
            };
        }

        var (f, info) = PreflightRead(root, relPath);
        string? sha = info.Length <= MaxReadBytes ? FileUploadSessions.Sha256Hex(File.ReadAllBytes(f)) : null;
        return new
        {
            path = relPath,
            type = "file",
            size = info.Length,
            modified = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss"),
            @protected = false,
            sha256 = sha,
            max_read_bytes = MaxReadBytes,
            max_write_bytes = MaxWriteBytes,
            max_chunk_bytes = MaxChunkBytes,
        };
    }

    /// <summary>按字节偏移读取一块（base64）。offset=0 时附带整文件 sha256 便于客户端校验。</summary>
    public static object ReadChunk(string root, string relPath, long offset, int length)
    {
        var (full, info) = PreflightRead(root, relPath);
        if (info.Length > MaxReadBytes)
            throw new InvalidOperationException($"文件过大（{info.Length} 字节 > 上限 {MaxReadBytes}），拒绝读取");
        if (offset < 0 || offset > info.Length)
            throw new ArgumentException($"offset 越界（0..{info.Length}）");
        if (length <= 0) length = DefaultChunkBytes;
        if (length > MaxChunkBytes) length = MaxChunkBytes;

        byte[] buf;
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            int want = (int)Math.Min(length, info.Length - offset);
            buf = new byte[want];
            int got = 0;
            while (got < want)
            {
                int n = fs.Read(buf, got, want - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < want) Array.Resize(ref buf, got);
        }

        long next = offset + buf.Length;
        return new
        {
            path = relPath,
            size = info.Length,
            offset,
            length = buf.Length,
            next_offset = next,
            eof = next >= info.Length,
            data = Convert.ToBase64String(buf),
            sha256 = offset == 0 ? FileUploadSessions.Sha256Hex(File.ReadAllBytes(full)) : null,
        };
    }

    /// <summary>
    /// 原样写入字节（分块上传提交时调用）：先经 PreflightWrite 全部防线，要求合法 UTF-8（只允许文本配置），
    /// 同目录临时文件 + 替换，避免写一半留下残缺配置。
    /// </summary>
    public static object WriteBytes(string root, string relPath, byte[] bytes)
    {
        string full = PreflightWrite(root, relPath);
        if (bytes.Length > MaxWriteBytes)
            throw new InvalidOperationException($"内容过大（{bytes.Length} 字节 > 上限 {MaxWriteBytes}），拒绝写入");
        try { new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        catch { throw new InvalidOperationException("内容不是合法 UTF-8 文本（文件端点只允许写入文本配置）"); }

        string dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        lock (WriteLock)
        {
            string tmp = full + ".sldtmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(full))
                File.Replace(tmp, full, null);
            else
                File.Move(tmp, full);
        }

        return new
        {
            path = relPath,
            bytes = bytes.Length,
            sha256 = FileUploadSessions.Sha256Hex(bytes),
        };
    }
}
