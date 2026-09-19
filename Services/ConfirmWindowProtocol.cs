using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SLDataAPI.Services;

/// <summary>确认会话的结果。</summary>
public enum ConfirmOutcome
{
    Confirmed,
    Denied,
    TimedOut,
}

/// <summary>
/// 新开 cmd 窗口确认通道的"协议"层（纯逻辑，无 Win32 / 游戏依赖，可直接单元测试）：
/// 往临时目录里写什么、批处理脚本长什么样、子进程退出后怎么判定结论。
///
/// 为什么是一个独立进程而不是接管当前控制台：LocalAdmin 的控制台由 LocalAdmin 自己持有
/// （它转发游戏进程的输出），外部 AttachConsole / 整屏接管在真实服务器上不生效；
/// 用 CREATE_NEW_CONSOLE 拉起的 cmd.exe 拥有自己的控制台缓冲区与输入队列，
/// 既不需要借 LocalAdmin 的窗口，也不会改动它的任何状态。
///
/// 进程间只传三样东西，都不含密钥明文：
///   1. 面板正文（panel-N.txt，每个倒计时秒数一屏，UTF-8 无 BOM，交给 cmd 的 type 打印）；
///   2. 批处理脚本（confirm.cmd，纯 ASCII——避免代码页差异把脚本本身解析坏）；
///   3. 结论（进程退出码 + result.txt 里的一次性校验串，二者都对上才算"确认通过"）。
/// 命令行上只有脚本文件名，面板字段不经命令行，也就不会出现在其它进程的命令行快照里。
/// </summary>
public static class ConfirmWindowProtocol
{
    // 退出码刻意避开 cmd / choice 自己会用到的小数值与 9009（命令不存在）
    public const int ExitConfirmed = 10;
    public const int ExitDenied = 11;
    public const int ExitTimedOut = 12;

    public const string ScriptFileName = "confirm.cmd";
    public const string ResultFileName = "result.txt";
    public const string PanelFilePrefix = "panel-";
    public const string PanelFileSuffix = ".txt";

    /// <summary>确认窗口的排版尺寸。</summary>
    public const int ScreenWidth = 80;

    /// <summary>
    /// 一屏画多少行。24 行 + 末尾换行 = 25 行，正好是 conhost 默认窗口高度：
    /// 即使 <c>mode con</c> 调整窗口失败，面板也不会把自己顶得滚屏。
    /// </summary>
    public const int ScreenHeight = 24;

    /// <summary>脚本请求的窗口尺寸（比一屏多两行余量）。</summary>
    public const int WindowLines = ScreenHeight + 2;

    private const string ConfirmTag = "CONFIRM";
    private const string DenyTag = "DENY";
    private const string TimeoutTag = "TIMEOUT";

    public static string PanelFileName(int secondsLeft) =>
        PanelFilePrefix + secondsLeft.ToString(CultureInfo.InvariantCulture) + PanelFileSuffix;

