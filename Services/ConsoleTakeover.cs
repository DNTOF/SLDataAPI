using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SLDataAPI.Services;

/// <summary>被接管的整屏控制台；Dispose 负责把控制台恢复成接管前的样子。</summary>
internal interface IConsoleTakeover : IConfirmScreen, IDisposable
{
}

/// <summary>
/// 整屏控制台接管（Linux 式全屏 TUI）。优先级：
///   1. Win32 控制台 API（主路径，覆盖绝大多数 Windows + LocalAdmin 窗口的部署）：
///      ReadConsoleOutput 完整备份可见屏幕缓冲 + 光标位置/形状 + 文本属性 + 输入模式，
///      绘制期间用原始输入模式读按键，结束后逐项写回——退出后 LocalAdmin 的输出原样还在。
///      本进程没有自己的控制台时（LocalAdmin 以无窗口方式拉起游戏进程），
///      先 AttachConsole(ATTACH_PARENT_PROCESS) 借用父进程（LocalAdmin）的控制台，用完 FreeConsole。
///   2. ANSI 备用屏幕缓冲（非 Windows / Win32 取用失败但 .NET 控制台可用）：
///      DECSET 1049 切到备用屏，退出时切回，等价于 vim/htop 的行为。
/// 两条路径都拿不到 → 调用方退化到行模式提示，再退化到 MessageBox。
/// </summary>
internal static class ConsoleTakeover
{
    public static bool TryAcquire(out IConsoleTakeover? takeover)
    {
        if (WindowsConsoleTakeover.TryAcquire(out takeover))
            return true;
        if (AnsiConsoleTakeover.TryAcquire(out takeover))
            return true;
        takeover = null;
        return false;
    }

    // ────────────── Win32 路径 ──────────────

    private sealed class WindowsConsoleTakeover : IConsoleTakeover
    {
        private const int KeyEvent = 0x0001;
        private const int AttachParentProcess = -1;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        private IntPtr _out = InvalidHandle;
        private IntPtr _in = InvalidHandle;
        private bool _attachedParentConsole;

        private CharInfo[]? _savedCells;
        private SmallRect _window;
        private Coord _savedCursorPos;
        private ushort _savedAttributes;
        private ConsoleCursorInfo _savedCursorInfo;
        private bool _savedCursorInfoValid;
        private uint _savedInputMode;
        private bool _savedInputModeValid;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public PanelCharset Charset { get; private set; } = PanelCharset.Unicode;

        public static bool TryAcquire(out IConsoleTakeover? takeover)
        {
            takeover = null;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return false;

            var instance = new WindowsConsoleTakeover();
            try
            {
                if (instance.Initialize())
                {
                    takeover = instance;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"[SLDataAPI] Win32 控制台接管不可用: {ex.Message}");
            }

            try { instance.Dispose(); } catch { /* 释放失败无所谓，走下一条路径 */ }
            return false;
        }

        private bool Initialize()
        {
            if (!OpenHandles())
            {
                // 本进程无控制台：借用父进程（LocalAdmin）的窗口
                if (!AttachConsole(AttachParentProcess))
                    return false;
                _attachedParentConsole = true;
                if (!OpenHandles())
                    return false;
            }

            if (!GetConsoleScreenBufferInfo(_out, out ConsoleScreenBufferInfo info))
                return false;

            _window = info.srWindow;
            Width = _window.Right - _window.Left + 1;
            Height = _window.Bottom - _window.Top + 1;
            if (Width < ConfirmPanelLayout.MinWidth || Height < ConfirmPanelLayout.MinHeight)
                return false;

            _savedCursorPos = info.dwCursorPosition;
            _savedAttributes = info.wAttributes;
            Charset = DetectCharset();

            // 完整备份可见区域：退出时按单元格写回，LocalAdmin 的历史输出不会被吃掉
            var cells = new CharInfo[Width * Height];
            SmallRect region = _window;
            if (!ReadConsoleOutput(_out, cells, new Coord((short)Width, (short)Height), new Coord(0, 0), ref region))
                return false;
            _savedCells = cells;

            if (GetConsoleCursorInfo(_out, out _savedCursorInfo))
            {
                _savedCursorInfoValid = true;
                ConsoleCursorInfo hidden = _savedCursorInfo;
                hidden.bVisible = 0;
                SetConsoleCursorInfo(_out, ref hidden);
            }

            if (_in != InvalidHandle && GetConsoleMode(_in, out _savedInputMode))
            {
                _savedInputModeValid = true;
                // 原始按键读取：关行输入/回显/处理输入（Ctrl+C 期间不再直接杀进程，Esc 即取消）
                SetConsoleMode(_in, 0);
                FlushConsoleInputBuffer(_in);
            }

            return true;
        }

