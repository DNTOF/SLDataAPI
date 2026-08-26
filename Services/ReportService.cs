using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LabApi.Features.Wrappers;
using MEC;
using Newtonsoft.Json;
using UserSettings.ServerSpecific;
using Player = LabApi.Features.Wrappers.Player;

namespace SLDataAPI.Services;

/// <summary>举报记录（reports.json 内一条）。</summary>
public class ReportRecord
{
    public string id { get; set; } = "";
    public string reporter_steam64 { get; set; } = "";
    public string reporter_name { get; set; } = "";
    public string reporter_ip { get; set; } = "";
    public string target_steam64 { get; set; } = "";
    public string target_name { get; set; } = "";
    public string reason { get; set; } = "";
    public string reported_at { get; set; } = "";
    public string status { get; set; } = "pending"; // pending 未处理 | handled 已处理
}

/// <summary>reports.json 根结构。</summary>
public class ReportStore
{
    public List<ReportRecord> reports { get; set; } = new();
}

/// <summary>
/// 举报功能（v2.5.4 推出，代号 GIS,GNSS,RS!）：SSS 游戏内举报面板 + 平台举报端点。
/// - 玩家在 Esc → 服务器设置 面板中：下拉选择要举报的在线玩家 → 文本填写原因 → 长按按钮提交
/// - 限流：每人每个限流窗口（默认 0.5h）最多提交 rateLimit 次
/// - 记录写入配置文件目录下的 reports.json（status: pending / handled）
/// - /control/reports 端点：list 读取未处理记录 / handle 标记已处理
/// - 记录数超 maxRecords 时自动删除最旧的已处理记录；全部未处理时 LocalAdmin 输出 WARN 提示
/// </summary>
public static class ReportService
{
    private const int DropdownId = 1;
    private const int TextId = 2;
    private const int ButtonId = 3;
    private const int ReasonCharLimit = 500;

    /// <summary>举报组标题：把举报控件与其他插件（或其他 SSS 选项）在面板中分隔开。</summary>
    private const string HeaderLabel = "举报功能 by DNT_OF";

    /// <summary>举报组的固定元素数：1 个组标题 + 下拉 + 文本 + 按钮（合并/移除时按此精确切分，不动其他插件的控件）。</summary>
    private const int GroupElementCount = 4;

    /// <summary>提交按钮长按秒数。</summary>
    private const float ButtonHoldSeconds = 3f;

    /// <summary>提交按钮默认文案。</summary>
    private const string ButtonIdleText = "长按 3 秒提交";

    private static readonly object FileLock = new();

    private static bool _enabled;
    private static int _maxRecords = 50;
    private static int _rateLimit = 5;
    private static TimeSpan _rateWindow = TimeSpan.FromMinutes(30);
    private static string _filePath = "";

    /// <summary>下拉选项与玩家索引映射（SSS 事件在主线程触发，主线程内访问）。</summary>
    private static readonly List<Player> PlayerOptions = new();

    /// <summary>限流记账：steam64（非 steam 玩家回落完整 UserId）→ 提交时间戳。</summary>
    private static readonly Dictionary<string, List<DateTime>> SubmissionTimes = new(StringComparer.Ordinal);

    private static CoroutineHandle _refreshRoutine;
    private static bool _warnedFullPending;

