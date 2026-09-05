using System.Collections.Generic;
using SLDataAPI.Auth;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class FingerprintTests
{
    [Fact]
    public void Fingerprint_IsDeterministic_AndPrefixed()
    {
        string a = EndpointAcl.Fingerprint("sld_live_abc");
        string b = EndpointAcl.Fingerprint("sld_live_abc");
        string c = EndpointAcl.Fingerprint("sld_live_abd");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("sha256:", a);
        Assert.Equal(7 + 64, a.Length);
    }

    [Fact]
    public void Fingerprint_Empty_IsStable()
    {
        Assert.Equal(EndpointAcl.Fingerprint(""), EndpointAcl.Fingerprint(string.Empty));
    }
}

public class EndpointGrantTests
{
    [Fact]
    public void BoolTrue_PermitsReadAndWrite()
    {
        var g = EndpointGrant.FromBool(true);
        Assert.True(g.Permits(false));
        Assert.True(g.Permits(true));
    }

    [Fact]
    public void ReadWrite_Split()
    {
        var g = EndpointGrant.FromReadWrite(read: true, write: false);
        Assert.True(g.Permits(wantWrite: false));
        Assert.False(g.Permits(wantWrite: true));
    }

    [Fact]
    public void TryParse_Object()
    {
        var raw = new Dictionary<string, object> { ["read"] = true, ["write"] = false };
        Assert.True(EndpointGrant.TryParse(raw, out var g));
        Assert.True(g.Permits(false));
        Assert.False(g.Permits(true));
    }
}

public class AclMatchTests
{
    [Fact]
    public void LongestPrefix_Wins()
    {
        var grants = new Dictionary<string, EndpointGrant>
        {
            ["/control/map/"] = EndpointGrant.FromReadWrite(true, false),
            ["/control/map/facility"] = EndpointGrant.FromBool(false),
        };
        Assert.True(EndpointAcl.IsAllowed(grants, "/control/map/layout", wantWrite: false));
        Assert.False(EndpointAcl.IsAllowed(grants, "/control/map/facility", wantWrite: true));
        Assert.False(EndpointAcl.IsAllowed(grants, "/control/map/facility", wantWrite: false));
    }

    [Fact]
    public void PrefixSlash_MatchesChildren()
    {
        var grants = new Dictionary<string, EndpointGrant>
        {
            ["/control/moderation/"] = EndpointGrant.FromBool(true),
        };
        Assert.True(EndpointAcl.IsAllowed(grants, "/control/moderation/kick", true));
        Assert.False(EndpointAcl.IsAllowed(grants, "/control/admin/teleport", true));
    }

    [Fact]
    public void ExactPath_WithoutSlash()
    {
        var grants = new Dictionary<string, EndpointGrant>
        {
            ["/control/logs"] = EndpointGrant.FromBool(true),
        };
        Assert.True(EndpointAcl.IsAllowed(grants, "/control/logs", false));
        Assert.False(EndpointAcl.IsAllowed(grants, "/control/logs/extra", false));
    }

    [Fact]
    public void Miss_Denies()
    {
        var grants = new Dictionary<string, EndpointGrant>();
        Assert.False(EndpointAcl.IsAllowed(grants, "/control/cassie", true));
    }
}

public class TemplateMergeTests
{
    [Fact]
    public void Duty_Default_AllowsMapRead_DeniesTeleport()
    {
        var g = EndpointAcl.MergeEffective("duty", null, null);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/map/layout", wantWrite: false));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/map/facility", wantWrite: true));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/admin/teleport", wantWrite: true));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/logs", wantWrite: false));
        Assert.True(EndpointAcl.IsAllowed(g, "ws:subscribe_events", wantWrite: false));
        Assert.False(EndpointAcl.IsAllowed(g, "voice:/ws", wantWrite: false));
    }

    [Fact]
    public void Admin_OpensCatalog()
    {
        var g = EndpointAcl.MergeEffective("admin", null, null);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/console/command", true));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/moderation/ban", true));
        Assert.True(EndpointAcl.IsAllowed(g, "voice:/ws", false));
    }

    [Fact]
    public void Override_CanDenyAdminTeleport()
    {
        var ov = new Dictionary<string, object>
        {
            ["/control/admin/"] = false,
        };
        var g = EndpointAcl.MergeEffective("admin", null, ov);
        Assert.False(EndpointAcl.IsAllowed(g, "/control/admin/teleport", true));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/moderation/kick", true));
    }

    [Fact]
    public void Override_CanOpenVoiceOnDuty()
    {
        var ov = new Dictionary<string, object> { ["voice:/ws"] = true };
        var g = EndpointAcl.MergeEffective("duty", null, ov);
        Assert.True(EndpointAcl.IsAllowed(g, "voice:/ws", false));
    }
}

public class WriteDetectionTests
{
    [Fact]
    public void MapLayout_IsRead()
    {
        Assert.False(EndpointAcl.IsWriteOperation("/control/map/layout", "{}"));
        Assert.True(EndpointAcl.IsWriteOperation("/control/map/facility", "{\"action\":\"doors\"}"));
    }

    [Fact]
    public void AdminState_DependsOnBody()
    {
        Assert.False(EndpointAcl.IsWriteOperation("/control/admin/state", "{\"target\":\"1\"}"));
        Assert.True(EndpointAcl.IsWriteOperation("/control/admin/state", "{\"target\":\"1\",\"godmode\":true}"));
    }

    [Fact]
    public void Reports_ListIsRead()
    {
        Assert.False(EndpointAcl.IsWriteOperation("/control/reports", "{\"action\":\"list\"}"));
        Assert.True(EndpointAcl.IsWriteOperation("/control/reports", "{\"action\":\"handle\",\"id\":\"x\"}"));
    }
}