using SLDataAPI.Auth;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class RemoteCommandGuardTests
{
    [Theory]
    [InlineData("sldataapi apikey create foo admin")]
    [InlineData("SLDataAPI APIKEY CREATE foo admin")]
    [InlineData("slda apikey create foo admin")]
    [InlineData("SLDA key create foo admin")]
    [InlineData(".sldataapi apikey create foo admin")]
    [InlineData("..slda apikey revoke foo")]
    [InlineData("   \t.sldataapi  apikey  list")]
    [InlineData("/sldataapi apikey list")]
    [InlineData("\"slda\" apikey list")]
    [InlineData("sldataapi")]
    [InlineData("slda apikey revoke foo")]
    [InlineData("slda apikey list")]
    [InlineData("help sldataapi")]
    public void ManagementCommands_AreBlocked(string command)
    {
        Assert.True(RemoteCommandGuard.IsManagementCommand(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("bc 5 hello")]
    [InlineData(".m fetch https://example.com/slda/list.yml")]
    [InlineData("sldataapix apikey list")]
    [InlineData("myslda apikey list")]
    [InlineData("reload configs")]
    public void OtherCommands_AreAllowed(string? command)
    {
        Assert.False(RemoteCommandGuard.IsManagementCommand(command));
    }

    [Fact]
    public void RemoteExecutionScope_TracksNesting()
    {
        Assert.False(RemoteCommandGuard.IsRemoteExecution);
        using (RemoteCommandGuard.RemoteExecutionScope.Enter())
        {
            Assert.True(RemoteCommandGuard.IsRemoteExecution);
            using (RemoteCommandGuard.RemoteExecutionScope.Enter())
                Assert.True(RemoteCommandGuard.IsRemoteExecution);
            Assert.True(RemoteCommandGuard.IsRemoteExecution);
        }
        Assert.False(RemoteCommandGuard.IsRemoteExecution);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData(" yes ")]
    [InlineData("YES")]
    public void Affirmative_Answers(string answer)
    {
        Assert.True(RemoteCommandGuard.IsAffirmative(answer));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("yeah")]
    [InlineData("ok")]
    [InlineData("1")]
    public void NonAffirmative_Answers(string? answer)
    {
        Assert.False(RemoteCommandGuard.IsAffirmative(answer));
    }
}