    /// <summary>由 Plugin.Enable 调用。enabled 时才定义并下发 SSS 界面、注册事件。</summary>
    public static void Init(bool enabled, int maxRecords, int rateLimit, int rateWindowMinutes, string configDir)
    {
        Dispose();

        _enabled = enabled;
        _maxRecords = maxRecords > 0 ? maxRecords : 50;
        _rateLimit = rateLimit > 0 ? rateLimit : 5;
        _rateWindow = TimeSpan.FromMinutes(rateWindowMinutes > 0 ? rateWindowMinutes : 30);

        // 记录文件放插件配置目录；目录获取失败时回退默认数据目录（并确保目录存在）
        if (string.IsNullOrEmpty(configDir))
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SCP Secret Laboratory", "SLDataAPI");
        try { Directory.CreateDirectory(configDir); } catch { /* 目录不可创建时写入会失败并在日志可见 */ }
        _filePath = Path.Combine(configDir, "reports.json");
        _warnedFullPending = false;

        if (!_enabled)
            return;

        // 定义并下发 SSS 界面（DefinedSettings 是全局单例，其他插件若覆盖面板是已知限制；
        // 举报组自带标题「举报功能 by DNT_OF」与其他 SSS 选项分隔）
        ServerSpecificSettingsSync.SendOnJoinFilter = _ => true; // 新加入玩家自动收到面板
        ServerSpecificSettingsSync.DefinedSettings = BuildReportSettings();
        ServerSpecificSettingsSync.SendToAll();
        ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnSettingValueReceived;

        RefreshDropdown();
        ScheduleRefresh(); // 每 30s 兜底刷新下拉（玩家进出即时刷新见 OnPlayersChanged）

        Log.Info(
            $"[SLDataAPI] 举报功能已启用：SSS 面板已下发，记录上限 {_maxRecords} 条，" +
            $"限流 {_rateLimit} 次/{_rateWindow.TotalMinutes:0} 分钟/人，记录文件 {_filePath}");
    }

    /// <summary>由 Plugin.Disable 调用：退订 SSS 事件、停掉刷新协程、从全局定义中移除举报组。</summary>
    public static void Dispose()
    {
        _enabled = false;
        if (_refreshRoutine.IsRunning)
            Timing.KillCoroutines(_refreshRoutine);
        ServerSpecificSettingsSync.ServerOnSettingValueReceived -= OnSettingValueReceived;

        // 从全局定义中精确移除举报组（1 组头 + 3 控件），保留其他插件的控件；
        // 空定义 = 客户端移除整个 tab
        try
        {
            var current = ServerSpecificSettingsSync.DefinedSettings;
            if (current != null && current.Length > 0)
            {
                int idx = -1;
                for (int i = 0; i < current.Length; i++)
                {
                    if (current[i] is SSGroupHeader h && h.Label == HeaderLabel)
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx >= 0)
                {
                    var kept = current.ToList();
                    kept.RemoveRange(idx, Math.Min(GroupElementCount, kept.Count - idx));
                    ServerSpecificSettingsSync.DefinedSettings = kept.ToArray();
                }
            }
            ServerSpecificSettingsSync.SendOnJoinFilter = null;
            ServerSpecificSettingsSync.SendToAll();
        }
        catch { /* 停服流程中网络层可能已不可用，忽略 */ }
    }

    /// <summary>构建举报组控件（组标题 + 下拉 + 文本 + 按钮），组标题用于与其他插件 SSS 选项分隔。</summary>
    private static ServerSpecificSettingBase[] BuildReportSettings() => new ServerSpecificSettingBase[]
    {
        new SSGroupHeader(HeaderLabel),
        new SSDropdownSetting(DropdownId, "选择要举报的玩家", Array.Empty<string>()),
        new SSPlaintextSetting(TextId, "举报原因", "请详细描述违规行为…", characterLimit: ReasonCharLimit),
        new SSButton(ButtonId, "提交举报", ButtonIdleText, holdTimeSeconds: ButtonHoldSeconds),
    };

    /// <summary>每 30 秒兜底刷新一次下拉选项（MEC 无无限 CallRepeating，用 CallDelayed 自重复）。</summary>
    private static void ScheduleRefresh()
    {
        _refreshRoutine = Timing.CallDelayed(30f, () =>
        {
            RefreshDropdown();
            ScheduleRefresh();
        });
    }

    /// <summary>玩家加入/离开时即时刷新下拉（Plugin 的 Joined/Left 事件里调用）。</summary>
    public static void OnPlayersChanged() => RefreshDropdown();

    // ────────────────────────── SSS 面板 ──────────────────────────

