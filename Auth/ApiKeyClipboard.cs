using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SLDataAPI.Auth;

/// <summary>
/// 尽力把明文复制到 Windows 剪贴板。后台线程 + 超时，失败静默，绝不阻塞游戏/LocalAdmin 主线程。
/// 不用 MsgBox / VBS / 网页 / 全屏 UI；密钥不进命令行参数。
/// </summary>
internal static class ApiKeyClipboard
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>排队后台尝试；调用方立即返回。</summary>
    public static void TryCopyInBackground(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return;
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return;

        // 拷一份，避免调用方随后清掉 plaintext 时后台读到空。
        string copy = plaintext;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { TryCopyWithTimeout(copy); }
            catch { /* 静默 */ }
        });
    }

    private static void TryCopyWithTimeout(string plaintext)
    {
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                if (!NativeClipboard.TrySetUnicodeText(plaintext))
                    TryClipExeStdin(plaintext);
            }
            catch
            {
                try { TryClipExeStdin(plaintext); }
                catch { /* 静默 */ }
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "SLDataAPI.ApiKeyClipboard",
        };

        try { thread.SetApartmentState(ApartmentState.STA); }
        catch { /* 非 Windows 或已启动则忽略 */ }

        thread.Start();
        done.Wait(Timeout);
        // 超时不 Abort：STA 线程随后自行结束，避免搅主线程。
    }

    /// <summary>clip.exe 标准输入，密钥不出现在命令行。</summary>
    private static void TryClipExeStdin(string plaintext)
    {
        string clip = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "clip.exe");
        if (!File.Exists(clip))
            return;

        var psi = new ProcessStartInfo
        {
            FileName = clip,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            return;

        try
        {
            proc.StandardInput.Write(plaintext);
            proc.StandardInput.Close();
            if (!proc.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { proc.Kill(); } catch { /* 静默 */ }
            }
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { /* 静默 */ }
        }
    }

    private static class NativeClipboard
    {
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        public static bool TrySetUnicodeText(string text)
        {
            if (!OpenClipboard(IntPtr.Zero))
                return false;

            IntPtr hGlobal = IntPtr.Zero;
            try
            {
                if (!EmptyClipboard())
                    return false;

                byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
                if (hGlobal == IntPtr.Zero)
                    return false;

                IntPtr locked = GlobalLock(hGlobal);
                if (locked == IntPtr.Zero)
                    return false;

                try { Marshal.Copy(bytes, 0, locked, bytes.Length); }
                finally { GlobalUnlock(hGlobal); }

                if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
                    return false;

                // 所有权交给系统，不要 GlobalFree。
                hGlobal = IntPtr.Zero;
                return true;
            }
            finally
            {
                if (hGlobal != IntPtr.Zero)
                    GlobalFree(hGlobal);
                CloseClipboard();
            }
        }
    }
}
