// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Features.Tests;

/// <summary>
/// error.code 与语言无关的回归测试：
///   - 逐项覆盖审查列出的中英文消息对；
///   - 扫描仓库内所有 Localization.T/F 的 ("zh","en") 字面量对，逐对比较两种语言下的推断结果。
/// 测试通过 Localization.T/F 真正生成 en 消息，模拟生产路径（会写入英文→中文缓存）。
/// </summary>
public class LocalizationCodeIndependenceTests : IDisposable
{
    public LocalizationCodeIndependenceTests() => Localization.SetLanguage("zh-CN");

    public void Dispose() => Localization.SetLanguage("zh-CN");

    [Theory]
    [InlineData(403, "服务端未配置访问令牌", "Access token is not configured on the server")]
    [InlineData(403, "token 错误或缺失", "Token is incorrect or missing")]
    [InlineData(400, "插件不存在", "Plugin does not exist")]
    [InlineData(400, "会话不存在或已过期", "Session does not exist or has expired")]
    [InlineData(400, "路径越界：不允许访问 FileRoot 之外的目录", "Path escapes the sandbox: access outside FileRoot is not allowed")]
    [InlineData(400, "系统目录（Windows）受保护，禁止访问", "The Windows system directory is protected; access is not allowed")]
    [InlineData(400, "系统目录（Windows）受保护，禁止读取", "The Windows system directory is protected; reading is not allowed")]
    [InlineData(400, "系统目录（Windows）受保护，禁止修改", "The Windows system directory is protected; modification is not allowed")]
    [InlineData(400, "消息过大", "Message too large")]
    [InlineData(400, "内测端点未开启（config: beta_file_root_ws: false）", "Beta endpoint is disabled (config: beta_file_root_ws: false)")]
    [InlineData(404, "控制接口已切换为 WebSocket 模式（control_transport: ws），HTTP /control/* 已禁用", "Control API switched to WebSocket mode (control_transport: ws); HTTP /control/* is disabled")]
    [InlineData(400, "数据接口已关闭：verify_token 未设置为强随机值（禁止出厂默认/空/弱口令）。请修改 config.yml 后重启。", "Data API is disabled: verify_token is not a strong random value (factory default, empty, and weak values are rejected). Update config.yml and restart.")]
    [InlineData(400, "当前游戏版本不提供假人 API（DummyUtils 不存在）", "This game version does not provide the dummy API (DummyUtils is missing)")]
    [InlineData(400, "控制接口仅支持 POST", "Control API only supports POST")]
    [InlineData(400, "command=send 需要 level 字段（非负整数目标楼层）", "command=send requires the level field (a non-negative target floor)")]
    [InlineData(400, "新上传必须从 offset 0 开始（或携带 upload_id 续传）", "A new upload must start at offset 0 (or include upload_id to resume)")]
    [InlineData(400, "不支持扩展位", "Unsupported reserved bits")]
    public void ListedMessagePairs_HaveSameCodeInBothLanguages(int status, string zh, string en)
    {
        AssertSameCode(status, zh, en);
    }

    [Theory]
    [InlineData(400, "目录不存在: {0}", "Directory does not exist: {0}", "logs")]
    [InlineData(400, "文件不存在: {0}", "File does not exist: {0}", "config.yml")]
    [InlineData(400, "日志文件不存在: {0}", "Log file does not exist: {0}", "server.log")]
    [InlineData(400, "受保护目录（SLDataAPI 配置），禁止{0}", "Protected directory (SLDataAPI configuration); {0} is not allowed", "access")]
    [InlineData(400, "effect_intensity 超出范围: {0}（0-255，0=关闭）", "effect_intensity is out of range: {0} (0-255; 0 disables the effect)", 300)]
    [InlineData(403, "该来源已因多次鉴权失败被临时锁定，请 {0} 秒后重试", "This source is temporarily locked after repeated authentication failures; retry in {0} seconds", 300)]
    public void ListedFormattedPairs_HaveSameCodeInBothLanguages(int status, string zh, string en, object arg)
    {
        AssertSameCode(status, zh, en, arg);
    }

