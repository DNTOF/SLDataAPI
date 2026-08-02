using System;
using System.Net.Http;
using System.Threading.Tasks;
using Exiled.API.Features;
using Newtonsoft.Json.Linq;

/// <summary>
/// 启动时异步检查 GitHub Releases 是否有新版本，仅日志提示，不做任何自动下载/替换。
/// 失败（无网络、限流等）一律静默降级，不影响插件正常运行。
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/DNTOF/SLDataAPI/releases/latest";

    private static readonly HttpClient Http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    static UpdateChecker()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("SLDataAPI-UpdateChecker");
    }

    public static void CheckAsync(Version currentVersion)
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

                var text = await resp.Content.ReadAsStringAsync();
                var obj = JObject.Parse(text);
                string tag = obj["tag_name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(tag))
                    return;

                string cleaned = tag.TrimStart('v', 'V');
                if (Version.TryParse(cleaned, out var remote) && remote > currentVersion)
                {
                    Log.Warn(
                        $"[SLDataAPI] 检测到新版本 {remote}（当前运行 {currentVersion}）。" +
                        "前往 https://github.com/DNTOF/SLDataAPI/releases/latest 下载更新。");
                }
                else
                {
                    Log.Debug("[SLDataAPI] 已是最新版本。");
                }
            }
            catch (Exception ex)
            {
                // 网络不可达 / GitHub 限流等，不影响插件本体功能
                Log.Debug($"[SLDataAPI] 更新检查失败（不影响正常运行）: {ex.Message}");
            }
        });
    }
}
