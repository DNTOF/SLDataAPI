// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Features.Tests;

/// <summary>
/// i18n 回归测试：
///   a) zh-CN 下所有既有映射表与关键字逐项锁定；
///   b) en 下输出英文；
///   c) 稳定 code 在 zh-CN / en 下完全一致；
///   d) 非法 language 回退 zh-CN。
/// 本地化使用静态当前语言，因此本测试程序集禁用了并行执行（见 AssemblyInfo.cs）。
/// </summary>
public class LocalizationTests : IDisposable
{
    public LocalizationTests() => Localization.SetLanguage("zh-CN");

    public void Dispose() => Localization.SetLanguage("zh-CN");

    // ────────────── a) zh-CN 既有映射表逐项锁定 ──────────────

    [Theory]
    [InlineData("ClassD", "D级人员")]
    [InlineData("Scientist", "科学家")]
    [InlineData("FacilityGuard", "保安")]
    [InlineData("NtfPrivate", "九尾狐-士兵")]
    [InlineData("NtfSergeant", "九尾狐-中士")]
    [InlineData("NtfCaptain", "九尾狐-指挥官")]
    [InlineData("ChaosConscript", "混沌-征召兵")]
    [InlineData("ChaosRifleman", "混沌-步枪手")]
    [InlineData("ChaosMarauder", "混沌-掠夺者")]
    [InlineData("ChaosRepressor", "混沌-镇压者")]
    [InlineData("Scp173", "SCP-173")]
    [InlineData("Scp049", "SCP-049")]
    [InlineData("Scp096", "SCP-096")]
    [InlineData("Scp106", "SCP-106")]
    [InlineData("Scp939", "SCP-939")]
    [InlineData("Scp079", "SCP-079")]
    [InlineData("Scp3114", "SCP-3114")]
    [InlineData("Scp0492", "SCP-049-2")]
    [InlineData("Spectator", "观察者")]
    [InlineData("Tutorial", "教程")]
    [InlineData("None", "未分配")]
    public void ZhDataRoleNames_MatchPreI18nTable(string role, string expected)
    {
        Assert.Equal(expected, Localization.DataRoleText(role, 0));
    }

    [Theory]
    [InlineData("ClassD", "D级人员")]
    [InlineData("Scientist", "科学家")]
    [InlineData("FacilityGuard", "设施警卫")]
    [InlineData("NtfSpecialist", "九尾狐-中士")]
    [InlineData("NtfSergeant", "九尾狐-军士")]
    [InlineData("NtfCaptain", "九尾狐-队长")]
    [InlineData("NtfPrivate", "九尾狐-列兵")]
    [InlineData("ChaosConscript", "混沌分裂者-征召兵")]
    [InlineData("ChaosRifleman", "混沌分裂者-步枪手")]
    [InlineData("ChaosMarauder", "混沌分裂者-掠夺者")]
    [InlineData("ChaosRepressor", "混沌分裂者-镇压者")]
    [InlineData("Scp049", "SCP-049")]
    [InlineData("Scp0492", "SCP-049-2")]
    [InlineData("Scp079", "SCP-079")]
    [InlineData("Scp096", "SCP-096")]
    [InlineData("Scp106", "SCP-106")]
    [InlineData("Scp173", "SCP-173")]
    [InlineData("Scp939", "SCP-939")]
    [InlineData("Scp3114", "SCP-3114")]
    [InlineData("Tutorial", "教程角色")]
    [InlineData("Spectator", "旁观者")]
    [InlineData("Overwatch", "观察者")]
    [InlineData("Filmmaker", "摄影者")]
    public void ZhVoiceRoleNames_MatchPreI18nTable(string role, string expected)
    {
        Assert.Equal(expected, Localization.VoiceRoleText(role));
    }

    [Theory]
    [InlineData("SCPs", "SCP")]
    [InlineData("FoundationForces", "基金会")]
    [InlineData("ClassD", "D级")]
    [InlineData("ChaosInsurgency", "混沌")]
    [InlineData("Dead", "观察者")]
    [InlineData("OtherAlive", "其他")]
    public void ZhTeamNames_MatchPreI18nTable(string team, string expected)
    {
        Assert.Equal(expected, Localization.TeamText(team));
    }

    [Fact]
    public void ZhUnknownFallbacks_MatchPreI18nBehavior()
    {
        // 数据接口未知职业保留「未知(数值)」诊断兜底
        Assert.Equal("未知(42)", Localization.DataRoleText("NotARole", 42));
        // 语音接口未知职业保留枚举成员名兜底
        Assert.Equal("NotARole", Localization.VoiceRoleText("NotARole"));
        // 未知阵营沿用「未知」
        Assert.Equal("未知", Localization.TeamText("NotATeam"));
    }

