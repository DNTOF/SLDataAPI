using System.Collections.Generic;

namespace SLDataAPI.Data;

public class PlayerInfo
{
    public string nickname { get; set; } = "";
    public string steam_id { get; set; } = "";
    public string role { get; set; } = "";
    public string team { get; set; } = "";

    /// <summary>世界坐标（地图追踪用，只取平面 x/z + 高度 y）。</summary>
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
}

public class ServerData
{
    public bool success { get; set; } = true;
    public string server_name { get; set; } = "";
    public bool online { get; set; }
    public int players_count { get; set; }
    public int max_players { get; set; }
    public bool round_started { get; set; }
    public int round_duration { get; set; }
    public string current_phase { get; set; } = "";
    public string nuke_status { get; set; } = "";
    public int nuke_countdown { get; set; }

    /// <summary>语音转发端口（VoiceEnabled=true 时非 0；前端据此建立 WebSocket）。</summary>
    public int voice_port { get; set; }
    public int d_count { get; set; }
    public int foundation_count { get; set; }
    public int scp_count { get; set; }
    public int spectator_count { get; set; }
    public int ping { get; set; }
    public List<PlayerInfo> players { get; set; } = new();

    // ★ 新增：DNT_OF 系列插件（SLPlayer / OmegaWarhead）运行时信息。
    // 两个子字段均可能为 null（对应插件未加载时），由 DntofDetector 每个刷新周期采集一次。
    public DntofInfo dntof_plugins { get; set; } = new();
}

// ===================== DNT_OF 系列插件信息 =====================

public class DntofInfo
{
    /// <summary>SLPlayer 未加载时为 null。</summary>
    public SlPlayerInfo? sl_player { get; set; }

    /// <summary>OmegaWarhead 未加载时为 null。</summary>
    public OmegaWarheadInfo? omega_warhead { get; set; }
}

public class SlPlayerInfo
{
    public bool present { get; set; } = true;

    /// <summary>"local" 或 "remote"。</summary>
    public string source_mode { get; set; } = "local";

    /// <summary>source_mode 为 remote 时，最近一次 .music fetch 的歌单地址；未获取过时为 null。</summary>
    public string? remote_url { get; set; }

    /// <summary>当前正在播放的曲目显示名，未播放时为 null。</summary>
    public string? now_playing { get; set; }
}

public class CoinHolderInfo
{
    public string nickname { get; set; } = "";
    public int count { get; set; }
    public string position { get; set; } = "";
}

public class OmegaWarheadInfo
{
    public bool present { get; set; } = true;

    /// <summary>none / collecting / idle_holding / confirming / locked / counting / detonation。</summary>
    public string phase { get; set; } = "none";

    /// <summary>phase == collecting 时有意义：正在拾取放射性元素（硬币）的玩家列表。</summary>
    public List<CoinHolderInfo> coin_holders { get; set; } = new();

    /// <summary>phase 处于 idle_holding 及之后阶段时有意义：控制器持有人昵称；无人持有时为 null。</summary>
    public string? controller_holder { get; set; }

    /// <summary>phase == counting 时有意义：倒计时剩余秒数。</summary>
    public int? countdown { get; set; }
}
