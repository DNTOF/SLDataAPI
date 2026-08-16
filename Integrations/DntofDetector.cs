using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LabApi.Loader;
using SLDataAPI.Data;

namespace SLDataAPI.Integrations;

/// <summary>
/// 反射探测 DNT_OF 系列插件（SLPlayer / OmegaWarhead）是否已加载，
/// 并在已加载时提取其运行时状态。
///
/// 设计原则：
/// - 不对 SLPlayer.dll / OmegaWarhead.dll 建立编译期引用（它们是可选依赖）。
///   全部通过反射按插件名 + 属性名查找，任意一方缺失/未加载都只是探测不到，
///   不会导致 SLDataAPI 编译失败或运行时崩溃。
/// - SLPlayer / OmegaWarhead 目前是 EXILED 插件：通过 ExiledInterop 反射桥
///   在 EXILED 与之共存的部署下找到实例；若它们将来迁移为 LabAPI 原生插件，
///   这里的 LabAPI 注册表查找同样能命中（探测逻辑只依赖属性名，与框架无关）。
/// - 必须在主线程调用（在 DataCollector.UpdateData 的 MEC 协程里调用），
///   因为要读取 Player.Position / Player.Nickname 等触及游戏对象的属性。
///   不要在 HttpServer 的请求处理线程里调用这个类。
/// - 每一步反射都做 null 判空 + 外层 try-catch，对方以后重构了属性名，
///   这里只会静默探测失败，不会报错崩服。
/// </summary>
public static class DntofDetector
{
    public static DntofInfo Collect()
    {
        var info = new DntofInfo();

        try { info.sl_player = CollectSlPlayer(); }
        catch (Exception ex) { Log.Debug($"[SLDataAPI] SLPlayer 探测异常（忽略）: {ex.Message}"); }

        try { info.omega_warhead = CollectOmegaWarhead(); }
        catch (Exception ex) { Log.Debug($"[SLDataAPI] OmegaWarhead 探测异常（忽略）: {ex.Message}"); }

        return info;
    }

    /// <summary>按名称查找目标插件：先 EXILED（反射桥），再 LabAPI 注册表。</summary>
    private static object? FindPlugin(string name) =>
        ExiledInterop.FindPlugin(name)
        ?? PluginLoader.Plugins.Keys.FirstOrDefault(p => p.Name == name) as object;

    // ===================== SLPlayer =====================

    private static SlPlayerInfo? CollectSlPlayer()
    {
        object? plugin = FindPlugin("SLPlayer");
        if (plugin == null) return null;

        var result = new SlPlayerInfo { present = true };

        object? controller = plugin.GetType().GetProperty("Controller")?.GetValue(plugin);
        if (controller == null) return result;

        Type ctType = controller.GetType();

        object? sourceModeObj = ctType.GetProperty("SourceMode")?.GetValue(controller);
        string sourceMode = sourceModeObj?.ToString() ?? "";
        if (sourceMode == "Remote")
        {
            result.source_mode = "remote";
            result.remote_url = ctType.GetProperty("RemoteSourceUrl")?.GetValue(controller) as string;
        }
        else
        {
            result.source_mode = "local";
        }

        object? currentPlayer = ctType.GetProperty("CurrentPlayer")?.GetValue(controller);
        int currentIndex = ctType.GetProperty("CurrentIndex")?.GetValue(controller) is int idx ? idx : -1;
        IList? playlist = ctType.GetProperty("Playlist")?.GetValue(controller) as IList;

        if (currentPlayer != null && currentIndex >= 0 && playlist != null && currentIndex < playlist.Count)
        {
            object? song = playlist[currentIndex];
            result.now_playing = song?.GetType().GetProperty("DisplayName")?.GetValue(song) as string;
        }

        return result;
    }

    // ===================== OmegaWarhead =====================

    private static OmegaWarheadInfo? CollectOmegaWarhead()
    {
        object? plugin = FindPlugin("OmegaWarhead");
        if (plugin == null) return null;

        var result = new OmegaWarheadInfo { present = true };
        Type pluginType = plugin.GetType();

        // ---- 硬币（放射性元素）拾取者 ----
        object? elementHolders = pluginType.GetProperty("ElementHolders")?.GetValue(plugin);
        if (elementHolders is IEnumerable holderEnum)
        {
            foreach (object? kv in holderEnum)
            {
                Type kvType = kv.GetType();
                object? keyPlayer = kvType.GetProperty("Key")?.GetValue(kv);
                object? valCount = kvType.GetProperty("Value")?.GetValue(kv);
                if (keyPlayer == null) continue;

                string? nickname = keyPlayer.GetType().GetProperty("Nickname")?.GetValue(keyPlayer) as string;
                object? positionObj = keyPlayer.GetType().GetProperty("Position")?.GetValue(keyPlayer);

                result.coin_holders.Add(new CoinHolderInfo
                {
                    nickname = nickname ?? "未知",
                    count = valCount is int c ? c : 0,
                    position = FormatPosition(positionObj) ?? ""
                });
            }
        }

        // ---- 控制器持有人 + 倒计时 ----
        object? sessionManager = pluginType.GetProperty("SessionManager")?.GetValue(plugin);
        object? activeSession = sessionManager?.GetType().GetProperty("ActiveSession")?.GetValue(sessionManager);

        if (activeSession != null)
        {
            object? operatorPlayer = activeSession.GetType().GetProperty("Operator")?.GetValue(activeSession);
            object? stateObj = activeSession.GetType().GetProperty("State")?.GetValue(activeSession);
            object? remainingObj = activeSession.GetType().GetProperty("RemainingTime")?.GetValue(activeSession);

            result.controller_holder = operatorPlayer?.GetType().GetProperty("Nickname")?.GetValue(operatorPlayer) as string;
            result.phase = TranslatePhase(stateObj?.ToString());
            result.countdown = remainingObj is float f ? (int)f : (int?)null;
        }
        else if (result.coin_holders.Count > 0)
        {
            result.phase = "collecting";
        }
        else
        {
            result.phase = "none";
        }

        return result;
    }

    private static string? FormatPosition(object? positionObj)
    {
        if (positionObj == null) return null;
        try
        {
            Type t = positionObj.GetType();
            float x = Convert.ToSingle(t.GetField("x")?.GetValue(positionObj) ?? 0f);
            float y = Convert.ToSingle(t.GetField("y")?.GetValue(positionObj) ?? 0f);
            float z = Convert.ToSingle(t.GetField("z")?.GetValue(positionObj) ?? 0f);
            return $"({x:F1}, {y:F1}, {z:F1})";
        }
        catch
        {
            return null;
        }
    }

    private static string TranslatePhase(string? state) => state switch
    {
        "Idle" => "idle_holding",
        "Confirming" => "confirming",
        "Locked" => "locked",
        "Counting" => "counting",
        "Detonation" => "detonation",
        _ => "none"
    };
}