    [Fact]
    public void ZhPhaseNukeAndKeywordTexts_AreLocked()
    {
        Assert.Equal("等待开始", Localization.PhaseText(false, false));
        // zh-CN 保持既有两态文案：回合已结束时仍未开始 → 等待开始
        Assert.Equal("等待开始", Localization.PhaseText(false, true));
        Assert.Equal("进行中", Localization.PhaseText(true, false));

        Assert.Equal("未激活", Localization.WarheadText(false, false, 0));
        // 拉杆已拉起但尚未开始倒计时：zh-CN 保持既有「未激活」，稳定 code 为 armed
        Assert.Equal("未激活", Localization.WarheadText(false, false, 0, armed: true));
        Assert.Equal("倒计时:42秒", Localization.WarheadText(false, true, 42));
        Assert.Equal("已爆炸", Localization.WarheadText(true, false, 0));

        // app 依赖的关键字
        Assert.Contains("超时", Localization.TimeoutRequestHeaders);
        Assert.Contains("超时", Localization.TimeoutRequestBody);
        Assert.Contains("超时", Localization.TimeoutMainThread);
        Assert.Contains("符号链接", Localization.SymlinkForbidden);
        Assert.Contains("D级", Localization.TeamText("ClassD"));
        Assert.Contains("混沌", Localization.TeamText("ChaosInsurgency"));
        Assert.Contains("基金会", Localization.TeamText("FoundationForces"));
        Assert.Contains("九尾狐", Localization.DataRoleText("NtfCaptain", 0));
        Assert.Contains("教程", Localization.DataRoleText("Tutorial", 0));
        // 源码既有文案是「观察者」/「旁观者」，不是「观众」；按 zh-CN 逐字不变约束保留原值。
        Assert.Contains("观察者", Localization.TeamText("Dead"));
        Assert.Contains("观察者", Localization.DataRoleText("Spectator", 0));
    }

    // ────────────── b) en 输出英文 ──────────────

    [Fact]
    public void EnglishOutput_IsTranslated()
    {
        Localization.SetLanguage("en");

        Assert.Equal("Class-D Personnel", Localization.DataRoleText("ClassD", 0));
        Assert.Equal("NTF Captain", Localization.DataRoleText("NtfCaptain", 0));
        Assert.Equal("NTF Specialist", Localization.DataRoleText("NtfSpecialist", 0));
        Assert.Equal("Unknown", Localization.DataRoleText("NotARole", 42));
        Assert.Equal("Facility Guard", Localization.VoiceRoleText("FacilityGuard"));
        Assert.Equal("Foundation", Localization.TeamText("FoundationForces"));
        Assert.Equal("Waiting to start", Localization.PhaseText(false, false));
        Assert.Equal("Ended", Localization.PhaseText(false, true));
        Assert.Equal("In progress", Localization.PhaseText(true, false));
        Assert.Equal("Inactive", Localization.WarheadText(false, false, 0));
        Assert.Equal("Armed", Localization.WarheadText(false, false, 0, armed: true));
        Assert.Equal("Detonating: 42s", Localization.WarheadText(false, true, 42));
        Assert.Equal("Detonated", Localization.WarheadText(true, false, 0));

        Assert.Contains("timed out", Localization.TimeoutRequestHeaders);
        Assert.Contains("symbolic link", Localization.SymlinkForbidden);
    }

    // ────────────── c) code 与语言无关 ──────────────

    [Theory]
    [InlineData("ClassD")]
    [InlineData("NtfCaptain")]
    [InlineData("ChaosInsurgency")]
    [InlineData("NotARole")]
    public void RoleAndTeamCodes_AreLanguageIndependent(string code)
    {
        Localization.SetLanguage("zh-CN");
        string zhRoleCode = Localization.RoleCode(code);
        string zhTeamCode = Localization.TeamCode(code);

        Localization.SetLanguage("en");
        Assert.Equal(zhRoleCode, Localization.RoleCode(code));
        Assert.Equal(zhTeamCode, Localization.TeamCode(code));
        Assert.Equal(code, zhRoleCode);
        Assert.Equal(code, zhTeamCode);
    }

