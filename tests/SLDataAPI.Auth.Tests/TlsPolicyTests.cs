// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF

using System.Security.Cryptography;
using System.Text.Json;
using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class TlsPolicyTests
{
    [Theory]
    [InlineData(null, TlsMode.Optional, true)]
    [InlineData("", TlsMode.Optional, true)]
    [InlineData("optional", TlsMode.Optional, true)]
    [InlineData(" Optional ", TlsMode.Optional, true)]
    [InlineData("off", TlsMode.Off, true)]
    [InlineData("OFF", TlsMode.Off, true)]
    [InlineData("required", TlsMode.Required, true)]
    [InlineData("REQUIRED", TlsMode.Required, true)]
    [InlineData("bogus", TlsMode.Optional, false)]
    public void ParseMode(string? input, TlsMode expected, bool expectedValid)
    {
        var mode = TlsPolicy.ParseMode(input, out bool valid);
        Assert.Equal(expected, mode);
        Assert.Equal(expectedValid, valid);
    }

    [Theory]
    [InlineData(TlsMode.Off, "off")]
    [InlineData(TlsMode.Optional, "optional")]
    [InlineData(TlsMode.Required, "required")]
    public void ModeRoundTrips(TlsMode mode, string text)
    {
        Assert.Equal(text, TlsPolicy.ToConfigString(mode));
        Assert.Equal(mode, TlsPolicy.ParseMode(text, out _));
    }

    [Fact]
    public void FingerprintFormat_IsUppercaseColonSeparatedSha256()
    {
        byte[] der = { 1, 2, 3, 4, 5 };
        string fp = TlsPolicy.ComputeFingerprint(der);
        string expected = string.Join(":", SHA256.HashData(der).Select(b => b.ToString("X2")));
        Assert.Equal(expected, fp);
        Assert.Equal(32 * 3 - 1, fp.Length);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fp);
    }

    [Fact]
    public void FormatFingerprint_SmallInput()
    {
        Assert.Equal("00:0A:FF", TlsPolicy.FormatFingerprint(new byte[] { 0x00, 0x0A, 0xFF }));
    }

    [Theory]
    [InlineData(TlsMode.Off, false)]
    [InlineData(TlsMode.Optional, true)]
    [InlineData(TlsMode.Required, true)]
    public void ShouldSniff(TlsMode mode, bool expected) => Assert.Equal(expected, TlsPolicy.ShouldSniff(mode));

    [Theory]
    [InlineData(TlsMode.Optional, 0x16, true)]
    [InlineData(TlsMode.Required, 0x16, true)]
    [InlineData(TlsMode.Off, 0x16, false)]        // off：从不进入 TLS
    [InlineData(TlsMode.Optional, 'G', false)]    // "GET ..."
    [InlineData(TlsMode.Optional, 'P', false)]    // "POST ..."
    [InlineData(TlsMode.Optional, 0x15, false)]   // TLS alert 记录不是 ClientHello
    [InlineData(TlsMode.Optional, -1, false)]     // 超时
    [InlineData(TlsMode.Optional, -2, false)]     // 已关闭
    public void SniffDecision(TlsMode mode, int firstByte, bool expected)
        => Assert.Equal(expected, TlsPolicy.IsTlsClientHello(mode, firstByte));

    [Theory]
    [InlineData(TlsMode.Off, false, false)]
    [InlineData(TlsMode.Optional, false, false)]
    [InlineData(TlsMode.Optional, true, false)]
    [InlineData(TlsMode.Required, false, true)]
    [InlineData(TlsMode.Required, true, false)]
    public void PlaintextRejection(TlsMode mode, bool isTls, bool expected)
        => Assert.Equal(expected, TlsPolicy.IsPlaintextRejected(mode, isTls));

    [Fact]
    public void TlsRequiredEnvelope()
    {
        using var doc = JsonDocument.Parse(TlsPolicy.BuildTlsRequiredJson());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        Assert.Equal("tls_required", root.GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Single(root.GetProperty("data").EnumerateObject());
    }

    [Fact]
    public void TlsRequiredEnvelope_EscapesMessage()
    {
        using var doc = JsonDocument.Parse(TlsPolicy.BuildTlsRequiredJson("a \"quoted\" \\ msg\n\u0001"));
        Assert.Equal("a \"quoted\" \\ msg\n\u0001", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("tls_required", doc.RootElement.GetProperty("data").GetProperty("code").GetString());
    }
}
