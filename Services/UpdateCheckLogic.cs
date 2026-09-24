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

    /// <summary>评估 GitHub /releases/latest JSON。detail 不含机密。</summary>
    public static UpdateCheckOutcome EvaluateLatestRelease(string? json, Version current, out Version? remote, out string detail)
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
        if (parsed <= current)
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
