using System;
using System.IO;
using System.Text;
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
    public const string OperatorHint = "一次性文件；保存后请删除。";

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
            TryTightenWindowsAcl(filePath);
            return true;
        }
        catch (Exception)
        {
            error = "一次性文件写入失败，请 revoke 后重试。";
            return false;
        }
    }

#if NETFRAMEWORK
    /// <summary>Windows 下尽量收紧 ACL：去掉继承，仅当前用户与本地 Administrators。</summary>
    private static void TryTightenWindowsAcl(string path)
    {
        try
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;

            var identity = WindowsIdentity.GetCurrent();
            if (identity?.User == null)
                return;

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
        }
        catch
        {
            // 收紧 ACL 失败不影响交付
        }
    }
#else
    private static void TryTightenWindowsAcl(string path) { }
#endif
}
