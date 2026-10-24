// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections.Generic;
using System.Linq;

namespace SLDataAPI.Services;

/// <summary>节日 / 游戏功能可用性判定结果（纯数据，无 Unity 依赖，可单测）。</summary>
public sealed class HolidayGateResult
{
    public bool Available { get; }
    /// <summary>不可用时建议的 HTTP 状态：409 = 节日未开启；501 = 当前游戏版本缺少该功能。可用时为 200。</summary>
    public int Status { get; }
    /// <summary>机器可读错误码：holiday_inactive | feature_unavailable；可用时为空串。</summary>
    public string Code { get; }
    public string Feature { get; }
    public string ActiveHoliday { get; }
    public IReadOnlyList<string> Required { get; }
    public string Message { get; }
    /// <summary>是否处于测试覆盖（SLDATAAPI_HOLIDAY_OVERRIDE）下的模拟判定。</summary>
    public bool Simulated { get; }

    internal HolidayGateResult(bool available, int status, string code, string feature,
        string activeHoliday, IReadOnlyList<string> required, string message, bool simulated = false)
    {
        Simulated = simulated;
        Available = available;
        Status = status;
        Code = code;
        Feature = feature;
        ActiveHoliday = activeHoliday;
        Required = required;
        Message = message;
    }

    /// <summary>失败响应的 data 字段（放进统一信封 {success:false,message,data}）。</summary>
    public Dictionary<string, object> ToData()
    {
        var d = new Dictionary<string, object>
        {
            ["code"] = Code,
            ["feature"] = Feature,
            ["active_holiday"] = ActiveHoliday,
            ["required"] = Required.ToArray(),
        };
        if (Simulated) d["simulated"] = true;
        return d;
    }
}

/// <summary>
/// 节日功能门控：节日专属端点/字段在运行时判定可用性，不可用时给出结构化错误而不是抛异常。
/// 判定顺序（保守，fail-closed）：
///   1. 功能所需的游戏类型/成员缺失（未来版本删改）→ 501 feature_unavailable；
///   2. 游戏自身给出了可用性（IHolidayItem/IHolidayEffect.IsAvailable、当前糖果池等）→ 以游戏为准；
///   3. 否则按“当前节日 ∈ 所需节日”判定；所需节日为空 = 不受节日限制。
/// 当前节日由游戏构建决定（GameCore.Version.ActiveHoliday），因此明年游戏重新开启万圣节时无需改代码即自动可用。
/// </summary>
public static class HolidayGate
{
    public const string CodeHolidayInactive = "holiday_inactive";
    public const string CodeFeatureUnavailable = "feature_unavailable";
    public const int StatusHolidayInactive = 409;
    public const int StatusFeatureUnavailable = 501;

    /// <summary>
    /// 测试钩子：环境变量 SLDATAAPI_HOLIDAY_OVERRIDE（None / Halloween / Christmas / AprilFools）。
    /// 只影响本插件的门控判定，不改游戏；且只能“收紧”——游戏本身不可用的功能不会因覆盖变为可用。
    /// 用于在节日版本上演练“节日未开启”路径。null = 未启用。
    /// </summary>
    public static string? TestOverride { get; private set; }

    public const string OverrideEnvVar = "SLDATAAPI_HOLIDAY_OVERRIDE";

    /// <summary>设置 / 清除测试覆盖；非法值忽略并返回 false。</summary>
    public static bool SetTestOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { TestOverride = null; return true; }
        string v = value!.Trim();
        foreach (var known in KnownHolidays)
        {
            if (string.Equals(known, v, StringComparison.OrdinalIgnoreCase))
            {
                TestOverride = known;
                return true;
            }
        }
        return false;
    }

    public static readonly string[] KnownHolidays = { "None", "Christmas", "Halloween", "AprilFools" };

    public static HolidayGateResult Evaluate(
        string feature,
        string? activeHoliday,
        IEnumerable<string>? requiredHolidays,
        bool featurePresent = true,
        bool? gameReportsAvailable = null)
    {
        bool simulated = TestOverride != null;
        string active = simulated ? TestOverride! : NormalizeHoliday(activeHoliday);
        var required = (requiredHolidays ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        feature = string.IsNullOrWhiteSpace(feature) ? "unknown" : feature.Trim();

        if (!featurePresent)
            return new HolidayGateResult(false, StatusFeatureUnavailable, CodeFeatureUnavailable, feature, active, required,
                $"当前游戏版本不提供该功能（{feature}：所需类型或成员不存在）", simulated);

        bool matches = required.Count == 0 ||
                       required.Any(r => string.Equals(r, active, StringComparison.OrdinalIgnoreCase));
        // 覆盖模式：游戏判定（缺省视为可用）与覆盖节日匹配必须同时满足——只收紧不放宽
        bool available = simulated
            ? (gameReportsAvailable ?? true) && matches
            : gameReportsAvailable ?? matches;
        if (available)
            return new HolidayGateResult(true, 200, "", feature, active, required, "ok", simulated);

        string need = required.Count == 0 ? "特定节日" : string.Join(" / ", required);
        return new HolidayGateResult(false, StatusHolidayInactive, CodeHolidayInactive, feature, active, required,
            $"节日功能未开启：{feature} 需要 {need}，当前节日 {active}" + (simulated ? "（测试覆盖）" : ""), simulated);
    }

    /// <summary>空 / null / 空白 → "None"；其余原样去空白。</summary>
    public static string NormalizeHoliday(string? holiday) =>
        string.IsNullOrWhiteSpace(holiday) ? "None" : holiday!.Trim();
}
