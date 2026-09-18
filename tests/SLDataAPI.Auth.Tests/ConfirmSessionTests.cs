using System;
using System.Collections.Generic;
using System.Linq;
using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class ConfirmInputMapTests
{
    private const int VkY = 0x59;
    private const int VkN = 0x4E;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkTab = 0x09;
    private const int VkSpace = 0x20;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkF1 = 0x70;

    [Theory]
    [InlineData(VkY, 'y', ConfirmKey.Yes)]
    [InlineData(VkY, 'Y', ConfirmKey.Yes)]
    [InlineData(VkN, 'n', ConfirmKey.No)]
    [InlineData(VkReturn, '\r', ConfirmKey.Accept)]
    [InlineData(VkEscape, (char)27, ConfirmKey.Cancel)]
    [InlineData(VkTab, '\t', ConfirmKey.Toggle)]
    [InlineData(VkSpace, ' ', ConfirmKey.Toggle)]
    [InlineData(VkLeft, '\0', ConfirmKey.Left)]
    [InlineData(VkUp, '\0', ConfirmKey.Left)]
    [InlineData(VkRight, '\0', ConfirmKey.Right)]
    [InlineData(VkDown, '\0', ConfirmKey.Right)]
    [InlineData(VkF1, '\0', ConfirmKey.None)]
    [InlineData(0, 'y', ConfirmKey.Yes)] // 虚拟键码缺失时靠字符兜底
    [InlineData(0, 'q', ConfirmKey.None)]
    public void FromVirtualKey_MapsWin32Keys(int virtualKey, char character, ConfirmKey expected)
    {
        Assert.Equal(expected, ConfirmInputMap.FromVirtualKey(virtualKey, character));
    }

    [Theory]
    [InlineData(ConsoleKey.Y, 'y', ConfirmKey.Yes)]
    [InlineData(ConsoleKey.N, 'n', ConfirmKey.No)]
    [InlineData(ConsoleKey.Enter, '\r', ConfirmKey.Accept)]
    [InlineData(ConsoleKey.Escape, (char)27, ConfirmKey.Cancel)]
    [InlineData(ConsoleKey.Tab, '\t', ConfirmKey.Toggle)]
    [InlineData(ConsoleKey.Spacebar, ' ', ConfirmKey.Toggle)]
    [InlineData(ConsoleKey.LeftArrow, '\0', ConfirmKey.Left)]
    [InlineData(ConsoleKey.RightArrow, '\0', ConfirmKey.Right)]
    [InlineData(ConsoleKey.F5, '\0', ConfirmKey.None)]
    public void FromConsoleKey_MapsDotNetKeys(ConsoleKey key, char character, ConfirmKey expected)
    {
        var info = new ConsoleKeyInfo(character, key, false, false, false);
        Assert.Equal(expected, ConfirmInputMap.FromConsoleKey(info));
    }
}

/// <summary>按脚本回放按键的假屏幕：记录每次重绘，并按时间片推进假时钟。</summary>
internal sealed class FakeScreen : IConfirmScreen, IConfirmClock
{
    private readonly Queue<ConfirmKey?> _script;

    public FakeScreen(params ConfirmKey?[] script)
    {
        _script = new Queue<ConfirmKey?>(script);
    }

    public int Width { get; set; } = 80;
    public int Height { get; set; } = 25;
    public PanelCharset Charset { get; set; } = PanelCharset.Unicode;
    public DateTime UtcNow { get; private set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public List<IReadOnlyList<PanelRow>> Draws { get; } = new List<IReadOnlyList<PanelRow>>();

    public void Draw(IReadOnlyList<PanelRow> rows) => Draws.Add(rows);

    public bool TryReadKey(int timeoutMs, out ConfirmKey key)
    {
        key = ConfirmKey.None;
        // 无论有没有按键，一个时间片都会过去——超时判定因此是确定性的
        UtcNow = UtcNow.AddMilliseconds(timeoutMs);
        if (_script.Count == 0)
            return false;

        ConfirmKey? next = _script.Dequeue();
        if (next == null)
            return false;

        key = next.Value;
        return true;
    }

    public string FlattenLastDraw() =>
        Draws.Count == 0 ? "" : string.Join("\n", Draws[^1].Select(r => r.Text));
}

public class ConfirmSessionTests
{
    private static ConfirmPanelModel Model() => new ConfirmPanelModel
    {
        Action = ConfirmAction.Create,
        KeyId = "platform-a",
        Template = "admin",
    };

