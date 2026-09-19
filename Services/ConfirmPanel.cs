using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SLDataAPI.Services;

/// <summary>确认面板要确认的操作类型。</summary>
public enum ConfirmAction
{
    Create,
    Revoke,

    /// <summary>确认通道自检：走完整条通道但不碰任何 Key（`sldataapi apikey confirmtest`）。</summary>
    SelfTest,
}

/// <summary>面板行的语义样式（由具体的控制台实现映射成颜色）。</summary>
public enum PanelRowStyle
{
    Blank,
    Frame,
    Separator,
    Title,
    Label,
    Warning,
    Hint,
    Choice,
}

/// <summary>
/// 边框字符集：Unicode 制表符（默认）与纯 ASCII 兜底
/// （旧代码页 / 不支持制表符的终端下 Unicode 边框会变成乱码）。
/// </summary>
public enum PanelCharset
{
    Unicode,
    Ascii,
}

/// <summary>确认窗口里的一行：已按目标宽度补齐的文本 + 语义样式。</summary>
public sealed class PanelRow
{
    public PanelRow(string text, PanelRowStyle style)
    {
        Text = text ?? "";
        Style = style;
    }

    public string Text { get; }
    public PanelRowStyle Style { get; }

    public override string ToString() => Text;
}

/// <summary>待确认操作的数据（面板上展示的字段）。</summary>
public sealed class ConfirmPanelModel
{
    public ConfirmAction Action { get; set; } = ConfirmAction.Create;
    public string KeyId { get; set; } = "";
    public string Template { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>行模式（弹不出确认窗口时）与日志用的单行描述。</summary>
    public string OneLinePrompt() => Action switch
    {
        ConfirmAction.Create => $"Confirm create API key id={KeyId} template={Template} ?",
        ConfirmAction.Revoke => $"Confirm revoke API key id={KeyId} ?",
        _ => "Confirm channel self-test (no API key will be created or revoked) ?",
    };
}

/// <summary>
/// 确认面板的纯排版逻辑：把待确认操作渲染成"一屏 × 每行定宽"的行列表
/// （新开的 cmd 确认窗口按秒各取一屏打印，见 ConfirmWindowProtocol）。
/// 不碰任何控制台 API，宽度按终端显示列计算（CJK 全角字符占 2 列），
/// 因此可脱离游戏 DLL 直接单元测试。
/// </summary>
public static class ConfirmPanelLayout
{
    /// <summary>低于此尺寸不足以放下面板（调用方退化到行模式提示）。</summary>
    public const int MinWidth = 44;
    public const int MinHeight = 14;

    private const int MaxPanelWidth = 78;

    private readonly struct Glyphs
    {
        public Glyphs(char h, char v, char tl, char tr, char bl, char br, char ml, char mr, string warn)
        {
            H = h; V = v; TL = tl; TR = tr; BL = bl; BR = br; ML = ml; MR = mr; Warn = warn;
        }

        public char H { get; }
        public char V { get; }
        public char TL { get; }
        public char TR { get; }
        public char BL { get; }
        public char BR { get; }
        public char ML { get; }
        public char MR { get; }
        public string Warn { get; }
    }

    private static Glyphs GlyphsFor(PanelCharset charset) =>
        charset == PanelCharset.Ascii
            ? new Glyphs('-', '|', '+', '+', '+', '+', '+', '+', "[!]")
            : new Glyphs('─', '│', '┌', '┐', '└', '┘', '├', '┤', "[!]");

