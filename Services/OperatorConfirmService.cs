using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using SLDataAPI.Auth;

namespace SLDataAPI.Services;

/// <summary>
/// 服务端敏感操作（API Key 创建 / 吊销）的人工确认（层 2）。
///
/// 通道优先级：
///   0. 远程控制通道下发的命令直接拒绝——层 1 已在 /control/console/command 处硬拦，
///      这里在触碰任何界面之前再兜一层，保证层 2 永远不可能被远程"确认"通过。
///   1. 【主通道】整屏 TUI 接管：清屏画确认面板（操作、id、模板、风险提示、倒计时、y/n 选择），
///      读按键，结束后把原控制台缓冲/光标/颜色/输入模式原样还回去，LocalAdmin 输出继续。
///      实现见 ConsoleTakeover（Win32 优先，其次 ANSI 备用屏）。
///   2. 行模式：拿不到屏幕但标准输入可读时，打一行提示 + 读一行（无头/管道方式运行）。
///   3. MessageBox：仅当上面两条都不可用且明确存在交互桌面时的最后兜底。
/// 拒绝、超时、无可用通道一律按拒绝处理，调用方不得执行变更。
///
/// 注意：行模式的标准输入读取线程在首次用到时才启动（此后常驻）并持续消费本进程标准输入；
/// 提示前会丢弃队列里的历史输入行，避免"提前敲 y"预先应答。
/// </summary>
public static class OperatorConfirmService
{
    private const int DefaultTimeoutSeconds = 20;
    private const int MinTimeoutSeconds = 5;
    private const int MaxTimeoutSeconds = 120;
    private const int PollSliceMs = 200;

    // 同一时刻只允许一个确认会话：两个面板/提示并存会抢同一个屏幕与同一行输入
    private static readonly object Gate = new object();
    private static readonly BlockingCollection<string> PendingLines =
        new BlockingCollection<string>(new ConcurrentQueue<string>());

    private static Thread? _stdinReader;
    private static volatile bool _stdinUnavailable;

    /// <summary>
    /// 向服务端操作者索取一次确认。返回 false 即调用方必须中止操作，
    /// <paramref name="reason"/> 给出可直接回显给命令发送者的原因。
    /// </summary>
    public static bool Confirm(ConfirmPanelModel model, out string reason)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        reason = "";

        // 远程执行上下文：在画任何界面之前就失败，远程通道不可能自我确认
        if (RemoteCommandGuard.IsRemoteExecution)
        {
            reason = "远程控制通道不允许该操作（只能在服务器本地控制台执行）";
            return false;
        }

        int timeoutSeconds = ResolveTimeoutSeconds();
        string prompt = model.OneLinePrompt();

