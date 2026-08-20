using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SLDataAPI.Services;

/// <summary>
/// 启动时异步检查 GitHub Releases 是否有新版本。
/// install=true 时自动下载并替换插件 DLL（LabAPI 从文件字节加载插件、不锁定文件，
/// 覆盖后下次重启服务器生效）；install=false 时仅日志提示。
/// 稳定版策略：自动更新只接受稳定版——GitHub 的 prerelease/draft 标记（权威）
/// 与 tag 语义识别（beta/alpha/rc/preview/dev 等）双保险，预发布版本一律跳过。
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

                // 稳定版策略（用户要求）：预发布版本一律不自动下载。
                // 双保险：GitHub 元数据（prerelease/draft 复选框，权威）+ tag 语义识别
                //（防发布时忘勾 prerelease 复选框就把 beta tag 当稳定版推出）。
                if (obj["prerelease"]?.Value<bool>() == true || obj["draft"]?.Value<bool>() == true ||
                    IsPreReleaseTag(tag))
                {
                    Log.Debug($"[SLDataAPI] 最新 Release 为预发布版本（{tag}），按稳定版策略跳过自动更新。");
                    return;
                }

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
        string? url = release["assets"]?.Children()
            .FirstOrDefault(a => string.Equals(a["name"]?.ToString(), dllFile, StringComparison.OrdinalIgnoreCase))
            ?["browser_download_url"]?.ToString();
        if (string.IsNullOrEmpty(url))
        {
            Log.Warn($"[SLDataAPI] Release 中未找到 {dllFile} 资产，放弃自动更新。");
            return;
        }

        // X-02：流式下载并限长——GetByteArrayAsync 会先把整个 asset 读进内存（GitHub 单文件
        // 上限约 2GB），被篡改的巨大文件会在启动期触发 GB 级分配甚至 OOM；改为边读边计数，
        // 超过 MaxDllBytes 立即中断
        byte[] data;
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"[SLDataAPI] 下载失败: HTTP {(int)resp.StatusCode}，放弃自动更新。");
                return;
            }
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            long total = 0;
            while (true)
            {
                int n = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (n <= 0) break;
                total += n;
                if (total > MaxDllBytes)
                {
                    Log.Warn($"[SLDataAPI] 下载文件超过上限 {MaxDllBytes} 字节（已读取 {total}），放弃自动更新。");
                    return;
                }
                ms.Write(buffer, 0, n);
            }
            data = ms.ToArray();
        }
        if (data.Length <= 0)
        {
            Log.Warn("[SLDataAPI] 下载文件为空，放弃自动更新。");
            return;
        }

        // 目标目录取自身 DLL 所在位置（LabAPI 的 FilePath；程序集是字节加载的，
        // Assembly.Location 为空字符串，不能用）
        string? selfPath = Plugin.Instance?.FilePath;
        if (string.IsNullOrWhiteSpace(selfPath) || !File.Exists(selfPath))
        {
            Log.Warn("[SLDataAPI] 找不到自身 DLL 路径（LabAPI FilePath），放弃自动更新。");
            return;
        }

        string pluginsDir = Path.GetDirectoryName(selfPath);
        if (string.IsNullOrEmpty(pluginsDir) || !Directory.Exists(pluginsDir))
        {
            Log.Warn("[SLDataAPI] 插件目录不可用，放弃自动更新。");
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

        // ---- 替换（LabAPI 从字节加载插件，文件不占用）----
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

    // 预发布标识段：段内只允许"标识词 + 数字后缀"（如 beta / rc1 / alpha2），
    // 避免 premium 这类含 pre 前缀的正常单词被误判（Y-02：旧实现的 \b 在非逐字
    // 字符串里是退格符，分支永远匹配不到，仅靠 -rc 兜底属侥幸正确）
    private static readonly Regex PreReleaseSegmentRegex = new Regex(
        @"^(?:beta|alpha|preview|pre|prerelease|rc|dev|nightly|canary|snapshot)\d*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>tag 是否为预发布版本：按 -_. 分段，任一段命中预发布标识词即判定。</summary>
    private static bool IsPreReleaseTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        foreach (string seg in tag.Split('-', '_', '.'))
        {
            if (PreReleaseSegmentRegex.IsMatch(seg))
                return true;
        }
        return false;
    }

    /// <summary>容错解析版本号：支持 v2.1.0 / 2.1.0 / v2.1.0（YYMMDDHHmm）等标签格式。
    /// 预发布 tag（beta/alpha/rc/preview/dev 等）不解析——自动更新只接受稳定版。</summary>
    private static bool TryParseVersion(string tag, out Version version)
    {
        version = null!;
        if (string.IsNullOrEmpty(tag)) return false;
        if (IsPreReleaseTag(tag)) return false;
        var m = Regex.Match(tag, @"(\d+)\.(\d+)\.(\d+)");
        return m.Success && Version.TryParse(m.Value, out version);
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
