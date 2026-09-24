using System;
using System.IO;
using System.Threading.Tasks;
using SLDataAPI.Auth;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class ApiKeyCreateDeliveryTests
{
    private const string SampleKey = "sld_live_UNITTEST_SECRET_KEY_VALUE_DO_NOT_ECHO";

    [Fact]
    public void FormatConsoleResponse_ContainsPath_ButNotPlaintextOrApiKeyBanner()
    {
        string path = Path.Combine(Path.GetTempPath(), "apikey_once_ops.txt");
        string response = ApiKeyCreateDelivery.FormatConsoleResponse(path);

        Assert.Contains(path, response);
        Assert.Contains(ApiKeyCreateDelivery.OperatorHint, response);
        Assert.DoesNotContain(SampleKey, response);
        Assert.DoesNotContain("api_key:", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SampleKey.Substring(0, 8), response);
    }

    [Fact]
    public void TryWriteOnceFile_WritesPlaintext_AndResponseOmitsIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sldataapi-once-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(ApiKeyCreateDelivery.TryWriteOnceFile(dir, "unit1", SampleKey, out string path, out string error));
            Assert.True(string.IsNullOrEmpty(error));
            Assert.Equal(Path.Combine(dir, "apikey_once_unit1.txt"), path);
            Assert.Equal(SampleKey, File.ReadAllText(path));

            string response = ApiKeyCreateDelivery.FormatConsoleResponse(path);
            Assert.DoesNotContain(SampleKey, response);
            Assert.DoesNotContain("api_key:", response, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(path, response);

            string key2 = "sld_duty_ANOTHER_SECRET_VALUE_456";
            Assert.True(ApiKeyCreateDelivery.TryWriteOnceFile(dir, "unit1", key2, out string path2, out _));
            Assert.Equal(path, path2);
            Assert.Equal(key2, File.ReadAllText(path2));
            Assert.DoesNotContain(key2, ApiKeyCreateDelivery.FormatConsoleResponse(path2));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理 */ }
        }
    }

    [Fact]
    public void TryWriteOnceFile_EmptyConfigDir_FailsWithoutLeakingKey()
    {
        Assert.False(ApiKeyCreateDelivery.TryWriteOnceFile("", "unit1", SampleKey, out _, out string error));
        Assert.DoesNotContain(SampleKey, error);
        Assert.Contains("配置目录", error);
    }

    [Fact]
    public void OnceFileName_SanitizesId_AndIsStableForSameId()
    {
        Assert.Equal("apikey_once_ops.txt", ApiKeyCreateDelivery.OnceFileName("ops"));
        Assert.Equal("apikey_once_ops.txt", ApiKeyCreateDelivery.OnceFileName(" ops "));
        Assert.StartsWith("apikey_once_", ApiKeyCreateDelivery.OnceFileName("a/b"));
        Assert.DoesNotContain("/", ApiKeyCreateDelivery.OnceFileName("a/b"));
        Assert.DoesNotContain(SampleKey, ApiKeyCreateDelivery.OnceFileName(SampleKey.Substring(0, 8)));
    }

    [Fact]
    public void FormatConsoleResponse_MentionsFiveMinuteAutoDelete()
    {
        string response = ApiKeyCreateDelivery.FormatConsoleResponse("/tmp/apikey_once_ops.txt");
        Assert.Contains("5 分钟", response);
        Assert.Contains("自动删除", response);
        Assert.Equal(TimeSpan.FromMinutes(5), ApiKeyCreateDelivery.AutoDeleteAfter);
        Assert.DoesNotContain(SampleKey, response);
    }

    [Fact]
    public void TryDeleteOnceFileIfPresent_DeletesExisting_AndNoopsIfGone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sldataapi-once-del-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "apikey_once_unit1.txt");
        try
        {
            File.WriteAllText(path, SampleKey);
            var deleted = ApiKeyCreateDelivery.TryDeleteOnceFileIfPresent(path);
            Assert.True(deleted.Existed);
            Assert.True(deleted.Deleted);
            Assert.False(File.Exists(path));
            Assert.DoesNotContain(SampleKey, deleted.FilePath);
            Assert.True(string.IsNullOrEmpty(deleted.Error));

            var gone = ApiKeyCreateDelivery.TryDeleteOnceFileIfPresent(path);
            Assert.False(gone.Existed);
            Assert.False(gone.Deleted);
            Assert.DoesNotContain(SampleKey, gone.Error ?? "");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理 */ }
        }
    }

    [Fact]
    public async Task ScheduleAutoDelete_TwoPaths_DoNotCancelEachOther()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sldataapi-once-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "apikey_once_a.txt");
        string b = Path.Combine(dir, "apikey_once_b.txt");
        File.WriteAllText(a, SampleKey + "_A");
        File.WriteAllText(b, SampleKey + "_B");

        var tcsA = new TaskCompletionSource<ApiKeyCreateDelivery.OnceFileDeleteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsB = new TaskCompletionSource<ApiKeyCreateDelivery.OnceFileDeleteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ApiKeyCreateDelivery.ScheduleAutoDelete(a, TimeSpan.FromMilliseconds(80), r => tcsA.TrySetResult(r));
            ApiKeyCreateDelivery.ScheduleAutoDelete(b, TimeSpan.FromMilliseconds(80), r => tcsB.TrySetResult(r));

            var finished = await Task.WhenAll(tcsA.Task, tcsB.Task).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(finished[0].Deleted);
            Assert.True(finished[1].Deleted);
            Assert.Equal(a, finished[0].FilePath);
            Assert.Equal(b, finished[1].FilePath);
            Assert.False(File.Exists(a));
            Assert.False(File.Exists(b));
            Assert.DoesNotContain(SampleKey, finished[0].Error ?? "");
            Assert.DoesNotContain(SampleKey, finished[1].Error ?? "");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理 */ }
        }
    }

    [Fact]
    public async Task ScheduleAutoDelete_AlreadyGone_DoesNothing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "apikey_once_missing_" + Guid.NewGuid().ToString("N") + ".txt");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ApiKeyCreateDelivery.ScheduleAutoDelete(missing, TimeSpan.FromMilliseconds(30), _ => tcs.TrySetResult(true));
        await Task.WhenAny(tcs.Task, Task.Delay(400));
        Assert.False(tcs.Task.IsCompleted);
        Assert.False(File.Exists(missing));
    }
}
