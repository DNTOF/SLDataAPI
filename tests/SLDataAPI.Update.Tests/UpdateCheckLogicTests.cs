using SLDataAPI;
using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Update.Tests;

public class UpdateCheckLogicTests
{
    [Fact]
    public void Config_Defaults_FollowStartupCheck_With72hInterval()
    {
        var cfg = new Config();
        Assert.True(cfg.AutoUpdateCheck);
        Assert.True(cfg.AutoUpdateInstall);
        Assert.Equal(72, cfg.AutoUpdateCheckIntervalHours);
        Assert.Equal(72, UpdateCheckLogic.DefaultIntervalHours);
        Assert.False(cfg.ApikeyCopyToClipboard);
        Assert.Equal("your_secret_token", cfg.VerifyToken);
    }

    [Fact]
    public void IsDue_NoLastCheck_IsDue()
    {
        Assert.True(UpdateCheckLogic.IsDue(null, DateTime.UtcNow, TimeSpan.FromHours(72)));
    }

    [Fact]
    public void IsDue_RecentCheck_NotDue()
    {
        var now = DateTime.UtcNow;
        Assert.False(UpdateCheckLogic.IsDue(now.AddHours(-1), now, TimeSpan.FromHours(72)));
    }

    [Fact]
    public void IsDue_OlderThanInterval_IsDue()
    {
        var now = DateTime.UtcNow;
        Assert.True(UpdateCheckLogic.IsDue(now.AddHours(-73), now, TimeSpan.FromHours(72)));
    }

    [Fact]
    public void IsDue_ZeroInterval_AlwaysDue()
    {
        var now = DateTime.UtcNow;
        Assert.True(UpdateCheckLogic.IsDue(now, now, TimeSpan.Zero));
        Assert.True(UpdateCheckLogic.IsDue(now.AddMinutes(-1), now, TimeSpan.FromHours(-1)));
    }

    [Fact]
    public void DelayUntilDue_ComputesRemainder()
    {
        var last = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var now = last.AddHours(24);
        Assert.Equal(TimeSpan.FromHours(48), UpdateCheckLogic.DelayUntilDue(last, now, TimeSpan.FromHours(72)));
        Assert.Equal(TimeSpan.Zero, UpdateCheckLogic.DelayUntilDue(last, last.AddHours(80), TimeSpan.FromHours(72)));
        Assert.Equal(TimeSpan.Zero, UpdateCheckLogic.DelayUntilDue(null, now, TimeSpan.FromHours(72)));
    }

    [Fact]
    public void IntervalFromHours_ClampsNonPositiveToZero()
    {
        Assert.Equal(TimeSpan.Zero, UpdateCheckLogic.IntervalFromHours(0));
        Assert.Equal(TimeSpan.Zero, UpdateCheckLogic.IntervalFromHours(-3));
        Assert.Equal(TimeSpan.FromHours(72), UpdateCheckLogic.IntervalFromHours(72));
    }

