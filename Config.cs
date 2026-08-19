namespace SLDataAPI;

/// <summary>
/// 插件配置。LabAPI 会在 Enable() 前自动加载：
///   %AppData%\SCP Secret Laboratory\LabAPI\configs\&lt;端口或 global&gt;\SLDataAPI\config.yml
/// 插件本身的启停由 LabAPI 的 properties.yml 管理（/control/plugins 端点可代写），这里不再提供 IsEnabled。
/// </summary>
public class Config
{
    public bool Debug { get; set; } = false;
    public string VerifyToken { get; set; } = "your_secret_token";
    public int HttpPort { get; set; } = 8081;
    public int PushIntervalSeconds { get; set; } = 8;

    // ================== 控制接口（v2.1 新增） ==================

    /// <summary>
    /// 是否启用控制接口（/control/*）。默认关闭。
    /// 关闭时，无论 ControlToken 是否配置，所有 /control/* 请求一律 404。
    /// </summary>
    public bool ControlEnabled { get; set; } = false;

    /// <summary>
    /// 控制接口专用 token（与 VerifyToken 分离，权限更高，务必单独保管）。
    /// 要求：长度不少于 8，且必须同时包含大写字母、小写字母、数字、特殊符号。
    /// 启动时格式不合法会强制在本次运行中禁用控制接口，并在日志中报错。
    /// </summary>
    public string ControlToken { get; set; } = "";

    /// <summary>
    /// 控制接口的传输方式（二选一，硬互斥）：
    /// - "http"（默认）：仅 HTTP POST /control/*（WS 握手被拒，带协商信号）。
    /// - "ws"：仅 WebSocket 长连接（HTTP /control/* 返回 404，带协商信号）。
    /// 设计考量：不设双通道默认值——选了 ws 就不该留着 HTTP 刷包面，选了 http 就不开放 WS；
    /// 始终只有一条控制通路。WS 升级地址为 /control（或别名 /ws/control），
    /// call 信封里的 path 仍是 /control/*。
    /// 只读数据接口 /get_sl_data 不受影响（始终走 HTTP）。非法值启动时按 http 处理并在日志警告。
    /// </summary>
    public string ControlTransport { get; set; } = "http";

    /// <summary>
    /// 是否在插件启用时自动检查 GitHub Releases 上的新版本（仅日志提示，不自动更新）。
    /// </summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>
    /// 检测到新版本时是否自动下载并替换插件 DLL（覆盖后下次重启游戏服务器生效，旧版备份为 .bak）。
    /// 校验：下载文件必须是合法程序集、名称一致；当前版本已强名称签名时还要求签名一致（防篡改）。
    /// 关闭时仅日志提示，需手动更新。
    /// </summary>
    public bool AutoUpdateInstall { get; set; } = true;

    /// <summary>
    /// 文件管理端点（/control/files/*）的根目录（绝对路径）。
    /// 留空 = 禁用文件端点（默认）。建议指向服务器的 SCPSL_Data 目录或某个只读配置目录；
    /// 所有文件操作都会被限制在该目录内（防路径穿越）。
    /// </summary>
    public string FileRoot { get; set; } = "";

    /// <summary>
    /// 服务器日志目录（/control/logs 读取用）。留空 = 自动探测，
    /// 探测顺序：%AppData%/SCP Secret Laboratory/ServerLogs（含端口子目录）→ SCPSL_Data/Logs。
    /// </summary>
    public string LogDirectory { get; set; } = "";

    // ================== 语音转发（v2.3 新增） ==================

    /// <summary>
    /// 是否启用游戏内语音转发（WebSocket 实时语音流）。默认关闭。
    /// 启用时 WebUI 可实时收听服务器内所有语音（近距离/对讲机/Intercom 等全部频道）。
    /// </summary>
    public bool VoiceEnabled { get; set; } = false;

    /// <summary>
    /// 语音转发 WebSocket 服务的监听端口（独立于 HttpPort，默认 8082）。
    /// </summary>
    public int VoicePort { get; set; } = 8082;

    // ================== 语音录音取证（v2.5 / Bay of Pigs Invasion 新增） ==================

    /// <summary>
    /// 是否自动保存每局游戏的语音录音：一条混合音轨（WAV 48kHz/16bit/单声道）
    /// + 一份时间轴日志（谁在什么时候说了多久，含 steamid/角色/频道）。
    /// 用于游戏不公平问题的取证。需要 voice_enabled=true（复用语音解码管线）。
    /// </summary>
    public bool VoiceRecordEnabled { get; set; } = false;

    /// <summary>
    /// 最多保留多少局游戏的录音（按最近时间排序，超出自动删除最旧的 wav+时间轴）。
    /// 0 或负数 = 不清理（注意磁盘占用）。参考占用：约 5.5MB/分钟/局。
    /// </summary>
    public int VoiceRecordMaxRounds { get; set; } = 10;

    /// <summary>
    /// 录音保存目录（绝对路径）。留空 = 默认
    /// %AppData%/SCP Secret Laboratory/SLDataAPI/VoiceRecords。
    /// </summary>
    public string VoiceRecordDir { get; set; } = "";
}
