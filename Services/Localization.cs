// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SLDataAPI.Services;

/// <summary>
/// 接口输出本地化（纯逻辑，无 Unity / LabAPI / Newtonsoft 依赖，可供单元测试直接链接）。
///
/// 约定：
///   - <c>zh-CN</c> 是默认语言，所有既有中文字符串必须与引入 i18n 前逐字一致；
///   - <c>en</c> 只影响本插件生成的 HTTP / WebSocket 文本，不翻译游戏内昵称、
///     控制台回显、封禁 / 踢出原因等外部内容；
///   - 映射一律按枚举成员名（<c>ToString()</c>）查表，不依赖数值；
///   - 稳定 code（role_code / team_code / phase_code / warhead_code / error.code）
///     与语言无关，调用方直接使用枚举成员名或本类的 Code 方法。
/// </summary>
public static class Localization
{
    public const string ChineseSimplified = "zh-CN";
    public const string English = "en";
    public const string DefaultLanguage = ChineseSimplified;

    private static string _language = DefaultLanguage;

    /// <summary>当前输出语言：<c>zh-CN</c> 或 <c>en</c>。</summary>
    public static string Language => _language;

    /// <summary>写入 JSON 根对象 <c>lang</c> 字段的当前语言值。</summary>
    public static string Lang => _language;

    public static bool IsEnglish => string.Equals(_language, English, StringComparison.Ordinal);

