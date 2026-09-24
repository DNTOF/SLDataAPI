using SLDataAPI.Control;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class WsCallGateTests
{
    [Fact]
    public void AclFailure_MustExit_OrSessionSticksAt429()
    {
        int pending = 0;
        const int max = WsCallGate.DefaultMaxConcurrent;

        for (int i = 0; i < max; i++)
        {
            Assert.True(WsCallGate.TryEnter(ref pending, max));
            // 403 路径：必须 Exit，与 HandleCall ACL 失败一致
            WsCallGate.Exit(ref pending);
        }

        Assert.Equal(0, pending);
        Assert.True(WsCallGate.TryEnter(ref pending, max));
        WsCallGate.Exit(ref pending);
    }

    [Fact]
    public void MissingExitOn403_WouldPermanently429()
    {
        int pending = 0;
        const int max = 4;
        for (int i = 0; i < max; i++)
            Assert.True(WsCallGate.TryEnter(ref pending, max));

        Assert.False(WsCallGate.TryEnter(ref pending, max));
        Assert.Equal(max, pending);

        // 补做 403 本应做的 Exit 后恢复
        for (int i = 0; i < max; i++)
            WsCallGate.Exit(ref pending);
        Assert.Equal(0, pending);
        Assert.True(WsCallGate.TryEnter(ref pending, max));
    }
}