        lock (Gate)
        {
            if (TryConfirmViaPanel(model, timeoutSeconds, out bool panelAnswer, out string panelReason))
            {
                reason = panelReason;
                LogDecision(prompt, panelAnswer, panelAnswer ? "面板确认" : panelReason);
                return panelAnswer;
            }

            if (TryConfirmViaStdin(prompt, timeoutSeconds, out bool lineAnswer, out string lineReason))
            {
                reason = lineReason;
                LogDecision(prompt, lineAnswer, lineAnswer ? "行模式确认" : lineReason);
                return lineAnswer;
            }

            if (TryConfirmViaMessageBox(prompt, timeoutSeconds, out bool dialogAnswer))
            {
                if (!dialogAnswer)
                    reason = "服务端操作者在确认对话框中选择了否";
                LogDecision(prompt, dialogAnswer, dialogAnswer ? "对话框确认" : reason);
                return dialogAnswer;
            }

            reason = "无可用的人工确认通道（无法接管服务器控制台、标准输入不可读且无交互桌面），已按拒绝处理";
            Log.Warn($"[SLDataAPI] 人工确认通道不可用，已拒绝敏感操作：{prompt}");
            return false;
        }
    }

    private static void LogDecision(string prompt, bool confirmed, string detail)
    {
        string text = $"[SLDataAPI] 人工确认{(confirmed ? "通过" : "未通过")}：{prompt}" +
                      (string.IsNullOrEmpty(detail) ? "" : $"（{detail}）");
        if (confirmed) Log.Info(text);
        else Log.Warn(text);
    }

    private static int ResolveTimeoutSeconds()
    {
        int configured = Plugin.Instance?.Config.ApiKeyConfirmTimeoutSeconds ?? DefaultTimeoutSeconds;
        if (configured <= 0) configured = DefaultTimeoutSeconds;
        return Math.Max(MinTimeoutSeconds, Math.Min(MaxTimeoutSeconds, configured));
    }

    // ────────────── 通道 1：整屏 TUI 面板 ──────────────

    private static bool TryConfirmViaPanel(
        ConfirmPanelModel model, int timeoutSeconds, out bool answer, out string reason)
    {
        answer = false;
        reason = "";

        if (!ConsoleTakeover.TryAcquire(out IConsoleTakeover? screen) || screen == null)
            return false;

        ConfirmOutcome outcome;
        try
        {
            outcome = ConfirmSession.Run(screen, model, timeoutSeconds);
        }
        catch (Exception ex)
        {
            Log.Warn($"[SLDataAPI] 确认面板异常，按拒绝处理: {ex.Message}");
            outcome = ConfirmOutcome.Denied;
        }
        finally
        {
            // 无论结果如何，先把控制台还原成接管前的样子
            try { screen.Dispose(); }
            catch (Exception ex) { Log.Warn($"[SLDataAPI] 控制台恢复失败: {ex.Message}"); }
        }

        switch (outcome)
        {
            case ConfirmOutcome.Confirmed:
                answer = true;
                return true;
            case ConfirmOutcome.TimedOut:
                reason = $"{timeoutSeconds} 秒内未在服务器控制台确认，已按拒绝处理";
                return true;
            default:
                reason = "服务端操作者在确认面板中选择了取消";
                return true;
        }
    }

    // ────────────── 通道 2：行模式 ──────────────

    /// <summary>
    /// 打一行提示并读一行标准输入。返回 false 表示该通道不可用（应尝试下一个通道）。
    /// </summary>
    private static bool TryConfirmViaStdin(
        string prompt, int timeoutSeconds, out bool answer, out string reason)
    {
        answer = false;
        reason = "";

        if (!EnsureStdinReader())
            return false;

        DrainPendingLines();
        WriteConsoleLine($"[SLDataAPI][安全确认] {prompt} [y/N]（{timeoutSeconds} 秒内在服务器控制台回答，超时按拒绝处理）");

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (PendingLines.TryTake(out string? line, PollSliceMs))
            {
                answer = RemoteCommandGuard.IsAffirmative(line);
                if (!answer)
                    reason = "服务端操作者未确认（回答不是 y/yes）";
                return true;
            }
            if (_stdinUnavailable)
                return false;
        }

        if (_stdinUnavailable)
            return false;

        reason = $"{timeoutSeconds} 秒内未在服务器控制台收到确认，已按拒绝处理";
        return true;
    }

    private static bool EnsureStdinReader()
    {
        if (_stdinUnavailable)
            return false;
        if (_stdinReader != null)
            return true;

        try
        {
            _stdinReader = new Thread(ReadStdinLoop)
            {
                IsBackground = true,
                Name = "SLDataAPI-Confirm-Stdin",
            };
            _stdinReader.Start();
            return true;
        }
        catch (Exception ex)
        {
            _stdinUnavailable = true;
            Log.Warn($"[SLDataAPI] 无法启动控制台确认读取线程: {ex.Message}");
            return false;
        }
    }

    private static void ReadStdinLoop()
    {
        try
        {
            while (true)
            {
                string? line = Console.In.ReadLine();
                if (line == null)
                {
                    // EOF：本进程没有可读的控制台输入（服务方式运行 / stdin 重定向到空设备）
                    _stdinUnavailable = true;
                    return;
                }
                PendingLines.Add(line);
            }
        }
        catch (Exception ex)
        {
            _stdinUnavailable = true;
            Log.Debug($"[SLDataAPI] 控制台确认读取线程结束: {ex.Message}");
        }
    }

    /// <summary>丢弃提示出现之前积压的输入行，防止预先敲入的 y 被当成本次回答。</summary>
    private static void DrainPendingLines()
    {
        while (PendingLines.TryTake(out _)) { }
    }

    // ────────────── 通道 3：图形对话框（最后兜底） ──────────────

    private const uint MbYesNo = 0x00000004;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbDefButton2 = 0x00000100;
    private const uint MbSystemModal = 0x00001000;
    private const uint MbSetForeground = 0x00010000;
    private const int IdYes = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static bool TryConfirmViaMessageBox(string prompt, int timeoutSeconds, out bool answer)
    {
        answer = false;
        if (!IsInteractiveDesktopAvailable())
            return false;

        int result = 0;
        Exception? failure = null;
        var done = new ManualResetEventSlim(false);

        var dialog = new Thread(() =>
        {
            try
            {
                result = MessageBoxW(IntPtr.Zero, prompt + "\n\n确认执行该操作？",
                    "SLDataAPI 安全确认",
                    MbYesNo | MbIconWarning | MbDefButton2 | MbSystemModal | MbSetForeground);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "SLDataAPI-Confirm-Dialog",
        };

        try { dialog.Start(); }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI] 无法启动确认对话框线程: {ex.Message}");
            return false;
        }

        // MessageBox 没有原生超时；超时后不再等待（残留窗口由操作者自行关闭），按拒绝处理
        if (!done.Wait(timeoutSeconds * 1000))
            return false;

        if (failure != null)
        {
            Log.Debug($"[SLDataAPI] 确认对话框不可用: {failure.Message}");
            return false;
        }

        answer = result == IdYes;
        return true;
    }

    private static bool IsInteractiveDesktopAvailable()
    {
        try
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.UserInteractive;
        }
        catch
        {
            return false;
        }
    }

    // ────────────── 控制台输出 ──────────────

    private static void WriteConsoleLine(string text)
    {
        try { ServerConsole.AddLog(text, ConsoleColor.Yellow); }
        catch { /* 控制台不可用时退化到标准输出 */ }

        try
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
        catch { /* 无标准输出时忽略 */ }
    }
}
