using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SLDataAPI.Auth;

namespace SLDataAPI.Services;

/// <summary>
/// 【主通道】在服务器桌面上新开一个 cmd.exe 窗口做人工确认。
///
/// 这里刻意不碰 LocalAdmin 的控制台：不 AttachConsole、不 FreeConsole、不清屏、不恢复。
/// 新窗口用 CREATE_NEW_CONSOLE 拉起（ShellExecute 兜底，它默认同样给控制台程序开新窗口），
/// 拥有自己的屏幕缓冲区与输入队列，关掉即走，LocalAdmin 的输出全程不受影响。
///
/// 结论只认"退出码 + 一次性校验串"两者同时对上；其余一切情况（窗口起不来、被 X 掉、
/// 脚本异常退出）要么按拒绝处理，要么把通道判为不可用交给下一条通道，绝不放行。
/// </summary>
internal static class ConfirmWindowChannel
{
    /// <summary>子进程自己会在倒计时结束时退出；这是父进程多等的宽限，防止卡住命令线程。</summary>
    private const int GraceMs = 8000;

    private const string ScratchPrefix = "confirm-ui-";

    /// <summary>
    /// 弹出确认窗口并等待结论。返回 false 表示该通道不可用（调用方继续尝试行模式 / 对话框）。
    /// </summary>
    public static bool TryConfirm(
        ConfirmPanelModel model, int timeoutSeconds, out ConfirmOutcome outcome, out string detail)
    {
        outcome = ConfirmOutcome.Denied;
        detail = "";

        if (model == null) throw new ArgumentNullException(nameof(model));
        if (!IsSupportedHost(out string comSpec))
            return false;

        string? dir = null;
        try
        {
            dir = CreateScratchDirectory();
            if (dir == null)
                return false;

            string nonce = ConfirmWindowProtocol.NewNonce();
            WritePayload(dir, model, timeoutSeconds, nonce);

            // 先说一声再阻塞：命令是在 LocalAdmin 里敲的，操作者得知道要去看新窗口
            Log.Info($"[SLDataAPI] 正在服务器桌面弹出确认窗口（独立 cmd 窗口），" +
                     $"请在该窗口按 Y 确认 / N 取消（{timeoutSeconds} 秒后自动拒绝）");

            if (!TryRunConfirmWindow(comSpec, dir, timeoutSeconds, out int exitCode))
                return false;

            string resultPath = Path.Combine(dir, ConfirmWindowProtocol.ResultFileName);
            string? resultText = null;
            try { if (File.Exists(resultPath)) resultText = File.ReadAllText(resultPath); }
            catch (Exception ex) { Log.Debug($"[SLDataAPI] 读取确认窗口结果失败: {ex.Message}"); }

            if (!ConfirmWindowProtocol.TryParseOutcome(exitCode, resultText, nonce, out outcome, out detail))
            {
                Log.Debug($"[SLDataAPI] 确认窗口未给出有效结论（退出码 {exitCode}），改用下一条确认通道");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[SLDataAPI] 新开确认窗口失败: {ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// 只在"Windows + 有交互桌面 + 找得到 cmd.exe"时可用。
    /// 以服务方式运行（会话 0，无桌面）时新窗口没人看得见，直接判不可用，让行模式接手。
    /// </summary>
    private static bool IsSupportedHost(out string comSpec)
    {
        comSpec = "";
        try
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return false;
            if (!Environment.UserInteractive)
            {
                Log.Debug("[SLDataAPI] 当前会话没有交互桌面，跳过新开确认窗口");
                return false;
            }

            string candidate = Environment.GetEnvironmentVariable("ComSpec") ?? "";
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            if (!File.Exists(candidate))
                return false;

            comSpec = candidate;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI] 确认窗口宿主环境判定失败: {ex.Message}");
            return false;
        }
    }

    // ────────────── 临时目录 ──────────────

    /// <summary>
    /// 面板与结论文件放在插件配置目录下（与 apikey.config 同一信任边界：能写这里的人
    /// 本来就能改密钥库），配置目录不可用时退回系统临时目录。目录名随机，用完即删。
    /// </summary>
    private static string? CreateScratchDirectory()
    {
        foreach (string? baseDir in new[] { PluginConfigDirectory(), SafeTempDirectory() })
        {
            if (string.IsNullOrWhiteSpace(baseDir))
                continue;
            try
            {
                SweepStaleDirectories(baseDir!);
                string dir = Path.Combine(baseDir!, ScratchPrefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                Log.Debug($"[SLDataAPI] 无法在 {baseDir} 建立确认窗口临时目录: {ex.Message}");
            }
        }
        return null;
    }

    private static string? PluginConfigDirectory()
    {
        try
        {
            string path = ApiKeyService.ConfigPath;
            if (string.IsNullOrWhiteSpace(path))
                return null;
            string? dir = Path.GetDirectoryName(path);
            return Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    private static string? SafeTempDirectory()
    {
        try { return Path.GetTempPath(); }
        catch { return null; }
    }

    /// <summary>清掉上次进程崩溃残留的目录，避免越堆越多。</summary>
    private static void SweepStaleDirectories(string baseDir)
    {
        try
        {
            foreach (string stale in Directory.GetDirectories(baseDir, ScratchPrefix + "*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(stale) > TimeSpan.FromHours(1))
                        Directory.Delete(stale, recursive: true);
                }
                catch { /* 残留目录删不掉不影响本次确认 */ }
            }
        }
        catch { /* 枚举失败忽略 */ }
    }

    private static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir!, recursive: true); }
        catch (Exception ex) { Log.Debug($"[SLDataAPI] 确认窗口临时目录清理失败: {ex.Message}"); }
    }

    /// <summary>写入脚本（ASCII）与每一秒对应的一屏面板（UTF-8 无 BOM，给 cmd 的 type 用）。</summary>
    private static void WritePayload(string dir, ConfirmPanelModel model, int timeoutSeconds, string nonce)
    {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        for (int seconds = timeoutSeconds; seconds >= 1; seconds--)
        {
            File.WriteAllText(
                Path.Combine(dir, ConfirmWindowProtocol.PanelFileName(seconds)),
                ConfirmWindowProtocol.RenderPanel(model, seconds),
                utf8NoBom);
        }

        File.WriteAllText(
            Path.Combine(dir, ConfirmWindowProtocol.ScriptFileName),
            ConfirmWindowProtocol.BuildScript(timeoutSeconds, nonce),
            Encoding.ASCII);
    }

    // ────────────── 起窗口 ──────────────

    private static bool TryRunConfirmWindow(string comSpec, string dir, int timeoutSeconds, out int exitCode)
    {
        int waitMs = timeoutSeconds * 1000 + GraceMs;
        // 工作目录就是脚本目录，命令行里只出现不含空格的脚本文件名，省掉 cmd 的引号陷阱
        string commandLine = $"\"{comSpec}\" /c {ConfirmWindowProtocol.ScriptFileName}";

        if (TryRunViaCreateProcess(commandLine, dir, waitMs, out exitCode))
            return true;
        return TryRunViaShellExecute(comSpec, dir, waitMs, out exitCode);
    }

    private static bool TryRunViaCreateProcess(string commandLine, string dir, int waitMs, out int exitCode)
    {
        exitCode = 0;
        var startupInfo = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)) };
        startupInfo.dwFlags = StartfUseShowWindow;
        startupInfo.wShowWindow = SwShowNormal;