    [Fact]
    public void PhaseWarheadAndErrorCodes_AreLanguageIndependent()
    {
        foreach (bool started in new[] { false, true })
        foreach (bool ended in new[] { false, true })
        {
            Localization.SetLanguage("zh-CN");
            string zh = Localization.PhaseCode(started, ended);
            Localization.SetLanguage("en");
            Assert.Equal(zh, Localization.PhaseCode(started, ended));
        }

        foreach (bool detonated in new[] { false, true })
        foreach (bool detonating in new[] { false, true })
        foreach (bool armed in new[] { false, true })
        {
            Localization.SetLanguage("zh-CN");
            string zh = Localization.WarheadCode(detonated, detonating, armed);
            Localization.SetLanguage("en");
            Assert.Equal(zh, Localization.WarheadCode(detonated, detonating, armed));
        }

        Localization.SetLanguage("zh-CN");
        string zhTimeout = Localization.ErrorCodeFor(408, "请求超时：头部长时间未收完");
        string zhSymlink = Localization.ErrorCodeFor(400, "路径经过符号链接或目录联接（junction），已拒绝");
        string zhUnauthorized = Localization.ErrorCodeFor(401, "API Key 无效");
        string zhForbidden = Localization.ErrorCodeFor(403, "API Key 有效但未授权该端点");
        string zhNotFound = Localization.ErrorCodeFor(404, "路径不存在");
        string zhInvalid = Localization.ErrorCodeFor(400, "缺少 action 字段");

        Localization.SetLanguage("en");
        Assert.Equal(zhTimeout, Localization.ErrorCodeFor(408, Localization.TimeoutRequestHeaders));
        Assert.Equal(zhSymlink, Localization.ErrorCodeFor(400, Localization.SymlinkForbidden));
        Assert.Equal(zhUnauthorized, Localization.ErrorCodeFor(401, "Invalid API key"));
        Assert.Equal(zhForbidden, Localization.ErrorCodeFor(403, "not authorized"));
        Assert.Equal(zhNotFound, Localization.ErrorCodeFor(404, "Path not found"));
        Assert.Equal(zhInvalid, Localization.ErrorCodeFor(400, "Missing action field"));
    }

    [Fact]
    public void RequiredErrorCodes_AreStable()
    {
        Localization.SetLanguage("zh-CN");
        Assert.Equal("timeout", Localization.ErrorCodeFor(408, "请求超时"));
        Assert.Equal("symlink_forbidden", Localization.ErrorCodeFor(400, "路径经过符号链接"));
        Assert.Equal("unauthorized", Localization.ErrorCodeFor(401, "API Key 无效"));
        Assert.Equal("forbidden", Localization.ErrorCodeFor(403, "未授权"));
        Assert.Equal("not_found", Localization.ErrorCodeFor(404, "找不到玩家"));
        Assert.Equal("invalid_argument", Localization.ErrorCodeFor(400, "缺少 target 字段"));
        Assert.Equal("armed", Localization.WarheadCode(false, false, armed: true));
        Assert.Equal("method_not_allowed", Localization.ErrorCodeFor(405, "Method Not Allowed"));
        Assert.Equal("payload_too_large", Localization.ErrorCodeFor(413, "payload too large"));
        Assert.Equal("internal_error", Localization.ErrorCodeFor(500, "内部错误"));
    }

    // ────────────── d) 非法 language 回退 ──────────────

    [Fact]
    public void InvalidLanguage_FallsBackToZhCn()
    {
        Localization.SetLanguage("en");
        Assert.False(Localization.SetLanguage("fr-FR"));
        Assert.Equal("zh-CN", Localization.Language);
        Assert.Equal("zh-CN", Localization.Lang);

        Assert.Equal("zh-CN", Localization.NormalizeLanguage("fr-FR"));
        Assert.Equal("zh-CN", Localization.NormalizeLanguage("zh"));
        Assert.Equal("zh-CN", Localization.NormalizeLanguage(null));
        Assert.Equal("en", Localization.NormalizeLanguage("EN"));
    }

    [Fact]
    public void ConfigLanguage_DefaultsToZhCn()
    {
        Assert.Equal("zh-CN", new Config().Language);
        Assert.Equal("zh-CN", Localization.DefaultLanguage);
    }

    [Fact]
    public void SetLanguage_AcceptsSupportedValuesCaseInsensitively()
    {
        Assert.True(Localization.SetLanguage("EN"));
        Assert.Equal("en", Localization.Language);
        Assert.True(Localization.SetLanguage(" zh-cn "));
        Assert.Equal("zh-CN", Localization.Language);
        Assert.True(Localization.SetLanguage(""));
        Assert.Equal("zh-CN", Localization.Language);
    }

    [Fact]
    public void Format_UsesCurrentLanguageTemplate()
    {
        Localization.SetLanguage("zh-CN");
        Assert.Equal("值=7", Localization.F("值={0}", "value={0}", 7));
        Localization.SetLanguage("en");
        Assert.Equal("value=7", Localization.F("值={0}", "value={0}", 7));
    }
}
