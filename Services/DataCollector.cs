// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles;
using SLDataAPI.Data;
using SLDataAPI.Integrations;
using Player = LabApi.Features.Wrappers.Player;

namespace SLDataAPI.Services;

public static class DataCollector
{
    public static volatile ServerData CachedData = new ServerData(); // volatile：快照引用原子替换，主线程单写、HTTP 线程只读引用，无字段撕裂

    private static CoroutineHandle _handle;

    private static readonly Dictionary<Team, int> TeamOrder = new()
    {
        { Team.SCPs, 0 },
        { Team.FoundationForces, 1 },
        { Team.ClassD, 2 },
        { Team.ChaosInsurgency, 2 },
        { Team.Dead, 3 }
    };

    // ★ 修复：插件启用时调用此方法，立刻采集一次并启动循环
    public static void InitData(int intervalSeconds)
    {
        UpdateData(); // 立即填充真实数据
        StartTimer(intervalSeconds);
    }

    // ★ 修复：定时器始终运行，不再依赖 IsRoundActive 作为循环条件
    public static void StartTimer(int intervalSeconds)
    {
        StopTimer();
        _handle = Timing.RunCoroutine(UpdateLoop(intervalSeconds));
    }

    public static void StopTimer()
    {
        if (_handle.IsRunning)
            Timing.KillCoroutines(_handle);
    }

    // ★ 修复：while(true) 替代 while(IsRoundActive)，保证无论回合状态都持续更新
    private static IEnumerator<float> UpdateLoop(int intervalSeconds)
    {
        while (true)
        {
            yield return Timing.WaitForSeconds(intervalSeconds);
            UpdateData();
        }
    }

    // ★ 新增：允许外部（事件处理器）触发立即更新
    public static void UpdateDataNow() => UpdateData();

    // ★ 修复2：核弹倒计时实时读取，不用缓存值（避免最多差 8 秒的误差）。
    //   BuildJson 只做只读快照 + 主线程派发读核弹（Warhead 是 Unity 状态，
    //   HTTP 线程直接读取违反线程模型；派发失败则回落快照中的缓存值）
    public static string BuildJson()
    {
        var snap = CachedData ?? new ServerData();
        var resp = Clone(snap);

        // 核弹实时读取：仅当回合进行中才派发主线程（快照的 round_started 由主线程写入，
        // HTTP 线程只读纯数据字段）；回合未开始/结束时核弹必然"未激活"，直接用快照值，
        // 避免高频轮询时每次请求都做一次主线程派发
        if (resp.round_started)
        {
            var (nukeStatus, nukeCode, nukeCountdown) = MainThreadExecutor.RunOnMainThread(
                () => (GetNukeStatus(), GetWarheadCode(), GetNukeCountdown()), out var err);
            if (err == null)
            {
                resp.nuke_status = nukeStatus;
                resp.warhead_code = nukeCode;
                resp.nuke_countdown = nukeCountdown;
            }
        }

        resp.lang = Localization.Language;
        return Newtonsoft.Json.JsonConvert.SerializeObject(resp);
    }

    /// <summary>浅拷贝快照（players 列表只读共享，序列化不修改）。</summary>
    private static ServerData Clone(ServerData src) => new ServerData
    {
        success = src.success,
        server_name = src.server_name,
        online = src.online,
        players_count = src.players_count,
        max_players = src.max_players,
        round_started = src.round_started,
        round_duration = src.round_duration,
        current_phase = src.current_phase,
        nuke_status = src.nuke_status,
        nuke_countdown = src.nuke_countdown,
        voice_port = src.voice_port,
        game_version = src.game_version,
        active_holiday = src.active_holiday,
        d_count = src.d_count,
        foundation_count = src.foundation_count,
        scp_count = src.scp_count,
        spectator_count = src.spectator_count,
        ping = src.ping,
        players = src.players,
        dntof_plugins = src.dntof_plugins,
        adapted_plugins = src.adapted_plugins,
        phase_code = src.phase_code,
        warhead_code = src.warhead_code,
        lang = src.lang,
    };

