using System;
using System.IO;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Features.Wrappers;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Yaml;
using SLDataAPI.Capture;
using SLDataAPI.Control;
using SLDataAPI.Map;
using SLDataAPI.Services;
using SLDataAPI.Voice;

namespace SLDataAPI;

public class Plugin : LabApi.Loader.Features.Plugins.Plugin<Config>
{
    public static Plugin? Instance;
    private HttpServer? server;

    public override string Name => "SLDataAPI";
    public override string Description => "通过 HTTP API 向外部（WebUI / 机器人）提供服务器数据采集与远程控制能力（LabAPI 原生插件，代号 Yagami Light）";
    public override string Author => "DNT_OF";
    public override Version Version => new Version(2, 5, 0);
    public override Version RequiredApiVersion => new Version(1, 1, 7);

    public override void Enable()
    {
        Instance = this;

        LabApi.Events.Handlers.ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
        LabApi.Events.Handlers.ServerEvents.RoundStarted    += OnRoundStarted;
        LabApi.Events.Handlers.ServerEvents.RoundEnded      += OnRoundEnded;

        // 命令输出捕获（patch ServerConsole.AddLog）
        CommandOutputCapture.Init();

        ValidateConfigFileIntegrity();
        ValidateControlConfig();

        // 安全提示：VerifyToken 仍是出厂默认值时，数据接口相当于裸奔
        if (string.Equals(Config.VerifyToken, "your_secret_token", StringComparison.Ordinal))
            Log.Warn("[SLDataAPI] VerifyToken 仍为出厂默认值 your_secret_token，请尽快修改为强随机值！");

        // 生效配置摘要（一眼识别"配置没被读到、正在用默认值"的状态）
        Log.Info(
            $"[SLDataAPI] 配置摘要：http_port={Config.HttpPort}，verify_token 长度 {Config.VerifyToken?.Length ?? 0}，" +
            $"control={(Config.ControlEnabled ? $"{Config.ControlTransport} 模式，token 长度 {Config.ControlToken?.Length ?? 0}" : "关闭")}，" +
            $"voice={(Config.VoiceEnabled ? $"启用(端口 {Config.VoicePort})" : "关闭")}，" +
            $"录音={(Config.VoiceRecordEnabled ? $"开(保留 {Config.VoiceRecordMaxRounds} 局)" : "关")}。");

        server = new HttpServer(Config.HttpPort, Config);
        server.Start();

        // 语音转发（v2.3）：独立 WebSocket 端口，ControlToken 鉴权
        if (Config.VoiceEnabled)
        {
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage += OnSendingVoiceMessage;
            VoiceService.Start(Config.VoicePort);
        }

        // 语音录音取证（v2.5）：每局自动保存 WAV + 时间轴日志
        VoiceRecorder.Configure(Config.VoiceRecordEnabled, Config.VoiceRecordMaxRounds, Config.VoiceRecordDir);

        // 插件启用时立即采集一次真实数据，并启动定时循环
        DataCollector.IsRoundActive = Round.IsRoundStarted;
        DataCollector.InitData(Config.PushIntervalSeconds);

        if (Config.AutoUpdateCheck)
            UpdateChecker.CheckAsync(Version, Config.AutoUpdateInstall);

        Log.Info($"SLDataAPI v{Version} (Yagami Light / LabAPI) enabled. HTTP on port {Config.HttpPort}. Control API: {(Config.ControlEnabled ? $"{Config.ControlTransport.ToUpperInvariant()} 模式" : "关闭")}. Voice: {(Config.VoiceEnabled ? $"启用(端口 {Config.VoicePort})" : "关闭")}.");
    }

    public override void Disable()
    {
        LabApi.Events.Handlers.ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
        LabApi.Events.Handlers.ServerEvents.RoundStarted    -= OnRoundStarted;
        LabApi.Events.Handlers.ServerEvents.RoundEnded      -= OnRoundEnded;
        LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage -= OnSendingVoiceMessage;

        VoiceService.Stop();
        VoiceRecorder.EndRound(waitFinalize: true); // 兜底：停服时定稿并等待打包完成
        server?.Stop();
        WsControlService.ShutdownAll();
        DataCollector.StopTimer();
        CommandOutputCapture.Shutdown();

        Instance = null;
    }

