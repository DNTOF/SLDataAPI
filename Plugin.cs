using System;
using Exiled.API.Features;
using Exiled.Events.EventArgs.Server;
using Exiled.Events.EventArgs.Player;

public class Plugin : Plugin<Config>
{
    public static Plugin? Instance;
    private HttpServer? server;

    public override string Name => "SLDataAPI";
    public override string Author => "DNT_OF";
    public override Version Version => new Version(2, 3, 0);

    public override void OnEnabled()
    {
        base.OnEnabled();

        Instance = this;

        Exiled.Events.Handlers.Server.WaitingForPlayers += OnWaitingForPlayers;
        Exiled.Events.Handlers.Server.RoundStarted    += OnRoundStarted;
        Exiled.Events.Handlers.Server.RoundEnded      += OnRoundEnded;

        // 命令输出捕获（patch ServerConsole.AddLog）
        CommandOutputCapture.Init();

        ValidateControlConfig();

        // 安全提示：VerifyToken 仍是出厂默认值时，数据接口相当于裸奔
        if (string.Equals(Config.VerifyToken, "your_secret_token", StringComparison.Ordinal))
            Log.Warn("[SLDataAPI] VerifyToken 仍为出厂默认值 your_secret_token，请尽快修改为强随机值！");

        server = new HttpServer(Config.HttpPort, Config);
        server.Start();

        // 语音转发（v2.3）：独立 WebSocket 端口，ControlToken 鉴权
        if (Config.VoiceEnabled)
        {
            Exiled.Events.Handlers.Player.VoiceChatting += OnVoiceChatting;
            Exiled.Events.Handlers.Player.Transmitting  += OnTransmitting;
            VoiceService.Start(Config.VoicePort);
        }

        // 插件启用时立即采集一次真实数据，并启动定时循环
        DataCollector.IsRoundActive = Round.IsStarted;
        DataCollector.InitData(Config.PushIntervalSeconds);

        if (Config.AutoUpdateCheck)
            UpdateChecker.CheckAsync(Version, Config.AutoUpdateInstall);

        Log.Info($"SLDataAPI v{Version} enabled. HTTP on port {Config.HttpPort}. Control API: {(Config.ControlEnabled ? "启用" : "关闭")}. Voice: {(Config.VoiceEnabled ? $"启用(端口 {Config.VoicePort})" : "关闭")}.");
    }

    public override void OnDisabled()
    {
        base.OnDisabled();

        Exiled.Events.Handlers.Server.WaitingForPlayers -= OnWaitingForPlayers;
        Exiled.Events.Handlers.Server.RoundStarted    -= OnRoundStarted;
        Exiled.Events.Handlers.Server.RoundEnded      -= OnRoundEnded;
        Exiled.Events.Handlers.Player.VoiceChatting   -= OnVoiceChatting;
        Exiled.Events.Handlers.Player.Transmitting    -= OnTransmitting;

        VoiceService.Stop();
        server?.Stop();
        DataCollector.StopTimer();
        CommandOutputCapture.Shutdown();

        Instance = null;
    }

    private void OnVoiceChatting(VoiceChattingEventArgs ev)
    {
        if (ev.IsAllowed) VoiceService.HandleIncoming(ev.Player, ev.VoiceMessage);
    }

    private void OnTransmitting(TransmittingEventArgs ev)
    {
        if (ev.IsAllowed) VoiceService.HandleIncoming(ev.Player, ev.VoiceMessage);
    }

    /// <summary>
    /// 启动时校验控制接口 token 格式。格式不合法则强制在本次运行中禁用控制接口，
    /// 而不是带着一个不合规（弱）的 token 裸奔——这只影响运行时状态，不会改写配置文件。
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
        Log.Info("SLDataAPI: Round started.");
    }

    private void OnRoundEnded(RoundEndedEventArgs ev)
    {
        DataCollector.IsRoundActive = false;
        DataCollector.UpdateDataNow();
        Log.Info("SLDataAPI: Round ended.");
    }
}
