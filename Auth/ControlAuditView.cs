using System;
using System.Text.RegularExpressions;

namespace SLDataAPI.Auth;

/// <summary>
/// 控制审计列表的查看策略：duty 默认不授予 /control/audit/list；
/// 即便被 override 打开，非 admin 也看不到其他 Key 的写操作请求体。
/// 落盘时顺带打码已知密钥形态。纯字符串逻辑，可供单元测试直接链接。
/// </summary>
public static class ControlAuditView
{
    public const string RedactedBody = "[redacted]";

    private static readonly Regex ApiKeyBlob = new Regex(
        @"sld_(?:live|duty)_[A-Za-z0-9_-]+",
        RegexOptions.Compiled);

    private static readonly Regex BearerBlob = new Regex(
        @"(?i)Bearer\s+\S+",
        RegexOptions.Compiled);

    private static readonly Regex SecretJsonField = new Regex(
        @"(?i)(""(?:password|passwd|token|verify_token|api_key|control_token|webdav_password|authorization)""\s*:\s*)""[^""]*""",
        RegexOptions.Compiled);

    public static bool IsPrivilegedViewer(string? template) =>
        string.Equals(template, "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>admin 看全量；其他人只能看自己这条的请求体。</summary>
    public static bool CanSeeBody(string? entryActor, string? viewerId, string? viewerTemplate)
    {
        if (IsPrivilegedViewer(viewerTemplate))
            return true;
        if (string.IsNullOrEmpty(viewerId) || string.IsNullOrEmpty(entryActor))
            return false;
        return string.Equals(entryActor, viewerId, StringComparison.OrdinalIgnoreCase);
    }

    public static string BodyForViewer(string? body, string? entryActor, string? viewerId, string? viewerTemplate)
    {
        string redacted = RedactSecrets(body);
        return CanSeeBody(entryActor, viewerId, viewerTemplate) ? redacted : RedactedBody;
    }

    /// <summary>落盘/回显前打码常见密钥形态；失败时返回原文截断保护不抛。</summary>
    public static string RedactSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        try
        {
            string s = ApiKeyBlob.Replace(text, "sld_***");
            s = BearerBlob.Replace(s, "Bearer ***");
            s = SecretJsonField.Replace(s, "$1\"***\"");
            return s;
        }
        catch
        {
            return text!;
        }
    }
}
