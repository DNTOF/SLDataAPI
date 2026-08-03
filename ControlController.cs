using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Features.Doors;
using Newtonsoft.Json;
using PlayerRoles;
using UnityEngine;
// 游戏原生的封禁系统（全局命名空间的类型，与 EXILED 的 Exiled.API.Features.BanHandler
// 重名，必须用别名区分：GetBans/RemoveBan 只在游戏原生类上）
using GameBanHandler = global::BanHandler;
using GameBanDetails = global::BanDetails;
using GameBanType = global::BanHandler.BanType;

/// <summary>
/// 控制接口的业务逻辑层。所有会触碰游戏 / Mirror 网络状态的调用
/// 都通过 MainThreadExecutor 派发到主线程执行，HTTP 线程同步等待结果。
/// </summary>
public static class ControlController
{
    public static (int status, string json) Handle(string path, string body)
    {
        try
        {
            return path switch
            {
                "/control/command" => RunCommand(body),
                "/control/player/kick" => PlayerAction(body, "kick"),
                "/control/player/ban" => PlayerAction(body, "ban"),
                "/control/player/role" => PlayerAction(body, "role"),
                "/control/player/teleport" => PlayerAction(body, "teleport"),
                "/control/player/mute" => PlayerAction(body, "mute"),
                "/control/player/msg" => PlayerAction(body, "msg"),
                "/control/player/effect" => PlayerAction(body, "effect"),
                "/control/player/state" => PlayerAction(body, "state"),
                "/control/map" => MapAction(body),
                "/control/map/export" => MapExportAction(),
                "/control/round" => RoundAction(body),
                "/control/cassie" => CassieAction(body),
                "/control/warhead" => WarheadAction(body),
                "/control/slplayer" => SlPlayerAction(body),
                "/control/plugins" => PluginsAction(body),
                "/control/ban_list" => BanListAction(),
                "/control/ban/revoke" => BanRevokeAction(body),
                "/control/ban/add" => BanAddAction(body),
                "/control/logs" => LogsAction(body),
                "/control/files/list" => FilesAction(body, "list"),
                "/control/files/read" => FilesAction(body, "read"),
                "/control/files/write" => FilesAction(body, "write"),
                _ => (404, Json(false, "未知控制端点")),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI][Control] 顶层异常: {ex}");
            return (500, Json(false, $"内部错误: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------
    // /control/command —— 任意服务器控制台命令。
    // ★ 安全警告：这等价于本机控制台权限（大多数 RA 命令通过 GameConsoleCommandHandler
    //   同时注册，可以在这里执行）。ControlToken 一旦泄露即完全沦陷，务必只在受信内网/
    //   反向代理白名单后暴露。
    // ------------------------------------------------------------------
    private static (int, string) RunCommand(string body)
    {
        var req = Parse<CommandRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.command))
            return (400, Json(false, "缺少 command 字段"));

        Log.Info($"[SLDataAPI][Control] 执行服务器命令: {req.command}");

        // 命令执行窗口内捕获控制台输出（插件命令如 SLPlayer .m 的输出
        // 走 AddLog 管线，Server.ExecuteCommand 返回值里没有）
        CommandOutputCapture.BeginCapture();
        string output;
        string consoleOutput;
        try
        {
            output = MainThreadExecutor.RunOnMainThread(
                () => Server.ExecuteCommand(req.command),
                out var err);
            if (err != null)
                return (500, Json(false, $"命令执行失败: {err.Message}"));
        }
        finally
        {
            consoleOutput = CommandOutputCapture.EndCapture();
        }

        return (200, Json(true, "已执行", new { output, console = consoleOutput }));
    }

    // ------------------------------------------------------------------
    // /control/player/{kick|ban|role|teleport}
    // ------------------------------------------------------------------
    private static (int, string) PlayerAction(string body, string kind)
    {
        var req = Parse<PlayerActionRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.target))
            return (400, Json(false, "缺少 target 字段"));

        MainThreadExecutor.RunOnMainThread(() =>
        {
            var player = Player.Get(req.target);
            if (player == null)
                throw new InvalidOperationException($"找不到玩家: {req.target}");

            switch (kind)
            {
                case "kick":
                    player.Kick(string.IsNullOrWhiteSpace(req.reason) ? "由管理端踢出" : req.reason);
                    break;

                case "ban":
                    player.Ban(req.duration, string.IsNullOrWhiteSpace(req.reason) ? "由管理端封禁" : req.reason);
                    break;

                case "role":
                    if (!Enum.TryParse<RoleTypeId>(req.role, true, out var roleId))
                        throw new InvalidOperationException($"无效角色名: {req.role}");
                    player.Role.Set(roleId);
                    break;

                case "teleport":
                    player.Teleport(new Vector3(req.x, req.y, req.z));
                    break;

                case "mute":
                    if (req.mute == true)
                        player.Mute();
                    else if (req.mute == false)
                        player.UnMute();
                    else
                        throw new InvalidOperationException("缺少 mute 字段（true=语音禁言 / false=解除）");
                    break;

                case "msg":
                    if (string.IsNullOrWhiteSpace(req.message))
                        throw new InvalidOperationException("缺少 message 字段");
                    float dur = req.duration_seconds <= 0 ? 5f : Math.Min(req.duration_seconds, 60f);
                    if (req.msg_type == "broadcast")
                        player.Broadcast((ushort)Math.Ceiling(dur), req.message);
                    else
                        player.ShowHint(req.message, dur);
                    break;

                case "effect":
                    if (!Enum.TryParse<EffectType>(req.effect, true, out var effectType))
                        throw new InvalidOperationException($"无效效果名: {req.effect}");
                    player.EnableEffect(effectType, Math.Max(0.1f, req.effect_duration));
                    break;

                case "state":
                    if (req.godmode.HasValue)
                        player.IsGodModeEnabled = req.godmode.Value;
                    if (req.bypass.HasValue)
                        player.IsBypassModeEnabled = req.bypass.Value;
                    if (req.health.HasValue)
                        player.Health = Math.Max(0f, req.health.Value);
                    if (req.intercom.HasValue)
                        player.VoiceChannel = req.intercom.Value
                            ? VoiceChat.VoiceChatChannel.Intercom
                            : VoiceChat.VoiceChatChannel.Proximity;
                    break;
            }
        }, out var err);

        if (err != null)
            return (400, Json(false, err.Message));

        Log.Info($"[SLDataAPI][Control] player/{kind} target={req.target}");
        return (200, Json(true, "操作完成"));
    }

