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
    public static bool IsRoundActive = false;

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

        var (nukeStatus, nukeCountdown) = MainThreadExecutor.RunOnMainThread(
            () => (GetNukeStatus(), GetNukeCountdown()), out var err);
        if (err == null)
        {
            resp.nuke_status = nukeStatus;
            resp.nuke_countdown = nukeCountdown;
        }

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
        d_count = src.d_count,
        foundation_count = src.foundation_count,
        scp_count = src.scp_count,
        spectator_count = src.spectator_count,
        ping = src.ping,
        players = src.players,
        dntof_plugins = src.dntof_plugins,
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
            fresh.players_count = players.Count;
            fresh.max_players = Server.MaxPlayers;
            fresh.round_started = Round.IsRoundStarted;
            fresh.round_duration = Round.IsRoundStarted ? (int)Round.Duration.TotalSeconds : 0;
            fresh.current_phase = Round.IsRoundStarted ? "进行中" : "等待开始";

            // ★ 修复：Warhead 在地图加载前是 null，用 try-catch 单独保护
            fresh.nuke_status = GetNukeStatus();
            fresh.nuke_countdown = GetNukeCountdown();

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
                    role = GetRoleCN(p.Role),
                    team = GetTeamCN(p.Team),
                    x = p.Position.x,
                    y = p.Position.y,
                    z = p.Position.z
                })
                .ToList();

            // ★ 新增：探测 DNT_OF 系列插件（SLPlayer / OmegaWarhead）。
            //   必须放在这里（MEC 协程 = 主线程），不能放进 BuildJson()——
            //   BuildJson() 是被 HttpServer 的后台线程直接调用的，
            //   在后台线程里访问 Player.Position 等游戏对象存在线程安全风险。
            fresh.dntof_plugins = DntofDetector.Collect();
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

    // ★ 修复：地图加载前 Warhead 对象未初始化，整个方法加保护
    private static string GetNukeStatus()
    {
        try
        {
            if (Warhead.IsDetonated) return "已爆炸";
            if (Warhead.IsDetonationInProgress) return $"倒计时:{(int)Warhead.DetonationTime}秒";
        }
        catch { }
        return "未激活";
    }

    private static int GetNukeCountdown()
    {
        try { return Warhead.IsDetonationInProgress ? (int)Warhead.DetonationTime : 0; }
        catch { return 0; }
    }

    private static string Sanitize(string name) =>
        (name ?? "").Replace("\n", "").Replace("\r", "").Trim();

    private static string GetRoleCN(RoleTypeId role) => role switch
    {
        RoleTypeId.ClassD => "D级人员",
        RoleTypeId.Scientist => "科学家",
        RoleTypeId.FacilityGuard => "保安",
        RoleTypeId.NtfPrivate => "九尾狐-士兵",
        RoleTypeId.NtfSergeant => "九尾狐-中士",
        RoleTypeId.NtfCaptain => "九尾狐-指挥官",
        RoleTypeId.ChaosConscript => "混沌-征召兵",
        RoleTypeId.ChaosRifleman => "混沌-步枪手",
        RoleTypeId.ChaosMarauder => "混沌-掠夺者",
        RoleTypeId.ChaosRepressor => "混沌-镇压者",
        RoleTypeId.Scp173 => "SCP-173",
        RoleTypeId.Scp049 => "SCP-049",
        RoleTypeId.Scp096 => "SCP-096",
        RoleTypeId.Scp106 => "SCP-106",
        RoleTypeId.Scp939 => "SCP-939",
        RoleTypeId.Scp079 => "SCP-079",
        RoleTypeId.Scp3114 => "SCP-3114",
        RoleTypeId.Scp0492 => "SCP-049-2",
        RoleTypeId.Spectator => "观察者",
        RoleTypeId.Tutorial => "教程",
        RoleTypeId.None => "未分配",

        // ★ 诊断用：如果仍然出现未知职业，HTTP 返回值会显示 "未知(数字)"
        // 把这个数字告诉开发者，就能知道是哪个 RoleTypeId 枚举值缺失了
        _ => $"未知({(int)role})"
    };

    private static string GetTeamCN(Team team) => team switch
    {
        Team.SCPs => "SCP",
        Team.FoundationForces => "基金会",
        Team.ClassD => "D级",
        Team.ChaosInsurgency => "混沌",
        Team.Dead => "观察者",
        Team.OtherAlive => "其他", // ★ 修复1：Tutorial 等非标准职业会落入此枚举值
        _ => "未知"
    };
}
