using SLDataAPI.Control;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class ControlMessageValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void MissingOrBlankMessage_Fails(string? message)
    {
        Assert.False(ControlMessageValidation.TryValidateMessage(message, out string error));
        Assert.Equal("缺少 message 字段", error);
    }

    [Fact]
    public void MessageAtLimit_Ok()
    {
        string msg = new string('a', ControlMessageValidation.MaxMessageLength);
        Assert.True(ControlMessageValidation.TryValidateMessage(msg, out string error));
        Assert.Equal("", error);
    }

    [Fact]
    public void MessageTooLong_FailsWithChineseLength()
    {
        string msg = new string('a', ControlMessageValidation.MaxMessageLength + 1);
        Assert.False(ControlMessageValidation.TryValidateMessage(msg, out string error));
        Assert.Equal($"message 过长（{msg.Length} 字符，上限 {ControlMessageValidation.MaxMessageLength}）", error);
    }

    [Fact]
    public void NormalMessage_Ok()
    {
        Assert.True(ControlMessageValidation.TryValidateMessage("服务器将重启", out string error));
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(0f, 5f)]
    [InlineData(-1f, 5f)]
    [InlineData(5f, 5f)]
    [InlineData(10.5f, 10.5f)]
    [InlineData(60f, 60f)]
    [InlineData(61f, 60f)]
    [InlineData(999f, 60f)]
    public void DurationClamp_MatchesModerationMsg(float input, float expected)
    {
        Assert.Equal(expected, ControlMessageValidation.ClampDurationSeconds(input));
    }
}
