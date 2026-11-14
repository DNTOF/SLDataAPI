// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Features.Tests;

/// <summary>
/// 错误信封 code 追加的字节级回归：只追加，不重新序列化既有字段。
/// 覆盖浮点数、\uXXXX、转义引号/反斜杠、Unicode、null 与字段顺序。
/// </summary>
public class ErrorCodeEnvelopeTests : System.IDisposable
{
    public ErrorCodeEnvelopeTests() => Localization.SetLanguage("zh-CN");

    public void Dispose() => Localization.SetLanguage("zh-CN");

    [Fact]
    public void AppendsCodeWithoutReformattingExistingJson()
    {
        const string original =
            "{\"success\":false,\"message\":\"缺少 action 字段\",\"data\":{\"ratio\":1.0,\"escaped\":\"a\\u0001\\\"b\\\\c\",\"zh\":\"中文\",\"nullv\":null}}";
        const string expected =
            "{\"success\":false,\"message\":\"缺少 action 字段\",\"data\":{\"ratio\":1.0,\"escaped\":\"a\\u0001\\\"b\\\\c\",\"zh\":\"中文\",\"nullv\":null},\"code\":\"invalid_argument\"}";

        Assert.Equal(expected, ErrorCodeEnvelope.EnsureErrorCode(400, original));
    }

    [Fact]
    public void ExistingDataCodeTakesPrecedence()
    {
        const string original =
            "{\"success\":false,\"message\":\"节日功能未开启\",\"data\":{\"code\":\"holiday_inactive\",\"feature\":\"candy:Evil\"}}";
        const string expected =
            "{\"success\":false,\"message\":\"节日功能未开启\",\"data\":{\"code\":\"holiday_inactive\",\"feature\":\"candy:Evil\"},\"code\":\"holiday_inactive\"}";

        Assert.Equal(expected, ErrorCodeEnvelope.EnsureErrorCode(409, original));
    }

    [Fact]
    public void SuccessAndExistingCodeAndNonErrorStatus_AreUnchanged()
    {
        const string success = "{\"success\":true,\"message\":\"ok\",\"data\":null}";
        Assert.Equal(success, ErrorCodeEnvelope.EnsureErrorCode(400, success));

        const string already = "{\"success\":false,\"message\":\"x\",\"data\":null,\"code\":\"forbidden\"}";
        Assert.Equal(already, ErrorCodeEnvelope.EnsureErrorCode(400, already));

        const string okStatus = "{\"success\":false,\"message\":\"x\",\"data\":null}";
        Assert.Equal(okStatus, ErrorCodeEnvelope.EnsureErrorCode(200, okStatus));
    }

    [Fact]
    public void EnglishMessage_UsesRecordedChineseMapping()
    {
        Localization.SetLanguage("en");
        string message = Localization.T("缺少 action 字段", "Missing action field");
        string original = "{\"success\":false,\"message\":\"" + message + "\",\"data\":null}";

        string result = ErrorCodeEnvelope.EnsureErrorCode(400, original);

        Assert.Equal("{\"success\":false,\"message\":\"" + message + "\",\"data\":null,\"code\":\"invalid_argument\"}", result);
    }

    [Fact]
    public void InvalidJson_IsReturnedUnchanged()
    {
        const string invalid = "not-json";
        Assert.Equal(invalid, ErrorCodeEnvelope.EnsureErrorCode(400, invalid));
    }
}
