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
        Assert.False(EndpointAcl.IsAllowed(g, "/control/audit/list", wantWrite: false));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/audit/list", wantWrite: true));
    }

    [Fact]
    public void Duty_Default_DoesNotGrantAuditList()
    {
        Assert.True(EndpointAcl.DutyDefaults.TryGetValue("/control/audit/list", out var grant));
        Assert.False(grant.Permits(false));
        Assert.False(grant.Permits(true));
    }

    [Fact]
    public void Admin_StillGrantsAuditList_FromCatalog()
    {
        var g = EndpointAcl.MergeEffective("admin", null, null);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/audit/list", wantWrite: false));
    }

    [Fact]
    public void Duty_Override_CanOpenAuditList()
    {
        var ov = new Dictionary<string, object> { ["/control/audit/list"] = true };
        var g = EndpointAcl.MergeEffective("duty", null, ov);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/audit/list", wantWrite: false));
    }

    [Fact]
    public void Admin_OpensCatalog()
    {
        var g = EndpointAcl.MergeEffective("admin", null, null);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/moderation/ban", true));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/admin/teleport", true));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/broadcast", true));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/staffchat", true));
        Assert.True(EndpointAcl.IsAllowed(g, "voice:/ws", false));
    }

    [Fact]
    public void Duty_Default_DeniesBroadcastAndStaffChat()
    {
        var g = EndpointAcl.MergeEffective("duty", null, null);
        Assert.False(EndpointAcl.IsAllowed(g, "/control/broadcast", true));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/broadcast", false));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/staffchat", true));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/staffchat", false));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/cassie", true));
    }

    [Fact]
    public void Catalog_BroadcastAndStaffChat_AdminTrue_DutyFalse()
    {
        Assert.True(EndpointAcl.DefaultCatalog["/control/broadcast"]);
        Assert.True(EndpointAcl.DefaultCatalog["/control/staffchat"]);
        Assert.True(EndpointAcl.DutyDefaults.TryGetValue("/control/broadcast", out var bc));
        Assert.True(EndpointAcl.DutyDefaults.TryGetValue("/control/staffchat", out var sc));
        Assert.False(bc.Permits(true));
        Assert.False(sc.Permits(true));
    }

    [Fact]
    public void Admin_RespectsCatalogFalse()
    {
        var g = EndpointAcl.MergeEffective("admin", null, null);
        Assert.False(EndpointAcl.IsAllowed(g, "/control/console/command", true));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/console/command", false));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/plugins", true));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/files/read", false));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/player/inventory", true));
    }

    [Fact]
    public void Admin_ExplicitTemplateAllControlTrue_RespectsCatalogFalse()
    {
        var tmpl = new Dictionary<string, object> { [""] = EndpointAcl.AllControlTrue };
        var g = EndpointAcl.MergeEffective("admin", tmpl, null);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/moderation/ban", true));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/console/command", true));
    }

    [Fact]
    public void Duty_AllControlTrue_RespectsCatalogFalse()
    {
        var tmpl = new Dictionary<string, object> { ["*"] = EndpointAcl.AllControlTrue };
        var g = EndpointAcl.MergeEffective("duty", tmpl, null);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/moderation/ban", true));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/console/command", true));
    }

    [Fact]
    public void Admin_CatalogFalse_StillGrantableViaOverride()
    {
        var ov = new Dictionary<string, object> { ["/control/console/"] = true };
        var g = EndpointAcl.MergeEffective("admin", null, ov);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/console/command", true));
    }

    [Fact]
    public void Admin_UsesSuppliedCatalogValues()
    {
        var catalog = new Dictionary<string, bool>
        {
            ["/control/round"] = true,
            ["/control/console/"] = true,
            ["/control/cassie"] = false,
        };
        var g = EndpointAcl.MergeEffective("admin", null, null, catalog);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/round", true));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/console/command", true));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/cassie", true));
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

    [Fact]
    public void BroadcastAndStaffChat_AreWrites()
    {
        Assert.True(EndpointAcl.IsWriteOperation("/control/broadcast", "{\"message\":\"hi\"}"));
        Assert.True(EndpointAcl.IsWriteOperation("/control/staffchat", "{\"message\":\"hi\"}"));
    }
}
