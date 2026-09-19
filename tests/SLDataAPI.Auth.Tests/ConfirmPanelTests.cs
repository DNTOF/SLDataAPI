using System;
using System.Collections.Generic;
using System.Linq;
using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class DisplayWidthTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("id：foo", 7)] // 全角冒号占 2 列
    [InlineData("确认", 4)]
    [InlineData("[ 确认 (Y) ]", 12)]
    public void DisplayWidth_CountsWideCharsAsTwo(string text, int expected)
    {
        Assert.Equal(expected, ConfirmPanelLayout.DisplayWidth(text));
    }

    [Fact]
    public void DisplayWidth_IgnoresControlChars()
    {
        Assert.Equal(3, ConfirmPanelLayout.DisplayWidth("a\r\nbc"));
    }

    [Fact]
    public void FitToWidth_PadsShortText()
    {
        string fit = ConfirmPanelLayout.FitToWidth("ab", 6);
        Assert.Equal("ab    ", fit);
        Assert.Equal(6, ConfirmPanelLayout.DisplayWidth(fit));
    }

    [Fact]
    public void FitToWidth_TruncatesLongText()
    {
        Assert.Equal("abcd", ConfirmPanelLayout.FitToWidth("abcdef", 4));
    }

    [Fact]
    public void FitToWidth_NeverSplitsWideChar()
    {
        // "确认" 占 4 列，裁到 3 列时只能放下一个全角字 + 一个补位空格
        string fit = ConfirmPanelLayout.FitToWidth("确认", 3);
        Assert.Equal("确 ", fit);
        Assert.Equal(3, ConfirmPanelLayout.DisplayWidth(fit));
    }

    [Fact]
    public void FitToWidth_AlwaysExactWidth()
    {
        foreach (string sample in new[] { "", "a", "确认取消", "mixed 混排 text", new string('x', 200) })
        {
            for (int w = 1; w <= 20; w++)
                Assert.Equal(w, ConfirmPanelLayout.DisplayWidth(ConfirmPanelLayout.FitToWidth(sample, w)));
        }
    }

    [Fact]
    public void Center_PutsTextInMiddle()
    {
        Assert.Equal("  ab  ", ConfirmPanelLayout.Center("ab", 6));
    }

    [Fact]
    public void Wrap_HardBreaksCjkWithoutSpaces()
    {
        var lines = ConfirmPanelLayout.Wrap("确认取消确认取消", 4);
        Assert.Equal(4, lines.Count);
        Assert.All(lines, l => Assert.True(ConfirmPanelLayout.DisplayWidth(l) <= 4));
    }

    [Fact]
    public void Wrap_BreaksOnSpacesWhenAvailable()
    {
        var lines = ConfirmPanelLayout.Wrap("alpha beta gamma", 11);
        Assert.Equal(2, lines.Count);
        Assert.Equal("alpha beta", lines[0]);
        Assert.Equal("gamma", lines[1]);
    }

    [Fact]
    public void Wrap_EmptyTextYieldsOneEmptyLine()
    {
        Assert.Single(ConfirmPanelLayout.Wrap("", 10));
    }

    [Fact]
    public void Wrap_PrefersHardBreak_WhenSpaceBreakWouldLeaveStubLine()
    {
        // 中英混排：最后一个空格在第 10 列，按空格断会留下一行只有几个字
        var lines = ConfirmPanelLayout.Wrap("[!] 新 Key 的明文只显示这一次，关闭后无法找回。", 40);
        Assert.True(ConfirmPanelLayout.DisplayWidth(lines[0]) >= 20,
            $"首行过短：\"{lines[0]}\"（{ConfirmPanelLayout.DisplayWidth(lines[0])} 列）");
        Assert.All(lines, l => Assert.True(ConfirmPanelLayout.DisplayWidth(l) <= 40));
    }

    [Theory]
    [InlineData('。')]
    [InlineData('，')]
    [InlineData('）')]
    public void IsNoBreakBefore_CoversClosingPunctuation(char c)
    {
        Assert.True(ConfirmPanelLayout.IsNoBreakBefore(c));
    }

    [Fact]
    public void IsNoBreakBefore_AllowsOrdinaryChars()
    {
        Assert.False(ConfirmPanelLayout.IsNoBreakBefore('新'));
        Assert.False(ConfirmPanelLayout.IsNoBreakBefore('a'));
    }

    [Fact]
    public void Wrap_DoesNotOrphanClosingPunctuation()
    {
        // 恰好在句号前断行时，把前一个字一起带下来，句号不会独占一行
        var lines = ConfirmPanelLayout.Wrap("关闭后无法找回。", 14);
        Assert.Equal(2, lines.Count);
        Assert.NotEqual("。", lines[1]);
        Assert.StartsWith("回", lines[1]);
    }
}

