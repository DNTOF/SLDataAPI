// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System;
using System.Text;
using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class FileUploadSessionsTests
{
    private DateTime _now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private FileUploadSessions New(int max = 1024) => new(max, () => _now);

    [Fact]
    public void Contiguous_Append_And_Complete()
    {
        var s = New();
        string id = s.Begin("k1", "dir/a.txt");
        Assert.StartsWith("up_", id);
        Assert.Equal(3, s.Append(id, "k1", "dir/a.txt", 0, Encoding.UTF8.GetBytes("abc")));
        Assert.Equal(6, s.Append(id, "k1", "/dir/a.txt", 3, Encoding.UTF8.GetBytes("def")));
        byte[] all = s.Complete(id, "k1", "dir\\a.txt", FileUploadSessions.Sha256Hex(Encoding.UTF8.GetBytes("abcdef")));
        Assert.Equal("abcdef", Encoding.UTF8.GetString(all));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void NonContiguous_Offset_Rejected()
    {
        var s = New();
        string id = s.Begin("k1", "a.txt");
        s.Append(id, "k1", "a.txt", 0, new byte[4]);
        Assert.Throws<ArgumentException>(() => s.Append(id, "k1", "a.txt", 2, new byte[4]));
        Assert.Throws<ArgumentException>(() => s.Append(id, "k1", "a.txt", 8, new byte[4]));
    }

    [Fact]
    public void SizeCap_KillsSession()
    {
        var s = New(max: 10);
        string id = s.Begin("k1", "a.txt");
        s.Append(id, "k1", "a.txt", 0, new byte[8]);
        Assert.Throws<InvalidOperationException>(() => s.Append(id, "k1", "a.txt", 8, new byte[3]));
        Assert.Throws<ArgumentException>(() => s.Append(id, "k1", "a.txt", 8, new byte[1]));
    }

    [Fact]
    public void Session_BoundToActorAndPath()
    {
        var s = New();
        string id = s.Begin("k1", "a.txt");
        Assert.Throws<ArgumentException>(() => s.Append(id, "k2", "a.txt", 0, new byte[1]));
        Assert.Throws<ArgumentException>(() => s.Append(id, "k1", "b.txt", 0, new byte[1]));
        Assert.False(s.Abort(id, "k2"));
        Assert.True(s.Abort(id, "k1"));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void ShaMismatch_Throws_AndDropsSession()
    {
        var s = New();
        string id = s.Begin("k1", "a.txt");
        s.Append(id, "k1", "a.txt", 0, new byte[] { 1, 2, 3 });
        Assert.Throws<InvalidOperationException>(() => s.Complete(id, "k1", "a.txt", new string('0', 64)));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Ttl_ExpiresIdleSessions()
    {
        var s = New();
        string id = s.Begin("k1", "a.txt");
        _now = _now.AddSeconds(60);
        s.Append(id, "k1", "a.txt", 0, new byte[1]); // 续命
        _now = _now.AddSeconds(100);
        Assert.Equal(1, s.Count);
        _now = _now.AddSeconds(30);
        Assert.Equal(0, s.Count);
        Assert.Throws<ArgumentException>(() => s.Append(id, "k1", "a.txt", 1, new byte[1]));
    }

    [Fact]
    public void Limits_PerActor_And_Global()
    {
        var s = New();
        s.Begin("k1", "a"); s.Begin("k1", "b");
        Assert.Throws<InvalidOperationException>(() => s.Begin("k1", "c"));
        for (int i = 0; i < FileUploadSessions.MaxSessions - 2; i += 2)
        {
            s.Begin("x" + i, "a"); s.Begin("x" + i, "b");
        }
        Assert.Equal(FileUploadSessions.MaxSessions, s.Count);
        Assert.Throws<InvalidOperationException>(() => s.Begin("fresh", "a"));
        s.Clear();
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Sha256Hex_KnownVector()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            FileUploadSessions.Sha256Hex(Encoding.ASCII.GetBytes("abc")));
    }
}
