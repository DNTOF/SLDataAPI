using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using LabApi.Features.Enums;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Plugins.Configuration;
using LabApi.Loader.Features.Yaml;
using Newtonsoft.Json;
using PlayerRoles;
using SLDataAPI.Capture;
using SLDataAPI.Data;
using SLDataAPI.Integrations;
using SLDataAPI.Map;
using SLDataAPI.Services;
using UnityEngine;
using VoiceChat;
// 游戏原生的封禁系统（全局命名空间的类型）
using GameBanHandler = global::BanHandler;
using GameBanDetails = global::BanDetails;
using GameBanType = global::BanHandler.BanType;
using LabPlugin = LabApi.Loader.Features.Plugins.Plugin;
using Player = LabApi.Features.Wrappers.Player;
using Door = LabApi.Features.Wrappers.Door;
using Room = LabApi.Features.Wrappers.Room;
using Server = LabApi.Features.Wrappers.Server;
using Round = LabApi.Features.Wrappers.Round;
using Warhead = LabApi.Features.Wrappers.Warhead;

namespace SLDataAPI.Control;

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
            // 不向客户端回显异常细节（可能含服务器路径等敏感信息），细节只进服务器日志
            return (500, Json(false, "内部错误"));
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
        // 走 AddLog 管线，Server.RunCommand 返回值里没有）
        CommandOutputCapture.BeginCapture();
        string output;
        string consoleOutput;
        try
        {
            output = MainThreadExecutor.RunOnMainThread(
                () => Server.RunCommand(req.command),
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

    /// <summary>
    /// 多方式解析玩家标识（与旧 EXILED Player.Get 的行为对齐）：
    /// 数字 PlayerId → UserId 精确 → IP 精确 → 昵称（先精确后包含）。
    /// </summary>
    private static Player? FindPlayer(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;

        if (int.TryParse(target.Trim(), out int playerId))
        {
            var byId = Player.Get(playerId);
            if (byId != null) return byId;
        }

        return Player.List.FirstOrDefault(p =>
                   string.Equals(p.UserId, target, StringComparison.OrdinalIgnoreCase))
               ?? Player.List.FirstOrDefault(p =>
                   string.Equals(p.IpAddress, target, StringComparison.OrdinalIgnoreCase))
               ?? Player.GetByNickname(target, requireFullMatch: true)
               ?? Player.GetByNickname(target);
    }

    private static (int, string) PlayerAction(string body, string kind)
    {
        var req = Parse<PlayerActionRequest>(body);
        if (req == null || string.IsNullOrWhiteSpace(req.target))
            return (400, Json(false, "缺少 target 字段"));

        MainThreadExecutor.RunOnMainThread(() =>
        {
            var player = FindPlayer(req.target);
            if (player == null)
                throw new InvalidOperationException($"找不到玩家: {req.target}");

            switch (kind)
            {
                case "kick":
                    player.Kick(string.IsNullOrWhiteSpace(req.reason) ? "由管理端踢出" : req.reason);
                    break;

                case "ban":
                    // 请求单位是分钟；LabAPI 的 Ban 按秒计，0 = 永久
                    player.Ban(
                        string.IsNullOrWhiteSpace(req.reason) ? "由管理端封禁" : req.reason,
                        (long)req.duration * 60);
                    break;

                case "role":
                    if (!Enum.TryParse<RoleTypeId>(req.role, true, out var roleId))
                        throw new InvalidOperationException($"无效角色名: {req.role}");
                    player.SetRole(roleId);
                    break;

                case "teleport":
                    player.Position = new Vector3(req.x, req.y, req.z);
                    break;

                case "mute":
                    if (req.mute == true)
                        player.Mute(isTemporary: false);
                    else if (req.mute == false)
                        player.Unmute(revokeMute: false);
                    else
                        throw new InvalidOperationException("缺少 mute 字段（true=语音禁言 / false=解除）");
                    break;

                case "msg":
                    if (string.IsNullOrWhiteSpace(req.message))
                        throw new InvalidOperationException("缺少 message 字段");
                    float dur = req.duration_seconds <= 0 ? 5f : Math.Min(req.duration_seconds, 60f);
                    if (req.msg_type == "broadcast")
                        player.SendBroadcast(req.message, (ushort)Math.Ceiling(dur));
                    else
                        player.SendHint(req.message, dur);
                    break;

                case "effect":
                    if (!TryEnableEffect(player, req.effect, Math.Max(0.1f, req.effect_duration)))
                        throw new InvalidOperationException($"无效效果名: {req.effect}");
                    break;

                case "state":
                    if (req.godmode.HasValue)
                        player.IsGodModeEnabled = req.godmode.Value;
                    if (req.bypass.HasValue)
                        player.IsBypassEnabled = req.bypass.Value;
                    if (req.health.HasValue)
                        player.Health = Math.Max(0f, req.health.Value);
                    if (req.intercom.HasValue)
                        SetVoiceChannel(player, req.intercom.Value);
                    break;
            }
        }, out var err);

        if (err != null)
            return (400, Json(false, err.Message));

        Log.Info($"[SLDataAPI][Control] player/{kind} target={req.target}");
        return (200, Json(true, "操作完成"));
    }

    // 效果名别名：旧 EXILED EffectType 枚举名 → base-game 效果类名（大小写不敏感匹配）
    private static readonly Dictionary<string, string> EffectAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["burning"] = "Burned",
            ["burn"] = "Burned",
            ["poison"] = "Poisoned",
            ["bleed"] = "Bleeding",
            ["blind"] = "Blinded",
            ["cardiac"] = "CardiacArrest",
            ["concussion"] = "Concussed",
            ["corroding"] = "Corroding",
            ["deaf"] = "Deafened",
            ["flash"] = "Flashed",
            ["invisible"] = "Invisible",
            ["movementboost"] = "MovementBoost",
            ["207"] = "Scp207",
            ["268"] = "Scp268",
            ["amnesiaitems"] = "Amnesia",
        };

    /// <summary>按名称给玩家上效果：先直接匹配，再走别名表，最后按效果类名兜底。</summary>
    private static bool TryEnableEffect(Player player, string? name, float duration)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string effect = name!.Trim();

        if (player.TryGetEffect(effect, out var statusEffect))
        {
            player.EnableEffect(statusEffect, 1, duration);
            return true;
        }

        if (EffectAliases.TryGetValue(effect, out string? canonical) &&
            player.TryGetEffect(canonical, out statusEffect))
        {
            player.EnableEffect(statusEffect, 1, duration);
            return true;
        }

        try
        {
            var byClass = player.ActiveEffects.FirstOrDefault(e =>
                string.Equals(e.GetType().Name, effect, StringComparison.OrdinalIgnoreCase));
            if (byClass != null)
            {
                player.EnableEffect(byClass, 1, duration);
                return true;
            }
        }
        catch { /* ActiveEffects 不可用时忽略，走失败路径 */ }

        return false;
    }

    /// <summary>
    /// 强制切换玩家语音通道（Intercom / Proximity）。
    /// LabAPI 的 VoiceChannel 是只读的，VoiceModuleBase.CurrentChannel 的 setter
    /// 也不是公开的，这里用反射写入（与旧 EXILED 的 VoiceChannel 赋值行为等价）。
    /// </summary>
    private static void SetVoiceChannel(Player player, bool intercom)
    {
        var channel = intercom ? VoiceChatChannel.Intercom : VoiceChatChannel.Proximity;
        try
        {
            var module = player.VoiceModule;
            if (module == null)
                throw new InvalidOperationException("玩家语音模块未初始化");
            var prop = typeof(PlayerRoles.Voice.VoiceModuleBase)
                .GetProperty("CurrentChannel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite)
                throw new InvalidOperationException("CurrentChannel 属性不可写");
            prop.SetValue(module, channel);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"切换语音通道失败: {ex.Message}");
        }
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
                    Round.Restart();
                    break;
                case "end":
                    Round.End(force: true);
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
            // LabAPI 1.1.7 把 Cassie.Message(msg, isHeld, isNoisy, isSubtitles) 标记为错误级过时
            // （新版游戏 CASSIE 已改为 TTS 队列体系）。这里用官方推荐的非过时重载：
            // isNoisy → playBackground；isHeld/isSubtitles 在新 API 中已无对应开关（忽略）。
            LabApi.Features.Wrappers.Announcer.Message(
                req.message,
                customSubtitles: "",
                playBackground: req.isNoisy,
                priority: 0f,
                glitchScale: 1f);
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
            // 防命令注入：URL 会拼进控制台命令，必须是合法 http(s) 绝对地址，
            // 且不含任何空白/控制字符（空格、换行等可被控制台解析成多条命令或参数）
            string url = req.url.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return (400, Json(false, "url 必须是合法的 http:// 或 https:// 绝对地址"));
            foreach (char c in url)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                    return (400, Json(false, "url 不能包含空白或控制字符"));
            }

            return RunConsoleCommand($".m fetch {url}", "正在拉取云端歌单...");
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
                () => Server.RunCommand(command),
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
    // /control/plugins —— 插件列表与启停
    // 列出 LabAPI 原生插件；服务器若同时装有 EXILED，一并反射列出其插件。
    // ------------------------------------------------------------------
    // 插件启停暂存（内存态，重启清零）：name -> 目标 enabled
    // 设计：点击启用/禁用只暂存（不写文件），全部设置好后点"保存并重载"统一写入。
    //
    // ★ LabAPI 语义变化（相对旧 EXILED 版本）：
    //   - apply 写入每个插件的 properties.yml（LabAPI 的启停配置），重启服务器后生效；
    //     LabAPI 没有运行时重载插件本体的公开 API。
    //   - reload 仅热重载各插件的配置文件（等价控制台 reload configs），
    //     不会重载插件 DLL，也不会应用启停变更。
    //   - 同服的 EXILED 插件在 apply 后会立即 ReloadPlugins 生效（走 EXILED 自身机制）。
    private static readonly Dictionary<string, bool> PluginStaged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否 SLDataAPI 自身（禁止禁用）。</summary>
    private static bool IsSelfPlugin(LabPlugin p) =>
        p.GetType().Assembly == typeof(ControlController).Assembly;

    private static LabPlugin? FindLabPlugin(string name) =>
        PluginLoader.Plugins.Keys.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static (int, string) PluginsAction(string body)
    {
        // 支持：
        //   {}                          → 列表（enabled 读配置文件 properties.yml / EXILED 的 is_enabled）
        //   { action: "reload" }        → 热重载全部插件配置（reload configs）
        //   { action: "stage", name, enabled }  → 暂存启停（不写文件）
        //   { action: "clear" }         → 清空暂存
        //   { action: "apply" }         → 一次性写入全部暂存配置（LabAPI 插件重启生效；EXILED 插件立即重载）
        var req = Parse<PluginsRequest>(body);

        string json = MainThreadExecutor.RunOnMainThread(() =>
        {
            if (req?.action == "reload")
            {
                int count = 0;
                foreach (var p in PluginLoader.Plugins.Keys.ToList())
                {
                    if (IsSelfPlugin(p)) continue; // 自身配置被 HTTP 服务持有，热重载会得到半旧半新的状态
                    try { p.LoadConfigs(); count++; }
                    catch { /* 单个插件失败不影响其余 */ }
                }
                return Json(true, $"已热重载 {count} 个插件的配置（插件本体与启停状态需重启服务器生效）");
            }

            if (req?.action == "stage")
            {
                if (string.IsNullOrWhiteSpace(req.name))
                    throw new InvalidOperationException("缺少 name 字段");
                bool found = FindLabPlugin(req.name) != null || ExiledInterop.FindPlugin(req.name) != null;
                if (!found)
                    throw new InvalidOperationException($"未找到插件: {req.name}");
                var labSelf = FindLabPlugin(req.name);
                if (labSelf != null && IsSelfPlugin(labSelf))
                    throw new InvalidOperationException("禁止禁用 SLDataAPI 自身");
                PluginStaged[req.name.Trim()] = req.enabled;
                return Json(true,
                    $"已暂存{(req.enabled ? "启用" : "禁用")}插件 {req.name.Trim()}（点\"保存并重载\"统一生效）",
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
                bool exiledTouched = false;
                foreach (var kv in PluginStaged)
                {
                    var labPlugin = FindLabPlugin(kv.Key);
                    if (labPlugin != null)
                    {
                        if (IsSelfPlugin(labPlugin))
                        {
                            failed.Add(new { name = kv.Key, reason = "禁止禁用 SLDataAPI 自身" });
                            continue;
                        }
                        try
                        {
                            WriteLabPluginEnabled(labPlugin, kv.Value);
                            applied.Add(new { name = kv.Key, enabled = kv.Value, note = "重启服务器后生效" });
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { name = kv.Key, reason = ex.Message });
                        }
                        continue;
                    }

                    var exiledPlugin = ExiledInterop.FindPlugin(kv.Key);
                    if (exiledPlugin != null)
                    {
                        string? errReason = ExiledInterop.SetPluginEnabled(exiledPlugin, kv.Value);
                        if (errReason == null)
                        {
                            applied.Add(new { name = kv.Key, enabled = kv.Value, note = "EXILED 插件已重载生效" });
                            exiledTouched = true;
                        }
                        else
                        {
                            failed.Add(new { name = kv.Key, reason = errReason });
                        }
                        continue;
                    }

                    failed.Add(new { name = kv.Key, reason = "插件不存在" });
                }
                PluginStaged.Clear();

                if (exiledTouched)
                    ExiledInterop.ReloadPlugins();

                return Json(true,
                    $"已保存 {applied.Count} 个插件的启停设置" + (failed.Count > 0 ? $"（{failed.Count} 个失败）" : "") +
                    "。LabAPI 插件需重启服务器生效。",
                    new { applied, failed });
            }

            if (req != null && !string.IsNullOrEmpty(req.action) && req.action != "list")
                throw new InvalidOperationException($"未知 action: {req.action}");

            // 列表：enabled 读【配置文件】（LabAPI properties.yml / EXILED is_enabled），
            // 而非运行时状态 —— 运行时可能被全局禁用/重载中，不代表配置文件里的启停设置
            var plugins = new List<object>();

            foreach (var p in PluginLoader.Plugins.Keys)
            {
                plugins.Add(new
                {
                    name = p.Name,
                    author = p.Author,
                    version = p.Version.ToString(),
                    prefix = "",
                    priority = p.Priority.ToString(),
                    enabled = p.Properties?.IsEnabled ?? true,
                    self = IsSelfPlugin(p),
                    staged = PluginStaged.TryGetValue(p.Name, out bool target) ? target : (bool?)null,
                    source = "labapi",
                });
            }

            foreach (var p in ExiledInterop.GetPlugins())
            {
                var info = ExiledInterop.GetInfo(p);
                if (info == null) continue;
                string name = info.Value.Name;
                plugins.Add(new
                {
                    name,
                    author = info.Value.Author,
                    version = info.Value.Version,
                    prefix = info.Value.Prefix,
                    priority = info.Value.Priority,
                    enabled = ExiledInterop.GetIsEnabled(p) ?? true,
                    self = false,
                    staged = PluginStaged.TryGetValue(name, out bool target) ? target : (bool?)null,
                    source = "exiled",
                });
            }

            return Json(true, "ok", new { count = plugins.Count, plugins });
        }, out var err);

        if (err != null)
            return (500, Json(false, $"插件操作失败: {err.Message}"));

        return (200, json);
    }

    /// <summary>把 LabAPI 插件的启停写入其 properties.yml（LabAPI/configs/&lt;端口&gt;/&lt;插件名&gt;/properties.yml）。</summary>
    private static void WriteLabPluginEnabled(LabPlugin plugin, bool enabled)
    {
        string path = ConfigurationLoader.GetConfigPath(plugin, "properties.yml");
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 沿用 LabAPI 自己的 YAML 序列化器，保证命名/格式与其加载器兼容
        string yaml = YamlConfigParser.Serializer.Serialize(new Properties { IsEnabled = enabled });
        File.WriteAllText(path, yaml, Encoding.UTF8);

        // 同步内存态，列表展示立即反映
        if (plugin.Properties != null)
            plugin.Properties.IsEnabled = enabled;
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
                    Door? door = FindDoor(req.door_type);
                    if (door == null)
                        throw new InvalidOperationException($"未找到门: {req.door_type}");

                    if (req.lock_door == true)
                        door.Lock(Interactables.Interobjects.DoorUtils.DoorLockReason.AdminCommand, true);
                    else if (req.lock_door == false)
                        door.Lock(Interactables.Interobjects.DoorUtils.DoorLockReason.AdminCommand, false);
                    if (req.open_door.HasValue) door.IsOpened = req.open_door.Value;

                    return (200, Json(true, "ok", new
                    {
                        door_type = req.door_type,
                        locked = door.IsLocked,
                        open = door.IsOpened
                    }));
                }

                case "lights":
                {
                    if (!Enum.TryParse<MapGeneration.RoomName>(req.room_type, true, out var roomName))
                        throw new InvalidOperationException($"无效房间类型: {req.room_type}");

                    // 同名房间可能有多处（Room.Get 返回集合），统一全部处理
                    Room? room = Room.Get(roomName).FirstOrDefault();
                    if (room == null)
                        throw new InvalidOperationException($"未找到房间: {req.room_type}");

                    if (req.lights_off == true)
                    {
                        float dur = Math.Max(1f, req.duration);
                        foreach (var lc in room.AllLightControllers)
                            lc.FlickerLights(dur);
                    }
                    else
                    {
                        // duration=0 → 立即恢复照明
                        foreach (var lc in room.AllLightControllers)
                            lc.LightsEnabled = true;
                    }

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

    /// <summary>
    /// 解析门标识：优先 LabAPI 的 DoorName 枚举（旧 EXILED DoorType 名称大多一致），
    /// 再按 NameTag 精确 / 包含匹配兜底。
    /// </summary>
    private static Door? FindDoor(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string tag = input!.Trim();

        if (Enum.TryParse<DoorName>(tag, true, out var doorName))
        {
            var byEnum = Door.Get(doorName);
            if (byEnum != null) return byEnum;
        }

        return Door.Get(tag)
               ?? Door.List.FirstOrDefault(d => string.Equals(d.NameTag, tag, StringComparison.OrdinalIgnoreCase))
               ?? Door.List.FirstOrDefault(d =>
                   !string.IsNullOrEmpty(d.NameTag) &&
                   d.NameTag!.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0);
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
