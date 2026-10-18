// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Features.Tests;

// 测试覆盖是静态状态：与其它修改它的测试放同一集合，禁止并行
[Collection("HolidayOverride")]
public class HolidayGateTests : IDisposable
{
    public HolidayGateTests() => HolidayGate.SetTestOverride(null);
    public void Dispose() => HolidayGate.SetTestOverride(null);

    [Fact]
    public void NoRequirement_AlwaysAvailable()
    {
        var r = HolidayGate.Evaluate("item:Medkit", "None", null);
        Assert.True(r.Available);
        Assert.Equal(200, r.Status);
        Assert.Equal("", r.Code);
    }

    [Fact]
    public void HalloweenActive_HalloweenFeatureAvailable()
    {
        var r = HolidayGate.Evaluate("candy:Evil", "Halloween", new[] { "Halloween" });
        Assert.True(r.Available);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Christmas")]
    [InlineData("")]
    [InlineData(null)]
    public void HolidayInactive_Returns409Structured(string? active)
    {
        var r = HolidayGate.Evaluate("candy:Evil", active, new[] { "Halloween" });
        Assert.False(r.Available);
        Assert.Equal(409, r.Status);
        Assert.Equal(HolidayGate.CodeHolidayInactive, r.Code);
        var d = r.ToData();
        Assert.Equal("holiday_inactive", d["code"]);
        Assert.Equal("candy:Evil", d["feature"]);
        Assert.Equal(string.IsNullOrWhiteSpace(active) ? "None" : active, d["active_holiday"]);
        Assert.Equal(new[] { "Halloween" }, (string[])d["required"]);
        Assert.False(d.ContainsKey("simulated"));
    }

    [Fact]
    public void AnyOfRequired_MatchesCaseInsensitive()
    {
        // MarshmallowEffect.ActiveHolidays = Halloween + Christmas
        Assert.True(HolidayGate.Evaluate("effect:MarshmallowEffect", "christmas", new[] { "Halloween", "Christmas" }).Available);
        Assert.False(HolidayGate.Evaluate("effect:MarshmallowEffect", "AprilFools", new[] { "Halloween", "Christmas" }).Available);
    }

    [Fact]
    public void MissingType_FailsClosedWith501()
    {
        // 未来版本删除/改名类型：即使节日匹配也不可用
        var r = HolidayGate.Evaluate("candy:Evil", "Halloween", new[] { "Halloween" }, featurePresent: false);
        Assert.False(r.Available);
        Assert.Equal(501, r.Status);
        Assert.Equal(HolidayGate.CodeFeatureUnavailable, r.Code);
    }

    [Fact]
    public void GameVerdict_TakesPrecedence()
    {
        // 开发 / 夜间构建：游戏认为可用（即使 ActiveHoliday=None）
        Assert.True(HolidayGate.Evaluate("item:MarshmallowItem", "None", new[] { "Halloween" }, true, true).Available);
        // 游戏认为不可用：以游戏为准
        var r = HolidayGate.Evaluate("item:MarshmallowItem", "Halloween", new[] { "Halloween" }, true, false);
        Assert.False(r.Available);
        Assert.Equal(409, r.Status);
    }

    [Fact]
    public void NextYear_SameCodeWorksWhenHolidayReturns()
    {
        // 同一调用，仅“当前节日”这个运行时输入变化：无需改代码
        var required = new[] { "Halloween" };
        Assert.False(HolidayGate.Evaluate("candy:Evil", "None", required).Available);
        Assert.True(HolidayGate.Evaluate("candy:Evil", "Halloween", required).Available);
    }

    [Fact]
    public void Override_OnlyTightens()
    {
        Assert.True(HolidayGate.SetTestOverride("none"));
        Assert.Equal("None", HolidayGate.TestOverride);

        // 游戏说可用、真实节日 Halloween，但覆盖为 None → 节日功能被判为未开启
        var r = HolidayGate.Evaluate("candy:Evil", "Halloween", new[] { "Halloween" }, true, true);
        Assert.False(r.Available);
        Assert.Equal(409, r.Status);
        Assert.True(r.Simulated);
        Assert.Equal("None", r.ToData()["active_holiday"]);
        Assert.Equal(true, r.ToData()["simulated"]);

        // 非节日功能不受影响
        Assert.True(HolidayGate.Evaluate("item:Medkit", "Halloween", null, true, true).Available);

        // 覆盖为 Halloween 也不能放宽游戏的“不可用”
        Assert.True(HolidayGate.SetTestOverride("Halloween"));
        Assert.False(HolidayGate.Evaluate("item:MarshmallowItem", "None", new[] { "Halloween" }, true, false).Available);
        // 类型缺失仍是 501
        Assert.Equal(501, HolidayGate.Evaluate("x", "Halloween", null, featurePresent: false).Status);
    }

    [Fact]
    public void Override_InvalidValueIgnored()
    {
        Assert.False(HolidayGate.SetTestOverride("Easter"));
        Assert.Null(HolidayGate.TestOverride);
        Assert.True(HolidayGate.SetTestOverride("  "));
        Assert.Null(HolidayGate.TestOverride);
    }

    [Fact]
    public void RequiredList_Deduplicated_AndBlankDropped()
    {
        var r = HolidayGate.Evaluate("f", "None", new[] { "Halloween", "halloween", " ", "" });
        Assert.Single(r.Required);
    }
}
