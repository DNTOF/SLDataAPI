// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Usables.Scp330;

namespace SLDataAPI.Services;

/// <summary>节日门控不通过时抛出，由控制端点转换为结构化错误（409 / 501），不会冒泡成 500。</summary>
public sealed class HolidayGateException : Exception
{
    public HolidayGateResult Result { get; }
    public HolidayGateException(HolidayGateResult result) : base(result.Message) { Result = result; }
}

/// <summary>
/// 节日专属物品 / 效果 / 糖果的运行时可用性探测（游戏侧，需主线程）。
/// 不在编译期引用任何节日专属类型：接口与字段全部按名称反射查找并缓存，
/// 未来游戏版本删除或改名时 fail-closed 返回 feature_unavailable；
/// 节日开关完全以当前游戏构建（GameCore.Version.ActiveHoliday）和游戏自身的 IsAvailable 为准。
/// </summary>
public static class HolidayRuntime
{
    private const string HolidayItemInterface = "InventorySystem.Items.IHolidayItem";
    private const string HolidayEffectInterface = "CustomPlayerEffects.IHolidayEffect";
    private const string CandiesTypeName = "InventorySystem.Items.Usables.Scp330.Scp330Candies";

    /// <summary>对象的节日属性。IsHoliday=false 表示与节日无关（始终可用）。</summary>
    public readonly struct HolidayInfo
    {
        public readonly bool IsHoliday;
        public readonly bool? Available;
        public readonly string[] Targets;
        public HolidayInfo(bool isHoliday, bool? available, string[] targets)
        {
            IsHoliday = isHoliday;
            Available = available;
            Targets = targets ?? Array.Empty<string>();
        }
    }

    /// <summary>按接口名读取 IsAvailable / TargetHolidays（读不到 IsAvailable 时交给 HolidayGate 按节日列表判定）。</summary>
    public static HolidayInfo ReadHolidayInfo(object? obj, string interfaceFullName)
    {
        var iface = ReflectionLookup.GetImplementedInterface(obj, interfaceFullName);
        if (iface == null)
            return new HolidayInfo(false, null, Array.Empty<string>());

        bool? available = null;
        if (ReflectionLookup.TryGetValue(ReflectionLookup.FindMember(iface, "IsAvailable"), obj, out var av) && av is bool b)
            available = b;

        var targets = new List<string>();
        if (ReflectionLookup.TryGetValue(ReflectionLookup.FindMember(iface, "TargetHolidays"), obj, out var tv) && tv is IEnumerable en)
        {
            foreach (var t in en)
                if (t != null) targets.Add(t.ToString());
        }
        return new HolidayInfo(true, available, targets.ToArray());
    }

    /// <summary>物品能否在当前游戏构建中发放（节日物品未开启 → 409；本版本不存在该物品 → 501）。</summary>
    public static HolidayGateResult CheckItem(ItemType type)
    {
        string feature = "item:" + type;
        string active = HolidayFeatures.ActiveHoliday();
        try
        {
            if (InventoryItemLoader.AvailableItems.TryGetValue(type, out var template) && template != null)
            {
                var info = ReadHolidayInfo(template, HolidayItemInterface);
                return info.IsHoliday
                    ? HolidayGate.Evaluate(feature, active, info.Targets, true, info.Available)
                    : HolidayGate.Evaluate(feature, active, null);
            }

            // 没加载：游戏只会跳过 IsAvailable=false 的节日物品。到全部预制体里找出它要求的节日以便提示
            foreach (var it in UnityEngine.Resources.FindObjectsOfTypeAll<ItemBase>())
            {
                if (it == null || it.ItemTypeId != type) continue;
                var info = ReadHolidayInfo(it, HolidayItemInterface);
                if (info.IsHoliday)
                    return HolidayGate.Evaluate(feature, active, info.Targets, true, false);
                break;
            }
            return HolidayGate.Evaluate(feature, active, null, featurePresent: false);
        }
        catch
        {
            return HolidayGate.Evaluate(feature, active, null, featurePresent: false);
        }
    }