    private static void UpdateData()
    {
        try
        {
            var players = Player.List?.ToList() ?? new List<Player>();

            // ★ 构建完整快照后原子替换引用：主线程单写，HTTP 线程只读引用，
            //   players_count 与 players 列表等字段永不失配（消除 M-01 字段撕裂）
            var fresh = new ServerData();

            fresh.success = true;
            fresh.server_name = ServerConsole.ServerName ?? "Unknown";
            fresh.online = true;
            fresh.voice_port = Plugin.Instance?.Config != null && Plugin.Instance.Config.VoiceEnabled
                ? Plugin.Instance.Config.VoicePort
                : 0;
            fresh.game_version = HolidayFeatures.GameVersion();
            fresh.active_holiday = HolidayFeatures.ActiveHoliday();
            fresh.players_count = players.Count;
            fresh.max_players = Server.MaxPlayers;
            fresh.round_started = Round.IsRoundStarted;
            fresh.round_duration = Round.IsRoundStarted ? (int)Round.Duration.TotalSeconds : 0;
            bool roundEnded = !Round.IsRoundStarted && Round.IsRoundEnded;
            fresh.current_phase = Localization.PhaseText(Round.IsRoundStarted, roundEnded);
            fresh.phase_code = Localization.PhaseCode(Round.IsRoundStarted, roundEnded);

            // ★ 修复：Warhead 在地图加载前是 null，用 try-catch 单独保护
            fresh.nuke_status = GetNukeStatus();
            fresh.warhead_code = GetWarheadCode();
            fresh.nuke_countdown = GetNukeCountdown();

            fresh.lang = Localization.Language;

            // ★ 修复：排除 Dummy/NPC/主机 玩家（手动添加的 dummy 会混入 Player.List 导致数据污染）
            var realPlayers = players.Where(p => p != null && !p.IsNpc && !p.IsHost).ToList();

            fresh.players_count = realPlayers.Count;
            fresh.d_count = realPlayers.Count(p => p.Team == Team.ClassD || p.Team == Team.ChaosInsurgency);
            fresh.foundation_count = realPlayers.Count(p => p.Team == Team.FoundationForces);
            fresh.scp_count = realPlayers.Count(p => p.Team == Team.SCPs);
            fresh.spectator_count = realPlayers.Count(p => p.Team == Team.Dead);

            fresh.ping = realPlayers.Any() ? (int)realPlayers.Average(GetPing) : 0;

            // ★ 诊断：ping 长期反馈为 0 时，打开配置里的 debug 开关，
            //   重启服务器后在控制台核对这里打印出的每个玩家原始 Ping 值。
            //   如果这里打出来的原始值本身就是 0，说明问题在 Mirror 连接层的
            //   rtt 上游数据，不在本插件的计算逻辑里；
            //   如果这里打出来的是非 0 正常值，但最终 JSON 里 ping 还是 0，
            //   那问题出在别处，请把这段日志发给开发者进一步排查。
            if (Plugin.Instance?.Config.Debug == true && realPlayers.Count > 0)
            {
                string pingDump = string.Join(", ", realPlayers.Select(p => $"{p.Nickname}={GetPing(p)}ms"));
                Log.Debug($"[SLDataAPI] Ping 原始值采样: {pingDump}");
            }

            fresh.players = realPlayers
                .Where(p => p.Role != RoleTypeId.None) // 排除尚未分配职业的玩家
                .OrderBy(p => TeamOrder.ContainsKey(p.Team) ? TeamOrder[p.Team] : 99)
                .ThenBy(p => p.Role.ToString())
                .Select(p => new PlayerInfo
                {
                    nickname = Sanitize(p.Nickname),
                    steam_id = p.UserId ?? "",
                    role = GetRoleText(p.Role),
                    team = GetTeamText(p.Team),
                    x = p.Position.x,
                    y = p.Position.y,
                    z = p.Position.z,
                    role_code = p.Role.ToString(),
                    team_code = p.Team.ToString()
                })
                .ToList();

            // ★ 新增：探测 DNT_OF 系列插件（SLPlayer / OmegaWarhead）。
            //   必须放在这里（MEC 协程 = 主线程），不能放进 BuildJson()——
            //   BuildJson() 是被 HttpServer 的后台线程直接调用的，
            //   在后台线程里访问 Player.Position 等游戏对象存在线程安全风险。
            fresh.dntof_plugins = DntofDetector.Collect();

            // Adapted plugin registry: status callbacks only on main thread (per-plugin try/catch + 4KB).
            // Discovery lists ALL registrations — no third-party allowlist.
            fresh.adapted_plugins = PluginEndpointRegistry.CollectStatusesOnMainThread();

            CachedData = fresh; // ★ F-01：快照构建完毕，原子替换发布（volatile 保证可见性）——缺失会导致 /get_sl_data 永远空数据
        }
        catch (System.Exception ex)
        {
            Log.Debug($"[SLDataAPI] UpdateData error (harmless during map load): {ex.Message}");
        }
    }

    /// <summary>单个玩家的往返延迟（ms）。取自 Mirror 连接层 rtt，连接缺失时返回 0。</summary>
    private static int GetPing(Player p)
    {
        try
        {
            var conn = p.ReferenceHub.networkIdentity.connectionToClient as Mirror.NetworkConnectionToClient;
            return conn == null ? 0 : (int)Math.Round(conn.rtt);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>核弹状态（地图加载前 Warhead 对象未初始化，读取失败按未激活处理）。</summary>
    private static (bool Detonated, bool Detonating, bool Armed, int Countdown) GetWarheadState()
    {
        try
        {
            if (Warhead.IsDetonated) return (true, false, false, 0);
            if (Warhead.IsDetonationInProgress)
                return (false, true, true, (int)Warhead.DetonationTime);
            // LeverStatus 为 bool 时表示拉杆已拉起（armed）；读取失败按未激活处理。
            bool armed = Warhead.LeverStatus is bool b && b;
            return (false, false, armed, 0);
        }
        catch
        {
            // 地图加载前 / Warhead 未初始化：按未激活处理
        }
        return (false, false, false, 0);
    }

    private static string GetNukeStatus()
    {
        var s = GetWarheadState();
        return Localization.WarheadText(s.Detonated, s.Detonating, s.Countdown, s.Armed);
    }

    private static string GetWarheadCode()
    {
        var s = GetWarheadState();
        return Localization.WarheadCode(s.Detonated, s.Detonating, s.Armed);
    }

    private static int GetNukeCountdown()
    {
        var s = GetWarheadState();
        return s.Detonating ? s.Countdown : 0;
    }

    private static string Sanitize(string name) =>
        (name ?? "").Replace("\n", "").Replace("\r", "").Trim();

    // ★ 映射按枚举成员名查表（Localization），不依赖枚举数值；
    //   未知职业仍保留改造前的 "未知(数字)" 兜底，便于定位缺失的 RoleTypeId。
    private static string GetRoleText(RoleTypeId role) =>
        Localization.DataRoleText(role.ToString(), (int)role);

    private static string GetTeamText(Team team) =>
        Localization.TeamText(team.ToString());
}