    private static ConfirmOutcome Run(FakeScreen screen, int timeoutSeconds = 20) =>
        ConfirmSession.Run(screen, Model(), timeoutSeconds, screen);

    [Fact]
    public void PressingY_Confirms()
    {
        Assert.Equal(ConfirmOutcome.Confirmed, Run(new FakeScreen(ConfirmKey.Yes)));
    }

    [Fact]
    public void PressingN_Denies()
    {
        Assert.Equal(ConfirmOutcome.Denied, Run(new FakeScreen(ConfirmKey.No)));
    }

    [Fact]
    public void PressingEscape_Denies()
    {
        Assert.Equal(ConfirmOutcome.Denied, Run(new FakeScreen(ConfirmKey.Cancel)));
    }

    [Fact]
    public void EnterWithoutMoving_Denies_BecauseCancelIsDefault()
    {
        Assert.Equal(ConfirmOutcome.Denied, Run(new FakeScreen(ConfirmKey.Accept)));
    }

    [Fact]
    public void LeftThenEnter_Confirms()
    {
        Assert.Equal(ConfirmOutcome.Confirmed, Run(new FakeScreen(ConfirmKey.Left, ConfirmKey.Accept)));
    }

    [Fact]
    public void LeftThenRightThenEnter_Denies()
    {
        Assert.Equal(ConfirmOutcome.Denied,
            Run(new FakeScreen(ConfirmKey.Left, ConfirmKey.Right, ConfirmKey.Accept)));
    }

    [Fact]
    public void TabTogglesSelection()
    {
        Assert.Equal(ConfirmOutcome.Confirmed, Run(new FakeScreen(ConfirmKey.Toggle, ConfirmKey.Accept)));
        Assert.Equal(ConfirmOutcome.Denied,
            Run(new FakeScreen(ConfirmKey.Toggle, ConfirmKey.Toggle, ConfirmKey.Accept)));
    }

    [Fact]
    public void NoInput_TimesOut()
    {
        var screen = new FakeScreen();
        Assert.Equal(ConfirmOutcome.TimedOut, ConfirmSession.Run(screen, Model(), 2, screen));
    }

    [Fact]
    public void UnrecognisedKeys_DoNotDecide_AndStillTimeOut()
    {
        var script = Enumerable.Repeat((ConfirmKey?)null, 40).ToArray();
        var screen = new FakeScreen(script);
        Assert.Equal(ConfirmOutcome.TimedOut, ConfirmSession.Run(screen, Model(), 1, screen));
    }

    [Fact]
    public void SelectionChange_TriggersRedrawWithMovedMarker()
    {
        var screen = new FakeScreen(ConfirmKey.Left, ConfirmKey.No);
        Run(screen);
        Assert.True(screen.Draws.Count >= 2);
        string first = string.Join("\n", screen.Draws[0].Select(r => r.Text));
        Assert.Contains("> [ 取消 (N) ] <", first);
        Assert.Contains(screen.Draws, d =>
            string.Join("\n", d.Select(r => r.Text)).Contains("> [ 确认 (Y) ] <"));
    }

    [Fact]
    public void CountdownRedrawsOncePerSecond()
    {
        // 3 秒超时、100ms 时间片：倒计时应逐秒刷新，而不是每个时间片重绘
        var screen = new FakeScreen();
        ConfirmSession.Run(screen, Model(), 3, screen);
        Assert.InRange(screen.Draws.Count, 3, 5);
        Assert.Contains("3 秒", string.Join("\n", screen.Draws[0].Select(r => r.Text)));
    }

    [Fact]
    public void FirstDrawMatchesScreenGeometry()
    {
        var screen = new FakeScreen(ConfirmKey.No) { Width = 100, Height = 30 };
        Run(screen);
        var drawn = screen.Draws[0];
        Assert.Equal(30, drawn.Count);
        Assert.All(drawn, r => Assert.Equal(100, ConfirmPanelLayout.DisplayWidth(r.Text)));
    }

    [Fact]
    public void AsciiScreen_GetsAsciiFrame()
    {
        var screen = new FakeScreen(ConfirmKey.No) { Charset = PanelCharset.Ascii };
        Run(screen);
        Assert.DoesNotContain('│', screen.FlattenLastDraw());
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ConfirmSession.Run(null!, Model(), 10));
        Assert.Throws<ArgumentNullException>(() => ConfirmSession.Run(new FakeScreen(), null!, 10));
    }
}
