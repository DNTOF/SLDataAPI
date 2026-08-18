using System.Collections.Generic;

namespace SLDataAPI.Data;

public class ControlResponse
{
    public bool success { get; set; }
    public string message { get; set; } = "";
    public object? data { get; set; }
}

/// <summary>POST /control/command</summary>
public class CommandRequest
{
    /// <summary>原始服务器控制台命令，等价于本机控制台权限执行。</summary>
    public string command { get; set; } = "";
}

/// <summary>POST /control/player/{kick|ban|role|teleport}</summary>
public class PlayerActionRequest
{
    /// <summary>玩家标识，支持 PlayerId / UserId / IP / 昵称模糊匹配（交给 Player.Get 解析）。</summary>
    public string target { get; set; } = "";

    /// <summary>kick / ban 用的理由。</summary>
    public string reason { get; set; } = "";

    /// <summary>ban 用，单位：分钟，0 表示永久。</summary>
    public int duration { get; set; } = 0;

    /// <summary>role 用，RoleTypeId 的字符串名（如 "Scp173"、"ClassD"）。</summary>
    public string role { get; set; } = "";

    /// <summary>teleport 用，世界坐标。</summary>
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }

    /// <summary>mute 用：true=语音禁言，false=解除。</summary>
    public bool? mute { get; set; }

    /// <summary>msg 用：消息内容。</summary>
    public string message { get; set; } = "";

    /// <summary>msg 用："hint"（屏幕下方提示）或 "broadcast"（屏幕中央广播），默认 hint。</summary>
    public string msg_type { get; set; } = "hint";

    /// <summary>msg 用：显示时长（秒），默认 5。</summary>
    public float duration_seconds { get; set; } = 5f;

    /// <summary>effect 用：EffectType 枚举名（如 Invigorated / Flashed）。</summary>
    public string effect { get; set; } = "";

    /// <summary>effect 用：效果时长（秒），默认 10。</summary>
    public float effect_duration { get; set; } = 10f;

    /// <summary>state 用：无敌模式（true/false）。</summary>
    public bool? godmode { get; set; }

    /// <summary>state 用：绕过权限（true/false）。</summary>
    public bool? bypass { get; set; }

    /// <summary>state 用：设置生命值（0 起）。</summary>
    public float? health { get; set; }

    /// <summary>state 用：强制玩家使用对讲机频道（true=切到 Intercom，false=切回近距语音）。</summary>
    public bool? intercom { get; set; }
}

/// <summary>POST /control/round，action: restart | end | start</summary>
public class RoundActionRequest
{
    public string action { get; set; } = "";
}

/// <summary>POST /control/cassie</summary>
public class CassieRequest
{
    public string message { get; set; } = "";
    public bool isHeld { get; set; } = false;
    public bool isNoisy { get; set; } = true;
    public bool isSubtitles { get; set; } = false;

    /// <summary>
    /// 字幕翻译文本（可选）：提供时语音仍播报 message 原文（含音效代码），
    /// 游戏内字幕显示 translation（纯文本，不解析音效代码）。空 = 现有行为。
    /// </summary>
    public string translation { get; set; } = "";
}

/// <summary>POST /control/warhead，action: start | stop | detonate</summary>
public class WarheadActionRequest
{
    public string action { get; set; } = "";
}

/// <summary>POST /control/slplayer —— 直接控制 SLPlayer_GUI 音乐播放（不经过控制台命令）。</summary>
public class SlPlayerRequest
{
    /// <summary>
    /// 操作：status（完整状态+播放列表）| list | play | next | stop |
    /// volume | shuffle | reload | fetch。
    /// </summary>
    public string action { get; set; } = "";

    /// <summary>play 用：歌曲序号（播放列表中的 index）。</summary>
    public int index { get; set; } = -1;

    /// <summary>volume 用：0-100。</summary>
    public int volume { get; set; } = -1;

    /// <summary>shuffle 用："on" / "off" / "toggle"（默认 toggle）。</summary>
    public string shuffle { get; set; } = "toggle";

    /// <summary>fetch 用：YAML 歌单直链（http/https）。</summary>
    public string url { get; set; } = "";
}

/// <summary>POST /control/ban/revoke —— 解除封禁。</summary>
public class BanRevokeRequest
{
    /// <summary>被封禁者的 Id（Steam UserId 形如 76561198XXXXXXXXX@steam，或 IP 地址）。</summary>
    public string user_id { get; set; } = "";

    /// <summary>封禁类型："steam"（默认）或 "ip"。</summary>
    public string ban_type { get; set; } = "steam";

