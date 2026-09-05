using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SLDataAPI.Auth;

/// <summary>apikey.config 中单把 Key 的落盘记录（永不存明文）。</summary>
public sealed class ApiKeyRecord
{
    public string Id { get; set; } = "";
    public string Template { get; set; } = "duty";
    public string Fingerprint { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Note { get; set; } = "";
    public Dictionary<string, object>? EndpointsOverride { get; set; }
}

/// <summary>内存中已合并权限的 Key 视图。</summary>
public sealed class ApiKeyPrincipal
{
    public string Id { get; set; } = "";
    public string Template { get; set; } = "";
    public IReadOnlyDictionary<string, EndpointGrant> Grants { get; set; } =
        new Dictionary<string, EndpointGrant>();

    public bool Allows(string path, bool wantWrite) =>
        EndpointAcl.IsAllowed(Grants, path, wantWrite);
}

/// <summary>
/// API Key 存储与校验（v2.6.0-preview-DevOnly 推出，代号 Kerckhoffs）：读写 apikey.config（仅指纹）、创建时明文只回传一次。
/// </summary>
public static class ApiKeyService
{
    private static readonly object Gate = new();
    private static string _path = "";
    private static List<ApiKeyRecord> _keys = new();
    private static Dictionary<string, object> _templates = new();
    private static Dictionary<string, bool> _catalog = EndpointAcl.DefaultCatalog.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    private static Dictionary<string, ApiKeyPrincipal> _byFingerprint =
        new(StringComparer.OrdinalIgnoreCase);

    public static string ConfigPath => _path;
    public static int KeyCount { get { lock (Gate) return _keys.Count; } }

    /// <summary>由 Plugin.Enable 调用：配置目录下加载/创建 apikey.config。</summary>
    public static void Init(string configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir))
            configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCP Secret Laboratory", "SLDataAPI");

        try { Directory.CreateDirectory(configDir); } catch { /* 写入失败会在日志可见 */ }