    /// <summary>重建下拉选项并下发到所有玩家（选项列表与 PlayerOptions 索引一一对应）。</summary>
    private static void RefreshDropdown()
    {
        if (!_enabled) return;
        try
        {
            PlayerOptions.Clear();
            var opts = new List<string>();
            foreach (var p in Player.List)
            {
                if (p.IsHost || p.IsNpc || p.IsDummy || !p.IsPlayer || !p.IsReady)
                    continue;
                PlayerOptions.Add(p);
                string name = p.Nickname ?? p.UserId ?? "?";
                if (name.Length > 24)
                    name = name.Substring(0, 24) + "…";
                opts.Add($"{name} · {TailId(p)}");
            }

            // 只更新举报组自己的下拉（DefinedSettings 是全局单例，按 id 精确定位避免误更新其他控件的同名下拉）
            var dropdown = ServerSpecificSettingsSync.DefinedSettings?
                .OfType<SSDropdownSetting>().FirstOrDefault(s => s.SettingId == DropdownId);
            if (dropdown != null)
                dropdown.SendDropdownUpdate(opts.ToArray(), true, _ => true);
        }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI] 举报下拉刷新异常（忽略）: {ex.Message}");
        }
    }

    /// <summary>SSS 玩家交互回传（主线程）。只处理举报组的提交按钮；下拉/文本变更仅存客户端状态，提交时读取。</summary>
    private static void OnSettingValueReceived(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        try
        {
            if (!_enabled || setting.SettingId != ButtonId)
                return;

            var player = Player.Get(hub);
            if (player == null)
                return;

            var dropdown = ServerSpecificSettingsSync.GetSettingOfUser<SSDropdownSetting>(hub, DropdownId);
            var text = ServerSpecificSettingsSync.GetSettingOfUser<SSPlaintextSetting>(hub, TextId);
            int idx = dropdown?.SyncSelectionIndexValidated ?? -1;
            string reason = text?.SyncInputText?.Trim() ?? "";

            Player? target = idx >= 0 && idx < PlayerOptions.Count ? PlayerOptions[idx] : null;
            if (target == null || target.ReferenceHub == null)
            {
                ShowFeedback(hub, "失败：所选玩家已不在服务器");
                return;
            }
            if (reason.Length == 0)
            {
                ShowFeedback(hub, "失败：请填写举报原因");
                return;
            }
            if (reason.Length > ReasonCharLimit)
                reason = reason.Substring(0, ReasonCharLimit);

            string reporterKey = Steam64(player);
            if (!TryConsumeRateSlot(reporterKey))
            {
                ShowFeedback(hub, $"过于频繁：每 {_rateWindow.TotalMinutes:0} 分钟最多 {_rateLimit} 次");
                return;
            }

            AppendRecord(new ReportRecord
            {
                id = Guid.NewGuid().ToString("N"),
                reporter_steam64 = reporterKey,
                reporter_name = player.Nickname ?? "?",
                reporter_ip = player.IpAddress ?? "",
                target_steam64 = Steam64(target),
                target_name = target.Nickname ?? "?",
                reason = reason,
                reported_at = DateTime.UtcNow.ToString("o"),
                status = "pending",
            });

            ShowFeedback(hub, "已提交，感谢反馈");
        }
        catch (Exception ex)
        {
            // 事件链保护：SSS 事件异常不得传播回游戏网络层
            Log.Error($"[SLDataAPI] 举报处理异常（已兜底）: {ex}");
        }
    }

    /// <summary>
    /// 反馈双通道：按钮文本（玩家正开着 SSS 面板，HUD hint 会被菜单挡住）
    /// + HUD hint（关闭菜单后仍可见）。按钮文本 4 秒后恢复默认文案。
    /// </summary>
    private static void ShowFeedback(ReferenceHub hub, string text)
    {
        var player = Player.Get(hub);
        if (player != null)
            player.SendHint($"举报：{text}", 4f);

        var btn = ServerSpecificSettingsSync.DefinedSettings?
            .OfType<SSButton>().FirstOrDefault(s => s.SettingId == ButtonId);
        if (btn == null)
            return;
        btn.SendButtonUpdate(text, ButtonHoldSeconds, true, h => h == hub);
        Timing.CallDelayed(4f, () => btn.SendButtonUpdate(ButtonIdleText, ButtonHoldSeconds, true, h => h == hub));
    }

    // ────────────────────────── 平台端点支持 ──────────────────────────

    /// <summary>读取全部未处理（pending）举报记录。端点 /control/reports action=list。</summary>
    public static List<ReportRecord> ListPending()
    {
        lock (FileLock)
        {
            return LoadAllUnlocked().Where(r => r.status == "pending").ToList();
        }
    }

    /// <summary>将指定记录标记为已处理（handled）。不存在或已处理返回 false。端点 action=handle。</summary>
    public static bool MarkHandled(string id)
    {
        lock (FileLock)
        {
            var all = LoadAllUnlocked();
            var rec = all.FirstOrDefault(r => r.id == id);
            if (rec == null || rec.status == "handled")
                return false;
            rec.status = "handled";
            SaveAllUnlocked(all);
            return true;
        }
    }

    // ────────────────────────── 存储与清理 ──────────────────────────

    private static void AppendRecord(ReportRecord record)
    {
        lock (FileLock)
        {
            var all = LoadAllUnlocked();
            all.Add(record);

            // 超限自动清理：只删最旧的"已处理"记录；未处理记录绝不删除
            int overflow = all.Count - _maxRecords;
            if (overflow > 0)
            {
                var handledIds = all.Where(r => r.status == "handled")
                                    .OrderBy(r => r.reported_at)
                                    .Take(overflow)
                                    .Select(r => r.id)
                                    .ToHashSet(StringComparer.Ordinal);
                if (handledIds.Count > 0)
                {
                    all.RemoveAll(r => handledIds.Contains(r.id));
                    Log.Info($"[SLDataAPI] 举报记录超限，已清理 {handledIds.Count} 条最旧的已处理记录（当前 {all.Count}/{_maxRecords}）");
                }
                else if (!_warnedFullPending)
                {
                    _warnedFullPending = true; // 只提示一次，避免每次写入刷屏
                    Log.Warn($"[SLDataAPI] 举报记录已达上限（{_maxRecords} 条）且全部未处理，无法自动清理——请平台端及时处理");
                }
            }

            SaveAllUnlocked(all);
        }
    }

    private static List<ReportRecord> LoadAllUnlocked()
    {
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
            return new List<ReportRecord>();
        try
        {
            return JsonConvert.DeserializeObject<ReportStore>(File.ReadAllText(_filePath))?.reports ?? new List<ReportRecord>();
        }
        catch (Exception ex)
        {
            Log.Warn($"[SLDataAPI] 举报记录文件解析失败（按空列表处理，原文件未删除）: {ex.Message}");
            return new List<ReportRecord>();
        }
    }

    /// <summary>原子写：先写 .tmp 再替换，避免崩溃留下半个 json。</summary>
    private static void SaveAllUnlocked(List<ReportRecord> list)
    {
        if (string.IsNullOrEmpty(_filePath))
            return;
        string json = JsonConvert.SerializeObject(new ReportStore { reports = list }, Formatting.Indented);
        string tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        if (File.Exists(_filePath))
            File.Delete(_filePath);
        File.Move(tmp, _filePath);
    }

    // ────────────────────────── 限流与标识 ──────────────────────────

    /// <summary>限流：窗口内次数已满返回 false；否则记账并返回 true。</summary>
    private static bool TryConsumeRateSlot(string key)
    {
        var now = DateTime.UtcNow;
        if (!SubmissionTimes.TryGetValue(key, out var list))
        {
            list = new List<DateTime>();
            SubmissionTimes[key] = list;
        }
        list.RemoveAll(t => now - t > _rateWindow);
        if (list.Count >= _rateLimit)
            return false;
        list.Add(now);
        return true;
    }

    /// <summary>steam64：UserId 前缀为纯数字时取其值；否则回落完整 UserId（northwood 账号等）。</summary>
    private static string Steam64(Player p)
    {
        string uid = p.UserId ?? "";
        string prefix = uid.Split('@')[0];
        return long.TryParse(prefix, out _) ? prefix : uid;
    }

    /// <summary>下拉选项尾部标识：steam64 尾 4 位（重名区分用）。</summary>
    private static string TailId(Player p)
    {
        string s = Steam64(p);
        return s.Length >= 4 ? s.Substring(s.Length - 4) : s;
    }
}
