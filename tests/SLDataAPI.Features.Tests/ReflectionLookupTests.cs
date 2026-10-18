// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Features.Tests;

public interface ISampleHoliday
{
    bool IsAvailable { get; }
    string[] TargetHolidays { get; }
}

public class SampleHolidayItem : ISampleHoliday
{
    public bool IsAvailable => false;
    public string[] TargetHolidays => new[] { "Halloween" };
}

public class ThrowingProperty
{
    public bool Boom => throw new InvalidOperationException("boom");
    public static int[] StaticField = { 1, 2, 3 };
}

[Collection("HolidayOverride")]
public class ReflectionLookupTests
{
    [Fact]
    public void MissingType_ReturnsNull_AndIsCached()
    {
        ReflectionLookup.ResetForTests();
        Assert.Null(ReflectionLookup.FindType("InventorySystem.Items.Usables.Scp330.DoesNotExist"));
        int after1 = ReflectionLookup.ResolveCount;
        Assert.Null(ReflectionLookup.FindType("InventorySystem.Items.Usables.Scp330.DoesNotExist"));
        Assert.Equal(after1, ReflectionLookup.ResolveCount); // 第二次走缓存
    }

    [Fact]
    public void ExistingType_Found()
    {
        Assert.Equal(typeof(SampleHolidayItem), ReflectionLookup.FindType(typeof(SampleHolidayItem).FullName!));
    }

    [Fact]
    public void MissingMember_ReturnsNull_TryGetValueFalse()
    {
        var m = ReflectionLookup.FindMember(typeof(SampleHolidayItem), "RenamedInFutureBuild");
        Assert.Null(m);
        Assert.False(ReflectionLookup.TryGetValue(m, new SampleHolidayItem(), out _));
        Assert.Null(ReflectionLookup.FindMember(null, "IsAvailable"));
    }

    [Fact]
    public void ThrowingGetter_FailsClosed()
    {
        var m = ReflectionLookup.FindMember(typeof(ThrowingProperty), "Boom");
        Assert.NotNull(m);
        Assert.False(ReflectionLookup.TryGetValue(m, new ThrowingProperty(), out var v));
        Assert.Null(v);
    }

    [Fact]
    public void StaticField_Readable()
    {
        var m = ReflectionLookup.FindMember(typeof(ThrowingProperty), "StaticField");
        Assert.True(ReflectionLookup.TryGetValue(m, null, out var v));
        Assert.Equal(new[] { 1, 2, 3 }, (int[])v!);
    }

    [Fact]
    public void InterfaceByName_ReadsHolidayInfo()
    {
        var obj = new SampleHolidayItem();
        var iface = ReflectionLookup.GetImplementedInterface(obj, typeof(ISampleHoliday).FullName!);
        Assert.Equal(typeof(ISampleHoliday), iface);
        Assert.True(ReflectionLookup.TryGetValue(ReflectionLookup.FindMember(iface, "IsAvailable"), obj, out var av));
        Assert.Equal(false, av);
        Assert.True(ReflectionLookup.TryGetValue(ReflectionLookup.FindMember(iface, "TargetHolidays"), obj, out var t));
        Assert.Equal(new[] { "Halloween" }, (string[])t!);

        // 接口被删除/改名：返回 null（调用方按非节日对象或 feature_unavailable 处理）
        Assert.Null(ReflectionLookup.GetImplementedInterface(obj, "CustomPlayerEffects.IHolidayEffect"));
        Assert.Null(ReflectionLookup.GetImplementedInterface(null, typeof(ISampleHoliday).FullName!));
    }

    [Fact]
    public void GatePipeline_MissingInterfaceMember_FallsBackToHolidayList()
    {
        // IsAvailable 读不到时 gameReportsAvailable=null，交给节日列表判定
        bool? reported = ReflectionLookup.TryGetValue(
            ReflectionLookup.FindMember(typeof(SampleHolidayItem), "IsAvailableRenamed"), new SampleHolidayItem(), out var v) && v is bool b
            ? b : null;
        Assert.Null(reported);
        HolidayGate.SetTestOverride(null);
        Assert.False(HolidayGate.Evaluate("item:X", "None", new[] { "Halloween" }, true, reported).Available);
        Assert.True(HolidayGate.Evaluate("item:X", "Halloween", new[] { "Halloween" }, true, reported).Available);
    }
}
