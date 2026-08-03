using Exiled.API.Interfaces;

public class Config : IConfig
{
    public bool IsEnabled { get; set; } = true;
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
}
