using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SLDataAPI.Auth;

/// <summary>
/// 端点授权值：bool 全开/全关，或 read/write 分离。
/// 纯逻辑，无 Unity / LabAPI 依赖，可供单元测试直接链接。
/// </summary>
public sealed class EndpointGrant
{
    public bool? Allow { get; }
    public bool? Read { get; }
    public bool? Write { get; }

    private EndpointGrant(bool? allow, bool? read, bool? write)
    {
        Allow = allow;
        Read = read;
        Write = write;
    }

    public static EndpointGrant FromBool(bool allow) => new EndpointGrant(allow, null, null);

    public static EndpointGrant FromReadWrite(bool read, bool write) =>
        new EndpointGrant(null, read, write);

    /// <summary>解析 YAML/字典中的端点值：bool、或含 read/write 的对象。</summary>
    public static bool TryParse(object? raw, out EndpointGrant grant)
    {
        grant = FromBool(false);
        if (raw == null) return false;

        if (raw is bool b)
        {
            grant = FromBool(b);
            return true;
        }

        if (raw is string s)
        {
            if (bool.TryParse(s, out bool sb))
            {
                grant = FromBool(sb);
                return true;
            }
            return false;
        }

        if (raw is IDictionary<string, object> dict)
        {
            bool? read = null, write = null;
            if (dict.TryGetValue("read", out var r)) read = CoerceBool(r);
            if (dict.TryGetValue("write", out var w)) write = CoerceBool(w);
            if (read == null && dict.TryGetValue("Read", out r)) read = CoerceBool(r);
            if (write == null && dict.TryGetValue("Write", out w)) write = CoerceBool(w);
            if (read == null && write == null) return false;
            grant = FromReadWrite(read ?? false, write ?? false);
            return true;
        }

        if (raw is System.Collections.IDictionary idict)
        {
            bool? read = null, write = null;
            foreach (System.Collections.DictionaryEntry e in idict)
            {
                string? k = e.Key?.ToString();
                if (k == null) continue;
                if (string.Equals(k, "read", StringComparison.OrdinalIgnoreCase))
                    read = CoerceBool(e.Value);
                else if (string.Equals(k, "write", StringComparison.OrdinalIgnoreCase))
                    write = CoerceBool(e.Value);
            }
            if (read == null && write == null) return false;
            grant = FromReadWrite(read ?? false, write ?? false);
            return true;
        }

        return false;
    }

    private static bool? CoerceBool(object? v)
    {
        if (v is bool b) return b;
        if (v is string s && bool.TryParse(s, out bool sb)) return sb;
        return null;
    }

    /// <summary>wantWrite=true 时要求写权限；false 时要求读权限（bool true 视为读写皆可）。</summary>
    public bool Permits(bool wantWrite)
    {
        if (Allow.HasValue) return Allow.Value;
        if (wantWrite) return Write == true;
        return Read == true;
    }
}

/// <summary>API Key 指纹与端点 ACL（最长前缀 + 模板合并）。无外部依赖。</summary>
public static class EndpointAcl
{
    public const string AllControlTrue = "all_control_true";

