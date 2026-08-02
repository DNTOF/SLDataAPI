using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// 地图布局采集与缓存。
///
/// 说明（重要）：SCP:SL 的 LCZ/HCZ 每回合布局随机，由回合种子决定。
/// 本项目**不逆向游戏的种子生成算法**（房间权重/连接图/随机数流极其复杂，
/// 逐帧复刻不可维护），而是从游戏运行时直接读取已摆放好的房间
/// （MapGeneration.RoomIdentifier.AllRoomIdentifiers —— 游戏自己按种子摆好的结果），
/// 效果与"从种子还原"完全一致：每回合开始事件触发重新采集，布局自动更新。
/// WebUI 侧用方块/拐角等固定素材按房间形状拼出 2D 地图。
///
/// 采集必须在 Unity 主线程（触碰 Transform/Bounds）；读取缓存可在任意线程。
/// </summary>
public static class MapLayoutService
{
    private static readonly object Lock = new object();
    private static object? _cachedLayout;

    // WorldspaceBounds / Name / Zone / Shape 在游戏程序集里是 internal 成员，
    // 编译期不可直接访问，用反射读取（运行时可用，字段/属性名随游戏版本兜底）。
    private static readonly Type RoomIdType = typeof(global::MapGeneration.RoomIdentifier);
    private static readonly FieldInfo? NameField =
        RoomIdType.GetField("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? ZoneField =
        RoomIdType.GetField("Zone", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? ShapeField =
        RoomIdType.GetField("Shape", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? GridScaleField =
        RoomIdType.GetField("GridScale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly PropertyInfo? BoundsProp =
        RoomIdType.GetProperty("WorldspaceBounds", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly PropertyInfo? MainCoordsProp =
        RoomIdType.GetProperty("MainCoords", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? MainCoordsBackingField =
        RoomIdType.GetField("<MainCoords>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>回合开始事件调用（主线程）：重新采集本回合布局。</summary>
    public static void CaptureLayout()
    {
        lock (Lock)
        {
            try
            {
                _cachedLayout = BuildLayout();
            }
            catch (Exception ex)
            {
                Exiled.API.Features.Log.Debug($"[SLDataAPI] 地图布局采集失败: {ex.Message}");
                _cachedLayout = null;
            }
        }
    }

    /// <summary>清空缓存（回合结束/等待玩家时调用，避免显示上一回合地图）。</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            _cachedLayout = null;
        }
    }

    /// <summary>返回缓存布局；无缓存（回合未开始）时返回 null。</summary>
    public static object? GetLayout()
    {
        lock (Lock)
        {
            return _cachedLayout;
        }
    }

    private static object BuildLayout()
    {
        var rooms = new List<object>();
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        // 游戏网格单元尺寸（RoomIdentifier.GridScale，静态字段）
        float gridScale = 1f;
        try
        {
            if (GridScaleField?.GetValue(null) is Vector3 gv && Math.Abs(gv.x) > 0.01f)
                gridScale = gv.x;
        }
        catch { /* 反射失败用 1 兜底 */ }

        foreach (var id in global::MapGeneration.RoomIdentifier.AllRoomIdentifiers)
        {
            // WorldspaceBounds（internal，反射读）：中心 + 尺寸
            Bounds b = default;
            bool hasBounds = false;
            if (BoundsProp != null)
            {
                try
                {
                    b = (Bounds)BoundsProp.GetValue(id);
                    hasBounds = true;
                }
                catch { /* 反射失败回退到 transform */ }
            }

            float cx = hasBounds ? b.center.x : id.transform.position.x;
            float cz = hasBounds ? b.center.z : id.transform.position.z;

            // 网格占用（尺寸 ÷ 网格单元取整，最少 1 格）：
            // 走廊 Straight 占 2×1，大房间 2×2 / 3×3，拐角 Curve 2×1 ——
            // 前端按占用格数绘制矩形，顶点对齐网格线，无需旋转，不重叠
            int gw = hasBounds ? Math.Max(1, (int)Math.Round(b.size.x / gridScale)) : 1;
            int gd = hasBounds ? Math.Max(1, (int)Math.Round(b.size.z / gridScale)) : 1;

            // 网格对齐的矩形（世界坐标）：中心反推到网格线
            float left = (float)Math.Round((cx - (gw * gridScale) / 2f) / gridScale) * gridScale;
            float top = (float)Math.Round((cz - (gd * gridScale) / 2f) / gridScale) * gridScale;
            float right = left + gw * gridScale;
            float bottom = top + gd * gridScale;

            if (left < minX) minX = left;
            if (right > maxX) maxX = right;
            if (top < minZ) minZ = top;
            if (bottom > maxZ) maxZ = bottom;

            // 房间名：RoomName 枚举很多对象是 Unnamed（装饰/道具），回退到游戏对象名
            string roomName = NameField?.GetValue(id)?.ToString() ?? "";
            if (string.IsNullOrEmpty(roomName) || roomName == "Unnamed")
                roomName = id.gameObject.name;

            rooms.Add(new
            {
                name = roomName,
                zone = ZoneField?.GetValue(id)?.ToString() ?? "",
                shape = ShapeField?.GetValue(id)?.ToString() ?? "",
                // 网格对齐矩形（世界坐标）：前端直接画，顶点落在网格线上
                ax = Math.Round(left, 1),
                az = Math.Round(top, 1),
                aw = Math.Round(gw * gridScale, 1),
                ad = Math.Round(gd * gridScale, 1),
                grid_scale = Math.Round(gridScale, 3),
                // 原始中心/尺寸（备用）
                x = Math.Round(cx, 1),
                z = Math.Round(cz, 1),
                y = Math.Round(hasBounds ? b.center.y : id.transform.position.y, 1),
                w = hasBounds ? Math.Round(b.size.x, 1) : 4.0,
                d = hasBounds ? Math.Round(b.size.z, 1) : 4.0
            });
        }

        return new
        {
            // 回合种子：WebUI 用它做布局快照缓存（同一 seed 永远对应同一布局，
            // 之后服务端只发 seed，WebUI 从缓存渲染，无需重复传输房间数据）
            seed = ReadSeed(),
            ready = true,
            count = rooms.Count,
            bounds = new
            {
                min_x = minX == float.MaxValue ? 0f : Math.Round(minX, 1),
                max_x = maxX == float.MinValue ? 0f : Math.Round(maxX, 1),
                min_z = minZ == float.MaxValue ? 0f : Math.Round(minZ, 1),
                max_z = maxZ == float.MinValue ? 0f : Math.Round(maxZ, 1)
            },
            rooms
        };
    }

    /// <summary>读取当前回合种子（MapGeneration.SeedSynchronizer.Seed，静态属性）。</summary>
    public static int ReadSeed()
    {
        try
        {
            var t = typeof(global::MapGeneration.SeedSynchronizer);
            var p = t.GetProperty("Seed", BindingFlags.Public | BindingFlags.Static);
            return p?.GetValue(null) is int s ? s : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 导出当前回合房间明细（含网格坐标 MainCoords 与朝向），
    /// 供 seed 重建算法验证用（seed → 生成布局 vs 实际布局逐房间比对）。
    /// 必须在主线程调用。
    /// </summary>
    public static List<object> ExportRooms()
    {
        var rooms = new List<object>();
        foreach (var id in global::MapGeneration.RoomIdentifier.AllRoomIdentifiers)
        {
            string name = NameField?.GetValue(id)?.ToString() ?? "";
            if (string.IsNullOrEmpty(name) || name == "Unnamed")
                name = id.gameObject.name;

            // 网格坐标（Vector3Int，internal 反射读：属性优先，backing field 兜底）
            int gx = 0, gz = 0;
            try
            {
                object? mc = MainCoordsProp?.GetValue(id);
                if (mc == null && MainCoordsBackingField != null)
                    mc = MainCoordsBackingField.GetValue(id);
                if (mc != null)
                {
                    gx = (int)(mc.GetType().GetField("x")?.GetValue(mc) ?? 0);
                    gz = (int)(mc.GetType().GetField("z")?.GetValue(mc) ?? 0);
                }
            }
            catch { }

            rooms.Add(new
            {
                name,
                zone = ZoneField?.GetValue(id)?.ToString() ?? "",
                shape = ShapeField?.GetValue(id)?.ToString() ?? "",
                gx,
                gz,
                rot_y = Math.Round(id.transform.eulerAngles.y, 0),
                x = Math.Round(id.transform.position.x, 1),
                z = Math.Round(id.transform.position.z, 1),
                y = Math.Round(id.transform.position.y, 1)
            });
        }
        return rooms;
    }
}
