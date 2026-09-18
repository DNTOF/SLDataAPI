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
///   1. 远程控制通道下发的命令直接拒绝——层 1 已在 /control/console/command 处硬拦，这里再兜一层，
///      保证层 2 永远不可能被远程"确认"通过。
///   2. 服务器控制台（LocalAdmin / 专用服自带控制台）：提示行写到控制台，读一行标准输入。
///      专用服多为无头/服务方式运行，所以这是主通道，不依赖任何图形环境。
///   3. 仅当明确存在交互桌面（Windows + UserInteractive）且标准输入不可读时，退化到 MessageBox。
/// 超时或回答不是明确的 y/yes 一律按拒绝处理，调用方不得执行变更。
///
/// 注意：标准输入读取线程在首次确认时才启动（此后常驻），启动后会持续消费本进程的标准输入；
/// 提示前会丢弃队列里的历史输入行，避免"提前敲 y"预先应答。
/// </summary>
public static class OperatorConfirmService
{
    private const int DefaultTimeoutSeconds = 20;
    private const int MinTimeoutSeconds = 5;
    private const int MaxTimeoutSeconds = 120;
    private const int PollSliceMs = 200;

    // 同一时刻只允许一个确认会话：两个提示并存会抢同一行输入
    private static readonly object Gate = new object();
    private static readonly BlockingCollection<string> PendingLines =
        new BlockingCollection<string>(new ConcurrentQueue<string>());

    private static Thread? _stdinReader;
    private static volatile bool _stdinUnavailable;

    /// <summary>
    /// 向服务端操作者索取一次 y/n 确认。返回 false 即调用方必须中止操作，
    /// <paramref name="reason"/> 给出可直接回显给命令发送者的原因。
    /// </summary>
    public static bool Confirm(string prompt, out string reason)
    {
        reason = "";

        if (RemoteCommandGuard.IsRemoteExecution)
        {
            reason = "远程控制通道不允许该操作（只能在服务器本地控制台执行）";
            return false;
        }

        int timeoutSeconds = ResolveTimeoutSeconds();

        lock (Gate)
        {
            WriteConsole($"[SLDataAPI][安全确认] {prompt} [y/N]（{timeoutSeconds} 秒内在服务器控制台回答，超时按拒绝处理）");

            if (TryConfirmViaStdin(timeoutSeconds, out bool answered, out string? line))
            {
                if (answered)
                {
                    bool ok = RemoteCommandGuard.IsAffirmative(line);
                    if (!ok)
                        reason = "服务端操作者未确认（回答不是 y/yes）";
                    WriteConsole($"[SLDataAPI][安全确认] {(ok ? "已确认" : "已拒绝")}：{prompt}");
                    return ok;
                }

                reason = $"{timeoutSeconds} 秒内未在服务器控制台收到确认，已按拒绝处理";
                WriteConsole($"[SLDataAPI][安全确认] 超时未确认，已拒绝：{prompt}");
                return false;
            }

            // 标准输入不可读（以服务/守护进程方式运行，stdin 直接 EOF）
            if (TryConfirmViaMessageBox(prompt, timeoutSeconds, out bool dialogAnswer))
            {
                if (!dialogAnswer)
                    reason = "服务端操作者在确认对话框中选择了否";
                return dialogAnswer;
            }

            reason = "无可用的人工确认通道（服务器控制台标准输入不可读，且无交互桌面），已按拒绝处理";
            Log.Warn($"[SLDataAPI] 人工确认通道不可用，已拒绝敏感操作：{prompt}");
            return false;
        }
    }

    private static int ResolveTimeoutSeconds()
    {
        int configured = Plugin.Instance?.Config.ApiKeyConfirmTimeoutSeconds ?? DefaultTimeoutSeconds;
        if (configured <= 0) configured = DefaultTimeoutSeconds;
        return Math.Max(MinTimeoutSeconds, Math.Min(MaxTimeoutSeconds, configured));
    }

    /// <summary>
    /// 读一行标准输入。返回 false 表示该通道不可用（应尝试下一个通道）；
    /// 返回 true 时 <paramref name="answered"/>=false 表示在超时前没人回答。
    /// </summary>
    private static bool TryConfirmViaStdin(int timeoutSeconds, out bool answered, out string? line)
    {
        answered = false;
        line = null;

        if (!EnsureStdinReader())
            return false;

        DrainPendingLines();

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (PendingLines.TryTake(out line, PollSliceMs))
            {
                answered = true;
                return true;
            }
            if (_stdinUnavailable)
                return false;
        }

        return !_stdinUnavailable;
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

    // ────────────── 可选的图形确认（仅在明确存在交互桌面时） ──────────────

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

    private static void WriteConsole(string text)
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
