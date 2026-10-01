// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System.Collections.Generic;
using SLDataAPI.Auth;
using Xunit;

namespace SLDataAPI.Auth.Tests;

/// <summary>[内测] /control/adapted/ 与 /control/files/{stat,read_chunk,write_chunk} 的 ACL 与读写判定。</summary>
public class BetaAclTests
{
    private const string Action = "/control/adapted/dntof.sample_adapted/bump";

    [Fact]
    public void Catalog_AdaptedIsFalse()
    {
        Assert.False(EndpointAcl.DefaultCatalog["/control/adapted/"]);
        Assert.True(EndpointAcl.DutyDefaults.ContainsKey("/control/adapted/"));
    }

    [Fact]
    public void AdminDefault_DeniesAdaptedActions()
    {
        var g = EndpointAcl.MergeEffective("admin", null, null);
        Assert.False(EndpointAcl.IsAllowed(g, Action, wantWrite: true));
        Assert.False(EndpointAcl.IsAllowed(g, Action, wantWrite: false));
    }

    [Fact]
    public void AllControlTrue_DeniesAdaptedActions()
    {
        var tmpl = new Dictionary<string, object> { ["*"] = EndpointAcl.AllControlTrue };
        var g = EndpointAcl.MergeEffective("admin", tmpl, null);
        Assert.False(EndpointAcl.IsAllowed(g, Action, wantWrite: true));
        var g2 = EndpointAcl.MergeEffective("duty", tmpl, null);
        Assert.False(EndpointAcl.IsAllowed(g2, Action, wantWrite: true));
    }

    [Fact]
    public void Duty_DeniesAdaptedActions()
    {
        var g = EndpointAcl.MergeEffective("duty", null, null);
        Assert.False(EndpointAcl.IsAllowed(g, Action, wantWrite: true));
    }

    [Fact]
    public void Override_PerPlugin_AllowsOnlyThatPlugin()
    {
        var ov = new Dictionary<string, object> { ["/control/adapted/dntof.sample_adapted/"] = true };
        var g = EndpointAcl.MergeEffective("duty", null, ov);
        Assert.True(EndpointAcl.IsAllowed(g, Action, wantWrite: true));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/adapted/other.plugin/bump", wantWrite: true));
    }

    [Fact]
    public void Override_Global_AllowsAll()
    {
        var ov = new Dictionary<string, object> { ["/control/adapted/"] = true };
        var g = EndpointAcl.MergeEffective("admin", null, ov);
        Assert.True(EndpointAcl.IsAllowed(g, Action, wantWrite: true));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/adapted/other.plugin/x", wantWrite: true));
    }

    [Fact]
    public void Override_ExplicitDenyForOnePlugin_Wins()
    {
        var ov = new Dictionary<string, object>
        {
            ["/control/adapted/"] = true,
            ["/control/adapted/dntof.sample_adapted/"] = false,
        };
        var g = EndpointAcl.MergeEffective("admin", null, ov);
        Assert.False(EndpointAcl.IsAllowed(g, Action, wantWrite: true));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/adapted/other.plugin/x", wantWrite: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"action\":\"list\"}")]
    public void AdaptedAction_IsAlwaysWrite(string? body)
    {
        Assert.True(EndpointAcl.IsWriteOperation(Action, body));
    }

    [Fact]
    public void FileChunkEndpoints_ReadWriteClassification()
    {
        Assert.False(EndpointAcl.IsWriteOperation("/control/files/stat", "{\"path\":\"a.txt\"}"));
        Assert.False(EndpointAcl.IsWriteOperation("/control/files/read_chunk", "{\"path\":\"a.txt\"}"));
        Assert.True(EndpointAcl.IsWriteOperation("/control/files/write_chunk", "{\"path\":\"a.txt\"}"));
    }

    [Fact]
    public void FileChunkEndpoints_FollowFilesPrefixGrant()
    {
        var admin = EndpointAcl.MergeEffective("admin", null, null);
        Assert.False(EndpointAcl.IsAllowed(admin, "/control/files/read_chunk", wantWrite: false));
        Assert.False(EndpointAcl.IsAllowed(admin, "/control/files/write_chunk", wantWrite: true));

        var ov = new Dictionary<string, object> { ["/control/files/"] = new Dictionary<string, object> { ["read"] = true, ["write"] = false } };
        var g = EndpointAcl.MergeEffective("admin", null, ov);
        Assert.True(EndpointAcl.IsAllowed(g, "/control/files/stat", wantWrite: false));
        Assert.True(EndpointAcl.IsAllowed(g, "/control/files/read_chunk", wantWrite: false));
        Assert.False(EndpointAcl.IsAllowed(g, "/control/files/write_chunk", wantWrite: true));
    }
}
