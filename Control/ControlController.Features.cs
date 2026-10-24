// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections.Generic;
using System.Linq;
using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Usables.Scp330;
using Mirror;
using NetworkManagerUtils.Dummies;
using PlayerRoles;
using SLDataAPI.Data;
using SLDataAPI.Services;
using UnityEngine;
using Player = LabApi.Features.Wrappers.Player;
using Round = LabApi.Features.Wrappers.Round;
using Warhead = LabApi.Features.Wrappers.Warhead;
using Generator = LabApi.Features.Wrappers.Generator;
using Decontamination = LabApi.Features.Wrappers.Decontamination;
using Scp914Wrapper = LabApi.Features.Wrappers.Scp914;

namespace SLDataAPI.Control;

/// <summary>
/// 新增控制端点：物品栏、糖果（节日门控）、设施状态快照、玩家数据扩展字段。
/// 节日相关逻辑统一走 HolidayRuntime + HolidayGate：不可用时返回结构化 409 / 501，不抛 500。
/// </summary>
public static partial class ControlController
{
    // ------------------------------------------------------------------
    // /control/player/inventory —— list（只读）| add | remove | clear
    // ------------------------------------------------------------------
    private static (int, string) PlayerInventoryAction(string body)
    {
        var req = Parse<InventoryRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.target))
            return (400, Json(false, "缺少 target 字段"));
        if (!FeatureInputRules.IsInventoryAction(req.action, out string action))
            return (400, Json(false, $"未知 action: {req.action}（支持 list/add/remove/clear）"));

        ItemType itemType = ItemType.None;
        int amount = 1;
        if (action is "add" or "remove")
        {
            if (!TryParseItemType(req.item, out itemType))
                return (400, Json(false, $"无效物品名: {req.item}", new { code = "invalid_item" }));
            if (IsAmmoType(itemType))
                return (400, Json(false, "弹药不是物品栏物品，暂不支持通过本端点增减", new { code = "ammo_not_supported" }));
            int max = action == "add" ? FeatureInputRules.MaxItemAdd : FeatureInputRules.MaxItemRemove;
            if (!FeatureInputRules.TryValidateAmount(req.amount, max, out amount))
                return (400, Json(false, $"amount 超出上限 {max}", new { code = "amount_exceeds_limit", max }));
        }

        var (status, json) = MainThreadExecutor.RunOnMainThread(() =>
        {
            var p = FindPlayer(req.target) ?? throw new InvalidOperationException($"找不到玩家: {req.target}");
            switch (action)
            {
                case "list":
                    return (200, Json(true, "ok", DescribeInventory(p)));

                case "add":
                {
                    var gate = HolidayRuntime.CheckItem(itemType);
                    if (!gate.Available)
                        return (gate.Status, Json(false, gate.Message, gate.ToData()));

                    var added = new List<object>();
                    for (int i = 0; i < amount; i++)
                    {
                        if (p.IsInventoryFull) break;
                        var it = p.AddItem(itemType, ItemAddReason.AdminCommand);
                        if (it == null) break;
                        added.Add(new { type = it.Type.ToString(), serial = it.Serial });
                    }
                    if (added.Count == 0)
                    {
                        bool full = p.IsInventoryFull;
                        return (409, Json(false, full ? "物品栏已满" : "物品发放失败（游戏拒绝添加）", new
                        {
                            code = full ? "inventory_full" : "add_failed",
                            inventory = DescribeInventory(p),
                        }));
                    }
                    return (200, Json(true, $"已发放 {added.Count}/{amount} 个 {itemType}", new
                    {
                        requested = amount,
                        added,
                        inventory = DescribeInventory(p),
                    }));
                }

                case "remove":
                {
                    int before = p.Items.Count(x => x != null && x.Type == itemType);
                    if (before > 0) p.RemoveItem(itemType, amount);
                    int after = p.Items.Count(x => x != null && x.Type == itemType);
                    return (200, Json(true, $"已移除 {before - after} 个 {itemType}", new
                    {
                        requested = amount,
                        removed = before - after,
                        inventory = DescribeInventory(p),
                    }));
                }

                case "clear":
                {
                    int items = p.Items.Count();
                    int ammoKinds = p.Ammo.Count(kv => kv.Value > 0);
                    p.ClearInventory(clearAmmo: req.clear_ammo, clearItems: true);
                    return (200, Json(true, "物品栏已清空", new
                    {
                        cleared_items = items,
                        cleared_ammo_types = req.clear_ammo ? ammoKinds : 0,
                        inventory = DescribeInventory(p),
                    }));
                }

                default:
                    return (400, Json(false, $"未知 action: {action}"));
            }
        }, out var err);
        return err != null ? (400, Json(false, err.Message)) : (status, json);
    }

    /// <summary>/control/player/data 用：不按 IP 匹配（PlayerId → UserId → 昵称精确 → LabAPI 模糊昵称）。</summary>
    private static Player? FindPlayerWithoutIp(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        return PlayerLookup.Find(target, Player.List,
                   p => p.PlayerId, p => p.UserId, _ => null, p => p.Nickname, allowIp: false)
               ?? Player.GetByNickname(target, requireFullMatch: true)
               ?? Player.GetByNickname(target);
    }

    private static bool TryParseItemType(string? name, out ItemType type)
    {
        type = ItemType.None;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Enum.TryParse(name!.Trim(), true, out type) &&
               type != ItemType.None &&
               Enum.IsDefined(typeof(ItemType), type);
    }

    private static bool IsAmmoType(ItemType t) => t.ToString().StartsWith("Ammo", StringComparison.Ordinal);

    private static object DescribeInventory(Player p)
    {
        var items = SafeList(() => p.Items.Where(x => x != null)
            .Select(x => (object)new { type = x.Type.ToString(), serial = x.Serial }).ToList());
        var ammo = SafeValue(() => p.Ammo.Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key.ToString(), kv => (int)kv.Value));
        return new
        {
            nickname = p.Nickname,
            player_id = p.PlayerId,
            count = items.Count,
            capacity = 8,
            items,
            ammo,
        };
    }

    // ------------------------------------------------------------------
    // /control/player/candy —— 给 SCP-330 糖果（指定 / 随机），节日糖果运行时门控
    // ------------------------------------------------------------------
    private static (int, string) PlayerCandyAction(string body)
    {
        var req = Parse<CandyRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.target))
            return (400, Json(false, "缺少 target 字段"));
        if (!FeatureInputRules.TryValidateAmount(req.amount, FeatureInputRules.MaxCandyGive, out int amount))
            return (400, Json(false, $"amount 超出上限 {FeatureInputRules.MaxCandyGive}",
                new { code = "amount_exceeds_limit", max = FeatureInputRules.MaxCandyGive }));

        string candyName = (req.candy ?? "").Trim();
        bool random = candyName.Length == 0 || candyName.Equals("random", StringComparison.OrdinalIgnoreCase);
        CandyKindID kind = CandyKindID.None;
        if (!random && (!Enum.TryParse(candyName, true, out kind) || kind == CandyKindID.None ||
                        !Enum.IsDefined(typeof(CandyKindID), kind)))
        {
            return (400, Json(false, $"无效糖果名: {req.candy}", new
            {
                code = "invalid_candy",
                valid = Enum.GetNames(typeof(CandyKindID)).Where(n => n != "None").ToArray(),
            }));
        }

        var (status, json) = MainThreadExecutor.RunOnMainThread(() =>
        {
            var p = FindPlayer(req.target) ?? throw new InvalidOperationException($"找不到玩家: {req.target}");

            if (!random)
            {
                var gate = HolidayRuntime.CheckCandy(kind);
                if (!gate.Available)
                {
                    var data = gate.ToData();
                    data["available_candies"] = HolidayRuntime.CandyPoolKinds().ToArray();
                    return (gate.Status, Json(false, gate.Message, data));
                }
            }

            var given = new List<string>();
            for (int i = 0; i < amount; i++)
            {
                CandyKindID k = random ? Scp330Candies.GetRandom() : kind;
                int before = CandyCount(p);
                p.ReferenceHub.GrantCandy(k, ItemAddReason.AdminCommand);
                if (CandyCount(p) <= before) break; // 背包满 / 糖果袋满
                given.Add(k.ToString());
            }

            var result = new
            {
                requested = amount,
                given,
                bag = SafeList(() => Scp330Bag.TryGetBag(p.ReferenceHub, out var bag) && bag != null
                    ? bag.Candies.Select(c => (object)c.ToString()).ToList()
                    : new List<object>()),
                candy_pool = HolidayRuntime.CandyPool(),
                active_holiday = HolidayFeatures.ActiveHoliday(),
            };
            if (given.Count == 0)
                return (409, Json(false, "糖果发放失败（物品栏或糖果袋已满）", new
                {
                    code = "candy_bag_full",
                    result.bag,
                    result.candy_pool,
                    result.active_holiday,
                }));
            return (200, Json(true, $"已发放 {given.Count}/{amount} 颗糖果", result));
        }, out var err);
        return err != null ? (400, Json(false, err.Message)) : (status, json);
    }

    private static int CandyCount(Player p)
    {
        try
        {
            return Scp330Bag.TryGetBag(p.ReferenceHub, out var bag) && bag != null ? bag.Candies.Count : 0;
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------
    // /control/map/status —— 设施状态快照（只读）
    // ------------------------------------------------------------------
    private static (int, string) MapStatusAction()
    {
        var (status, json) = MainThreadExecutor.RunOnMainThread(() =>
        {
            var data = new Dictionary<string, object?>
            {
                ["round"] = SafeValue<object>(() => new
                {
                    started = Round.IsRoundStarted,
                    ended = Round.IsRoundEnded,
                    duration_seconds = (int)Round.Duration.TotalSeconds,
                    locked = Round.IsLocked,
                    lobby_locked = Round.IsLobbyLocked,
                    players = Player.ReadyList.Count(),
                    total_deaths = Round.TotalDeaths,
                    killed_by_scps = Round.KilledBySCPs,
                    escaped_class_d = Round.EscapedClassD,
                    escaped_scientists = Round.EscapedScientists,
                    surviving_scps = Round.SurvivingSCPs,
                    changed_into_zombies = Round.ChangedIntoZombies,
                }),
                ["decontamination"] = SafeValue<object>(() => new
                {
                    status = Decontamination.Status.ToString(),
                    is_decontaminating = Decontamination.IsDecontaminating,
                    server_time = Math.Round(Decontamination.ServerTime, 1),
                }),
                ["generators"] = SafeValue<object>(() =>
                {
                    var list = Generator.List.Where(g => g != null).Select(g => new
                    {
                        room = g.Room?.Name.ToString() ?? "",
                        engaged = g.Engaged,
                        activating = g.Activating,
                        remaining_time = (int)g.RemainingTime,
                        is_open = g.IsOpen,
                        is_unlocked = g.IsUnlocked,
                    }).ToList();
                    return new { total = list.Count, engaged = list.Count(x => x.engaged), list };
                }),
                ["warhead"] = SafeValue<object>(() => new
                {
                    exists = Warhead.Exists,
                    lever = Warhead.LeverStatus,
                    locked = Warhead.IsLocked,
                    authorized = Warhead.IsAuthorized,
                    in_progress = Warhead.IsDetonationInProgress,
                    detonated = Warhead.IsDetonated,
                    time_until_detonation = Warhead.IsDetonationInProgress ? Math.Round(Warhead.DetonationTime, 1) : (double?)null,
                }),
                ["scp914"] = SafeValue<object>(() => new
                {
                    knob = Scp914Wrapper.KnobSetting.ToString(),
                    is_upgrading = Scp914Wrapper.IsUpgrading,
                }),
                ["holiday"] = SafeValue(HolidayRuntime.Snapshot),
            };
            return (200, Json(true, "ok", data));
        }, out var err);
        return err != null ? (400, Json(false, err.Message)) : (status, json);
    }


    // ------------------------------------------------------------------
    // /control/dummies —— spawn | list | remove（硬上限 MaxDummies；缺 DummyUtils → 501）
    // ------------------------------------------------------------------
    private static (int, string) DummiesAction(string body)
    {
        var req = Parse<DummiesRequest>(body);
        if (req == null || !FeatureInputRules.IsDummyAction(req.action, out string action))
            return (400, Json(false, "缺少或未知 action（支持 spawn/list/remove）",
                new { code = "invalid_action", valid = FeatureInputRules.DummyActions }));

        // 旧游戏版本可能没有 DummyUtils：先按类型名探测，避免 JIT 到引用缺失类型的方法时抛 TypeLoadException
        if (ReflectionLookup.FindType("NetworkManagerUtils.Dummies.DummyUtils") == null)
        {
            return (501, Json(false, "当前游戏版本不提供假人 API（DummyUtils 不存在）", new
            {
                code = "feature_unavailable",
                feature = "dummies",
            }));
        }

        RoleTypeId? spawnRole = null;
        if (action == "spawn" && !string.IsNullOrWhiteSpace(req.role))
        {
            if (!Enum.TryParse(req.role.Trim(), true, out RoleTypeId roleId) ||
                !Enum.IsDefined(typeof(RoleTypeId), roleId))
            {
                return (400, Json(false, $"无效角色名: {req.role}", new { code = "invalid_role" }));
            }
            spawnRole = roleId;
        }

        if (action == "remove" && string.IsNullOrWhiteSpace(req.target))
            return (400, Json(false, "remove 需要 target（假人 PlayerId / 昵称，或 all）", new { code = "missing_target" }));

        string nickname = FeatureInputRules.NormalizeDummyNickname(req.nickname);
        string target = (req.target ?? "").Trim();

        var (status, json) = MainThreadExecutor.RunOnMainThread(() =>
        {
            switch (action)
            {
                case "list":
                    return (200, Json(true, "ok", DescribeDummies()));

                case "spawn":
                {
                    int count = Player.DummyList.Count();
                    if (count >= FeatureInputRules.MaxDummies)
                    {
                        return (409, Json(false, $"假人数量已达上限 {FeatureInputRules.MaxDummies}", new
                        {
                            code = "dummy_limit",
                            max = FeatureInputRules.MaxDummies,
                            count,
                            dummies = DescribeDummies(),
                        }));
                    }

                    ReferenceHub? hub;
                    try { hub = SpawnDummySafe(nickname); }
                    catch (TypeLoadException ex)
                    {
                        return (501, Json(false, $"当前游戏版本不提供假人 API: {ex.Message}", new
                        {
                            code = "feature_unavailable",
                            feature = "dummies",
                        }));
                    }
                    catch (MissingMethodException ex)
                    {
                        return (501, Json(false, $"当前游戏版本不提供假人 API: {ex.Message}", new
                        {
                            code = "feature_unavailable",
                            feature = "dummies",
                        }));
                    }

                    if (hub == null)
                        return (500, Json(false, "假人生成失败（SpawnDummy 返回 null）", new { code = "spawn_failed" }));

                    var player = Player.Get(hub);
                    if (player == null)
                        return (500, Json(false, "假人已生成但无法包装为 Player", new { code = "spawn_failed" }));

                    if (spawnRole.HasValue)
                        player.SetRole(spawnRole.Value);

                    return (200, Json(true, $"已生成假人 {player.Nickname}", new
                    {
                        dummy = DescribeDummy(player),
                        count = Player.DummyList.Count(),
                        max = FeatureInputRules.MaxDummies,
                    }));
                }

                case "remove":
                {
                    if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        int before = Player.DummyList.Count();
                        try { DestroyAllDummiesSafe(); }
                        catch (TypeLoadException ex)
                        {
                            return (501, Json(false, $"当前游戏版本不提供假人 API: {ex.Message}", new
                            {
                                code = "feature_unavailable",
                                feature = "dummies",
                            }));
                        }
                        catch (MissingMethodException ex)
                        {
                            return (501, Json(false, $"当前游戏版本不提供假人 API: {ex.Message}", new
                            {
                                code = "feature_unavailable",
                                feature = "dummies",
                            }));
                        }
                        return (200, Json(true, $"已清除全部假人（{before}）", new
                        {
                            removed = before,
                            count = Player.DummyList.Count(),
                            max = FeatureInputRules.MaxDummies,
                        }));
                    }

                    var dummy = FindDummy(target);
                    if (dummy == null)
                        return (404, Json(false, $"找不到假人: {target}", new { code = "dummy_not_found" }));

                    var snapshot = DescribeDummy(dummy);
                    string goneName = dummy.Nickname;
                    try
                    {
                        if (dummy.GameObject != null)
                            NetworkServer.Destroy(dummy.GameObject);
                    }
                    catch (Exception ex)
                    {
                        return (500, Json(false, $"销毁假人失败: {ex.Message}", new { code = "destroy_failed" }));
                    }

                    return (200, Json(true, $"已移除假人 {goneName}", new
                    {
                        removed = snapshot,
                        count = Player.DummyList.Count(),
                        max = FeatureInputRules.MaxDummies,
                    }));
                }

                default:
                    return (400, Json(false, $"未知 action: {action}"));
            }
        }, out var err);
        return err != null ? (400, Json(false, err.Message)) : (status, json);
    }

    /// <summary>隔离对 DummyUtils 的引用：仅在确认类型存在后调用，避免整段方法 JIT 时 TypeLoadException。</summary>
    private static ReferenceHub? SpawnDummySafe(string nickname) => DummyUtils.SpawnDummy(nickname);

    private static void DestroyAllDummiesSafe() => DummyUtils.DestroyAllDummies();

    private static Player? FindDummy(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var list = Player.DummyList.ToList();
        return PlayerLookup.Find(target, list,
                   p => p.PlayerId, p => p.UserId, _ => null, p => p.Nickname, allowIp: false)
               ?? list.FirstOrDefault(p => string.Equals(p.Nickname, target, StringComparison.OrdinalIgnoreCase));
    }

    private static object DescribeDummy(Player p) => new
    {
        nickname = p.Nickname,
        player_id = p.PlayerId,
        userid = p.UserId,
        role = p.Role.ToString(),
        is_dummy = true,
    };

    private static object DescribeDummies()
    {
        var items = Player.DummyList.Select(DescribeDummy).ToList();
        return new
        {
            count = items.Count,
            max = FeatureInputRules.MaxDummies,
            dummies = items,
        };
    }

    // ------------------------------------------------------------------
    // /control/player/data 扩展字段（不含 IP）
    // ------------------------------------------------------------------
    private static Dictionary<string, object?> PlayerDataExtras(Player p)
    {
        return new Dictionary<string, object?>
        {
            ["max_health"] = SafeValue<object>(() => p.MaxHealth),
            ["artificial_health"] = SafeValue<object>(() => p.ArtificialHealth),
            ["max_artificial_health"] = SafeValue<object>(() => p.MaxArtificialHealth),
            ["hume_shield"] = SafeValue<object>(() => p.HumeShield),
            ["max_hume_shield"] = SafeValue<object>(() => p.MaxHumeShield),
            ["stamina"] = SafeValue<object>(() => p.StaminaRemaining),
            ["items"] = SafeList(() => p.Items.Where(x => x != null)
                .Select(x => (object)new { type = x.Type.ToString(), serial = x.Serial }).ToList()),
            ["ammo"] = SafeValue<object>(() => p.Ammo.Where(kv => kv.Value > 0)
                .ToDictionary(kv => kv.Key.ToString(), kv => (int)kv.Value)),
            ["group"] = SafeValue<object>(() => p.GroupName),
            ["godmode"] = SafeValue<object>(() => p.IsGodModeEnabled),
            ["noclip"] = SafeValue<object>(() => p.IsNoclipEnabled),
            ["bypass"] = SafeValue<object>(() => p.IsBypassEnabled),
            ["overwatch"] = SafeValue<object>(() => p.IsOverwatchEnabled),
            ["spectating"] = SafeValue<object>(() =>
            {
                var s = p.CurrentlySpectating;
                return s == null ? null! : new { nickname = s.Nickname, userid = s.UserId, player_id = s.PlayerId };
            }),
            ["zone"] = SafeValue<object>(() => p.Zone.ToString()),
            ["is_speaking"] = SafeValue<object>(() => p.IsSpeaking),
            ["is_muted"] = SafeValue<object>(() => p.IsMuted),
            ["is_intercom_muted"] = SafeValue<object>(() => p.IsIntercomMuted),
            ["voice_channel"] = SafeValue<object>(() => p.VoiceChannel.ToString()),
            ["is_dummy"] = SafeValue<object>(() => p.IsDummy),
        };
    }

    // ------------------------------------------------------------------
    // 节日效果门控：/control/player/effects 开启 IHolidayEffect（如 MarshmallowEffect）前检查
    // ------------------------------------------------------------------
    private static void EnsureEffectAvailable(CustomPlayerEffects.StatusEffectBase effect, byte intensity)
    {
        if (intensity == 0) return; // 关闭效果永远允许
        var gate = HolidayRuntime.CheckEffect(effect);
        if (!gate.Available)
            throw new HolidayGateException(gate);
    }

    private static T? SafeValue<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch { return null; }
    }

    private static List<object> SafeList(Func<List<object>> f)
    {
        try { return f() ?? new List<object>(); }
        catch { return new List<object>(); }
    }
}
