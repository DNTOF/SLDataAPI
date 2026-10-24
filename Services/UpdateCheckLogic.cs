// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SLDataAPI.Services;

/// <summary>GitHub latest 评估结果（无网络、无 LabAPI）。</summary>
public enum UpdateCheckOutcome
{
    UpToDate,
    PreReleaseSkipped,
    UpdateAvailable,
    Failed,
}

/// <summary>磁盘文件强名称验签结果（真验签，非公钥令牌元数据）。</summary>
public enum StrongNameVerifyStatus
{
    /// <summary>签名有效且已强制验证。</summary>
    Verified,
    /// <summary>文件缺失、损坏或签名无效。</summary>
    Invalid,
    /// <summary>原生验签 API 不可用（非 Windows / mscoree 缺失等）。</summary>
    Unavailable,
}

/// <summary>自动安装前签名闸门决策（令牌快筛 + 真验签）。</summary>
public enum AutoInstallSignatureDecision
{
    Allow,
    RefuseUnsignedCurrent,
    RefuseTokenMismatch,
    RefuseNotOfficialToken,
    RefuseSignatureInvalid,
    RefuseVerifyUnavailable,
}

/// <summary>
/// 更新检查的纯逻辑：状态文件、是否到期、解析 GitHub latest JSON / 版本 tag。
/// 无 Unity / LabAPI 依赖，可供单元测试直接链接。
/// </summary>
public static class UpdateCheckLogic
{
    public const string StateFileName = "update_check_state.json";
    public const int DefaultIntervalHours = 72;

    /// <summary>正式发布构建的公钥令牌（与 SECURITY.md / key.snk 一致）。</summary>
    public const string ExpectedPublicKeyTokenHex = "3ec73bb20070fa9c";

