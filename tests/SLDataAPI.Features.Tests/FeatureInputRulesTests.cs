// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using SLDataAPI.Control;
using Xunit;

namespace SLDataAPI.Features.Tests;

public class FeatureInputRulesTests
{
    [Theory]
    [InlineData(null, "list", true)]
    [InlineData("", "list", true)]
    [InlineData("LIST", "list", true)]
    [InlineData(" add ", "add", true)]
    [InlineData("remove", "remove", true)]
    [InlineData("clear", "clear", true)]
    [InlineData("drop", "drop", false)]
    public void InventoryActions(string? input, string normalized, bool ok)
    {
        Assert.Equal(ok, FeatureInputRules.IsInventoryAction(input, out var n));
        Assert.Equal(normalized, n);
    }

    [Theory]
    [InlineData("FoundationStaff", "mtf")]
    [InlineData("FoundationEnemy", "chaos")]
    [InlineData("foundationenemy", "chaos")]
    [InlineData("Flamingos", "flamingos")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void WaveTeam_ShortNames(string? faction, string team)
    {
        Assert.Equal(team, FeatureInputRules.WaveTeam(faction));
    }

    [Theory]
    [InlineData(0, 8, true, 1)]
    [InlineData(-5, 8, true, 1)]
    [InlineData(8, 8, true, 8)]
    [InlineData(9, 8, false, 9)]
    [InlineData(6, 6, true, 6)]
    [InlineData(7, 6, false, 7)]
    public void Amounts_RejectOverCap(int amount, int max, bool ok, int value)
    {
        Assert.Equal(ok, FeatureInputRules.TryValidateAmount(amount, max, out var v));
        Assert.Equal(value, v);
    }

    [Fact]
    public void Flicker_Clamped_NaNRejected()
    {
        Assert.True(FeatureInputRules.TryClampFlicker(0f, out var a)); Assert.Equal(0.1f, a);
        Assert.True(FeatureInputRules.TryClampFlicker(10000f, out var b)); Assert.Equal(FeatureInputRules.MaxFlickerSeconds, b);
        Assert.False(FeatureInputRules.TryClampFlicker(float.NaN, out _));
        Assert.False(FeatureInputRules.TryClampFlicker(float.PositiveInfinity, out _));
    }

    [Theory]
    [InlineData("#FF8000", 1f, 128 / 255f, 0f, 1f)]
    [InlineData("ff8000", 1f, 128 / 255f, 0f, 1f)]
    [InlineData("#F80", 1f, 136 / 255f, 0f, 1f)]
    [InlineData("#00000080", 0f, 0f, 0f, 128 / 255f)]
    public void Color_Parses(string s, float r, float g, float b, float a)
    {
        Assert.True(FeatureInputRules.TryParseColor(s, out var rr, out var gg, out var bb, out var aa, out var reset));
        Assert.False(reset);
        Assert.Equal(r, rr, 3); Assert.Equal(g, gg, 3); Assert.Equal(b, bb, 3); Assert.Equal(a, aa, 3);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("NONE")]
    [InlineData("clear")]
    public void Color_Reset(string s)
    {
        Assert.True(FeatureInputRules.TryParseColor(s, out _, out _, out _, out _, out var reset));
        Assert.True(reset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#GG0000")]
    [InlineData("#12345")]
    [InlineData("orange")]
    public void Color_Invalid(string? s)
    {
        Assert.False(FeatureInputRules.TryParseColor(s, out _, out _, out _, out _, out _));
    }
}
