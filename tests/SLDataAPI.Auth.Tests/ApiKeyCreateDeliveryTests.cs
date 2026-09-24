using System;
using System.IO;
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
}