    /// <summary>一次性校验串：确认窗口把它写回 result.txt，父进程逐字比对。</summary>
    public static string NewNonce()
    {
        var bytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>确认窗口写回 result.txt 的整行内容。</summary>
    public static string ResultLine(ConfirmOutcome outcome, string nonce) =>
        $"{TagFor(outcome)} {nonce}";

    private static string TagFor(ConfirmOutcome outcome) => outcome switch
    {
        ConfirmOutcome.Confirmed => ConfirmTag,
        ConfirmOutcome.TimedOut => TimeoutTag,
        _ => DenyTag,
    };

    /// <summary>
    /// 渲染倒计时到 <paramref name="secondsLeft"/> 秒时的一屏正文（末行是 ASCII 摘要）。
    /// 行尾空白裁掉：cmd 里写满整行会触发自动换行，把画面顶上去。
    /// </summary>
    public static string RenderPanel(
        ConfirmPanelModel model, int secondsLeft, PanelCharset charset = PanelCharset.Unicode)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));

        IReadOnlyList<PanelRow> rows = ConfirmPanelLayout.Render(
            model, ScreenWidth, ScreenHeight - 1, charset, secondsLeft, confirmSelected: false);

        var lines = rows.Select(r => r.Text.TrimEnd()).ToList();
        lines.Add(AsciiSummary(model, secondsLeft));
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// 屏幕最下面的纯 ASCII 摘要行：万一操作者的控制台字体 / 代码页画不出中文，
    /// 这一行仍能说清在确认什么、按哪个键、还剩几秒。
    /// </summary>
    public static string AsciiSummary(ConfirmPanelModel model, int secondsLeft)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));

        string action = model.Action switch
        {
            ConfirmAction.Create => "CREATE",
            ConfirmAction.Revoke => "REVOKE",
            _ => "SELF-TEST",
        };
        string template = model.Action == ConfirmAction.Create && !string.IsNullOrWhiteSpace(model.Template)
            ? " template=" + ToAscii(model.Template, 24)
            : "";
        // 按键与倒计时放最前面：行尾被裁掉时，最要紧的信息也还在
        string text = $"Y=allow  N=cancel  |  {Math.Max(0, secondsLeft).ToString(CultureInfo.InvariantCulture)}s" +
                      $"  |  {action} api key id={ToAscii(model.KeyId, 32)}{template}";
        if (text.Length > ScreenWidth - 1)
            text = text.Substring(0, ScreenWidth - 1);
        return text.TrimEnd();
    }

    /// <summary>非 ASCII 字符（含控制字符）统一换成 ?，并限长。</summary>
    private static string ToAscii(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text!.Length, maxLength));
        foreach (char c in text!)
        {
            if (sb.Length >= maxLength) { sb.Append('~'); break; }
            sb.Append(c >= 0x20 && c <= 0x7E ? c : '?');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 生成确认窗口跑的批处理。纯 ASCII：中文只出现在 type 打印的面板文件里，
    /// 脚本本身在任何代码页下都解析得一样。
    ///
    /// 交互靠 choice（Vista 起随系统自带）：每秒一次 1 秒超时的按键读取，
    /// 超时就重画下一秒的面板，于是倒计时会真的走。choice 缺失时退化成一次 set /p 行输入
    /// （无倒计时，父进程等到超时会结束掉窗口，按拒绝处理）。
    /// </summary>
    public static string BuildScript(int timeoutSeconds, string nonce)
    {
        if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        if (string.IsNullOrEmpty(nonce)) throw new ArgumentException("nonce 不能为空", nameof(nonce));
        if (!IsAsciiWord(nonce)) throw new ArgumentException("nonce 必须是 ASCII 字母数字", nameof(nonce));

        string seconds = timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            "@echo off",
            "setlocal enableextensions",
            "title SLDataAPI Security Confirm - press Y to allow / N to cancel",
            "chcp 65001 >nul 2>&1",
            $"mode con: cols={ScreenWidth.ToString(CultureInfo.InvariantCulture)} " +
                $"lines={WindowLines.ToString(CultureInfo.InvariantCulture)} >nul 2>&1",
            "color 1F",
            "set \"PANELDIR=%~dp0\"",
            $"set \"RESULT=%PANELDIR%{ResultFileName}\"",
            $"set \"LEFT={seconds}\"",
            "where choice >nul 2>&1 || goto noChoice",
            "",
            ":loop",
            "cls",
            $"type \"%PANELDIR%{PanelFilePrefix}%LEFT%{PanelFileSuffix}\" 2>nul",
            // choice 的 /C 里 T 只是"计时片用完"的默认答案，按到它等同于一次静默重画
            "choice /C YNT /N /T 1 /D T >nul 2>&1",
            "if errorlevel 3 goto tick",
            "if errorlevel 2 goto deny",
            "if errorlevel 1 goto allow",
            "goto deny",
            "",
            ":tick",
            "set /a LEFT-=1",
            "if %LEFT% GTR 0 goto loop",
            "goto expire",
            "",
            ":noChoice",
            "cls",
            $"type \"%PANELDIR%{PanelFilePrefix}%LEFT%{PanelFileSuffix}\" 2>nul",
            "echo.",
            "set \"ANSWER=\"",
            // 提示语里不放 > ：即便被引号保护，也不给 cmd 的重定向解析留想象空间
            "set /p \"ANSWER=Y = allow / N = cancel : \"",
            "if /i \"%ANSWER%\"==\"y\" goto allow",
            "if /i \"%ANSWER%\"==\"yes\" goto allow",
            "goto deny",
            "",
            ":allow",
            $">\"%RESULT%\" echo {ResultLine(ConfirmOutcome.Confirmed, nonce)}",
            $"exit {ExitConfirmed.ToString(CultureInfo.InvariantCulture)}",
            "",
            ":deny",
            $">\"%RESULT%\" echo {ResultLine(ConfirmOutcome.Denied, nonce)}",
            $"exit {ExitDenied.ToString(CultureInfo.InvariantCulture)}",
            "",
            ":expire",
            $">\"%RESULT%\" echo {ResultLine(ConfirmOutcome.TimedOut, nonce)}",
            $"exit {ExitTimedOut.ToString(CultureInfo.InvariantCulture)}",
            "",
        };

        string script = string.Join("\r\n", lines);
        if (!IsAscii(script))
            throw new InvalidOperationException("确认脚本必须是纯 ASCII");
        return script;
    }

    /// <summary>
    /// 判定确认窗口给出的结论。返回 false 表示这一路没有给出有效结论
    /// （窗口没起来 / 被 X 掉 / 脚本本身跑挂），调用方应继续尝试下一条通道。
    /// 任何"看不懂"的情况都不会被当成确认通过。
    /// </summary>
    public static bool TryParseOutcome(
        int exitCode, string? resultText, string nonce, out ConfirmOutcome outcome, out string detail)
    {
        outcome = ConfirmOutcome.Denied;
        detail = "";
        string token = (resultText ?? "").Trim();

        switch (exitCode)
        {
            case ExitConfirmed:
                if (string.Equals(token, ResultLine(ConfirmOutcome.Confirmed, nonce), StringComparison.Ordinal))
                {
                    outcome = ConfirmOutcome.Confirmed;
                    return true;
                }
                // 退出码说"确认"但校验串对不上：只能按拒绝处理
                detail = "确认窗口返回的一次性校验串不匹配";
                outcome = ConfirmOutcome.Denied;
                return true;

            case ExitDenied:
                outcome = ConfirmOutcome.Denied;
                return true;

            case ExitTimedOut:
                outcome = ConfirmOutcome.TimedOut;
                return true;

            default:
                return false;
        }
    }

    private static bool IsAscii(string text) => text.All(c => c <= 0x7F);

    private static bool IsAsciiWord(string text) =>
        text.Length > 0 && text.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
}
