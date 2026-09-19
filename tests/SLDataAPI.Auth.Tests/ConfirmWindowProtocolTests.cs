using System;
using System.Linq;
using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class ConfirmWindowPanelTests
{
    private static ConfirmPanelModel CreateModel() => new ConfirmPanelModel
    {
        Action = ConfirmAction.Create,
        KeyId = "platform-a",
        Template = "admin",
        Note = "上游平台",
    };

    private static string[] Lines(string panel) => panel.Split(new[] { "\r\n" }, StringSplitOptions.None);

    [Fact]
    public void RenderPanel_UsesCrlfAndOneLinePerScreenRow()
    {
        string panel = ConfirmWindowProtocol.RenderPanel(CreateModel(), 20);
        Assert.Equal(ConfirmWindowProtocol.ScreenHeight, Lines(panel).Length);
        Assert.DoesNotContain('\n', panel.Replace("\r\n", ""));
    }

    [Fact]
    public void RenderPanel_NeverFillsTheLastColumn()
    {
        // 在 80 列的 cmd 窗口里写满整行会自动换行，把画面顶上去滚屏
        foreach (string line in Lines(ConfirmWindowProtocol.RenderPanel(CreateModel(), 20)))
            Assert.True(ConfirmPanelLayout.DisplayWidth(line) < ConfirmWindowProtocol.ScreenWidth,
                $"行过宽：\"{line}\"");
    }

    [Fact]
    public void RenderPanel_TrimsTrailingSpaces()
    {
        foreach (string line in Lines(ConfirmWindowProtocol.RenderPanel(CreateModel(), 20)))
            Assert.Equal(line.TrimEnd(), line);
    }

    [Fact]
    public void RenderPanel_FitsDefaultConhostWindow()
    {
        // mode con 调整窗口失败时也要放得下：conhost 默认 80×25，末行换行占掉第 25 行
        Assert.True(ConfirmWindowProtocol.ScreenHeight + 1 <= 25);
    }

    [Fact]
    public void RenderPanel_ShowsFieldsCountdownAndKeys()
    {
        string panel = ConfirmWindowProtocol.RenderPanel(CreateModel(), 17);
        Assert.Contains("创建 API Key", panel);
        Assert.Contains("platform-a", panel);
        Assert.Contains("admin", panel);
        Assert.Contains("上游平台", panel);
        Assert.Contains("17 秒", panel);
        Assert.Contains("[ 确认 (Y) ]", panel);
        Assert.Contains("[ 取消 (N) ]", panel);
        Assert.Contains("按 Y 确认", panel);
    }

    [Fact]
    public void RenderPanel_KeepsAllCreateWarnings()
    {
        string panel = ConfirmWindowProtocol.RenderPanel(CreateModel(), 20);
        Assert.Contains("明文只显示这一次", panel);
        Assert.Contains("admin 模板", panel);
        Assert.Contains("不是你本人在操作", panel);
    }

    [Fact]
    public void RenderPanel_DefaultsToCancelHighlighted()
    {
        Assert.Contains("> [ 取消 (N) ] <", ConfirmWindowProtocol.RenderPanel(CreateModel(), 20));
    }

    [Fact]
    public void RenderPanel_RevokeShowsDestructiveWarnings()
    {
        var model = new ConfirmPanelModel { Action = ConfirmAction.Revoke, KeyId = "platform-a" };
        string panel = ConfirmWindowProtocol.RenderPanel(model, 20);
        Assert.Contains("吊销 API Key", panel);
        Assert.Contains("立即失效", panel);
        Assert.DoesNotContain("模板：", panel);
    }

    [Fact]
    public void RenderPanel_SelfTestSaysNothingChanges()
    {
        var model = new ConfirmPanelModel
        {
            Action = ConfirmAction.SelfTest,
            KeyId = "self-test",
            Template = "admin",
        };
        string panel = ConfirmWindowProtocol.RenderPanel(model, 20);
        Assert.Contains("确认通道自检", panel);
        Assert.Contains("不会创建或吊销任何 API Key", panel);
        Assert.DoesNotContain("明文只显示这一次", panel);
        Assert.DoesNotContain("模板：", panel);
        Assert.StartsWith("Y=allow  N=cancel  |  20s  |  SELF-TEST api key id=self-test", Lines(panel)[^1]);
    }

    [Fact]
    public void RenderPanel_LongNoteStaysInsideTheWindow()
    {
        var model = CreateModel();
        model.Note = new string('长', 200);
        string panel = ConfirmWindowProtocol.RenderPanel(model, 20);
        Assert.Equal(ConfirmWindowProtocol.ScreenHeight, Lines(panel).Length);
        Assert.All(Lines(panel), l =>
            Assert.True(ConfirmPanelLayout.DisplayWidth(l) < ConfirmWindowProtocol.ScreenWidth));
    }

    [Fact]
    public void RenderPanel_LastLineIsAnAsciiSummary()
    {
        // 中文画不出来（字体 / 代码页不给力）时，这一行仍然说得清在确认什么
        string last = Lines(ConfirmWindowProtocol.RenderPanel(CreateModel(), 9))[^1];
        Assert.All(last, c => Assert.True(c <= 0x7F));
        Assert.StartsWith("Y=allow  N=cancel  |  9s", last);
        Assert.Contains("CREATE api key id=platform-a", last);
        Assert.Contains("template=admin", last);
    }

    [Fact]
    public void AsciiSummary_ReplacesNonAsciiAndStaysInsideTheWindow()
    {
        var model = new ConfirmPanelModel
        {
            Action = ConfirmAction.Revoke,
            KeyId = new string('键', 80),
        };
        string summary = ConfirmWindowProtocol.AsciiSummary(model, 20);
        Assert.All(summary, c => Assert.True(c <= 0x7F));
        Assert.True(summary.Length < ConfirmWindowProtocol.ScreenWidth);
        Assert.Contains("REVOKE api key", summary);
        Assert.DoesNotContain("template=", summary);
    }

    [Fact]
    public void RenderPanel_AsciiCharsetDropsBoxDrawing()
    {
        string panel = ConfirmWindowProtocol.RenderPanel(CreateModel(), 20, PanelCharset.Ascii);
        Assert.DoesNotContain('│', panel);
        Assert.Contains('|', panel);
    }

    [Fact]
    public void RenderPanel_NullModelThrows()
    {
        Assert.Throws<ArgumentNullException>(() => ConfirmWindowProtocol.RenderPanel(null!, 20));
    }

    [Fact]
    public void PanelFileName_IsPerSecond()
    {
        Assert.Equal("panel-7.txt", ConfirmWindowProtocol.PanelFileName(7));
        Assert.NotEqual(ConfirmWindowProtocol.PanelFileName(7), ConfirmWindowProtocol.PanelFileName(8));
    }
}

