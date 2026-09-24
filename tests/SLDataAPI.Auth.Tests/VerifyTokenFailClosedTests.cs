using SLDataAPI.Control;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class VerifyTokenFailClosedTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("your_secret_token")]
    [InlineData("password")]
    [InlineData("Short1!")]
    [InlineData("alllowercase1!")]
    [InlineData("ALLUPPERCASE1!")]
    [InlineData("NoSpecial123")]
    [InlineData("NoDigits!Abc")]
    public void WeakOrDefault_IsRejected(string? token)
    {
        Assert.False(ControlAuth.IsAcceptableVerifyToken(token));
        Assert.False(string.IsNullOrEmpty(ControlAuth.DescribeVerifyTokenRejection(token)));
    }

    [Fact]
    public void FactoryDefaultConstant_MatchesDocumented()
    {
        Assert.Equal("your_secret_token", ControlAuth.FactoryDefaultVerifyToken);
        Assert.False(ControlAuth.IsAcceptableVerifyToken(ControlAuth.FactoryDefaultVerifyToken));
        Assert.Contains("出厂默认", ControlAuth.DescribeVerifyTokenRejection(ControlAuth.FactoryDefaultVerifyToken));
    }

    [Theory]
    [InlineData("Abcd1234!")]
    [InlineData("MyVerify_Tok3n")]
    [InlineData("S3cret-Value")]
    public void StrongNonDefault_IsAccepted(string token)
    {
        Assert.True(ControlAuth.IsValidTokenFormat(token));
        Assert.True(ControlAuth.IsAcceptableVerifyToken(token));
        Assert.Equal("", ControlAuth.DescribeVerifyTokenRejection(token));
    }

    [Fact]
    public void ExtractDataPlaneToken_PrefersBearerThenAliasThenQuery()
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer HeaderTok3n!",
            ["X-SLDataAPI-Token"] = "AliasTok3n!",
        };
        Assert.Equal("HeaderTok3n!", ControlAuth.ExtractDataPlaneToken(headers, "token=QueryTok3n!"));

        headers.Remove("Authorization");
        Assert.Equal("AliasTok3n!", ControlAuth.ExtractDataPlaneToken(headers, "token=QueryTok3n!"));

        Assert.Equal("QueryTok3n!", ControlAuth.ExtractDataPlaneToken(new Dictionary<string, string>(), "token=QueryTok3n!"));
        Assert.True(ControlAuth.QueryHasTokenParam("foo=1&token=abc"));
        Assert.False(ControlAuth.QueryHasTokenParam("foo=1"));
    }
}