    [Fact]
    public void SourceLocalizationPairs_HaveSameCodeInBothLanguages()
    {
        var pairs = LoadSourcePairs();
        Assert.True(pairs.Count >= 200, $"只解析到 {pairs.Count} 对 Localization.T/F 文本，扫描可能失效");

        var failures = new List<string>();
        foreach (Pair pair in pairs)
        {
            try
            {
                object?[] args = SampleArgs(pair);
                AssertSameCode(400, pair.Zh, pair.En, args, failures);
            }
            catch (Exception ex)
            {
                failures.Add($"{pair.Zh} / {pair.En}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Localization.SetLanguage("zh-CN");
        Assert.True(failures.Count == 0,
            $"以下文本对在 zh-CN / en 下推断出的 error.code 不同（共 {failures.Count} 对）：\n" +
            string.Join("\n", failures.Take(30)));
    }

    [Fact]
    public void DuplicateEnglishTranslations_DoNotConflict()
    {
        var pairs = LoadSourcePairs();
        Localization.SetLanguage("zh-CN");

        var conflicts = new List<string>();
        foreach (var group in pairs.GroupBy(p => Render(p, english: true)))
        {
            string[] codes = group
                .Select(p => Localization.ErrorCodeFor(400, Render(p, english: false)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (codes.Length > 1)
            {
                conflicts.Add($"{group.Key}: {string.Join(", ", codes)} | " +
                              string.Join(" | ", group.Select(p => p.Zh)));
            }
        }

        Assert.True(conflicts.Count == 0,
            "同一英文文本对应了不同 error.code，缓存反向映射会引入歧义：\n" +
            string.Join("\n", conflicts.Take(20)));
    }

    private static List<Pair> LoadSourcePairs()
    {
        string root = FindRepoRoot();
        var pairs = new List<Pair>();
        foreach (string file in EnumerateSourceFiles(root))
        {
            string source = File.ReadAllText(file);
            bool includeBare = string.Equals(Path.GetFileName(file), "Localization.cs", StringComparison.Ordinal);
            ExtractPairs(source, includeBare, pairs);
        }
        return pairs;
    }

    private static string Render(Pair pair, bool english)
    {
        string template = english ? pair.En : pair.Zh;
        if (!pair.IsFormat) return template;
        return string.Format(template, SampleArgs(pair));
    }

    private static object?[] SampleArgs(Pair pair)
    {
        int max = Math.Max(MaxPlaceholder(pair.Zh), MaxPlaceholder(pair.En));
        return Enumerable.Range(0, max + 1).Select(_ => (object?)"X").ToArray();
    }

    private static void AssertSameCode(int status, string zh, string en, params object?[] args)
    {
        var failures = new List<string>();
        AssertSameCode(status, zh, en, args, failures);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void AssertSameCode(int status, string zh, string en, object?[] args, List<string> failures)
    {
        Localization.SetLanguage("zh-CN");
        string zhMessage = args.Length == 0 ? Localization.T(zh, en) : Localization.F(zh, en, args);
        string zhCode = Localization.ErrorCodeFor(status, zhMessage);

        Localization.SetLanguage("en");
        string enMessage = args.Length == 0 ? Localization.T(zh, en) : Localization.F(zh, en, args);
        string enCode = Localization.ErrorCodeFor(status, enMessage);

        if (!string.Equals(zhCode, enCode, StringComparison.Ordinal))
            failures.Add($"{zh} => {en} (status {status}): zh={zhCode}, en={enCode}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SLDataAPI.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录（SLDataAPI.csproj）");
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string[] parts = file.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                               p.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return file;
        }
    }

    private sealed record Pair(string Zh, string En, bool IsFormat);

    private static void ExtractPairs(string source, bool includeBare, List<Pair> pairs)
    {
        for (int i = 0; i < source.Length; i++)
        {
            bool isFormat;
            int argsStart;
            if (StartsWith(source, i, "Localization.T("))
            {
                argsStart = i + "Localization.T(".Length;
                isFormat = false;
            }
            else if (StartsWith(source, i, "Localization.F("))
            {
                argsStart = i + "Localization.F(".Length;
                isFormat = true;
            }
            else if (includeBare && MatchesBareCall(source, i, "T("))
            {
                argsStart = i + 2;
                isFormat = false;
            }
            else if (includeBare && MatchesBareCall(source, i, "F("))
            {
                argsStart = i + 2;
                isFormat = true;
            }
            else
            {
                continue;
            }

            int openParen = argsStart - 1;
            int closeParen = FindMatchingParen(source, openParen);
            if (closeParen < 0)
            {
                i = argsStart;
                continue;
            }

            string argsText = source.Substring(argsStart, closeParen - argsStart);
            List<string> args = SplitTopLevel(argsText);
            if (args.Count >= 2)
            {
                string? zh = EvalStringExpression(args[0]);
                string? en = EvalStringExpression(args[1]);
                if (zh != null && en != null)
                    pairs.Add(new Pair(zh, en, isFormat));
            }

            i = closeParen;
        }
    }

    private static bool StartsWith(string source, int index, string token) =>
        index >= 0 && index + token.Length <= source.Length &&
        source.AsSpan(index, token.Length).SequenceEqual(token);

    private static bool MatchesBareCall(string source, int index, string token)
    {
        if (!StartsWith(source, index, token)) return false;
        if (index == 0) return true;
        char prev = source[index - 1];
        return !char.IsLetterOrDigit(prev) && prev != '_' && prev != '.';
    }

    private static int FindMatchingParen(string source, int openParen)
    {
        int depth = 0;
        bool inString = false, verbatim = false, inChar = false, lineComment = false, blockComment = false;

        for (int i = openParen; i < source.Length; i++)
        {
            char c = source[i];

            if (lineComment)
            {
                if (c == '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (c == '*' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    blockComment = false;
                    i++;
                }
                continue;
            }
            if (inString)
            {
                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') i++;
                        else inString = false;
                    }
                }
                else
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                continue;
            }
            if (inChar)
            {
                if (c == '\\') i++;
                else if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && i + 1 < source.Length)
            {
                if (source[i + 1] == '/') { lineComment = true; i++; continue; }
                if (source[i + 1] == '*') { blockComment = true; i++; continue; }
            }
            if (c == '"')
            {
                verbatim = i > 0 && source[i - 1] == '@';
                inString = true;
                continue;
            }
            if (c == '\'')
            {
                inChar = true;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var result = new List<string>();
        int start = 0, depth = 0;
        bool inString = false, verbatim = false, inChar = false, lineComment = false, blockComment = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (lineComment)
            {
                if (c == '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    blockComment = false;
                    i++;
                }
                continue;
            }
            if (inString)
            {
                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') i++;
                        else inString = false;
                    }
                }
                else
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                continue;
            }
            if (inChar)
            {
                if (c == '\\') i++;
                else if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/') { lineComment = true; i++; continue; }
                if (text[i + 1] == '*') { blockComment = true; i++; continue; }
            }
            if (c == '"')
            {
                verbatim = i > 0 && text[i - 1] == '@';
                inString = true;
                continue;
            }
            if (c == '\'')
            {
                inChar = true;
                continue;
            }
            if (c == '(' || c == '{' || c == '[') depth++;
            else if (c == ')' || c == '}' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }

        result.Add(text.Substring(start));
        return result;
    }

    private static string? EvalStringExpression(string expression)
    {
        string text = expression.Trim();
        if (text.Length == 0) return null;

        var sb = new StringBuilder();
        int i = 0;
        bool sawLiteral = false;

        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            if (text[i] == '+')
            {
                i++;
                continue;
            }

            bool verbatim = false;
            if (text[i] == '@')
            {
                verbatim = true;
                i++;
            }
            if (i >= text.Length || text[i] != '"') return null;
            i++;

            if (verbatim)
            {
                bool closed = false;
                while (i < text.Length)
                {
                    char c = text[i++];
                    if (c == '"')
                    {
                        if (i < text.Length && text[i] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            closed = true;
                            break;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                if (!closed) return null;
            }
            else
            {
                bool closed = false;
                while (i < text.Length)
                {
                    char c = text[i++];
                    if (c == '"')
                    {
                        closed = true;
                        break;
                    }
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (i >= text.Length) return null;
                    char esc = text[i++];
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        case '0': sb.Append('\0'); break;
                        case 'u':
                            if (i + 4 > text.Length) return null;
                            if (!int.TryParse(text.AsSpan(i, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out int code))
                                return null;
                            sb.Append((char)code);
                            i += 4;
                            break;
                        case 'x':
                        {
                            int len = 0;
                            while (len < 4 && i + len < text.Length && Uri.IsHexDigit(text[i + len])) len++;
                            if (len == 0) return null;
                            sb.Append((char)int.Parse(text.AsSpan(i, len), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture));
                            i += len;
                            break;
                        }
                        default:
                            sb.Append(esc);
                            break;
                    }
                }
                if (!closed) return null;
            }

            sawLiteral = true;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i < text.Length && text[i] != '+') return null;
        }

        return sawLiteral ? sb.ToString() : null;
    }

    private static int MaxPlaceholder(string template)
    {
        int max = -1;
        for (int i = 0; i + 1 < template.Length; i++)
        {
            if (template[i] != '{' || !char.IsDigit(template[i + 1])) continue;
            int j = i + 1;
            int value = 0;
            while (j < template.Length && char.IsDigit(template[j]))
            {
                value = value * 10 + (template[j] - '0');
                j++;
            }
            if (j < template.Length && template[j] == '}')
            {
                max = Math.Max(max, value);
                i = j;
            }
        }
        return max;
    }
}