    [Fact]
    public void StateFile_Roundtrip_AndIsDueFromFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sldataapi-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = UpdateCheckLogic.StatePath(dir);
        try
        {
            Assert.Equal(Path.Combine(dir, "update_check_state.json"), path);
            Assert.True(UpdateCheckLogic.IsDueFromFile(path, DateTime.UtcNow, TimeSpan.FromHours(72)));

            var saved = new DateTime(2026, 9, 20, 8, 30, 0, DateTimeKind.Utc);
            Assert.True(UpdateCheckLogic.TrySaveLastCheckUtc(path, saved));
            DateTime? loaded = UpdateCheckLogic.LoadLastCheckUtc(path);
            Assert.NotNull(loaded);
            Assert.Equal(saved, loaded.Value);

            Assert.False(UpdateCheckLogic.IsDueFromFile(path, saved.AddHours(10), TimeSpan.FromHours(72)));
            Assert.True(UpdateCheckLogic.IsDueFromFile(path, saved.AddHours(73), TimeSpan.FromHours(72)));

            string text = File.ReadAllText(path);
            Assert.Contains("last_check_utc", text);
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 测试清理 */ }
        }
    }

    [Fact]
    public void TrySave_EmptyPath_FailsQuietly()
    {
        Assert.False(UpdateCheckLogic.TrySaveLastCheckUtc("", DateTime.UtcNow));
        Assert.Null(UpdateCheckLogic.LoadLastCheckUtc(""));
        Assert.Equal("", UpdateCheckLogic.StatePath(""));
    }

    [Theory]
    [InlineData("v2.6.1-beta")]
    [InlineData("2.6.0-rc1")]
    [InlineData("v2.6.0-preview.1")]
    [InlineData("2.6.0_dev")]
    public void IsPreReleaseTag_Detects(string tag)
    {
        Assert.True(UpdateCheckLogic.IsPreReleaseTag(tag));
        Assert.False(UpdateCheckLogic.TryParseVersion(tag, out _));
    }

    [Fact]
    public void TryParseVersion_StableTags()
    {
        Assert.True(UpdateCheckLogic.TryParseVersion("v2.6.0", out var v));
        Assert.Equal(new Version(2, 6, 0), v);
        Assert.False(UpdateCheckLogic.IsPreReleaseTag("v2.6.0"));
        Assert.False(UpdateCheckLogic.IsPreReleaseTag("premium-2.6.0"));
    }

    [Fact]
    public void Evaluate_NewerStable_UpdateAvailable()
    {
        string json = """{"tag_name":"v2.7.0","prerelease":false,"draft":false}""";
        var outcome = UpdateCheckLogic.EvaluateLatestRelease(json, new Version(2, 6, 0), out var remote, out string detail);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, outcome);
        Assert.Equal(new Version(2, 7, 0), remote);
        Assert.Equal("v2.7.0", detail);
    }

    [Fact]
    public void Evaluate_SameOrOlder_UpToDate()
    {
        string json = """{"tag_name":"v2.6.0","prerelease":false,"draft":false}""";
        Assert.Equal(UpdateCheckOutcome.UpToDate,
            UpdateCheckLogic.EvaluateLatestRelease(json, new Version(2, 6, 0), out _, out _));
    }

    [Fact]
    public void Evaluate_Prerelease_Skipped()
    {
        string json = """{"tag_name":"v2.7.0-beta","prerelease":true,"draft":false}""";
        Assert.Equal(UpdateCheckOutcome.PreReleaseSkipped,
            UpdateCheckLogic.EvaluateLatestRelease(json, new Version(2, 6, 0), out var remote, out string detail));
        Assert.Null(remote);
        Assert.Equal("v2.7.0-beta", detail);
    }

    [Fact]
    public void Evaluate_BadJson_Failed()
    {
        Assert.Equal(UpdateCheckOutcome.Failed,
            UpdateCheckLogic.EvaluateLatestRelease("not-json", new Version(2, 6, 0), out _, out string detail));
        Assert.Contains("JSON", detail);
    }

    [Fact]
    public void Evaluate_Empty_Failed()
    {
        Assert.Equal(UpdateCheckOutcome.Failed,
            UpdateCheckLogic.EvaluateLatestRelease("", new Version(2, 6, 0), out _, out string detail));
        Assert.Contains("空", detail);
    }

    [Fact]
    public void UnsignedCurrentAssembly_RefusesAutoInstall()
    {
        Assert.True(UpdateCheckLogic.ShouldRefuseAutoInstallBecauseUnsigned(null));
        Assert.True(UpdateCheckLogic.ShouldRefuseAutoInstallBecauseUnsigned(Array.Empty<byte>()));
        Assert.False(UpdateCheckLogic.IsSignedPublicKeyToken(null));
        Assert.Equal("3ec73bb20070fa9c", UpdateCheckLogic.ExpectedPublicKeyTokenHex);
    }

    [Fact]
    public void SignedTokens_MustMatch_ForInstall()
    {
        byte[] official = { 0x3e, 0xc7, 0x3b, 0xb2, 0x00, 0x70, 0xfa, 0x9c };
        byte[] other = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
        Assert.False(UpdateCheckLogic.ShouldRefuseAutoInstallBecauseUnsigned(official));
        Assert.True(UpdateCheckLogic.PublicKeyTokensMatch(official, official));
        Assert.False(UpdateCheckLogic.PublicKeyTokensMatch(official, other));
        Assert.False(UpdateCheckLogic.PublicKeyTokensMatch(official, null));
        Assert.False(UpdateCheckLogic.PublicKeyTokensMatch(official, Array.Empty<byte>()));
    }
}
