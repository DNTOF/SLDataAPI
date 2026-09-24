namespace SLDataAPI;

/// <summary>
/// 插件配置。LabAPI 会在 Enable() 前自动加载：
///   %AppData%\SCP Secret Laboratory\LabAPI\configs\&lt;端口或 global&gt;\SLDataAPI\config.yml
/// 插件本身的启停由 LabAPI 的 properties.yml 管理（/control/plugins 端点可代写），这里不再提供 IsEnabled。
/// </summary>
public class Config
{
    public bool Debug { get; set; } = false;

    /// <summary>
    /// 只读数据口（/get_sl_data、/plugins/adapted 等）的共享口令。
    /// 出厂默认 <c>your_secret_token</c>、空、或未通过强度校验（长度≥8 且同时含大写/小写/数字/特殊符号）
    /// 时 Enable 会 fail-closed：不对外提供数据口；若控制面也未启用则不绑定 HTTP 端口。
    /// </summary>
    public string VerifyToken { get; set; } = "your_secret_token";
    public int HttpPort { get; set; } = 8081;
    public int PushIntervalSeconds { get; set; } = 8;

    // ================== 控制接口（v2.1 推出；v2.5.0 WebSocket 长连接化，代号 Yagami Light；v2.6.0 推出 API Key 双轨鉴权，代号 PEAK） ==================

    /// <summary>
    /// 是否启用控制接口（/control/*）。默认关闭。
    /// 关闭时，无论 ControlToken 是否配置，所有 /control/* 请求一律 404。
    /// </summary>
    public bool ControlEnabled { get; set; } = false;

    /// <summary>
    /// [已废弃 v2.6.0，代号 PEAK] 旧版控制接口万能 token。若仍配置会在启动时警告并忽略，鉴权改走 apikey.config。
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
    /// 是否检查 GitHub Releases 上的新版本（启动 + 周期共用同一通道）。
    /// 默认开启，与既有启动检查一致；关闭则启动与 72h 静默复查都不跑。
    /// </summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>
    /// 检测到新版本时是否自动下载并替换插件 DLL（覆盖后下次重启游戏服务器生效，旧版备份为 .bak）。
    /// 校验：下载文件必须是合法程序集、名称一致；当前程序集未签名则拒绝自动安装；
    /// 已签名时要求新文件公钥令牌与当前一致（防篡改）。
    /// 稳定版策略：只自动接受稳定版——预发布版本（GitHub prerelease/draft 标记，
    /// 或 tag 含 beta/alpha/rc/preview/dev 等标识）不会自动下载。
    /// 关闭时仅日志提示，需手动更新。启动检查与周期检查共用本开关。
    /// </summary>
    public bool AutoUpdateInstall { get; set; } = true;

    /// <summary>
    /// 两次更新检查的最小间隔（小时）。启动与周期复查共用：距上次检查不足则跳过，
    /// 避免频繁重启打 GitHub。默认 72。0 或负数 = 仅 Enable 时检查一次（旧行为），不排周期。
    /// 仅当 <see cref="AutoUpdateCheck"/> 为 true 时生效。
    /// </summary>
    public int AutoUpdateCheckIntervalHours { get; set; } = 72;

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

    // ================== 语音转发（v2.3 推出，代号 SPY） ==================

    /// <summary>
    /// 是否启用游戏内语音转发（WebSocket 实时语音流）。默认关闭。
    /// 启用时 WebUI 可实时收听服务器内所有语音（近距离/对讲机/Intercom 等全部频道）。
    /// </summary>
    public bool VoiceEnabled { get; set; } = false;

    /// <summary>
    /// 语音转发 WebSocket 服务的监听端口（独立于 HttpPort，默认 8082）。
    /// </summary>
    public int VoicePort { get; set; } = 8082;

    // ================== 语音录音取证（v2.5.1 推出，代号 Yagami Light；v2.5.2 连续拼接音质修复，代号 Bay of Pigs Invasion；
    //    v2.5.3 时间轴按频道对齐，代号 Bay of Pigs Invasion / Apollo 11's Tapes；v2.5.3-Patch 稳定性加固，代号 FI-STM） ==================

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

    // ================== 语音 zip WebDAV 自动上传（v2.6.0 PEAK，默认关闭；仅 https://） ==================