    /// <summary>内置端点目录（与契约 §3.1 / §4.2 对齐）。</summary>
    public static IReadOnlyDictionary<string, bool> DefaultCatalog { get; } =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["/control/player/data"] = true,
            ["/control/player/role"] = true,
            ["/control/player/effects"] = true,
            ["/control/player/inventory"] = false,
            ["/control/moderation/"] = true,
            ["/control/admin/"] = true,
            ["/control/broadcast"] = true,
            ["/control/staffchat"] = true,
            ["/control/round/"] = true,
            ["/control/round"] = true,
            ["/control/dummies/"] = true,
            ["/control/map/"] = true,
            ["/control/cassie"] = true,
            ["/control/console/"] = false,
            ["/control/plugins"] = false,
            ["/control/plugins/"] = false,
            ["/control/files/"] = false,
            ["/control/logs"] = true,
            ["/control/reports"] = true,
            ["/control/audit/list"] = true,
            ["voice:/ws"] = true,
            ["voice:/status"] = true,
            ["ws:subscribe_events"] = true,
        };

    /// <summary>值班模板默认端点表。</summary>
    public static IReadOnlyDictionary<string, EndpointGrant> DutyDefaults { get; } =
        new Dictionary<string, EndpointGrant>(StringComparer.Ordinal)
        {
            ["/control/player/data"] = EndpointGrant.FromReadWrite(true, false),
            ["/control/map/"] = EndpointGrant.FromReadWrite(true, false),
            ["/control/round/"] = EndpointGrant.FromReadWrite(true, false),
            ["/control/round"] = EndpointGrant.FromReadWrite(true, false),
            ["/control/logs"] = EndpointGrant.FromBool(true),
            ["/control/audit/list"] = EndpointGrant.FromBool(true),
            ["ws:subscribe_events"] = EndpointGrant.FromBool(true),
            ["voice:/ws"] = EndpointGrant.FromBool(false),
            ["voice:/status"] = EndpointGrant.FromBool(false),
            ["/control/moderation/"] = EndpointGrant.FromBool(false),
            ["/control/admin/"] = EndpointGrant.FromBool(false),
            ["/control/cassie"] = EndpointGrant.FromBool(false),
            ["/control/console/"] = EndpointGrant.FromBool(false),
            ["/control/plugins"] = EndpointGrant.FromBool(false),
            ["/control/plugins/"] = EndpointGrant.FromBool(false),
            ["/control/files/"] = EndpointGrant.FromBool(false),
            ["/control/reports"] = EndpointGrant.FromBool(false),
            ["/control/broadcast"] = EndpointGrant.FromBool(false),
            ["/control/staffchat"] = EndpointGrant.FromBool(false),
            ["/control/player/role"] = EndpointGrant.FromBool(false),
            ["/control/player/effects"] = EndpointGrant.FromBool(false),
            ["/control/player/inventory"] = EndpointGrant.FromBool(false),
            ["/control/dummies/"] = EndpointGrant.FromBool(false),
        };

    /// <summary>SHA-256 指纹，格式 sha256:hexlowercase。</summary>
    public static string Fingerprint(string plaintext)
    {
        plaintext ??= string.Empty;
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(plaintext));
        var sb = new StringBuilder(hash.Length * 2 + 7);
        sb.Append("sha256:");
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// 合并模板端点与 keys[].endpoints_override（覆盖优先）。
    /// templateName: duty | admin；admin 展开为 catalog 全 true（再被 override 裁剪）。
    /// </summary>
    public static Dictionary<string, EndpointGrant> MergeEffective(
        string templateName,
        IDictionary<string, object>? templateEndpoints,
        IDictionary<string, object>? endpointsOverride,
        IDictionary<string, bool>? catalog = null)
    {
        var result = new Dictionary<string, EndpointGrant>(StringComparer.Ordinal);
        string tmpl = (templateName ?? "").Trim().ToLowerInvariant();
        var cat = catalog != null
            ? new Dictionary<string, bool>(catalog.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)
            : DefaultCatalog.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        if (tmpl == "admin")
        {
            if (IsAllControlTrueOnly(templateEndpoints))
            {
                foreach (var kv in cat)
                    result[kv.Key] = EndpointGrant.FromBool(true);
            }
            else if (templateEndpoints != null && templateEndpoints.Count > 0)
            {
                foreach (var kv in templateEndpoints)
                {
                    if (kv.Value is string sv &&
                        string.Equals(sv, AllControlTrue, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var c in cat)
                            result[c.Key] = EndpointGrant.FromBool(true);
                    }
                    else if (EndpointGrant.TryParse(kv.Value, out var g))
                    {
                        result[kv.Key] = g;
                    }
                }
            }
            else
            {
                foreach (var kv in cat)
                    result[kv.Key] = EndpointGrant.FromBool(true);
            }
        }
        else
        {
            foreach (var kv in DutyDefaults)
                result[kv.Key] = kv.Value;

            if (templateEndpoints != null)
            {
                foreach (var kv in templateEndpoints)
                {
                    if (kv.Value is string sv &&
                        string.Equals(sv, AllControlTrue, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var c in cat)
                            result[c.Key] = EndpointGrant.FromBool(true);
                        continue;
                    }
                    if (EndpointGrant.TryParse(kv.Value, out var g))
                        result[kv.Key] = g;
                }
            }
        }

        if (endpointsOverride != null)
        {
            foreach (var kv in endpointsOverride)
            {
                if (EndpointGrant.TryParse(kv.Value, out var g))
                    result[kv.Key] = g;
            }
        }

        return result;
    }

    private static bool IsAllControlTrueOnly(IDictionary<string, object>? endpoints)
    {
        if (endpoints == null || endpoints.Count == 0) return true;
        if (endpoints.Count == 1)
        {
            var only = endpoints.First().Value;
            if (only is string sv &&
                string.Equals(sv, AllControlTrue, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 最长前缀匹配。键以 / 结尾：前缀匹配；否则精确或 键+/ 前缀。
    /// 未命中 → 拒绝。
    /// </summary>
    public static bool IsAllowed(IReadOnlyDictionary<string, EndpointGrant> grants, string path, bool wantWrite)
    {
        if (grants == null || string.IsNullOrEmpty(path))
            return false;

        string? bestKey = null;
        int bestLen = -1;

        foreach (var key in grants.Keys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (!PathMatches(key, path)) continue;
            if (key.Length > bestLen)
            {
                bestLen = key.Length;
                bestKey = key;
            }
        }

        if (bestKey == null)
            return false;

        return grants[bestKey].Permits(wantWrite);
    }

    public static bool PathMatches(string key, string path)
    {
        // 以 / 结尾 = 前缀规则；否则仅精确匹配（显式 path）
        if (key.EndsWith("/", StringComparison.Ordinal))
            return path.StartsWith(key, StringComparison.Ordinal) ||
                   (path + "/").Equals(key, StringComparison.Ordinal);

        return path.Equals(key, StringComparison.Ordinal);
    }

    /// <summary>
    /// 根据控制路径与请求体判断本次是否为写操作（与审计只读判定互补）。
    /// 判定失败按写处理（保守）。
    /// </summary>
    public static bool IsWriteOperation(string path, string? body)
    {
        try
        {
            body ??= "";
            switch (path)
            {
                case "/control/map/layout":
                case "/control/map/export":
                case "/control/moderation/ban_list":
                case "/control/logs":
                case "/control/files/list":
                case "/control/files/read":
                case "/control/audit/list":
                case "voice:/status":
                case "voice:/ws":
                case "ws:subscribe_events":
                    return false;

                case "/control/map/seed":
                    return false;

                case "/control/map/facility":
                    return true;

                case "/control/reports":
                    return !BodyActionEquals(body, "list");

                case "/control/admin/state":
                    return BodyHasAny(body, "godmode", "bypass", "health", "intercom");

                case "/control/player/data":
                    return BodyActionEquals(body, "write") ||
                           BodyActionEquals(body, "set") ||
                           BodyHasAny(body, "set_role", "patch");

                case "/control/round/wave":
                    return !BodyActionEquals(body, "status");

                case "/control/plugins":
                {
                    string? act = ExtractJsonString(body, "action");
                    return !string.IsNullOrWhiteSpace(act);
                }

                case "/control/plugins/slplayer":
                {
                    string? act = ExtractJsonString(body, "action");
                    if (string.IsNullOrWhiteSpace(act)) return false;
                    return act is not ("status" or "list");
                }

                default:
                    return true;
            }
        }
        catch
        {
            return true;
        }
    }

    private static bool BodyActionEquals(string body, string action)
    {
        string? a = ExtractJsonString(body, "action");
        return string.Equals(a, action, StringComparison.OrdinalIgnoreCase);
    }

    private static bool BodyHasAny(string body, params string[] fields)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        foreach (var f in fields)
        {
            string needle = "\"" + f + "\"";
            int i = body.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            int after = i + needle.Length;
            while (after < body.Length && char.IsWhiteSpace(body[after])) after++;
            if (after >= body.Length || body[after] != ':') continue;
            after++;
            while (after < body.Length && char.IsWhiteSpace(body[after])) after++;
            if (after >= body.Length) continue;
            if (after + 4 <= body.Length &&
                string.Compare(body, after, "null", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                char next = after + 4 < body.Length ? body[after + 4] : ',';
                if (next is ',' or '}' or ' ' or '\r' or '\n' or '\t')
                    continue;
            }
            return true;
        }
        return false;
    }

    private static string? ExtractJsonString(string body, string field)
    {
        if (string.IsNullOrEmpty(body)) return null;
        string needle = "\"" + field + "\"";
        int i = body.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int after = i + needle.Length;
        while (after < body.Length && char.IsWhiteSpace(body[after])) after++;
        if (after >= body.Length || body[after] != ':') return null;
        after++;
        while (after < body.Length && char.IsWhiteSpace(body[after])) after++;
        if (after >= body.Length) return null;
        if (body[after] == '"')
        {
            after++;
            int end = after;
            while (end < body.Length && body[end] != '"') end++;
            return body.Substring(after, end - after);
        }
        int e2 = after;
        while (e2 < body.Length && body[e2] is not (',' or '}' or ' ' or '\r' or '\n' or '\t'))
            e2++;
        return body.Substring(after, e2 - after);
    }
}