    /// <summary>解除原因（会记入封禁记录）。</summary>
    public string reason { get; set; } = "";
}

/// <summary>POST /control/ban/add —— 新增封禁（支持离线玩家，按 UserId 或 IP）。</summary>
public class BanAddRequest
{
    /// <summary>被封禁者 Id：Steam UserId（形如 76561198XXXXXXXXX@steam）或 IP 地址。</summary>
    public string user_id { get; set; } = "";

    /// <summary>可选：被封禁者的显示名（会写入封禁记录的 OriginalName）。</summary>
    public string original_name { get; set; } = "";

    /// <summary>封禁理由。</summary>
    public string reason { get; set; } = "";

    /// <summary>封禁时长（分钟），0 = 永久。默认 0。</summary>
    public int duration { get; set; } = 0;

    /// <summary>封禁类型："steam"（默认）或 "ip"。</summary>
    public string ban_type { get; set; } = "steam";
}

/// <summary>POST /control/logs —— 读取服务器日志尾部。</summary>
public class LogsRequest
{
    /// <summary>"list" = 列出所有可用日志文件；空 = 尾部读取。</summary>
    public string action { get; set; } = "";

    /// <summary>可选：指定要读取的日志文件绝对路径（必须位于服务器日志目录内且为 .log/.txt）。</summary>
    public string path { get; set; } = "";

    /// <summary>读取的末尾行数，默认 200，上限 2000。</summary>
    public int lines { get; set; } = 200;

    /// <summary>可选：仅返回包含该关键词的行（大小写不敏感）。</summary>
    public string filter { get; set; } = "";
}

/// <summary>POST /control/plugins —— 插件列表 / 重载 / 启停。</summary>
public class PluginsRequest
{
    /// <summary>空 = 列表；"reload" = 重载全部插件；"set" = 启用/禁用单个插件。</summary>
    public string action { get; set; } = "";

    /// <summary>set 用：插件名（Name，大小写不敏感）。</summary>
    public string name { get; set; } = "";

    /// <summary>set 用：true=启用，false=禁用（写入插件配置的 is_enabled 后全局重载）。</summary>
    public bool enabled { get; set; }
}

/// <summary>POST /control/files/list|read|write —— 文件管理。</summary>
public class FilesRequest
{
    /// <summary>相对 FileRoot 的路径；空 = 根目录。list/read/write 通用。</summary>
    public string path { get; set; } = "";

    /// <summary>write 用：要写入的内容。</summary>
    public string content { get; set; } = "";
}

/// <summary>POST /control/map —— 地图布局与控制。</summary>
public class MapControlRequest
{
    /// <summary>
    /// 操作：layout（房间布局+玩家位置）| doors（门控制）| lights（灯控制）。
    /// </summary>
    public string action { get; set; } = "";

    /// <summary>doors 用：DoorType 枚举名（如 GateA / CheckpointLczA）。</summary>
    public string door_type { get; set; } = "";

    /// <summary>doors 用：true=锁定，false=解锁，null=不改变锁定状态。</summary>
    public bool? lock_door { get; set; }

    /// <summary>doors 用：true=开门，false=关门，null=不改变开关状态。</summary>
    public bool? open_door { get; set; }

    /// <summary>
    /// doors 用：操作范围。type（默认，按 door_type 单门）| all（全部已实例化的门）|
    /// all_not_list（除 DoorType 枚举能匹配到的门之外的所有门）。scope=all|all_not_list 时忽略 door_type。
    /// </summary>
    public string scope { get; set; } = "";

    /// <summary>elevators 用：电梯类型（旧名 Nuke/Scp049/GateA/GateB/LiftA/LiftB/LczA/LczB/ServerRoom，或当前 ElevatorGroup 名如 Nuke01/LczA01）。scope=all 时忽略。</summary>
    public string elevator_type { get; set; } = "";

    /// <summary>elevators 用：up（升一层）| down（降一层）| send（直达 level 目标楼层）。</summary>
    public string command { get; set; } = "";

    /// <summary>elevators 用，command=send 时必填：目标楼层（非负整数，0=最低层；不能超过该组电梯楼层数）。</summary>
    public int level { get; set; } = -1;

    /// <summary>lights 用：RoomType 枚举名（如 Hcz106 / Lcz173）。</summary>
    public string room_type { get; set; } = "";

    /// <summary>lights 用：true=关灯，false=恢复照明。</summary>
    public bool? lights_off { get; set; }

    /// <summary>lights 用：关灯持续秒数（默认 30）。</summary>
    public float duration { get; set; } = 30f;
}
