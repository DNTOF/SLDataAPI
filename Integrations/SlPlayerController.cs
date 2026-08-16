using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using LabApi.Loader;
using Newtonsoft.Json;

namespace SLDataAPI.Integrations;

/// <summary>
/// /control/slplayer 端点的业务实现 —— 直接调用 SLPlayer_GUI（DNT_OF 的 player_gui 项目）
/// 的 MusicController，而不是通过控制台命令拼字符串，WebUI 上就能按钮化控制音乐播放。
///
/// 设计原则（与 DntofDetector 一致）：
/// - 不对 SLPlayer.dll 建立编译期引用（可选依赖），全部反射按属性/方法名调用；
///   SLPlayer 未加载 / 版本属性名变动时返回明确错误而不是崩服。
///   SLPlayer 目前是 EXILED 插件，经 ExiledInterop 反射桥定位实例；
///   若其将来迁移为 LabAPI 原生插件，LabAPI 注册表查找同样能命中。
/// - 所有播放操作必须在 Unity 主线程执行（触碰 AudioPlayer），由调用方包在
///   MainThreadExecutor.RunOnMainThread 里。
/// - fetch（拉取云端 YAML 歌单）走服务器命令通道 .m fetch —— 复用命令输出捕获，
///   且 YAML 解析逻辑集中在 SLPlayer 侧，不在这里重复实现。
/// </summary>
public static class SlPlayerController
{
    private static object? FindPlugin() =>
        ExiledInterop.FindPlugin("SLPlayer")
        ?? PluginLoader.Plugins.Keys.FirstOrDefault(p => p.Name == "SLPlayer") as object;

    /// <summary>获取 SLPlayer 插件的 MusicController 实例；未加载/未就绪时抛异常。</summary>
    public static object GetController()
    {
        object? plugin = FindPlugin();
        if (plugin == null)
            throw new InvalidOperationException("SLPlayer 插件未加载");
        object? controller = plugin.GetType().GetProperty("Controller")?.GetValue(plugin);
        if (controller == null)
            throw new InvalidOperationException("SLPlayer 控制器未就绪（插件可能仍在初始化）");
        return controller;
    }

    /// <summary>反射调用实例方法；返回方法返回值（无返回值时为 null）。</summary>
    private static object? Call(object target, string method, Type[] argTypes, object[] args)
    {
        MethodInfo? mi = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, argTypes, null);
        if (mi == null)
            throw new InvalidOperationException($"SLPlayer 缺少方法 {method}（版本不兼容？）");
        return mi.Invoke(target, args);
    }

    /// <summary>按名称读属性（找不到抛异常，供状态构建用）。</summary>
    private static object? GetProp(object target, string name)
    {
        PropertyInfo? pi = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (pi == null)
            throw new InvalidOperationException($"SLPlayer 缺少属性 {name}（版本不兼容？）");
        return pi.GetValue(target);
    }

    // ------------------------------------------------------------------
    // action: status —— 完整播放状态（含播放列表，一次请求拉全）
    // ------------------------------------------------------------------
    public static object Status(object controller)
    {
        var playlist = GetProp(controller, "Playlist") as IList;
        int idx = GetProp(controller, "CurrentIndex") is int i ? i : -1;
        bool shuffle = GetProp(controller, "IsShuffle") is true;
        float vol = GetProp(controller, "CurrentVolume") is float v ? v : 0f;
        string source = GetProp(controller, "SourceMode")?.ToString() ?? "Local";
        string? remoteUrl = GetProp(controller, "RemoteSourceUrl") as string;
        var timer = GetProp(controller, "PlaybackTimer") as Stopwatch;

        object? song = idx >= 0 && playlist != null && idx < playlist.Count ? playlist[idx] : null;
        string? display = song?.GetType().GetProperty("DisplayName")?.GetValue(song) as string;
        TimeSpan duration = song?.GetType().GetProperty("Duration")?.GetValue(song) is TimeSpan d ? d : TimeSpan.Zero;
        TimeSpan elapsed = timer?.Elapsed ?? TimeSpan.Zero;

        var list = BuildPlaylist(playlist, idx);

        return new
        {
            playing = song != null,
            index = idx,
            song = display,
            elapsed_seconds = (int)elapsed.TotalSeconds,
            duration_seconds = (int)duration.TotalSeconds,
            volume = (int)(vol * 100),
            shuffle,
            source = source == "Remote" ? "remote" : "local",
            remote_url = remoteUrl,
            playlist_count = list.Count,
            playlist = list
        };
    }

    // ------------------------------------------------------------------
    // action: list —— 仅播放列表
    // ------------------------------------------------------------------
    public static System.Collections.Generic.List<object> ListSongs(object controller)
    {
        var playlist = GetProp(controller, "Playlist") as IList;
        int idx = GetProp(controller, "CurrentIndex") is int i ? i : -1;
        return BuildPlaylist(playlist, idx);
    }

    private static System.Collections.Generic.List<object> BuildPlaylist(IList? playlist, int currentIndex)
    {
        var list = new System.Collections.Generic.List<object>();
        if (playlist == null)
            return list;

        for (int n = 0; n < playlist.Count; n++)
        {
            object? s = playlist[n];
            list.Add(new
            {
                index = n,
                display = s?.GetType().GetProperty("DisplayName")?.GetValue(s) as string ?? "?",
                duration_seconds = s?.GetType().GetProperty("Duration")?.GetValue(s) is TimeSpan dd ? (int)dd.TotalSeconds : 0,
                current = n == currentIndex
            });
        }
        return list;
    }

    // ------------------------------------------------------------------
    // 播放控制动作（返回提示文本）
    // ------------------------------------------------------------------
    public static string Play(object controller, int index)
    {
        object? result = Call(controller, "PlayIndex", new[] { typeof(int) }, new object[] { index });
        return result as string ?? "已触发播放";
    }

    public static string PlayNext(object controller)
    {
        object? result = Call(controller, "PlayNext", Type.EmptyTypes, Array.Empty<object>());
        return result as string ?? "已切换下一首";
    }

    public static string Stop(object controller)
    {
        Call(controller, "Stop", Type.EmptyTypes, Array.Empty<object>());
        return "音乐已停止播放";
    }

    public static string SetVolume(object controller, int volumePercent)
    {
        object? result = Call(controller, "SetVolume", new[] { typeof(float) }, new object[] { volumePercent / 100f });
        return result as string ?? $"音量已设置为 {volumePercent}%";
    }

    public static string SetShuffle(object controller, string mode)
    {
        PropertyInfo? pi = controller.GetType().GetProperty("IsShuffle", BindingFlags.Public | BindingFlags.Instance);
        if (pi == null)
            throw new InvalidOperationException("SLPlayer 缺少 IsShuffle 属性（版本不兼容？）");

        bool cur = pi.GetValue(controller) is true;
        bool next = mode switch
        {
            "on" => true,
            "off" => false,
            _ => !cur
        };
        pi.SetValue(controller, next);
        return next ? "已切换为随机模式" : "已切换为顺序模式";
    }

    public static string Reload(object controller)
    {
        object? result = Call(controller, "ScanFiles", Type.EmptyTypes, Array.Empty<object>());
        int count = result is int n ? n : 0;
        return $"扫描完成，共加载 {count} 首本地歌曲";
    }
}