    private static readonly Regex PreReleaseSegmentRegex = new Regex(
        @"^(?:beta|alpha|preview|pre|prerelease|rc|dev|nightly|canary|snapshot)\d*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LastCheckJson = new Regex(
        @"""last_check_utc""\s*:\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string StatePath(string? configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir))
            return "";
        return Path.Combine(configDir, StateFileName);
    }

    /// <summary>
    /// interval ≤ 0：每次询问都到期（仅启动检查、不周期）。
    /// lastCheck 为空：到期（首次 / 无状态文件）。
    /// </summary>
    public static bool IsDue(DateTime? lastCheckUtc, DateTime nowUtc, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            return true;
        if (!lastCheckUtc.HasValue)
            return true;
        return nowUtc >= lastCheckUtc.Value.ToUniversalTime() + interval;
    }

    public static TimeSpan DelayUntilDue(DateTime? lastCheckUtc, DateTime nowUtc, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || !lastCheckUtc.HasValue)
            return TimeSpan.Zero;
        TimeSpan remain = lastCheckUtc.Value.ToUniversalTime() + interval - nowUtc.ToUniversalTime();
        return remain > TimeSpan.Zero ? remain : TimeSpan.Zero;
    }

    public static TimeSpan IntervalFromHours(int hours)
    {
        if (hours <= 0)
            return TimeSpan.Zero;
        if (hours > 24 * 365)
            hours = 24 * 365;
        return TimeSpan.FromHours(hours);
    }

    public static DateTime? LoadLastCheckUtc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var m = LastCheckJson.Match(text);
            string raw = m.Success ? m.Groups[1].Value : text.Trim();
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
                return utc;
        }
        catch
        {
            /* 损坏则视为无状态，下次会再查 */
        }
        return null;
    }

    public static bool TrySaveLastCheckUtc(string? path, DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string stamp = utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(path, "{\"last_check_utc\":\"" + stamp + "\"}", Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDueFromFile(string? path, DateTime nowUtc, TimeSpan interval) =>
        IsDue(LoadLastCheckUtc(path), nowUtc, interval);

    /// <summary>当前程序集公钥令牌是否为非空强名称。</summary>
    public static bool IsSignedPublicKeyToken(byte[]? token) =>
        token != null && token.Length > 0;

    /// <summary>未签名构建不得自动安装（即使 AutoUpdateInstall=true）。</summary>
    public static bool ShouldRefuseAutoInstallBecauseUnsigned(byte[]? currentPublicKeyToken) =>
        !IsSignedPublicKeyToken(currentPublicKeyToken);

    /// <summary>已签名构建要求新文件令牌与当前一致。</summary>
    public static bool PublicKeyTokensMatch(byte[]? current, byte[]? incoming) =>
        IsSignedPublicKeyToken(current) &&
        IsSignedPublicKeyToken(incoming) &&
        current!.SequenceEqual(incoming!);

    /// <summary>令牌是否为正式发布公钥令牌（<see cref="ExpectedPublicKeyTokenHex"/>）。</summary>
    public static bool MatchesExpectedPublicKeyToken(byte[]? token)
    {
        if (!IsSignedPublicKeyToken(token))
            return false;
        byte[]? expected = TryParsePublicKeyTokenHex(ExpectedPublicKeyTokenHex);
        return expected != null && token!.SequenceEqual(expected);
    }

    /// <summary>解析 16 位小写/大写十六进制公钥令牌；非法则 null。</summary>
    public static byte[]? TryParsePublicKeyTokenHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 16)
            return null;
        var bytes = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }

    /// <summary>
    /// 自动安装签名闸门：先令牌（未签名 / 不一致 / 非正式令牌），再真验签。
    /// <paramref name="currentFileVerify"/> 为对当前磁盘插件文件的验签；传 null 则跳过该项（仅测令牌时）。
    /// fail-closed：Unavailable / Invalid 一律拒绝。
    /// </summary>
    public static AutoInstallSignatureDecision DecideAutoInstallSignature(
        byte[]? currentToken,
        byte[]? incomingToken,
        StrongNameVerifyStatus incomingVerify,
        StrongNameVerifyStatus? currentFileVerify = null)
    {
        if (ShouldRefuseAutoInstallBecauseUnsigned(currentToken))
            return AutoInstallSignatureDecision.RefuseUnsignedCurrent;
        if (!PublicKeyTokensMatch(currentToken, incomingToken))
            return AutoInstallSignatureDecision.RefuseTokenMismatch;
        if (!MatchesExpectedPublicKeyToken(incomingToken))
            return AutoInstallSignatureDecision.RefuseNotOfficialToken;

        if (incomingVerify == StrongNameVerifyStatus.Unavailable ||
            currentFileVerify == StrongNameVerifyStatus.Unavailable)
            return AutoInstallSignatureDecision.RefuseVerifyUnavailable;
        if (incomingVerify != StrongNameVerifyStatus.Verified ||
            (currentFileVerify.HasValue && currentFileVerify.Value != StrongNameVerifyStatus.Verified))
            return AutoInstallSignatureDecision.RefuseSignatureInvalid;

        return AutoInstallSignatureDecision.Allow;
    }

    /// <summary>评估 GitHub /releases/latest JSON。detail 不含机密。</summary>
    /// <param name="currentIsPreRelease">当前运行的是预发布构建（如 2.6.2-preview）时为 true：同版本号的正式版（2.6.2）也视为更新。</param>
    public static UpdateCheckOutcome EvaluateLatestRelease(string? json, Version current, out Version? remote, out string detail,
        bool currentIsPreRelease = false)
    {
        remote = null;
        detail = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            detail = "响应为空";
            return UpdateCheckOutcome.Failed;
        }

        JObject obj;
        try
        {
            obj = JObject.Parse(json);
        }
        catch (Exception ex)
        {
            detail = "JSON 解析失败: " + ex.Message;
            return UpdateCheckOutcome.Failed;
        }

        string tag = obj["tag_name"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(tag))
        {
            detail = "缺少 tag_name";
            return UpdateCheckOutcome.Failed;
        }

        if (obj["prerelease"]?.Value<bool>() == true || obj["draft"]?.Value<bool>() == true ||
            IsPreReleaseTag(tag))
        {
            detail = tag;
            return UpdateCheckOutcome.PreReleaseSkipped;
        }

        if (!TryParseVersion(tag, out var parsed))
        {
            detail = "无法解析版本: " + tag;
            return UpdateCheckOutcome.Failed;
        }

        remote = parsed;
        if (parsed < current || (parsed == current && !currentIsPreRelease))
        {
            detail = tag;
            return UpdateCheckOutcome.UpToDate;
        }

        detail = tag;
        return UpdateCheckOutcome.UpdateAvailable;
    }

    /// <summary>tag 是否为预发布版本：按 -_. 分段，任一段命中预发布标识词即判定。</summary>
    public static bool IsPreReleaseTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        foreach (string seg in tag.Split('-', '_', '.'))
        {
            if (PreReleaseSegmentRegex.IsMatch(seg))
                return true;
        }
        return false;
    }

    /// <summary>InformationalVersion（如 "2.6.2-preview CODENAME"）是否标记为预发布：按空格与 -_.+ 分段判定。</summary>
    public static bool IsPreReleaseInformationalVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return false;
        foreach (string seg in informational!.Split(new[] { ' ', '-', '_', '.', '+' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (PreReleaseSegmentRegex.IsMatch(seg))
                return true;
        }
        return false;
    }

    /// <summary>容错解析版本号：支持 v2.1.0 / 2.1.0 等。预发布 tag 不解析。</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        version = null!;
        if (string.IsNullOrEmpty(tag)) return false;
        if (IsPreReleaseTag(tag)) return false;
        var m = Regex.Match(tag, @"(\d+)\.(\d+)\.(\d+)");
        if (!m.Success || !Version.TryParse(m.Value, out var parsed))
            return false;
        version = parsed;
        return true;
    }
}