        private bool OpenHandles()
        {
            _out = CreateFile("CONOUT$", GenericRead | GenericWrite,
                FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            _in = CreateFile("CONIN$", GenericRead | GenericWrite,
                FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

            if (_out == InvalidHandle)
            {
                CloseHandles();
                return false;
            }
            return true;
        }

        private void CloseHandles()
        {
            if (_out != InvalidHandle) { CloseHandle(_out); _out = InvalidHandle; }
            if (_in != InvalidHandle) { CloseHandle(_in); _in = InvalidHandle; }
        }

        /// <summary>代码页不支持制表符时退化到 ASCII 边框（否则边框会变成乱码）。</summary>
        private PanelCharset DetectCharset()
        {
            try
            {
                uint cp = GetConsoleOutputCP();
                return cp is 65001 or 936 or 950 or 932 or 949 or 1200 or 1201
                    ? PanelCharset.Unicode
                    : PanelCharset.Ascii;
            }
            catch
            {
                return PanelCharset.Ascii;
            }
        }

        public void Draw(IReadOnlyList<PanelRow> rows)
        {
            if (_out == InvalidHandle) return;

            for (int y = 0; y < Height; y++)
            {
                PanelRow row = y < rows.Count ? rows[y] : new PanelRow("", PanelRowStyle.Blank);
                // 最后一行少写一格：写满整行会触发控制台滚动，把备份区域挪位
                int cells = y == Height - 1 ? Width - 1 : Width;
                if (cells <= 0) continue;

                string text = ConfirmPanelLayout.FitToWidth(row.Text, cells);
                SetConsoleTextAttribute(_out, AttributeFor(row.Style));
                SetConsoleCursorPosition(_out, new Coord(_window.Left, (short)(_window.Top + y)));
                WriteConsole(_out, text, (uint)text.Length, out _, IntPtr.Zero);
            }
        }

        public bool TryReadKey(int timeoutMs, out ConfirmKey key)
        {
            key = ConfirmKey.None;
            if (_in == InvalidHandle) return false;

            int waited = 0;
            while (waited <= timeoutMs)
            {
                if (!GetNumberOfConsoleInputEvents(_in, out uint pending))
                    return false;

                if (pending == 0)
                {
                    Thread.Sleep(10);
                    waited += 10;
                    continue;
                }

                var records = new InputRecord[Math.Min(pending, 32u)];
                if (!ReadConsoleInput(_in, records, (uint)records.Length, out uint read))
                    return false;

                for (int i = 0; i < read; i++)
                {
                    if (records[i].EventType != KeyEvent) continue;
                    if (records[i].KeyEvent.bKeyDown == 0) continue;

                    ConfirmKey mapped = ConfirmInputMap.FromVirtualKey(
                        records[i].KeyEvent.wVirtualKeyCode, records[i].KeyEvent.UnicodeChar);
                    if (mapped == ConfirmKey.None) continue;

                    key = mapped;
                    return true;
                }
            }
            return false;
        }

        private static ushort AttributeFor(PanelRowStyle style) => style switch
        {
            // 前景 | 背景（背景统一深蓝，整屏一眼可辨是接管态）
            PanelRowStyle.Title => 0x1F,     // 亮白
            PanelRowStyle.Warning => 0x1E,   // 亮黄
            PanelRowStyle.Choice => 0x1F,
            PanelRowStyle.Frame => 0x1B,     // 亮青
            PanelRowStyle.Separator => 0x1B,
            PanelRowStyle.Hint => 0x17,      // 灰白
            _ => 0x17,
        };

        public void Dispose()
        {
            try
            {
                if (_out != InvalidHandle)
                {
                    if (_savedCells != null)
                    {
                        SmallRect region = _window;
                        WriteConsoleOutput(_out, _savedCells, new Coord((short)Width, (short)Height),
                            new Coord(0, 0), ref region);
                    }
                    SetConsoleTextAttribute(_out, _savedAttributes);
                    SetConsoleCursorPosition(_out, _savedCursorPos);
                    if (_savedCursorInfoValid)
                        SetConsoleCursorInfo(_out, ref _savedCursorInfo);
                }

                if (_in != InvalidHandle && _savedInputModeValid)
                {
                    FlushConsoleInputBuffer(_in);
                    SetConsoleMode(_in, _savedInputMode);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[SLDataAPI] 控制台状态恢复失败（显示可能残留，功能不受影响）: {ex.Message}");
            }
            finally
            {
                CloseHandles();
                if (_attachedParentConsole)
                {
                    _attachedParentConsole = false;
                    try { FreeConsole(); } catch { /* 忽略 */ }
                }
                _savedCells = null;
            }
        }

        // ────────────── P/Invoke ──────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord
        {
            public short X;
            public short Y;
            public Coord(short x, short y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SmallRect
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ConsoleScreenBufferInfo
        {
            public Coord dwSize;
            public Coord dwCursorPosition;
            public ushort wAttributes;
            public SmallRect srWindow;
            public Coord dwMaximumWindowSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ConsoleCursorInfo
        {
            public uint dwSize;
            public int bVisible;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CharInfo
        {
            public char UnicodeChar;
            public ushort Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyEventRecord
        {
            public int bKeyDown;
            public ushort wRepeatCount;
            public ushort wVirtualKeyCode;
            public ushort wVirtualScanCode;
            public char UnicodeChar;
            public uint dwControlKeyState;
        }

        // INPUT_RECORD：WORD EventType + 4 字节对齐的联合体（KEY_EVENT_RECORD 是其中最大成员）
        [StructLayout(LayoutKind.Sequential)]
        private struct InputRecord
        {
            public ushort EventType;
            public ushort Padding;
            public KeyEventRecord KeyEvent;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string fileName, uint access, uint share,
            IntPtr security, uint creationDisposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleOutputCP();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out ConsoleScreenBufferInfo info);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleCursorInfo(IntPtr handle, out ConsoleCursorInfo info);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCursorInfo(IntPtr handle, ref ConsoleCursorInfo info);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCursorPosition(IntPtr handle, Coord position);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleTextAttribute(IntPtr handle, ushort attributes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WriteConsole(IntPtr handle, string buffer, uint charsToWrite,
            out uint charsWritten, IntPtr reserved);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ReadConsoleOutput(IntPtr handle, [Out] CharInfo[] buffer,
            Coord bufferSize, Coord bufferCoord, ref SmallRect region);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WriteConsoleOutput(IntPtr handle, CharInfo[] buffer,
            Coord bufferSize, Coord bufferCoord, ref SmallRect region);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushConsoleInputBuffer(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNumberOfConsoleInputEvents(IntPtr handle, out uint count);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ReadConsoleInput(IntPtr handle, [Out] InputRecord[] buffer,
            uint length, out uint eventsRead);
    }

    // ────────────── ANSI 备用屏幕路径 ──────────────

    private sealed class AnsiConsoleTakeover : IConsoleTakeover
    {
        private const string EnterAltScreen = "\u001b[?1049h";
        private const string LeaveAltScreen = "\u001b[?1049l";

        private ConsoleColor _savedForeground;
        private ConsoleColor _savedBackground;
        private bool _cursorHidden;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public PanelCharset Charset => PanelCharset.Unicode;

        public static bool TryAcquire(out IConsoleTakeover? takeover)
        {
            takeover = null;
            try
            {
                if (Console.IsOutputRedirected || Console.IsInputRedirected)
                    return false;

                var instance = new AnsiConsoleTakeover
                {
                    Width = Console.WindowWidth,
                    Height = Console.WindowHeight,
                };
                if (instance.Width < ConfirmPanelLayout.MinWidth || instance.Height < ConfirmPanelLayout.MinHeight)
                    return false;

                instance._savedForeground = Console.ForegroundColor;
                instance._savedBackground = Console.BackgroundColor;
                Console.Write(EnterAltScreen);
                try { Console.CursorVisible = false; instance._cursorHidden = true; }
                catch { /* 某些终端不支持，忽略 */ }

                takeover = instance;
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug($"[SLDataAPI] ANSI 控制台接管不可用: {ex.Message}");
                return false;
            }
        }

        public void Draw(IReadOnlyList<PanelRow> rows)
        {
            try
            {
                for (int y = 0; y < Height; y++)
                {
                    PanelRow row = y < rows.Count ? rows[y] : new PanelRow("", PanelRowStyle.Blank);
                    int cells = y == Height - 1 ? Width - 1 : Width;
                    if (cells <= 0) continue;

                    Console.SetCursorPosition(0, y);
                    Console.BackgroundColor = ConsoleColor.DarkBlue;
                    Console.ForegroundColor = ForegroundFor(row.Style);
                    Console.Write(ConfirmPanelLayout.FitToWidth(row.Text, cells));
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"[SLDataAPI] 面板绘制失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 注意：非 Windows 终端上单独一个 Esc 要等转义序列消歧，KeyAvailable 可能一直不报，
        /// 表现为"按 Esc 没反应直到倒计时结束"。两种结局都是中止操作，安全语义不变；
        /// Win32 路径按 VK_ESCAPE 直接拿到键事件，不存在这个问题。
        /// </summary>
        public bool TryReadKey(int timeoutMs, out ConfirmKey key)
        {
            key = ConfirmKey.None;
            int waited = 0;
            while (waited <= timeoutMs)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        ConfirmKey mapped = ConfirmInputMap.FromConsoleKey(Console.ReadKey(intercept: true));
                        if (mapped != ConfirmKey.None)
                        {
                            key = mapped;
                            return true;
                        }
                        continue;
                    }
                }
                catch
                {
                    return false;
                }
                Thread.Sleep(10);
                waited += 10;
            }
            return false;
        }

        private static ConsoleColor ForegroundFor(PanelRowStyle style) => style switch
        {
            PanelRowStyle.Title => ConsoleColor.White,
            PanelRowStyle.Warning => ConsoleColor.Yellow,
            PanelRowStyle.Choice => ConsoleColor.White,
            PanelRowStyle.Frame => ConsoleColor.Cyan,
            PanelRowStyle.Separator => ConsoleColor.Cyan,
            _ => ConsoleColor.Gray,
        };

        public void Dispose()
        {
            try
            {
                if (_cursorHidden)
                {
                    try { Console.CursorVisible = true; } catch { /* 忽略 */ }
                }
                Console.Write(LeaveAltScreen);
                Console.ForegroundColor = _savedForeground;
                Console.BackgroundColor = _savedBackground;
            }
            catch (Exception ex)
            {
                Log.Warn($"[SLDataAPI] 控制台状态恢复失败（显示可能残留，功能不受影响）: {ex.Message}");
            }
        }
    }
}
