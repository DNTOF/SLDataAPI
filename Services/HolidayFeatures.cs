// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CustomPlayerEffects;
using Interactables.Interobjects.DoorUtils;

namespace SLDataAPI.Services;

/// <summary>
/// 游戏 14.2.8（万圣节版本）新增接口的读取层：玩家当前生效效果、TemporaryBypass 开门权限、当前节日。
/// 兼容 14.2.7：缺少新成员时自动回落到旧版等价写法，不抛异常。
/// 所有方法都会触碰 Unity 对象，只能在主线程调用（MEC 协程 / MainThreadExecutor 内）。
/// </summary>
public static class HolidayFeatures
{
    // 0 = 未探测，1 = 有 EnabledEffects（14.2.8+），2 = 没有（14.2.7 回落）
    private static int _enabledEffectsMode;

    /// <summary>当前游戏版本串（如 "14.2.8"；带扩展校验时会附带构建描述）。</summary>
    public static string GameVersion()
    {
        try { return GameCore.Version.VersionString ?? ""; }
        catch { return ""; }
    }

    /// <summary>当前生效的节日：None / Christmas / Halloween / AprilFools。</summary>
    public static string ActiveHoliday()
    {
        try { return GameCore.Version.ActiveHoliday.ToString(); }
        catch { return "None"; }
    }

    /// <summary>
    /// 玩家当前生效的效果。14.2.8+ 直接用 PlayerEffectsController.EnabledEffects；
    /// 14.2.7 没有该属性，回落为 AllEffects 中 IsEnabled 的项（与新属性实现等价）。
    /// </summary>
    public static List<StatusEffectBase> EnabledEffects(ReferenceHub? hub)
    {
        var controller = hub?.playerEffectsController;
        if (controller == null)
            return new List<StatusEffectBase>();

        if (_enabledEffectsMode != 2)
        {
            try
            {
                var list = EnabledEffectsNative(controller);
                _enabledEffectsMode = 1;
                return list;
            }
            catch (MissingMemberException)
            {
                // 旧版游戏没有 EnabledEffects：记住结果，之后直接走回落
                _enabledEffectsMode = 2;
            }
        }

        return (controller.AllEffects ?? Array.Empty<StatusEffectBase>())
            .Where(e => e != null && e.IsEnabled)
            .ToList();
    }

    // 单独方法 + 禁止内联：旧版游戏缺成员时 JIT 只在调用这里时抛 MissingMemberException，便于捕获回落
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<StatusEffectBase> EnabledEffectsNative(PlayerEffectsController controller) =>
        controller.EnabledEffects.Where(e => e != null).ToList();

    /// <summary>
    /// 效果本身提供的开门权限（14.2.8 起 StatusEffectBase 可以实现 IDoorPermissionProvider，
    /// 目前只有 TemporaryBypass）。旧版或不提供权限的效果返回 None。
    /// </summary>
    public static DoorPermissionFlags EffectDoorPermissions(StatusEffectBase effect)
    {
        if (effect is not IDoorPermissionProvider provider)
            return DoorPermissionFlags.None;
        try { return provider.GetPermissions(null!); }
        catch { return DoorPermissionFlags.None; }
    }

    /// <summary>效果列表条目（/control/player/data 的 effects[*]）。</summary>
    public static object DescribeEffect(StatusEffectBase e)
    {
        var door = EffectDoorPermissions(e);
        float duration = 0f, timeLeft = 0f;
        try { duration = e.Duration; timeLeft = e.TimeLeft; } catch { }
        return new
        {
            name = e.GetType().Name,
            intensity = (int)e.Intensity,
            duration,                 // 0 = 无限期
            time_left = timeLeft,
            classification = SafeClassification(e),
            door_permissions = door == DoorPermissionFlags.None ? null : door.ToString(),
        };
    }

    /// <summary>
    /// TemporaryBypass 状态（/control/player/data 的 temporary_bypass）。
    /// level：none | scp_override（强度 1）| all（强度 ≥2）。
    /// grants_door_access：本服游戏版本里该效果是否真的参与开门判定（14.2.8+ 为 true，14.2.7 为 false）。
    /// </summary>
    public static object DescribeTemporaryBypass(IEnumerable<StatusEffectBase> enabled)
    {
        var tb = enabled.OfType<TemporaryBypass>().FirstOrDefault();
        bool providerSupported = typeof(IDoorPermissionProvider).IsAssignableFrom(typeof(TemporaryBypass));
        int intensity = tb == null ? 0 : tb.Intensity;
        string level = intensity <= 0 ? "none" : intensity == 1 ? "scp_override" : "all";
        float timeLeft = 0f;
        try { if (tb != null) timeLeft = tb.TimeLeft; } catch { }
        return new
        {
            active = tb != null,
            intensity,
            level,
            time_left = timeLeft,
            grants_door_access = providerSupported,
        };
    }

    private static string SafeClassification(StatusEffectBase e)
    {
        try { return e.Classification.ToString(); }
        catch { return ""; }
    }
}