public class ConfirmWindowScriptTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildScript_IsPureAscii()
    {
        // 脚本在新窗口的默认代码页下被 cmd 逐行解析：非 ASCII 会随代码页变味
        Assert.All(ConfirmWindowProtocol.BuildScript(20, Nonce), c => Assert.True(c <= 0x7F));
    }

    [Fact]
    public void BuildScript_UsesCrlfLineEndings()
    {
        string script = ConfirmWindowProtocol.BuildScript(20, Nonce);
        Assert.DoesNotContain('\n', script.Replace("\r\n", ""));
        Assert.StartsWith("@echo off\r\n", script);
    }

    [Fact]
    public void BuildScript_CountsDownFromTheConfiguredTimeout()
    {
        string script = ConfirmWindowProtocol.BuildScript(45, Nonce);
        Assert.Contains("set \"LEFT=45\"", script);
        Assert.Contains("choice /C YNT /N /T 1 /D T", script);
        Assert.Contains("set /a LEFT-=1", script);
        Assert.Contains("type \"%PANELDIR%panel-%LEFT%.txt\"", script);
    }

    [Fact]
    public void BuildScript_MapsEveryBranchToItsExitCodeAndToken()
    {
        string script = ConfirmWindowProtocol.BuildScript(20, Nonce);
        Assert.Contains($"echo CONFIRM {Nonce}", script);
        Assert.Contains($"echo DENY {Nonce}", script);
        Assert.Contains($"echo TIMEOUT {Nonce}", script);
        Assert.Contains($"exit {ConfirmWindowProtocol.ExitConfirmed}", script);
        Assert.Contains($"exit {ConfirmWindowProtocol.ExitDenied}", script);
        Assert.Contains($"exit {ConfirmWindowProtocol.ExitTimedOut}", script);
    }

    [Fact]
    public void BuildScript_FallsBackToLineInputWhenChoiceIsMissing()
    {
        string script = ConfirmWindowProtocol.BuildScript(20, Nonce);
        Assert.Contains("where choice >nul 2>&1 || goto noChoice", script);
        Assert.Contains(":noChoice", script);
        Assert.Contains("set /p \"ANSWER=", script);
    }

    [Fact]
    public void BuildScript_OnlyYAnswersAllow()
    {
        string script = ConfirmWindowProtocol.BuildScript(20, Nonce);
        // 行输入兜底里除了 y / yes，其余（含空回车）都落到 :deny
        Assert.Contains("if /i \"%ANSWER%\"==\"y\" goto allow", script);
        Assert.Contains("if /i \"%ANSWER%\"==\"yes\" goto allow", script);
        int denyFallThrough = script.IndexOf("if /i \"%ANSWER%\"==\"yes\" goto allow", StringComparison.Ordinal);
        Assert.Contains("goto deny", script.Substring(denyFallThrough));
    }

    [Fact]
    public void BuildScript_RejectsUnusableArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfirmWindowProtocol.BuildScript(0, Nonce));
        Assert.Throws<ArgumentException>(() => ConfirmWindowProtocol.BuildScript(20, ""));
        Assert.Throws<ArgumentException>(() => ConfirmWindowProtocol.BuildScript(20, "bad nonce & echo pwn"));
        Assert.Throws<ArgumentException>(() => ConfirmWindowProtocol.BuildScript(20, "校验串"));
    }

    [Fact]
    public void NewNonce_IsHexAndUnique()
    {
        string a = ConfirmWindowProtocol.NewNonce();
        string b = ConfirmWindowProtocol.NewNonce();
        Assert.Equal(32, a.Length);
        Assert.All(a, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.NotEqual(a, b);
    }
}

public class ConfirmWindowOutcomeTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private static bool Parse(int exitCode, string? resultText, out ConfirmOutcome outcome) =>
        ConfirmWindowProtocol.TryParseOutcome(exitCode, resultText, Nonce, out outcome, out _);

    [Fact]
    public void ExitConfirmedWithMatchingToken_Confirms()
    {
        Assert.True(Parse(ConfirmWindowProtocol.ExitConfirmed, $"CONFIRM {Nonce}\r\n", out ConfirmOutcome outcome));
        Assert.Equal(ConfirmOutcome.Confirmed, outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CONFIRM deadbeef")]
    [InlineData("DENY 0123456789abcdef0123456789abcdef")]
    public void ExitConfirmedWithoutMatchingToken_Denies(string? resultText)
    {
        Assert.True(Parse(ConfirmWindowProtocol.ExitConfirmed, resultText, out ConfirmOutcome outcome));
        Assert.Equal(ConfirmOutcome.Denied, outcome);
    }

    [Fact]
    public void ExitConfirmedWithoutMatchingToken_ExplainsWhy()
    {
        ConfirmWindowProtocol.TryParseOutcome(
            ConfirmWindowProtocol.ExitConfirmed, "CONFIRM nope", Nonce, out _, out string detail);
        Assert.Contains("校验串", detail);
    }

    [Fact]
    public void ExitDenied_Denies()
    {
        Assert.True(Parse(ConfirmWindowProtocol.ExitDenied, $"DENY {Nonce}", out ConfirmOutcome outcome));
        Assert.Equal(ConfirmOutcome.Denied, outcome);
    }

    [Fact]
    public void ExitTimedOut_TimesOut()
    {
        Assert.True(Parse(ConfirmWindowProtocol.ExitTimedOut, $"TIMEOUT {Nonce}", out ConfirmOutcome outcome));
        Assert.Equal(ConfirmOutcome.TimedOut, outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9009)]          // cmd: 命令不存在
    [InlineData(-1073741510)]   // 0xC000013A: 窗口被关掉 / Ctrl+C
    public void UnknownExitCode_YieldsNoVerdict(int exitCode)
    {
        Assert.False(Parse(exitCode, $"CONFIRM {Nonce}", out ConfirmOutcome outcome));
        Assert.NotEqual(ConfirmOutcome.Confirmed, outcome);
    }

    [Fact]
    public void ResultLine_MatchesWhatTheScriptWrites()
    {
        Assert.Equal($"CONFIRM {Nonce}", ConfirmWindowProtocol.ResultLine(ConfirmOutcome.Confirmed, Nonce));
        Assert.Equal($"DENY {Nonce}", ConfirmWindowProtocol.ResultLine(ConfirmOutcome.Denied, Nonce));
        Assert.Equal($"TIMEOUT {Nonce}", ConfirmWindowProtocol.ResultLine(ConfirmOutcome.TimedOut, Nonce));
        Assert.Contains(
            ConfirmWindowProtocol.ResultLine(ConfirmOutcome.Confirmed, Nonce),
            ConfirmWindowProtocol.BuildScript(20, Nonce));
    }
}
