using System;
using System.Collections.Concurrent;

namespace SLDataAPI.Control;

/// <summary>
/// 控制接口鉴权：token 格式校验、常量时间比较、按 IP 的暴力破解锁定。
/// </summary>
public static class ControlAuth
{
    private const int MaxFailuresPerWindow = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    // IP -> (窗口内失败次数, 窗口起始时间)
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> Failures = new();

    /// <summary>
    /// 校验 token 格式：长度不少于 8 位，且同时包含大写字母 / 小写字母 / 数字 / 特殊符号。
    /// </summary>
    public static bool IsValidTokenFormat(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 8)
            return false;

        bool upper = false, lower = false, digit = false, special = false;
        foreach (char c in token)
        {
            if (char.IsUpper(c)) upper = true;
            else if (char.IsLower(c)) lower = true;
            else if (char.IsDigit(c)) digit = true;
            else special = true;
        }

        return upper && lower && digit && special;
    }

    /// <summary>
    /// 常量时间字符串比较，避免攻击者通过响应耗时差异逐字节猜出 token。
    /// </summary>
    public static bool SecureEquals(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;

        int diff = a.Length ^ b.Length;
        int max = Math.Max(a.Length, b.Length);
        for (int i = 0; i < max; i++)
        {
            char ca = i < a.Length ? a[i] : '\0';
            char cb = i < b.Length ? b[i] : '\0';
            diff |= ca ^ cb;
        }

        return diff == 0;
    }

    /// <summary>
    /// 对某个 IP 的一次鉴权尝试。失败会计入锁定窗口；达到锁定条件的 IP 直接拒绝，不再比较 token。
    /// </summary>
    public static bool TryAuthenticate(string ip, string providedToken, string configuredToken, out string error)
    {
        ip ??= "unknown";

        if (IsLocked(ip, out var remaining))
        {
            error = $"该来源已因多次鉴权失败被临时锁定，请 {Math.Ceiling(remaining.TotalSeconds)} 秒后重试";
            return false;
        }

        if (string.IsNullOrEmpty(configuredToken))
        {
            error = "服务端未配置访问令牌";
            return false;
        }

        if (!SecureEquals(providedToken ?? string.Empty, configuredToken))
        {
            RegisterFailure(ip);
            error = "token 错误或缺失";
            return false;
        }

        ResetFailures(ip);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 鉴权失败时的诊断摘要（仅供服务器控制台日志，不返回给客户端）：
    /// 比较配置值与收到值的长度，秒级定位"YAML 截断 / 复制带空格"类问题。不泄露 token 内容。
    /// </summary>
    public static string DescribeMismatch(string? provided, string? configured)
    {
        int cfgLen = configured?.Length ?? 0;
        int gotLen = provided?.Length ?? 0;
        if (cfgLen == 0)
            return "（服务端 token 为空）";
        return gotLen == cfgLen
            ? $"（长度一致：{cfgLen}，内容不同——检查引号/空格/智能引号）"
            : $"（长度不符：配置 {cfgLen} / 收到 {gotLen}——配置值可能被 YAML 截断，含 # 等特殊字符必须加引号）";
    }

    private static bool IsLocked(string ip, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!Failures.TryGetValue(ip, out var entry))
            return false;

        if (DateTime.UtcNow - entry.WindowStart > Window && entry.Count < MaxFailuresPerWindow)
        {
            // 窗口已过期且未达到锁定阈值，视为未锁定（下一次失败会重开新窗口）
            return false;
        }

        if (entry.Count < MaxFailuresPerWindow)
            return false;

        var lockedUntil = entry.WindowStart + Window + LockDuration;
        if (DateTime.UtcNow >= lockedUntil)
        {
            Failures.TryRemove(ip, out _);
            return false;
        }

        remaining = lockedUntil - DateTime.UtcNow;
        return true;
    }

    private static void RegisterFailure(string ip)
    {
        Failures.AddOrUpdate(
            ip,
            _ => (1, DateTime.UtcNow),
            (_, old) => DateTime.UtcNow - old.WindowStart > Window
                ? (1, DateTime.UtcNow)
                : (old.Count + 1, old.WindowStart));
    }

    private static void ResetFailures(string ip) => Failures.TryRemove(ip, out _);
}
