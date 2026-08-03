using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Exiled.API.Features;
using Newtonsoft.Json.Linq;

/// <summary>
/// 启动时异步检查 GitHub Releases 是否有新版本。
/// install=true 时自动下载并替换插件 DLL（EXILED 9 从内存加载插件，文件不被占用，
/// 覆盖后下次重启服务器生效）；install=false 时仅日志提示。
/// 失败（无网络、限流、校验不过）一律安全降级，不影响插件正常运行。
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/DNTOF/SLDataAPI/releases/latest";
    private const int MaxDllBytes = 5 * 1024 * 1024;   // 5MB 上限，防恶意超大文件

    private static readonly HttpClient Http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly string AsmName =
        typeof(UpdateChecker).Assembly.GetName().Name ?? "SLDataAPI";

    static UpdateChecker()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SLDataAPI-Updater");
    }

    public static void CheckAsync(Version currentVersion, bool install)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var resp = await Http.GetAsync(ReleasesApiUrl);
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Debug($"[SLDataAPI] 更新检查请求失败: HTTP {(int)resp.StatusCode}");
                    return;
                }

                var obj = JObject.Parse(await resp.Content.ReadAsStringAsync());
                string tag = obj["tag_name"]?.ToString() ?? "";
                if (!TryParseVersion(tag, out var remote) || remote <= currentVersion)
                {
                    Log.Debug("[SLDataAPI] 已是最新版本。");
                    return;
                }

                Log.Warn($"[SLDataAPI] 检测到新版本 v{remote}（当前 v{currentVersion}）。");
                if (!install)
                {
                    Log.Warn("[SLDataAPI] AutoUpdateInstall=false：仅提示，请前往 https://github.com/DNTOF/SLDataAPI/releases/latest 手动更新。");
                    return;
                }

                await InstallAsync(remote, obj);
            }
            catch (Exception ex)
            {
                // 网络不可达 / GitHub 限流 / 解析失败等，不影响插件本体功能
                Log.Debug($"[SLDataAPI] 更新检查失败（不影响正常运行）: {ex.Message}");
            }
        });
    }

    private static async Task InstallAsync(Version remote, JObject release)
    {
        string dllFile = AsmName + ".dll";
        string url = release["assets"]?.Children()
            .FirstOrDefault(a => string.Equals(a["name"]?.ToString(), dllFile, StringComparison.OrdinalIgnoreCase))
            ?["browser_download_url"]?.ToString();
        if (string.IsNullOrEmpty(url))
        {
            Log.Warn($"[SLDataAPI] Release 中未找到 {dllFile} 资产，放弃自动更新。");
            return;
        }

        byte[] data = await Http.GetByteArrayAsync(url);
        if (data.Length <= 0 || data.Length > MaxDllBytes)
        {
            Log.Warn($"[SLDataAPI] 下载文件大小异常（{data.Length} 字节），放弃自动更新。");
            return;
        }

        string pluginsDir = Paths.Plugins;
        if (string.IsNullOrEmpty(pluginsDir) || !Directory.Exists(pluginsDir))
        {
            Log.Warn("[SLDataAPI] 找不到插件目录（Paths.Plugins），放弃自动更新。");
            return;
        }

        string target = Path.Combine(pluginsDir, dllFile);
        string tmp = target + ".tmp";

        // ---- 安全校验：合法程序集 + 名称一致 + 签名一致 ----
        File.WriteAllBytes(tmp, data);
        AssemblyName newName;
        try
        {
            newName = AssemblyName.GetAssemblyName(tmp);
        }
        catch
        {
            Log.Warn("[SLDataAPI] 下载文件不是合法程序集，已删除并放弃自动更新。");
            SafeDelete(tmp);
            return;
        }

        if (!string.Equals(newName.Name, AsmName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"[SLDataAPI] 下载程序集名称 {newName.Name} 与自身不一致，已删除并放弃。");
            SafeDelete(tmp);
            return;
        }

        // 当前版本已强名称签名时，要求新文件签名一致（同一把私钥 = 同源可信，防篡改）
        byte[] curToken = typeof(UpdateChecker).Assembly.GetName().GetPublicKeyToken();
        byte[] newToken = newName.GetPublicKeyToken();
        if (curToken != null && curToken.Length > 0)
        {
            if (newToken == null || newToken.Length == 0 || !curToken.SequenceEqual(newToken))
            {
                Log.Warn("[SLDataAPI] 下载程序集强名称签名与当前版本不一致（可能被篡改），拒绝自动替换。");
                SafeDelete(tmp);
                return;
            }
        }

        // ---- 替换（EXILED 9 从内存加载，文件不占用）----
        try
        {
            if (File.Exists(target))
                File.Copy(target, target + ".bak", overwrite: true);   // 旧版备份，便于回滚
            File.Copy(tmp, target, overwrite: true);
            SafeDelete(tmp);
            Log.Warn(
                $"[SLDataAPI] 已自动更新到 v{remote}：{dllFile} 已替换，重启游戏服务器后生效。" +
                $"旧版备份为 {dllFile}.bak。");
        }
        catch (Exception ex)
        {
            Log.Warn($"[SLDataAPI] 自动替换失败（文件可能被占用）: {ex.Message}。已保留 {dllFile}.tmp，可手动替换。");
        }
    }

    /// <summary>容错解析版本号：支持 v2.1.0 / 2.1.0 / v2.1.0（YYMMDDHHmm）等标签格式。</summary>
    private static bool TryParseVersion(string tag, out Version version)
    {
        version = null;
        if (string.IsNullOrEmpty(tag)) return false;
        var m = Regex.Match(tag, @"(\d+)\.(\d+)\.(\d+)");
        return m.Success && Version.TryParse(m.Value, out version);
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
