using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SLDataAPI.Map;

/// <summary>
/// 地图生成数据导出（seed 重建方案的前置数据采集）。
///
/// 背景：要从 seed 在 WebUI 侧纯算法重建设施布局，需要三样游戏内数据：
///   1. Atlas 图集（AtlasZoneGenerator.Atlases，Texture2D[]）—— 布局的"骨架"贴图
///   2. GlyphShapePair 表（MapAtlasInterpreter.PairDefinitions）—— 图集颜色 → 房间形状映射
///   3. 各区域候选房间权重（ZoneGenerator._spawnCandidates / CompatibleRooms）——
///      ChanceMultiplier / MinAmount / MaxAmount / AdjacentChanceMultiplier 等
/// 这些数据只能运行时导出（不在 DLL 代码里），本服务在服务器上跑一次即可。
///
/// 必须在 Unity 主线程调用（触碰 Texture2D / 场景对象）。
/// </summary>
public static class MapExportService
{
    private static readonly Type AzgType = typeof(global::MapGeneration.AtlasZoneGenerator);
    private static readonly Type ZoneGenType = typeof(global::MapGeneration.ZoneGenerator);
    private static readonly Type SpawnableType = typeof(global::MapGeneration.SpawnableRoom);
    private static readonly Type GlyphPairType = typeof(global::MapGeneration.GlyphShapePair);
    private static readonly Type RoomIdType = typeof(global::MapGeneration.RoomIdentifier);

