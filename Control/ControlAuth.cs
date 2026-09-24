using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SLDataAPI.Control;

/// <summary>
/// 控制接口鉴权（v2.6.0 推出，代号 PEAK：双轨 verify_token + API Key）：格式校验、常量时间比较、按 IP 的暴力破解锁定。
/// 锁定按权限分级（M-02）：
///   - 只读数据接口（/get_sl_data，verify_token 低权限）单独一张失败表——
///     攻击者刷数据接口不会锁死管理员的高权限通道；
///   - 控制/语音（API Key 高权限）共用另一张失败表。
/// 失败表带周期清扫（窗口过期 + 锁定期即删），IPv6 海量源地址不会造成无界内存增长。
/// </summary>
public static class ControlAuth
{
    private const int MaxFailuresPerWindow = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);

    // 失败表按权限分级（IP -> (窗口内失败次数, 窗口起始时间)）
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> ReadFailures = new();
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> ControlFailures = new();

    private static Timer? _sweeper;

    private static ConcurrentDictionary<string, (int Count, DateTime WindowStart)> TableFor(bool highPrivilege) =>
        highPrivilege ? ControlFailures : ReadFailures;

    private static void EnsureSweeper()
    {
        if (_sweeper != null) return;
        _sweeper = new Timer(_ =>
        {
            Sweep(ReadFailures);
            Sweep(ControlFailures);
        }, null, 60000, 60000);
    }

    /// <summary>删除窗口+锁定期均已过期的条目（IPv6 海量源地址不会无界增长）。</summary>
    private static void Sweep(ConcurrentDictionary<string, (int Count, DateTime WindowStart)> table)
    {
        DateTime cutoff = DateTime.UtcNow - Window - LockDuration;
        foreach (var kv in table)
        {
            if (kv.Value.WindowStart < cutoff)
                table.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>文档与 config.yml 出厂默认值；启用时 fail-closed，不得当作有效数据口令。</summary>
    public const string FactoryDefaultVerifyToken = "your_secret_token";

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
    /// 数据口 verify_token 是否允许对外提供服务：非空、非出厂默认、且通过 <see cref="IsValidTokenFormat"/>。
    /// </summary>
    public static bool IsAcceptableVerifyToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        token = token.Trim();
        if (string.Equals(token, FactoryDefaultVerifyToken, StringComparison.Ordinal))
            return false;
        return IsValidTokenFormat(token);
    }

    /// <summary>拒绝原因（不含 token 原文），供启动 Error 日志。</summary>
    public static string DescribeVerifyTokenRejection(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "为空或仅空白";
        if (string.Equals(token.Trim(), FactoryDefaultVerifyToken, StringComparison.Ordinal))
            return "仍为出厂默认值 your_secret_token";
        if (!IsValidTokenFormat(token.Trim()))
            return "强度不足（长度须≥8，且同时含大写、小写、数字、特殊符号）";
        return "";
    }

    /// <summary>
    /// 数据口令提取：优先 Authorization: Bearer / X-SLDataAPI-Token / X-SLDataAPI-Verify-Token，
    /// 回退 URL <c>?token=</c>（兼容旧客户端；会出现在访问日志，建议改用请求头）。
    /// </summary>
    public static string ExtractDataPlaneToken(IDictionary<string, string>? headers, string? query)
    {
        if (headers != null)
        {
            if (TryGetHeader(headers, "Authorization", out string auth) &&
                auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string bearer = auth.Substring(7).Trim();
                if (bearer.Length > 0)
                    return bearer;
            }

            if (TryGetHeader(headers, "X-SLDataAPI-Token", out string alias) && alias.Length > 0)
                return alias;
            if (TryGetHeader(headers, "X-SLDataAPI-Verify-Token", out string alias2) && alias2.Length > 0)
                return alias2;
        }

        return ExtractQueryToken(query);
    }

    /// <summary>查询串是否带 token=（用于弃用提示；不返回值以免入日志）。</summary>
    public static bool QueryHasTokenParam(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return false;
        foreach (var pair in query!.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;
            int eq = pair.IndexOf('=');
            string k = eq >= 0 ? pair.Substring(0, eq) : pair;
            if (string.Equals(k, "token", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryGetHeader(IDictionary<string, string> headers, string name, out string value)
    {
        if (headers.TryGetValue(name, out value!) && !string.IsNullOrWhiteSpace(value))
        {
            value = value.Trim();
            return true;
        }

        foreach (var kv in headers)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(kv.Value))
            {
                value = kv.Value.Trim();
                return true;
            }
        }

        value = "";
        return false;
    }

    private static string ExtractQueryToken(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return "";
        foreach (var pair in query!.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;
            int eq = pair.IndexOf('=');
            string k = eq >= 0 ? pair.Substring(0, eq) : pair;
            string v = eq >= 0 ? pair.Substring(eq + 1) : "";
            if (string.Equals(k, "token", StringComparison.OrdinalIgnoreCase))
            {
                try { return Uri.UnescapeDataString(v); }
                catch { return v; }
            }
        }
        return "";
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
    /// 对某个 IP 的一次鉴权尝试。失败会计入对应权限级的锁定窗口；达到锁定条件的 IP 直接拒绝，不再比较 token。
    /// highPrivilege=false 用于只读数据接口（verify_token），true 用于控制/语音（API Key）。
    /// </summary>
    public static bool TryAuthenticate(string ip, string providedToken, string configuredToken, out string error,
        bool highPrivilege = true)
    {
        ip ??= "unknown";
        var table = TableFor(highPrivilege);

        if (IsLocked(ip, table, out var remaining))
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
            RegisterFailure(ip, table);
            error = "token 错误或缺失";
            return false;
        }

        ResetFailures(ip, table);
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

    private static bool IsLocked(string ip,
        ConcurrentDictionary<string, (int Count, DateTime WindowStart)> table, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        EnsureSweeper();
        if (!table.TryGetValue(ip, out var entry))
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
            table.TryRemove(ip, out _);
            return false;
        }

        remaining = lockedUntil - DateTime.UtcNow;
        return true;
    }

    private static void RegisterFailure(string ip,
        ConcurrentDictionary<string, (int Count, DateTime WindowStart)> table)
    {
        table.AddOrUpdate(
            ip,
            _ => (1, DateTime.UtcNow),
            (_, old) => DateTime.UtcNow - old.WindowStart > Window
                ? (1, DateTime.UtcNow)
                : (old.Count + 1, old.WindowStart));
    }

    private static void ResetFailures(string ip,
        ConcurrentDictionary<string, (int Count, DateTime WindowStart)> table) =>
        table.TryRemove(ip, out _);

    /// <summary>控制/语音通道是否因失败过多被锁定（供 API Key 鉴权复用）。</summary>
    public static bool IsControlLocked(string ip, out TimeSpan remaining) =>
        IsLocked(ip ?? "unknown", ControlFailures, out remaining);

    /// <summary>登记一次控制面鉴权失败。</summary>
    public static void RegisterControlFailure(string ip) =>
        RegisterFailure(ip ?? "unknown", ControlFailures);

    /// <summary>鉴权成功后清除控制面失败计数。</summary>
    public static void ResetControlFailures(string ip) =>
        ResetFailures(ip ?? "unknown", ControlFailures);
}