public class ConfirmPanelRenderTests
{
    private static ConfirmPanelModel CreateModel() => new ConfirmPanelModel
    {
        Action = ConfirmAction.Create,
        KeyId = "platform-a",
        Template = "admin",
        Note = "上游平台",
    };

    private static ConfirmPanelModel RevokeModel() => new ConfirmPanelModel
    {
        Action = ConfirmAction.Revoke,
        KeyId = "platform-a",
    };

    private static string Flatten(IReadOnlyList<PanelRow> rows) =>
        string.Join("\n", rows.Select(r => r.Text));

    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 30)]
    [InlineData(44, 14)]
    [InlineData(200, 60)]
    public void Render_FillsWholeScreenExactly(int width, int height)
    {
        var rows = ConfirmPanelLayout.Render(CreateModel(), width, height, PanelCharset.Unicode, 20, false);
        Assert.Equal(height, rows.Count);
        Assert.All(rows, r => Assert.Equal(width, ConfirmPanelLayout.DisplayWidth(r.Text)));
    }

    [Theory]
    [InlineData(10, 4)]
    [InlineData(0, 0)]
    public void Render_ClampsToMinimumSize(int width, int height)
    {
        var rows = ConfirmPanelLayout.Render(CreateModel(), width, height, PanelCharset.Unicode, 20, false);
        Assert.Equal(ConfirmPanelLayout.MinHeight, rows.Count);
        Assert.All(rows, r => Assert.Equal(ConfirmPanelLayout.MinWidth, ConfirmPanelLayout.DisplayWidth(r.Text)));
    }

    [Fact]
    public void Render_ShowsKeyFieldsAndCountdown()
    {
        string screen = Flatten(ConfirmPanelLayout.Render(CreateModel(), 80, 25, PanelCharset.Unicode, 17, false));
        Assert.Contains("创建 API Key", screen);
        Assert.Contains("platform-a", screen);
        Assert.Contains("admin", screen);
        Assert.Contains("上游平台", screen);
        Assert.Contains("17 秒", screen);
        Assert.Contains("[ 确认 (Y) ]", screen);
        Assert.Contains("[ 取消 (N) ]", screen);
    }

    [Fact]
    public void Render_CreateWarnsAboutOneTimePlaintext()
    {
        string screen = Flatten(ConfirmPanelLayout.Render(CreateModel(), 80, 25, PanelCharset.Unicode, 20, false));
        Assert.Contains("明文只显示这一次", screen);
        Assert.Contains("admin 模板", screen);
    }

    [Fact]
    public void Render_DutyTemplateDoesNotShowAdminWarning()
    {
        var model = CreateModel();
        model.Template = "duty";
        string screen = Flatten(ConfirmPanelLayout.Render(model, 80, 25, PanelCharset.Unicode, 20, false));
        Assert.Contains("明文只显示这一次", screen);
        Assert.DoesNotContain("admin 模板", screen);
    }

    [Fact]
    public void Render_RevokeWarnsDestructive()
    {
        string screen = Flatten(ConfirmPanelLayout.Render(RevokeModel(), 80, 25, PanelCharset.Unicode, 20, false));
        Assert.Contains("吊销 API Key", screen);
        Assert.Contains("立即失效", screen);
        Assert.Contains("不可撤销", screen);
        Assert.DoesNotContain("模板：", screen);
    }

    [Fact]
    public void Render_SelectionMarkerFollowsSelection()
    {
        string denied = Flatten(ConfirmPanelLayout.Render(CreateModel(), 80, 25, PanelCharset.Unicode, 20, false));
        string confirmed = Flatten(ConfirmPanelLayout.Render(CreateModel(), 80, 25, PanelCharset.Unicode, 20, true));
        Assert.Contains("> [ 取消 (N) ] <", denied);
        Assert.DoesNotContain("> [ 确认 (Y) ] <", denied);
        Assert.Contains("> [ 确认 (Y) ] <", confirmed);
        Assert.DoesNotContain("> [ 取消 (N) ] <", confirmed);
    }

    [Fact]
    public void ChoiceLine_DefaultsToCancelHighlighted()
    {
        Assert.Contains("> [ 取消 (N) ] <", ConfirmPanelLayout.ChoiceLine(confirmSelected: false));
        Assert.Contains("> [ 确认 (Y) ] <", ConfirmPanelLayout.ChoiceLine(confirmSelected: true));
    }

    [Fact]
    public void Render_AsciiCharsetAvoidsBoxDrawingGlyphs()
    {
        var rows = ConfirmPanelLayout.Render(CreateModel(), 80, 25, PanelCharset.Ascii, 20, false);
        string screen = Flatten(rows);
        Assert.DoesNotContain('─', screen);
        Assert.DoesNotContain('│', screen);
        Assert.DoesNotContain('┌', screen);
        Assert.Contains('+', screen);
        Assert.Contains('|', screen);
        Assert.All(rows, r => Assert.Equal(80, ConfirmPanelLayout.DisplayWidth(r.Text)));
    }

    [Fact]
    public void Render_UnicodeCharsetDrawsBoxFrame()
    {
        string screen = Flatten(ConfirmPanelLayout.Render(CreateModel(), 80, 25, PanelCharset.Unicode, 20, false));
        Assert.Contains('┌', screen);
        Assert.Contains('┘', screen);
        Assert.Contains('├', screen);
    }

    [Theory]
    [InlineData(80, 14)]
    [InlineData(60, 16)]
    [InlineData(44, 14)]
    public void Render_ShortScreen_KeepsInteractiveRowsAndWholeWarnings(int width, int height)
    {
        var rows = ConfirmPanelLayout.Render(CreateModel(), width, height, PanelCharset.Unicode, 20, false);
        string screen = Flatten(rows);

        Assert.Equal(height, rows.Count);
        Assert.Contains("platform-a", screen);
        Assert.Contains("[ 确认 (Y) ]", screen);
        Assert.Contains("[ 取消 (N) ]", screen);
        Assert.Contains("秒内未确认", screen);
        Assert.Contains(rows, r => r.Style == PanelRowStyle.Frame);

        // 风险提示按整条删减：留下来的最后一条必须是完整句子（以句号结尾）
        var warnings = rows.Where(r => r.Style == PanelRowStyle.Warning).ToList();
        Assert.NotEmpty(warnings);
        Assert.EndsWith("。", warnings[^1].Text.TrimEnd().TrimEnd('│').TrimEnd());
    }

    [Fact]
    public void Render_LongNoteWrapsInsideFrame()
    {
        var model = CreateModel();
        model.Note = new string('长', 120);
        var rows = ConfirmPanelLayout.Render(model, 80, 40, PanelCharset.Unicode, 20, false);
        Assert.All(rows, r => Assert.Equal(80, ConfirmPanelLayout.DisplayWidth(r.Text)));
        // 每一行仍然是完整的框线包裹（左右竖线成对出现）
        foreach (var row in rows.Where(r => r.Style is PanelRowStyle.Label or PanelRowStyle.Warning))
            Assert.Equal(2, row.Text.Count(c => c == '│'));
    }

    [Fact]
    public void Render_NullModelThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ConfirmPanelLayout.Render(null!, 80, 25, PanelCharset.Unicode, 20, false));
    }

    [Fact]
    public void OneLinePrompt_MatchesActionShape()
    {
        Assert.Equal("Confirm create API key id=platform-a template=admin ?", CreateModel().OneLinePrompt());
        Assert.Equal("Confirm revoke API key id=platform-a ?", RevokeModel().OneLinePrompt());
        Assert.Equal("Confirm channel self-test (no API key will be created or revoked) ?",
            new ConfirmPanelModel { Action = ConfirmAction.SelfTest }.OneLinePrompt());
    }
}