        bool created;
        ProcessInformation info;
        try
        {
            created = CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                bInheritHandles: false, dwCreationFlags: CreateNewConsole, lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: dir, lpStartupInfo: ref startupInfo, lpProcessInformation: out info);
        }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI] CreateProcess 起确认窗口失败: {ex.Message}");
            return false;
        }

        if (!created)
        {
            Log.Debug($"[SLDataAPI] CreateProcess 起确认窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
            return false;
        }

        try
        {
            uint wait = WaitForSingleObject(info.hProcess, (uint)waitMs);
            if (wait != WaitObject0)
            {
                // 子进程自己应该已经超时退出；没退就关掉窗口，按超时处理
                try { TerminateProcess(info.hProcess, (uint)ConfirmWindowProtocol.ExitTimedOut); }
                catch { /* 已退出 */ }
                WaitForSingleObject(info.hProcess, 2000);
            }

            if (!GetExitCodeProcess(info.hProcess, out uint code))
                return false;
            exitCode = unchecked((int)code);
            return true;
        }
        finally
        {
            if (info.hThread != IntPtr.Zero) CloseHandle(info.hThread);
            if (info.hProcess != IntPtr.Zero) CloseHandle(info.hProcess);
        }
    }

    /// <summary>
    /// 兜底：ShellExecute（UseShellExecute=true）默认也会给控制台程序开新窗口
    /// （不带 SEE_MASK_NO_CONSOLE 就等同 CREATE_NEW_CONSOLE），同样不会借用 LocalAdmin 的窗口。
    /// </summary>
    private static bool TryRunViaShellExecute(string comSpec, string dir, int waitMs, out int exitCode)
    {
        exitCode = 0;
        try
        {
            var psi = new ProcessStartInfo(comSpec, "/c " + ConfirmWindowProtocol.ScriptFileName)
            {
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = dir,
            };

            using (Process? process = Process.Start(psi))
            {
                if (process == null)
                    return false;
                if (!process.WaitForExit(waitMs))
                {
                    // 子进程本该自己倒计时结束退出；没退就关掉窗口，按超时处理
                    try { process.Kill(); } catch { /* 已退出 */ }
                    try { process.WaitForExit(2000); } catch { /* 忽略 */ }
                    exitCode = ConfirmWindowProtocol.ExitTimedOut;
                    return true;
                }
                exitCode = process.ExitCode;
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI] ShellExecute 起确认窗口失败: {ex.Message}");
            return false;
        }
    }

    // ────────────── P/Invoke ──────────────

    private const uint CreateNewConsole = 0x00000010;
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwShowNormal = 1;
    private const uint WaitObject0 = 0x00000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
