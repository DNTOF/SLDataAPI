using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace SLDataAPI.Services;

/// <summary>控制操作审计日志条目（不计 IP，只记时间与操作细节）。</summary>
public class ControlLogEntry
{
    public string time { get; set; } = "";       // UTC ISO8601
    public string endpoint { get; set; } = "";   // /control/xxx
    public string body { get; set; } = "";       // 请求体原文（操作细节）
    public bool success { get; set; }            // 是否执行成功
    public string message { get; set; } = "";    // 响应 message
}

/// <summary>control_log.json 根结构。</summary>
public class ControlLogStore
{
    public List<ControlLogEntry> entries { get; set; } = new();
}

/// <summary>
/// 控制操作审计日志（v2.5.5-preview 推出，代号 Everest C1）：记录所有**主动侵入性**远程控制操作，
/// 用于服务器管理层出现问题时按时间追责。
/// - 记录范围：/control/* 中的写操作（命令执行、玩家管理、回合/播报/核弹/波次控制、
///   门/电梯/灯光、举报处理、插件启停、封禁、文件写入、SLPlayer 播放控制等）
/// - 不记录：只读/自动化流程（get_sl_data、map layout/seed、map/export、reports list、
///   ban_list、logs、files 读、wave status、slplayer status/list、plugins 列表、state 纯查询）
/// - 不计 IP，只记时间 + 端点 + 请求体 + 结果
/// - 写入插件配置目录 control_log.json；超 control_log_max_records 自动删除最旧条目
/// </summary>
public static class ControlLogService
{
    private static readonly object FileLock = new();

    private static bool _enabled;
    private static int _maxRecords = 500;
    private static string _filePath = "";

    /// <summary>由 Plugin.Enable 调用。默认启用（审计日志不暴露攻击面，默认开启才有追责意义）。</summary>
    public static void Init(bool enabled, int maxRecords, string configDir)
    {
        _enabled = enabled;
        _maxRecords = maxRecords > 0 ? maxRecords : 500;

        if (string.IsNullOrEmpty(configDir))
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SCP Secret Laboratory", "SLDataAPI");
        try { Directory.CreateDirectory(configDir); } catch { /* 目录不可创建时写入会失败并在日志可见 */ }
        _filePath = Path.Combine(configDir, "control_log.json");
    }

    /// <summary>由 Plugin.Disable 调用。</summary>
    public static void Dispose() => _enabled = false;

    /// <summary>记录一条主动侵入性操作（HTTP 与 WS call 通道均经 ControlController.Handle 调用）。</summary>
    public static void Record(string endpoint, string body, bool success, string message)
    {
        if (!_enabled || string.IsNullOrEmpty(_filePath))
            return;

        lock (FileLock)
        {
            var all = LoadAllUnlocked();
            all.Add(new ControlLogEntry
            {
                time = DateTime.UtcNow.ToString("o"),
                endpoint = endpoint,
                body = body ?? "",
                success = success,
                message = message ?? "",
            });

            // 超出上限删除最旧条目（0/负数 = 不清理）
            if (_maxRecords > 0 && all.Count > _maxRecords)
                all.RemoveRange(0, all.Count - _maxRecords);

            SaveAllUnlocked(all);
        }
    }

    private static List<ControlLogEntry> LoadAllUnlocked()
    {
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
            return new List<ControlLogEntry>();
        try
        {
            return JsonConvert.DeserializeObject<ControlLogStore>(File.ReadAllText(_filePath))?.entries ?? new List<ControlLogEntry>();
        }
        catch (Exception ex)
        {
            Log.Warn($"[SLDataAPI] 控制日志文件解析失败（按空列表处理，原文件未删除）: {ex.Message}");
            return new List<ControlLogEntry>();
        }
    }

    /// <summary>原子写：先写 .tmp 再替换，避免崩溃留下半个 json。</summary>
    private static void SaveAllUnlocked(List<ControlLogEntry> list)
    {
        if (string.IsNullOrEmpty(_filePath))
            return;
        string json = JsonConvert.SerializeObject(new ControlLogStore { entries = list }, Formatting.Indented);
        string tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        if (File.Exists(_filePath))
            File.Delete(_filePath);
        File.Move(tmp, _filePath);
    }
}
