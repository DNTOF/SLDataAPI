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

    [Fact]
    public void RemoteDenyMessage_DoesNotRequireLocalConfirm()
    {
        Assert.Contains("不允许通过远程控制通道执行", RemoteCommandGuard.RemoteDenyMessage);
        Assert.DoesNotContain("人工确认", RemoteCommandGuard.RemoteDenyMessage);
        Assert.DoesNotContain("确认窗口", RemoteCommandGuard.RemoteDenyMessage);
        Assert.DoesNotContain("确认面板", RemoteCommandGuard.RemoteDenyMessage);
    }

    [Fact]
    public void RejectIfRemote_Local_Allows()
    {
        Assert.False(RemoteCommandGuard.IsRemoteExecution);
        Assert.False(RemoteCommandGuard.RejectIfRemote(out string error));
        Assert.Equal("", error);
    }

    [Fact]
    public void RejectIfRemote_RemoteScope_DeniesKeyOps()
    {
        using (RemoteCommandGuard.RemoteExecutionScope.Enter())
        {
            Assert.True(RemoteCommandGuard.RejectIfRemote(out string error));
            Assert.Equal(RemoteCommandGuard.RemoteDenyMessage, error);
            Assert.Contains("不允许通过远程控制通道执行", error);
        }
        Assert.False(RemoteCommandGuard.RejectIfRemote(out _));
    }
}