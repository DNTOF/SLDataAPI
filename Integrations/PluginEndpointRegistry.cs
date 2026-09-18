using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SLDataAPI.Data;

namespace SLDataAPI.Integrations;

/// <summary>
/// In-process registry other LabAPI plugins can call to advertise adapted endpoints.
/// Prefer a project reference on SLDataAPI.dll; reflection against these public types also works.
/// </summary>
public static class PluginEndpointRegistry
{
    public const int MaxStatusJsonBytes = 4096;
    public const int MaxRoutesPerPlugin = 16;
    public const int MaxRouteBodyBytes = 65536;
    public const int RouteTimeoutMs = 3000;
    public const int MaxCapabilityCount = 32;
    public const int MaxIdLength = 64;
    public const int MaxNameLength = 128;
    public const int MaxVersionLength = 32;

    private static readonly object Gate = new object();
    private static readonly Dictionary<string, Registration> ById =
        new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex IdRegex = new Regex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex RoutePathRegex = new Regex(@"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.Compiled);

    /// <summary>Register (or fail on duplicate id). Returns false if rejected.</summary>
    public static bool TryRegister(
        string id,
        string name,
        string version,
        IEnumerable<string>? capabilities,
        Func<string?>? statusCallback,
        IEnumerable<AdaptedRoute>? routes,
        out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(id) || !IdRegex.IsMatch(id))
        {
            error = "invalid id (use reverse-dns style: letters/digits/._- , max 64)";
            return false;
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            error = "invalid name";
            return false;
        }
        version ??= "";
        if (version.Length > MaxVersionLength)
        {
            error = "version too long";
            return false;
        }

        var caps = NormalizeCapabilities(capabilities, out error);
        if (error.Length > 0) return false;

        var routeList = NormalizeRoutes(routes, out error);
        if (error.Length > 0) return false;

        var reg = new Registration
        {
            Id = id,
            Name = name.Trim(),
            Version = version.Trim(),
            Capabilities = caps,
            StatusCallback = statusCallback,
            Routes = routeList,
        };

        lock (Gate)
        {
            if (ById.ContainsKey(id))
            {
                error = $"duplicate id '{id}'";
                return false;
            }
            ById[id] = reg;
        }

        Log.Info($"[SLDataAPI] Adapted plugin registered: {id} ({name} {version}), caps={caps.Length}, routes={routeList.Count}");
        return true;
    }

    /// <summary>Unregister by id. Returns true if something was removed.</summary>
    public static bool Unregister(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (Gate)
        {
            if (!ById.Remove(id)) return false;
        }
        Log.Info($"[SLDataAPI] Adapted plugin unregistered: {id}");
        return true;
    }

