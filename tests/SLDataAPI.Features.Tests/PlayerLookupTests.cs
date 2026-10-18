// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using SLDataAPI.Control;
using Xunit;

namespace SLDataAPI.Features.Tests;

public class PlayerLookupTests
{
    private sealed record P(int Id, string UserId, string Ip, string Nick);

    private static readonly P[] Players =
    {
        new(2, "76561198000000001@steam", "203.0.113.7", "Alice"),
        new(3, "76561198000000002@steam", "198.51.100.9", "Bob"),
        new(4, "ID_Dummy", "", "203.0.113.7"), // 昵称恰好等于他人 IP
    };

    private static P? Find(string t, bool allowIp) =>
        PlayerLookup.Find(t, Players, p => p.Id, p => p.UserId, p => p.Ip, p => p.Nick, allowIp);

    [Fact]
    public void ById_ThenUserId_ThenNickname()
    {
        Assert.Equal(3, Find("3", false)!.Id);
        Assert.Equal(2, Find("76561198000000001@STEAM", false)!.Id);
        Assert.Equal(3, Find("bob", false)!.Id);
    }

    [Fact]
    public void NoIp_IpTargetDoesNotResolveByIp()
    {
        Assert.Null(Find("198.51.100.9", allowIp: false));
        Assert.Equal(3, Find("198.51.100.9", allowIp: true)!.Id);
    }

    [Fact]
    public void NoIp_NicknameEqualToOthersIp_ResolvesNickname()
    {
        // 不按 IP 时，"203.0.113.7" 只能匹配到昵称为该串的 4 号，绝不会命中 IP 为该串的 2 号
        Assert.Equal(4, Find("203.0.113.7", allowIp: false)!.Id);
        Assert.Equal(2, Find("203.0.113.7", allowIp: true)!.Id);
    }

    [Fact]
    public void BlankOrUnknown_Null()
    {
        Assert.Null(Find("", false));
        Assert.Null(Find("nobody", false));
        Assert.Null(Find("99", false));
    }
}