    /// <summary>
    /// 渲染一屏。返回恰好 <paramref name="height"/> 行，每行恰好 <paramref name="width"/> 显示列，
    /// 面板在屏幕中水平与垂直居中，其余区域为空白行（整屏重画，不残留旧内容）。
    /// <paramref name="confirmSelected"/> 标出默认选项：确认窗口只收 Y/N，始终传 false，
    /// 于是"取消"带着尖括号，一眼能看出默认是不放行。
    /// </summary>
    public static IReadOnlyList<PanelRow> Render(
        ConfirmPanelModel model,
        int width,
        int height,
        PanelCharset charset,
        int secondsLeft,
        bool confirmSelected)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));

        width = Math.Max(MinWidth, width);
        height = Math.Max(MinHeight, height);

        Glyphs g = GlyphsFor(charset);
        int panelWidth = Math.Min(width - 2, MaxPanelWidth);
        int inner = panelWidth - 4; // 左右边框各 1 列 + 内缩进各 1 列

        // 屏幕不够高时整条整条地少画风险提示（至少留一条），而不是把句子截一半
        List<PanelRow> panel;
        int warningCount = Warnings(model).Count();
        while (true)
        {
            panel = Frame(BuildBody(model, g, secondsLeft, confirmSelected, inner, warningCount), panelWidth, inner, g);
            if (panel.Count <= height || warningCount <= 1) break;
            warningCount--;
        }

        TrimToHeight(panel, height);

        int top = Math.Max(0, (height - panel.Count) / 2);
        int left = Math.Max(0, (width - panelWidth) / 2);
        string indent = new string(' ', left);

        var screen = new List<PanelRow>(height);
        for (int y = 0; y < height; y++)
        {
            int idx = y - top;
            if (idx < 0 || idx >= panel.Count)
            {
                screen.Add(new PanelRow(FitToWidth("", width), PanelRowStyle.Blank));
                continue;
            }
            screen.Add(new PanelRow(FitToWidth(indent + panel[idx].Text, width), panel[idx].Style));
        }
        return screen;
    }

    private static bool IsCentered(PanelRowStyle style) =>
        style is PanelRowStyle.Title or PanelRowStyle.Hint or PanelRowStyle.Choice;

    /// <summary>给内容行套上边框（分隔行换成框线，居中样式的行居中）。</summary>
    private static List<PanelRow> Frame(List<PanelRow> body, int panelWidth, int inner, Glyphs g)
    {
        var framed = new List<PanelRow>(body.Count + 2);
        framed.Add(new PanelRow(TopBorder(panelWidth, g), PanelRowStyle.Frame));
        foreach (var item in body)
        {
            if (item.Style == PanelRowStyle.Separator)
            {
                framed.Add(new PanelRow(MidBorder(panelWidth, g), PanelRowStyle.Separator));
                continue;
            }

            string text = IsCentered(item.Style) ? Center(item.Text, inner) : item.Text;
            framed.Add(new PanelRow($"{g.V} {FitToWidth(text, inner)} {g.V}", item.Style));
        }
        framed.Add(new PanelRow(BottomBorder(panelWidth, g), PanelRowStyle.Frame));
        return framed;
    }

    private static List<PanelRow> BuildBody(
        ConfirmPanelModel model, Glyphs g, int secondsLeft, bool confirmSelected, int inner, int maxWarnings)
    {
        bool create = model.Action == ConfirmAction.Create;
        var body = new List<PanelRow>
        {
            new PanelRow("", PanelRowStyle.Blank),
            new PanelRow(TitleFor(model.Action), PanelRowStyle.Title),
            new PanelRow("", PanelRowStyle.Blank),
        };

        AddWrapped(body, $"id：{model.KeyId}", PanelRowStyle.Label, inner);
        if (create && !string.IsNullOrWhiteSpace(model.Template))
            AddWrapped(body, $"模板：{model.Template}", PanelRowStyle.Label, inner);
        if (!string.IsNullOrWhiteSpace(model.Note))
            AddWrapped(body, $"备注：{model.Note}", PanelRowStyle.Label, inner);

        body.Add(new PanelRow("", PanelRowStyle.Blank));
        foreach (string warning in Warnings(model).Take(Math.Max(1, maxWarnings)))
            AddWrapped(body, $"{g.Warn} {warning}", PanelRowStyle.Warning, inner);

        body.Add(new PanelRow("", PanelRowStyle.Separator));
        body.Add(new PanelRow($"{Math.Max(0, secondsLeft)} 秒内未确认将自动拒绝（不创建 / 不吊销）", PanelRowStyle.Hint));
        body.Add(new PanelRow("", PanelRowStyle.Blank));
        body.Add(new PanelRow(ChoiceLine(confirmSelected), PanelRowStyle.Choice));
        body.Add(new PanelRow("", PanelRowStyle.Blank));
        AddWrapped(body, "按 Y 确认 · 按 N 取消 · 关闭本窗口或倒计时结束都按取消处理", PanelRowStyle.Hint, inner);
        body.Add(new PanelRow("", PanelRowStyle.Blank));
        return body;
    }

    private static string TitleFor(ConfirmAction action) => action switch
    {
        ConfirmAction.Create => "SLDataAPI 安全确认 · 创建 API Key",
        ConfirmAction.Revoke => "SLDataAPI 安全确认 · 吊销 API Key",
        _ => "SLDataAPI 安全确认 · 确认通道自检",
    };

    private static IEnumerable<string> Warnings(ConfirmPanelModel model)
    {
        if (model.Action == ConfirmAction.Create)
        {
            yield return "新 Key 的明文只显示这一次，关闭后无法找回，只能吊销重建。";
            if (string.Equals(model.Template, "admin", StringComparison.OrdinalIgnoreCase))
                yield return "admin 模板可调用控制面全部已授权端点，等同管理权限。";
            yield return "若这次创建不是你本人在操作，请立刻选择取消并检查控制面日志。";
        }
        else if (model.Action == ConfirmAction.Revoke)
        {
            yield return "吊销后该 Key 立即失效，正在使用它的平台 / 面板会断连。";
            yield return "此操作不可撤销，恢复需重新 create 并重新分发明文。";
            yield return "若这次吊销不是你本人在操作，请立刻选择取消并检查控制面日志。";
        }
        else
        {
            yield return "这是确认通道自检：按 Y 或 N 都不会创建或吊销任何 API Key。";
            yield return "请确认本面板是新弹出的 cmd 窗口，且 LocalAdmin 窗口没有被清屏或接管。";
        }
    }

    /// <summary>选择行：左"确认"右"取消"，选中项用尖括号标出（不依赖颜色也能看清）。</summary>
    public static string ChoiceLine(bool confirmSelected)
    {
        const string yes = "[ 确认 (Y) ]";
        const string no = "[ 取消 (N) ]";
        string left = confirmSelected ? $"> {yes} <" : $"  {yes}  ";
        string right = confirmSelected ? $"  {no}  " : $"> {no} <";
        return left + "   " + right;
    }

    private static void AddWrapped(List<PanelRow> body, string text, PanelRowStyle style, int inner)
    {
        foreach (string line in Wrap(text, inner))
            body.Add(new PanelRow(line, style));
    }

    /// <summary>
    /// 面板仍高于屏幕时先削空白行，最后才从尾部硬裁（底部边框始终保留）。
    /// 风险提示的条数已在 Render 里按高度递减，这里不再动它。
    /// </summary>
    private static void TrimToHeight(List<PanelRow> panel, int height)
    {
        for (int i = panel.Count - 2; i > 0 && panel.Count > height; i--)
        {
            if (panel[i].Style == PanelRowStyle.Blank)
                panel.RemoveAt(i);
        }
        while (panel.Count > height)
            panel.RemoveAt(panel.Count - 2);
    }

    private static string TopBorder(int panelWidth, Glyphs g) =>
        g.TL + new string(g.H, Math.Max(0, panelWidth - 2)) + g.TR;

    private static string MidBorder(int panelWidth, Glyphs g) =>
        g.ML + new string(g.H, Math.Max(0, panelWidth - 2)) + g.MR;

    private static string BottomBorder(int panelWidth, Glyphs g) =>
        g.BL + new string(g.H, Math.Max(0, panelWidth - 2)) + g.BR;

    // ────────────── 显示宽度工具（终端按显示列排版，不能按 char 数） ──────────────

    /// <summary>字符是否占两个终端显示列（CJK 及全角标点）。</summary>
    public static bool IsWide(char c) =>
        (c >= 0x1100 && c <= 0x115F) ||
        (c >= 0x2E80 && c <= 0x303E) ||
        (c >= 0x3041 && c <= 0x33FF) ||
        (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0x4E00 && c <= 0x9FFF) ||
        (c >= 0xA000 && c <= 0xA4CF) ||
        (c >= 0xAC00 && c <= 0xD7A3) ||
        (c >= 0xF900 && c <= 0xFAFF) ||
        (c >= 0xFE30 && c <= 0xFE4F) ||
        (c >= 0xFF00 && c <= 0xFF60) ||
        (c >= 0xFFE0 && c <= 0xFFE6);

    /// <summary>字符串占用的终端显示列数。</summary>
    public static int DisplayWidth(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int w = 0;
        foreach (char c in text!)
        {
            if (char.IsControl(c)) continue;
            w += IsWide(c) ? 2 : 1;
        }
        return w;
    }

    /// <summary>
    /// 把文本裁剪 / 补齐到恰好 <paramref name="width"/> 显示列。
    /// 裁剪位置落在全角字符中间时不劈开该字符，改用空格补位。
    /// </summary>
    public static string FitToWidth(string? text, int width)
    {
        if (width <= 0) return "";
        var sb = new StringBuilder(width);
        int used = 0;
        foreach (char c in text ?? "")
        {
            if (char.IsControl(c)) continue;
            int cw = IsWide(c) ? 2 : 1;
            if (used + cw > width) break;
            sb.Append(c);
            used += cw;
        }
        if (used < width) sb.Append(' ', width - used);
        return sb.ToString();
    }

    /// <summary>按显示列宽度折行；无空格可断（中文）时按字符硬断。</summary>
    public static IReadOnlyList<string> Wrap(string? text, int width)
    {
        var lines = new List<string>();
        if (width <= 0) return lines;
        if (string.IsNullOrEmpty(text))
        {
            lines.Add("");
            return lines;
        }

        var current = new StringBuilder();
        int used = 0;
        int lastBreak = -1; // current 中最后一个空格的位置（含其显示宽度）
        int widthAtBreak = 0;

        foreach (char c in text!)
        {
            if (char.IsControl(c)) continue;
            int cw = IsWide(c) ? 2 : 1;
            if (used + cw > width)
            {
                // 空格断行只在断点已经填满半行以上时才用；否则硬断。
                // 中英混排（"[!] 新 Key 的明文……"）里最后一个空格往往在很靠前的位置，
                // 一律按空格断会留下一行几个字、下一行超长的难看结果。
                if (lastBreak > 0 && lastBreak <= current.Length && widthAtBreak * 2 >= width)
                {
                    string head = current.ToString(0, lastBreak).TrimEnd();
                    string tail = current.ToString(lastBreak, current.Length - lastBreak);
                    lines.Add(head);
                    current.Clear();
                    current.Append(tail);
                    used -= widthAtBreak;
                }
                else
                {
                    // 硬断：避让行首禁则字符（句读、收尾括号），把前一个字一起带到下一行
                    string lineText = current.ToString();
                    string carry = "";
                    if (IsNoBreakBefore(c) && lineText.Length > 1)
                    {
                        carry = lineText.Substring(lineText.Length - 1);
                        lineText = lineText.Substring(0, lineText.Length - 1);
                    }
                    lines.Add(lineText);
                    current.Clear();
                    current.Append(carry);
                    used = DisplayWidth(carry);
                }
                lastBreak = -1;
            }
            current.Append(c);
            used += cw;
            if (c == ' ')
            {
                lastBreak = current.Length;
                widthAtBreak = used;
            }
        }

        if (current.Length > 0 || lines.Count == 0)
            lines.Add(current.ToString());
        return lines;
    }

    /// <summary>行首禁则：这些字符不应落在折行后的行首（中文排版习惯）。</summary>
    public static bool IsNoBreakBefore(char c) =>
        "。，、；：？！）】」』》”’·%".IndexOf(c) >= 0;

    /// <summary>按显示列居中。</summary>
    public static string Center(string? text, int width)
    {
        int w = DisplayWidth(text);
        if (w >= width) return FitToWidth(text, width);
        int left = (width - w) / 2;
        return FitToWidth(new string(' ', left) + text, width);
    }
}