    // ------------------------------------------------------------------
    // /control/round —— action: restart | end | start
    // ------------------------------------------------------------------
    private static (int, string) RoundAction(string body)
    {
        var req = Parse<RoundActionRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.action))
            return (400, Json(false, "缺少 action 字段"));

        MainThreadExecutor.RunOnMainThread(() =>
        {
            switch (req.action.ToLowerInvariant())
            {
                case "restart":
                    Round.Restart(false);
                    break;
                case "end":
                    Round.EndRound(true);
                    break;
                case "start":
                    Round.Start();
                    break;
                default:
                    throw new InvalidOperationException($"未知 action: {req.action}（支持 restart / end / start）");
            }
        }, out var err);

        if (err != null)
            return (400, Json(false, err.Message));

        Log.Info($"[SLDataAPI][Control] round action={req.action}");
        return (200, Json(true, "回合操作完成"));
    }

    // ------------------------------------------------------------------
    // /control/cassie
    // ------------------------------------------------------------------
    private static (int, string) CassieAction(string body)
    {
        var req = Parse<CassieRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.message))
            return (400, Json(false, "缺少 message 字段"));

        MainThreadExecutor.RunOnMainThread(() =>
        {
            Cassie.Message(req.message, req.isHeld, req.isNoisy, req.isSubtitles);
        }, out var err);

        if (err != null)
            return (400, Json(false, err.Message));

        return (200, Json(true, "CASSIE 播报已触发"));
    }

    // ------------------------------------------------------------------
    // /control/warhead —— action: start | stop | detonate
    // ------------------------------------------------------------------
    private static (int, string) WarheadAction(string body)
    {
        var req = Parse<WarheadActionRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.action))
            return (400, Json(false, "缺少 action 字段"));

        MainThreadExecutor.RunOnMainThread(() =>
        {
            switch (req.action.ToLowerInvariant())
            {
                case "start":
                    Warhead.Start();
                    break;
                case "stop":
                    Warhead.Stop();
                    break;
                case "detonate":
                    Warhead.Detonate();
                    break;
                default:
                    throw new InvalidOperationException($"未知 action: {req.action}（支持 start / stop / detonate）");
            }
        }, out var err);

        if (err != null)
            return (400, Json(false, err.Message));

        Log.Info($"[SLDataAPI][Control] warhead action={req.action}");
        return (200, Json(true, "核弹操作完成"));
    }

    // ------------------------------------------------------------------
    // /control/slplayer —— 直接控制 SLPlayer_GUI 音乐播放（反射调用 MusicController）
    // ------------------------------------------------------------------
    private static (int, string) SlPlayerAction(string body)
    {
        var req = Parse<SlPlayerRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.action))
            return (400, Json(false, "缺少 action 字段"));

        string action = req.action.ToLowerInvariant();

        // fetch 走服务器命令通道（.m fetch）—— 复用命令输出捕获，
        // YAML 解析由 SLPlayer 自己完成，这里不重复实现
        if (action == "fetch")
        {
            if (string.IsNullOrWhiteSpace(req.url))
                return (400, Json(false, "缺少 url 字段"));
            if (!req.url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !req.url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return (400, Json(false, "url 必须以 http:// 或 https:// 开头"));

            return RunConsoleCommand($".m fetch {req.url.Trim()}", "正在拉取云端歌单...");
        }

        return MainThreadExecutor.RunOnMainThread(() =>
        {
            object controller = SlPlayerController.GetController();

            switch (action)
            {
                case "status":
                    return (200, Json(true, "ok", SlPlayerController.Status(controller)));

                case "list":
                    return (200, Json(true, "ok", new { songs = SlPlayerController.ListSongs(controller) }));

                case "play":
                    if (req.index < 0)
                        return (400, Json(false, "缺少 index 字段"));
                    return (200, Json(true, SlPlayerController.Play(controller, req.index)));

                case "next":
                    return (200, Json(true, SlPlayerController.PlayNext(controller)));

                case "stop":
                    return (200, Json(true, SlPlayerController.Stop(controller)));

                case "volume":
                    if (req.volume < 0 || req.volume > 100)
                        return (400, Json(false, "volume 需在 0-100 之间"));
                    return (200, Json(true, SlPlayerController.SetVolume(controller, req.volume)));

                case "shuffle":
                    return (200, Json(true, SlPlayerController.SetShuffle(controller, req.shuffle ?? "toggle")));

                case "reload":
                    return (200, Json(true, SlPlayerController.Reload(controller)));

                default:
                    return (404, Json(false, $"未知 action: {req.action}（支持 status/list/play/next/stop/volume/shuffle/reload/fetch）"));
            }
        }, out var err);
    }

    /// <summary>通过服务器控制台执行命令并捕获输出（fetch 用）。</summary>
    private static (int, string) RunConsoleCommand(string command, string okText)
    {
        CommandOutputCapture.BeginCapture();
        try
        {
            string output = MainThreadExecutor.RunOnMainThread(
                () => Server.ExecuteCommand(command),
                out var err);

            if (err != null)
                return (500, Json(false, $"命令执行失败: {err.Message}"));

            return (200, Json(true, okText, new { output }));
        }
        finally
        {
            CommandOutputCapture.EndCapture();
        }
    }

    // ------------------------------------------------------------------
    // /control/plugins —— EXILED 插件列表
    // ------------------------------------------------------------------
    // 插件启停暂存（内存态，重启清零）：name -> 目标 enabled
    // 设计：点击启用/禁用只暂存（不写文件），全部设置好后点"保存并重载"统一写入 + ReloadPlugins
    private static readonly Dictionary<string, bool> PluginStaged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否 SLDataAPI 自身（禁止禁用）。</summary>
    private static bool IsSelfPlugin(Exiled.API.Interfaces.IPlugin<Exiled.API.Interfaces.IConfig> p) =>
        p.Assembly == typeof(ControlController).Assembly;

    private static Exiled.API.Interfaces.IPlugin<Exiled.API.Interfaces.IConfig>? FindPlugin(string name) =>
        Exiled.Loader.Loader.Plugins.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static (int, string) PluginsAction(string body)
    {
        // 支持：
        //   {}                          → 列表（enabled 读配置文件 is_enabled）
        //   { action: "reload" }        → 重载全部插件
        //   { action: "stage", name, enabled }  → 暂存启停（不写文件）
        //   { action: "clear" }         → 清空暂存
        //   { action: "apply" }         → 一次性写入全部暂存配置 + ReloadPlugins
        var req = Parse<PluginsRequest>(body);

        string json = MainThreadExecutor.RunOnMainThread(() =>
        {
            if (req?.action == "reload")
            {
                Exiled.Loader.Loader.ReloadPlugins();
                return Json(true, "插件已全部重载");
            }

            if (req?.action == "stage")
            {
                if (string.IsNullOrWhiteSpace(req.name))
                    throw new InvalidOperationException("缺少 name 字段");
                var plugin = FindPlugin(req.name)
                    ?? throw new InvalidOperationException($"未找到插件: {req.name}");
                if (IsSelfPlugin(plugin))
                    throw new InvalidOperationException("禁止禁用 SLDataAPI 自身");
                if (plugin.Config == null)
                    throw new InvalidOperationException($"插件 {req.name} 没有配置文件，无法启停");
                PluginStaged[plugin.Name] = req.enabled;
                return Json(true,
                    $"已暂存{(req.enabled ? "启用" : "禁用")}插件 {plugin.Name}（点\"保存并重载\"统一生效）",
                    new { staged = StagedSnapshot() });
            }

            if (req?.action == "clear")
            {
                PluginStaged.Clear();
                return Json(true, "已清空暂存", new { staged = StagedSnapshot() });
            }

            if (req?.action == "apply")
            {
                var applied = new List<object>();
                var failed = new List<object>();
                foreach (var kv in PluginStaged)
                {
                    var plugin = FindPlugin(kv.Key);
                    if (plugin == null || IsSelfPlugin(plugin))
                    {
                        failed.Add(new { name = kv.Key, reason = "插件不存在或为 SLDataAPI 自身" });
                        continue;
                    }
                    try
                    {
                        if (plugin.Config == null)
                            throw new InvalidOperationException("没有配置文件");
                        if (string.IsNullOrEmpty(plugin.ConfigPath))
                            throw new InvalidOperationException("未暴露 ConfigPath");
                        // 改配置里的 is_enabled 并写回（用 EXILED 自己的序列化器保证格式兼容）
                        plugin.Config.IsEnabled = kv.Value;
                        File.WriteAllText(plugin.ConfigPath,
                            Exiled.Loader.Loader.Serializer.Serialize(plugin.Config), Encoding.UTF8);
                        applied.Add(new { name = kv.Key, enabled = kv.Value });
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new { name = kv.Key, reason = ex.Message });
                    }
                }
                PluginStaged.Clear();
                if (applied.Count > 0)
                    Exiled.Loader.Loader.ReloadPlugins();
                return Json(true,
                    $"已保存 {applied.Count} 个插件并重载" + (failed.Count > 0 ? $"（{failed.Count} 个失败）" : ""),
                    new { applied, failed });
            }

            if (!string.IsNullOrEmpty(req?.action) && req.action != "list")
                throw new InvalidOperationException($"未知 action: {req.action}");

            // 列表：enabled 读【配置文件】的 is_enabled（EXILED 加载配置时写入 Config.IsEnabled），
            // 而非运行时状态 —— 运行时可能被全局禁用/重载中，不代表配置文件里的启停设置
            var plugins = Exiled.Loader.Loader.Plugins
                .Select(p => new
                {
                    name = p.Name,
                    author = p.Author,
                    version = p.Version.ToString(),
                    prefix = p.Prefix,
                    priority = p.Priority.ToString(),
                    enabled = p.Config?.IsEnabled ?? true,
                    // SLDataAPI 自身：前端禁用启停按钮
                    self = IsSelfPlugin(p),
                    // 该插件是否有暂存的启停改动
                    staged = PluginStaged.TryGetValue(p.Name, out bool target) ? target : (bool?)null,
                })
                .OrderBy(p => p.name)
                .ToList();

            return Json(true, "ok", new { count = plugins.Count, plugins });
        }, out var err);

        if (err != null)
            return (500, Json(false, $"插件操作失败: {err.Message}"));

        return (200, json);
    }

    // ------------------------------------------------------------------
    // /control/ban_list —— 封禁列表
    // ------------------------------------------------------------------
    private static (int, string) BanListAction()
    {
        string json = MainThreadExecutor.RunOnMainThread(() =>
        {
            // 游戏封禁分两类：按 UserId（Steam）和按 IP，分别读取后合并
            var list = new List<object>();
            list.AddRange(GameBanHandler.GetBans(GameBanType.UserId).Select(b => ToBanDto(b, "steam")));
            list.AddRange(GameBanHandler.GetBans(GameBanType.IP).Select(b => ToBanDto(b, "ip")));

            return Json(true, "ok", new { count = list.Count, bans = list });
        }, out var err);

        if (err != null)
            return (500, Json(false, $"封禁列表读取失败: {err.Message}"));

        return (200, json);
    }

    /// <summary>BanDetails → 对外 DTO。Expires/IssuanceTime 是 unix 秒（0 表示永久/未知）。</summary>
    private static object ToBanDto(GameBanDetails b, string banType)
    {
        return new
        {
            user_id = b.Id,
            original_name = b.OriginalName,
            reason = b.Reason,
            issuer = b.Issuer,
            ban_type = banType,
            expires = b.Expires,
            issuance_time = b.IssuanceTime
        };
    }

    // ------------------------------------------------------------------
    // /control/ban/revoke —— 解除封禁
    // ------------------------------------------------------------------
    private static (int, string) BanRevokeAction(string body)
    {
        var req = Parse<BanRevokeRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.user_id))
            return (400, Json(false, "缺少 user_id 字段"));

        bool ok = MainThreadExecutor.RunOnMainThread(() =>
        {
            GameBanType bt = req.ban_type == "ip" ? GameBanType.IP : GameBanType.UserId;
            GameBanDetails? ban = GameBanHandler.GetBans(bt)
                .FirstOrDefault(b => b.Id == req.user_id);
            if (ban == null)
                return false;
            GameBanHandler.RemoveBan(ban.Id, bt, false);
            return true;
        }, out var err);

        if (err != null)
            return (500, Json(false, $"解除封禁失败: {err.Message}"));

        if (!ok)
            return (404, Json(false, $"未找到该封禁记录: {req.user_id}"));

        Log.Info($"[SLDataAPI][Control] 解除封禁 user_id={req.user_id}");
        return (200, Json(true, "已解除封禁"));
    }

    // ------------------------------------------------------------------
    // /control/ban/add —— 新增封禁（支持离线玩家，按 UserId 或 IP）
    // ------------------------------------------------------------------
    private static (int, string) BanAddAction(string body)
    {
        var req = Parse<BanAddRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.user_id))
            return (400, Json(false, "缺少 user_id 字段"));

        string userId = req.user_id.Trim();
        GameBanType bt = req.ban_type == "ip" ? GameBanType.IP : GameBanType.UserId;

        bool ok = MainThreadExecutor.RunOnMainThread(() =>
        {
            var details = new GameBanDetails
            {
                Id = userId,
                OriginalName = req.original_name ?? "",
                Reason = req.reason ?? "",
                Issuer = "WebUI",
                // unix 秒；0 = 永久
                Expires = req.duration <= 0
                    ? 0
                    : DateTimeOffset.UtcNow.AddMinutes(req.duration).ToUnixTimeSeconds(),
                IssuanceTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            return GameBanHandler.IssueBan(details, bt, false);
        }, out var err);

        if (err != null)
            return (500, Json(false, $"封禁失败: {err.Message}"));

        if (!ok)
            return (400, Json(false, "封禁写入失败（IssueBan 返回 false）"));

        Log.Info($"[SLDataAPI][Control] 新增封禁 user_id={userId} type={req.ban_type} duration={req.duration}min");
        return (200, Json(true, "已封禁"));
    }

    // ------------------------------------------------------------------
    // /control/logs —— 服务器日志尾部
    // 纯 IO，直接在当前线程读，不派发主线程。
    // ------------------------------------------------------------------
    private static (int, string) LogsAction(string body)
    {
        var req = Parse<LogsRequest>(body);
        try
        {
            // action=list → 列出所有可用日志文件（前端选择器用）
            if (req?.action == "list")
            {
                object list = ServerLogService.ListLogFiles();
                return (200, Json(true, "ok", list));
            }
            // 其余：尾部读取（path 非空时读取指定日志文件，路径经白名单校验）
            object data = ServerLogService.Tail(req?.lines ?? 200, req?.filter ?? "", req?.path);
            return (200, Json(true, "ok", data));
        }
        catch (Exception ex)
        {
            return (400, Json(false, ex.Message));
        }
    }

    // ------------------------------------------------------------------
    // /control/files/{list|read|write} —— 文件管理（受 FileRoot 白名单约束）
    // 纯 IO，直接在当前线程执行，不派发主线程。
    // ------------------------------------------------------------------
    private static (int, string) FilesAction(string body, string kind)
    {
        var req = Parse<FilesRequest>(body);
        string root = Plugin.Instance?.Config.FileRoot ?? "";
        if (!FileService.IsEnabled(root))
            return (404, Json(false, "文件端点未启用（服务器未配置 FileRoot）"));

        try
        {
            object data = kind switch
            {
                "list" => FileService.List(root, req?.path ?? ""),
                "read" => FileService.Read(root, req?.path ?? ""),
                "write" => FileService.Write(root, req?.path ?? "", req?.content ?? ""),
                _ => throw new InvalidOperationException("未知文件操作")
            };
            return (200, Json(true, "ok", data));
        }
        catch (Exception ex)
        {
            return (400, Json(false, ex.Message));
        }
    }

    // ------------------------------------------------------------------
    // /control/map —— 地图布局读取 + 门/灯控制
    // layout 只读缓存（回合开始事件在主线程采集），doors/lights 派发主线程执行。
    // ------------------------------------------------------------------
    private static (int, string) MapAction(string body)
    {
        var req = Parse<MapControlRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.action))
            return (400, Json(false, "缺少 action 字段"));

        string action = req.action.ToLowerInvariant();

        if (action == "seed")
        {
            // 轻量端点：只返回回合种子。WebUI 按 seed 命中本地布局缓存时
            // 无需再传输房间数据（同一 seed 布局恒定）。
            return (200, Json(true, "ok", new
            {
                ready = MapLayoutService.GetLayout() != null,
                seed = MapLayoutService.ReadSeed()
            }));
        }

        if (action == "layout")
        {
            object? layout = MapLayoutService.GetLayout();
            if (layout == null)
                return (200, Json(true, "ok", new { ready = false, count = 0, rooms = new object[0] }));
            return (200, Json(true, "ok", layout));
        }

        return MainThreadExecutor.RunOnMainThread(() =>
        {
            switch (action)
            {
                case "doors":
                {
                    if (!Enum.TryParse<DoorType>(req.door_type, true, out var doorType))
                        throw new InvalidOperationException($"无效门类型: {req.door_type}");

                    Door? door = Door.Get(doorType);
                    if (door == null)
                        throw new InvalidOperationException($"未找到门: {req.door_type}");

                    if (req.lock_door == true) door.Lock(DoorLockType.AdminCommand);
                    else if (req.lock_door == false) door.Unlock();
                    if (req.open_door.HasValue) door.IsOpen = req.open_door.Value;

                    return (200, Json(true, "ok", new
                    {
                        door_type = req.door_type,
                        locked = door.IsLocked,
                        open = door.IsOpen
                    }));
                }

                case "lights":
                {
                    if (!Enum.TryParse<RoomType>(req.room_type, true, out var roomType))
                        throw new InvalidOperationException($"无效房间类型: {req.room_type}");

                    Room? room = Room.Get(roomType);
                    if (room == null)
                        throw new InvalidOperationException($"未找到房间: {req.room_type}");

                    if (req.lights_off == true)
                        room.TurnOffLights(Math.Max(1f, req.duration));
                    else
                        room.TurnOffLights(0f); // duration=0 立即恢复照明

                    return (200, Json(true, "ok", new
                    {
                        room_type = req.room_type,
                        lights_off = req.lights_off == true
                    }));
                }

                default:
                    return (404, Json(false, $"未知 action: {req.action}（支持 layout/doors/lights）"));
            }
        }, out var err);
    }

    // ------------------------------------------------------------------
    // /control/map/export —— 导出地图生成数据（seed 重建方案的数据采集）
    // 输出：Atlas 图集（RGBA base64）+ GlyphShapePair 表 + 各区域候选房间权重。
    // 在服务器上跑一次，把响应保存为 JSON 交给开发者即可。
    // ------------------------------------------------------------------
    private static (int, string) MapExportAction()
    {
        string json = MainThreadExecutor.RunOnMainThread(() =>
        {
            try
            {
                return Json(true, "ok", MapExportService.Export());
            }
            catch (Exception ex)
            {
                return Json(false, $"导出失败: {ex.Message}");
            }
        }, out var err);

        if (err != null)
            return (500, Json(false, $"导出失败: {err.Message}"));

        return (200, json);
    }

    private static T? Parse<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonConvert.DeserializeObject<T>(body);
        }
        catch
        {
            return null;
        }
    }

    private static string Json(bool success, string message, object data = null!) =>
        JsonConvert.SerializeObject(new ControlResponse { success = success, message = message, data = data });

    /// <summary>暂存清单快照（name → 目标 enabled）。</summary>
    private static object StagedSnapshot() =>
        PluginStaged.Select(kv => new { name = kv.Key, enabled = kv.Value }).ToList();
}
