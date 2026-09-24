using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
#if NETFRAMEWORK
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace SLDataAPI.Auth;

/// <summary>
/// 创建成功后的明文交付：写入配置目录下的一次性 txt，命令 response 只回路径。
/// 纯 IO / 字符串逻辑，无 Unity / LabAPI 依赖，可供单元测试直接链接。
/// </summary>
public static class ApiKeyCreateDelivery
{
    public const string OnceFilePrefix = "apikey_once_";
    public const string OnceFileSuffix = ".txt";
    public static readonly TimeSpan AutoDeleteAfter = TimeSpan.FromMinutes(5);
    public const string OperatorHint = "一次性文件；5 分钟后自动删除，也可自行删除。";

    /// <summary>同 id 固定文件名，再次 create（revoke 之后）会覆盖上一份。</summary>
    public static string OnceFileName(string id)
    {
        id = (id ?? "").Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        if (string.IsNullOrEmpty(id))
            id = "unnamed";
        return OnceFilePrefix + id + OnceFileSuffix;
    }

    public static string ResolveOnceFilePath(string configDir, string id) =>
        Path.Combine(string.IsNullOrWhiteSpace(configDir) ? "." : configDir, OnceFileName(id));

    /// <summary>控制台 response：路径 + 一行非机密提示。不得包含明文。</summary>
    public static string FormatConsoleResponse(string filePath)
    {
        filePath = filePath ?? "";
        return filePath + Environment.NewLine + OperatorHint;
    }

    /// <summary>
    /// 把明文写入一次性文件（同 id 覆盖）。失败不抛给调用方；error 不含明文。
    /// </summary>
    public static bool TryWriteOnceFile(string configDir, string id, string plaintext, out string filePath, out string error)
    {
        filePath = ResolveOnceFilePath(configDir, id);
        error = "";

        if (string.IsNullOrWhiteSpace(configDir))
        {
            error = "配置目录未知，无法写入一次性文件；请 revoke 后重试。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(configDir);
            File.WriteAllText(filePath, plaintext ?? "", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            // ACL/chmod 失败不得阻断创建（mono/Wine/受限 FS）
            TryTightenOnceFileAcl(filePath);
            return true;
        }
        catch (Exception)
        {
            error = "一次性文件写入失败，请 revoke 后重试。";
            return false;
        }
    }

    /// <summary>
    /// 若该精确路径仍在则尽力删除。文件已不在则什么都不做。
    /// 结果不含明文；error 只用异常类型名。
    /// </summary>
    public static OnceFileDeleteResult TryDeleteOnceFileIfPresent(string filePath)
    {
        filePath = filePath ?? "";
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return new OnceFileDeleteResult(filePath, existed: false, deleted: false, error: null);

        try
        {
            File.Delete(filePath);
            return new OnceFileDeleteResult(filePath, existed: true, deleted: true, error: null);
        }
        catch (Exception ex)
        {
            return new OnceFileDeleteResult(filePath, existed: true, deleted: false, error: ex.GetType().Name);
        }
    }

    /// <summary>默认 5 分钟后按该路径各自清理；不取消其它路径上已排队的删除。</summary>
    public static void ScheduleAutoDelete(string filePath, Action<OnceFileDeleteResult>? onSettled = null) =>
        ScheduleAutoDelete(filePath, AutoDeleteAfter, onSettled);

    /// <summary>
    /// 后台等待 <paramref name="delay"/> 后检查该精确路径：仍在则删，已不在则跳过。
    /// 不阻塞调用线程。每个路径一次调度，互不取消。
    /// </summary>
    public static void ScheduleAutoDelete(string filePath, TimeSpan delay, Action<OnceFileDeleteResult>? onSettled = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        string path = filePath;
        _ = DeleteAfterAsync(path, delay, onSettled);
    }

    private static async Task DeleteAfterAsync(string path, TimeSpan delay, Action<OnceFileDeleteResult>? onSettled)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay).ConfigureAwait(false);

            OnceFileDeleteResult result = TryDeleteOnceFileIfPresent(path);
            if (!result.Existed)
                return;

            try { onSettled?.Invoke(result); }
            catch { /* 日志回调失败不影响清理 */ }
        }
        catch
        {
            // 尽力而为
        }
    }

    public readonly struct OnceFileDeleteResult
    {
        public OnceFileDeleteResult(string filePath, bool existed, bool deleted, string? error)
        {
            FilePath = filePath ?? "";
            Existed = existed;
            Deleted = deleted;
            Error = error;
        }

        public string FilePath { get; }
        public bool Existed { get; }
        public bool Deleted { get; }
        public string? Error { get; }
    }

    /// <summary>
    /// 尽力把一次性文件收成仅当前用户可读（Windows ACL / Unix 0600）。
    /// 任何失败都吞掉：不得让 apikey create 失败。
    /// </summary>
    public static bool TryTightenOnceFileAcl(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;
        try
        {
#if NETFRAMEWORK
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return TryTightenWindowsAcl(path);
            return TryChmod0600Libc(path);
#else
            if (OperatingSystem.IsWindows())
                return true;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
#endif
        }
        catch
        {
            return false;
        }
    }

#if NETFRAMEWORK
    /// <summary>Windows 下尽量收紧 ACL：去掉继承，仅当前用户与本地 Administrators。</summary>
    private static bool TryTightenWindowsAcl(string path)
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            if (identity?.User == null)
                return false;

            var security = File.GetAccessControl(path);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                try { security.RemoveAccessRule(rule); }
                catch { /* 个别内置规则可能删不掉 */ }
            }

            security.AddAccessRule(new FileSystemAccessRule(
                identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                admins, FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(path, security);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int chmod(string pathname, uint mode);

    /// <summary>Mono / Linux：chmod 0600。libc 不可用（Wine 等）时返回 false，不抛。</summary>
    private static bool TryChmod0600Libc(string path)
    {
        try
        {
            // 0600 = S_IRUSR | S_IWUSR
            return chmod(path, 0x180) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
#endif
}
