using System;
using System.Collections.Generic;

namespace SLDataAPI.Services;

/// <summary>确认面板可识别的按键语义。</summary>
public enum ConfirmKey
{
    None,
    Yes,
    No,
    Left,
    Right,
    Toggle,
    Accept,
    Cancel,
}

/// <summary>确认会话的结果。</summary>
public enum ConfirmOutcome
{
    Confirmed,
    Denied,
    TimedOut,
}

/// <summary>被接管的整屏控制台（Win32 / ANSI 实现各自满足此接口，测试用假实现）。</summary>
public interface IConfirmScreen
{
    int Width { get; }
    int Height { get; }
    PanelCharset Charset { get; }

    /// <summary>整屏重绘（行数/列宽由 Width、Height 决定）。</summary>
    void Draw(IReadOnlyList<PanelRow> rows);

    /// <summary>等待一个可识别的按键，最多 <paramref name="timeoutMs"/> 毫秒；无按键返回 false。</summary>
    bool TryReadKey(int timeoutMs, out ConfirmKey key);
}

/// <summary>可注入的时钟（倒计时与超时判定用，便于确定性测试）。</summary>
public interface IConfirmClock
{
    DateTime UtcNow { get; }
}

/// <summary>纯按键映射：把 Win32 虚拟键码 / .NET ConsoleKeyInfo 归一成面板语义。</summary>
public static class ConfirmInputMap
{
    private const int VkTab = 0x09;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkN = 0x4E;
    private const int VkY = 0x59;

    /// <summary>Win32 KEY_EVENT_RECORD 的 wVirtualKeyCode + UnicodeChar。</summary>
    public static ConfirmKey FromVirtualKey(int virtualKey, char character)
    {
        switch (virtualKey)
        {
            case VkY: return ConfirmKey.Yes;
            case VkN: return ConfirmKey.No;
            case VkReturn: return ConfirmKey.Accept;
            case VkEscape: return ConfirmKey.Cancel;
            case VkTab:
            case VkSpace: return ConfirmKey.Toggle;
            case VkLeft:
            case VkUp: return ConfirmKey.Left;
            case VkRight:
            case VkDown: return ConfirmKey.Right;
        }
        return FromChar(character);
    }

    /// <summary>.NET 控制台按键（ANSI / 非 Windows 路径）。</summary>
    public static ConfirmKey FromConsoleKey(ConsoleKeyInfo info)
    {
        switch (info.Key)
        {
            case ConsoleKey.Y: return ConfirmKey.Yes;
            case ConsoleKey.N: return ConfirmKey.No;
            case ConsoleKey.Enter: return ConfirmKey.Accept;
            case ConsoleKey.Escape: return ConfirmKey.Cancel;
            case ConsoleKey.Tab:
            case ConsoleKey.Spacebar: return ConfirmKey.Toggle;
            case ConsoleKey.LeftArrow:
            case ConsoleKey.UpArrow: return ConfirmKey.Left;
            case ConsoleKey.RightArrow:
            case ConsoleKey.DownArrow: return ConfirmKey.Right;
        }
        return FromChar(info.KeyChar);
    }

    /// <summary>裸字符兜底（虚拟键码不可用或为 0 时）。</summary>
    public static ConfirmKey FromChar(char character)
    {
        switch (character)
        {
            case 'y':
            case 'Y': return ConfirmKey.Yes;
            case 'n':
            case 'N': return ConfirmKey.No;
            case '\r':
            case '\n': return ConfirmKey.Accept;
            case '\t':
            case ' ': return ConfirmKey.Toggle;
            case (char)27: return ConfirmKey.Cancel;
        }
        return ConfirmKey.None;
    }
}

/// <summary>
/// 确认会话的交互循环：整屏重绘 → 轮询按键 → 倒计时刷新 → 得出结论。
/// 纯逻辑（屏幕与时钟都是注入的接口），可脱离游戏 DLL 与真实控制台单元测试。
///
/// 默认选中"取消"：误按 Enter 不会铸出密钥。
/// </summary>
public static class ConfirmSession
{
    /// <summary>单次按键轮询的时间片；同时决定倒计时刷新的粒度。</summary>
    public const int PollSliceMs = 100;

    public static ConfirmOutcome Run(
        IConfirmScreen screen,
        ConfirmPanelModel model,
        int timeoutSeconds,
        IConfirmClock? clock = null)
    {
        if (screen == null) throw new ArgumentNullException(nameof(screen));
        if (model == null) throw new ArgumentNullException(nameof(model));

        clock ??= SystemClock.Instance;
        DateTime deadline = clock.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));

        bool confirmSelected = false;
        int shownSeconds = -1;

        while (true)
        {
            int secondsLeft = (int)Math.Ceiling((deadline - clock.UtcNow).TotalSeconds);
            if (secondsLeft <= 0)
                return ConfirmOutcome.TimedOut;

            if (secondsLeft != shownSeconds)
            {
                Draw(screen, model, secondsLeft, confirmSelected);
                shownSeconds = secondsLeft;
            }

            if (!screen.TryReadKey(PollSliceMs, out ConfirmKey key))
                continue;

            switch (key)
            {
                case ConfirmKey.Yes:
                    return ConfirmOutcome.Confirmed;

                case ConfirmKey.No:
                case ConfirmKey.Cancel:
                    return ConfirmOutcome.Denied;

                case ConfirmKey.Accept:
                    return confirmSelected ? ConfirmOutcome.Confirmed : ConfirmOutcome.Denied;

                case ConfirmKey.Left:
                    confirmSelected = true;
                    break;

                case ConfirmKey.Right:
                    confirmSelected = false;
                    break;

                case ConfirmKey.Toggle:
                    confirmSelected = !confirmSelected;
                    break;

                default:
                    continue;
            }

            Draw(screen, model, secondsLeft, confirmSelected);
            shownSeconds = secondsLeft;
        }
    }

    private static void Draw(IConfirmScreen screen, ConfirmPanelModel model, int secondsLeft, bool confirmSelected) =>
        screen.Draw(ConfirmPanelLayout.Render(
            model, screen.Width, screen.Height, screen.Charset, secondsLeft, confirmSelected));

    private sealed class SystemClock : IConfirmClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