    /// <summary>
    /// 每局录音 zip 定稿后是否自动 PUT 到 WebDAV。默认关闭——不影响既有行为。
    /// 启用但 URL 无效时启动 Warn 一次后本会话跳过。
    /// </summary>
    public bool WebdavUploadEnabled { get; set; } = false;

    /// <summary>
    /// WebDAV 目标：目录 URL（自动追加文件名），或含 <c>{filename}</c> / <c>{file}</c> 的完整模板。
    /// 必须是 https:// 绝对地址（明文 http:// 会被拒绝，以免 Basic Auth 密码走明文）。
    /// 不改变插件自身的 HTTP 数据/控制端口。
    /// </summary>
    public string WebdavUrl { get; set; } = "";

    /// <summary>WebDAV Basic Auth 用户名。可空（匿名）。</summary>
    public string WebdavUsername { get; set; } = "";

    /// <summary>WebDAV Basic Auth 密码。可空。日志永不输出此值或 Authorization 头。</summary>
    public string WebdavPassword { get; set; } = "";

    /// <summary>
    /// 远程目录前缀（拼在 webdav_url 与文件名之间）。模板 URL 模式下忽略。
    /// 含 <c>..</c> 的段会被丢弃。
    /// </summary>
    public string WebdavRemotePathPrefix { get; set; } = "";

    /// <summary>单次 PUT 超时（秒），默认 30。</summary>
    public int WebdavTimeoutSeconds { get; set; } = 30;

    /// <summary>首次失败后的最多重试次数（总尝试 = 1 + 本值），默认 5。网络 / 5xx / 408 / 429 / 超时才重试。</summary>
    public int WebdavMaxRetries { get; set; } = 5;

    /// <summary>重试基础间隔（秒），按失败次数指数退避（上限 600s）。默认 15。</summary>
    public int WebdavRetryIntervalSeconds { get; set; } = 15;

    // ================== 举报功能（v2.5.4 推出，代号 GIS,GNSS,RS!：SSS UI + 平台端点） ==================

    /// <summary>
    /// 是否启用举报功能（默认关闭）：玩家在 Esc → 服务器设置 面板中
    /// 下拉选择在线玩家、填写原因、长按按钮提交举报；
    /// 平台端通过 /control/reports 端点读取未处理记录并标记已处理。
    /// </summary>
    public bool ReportEnabled { get; set; } = false;

    /// <summary>
    /// 举报记录最大条数。超出后自动删除最旧的已处理记录；
    /// 未处理记录不删除，若全部未处理则在 LocalAdmin 输出 WARN 提示。
    /// </summary>
    public int ReportMaxRecords { get; set; } = 50;

    /// <summary>限流窗口内（report_rate_window_minutes 分钟）每人最多提交举报次数。</summary>
    public int ReportRateLimit { get; set; } = 5;

    /// <summary>举报限流窗口（分钟），默认 30（半小时）。</summary>
    public int ReportRateWindowMinutes { get; set; } = 30;

    // ================== 控制操作审计日志（v2.5.5-preview 推出，代号 Everest C1） ==================
// ================== 适配插件端点发现：v2.5.5 正式引入（代号 Nexus） ==================

    /// <summary>
    /// 是否记录远程控制的主动侵入性操作（命令执行、玩家管理、回合/播报/核弹/波次控制、
    /// 门/电梯/灯光、举报处理、插件启停、封禁、文件写入等）。
    /// 写入插件配置目录 control_log.json：不计 IP，只记时间 + 端点 + 请求体 + 结果，
    /// 用于管理层问题追责。只读/自动化流程（数据查询、地图布局/导出、日志读取等）不记录。
    /// 默认开启——审计日志不暴露攻击面，默认开启才有追责意义。
    /// </summary>
    public bool ControlLogEnabled { get; set; } = true;

    /// <summary>控制日志最大条数，超出自动删除最旧条目（0/负数 = 不清理）。</summary>
    public int ControlLogMaxRecords { get; set; } = 500;

    /// <summary>
    /// 创建 API Key 后是否在 Windows 上后台尽力复制明文到剪贴板。默认关闭。
    /// 开启也不影响命令 response（始终只回一次性文件路径，不含明文）。
    /// </summary>
    public bool ApikeyCopyToClipboard { get; set; } = false;
}
