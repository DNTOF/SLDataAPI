// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System;
using System.Diagnostics;
using System.IO;
using SLDataAPI.Services;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public sealed class FileSandboxTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly string _outside;

    public FileSandboxTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "sld_sbx_" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "sub", "a.txt"), "hi");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "nope");
    }

    public void Dispose()
    {
        // 先删链接本身（Directory.Delete / File.Delete 作用于链接，不跟随目标），再删目录树
        try { if (Directory.Exists(Path.Combine(_root, "link"))) Directory.Delete(Path.Combine(_root, "link")); } catch { /* ignore */ }
        try { File.Delete(Path.Combine(_root, "flink.txt")); } catch { /* ignore */ }
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Resolve_InsideRoot_Ok()
    {
        string full = FileSandbox.Resolve(_root, "sub/a.txt");
        Assert.Equal(Path.Combine(_root, "sub", "a.txt"), full);
        Assert.Equal(Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar), FileSandbox.Resolve(_root, "").TrimEnd(Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("sub/../../outside/secret.txt")]
    [InlineData("..\\outside\\secret.txt")]
    public void Resolve_Traversal_Rejected(string rel)
    {
        Assert.Throws<ArgumentException>(() => FileSandbox.Resolve(_root, rel));
    }

    [Fact]
    public void Resolve_Colon_Rejected()
    {
        Assert.Throws<ArgumentException>(() => FileSandbox.Resolve(_root, "sub/a.txt:ads"));
    }

    [Fact]
    public void Resolve_EmptyRoot_Disabled()
    {
        Assert.Throws<InvalidOperationException>(() => FileSandbox.Resolve("", "a.txt"));
    }

    [Fact]
    public void Resolve_NonexistentChild_Ok()
    {
        string full = FileSandbox.Resolve(_root, "sub/new/deeper.txt");
        Assert.EndsWith("deeper.txt", full);
    }

    [Fact]
    public void IsInside_PrefixSibling_NotInside()
    {
        string r = Path.Combine(_base, "root");
        Assert.False(FileSandbox.IsInside(r, r + "2" + Path.DirectorySeparatorChar + "x"));
        Assert.True(FileSandbox.IsInside(r, Path.Combine(r, "x")));
    }

    [Fact]
    public void Resolve_ThroughDirectoryLink_Rejected()
    {
        string link = Path.Combine(_root, "link");
        if (!TryCreateDirLink(link, _outside)) return; // 环境不允许建链接：跳过
        var ex = Assert.Throws<InvalidOperationException>(() => FileSandbox.Resolve(_root, "link/secret.txt"));
        Assert.Contains("junction", ex.Message);
        Assert.Throws<InvalidOperationException>(() => FileSandbox.Resolve(_root, "link"));
    }

    [Fact]
    public void Resolve_FileSymlink_Rejected()
    {
        string link = Path.Combine(_root, "flink.txt");
        try { File.CreateSymbolicLink(link, Path.Combine(_outside, "secret.txt")); }
        catch { return; } // 无 symlink 权限（Windows 非开发者模式）：跳过
        Assert.Throws<InvalidOperationException>(() => FileSandbox.Resolve(_root, "flink.txt"));
    }

    [Fact]
    public void Resolve_LinkCheckCanBeDisabled()
    {
        string link = Path.Combine(_root, "link");
        if (!TryCreateDirLink(link, _outside)) return;
        // 关闭链接检查时只做字符串层面的越界判定（旧行为）
        string full = FileSandbox.Resolve(_root, "link/secret.txt", rejectReparsePoints: false);
        Assert.EndsWith("secret.txt", full);
    }

    private static bool TryCreateDirLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch { /* 可能无 symlink 权限，Windows 再试 junction */ }

        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}