    /// <summary>
    /// Snapshot ALL registered plugins for discovery. Status JSON is only filled when
    /// <paramref name="includeLiveStatus"/> is true AND this is called on the main thread
    /// (DataCollector). HTTP discovery that needs live status must use the cached field
    /// populated by <see cref="CollectStatusesOnMainThread"/>.
    /// </summary>
    public static List<AdaptedPluginInfo> Snapshot(bool includeLiveStatus = false)
    {
        List<Registration> regs;
        lock (Gate)
        {
            regs = ById.Values.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var list = new List<AdaptedPluginInfo>(regs.Count);
        foreach (var r in regs)
        {
            var info = ToInfo(r);
            if (includeLiveStatus)
                info.status = InvokeStatusSafe(r);
            list.Add(info);
        }
        return list;
    }

    /// <summary>Main-thread only: refresh status for every registration (per-plugin try/catch + 4KB cap).</summary>
    public static List<AdaptedPluginInfo> CollectStatusesOnMainThread()
    {
        return Snapshot(includeLiveStatus: true);
    }

    public static bool TryGet(string id, out AdaptedPluginInfo? info)
    {
        info = null;
        if (string.IsNullOrEmpty(id)) return false;
        Registration? reg;
        lock (Gate)
        {
            if (!ById.TryGetValue(id, out reg)) return false;
        }
        info = ToInfo(reg);
        return true;
    }

    /// <summary>
    /// Dispatch a read-only GET subpath. Must be invoked via MainThreadExecutor from HttpServer.
    /// Returns HTTP status + JSON body.
    /// </summary>
    public static (int status, string json) HandleRouteOnMainThread(string pluginId, string routePath)
    {
        if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(routePath))
            return (404, Err("not found"));

        if (routePath.Contains("..") || routePath.IndexOf('/') >= 0 || routePath.IndexOf('\\') >= 0)
            return (400, Err("invalid path"));

        if (!RoutePathRegex.IsMatch(routePath))
            return (400, Err("invalid path charset"));

        Registration? reg;
        lock (Gate)
        {
            if (!ById.TryGetValue(pluginId, out reg))
                return (404, Err("plugin not registered"));
        }

        AdaptedRoute? route = null;
        foreach (var r in reg.Routes)
        {
            if (string.Equals(r.Path, routePath, StringComparison.OrdinalIgnoreCase))
            {
                route = r;
                break;
            }
        }
        if (route == null)
            return (404, Err("route not found"));

        try
        {
            string? body = route.Handler?.Invoke(new AdaptedRouteRequest
            {
                PluginId = reg.Id,
                Path = route.Path,
            });

            if (body == null)
                return (204, "");

            if (Encoding.UTF8.GetByteCount(body) > MaxRouteBodyBytes)
                return (413, Err("route response too large"));

            // Validate JSON shape lightly; if not JSON object/array, wrap as string value
            try
            {
                JToken.Parse(body);
                return (200, body);
            }
            catch
            {
                return (200, JsonConvert.SerializeObject(new { ok = true, data = body }));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[SLDataAPI] Adapted route error {pluginId}/{routePath}: {ex.Message}");
            return (500, Err("route handler error"));
        }
    }

    /// <summary>Upsert a built-in / detector-backed registration (used by DntofDetector wrappers).</summary>
    internal static void UpsertInternal(
        string id,
        string name,
        string version,
        string[] capabilities,
        Func<string?>? statusCallback,
        IReadOnlyList<AdaptedRoute>? routes = null)
    {
        var reg = new Registration
        {
            Id = id,
            Name = name,
            Version = version ?? "",
            Capabilities = capabilities ?? Array.Empty<string>(),
            StatusCallback = statusCallback,
            Routes = routes != null ? routes.ToList() : new List<AdaptedRoute>(),
        };
        lock (Gate)
        {
            ById[id] = reg;
        }
    }

    internal static void RemoveInternal(string id)
    {
        lock (Gate) { ById.Remove(id); }
    }

    private static AdaptedPluginInfo ToInfo(Registration r) => new AdaptedPluginInfo
    {
        id = r.Id,
        name = r.Name,
        version = r.Version,
        capabilities = r.Capabilities.ToList(),
        routes = r.Routes.Select(x => x.Path).ToList(),
        status = null,
    };

    private static JToken? InvokeStatusSafe(Registration r)
    {
        if (r.StatusCallback == null) return null;
        try
        {
            string? raw = r.StatusCallback();
            if (string.IsNullOrEmpty(raw)) return null;
            if (Encoding.UTF8.GetByteCount(raw) > MaxStatusJsonBytes)
            {
                Log.Warn($"[SLDataAPI] Adapted status for {r.Id} exceeded {MaxStatusJsonBytes} bytes — truncated/dropped");
                return JObject.FromObject(new { error = "status_too_large" });
            }
            try { return JToken.Parse(raw!); }
            catch { return JValue.CreateString(raw); }
        }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI] Adapted status callback failed for {r.Id}: {ex.Message}");
            return JObject.FromObject(new { error = "status_failed" });
        }
    }

    private static string[] NormalizeCapabilities(IEnumerable<string>? capabilities, out string error)
    {
        error = "";
        if (capabilities == null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var c in capabilities)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var t = c.Trim();
            if (t.Length > 64) { error = "capability too long"; return Array.Empty<string>(); }
            list.Add(t);
            if (list.Count > MaxCapabilityCount) { error = "too many capabilities"; return Array.Empty<string>(); }
        }
        return list.ToArray();
    }

    private static List<AdaptedRoute> NormalizeRoutes(IEnumerable<AdaptedRoute>? routes, out string error)
    {
        error = "";
        var list = new List<AdaptedRoute>();
        if (routes == null) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in routes)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Path) || r.Handler == null)
            {
                error = "invalid route entry";
                return new List<AdaptedRoute>();
            }
            var path = r.Path.Trim().Trim('/');
            if (!RoutePathRegex.IsMatch(path) || path.Contains(".."))
            {
                error = $"invalid route path '{r.Path}'";
                return new List<AdaptedRoute>();
            }
            if (!seen.Add(path))
            {
                error = $"duplicate route '{path}'";
                return new List<AdaptedRoute>();
            }
            list.Add(new AdaptedRoute { Path = path, Handler = r.Handler });
            if (list.Count > MaxRoutesPerPlugin)
            {
                error = $"too many routes (max {MaxRoutesPerPlugin})";
                return new List<AdaptedRoute>();
            }
        }
        return list;
    }

    private static string Err(string message) =>
        JsonConvert.SerializeObject(new { success = false, message });

    private sealed class Registration
    {
        public string Id = "";
        public string Name = "";
        public string Version = "";
        public string[] Capabilities = Array.Empty<string>();
        public Func<string?>? StatusCallback;
        public List<AdaptedRoute> Routes = new List<AdaptedRoute>();
    }
}

/// <summary>Read-only route descriptor passed to <see cref="PluginEndpointRegistry.TryRegister"/>.</summary>
public sealed class AdaptedRoute
{
    public string Path { get; set; } = "";
    public Func<AdaptedRouteRequest, string?>? Handler { get; set; }
}

public sealed class AdaptedRouteRequest
{
    public string PluginId { get; set; } = "";
    public string Path { get; set; } = "";
}