    private static object? GetMember(Type t, string name, bool isField, BindingFlags flags, object? target)
    {
        try
        {
            return isField
                ? t.GetField(name, flags)?.GetValue(target)
                : t.GetProperty(name, flags)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    public static object Export()
    {
        var result = new Dictionary<string, object>
        {
            ["grid_scale"] = ReadGridScale(),
            ["seed"] = MapLayoutService.ReadSeed(),
        };

        // ================= 1. Atlas 图集（原始 RGBA base64） =================
        var atlases = new List<object>();
#pragma warning disable CS0618 // FindObjectsOfType 在新版 Unity 中过时但可用
        foreach (var azg in UnityEngine.Object.FindObjectsOfType(AzgType))
        {
            if (GetMember(AzgType, "Atlases", true,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, azg) is Texture2D[] texs)
            {
                foreach (var tex in texs)
                {
                    try
                    {
                        Color32[] px = tex.GetPixels32();
                        var bytes = new byte[px.Length * 4];
                        for (int i = 0; i < px.Length; i++)
                        {
                            bytes[i * 4] = px[i].r;
                            bytes[i * 4 + 1] = px[i].g;
                            bytes[i * 4 + 2] = px[i].b;
                            bytes[i * 4 + 3] = px[i].a;
                        }
                        atlases.Add(new
                        {
                            name = tex.name,
                            width = tex.width,
                            height = tex.height,
                            rgba = Convert.ToBase64String(bytes)
                        });
                    }
                    catch (Exception ex)
                    {
                        atlases.Add(new { name = tex?.name, error = ex.Message });
                    }
                }
            }
        }
        result["atlases"] = atlases;

        // ================= 2. GlyphShapePair 映射表 =================
        var pairs = new List<object>();
        object? interpreter = GetMember(typeof(global::MapGeneration.MapAtlasInterpreter),
            "Singleton", false, BindingFlags.Public | BindingFlags.Static, null);
        if (GetMember(typeof(global::MapGeneration.MapAtlasInterpreter), "PairDefinitions", false,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, interpreter) is IEnumerable pairDefs)
        {
            foreach (var p in pairDefs)
            {
                object? color = GetMember(GlyphPairType, "GlyphColor", true,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, p);
                object? offset = GetMember(GlyphPairType, "GlyphCenterOffset", true,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, p);
                object? shape = GetMember(GlyphPairType, "RoomShape", true,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, p);
                object? specific = GetMember(GlyphPairType, "SpecificRooms", true,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, p);
                object? rotations = GetMember(GlyphPairType, "RoomRotations", true,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, p);

                pairs.Add(new
                {
                    color = color is Color32 c ? $"{c.r},{c.g},{c.b},{c.a}" : null,
                    center_offset = offset is Vector2Int v ? $"{v.x},{v.y}" : null,
                    shape = shape?.ToString(),
                    specific_rooms = specific is Array sa ? sa.Cast<object>().Select(x => x?.ToString() ?? "").ToArray() : null,
                    rotations = rotations is Array ra ? ra.Cast<object>().Select(Convert.ToSingle).ToArray() : null
                });
            }
        }
        result["glyph_pairs"] = pairs;

        // ================= 3. 各区域候选房间权重 =================
        // 注意：_spawnCandidates / CompatibleRooms 声明在各区域子类上，
        // 必须用实例的实际类型反射（基类 GetField 查不到子类字段）。
        var zones = new Dictionary<string, List<object>>();
        foreach (var zg in UnityEngine.Object.FindObjectsOfType(ZoneGenType))
        {
            Type actualType = zg.GetType();

            object? zoneObj = actualType.GetField("TargetZone",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(zg);
            string zone = zoneObj?.ToString() ?? "Unknown";

            var candidates = new List<object>();

            // 只收集 CompatibleRooms（SpawnableRoom[]）—— ProcessInterpreted 的遍历源，
            // 顺序即游戏的候选顺序（MinAmount 直选依赖它）。
            // 注意：不合并 _spawnCandidates（运行时 List，含上一回合残留，会污染顺序）。
            string compatibleField = "CompatibleRooms";
            {
                if (actualType.GetField(compatibleField,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(zg) is not IEnumerable list)
                    continue;

                foreach (var s in list)
                {
                    object? room = GetMember(SpawnableType, "Room", false,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, s);
                    string name = GetMember(RoomIdType, "Name", true,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, room)?.ToString() ?? "";
                    string shape = GetMember(RoomIdType, "Shape", true,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, room)?.ToString() ?? "";

                    candidates.Add(new
                    {
                        name,
                        shape,
                        min = GetMember(SpawnableType, "MinAmount", true, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, s),
                        max = GetMember(SpawnableType, "MaxAmount", true, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, s),
                        chance = GetMember(SpawnableType, "ChanceMultiplier", true, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, s),
                        adj_chance = GetMember(SpawnableType, "AdjacentChanceMultiplier", true, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, s),
                        first_chance = GetMember(SpawnableType, "FirstChanceMultiplier", true, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, s),
                        special = GetMember(SpawnableType, "SpecialRoom", true, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, s)
                    });
                }
            }

            // 按房间名去重（_spawnCandidates 与 CompatibleRooms 可能重复）
            var seen = new HashSet<string>();
            var deduped = new List<object>();
            foreach (var c in candidates)
            {
                Type ct = c.GetType();
                string k = $"{ct.GetProperty("name")?.GetValue(c)}|{ct.GetProperty("shape")?.GetValue(c)}|{ct.GetProperty("chance")?.GetValue(c)}";
                if (seen.Add(k))
                    deduped.Add(c);
            }

            if (deduped.Count > 0 && !zones.ContainsKey(zone))
                zones[zone] = deduped;
        }
        result["zone_candidates"] = zones;

        // ================= 4. 区域生成顺序 + 区域高度 + EZ 偏移（场景序列化数据） =================
        var zoneOrder = new List<string>();
        var zoneHeights = new Dictionary<string, double>();
        object? ezOffsets = null;
        object? ezRotOffset = null;

        try
        {
            // _zoneGenerators 是场景序列化数组（SeedSynchronizer 私有字段）
            var seedSyncType = typeof(global::MapGeneration.SeedSynchronizer);
            var singletons = UnityEngine.Object.FindObjectsOfType(seedSyncType);
            if (singletons.Length > 0)
            {
                var zoneGenField = seedSyncType.GetField("_zoneGenerators",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (zoneGenField?.GetValue(singletons[0]) is Array gens)
                {
                    foreach (var g in gens)
                    {
                        if (g == null) continue;
                        Type gt = g.GetType();
                        string z = gt.GetField("TargetZone",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g)?.ToString() ?? "?";
                        zoneOrder.Add(z);

                        object? h = gt.GetField("_zoneHeight",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g);
                        if (h is float hf)
                            zoneHeights[z] = Math.Round(hf, 1);

                        // EZ 的硬偏移（场景序列化）
                        if (z == "Entrance")
                        {
                            object? posOff = gt.GetField("_hardPositionOffset",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g);
                            object? rotOff = gt.GetField("_hardRotationOffset",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(g);
                            if (posOff is Vector3 pv)
                                ezOffsets = new { x = Math.Round(pv.x, 2), y = Math.Round(pv.y, 2), z = Math.Round(pv.z, 2) };
                            if (rotOff is float rf)
                                ezRotOffset = Math.Round(rf, 1);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result["zone_order_error"] = ex.Message;
        }

        result["zone_order"] = zoneOrder;
        result["zone_heights"] = zoneHeights;
        if (ezOffsets != null) result["ez_hard_position_offset"] = ezOffsets;
        if (ezRotOffset != null) result["ez_hard_rotation_offset"] = ezRotOffset;

        // ================= 5. 当前回合实际布局（验证用） =================
        result["actual_layout"] = MapLayoutService.ExportRooms();

        return result;
    }

    private static double ReadGridScale()
    {
        try
        {
            var f = RoomIdType.GetField("GridScale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f?.GetValue(null) is Vector3 v && Math.Abs(v.x) > 0.01f)
                return v.x;
        }
        catch { }
        return 1.0;
    }
}