    /// <summary>
    /// 设置输出语言。仅接受 <c>zh-CN</c> / <c>en</c>（大小写不敏感、忽略首尾空白）。
    /// 空值按默认 <c>zh-CN</c> 处理；非法值回退 <c>zh-CN</c> 并返回 false（由调用方打警告）。
    /// </summary>
    public static bool SetLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _language = DefaultLanguage;
            return true;
        }

        string v = value!.Trim();
        if (string.Equals(v, ChineseSimplified, StringComparison.OrdinalIgnoreCase))
        {
            _language = ChineseSimplified;
            return true;
        }
        if (string.Equals(v, English, StringComparison.OrdinalIgnoreCase))
        {
            _language = English;
            return true;
        }

        _language = DefaultLanguage;
        return false;
    }

    /// <summary>把配置值规整为受支持的语言；非法值回退 <c>zh-CN</c>。</summary>
    public static string NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultLanguage;
        string v = value!.Trim();
        if (string.Equals(v, ChineseSimplified, StringComparison.OrdinalIgnoreCase)) return ChineseSimplified;
        if (string.Equals(v, English, StringComparison.OrdinalIgnoreCase)) return English;
        return DefaultLanguage;
    }

    // 英文结果 -> 中文结果（仅 en 下写入）。ErrorCodeFor 用它把 en 文本还原成 zh 文本，
    // 保证 code 推断与语言无关。错误响应会立即推断，缓存只需覆盖最近产生的一批消息；
    // 超过上限直接清空，避免长期运行无界增长。
    private const int TranslationCacheLimit = 2048;
    private static readonly ConcurrentDictionary<string, string> EnglishToChinese =
        new(StringComparer.Ordinal);

    /// <summary>按当前语言返回 zh / en 文本；en 下记录映射供错误码推断还原中文。</summary>
    public static string T(string zh, string en)
    {
        if (!IsEnglish) return zh;
        if (!string.Equals(zh, en, StringComparison.Ordinal))
            RememberTranslation(en, zh);
        return en;
    }

    /// <summary>按当前语言返回格式化文本；en 下记录“英文结果 -> 中文结果”映射。</summary>
    public static string F(string zh, string en, params object?[] args)
    {
        if (!IsEnglish) return string.Format(zh, args);

        string enText = string.Format(en, args);
        if (!string.Equals(zh, en, StringComparison.Ordinal))
            RememberTranslation(enText, string.Format(zh, args));
        return enText;
    }

    private static void RememberTranslation(string english, string chinese)
    {
        if (EnglishToChinese.Count >= TranslationCacheLimit)
            EnglishToChinese.Clear();
        EnglishToChinese[english] = chinese;
    }

    /// <summary>en 下把已知英文消息还原为对应的中文消息；未知消息原样返回。</summary>
    private static string ResolveChineseMessage(string message)
    {
        if (IsEnglish && EnglishToChinese.TryGetValue(message, out string? zh))
            return zh;
        return message;
    }

    // ────────────── 回合阶段 ──────────────

    /// <summary>阶段稳定代码：waiting / in_progress / ended。</summary>
    public static string PhaseCode(bool roundStarted, bool roundEnded) =>
        roundStarted ? "in_progress" : roundEnded ? "ended" : "waiting";

    /// <summary>
    /// 阶段显示文本。zh-CN 保持既有两态文案（未开始统一显示「等待开始」，即使回合已结束）；
    /// en 按实际状态给出 Waiting to start / In progress / Ended。
    /// </summary>
    public static string PhaseText(bool roundStarted, bool roundEnded) =>
        roundStarted
            ? T("进行中", "In progress")
            : T("等待开始", roundEnded ? "Ended" : "Waiting to start");

    // ────────────── 核弹 ──────────────

    /// <summary>核弹稳定代码：inactive / armed / detonating / detonated。</summary>
    public static string WarheadCode(bool detonated, bool detonating, bool armed = false) =>
        detonated ? "detonated" : detonating ? "detonating" : armed ? "armed" : "inactive";

    /// <summary>
    /// 核弹显示文本。倒计时秒数沿用既有的整数截断。
    /// zh-CN 保持既有文案：仅拉起拉杆（armed）但尚未开始倒计时时仍显示「未激活」；
    /// en 下给出更准确的 "Armed"。
    /// </summary>
    public static string WarheadText(bool detonated, bool detonating, int countdownSeconds, bool armed = false)
    {
        if (detonated) return T("已爆炸", "Detonated");
        if (detonating) return F("倒计时:{0}秒", "Detonating: {0}s", countdownSeconds);
        if (armed) return T("未激活", "Armed");
        return T("未激活", "Inactive");
    }

    // ────────────── 阵营（数据接口 /get_sl_data） ──────────────

    /// <summary>阵营稳定代码：Team 枚举成员名（如 ClassD / ChaosInsurgency / FoundationForces / SCPs / Dead / OtherAlive）。</summary>
    public static string TeamCode(string teamName) => teamName ?? "";

    private static readonly Dictionary<string, (string Zh, string En)> TeamNames =
        new(StringComparer.Ordinal)
        {
            ["SCPs"] = ("SCP", "SCP"),
            ["FoundationForces"] = ("基金会", "Foundation"),
            ["ClassD"] = ("D级", "Class-D"),
            ["ChaosInsurgency"] = ("混沌", "Chaos Insurgency"),
            ["Dead"] = ("观察者", "Spectators"),
            ["OtherAlive"] = ("其他", "Other"),
        };

    /// <summary>阵营显示文本；未知成员名沿用既有「未知」兜底。</summary>
    public static string TeamText(string teamName)
    {
        if (teamName != null && TeamNames.TryGetValue(teamName, out var v))
            return T(v.Zh, v.En);
        return T("未知", "Unknown");
    }

    // ────────────── 角色（数据接口 /get_sl_data） ──────────────

    /// <summary>角色稳定代码：RoleTypeId 枚举成员名（如 Spectator / Tutorial / NtfCaptain）。</summary>
    public static string RoleCode(string roleName) => roleName ?? "";

    // zh 值必须与改造前 DataCollector.GetRoleCN 逐字一致。
    // en 额外覆盖若干原表未列出的成员（原表这些成员的中文兜底为「未知(数字)」，保持不变）。
    private static readonly Dictionary<string, (string Zh, string En)> DataRoleNames =
        new(StringComparer.Ordinal)
        {
            ["ClassD"] = ("D级人员", "Class-D Personnel"),
            ["Scientist"] = ("科学家", "Scientist"),
            ["FacilityGuard"] = ("保安", "Facility Guard"),
            ["NtfPrivate"] = ("九尾狐-士兵", "NTF Private"),
            ["NtfSergeant"] = ("九尾狐-中士", "NTF Sergeant"),
            ["NtfCaptain"] = ("九尾狐-指挥官", "NTF Captain"),
            ["ChaosConscript"] = ("混沌-征召兵", "Chaos Insurgency Conscript"),
            ["ChaosRifleman"] = ("混沌-步枪手", "Chaos Insurgency Rifleman"),
            ["ChaosMarauder"] = ("混沌-掠夺者", "Chaos Insurgency Marauder"),
            ["ChaosRepressor"] = ("混沌-镇压者", "Chaos Insurgency Repressor"),
            ["Scp173"] = ("SCP-173", "SCP-173"),
            ["Scp049"] = ("SCP-049", "SCP-049"),
            ["Scp096"] = ("SCP-096", "SCP-096"),
            ["Scp106"] = ("SCP-106", "SCP-106"),
            ["Scp939"] = ("SCP-939", "SCP-939"),
            ["Scp079"] = ("SCP-079", "SCP-079"),
            ["Scp3114"] = ("SCP-3114", "SCP-3114"),
            ["Scp0492"] = ("SCP-049-2", "SCP-049-2"),
            ["Spectator"] = ("观察者", "Spectator"),
            ["Tutorial"] = ("教程", "Tutorial"),
            ["None"] = ("未分配", "Unassigned"),
        };

    // 原中文表未覆盖、但 en 下可以给出明确英文名的成员；zh 仍走「未知(数字)」兜底。
    private static readonly Dictionary<string, string> DataRoleEnglishOnly =
        new(StringComparer.Ordinal)
        {
            ["NtfSpecialist"] = "NTF Specialist",
            ["Overwatch"] = "Overwatch",
            ["Filmmaker"] = "Filmmaker",
        };

    /// <summary>
    /// 数据接口角色显示文本。未知成员沿用改造前的 <c>未知(数值)</c> 中文兜底；
    /// en 下优先查英文补充表，仍未知则返回 <c>Unknown</c>。
    /// </summary>
    public static string DataRoleText(string roleName, int numericValue)
    {
        if (roleName != null && DataRoleNames.TryGetValue(roleName, out var v))
            return T(v.Zh, v.En);
        if (IsEnglish && roleName != null && DataRoleEnglishOnly.TryGetValue(roleName, out var en))
            return en;
        return IsEnglish ? "Unknown" : $"未知({numericValue})";
    }

    // ────────────── 角色（语音 /status 与 speaker 帧） ──────────────

    // zh 值必须与改造前 VoiceService.GetRoleCN 逐字一致。
    private static readonly Dictionary<string, (string Zh, string En)> VoiceRoleNames =
        new(StringComparer.Ordinal)
        {
            ["ClassD"] = ("D级人员", "Class-D Personnel"),
            ["Scientist"] = ("科学家", "Scientist"),
            ["FacilityGuard"] = ("设施警卫", "Facility Guard"),
            ["NtfSpecialist"] = ("九尾狐-中士", "NTF Specialist"),
            ["NtfSergeant"] = ("九尾狐-军士", "NTF Sergeant"),
            ["NtfCaptain"] = ("九尾狐-队长", "NTF Captain"),
            ["NtfPrivate"] = ("九尾狐-列兵", "NTF Private"),
            ["ChaosConscript"] = ("混沌分裂者-征召兵", "Chaos Insurgency Conscript"),
            ["ChaosRifleman"] = ("混沌分裂者-步枪手", "Chaos Insurgency Rifleman"),
            ["ChaosMarauder"] = ("混沌分裂者-掠夺者", "Chaos Insurgency Marauder"),
            ["ChaosRepressor"] = ("混沌分裂者-镇压者", "Chaos Insurgency Repressor"),
            ["Scp049"] = ("SCP-049", "SCP-049"),
            ["Scp0492"] = ("SCP-049-2", "SCP-049-2"),
            ["Scp079"] = ("SCP-079", "SCP-079"),
            ["Scp096"] = ("SCP-096", "SCP-096"),
            ["Scp106"] = ("SCP-106", "SCP-106"),
            ["Scp173"] = ("SCP-173", "SCP-173"),
            ["Scp939"] = ("SCP-939", "SCP-939"),
            ["Scp3114"] = ("SCP-3114", "SCP-3114"),
            ["Tutorial"] = ("教程角色", "Tutorial"),
            ["Spectator"] = ("旁观者", "Spectator"),
            ["Overwatch"] = ("观察者", "Overwatch"),
            ["Filmmaker"] = ("摄影者", "Filmmaker"),
        };

    /// <summary>语音角色显示文本；未知成员沿用改造前「枚举成员名」兜底。</summary>
    public static string VoiceRoleText(string roleName)
    {
        if (roleName != null && VoiceRoleNames.TryGetValue(roleName, out var v))
            return T(v.Zh, v.En);
        return roleName ?? "";
    }

    // ────────────── 稳定错误码 ──────────────

    public const string CodeTimeout = "timeout";
    public const string CodeSymlinkForbidden = "symlink_forbidden";
    public const string CodeUnauthorized = "unauthorized";
    public const string CodeForbidden = "forbidden";
    public const string CodeNotFound = "not_found";
    public const string CodeInvalidArgument = "invalid_argument";
    public const string CodeBadRequest = "bad_request";
    public const string CodeMethodNotAllowed = "method_not_allowed";
    public const string CodePayloadTooLarge = "payload_too_large";
    public const string CodeRequestHeaderTooLarge = "request_header_too_large";
    public const string CodeConflict = "conflict";
    public const string CodeInternalError = "internal_error";
    public const string CodeNotImplemented = "not_implemented";
    public const string CodeServiceUnavailable = "service_unavailable";
    public const string CodeTooManyRequests = "too_many_requests";
    public const string CodeOperationFailed = "operation_failed";
    public const string CodeAuthLocked = "auth_locked";
    public const string CodeTokenNotConfigured = "token_not_configured";

    /// <summary>
    /// 当调用点没有显式 code 时，按状态码 + 文本推断一个稳定的 snake_case 错误码。
    /// en 下先通过 T/F 记录的“英文结果 -> 中文结果”映射还原成中文，再复用同一套判断，
    /// 因此 code 与当前语言无关；zh-CN 下直接使用原文本，推断结果与改造前完全一致。
    /// </summary>
    public static string ErrorCodeFor(int status, string? message)
    {
        string m = ResolveChineseMessage(message ?? "");

        if (Contains(m, "锁定", "locked")) return CodeAuthLocked;
        if (Contains(m, "超时", "timed out", "timeout")) return CodeTimeout;
        if (Contains(m, "符号链接", "symbolic link", "junction")) return CodeSymlinkForbidden;
        // 必须早于 forbidden 的 "not allowed"：Method Not Allowed 不能被误判为权限错误。
        if (Contains(m, "method not allowed", "仅支持 POST")) return CodeMethodNotAllowed;
        if (Contains(m, "未授权", "not authorized", "unauthorized", "not allowed")) return CodeForbidden;
        if (Contains(m, "找不到", "未找到", "不存在", "not found", "not registered")) return CodeNotFound;
        if (Contains(m, "未配置访问令牌", "token 错误或缺失", "缺少 API Key", "API Key 无效",
                "missing api key", "invalid api key", "token is missing", "token is invalid"))
            return CodeUnauthorized;
        if (Contains(m, "请求头过大", "request header")) return CodeRequestHeaderTooLarge;
        if (Contains(m, "payload too large", "内容过大", "文件过大", "too large"))
            return CodePayloadTooLarge;
        if (Contains(m, "method not allowed")) return CodeMethodNotAllowed;
        if (Contains(m, "bad request")) return CodeBadRequest;
        if (Contains(m, "内部错误", "internal error", "handler error")) return CodeInternalError;
        if (Contains(m, "尚未实现", "not implemented")) return CodeNotImplemented;
        if (Contains(m, "服务不可用", "service unavailable", "未启用", "disabled")) return CodeServiceUnavailable;
        if (Contains(m, "缺少", "missing", "无效", "invalid", "非法", "不支持", "not supported",
                "超出", "exceeds", "必须", "must be", "需要", "required"))
            return CodeInvalidArgument;
        if (Contains(m, "失败", "failed")) return CodeOperationFailed;

        switch (status)
        {
            case 400: return CodeInvalidArgument;
            case 401: return CodeUnauthorized;
            case 403: return CodeForbidden;
            case 404: return CodeNotFound;
            case 405: return CodeMethodNotAllowed;
            case 408: return CodeTimeout;
            case 409: return CodeConflict;
            case 413: return CodePayloadTooLarge;
            case 429: return CodeTooManyRequests;
            case 431: return CodeRequestHeaderTooLarge;
            case 500: return CodeInternalError;
            case 501: return CodeNotImplemented;
            case 503: return CodeServiceUnavailable;
            case 504: return CodeTimeout;
            default: return CodeOperationFailed;
        }
    }

    private static bool Contains(string text, params string[] needles)
    {
        foreach (string n in needles)
        {
            if (text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    // ────────────── 关键固定文案（同时供测试锁定） ──────────────

    public static string TimeoutRequestHeaders =>
        T("请求超时：头部长时间未收完（Slowloris 防护）",
          "Request timed out: headers were not received in time (Slowloris protection)");

    public static string TimeoutRequestBody =>
        T("请求超时：请求体长时间未收完（Slowloris 防护）",
          "Request timed out: body was not received in time (Slowloris protection)");

    public static string TimeoutMainThread =>
        T("主线程执行超时（服务器可能卡顿、正在切图或已停止响应）" +
          "——若主线程在超时后恢复且操作已经开始，仍可能执行一次，请勿盲目重试非幂等操作",
          "Main-thread execution timed out (the server may be lagging, loading a map, or unresponsive)" +
          " — if the main thread recovers after the timeout and the operation has already started, it may still run once; do not blindly retry non-idempotent operations");

    public static string SymlinkForbidden =>
        T("路径经过符号链接或目录联接（junction），已拒绝（防止越出 FileRoot）",
          "Path traverses a symbolic link or directory junction; rejected to prevent escaping FileRoot");

    public static string FileRootNotConfigured =>
        T("文件端点未启用（服务器未配置 FileRoot）",
          "File endpoints are disabled (FileRoot is not configured on the server)");

    public static string InternalError => T("内部错误", "Internal error");

    public static string NotFound => T("路径不存在", "Path not found");

    public static string Forbidden => T("API Key 有效但未授权该端点",
        "API key is valid but not authorized for this endpoint");
}