    private void OnSendingVoiceMessage(PlayerSendingVoiceMessageEventArgs ev)
    {
        if (!ev.IsAllowed) return;
        var message = ev.Message; // Message 是 ref 属性，先拷贝副本再传递
        VoiceService.HandleIncoming(ev.Player, message);
    }

    /// <summary>
    /// 启动时校验控制接口配置。token 格式不合法则强制在本次运行中禁用控制接口，
    /// 连接方式仅接受 http / ws（非法值回落 http）——这只影响运行时状态，不会改写配置文件。
    /// </summary>
    private void ValidateControlConfig()
    {
        if (!Config.ControlEnabled)
            return;

        if (!ControlAuth.IsValidTokenFormat(Config.ControlToken))
        {
            Log.Error(
                "[SLDataAPI] ControlEnabled=true 但 ControlToken 格式不合法" +
                "（要求长度不少于8，且同时包含大写字母/小写字母/数字/特殊符号）。" +
                "本次运行将强制禁用控制接口，请修正配置后重启服务器。");
            Config.ControlEnabled = false;
            return;
        }

        string transport = (Config.ControlTransport ?? "http").Trim().ToLowerInvariant();
        if (transport != "http" && transport != "ws")
        {
            Log.Warn(
                $"[SLDataAPI] ControlTransport 值非法: \"{Config.ControlTransport}\"" +
                "（仅支持 http / ws，二选一硬互斥），本次运行按 http 处理。");
            transport = "http";
        }
        Config.ControlTransport = transport;
    }

    /// <summary>
    /// 配置文件完整性自检：用 LabAPI 同款反序列化器重读一遍配置文件。
    /// LabAPI 的 LoadConfigs 在任何值格式错误（布尔加引号变字符串 / Tab 缩进 / 行尾杂字符等）时，
    /// 会把【整个文件】静默回退到默认值且控制台无任何报错——鉴权失败、接口"未启用"
    /// 却找不到原因基本都是这个。这里把 YamlDotNet 的精确错误（含行号）暴露出来。
    /// </summary>
    private void ValidateConfigFileIntegrity()
    {
        try
        {
            string? path = ConfigurationLoader.GetConfigPath(this, ConfigFileName);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return; // 尚未生成配置文件（首次启动），属正常

            string text = File.ReadAllText(path);
            try
            {
                YamlConfigParser.Deserializer.Deserialize<Config>(text);
            }
            catch (Exception ex)
            {
                Log.Error(
                    "[SLDataAPI] 配置文件解析失败！LabAPI 已静默回退到全部默认值（当前正以默认配置运行，" +
                    $"鉴权必然失败）。请修正配置文件后重启服务器。错误位置：{ex.Message.Replace("\n", " ")}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI] 配置自检异常（忽略）: {ex.Message}");
        }
    }

    private void OnWaitingForPlayers()
    {
        DataCollector.IsRoundActive = false;
        DataCollector.UpdateDataNow();
        MapLayoutService.Clear(); // 上一回合的地图布局失效
        MapLayoutService.CaptureLayout(); // 等待玩家时地图已生成，立即采集（失败自动重试）
        Log.Info("SLDataAPI: Waiting for players.");
    }

    private void OnRoundStarted()
    {
        DataCollector.IsRoundActive = true;
        DataCollector.UpdateDataNow();
        MapLayoutService.CaptureLayout(); // 采集本回合随机布局（LCZ/HCZ 每回合不同）
        VoiceRecorder.BeginRound(); // 语音录音取证：本局开始
        Log.Info("SLDataAPI: Round started.");
    }

    private void OnRoundEnded(RoundEndedEventArgs ev)
    {
        DataCollector.IsRoundActive = false;
        DataCollector.UpdateDataNow();
        VoiceRecorder.EndRound(); // 语音录音取证：定稿本局录音
        Log.Info("SLDataAPI: Round ended.");
    }
}
