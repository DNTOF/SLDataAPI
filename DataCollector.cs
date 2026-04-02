using Exiled.API.Features;
using MEC;
using PlayerRoles;
using System.Collections.Generic;
using System.Linq;

public static class DataCollector
{
    public static ServerData CachedData = new ServerData();
    public static bool IsRoundActive = false;
    private static CoroutineHandle _handle;

    private static readonly Dictionary<Team, int> TeamOrder = new()
    {
        { Team.SCPs,              0 },
        { Team.FoundationForces,  1 },
        { Team.ClassD,            2 },
        { Team.ChaosInsurgency,   2 },
        { Team.Dead,              3 }
    };

    // ★ 修复：插件启用时调用此方法，立刻采集一次并启动循环
    public static void InitData(int intervalSeconds)
    {
        UpdateData();           // 立即填充真实数据
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

    // ★ 修复2：核弹倒计时实时读取，不用缓存值（避免最多差 8 秒的误差）
    public static string BuildJson()
    {
        CachedData.nuke_status    = GetNukeStatus();
        CachedData.nuke_countdown = GetNukeCountdown();
        return Newtonsoft.Json.JsonConvert.SerializeObject(CachedData);
    }

    private static void UpdateData()
    {
        try
        {
            var players = Player.List?.ToList() ?? new List<Player>();

            CachedData.success        = true;
            CachedData.server_name    = Server.Name ?? "Unknown";
            CachedData.online         = true;
            CachedData.players_count  = players.Count;
            CachedData.max_players    = Server.MaxPlayerCount;
            CachedData.round_started  = Round.IsStarted;
            CachedData.round_duration = Round.IsStarted ? (int)Round.ElapsedTime.TotalSeconds : 0;
            CachedData.current_phase  = Round.IsStarted ? "进行中" : "等待开始";

            // ★ 修复：Warhead 在地图加载前是 null，用 try-catch 单独保护
            CachedData.nuke_status    = GetNukeStatus();
            CachedData.nuke_countdown = GetNukeCountdown();

            // ★ 修复：排除 Dummy/NPC 玩家（手动添加的 dummy 会混入 Player.List 导致数据污染）
            var realPlayers = players.Where(p => p != null && !p.IsNPC).ToList();

            CachedData.players_count    = realPlayers.Count;
            CachedData.d_count          = realPlayers.Count(p => p.Role.Team == Team.ClassD || p.Role.Team == Team.ChaosInsurgency);
            CachedData.foundation_count = realPlayers.Count(p => p.Role.Team == Team.FoundationForces);
            CachedData.scp_count        = realPlayers.Count(p => p.Role.Team == Team.SCPs);
            CachedData.spectator_count  = realPlayers.Count(p => p.Role.Team == Team.Dead);
            CachedData.ping             = realPlayers.Any() ? (int)realPlayers.Average(p => p.Ping) : 0;

            CachedData.players = realPlayers
                .Where(p => p.Role.Type != RoleTypeId.None)   // 排除尚未分配职业的玩家
                .OrderBy(p => TeamOrder.ContainsKey(p.Role.Team) ? TeamOrder[p.Role.Team] : 99)
                .ThenBy(p => p.Role.Type.ToString())
                .Select(p => new PlayerInfo
                {
                    nickname = Sanitize(p.Nickname),
                    role     = GetRoleCN(p.Role.Type),
                    team     = GetTeamCN(p.Role.Team)
                })
                .ToList();
        }
        catch (System.Exception ex)
        {
            Log.Debug($"[SLDataAPI] UpdateData error (harmless during map load): {ex.Message}");
        }
    }

    // ★ 修复：地图加载前 Warhead 对象未初始化，整个方法加保护
    private static string GetNukeStatus()
    {
        try
        {
            if (Warhead.IsDetonated)  return "已爆炸";
            if (Warhead.IsInProgress) return $"倒计时:{(int)Warhead.RealDetonationTimer}秒";
        }
        catch { }
        return "未激活";
    }

    private static int GetNukeCountdown()
    {
        try { return Warhead.IsInProgress ? (int)Warhead.RealDetonationTimer : 0; }
        catch { return 0; }
    }

    private static string Sanitize(string name) =>
        (name ?? "").Replace("\n", "").Replace("\r", "").Trim();

    private static string GetRoleCN(RoleTypeId role) => role switch
    {
        RoleTypeId.ClassD           => "D级人员",
        RoleTypeId.Scientist        => "科学家",
        RoleTypeId.FacilityGuard    => "保安",
        RoleTypeId.NtfPrivate       => "九尾狐-士兵",
        RoleTypeId.NtfSergeant      => "九尾狐-中士",
        RoleTypeId.NtfCaptain       => "九尾狐-指挥官",
        RoleTypeId.ChaosConscript   => "混沌-征召兵",
        RoleTypeId.ChaosRifleman    => "混沌-步枪手",
        RoleTypeId.ChaosMarauder    => "混沌-掠夺者",
        RoleTypeId.ChaosRepressor   => "混沌-镇压者",
        RoleTypeId.Scp173           => "SCP-173",
        RoleTypeId.Scp049           => "SCP-049",
        RoleTypeId.Scp096           => "SCP-096",
        RoleTypeId.Scp106           => "SCP-106",
        RoleTypeId.Scp939           => "SCP-939",
        RoleTypeId.Scp079           => "SCP-079",
        RoleTypeId.Scp3114          => "SCP-3114",
        RoleTypeId.Scp0492          => "SCP-049-2",
        RoleTypeId.Spectator        => "观察者",
        RoleTypeId.Tutorial         => "教程",
        RoleTypeId.None             => "未分配",
        // ★ 诊断用：如果仍然出现未知职业，HTTP 返回值会显示 "未知(数字)"
        // 把这个数字告诉开发者，就能知道是哪个 RoleTypeId 枚举值缺失了
        _                           => $"未知({(int)role})"
    };

    private static string GetTeamCN(Team team) => team switch
    {
        Team.SCPs             => "SCP",
        Team.FoundationForces => "基金会",
        Team.ClassD           => "D级",
        Team.ChaosInsurgency  => "混沌",
        Team.Dead             => "观察者",
        Team.OtherAlive       => "其他",   // ★ 修复1：Tutorial 等非标准职业会落入此枚举值
        _                     => "未知"
    };
}
