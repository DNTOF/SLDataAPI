using System;
using Exiled.API.Features;
using Exiled.Events.EventArgs.Server;

public class Plugin : Plugin<Config>
{
    public static Plugin? Instance;
    private HttpServer? server;

    public override string Name => "SLDataAPI";
    public override string Author => "DNT_OF";
    public override Version Version => new Version(1, 0, 0);

    public override void OnEnabled()
    {
        base.OnEnabled();

        Instance = this;

        Exiled.Events.Handlers.Server.WaitingForPlayers += OnWaitingForPlayers;
        Exiled.Events.Handlers.Server.RoundStarted    += OnRoundStarted;
        Exiled.Events.Handlers.Server.RoundEnded      += OnRoundEnded;

        server = new HttpServer(Config.HttpPort, Config.VerifyToken);
        server.Start();

        // ★ 修复：插件启用时立即采集一次真实数据，并启动定时循环
        // 不再依赖 RoundStarted 事件触发——加载时若回合已在进行中同样能正常工作
        DataCollector.IsRoundActive = Round.IsStarted;
        DataCollector.InitData(Config.PushIntervalSeconds);

        Log.Info($"SLDataAPI v{Version} enabled. HTTP on port {Config.HttpPort}.");
    }

    public override void OnDisabled()
    {
        base.OnDisabled();

        Exiled.Events.Handlers.Server.WaitingForPlayers -= OnWaitingForPlayers;
        Exiled.Events.Handlers.Server.RoundStarted    -= OnRoundStarted;
        Exiled.Events.Handlers.Server.RoundEnded      -= OnRoundEnded;

        server?.Stop();
        DataCollector.StopTimer();

        Instance = null;
    }

    // ★ 新增：等待玩家阶段也更新数据（大厅状态）
    private void OnWaitingForPlayers()
    {
        DataCollector.IsRoundActive = false;
        DataCollector.UpdateDataNow();
        Log.Info("SLDataAPI: Waiting for players.");
    }

    private void OnRoundStarted()
    {
        DataCollector.IsRoundActive = true;
        DataCollector.UpdateDataNow();
        Log.Info("SLDataAPI: Round started.");
    }

    private void OnRoundEnded(RoundEndedEventArgs ev)
    {
        DataCollector.IsRoundActive = false;
        DataCollector.UpdateDataNow();
        Log.Info("SLDataAPI: Round ended.");
    }
}
