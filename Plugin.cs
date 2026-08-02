using System;
using Exiled.API.Features;
using Exiled.Events.EventArgs.Server;

public class Plugin : Plugin<Config>
{
    public static Plugin? Instance;
    private HttpServer? server;

    public override string Name => "SLDataAPI";
    public override string Author => "DNT_OF";
    public override Version Version => new Version(2, 1, 0);

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

        server = new HttpServer(Config.HttpPort, Config);
        server.Start();

        // 插件启用时立即采集一次真实数据，并启动定时循环
        DataCollector.IsRoundActive = Round.IsStarted;
        DataCollector.InitData(Config.PushIntervalSeconds);

        if (Config.AutoUpdateCheck)
            UpdateChecker.CheckAsync(Version);

        Log.Info($"SLDataAPI v{Version} enabled. HTTP on port {Config.HttpPort}. Control API: {(Config.ControlEnabled ? "启用" : "关闭")}.");
    }

    public override void OnDisabled()
    {
        base.OnDisabled();

        Exiled.Events.Handlers.Server.WaitingForPlayers -= OnWaitingForPlayers;
        Exiled.Events.Handlers.Server.RoundStarted    -= OnRoundStarted;
        Exiled.Events.Handlers.Server.RoundEnded      -= OnRoundEnded;

        server?.Stop();
        DataCollector.StopTimer();
        CommandOutputCapture.Shutdown();

        Instance = null;
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