        lock (Gate)
        {
            _path = Path.Combine(configDir, "apikey.config");
            if (!File.Exists(_path))
            {
                WriteDefaultUnlocked();
                Log.Info($"[SLDataAPI] 已创建默认 apikey.config：{_path}");
            }
            ReloadUnlocked();
        }
    }

    public static void Reload()
    {
        lock (Gate) ReloadUnlocked();
    }

    /// <summary>
    /// 从请求头提取 API Key 明文。
    /// 支持 Authorization: Bearer 与 X-SLDataAPI-Key；不再接受 X-Control-Token / ?token= / ?key=。
    /// </summary>
    public static string? ExtractKeyFromHeaders(IDictionary<string, string> headers)
    {
        if (headers == null) return null;

        if (headers.TryGetValue("Authorization", out var auth) ||
            headers.TryGetValue("authorization", out auth))
        {
            if (!string.IsNullOrEmpty(auth))
            {
                const string bearer = "Bearer ";
                if (auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
                    return auth.Substring(bearer.Length).Trim();
            }
        }

        if (headers.TryGetValue("X-SLDataAPI-Key", out var k) ||
            headers.TryGetValue("x-sldataapi-key", out k))
            return string.IsNullOrWhiteSpace(k) ? null : k.Trim();

        return null;
    }

    /// <summary>从原始 HTTP 头文本提取（语音端口用）。</summary>
    public static string? ExtractKeyFromRawHeader(string headerBlock)
    {
        if (string.IsNullOrEmpty(headerBlock)) return null;
        string? bearer = null;
        string? alias = null;
        foreach (var raw in headerBlock.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int idx = line.IndexOf(':');
            if (idx <= 0) continue;
            string name = line.Substring(0, idx).Trim();
            string value = line.Substring(idx + 1).Trim();
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    bearer = value.Substring(7).Trim();
            }
            else if (name.Equals("X-SLDataAPI-Key", StringComparison.OrdinalIgnoreCase))
            {
                alias = value;
            }
        }
        return !string.IsNullOrEmpty(bearer) ? bearer : alias;
    }

    /// <summary>校验明文 Key → 主体。失败 401；权限另检 403。</summary>
    public static bool TryAuthenticate(string ip, string? plaintextKey, out ApiKeyPrincipal? principal, out string error)
    {
        principal = null;
        error = "";

        if (Control.ControlAuth.IsControlLocked(ip, out var remaining))
        {
            error = $"该来源已因多次鉴权失败被临时锁定，请 {Math.Ceiling(remaining.TotalSeconds)} 秒后重试";
            return false;
        }

        if (string.IsNullOrWhiteSpace(plaintextKey))
        {
            Control.ControlAuth.RegisterControlFailure(ip);
            error = "缺少 API Key（请使用 Authorization: Bearer 或 X-SLDataAPI-Key）";
            return false;
        }

        string fp = EndpointAcl.Fingerprint(plaintextKey ?? "");
        ApiKeyPrincipal? hit = null;
        lock (Gate)
        {
            foreach (var kv in _byFingerprint)
            {
                if (Control.ControlAuth.SecureEquals(kv.Key, fp))
                    hit = kv.Value;
            }
        }

        if (hit == null)
        {
            Control.ControlAuth.RegisterControlFailure(ip);
            error = "API Key 无效";
            return false;
        }

        Control.ControlAuth.ResetControlFailures(ip);
        principal = hit;
        return true;
    }

    /// <summary>创建 Key。明文仅通过 out plaintext 返回一次。</summary>
    public static bool TryCreate(string id, string template, string? note, out string plaintext, out string error)
    {
        plaintext = "";
        error = "";
        id = (id ?? "").Trim();
        template = (template ?? "").Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(id) || id.Length > 64 ||
            id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id.IndexOf(' ') >= 0)
        {
            error = "id 非法（非空、无空格、无路径非法字符、≤64）";
            return false;
        }

        if (template is not ("duty" or "admin"))
        {
            error = "template 仅支持 duty 或 admin";
            return false;
        }

        lock (Gate)
        {
            if (_keys.Any(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"id 已存在: {id}";
                return false;
            }

            string prefix = template == "duty" ? "sld_duty_" : "sld_live_";
            plaintext = prefix + GenerateSecret(24);
            var rec = new ApiKeyRecord
            {
                Id = id,
                Template = template,
                Fingerprint = EndpointAcl.Fingerprint(plaintext),
                CreatedAt = DateTime.UtcNow.ToString("o"),
                Note = note ?? "",
            };
            _keys.Add(rec);
            SaveUnlocked();
            RebuildIndexUnlocked();
        }

        return true;
    }

    public static bool TryRevoke(string id, out string error)
    {
        error = "";
        id = (id ?? "").Trim();
        lock (Gate)
        {
            int n = _keys.RemoveAll(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));
            if (n == 0)
            {
                error = $"未找到 id: {id}";
                return false;
            }
            SaveUnlocked();
            RebuildIndexUnlocked();
        }
        return true;
    }

    public static IReadOnlyList<(string Id, string Template, string CreatedAt, string Note, string Fingerprint)> List()
    {
        lock (Gate)
        {
            return _keys.Select(k => (k.Id, k.Template, k.CreatedAt, k.Note, k.Fingerprint)).ToList();
        }
    }
    private static string GenerateSecret(int bytes)
    {
        byte[] buf = new byte[bytes];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(buf);
        return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void WriteDefaultUnlocked()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# apikey.config — API Key 与端点权限（SLDataAPI v2.6.0-preview-DevOnly，代号 Kerckhoffs）");
        sb.AppendLine("# 密钥明文不会写回此文件；此处只存不可逆指纹 + 权限");
        sb.AppendLine("# 管理命令：sldataapi apikey create|revoke|list");
        sb.AppendLine();
        sb.AppendLine("endpoint_catalog:");
        foreach (var kv in EndpointAcl.DefaultCatalog)
            sb.AppendLine($"  \"{kv.Key}\": {(kv.Value ? "true" : "false")}");
        sb.AppendLine();
        sb.AppendLine("templates:");
        sb.AppendLine("  duty:");
        sb.AppendLine("    description: \"值班：只读信息（含地图定位只读），不可执行管理命令\"");
        sb.AppendLine("    endpoints:");
        foreach (var kv in EndpointAcl.DutyDefaults)
        {
            if (kv.Value.Allow.HasValue)
                sb.AppendLine($"      \"{kv.Key}\": {(kv.Value.Allow.Value ? "true" : "false")}");
            else
                sb.AppendLine($"      \"{kv.Key}\": {{ read: {(kv.Value.Read == true ? "true" : "false")}, write: {(kv.Value.Write == true ? "true" : "false")} }}");
        }
        sb.AppendLine("  admin:");
        sb.AppendLine("    description: \"管理：控制面全开（不含 /get_sl_data，数据口仍用 verify_token）\"");
        sb.AppendLine("    endpoints: all_control_true");
        sb.AppendLine();
        sb.AppendLine("keys: []");
        File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void ReloadUnlocked()
    {
        _keys = new List<ApiKeyRecord>();
        _templates = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        _catalog = EndpointAcl.DefaultCatalog.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
        {
            RebuildIndexUnlocked();
            return;
        }

        try
        {
            string text = File.ReadAllText(_path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var root = deserializer.Deserialize<Dictionary<object, object>>(text)
                       ?? new Dictionary<object, object>();

            if (TryGetMap(root, "endpoint_catalog", out var catMap))
            {
                foreach (var kv in catMap)
                {
                    string k = kv.Key?.ToString() ?? "";
                    if (string.IsNullOrEmpty(k)) continue;
                    if (kv.Value is bool b) _catalog[k] = b;
                    else if (bool.TryParse(kv.Value?.ToString(), out bool bb)) _catalog[k] = bb;
                }
            }

            if (TryGetMap(root, "templates", out var tmplMap))
            {
                foreach (var kv in tmplMap)
                {
                    string name = kv.Key?.ToString() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    _templates[name] = kv.Value!;
                }
            }

            if (TryGetList(root, "keys", out var keyList))
            {
                foreach (var item in keyList)
                {
                    if (item is not System.Collections.IDictionary d) continue;
                    var rec = new ApiKeyRecord
                    {
                        Id = GetStr(d, "id"),
                        Template = GetStr(d, "template"),
                        Fingerprint = GetStr(d, "fingerprint"),
                        CreatedAt = GetStr(d, "created_at"),
                        Note = GetStr(d, "note"),
                    };
                    if (TryGetMapFromDict(d, "endpoints_override", out var ov))
                        rec.EndpointsOverride = ov;
                    if (!string.IsNullOrEmpty(rec.Id) && !string.IsNullOrEmpty(rec.Fingerprint))
                        _keys.Add(rec);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI] 解析 apikey.config 失败: {ex.Message}");
        }

        RebuildIndexUnlocked();
        Log.Info($"[SLDataAPI] apikey.config 已加载：{_keys.Count} 把 Key，路径 {_path}");
    }

    private static void RebuildIndexUnlocked()
    {
        _byFingerprint = new Dictionary<string, ApiKeyPrincipal>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in _keys)
        {
            var tmplEndpoints = ResolveTemplateEndpoints(rec.Template);
            var grants = EndpointAcl.MergeEffective(
                rec.Template, tmplEndpoints, rec.EndpointsOverride, _catalog);
            _byFingerprint[rec.Fingerprint] = new ApiKeyPrincipal
            {
                Id = rec.Id,
                Template = rec.Template,
                Grants = grants,
            };
        }
    }

    private static Dictionary<string, object>? ResolveTemplateEndpoints(string templateName)
    {
        if (!_templates.TryGetValue(templateName, out var raw) &&
            !_templates.TryGetValue(templateName?.ToLowerInvariant() ?? "", out raw))
            return null;

        if (raw is string s &&
            string.Equals(s, EndpointAcl.AllControlTrue, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object> { [""] = EndpointAcl.AllControlTrue };
        }

        if (raw is System.Collections.IDictionary d)
        {
            if (TryGetMapFromDict(d, "endpoints", out var ep))
                return ep;

            object? epVal = null;
            foreach (System.Collections.DictionaryEntry e in d)
            {
                if (string.Equals(e.Key?.ToString(), "endpoints", StringComparison.OrdinalIgnoreCase))
                    epVal = e.Value;
            }
            if (epVal is string es &&
                string.Equals(es, EndpointAcl.AllControlTrue, StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object> { [""] = EndpointAcl.AllControlTrue };
        }

        return null;
    }

    private static void SaveUnlocked()
    {
        if (string.IsNullOrEmpty(_path)) return;

        var root = new Dictionary<object, object>
        {
            ["endpoint_catalog"] = _catalog.ToDictionary(kv => (object)kv.Key, kv => (object)kv.Value),
            ["templates"] = BuildTemplatesForSave(),
            ["keys"] = _keys.Select(k =>
            {
                var d = new Dictionary<object, object>
                {
                    ["id"] = k.Id,
                    ["template"] = k.Template,
                    ["fingerprint"] = k.Fingerprint,
                    ["created_at"] = k.CreatedAt,
                    ["note"] = k.Note ?? "",
                };
                if (k.EndpointsOverride != null && k.EndpointsOverride.Count > 0)
                    d["endpoints_override"] = k.EndpointsOverride.ToDictionary(
                        kv => (object)kv.Key, kv => kv.Value);
                return (object)d;
            }).ToList(),
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        string yaml = "# apikey.config — 由 SLDataAPI 管理；明文 Key 不会出现在此文件\n" +
                      serializer.Serialize(root);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, yaml, new UTF8Encoding(false));
        if (File.Exists(_path)) File.Delete(_path);
        File.Move(tmp, _path);
    }

    private static Dictionary<object, object> BuildTemplatesForSave()
    {
        if (_templates.Count == 0)
        {
            return new Dictionary<object, object>
            {
                ["duty"] = new Dictionary<object, object>
                {
                    ["description"] = "值班：只读信息（含地图定位只读），不可执行管理命令",
                    ["endpoints"] = DutyDefaultsObject(),
                },
                ["admin"] = new Dictionary<object, object>
                {
                    ["description"] = "管理：控制面全开（不含 /get_sl_data，数据口仍用 verify_token）",
                    ["endpoints"] = EndpointAcl.AllControlTrue,
                },
            };
        }

        var outMap = new Dictionary<object, object>();
        foreach (var kv in _templates)
            outMap[kv.Key] = kv.Value;
        return outMap;
    }

    private static Dictionary<object, object> DutyDefaultsObject()
    {
        var d = new Dictionary<object, object>();
        foreach (var kv in EndpointAcl.DutyDefaults)
        {
            if (kv.Value.Allow.HasValue)
                d[kv.Key] = kv.Value.Allow.Value;
            else
                d[kv.Key] = new Dictionary<object, object>
                {
                    ["read"] = kv.Value.Read == true,
                    ["write"] = kv.Value.Write == true,
                };
        }
        return d;
    }

    private static bool TryGetMap(Dictionary<object, object> root, string key, out Dictionary<object, object?> map)
    {
        map = new Dictionary<object, object?>();
        foreach (var kv in root)
        {
            if (!string.Equals(kv.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (kv.Value is System.Collections.IDictionary id)
            {
                foreach (System.Collections.DictionaryEntry e in id)
                    map[e.Key!] = e.Value;
                return true;
            }
        }
        return false;
    }

    private static bool TryGetMapFromDict(System.Collections.IDictionary d, string key, out Dictionary<string, object> map)
    {
        map = new Dictionary<string, object>(StringComparer.Ordinal);
        object? raw = null;
        foreach (System.Collections.DictionaryEntry e in d)
        {
            if (string.Equals(e.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                raw = e.Value;
        }
        if (raw is not System.Collections.IDictionary id) return false;
        foreach (System.Collections.DictionaryEntry e in id)
        {
            string k = e.Key?.ToString() ?? "";
            if (!string.IsNullOrEmpty(k) && e.Value != null)
                map[k] = e.Value;
        }
        return true;
    }

    private static bool TryGetList(Dictionary<object, object> root, string key, out List<object> list)
    {
        list = new List<object>();
        foreach (var kv in root)
        {
            if (!string.Equals(kv.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (kv.Value is System.Collections.IList il)
            {
                foreach (var item in il) list.Add(item!);
                return true;
            }
        }
        return false;
    }

    private static string GetStr(System.Collections.IDictionary d, string key)
    {
        foreach (System.Collections.DictionaryEntry e in d)
        {
            if (string.Equals(e.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                return e.Value?.ToString() ?? "";
        }
        return "";
    }
}