    /// <summary>效果能否开启（IHolidayEffect.IsAvailable=false → 409；非节日效果始终可用）。</summary>
    public static HolidayGateResult CheckEffect(object effect)
    {
        string feature = "effect:" + (effect?.GetType().Name ?? "?");
        string active = HolidayFeatures.ActiveHoliday();
        try
        {
            var info = ReadHolidayInfo(effect, HolidayEffectInterface);
            return info.IsHoliday
                ? HolidayGate.Evaluate(feature, active, info.Targets, true, info.Available)
                : HolidayGate.Evaluate(feature, active, null);
        }
        catch
        {
            return HolidayGate.Evaluate(feature, active, null, featurePresent: false);
        }
    }

    /// <summary>
    /// 指定糖果在当前糖果池中是否可用：游戏的 Scp330Candies.CandiesById 已按当前节日切换（万圣节 = 13 种闹鬼糖果，否则 8 种普通糖果），
    /// 不在池中但在 HauntedCandies 中 → 409（需要 Halloween）；两边都没有 → 501。
    /// </summary>
    public static HolidayGateResult CheckCandy(CandyKindID kind)
    {
        string feature = "candy:" + kind;
        string active = HolidayFeatures.ActiveHoliday();
        try
        {
            if (kind == CandyKindID.None)
                return HolidayGate.Evaluate(feature, active, null, featurePresent: false);
            if (Scp330Candies.CandiesById.ContainsKey(kind))
            {
                // 普通糖果不受节日限制；闹鬼专属（不在 NormalCandies）标注需要 Halloween，便于测试覆盖收紧
                bool normal = CandyInField("NormalCandies", kind);
                return HolidayGate.Evaluate(feature, active, normal ? null : new[] { "Halloween" }, true, true);
            }

            if (CandyInField("HauntedCandies", kind))
                return HolidayGate.Evaluate(feature, active, new[] { "Halloween" }, true, false);
            return HolidayGate.Evaluate(feature, active, null, featurePresent: false);
        }
        catch
        {
            return HolidayGate.Evaluate(feature, active, null, featurePresent: false);
        }
    }

    /// <summary>当前糖果池：haunted（万圣节）| normal；探测失败返回 unknown。</summary>
    public static string CandyPool()
    {
        try
        {
            var current = Scp330Candies.Candies;
            if (ReflectionLookup.TryGetValue(ReflectionLookup.FindMember(ReflectionLookup.FindType(CandiesTypeName), "HauntedCandies"), null, out var h)
                && ReferenceEquals(h, current))
                return "haunted";
            return "normal";
        }
        catch { return "unknown"; }
    }

    /// <summary>当前池中所有糖果名（给 400 / 文档提示用）。</summary>
    public static List<string> CandyPoolKinds()
    {
        try { return Scp330Candies.CandiesById.Keys.Select(k => k.ToString()).ToList(); }
        catch { return new List<string>(); }
    }

    private static bool CandyInField(string fieldName, CandyKindID kind)
    {
        var member = ReflectionLookup.FindMember(ReflectionLookup.FindType(CandiesTypeName), fieldName);
        if (!ReflectionLookup.TryGetValue(member, null, out var v) || v is not IEnumerable en)
            return false;
        foreach (var c in en)
        {
            if (c == null) continue;
            if (ReflectionLookup.TryGetValue(ReflectionLookup.FindMember(c.GetType(), "Kind"), c, out var k) &&
                k is CandyKindID ck && ck == kind)
                return true;
        }
        return false;
    }

    /// <summary>节日状态快照（/control/map/status 的 holiday 段）。</summary>
    public static object Snapshot() => new
    {
        active_holiday = HolidayFeatures.ActiveHoliday(),
        game_version = HolidayFeatures.GameVersion(),
        candy_pool = CandyPool(),
        candy_kinds = CandyPoolKinds(),
        test_override = HolidayGate.TestOverride,
    };
}
