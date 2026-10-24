// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using SLDataAPI.Auth;
using Xunit;

namespace SLDataAPI.Auth.Tests;

public class ApikeyConsoleGuardLogicTests
{
    [Fact]
    public void LocalConsoleSender_Allows()
    {
        Assert.False(ApikeyConsoleGuardLogic.Reject(isServerConsoleSender: true, out string response));
        Assert.Equal("", response);
    }

    [Fact]
    public void NonConsoleSender_Denies_WithLocalOnlyMessage()
    {
        Assert.True(ApikeyConsoleGuardLogic.Reject(isServerConsoleSender: false, out string response));
        Assert.Equal(ApikeyConsoleGuardLogic.LocalOnlyMessage, response);
        Assert.Contains("仅限服务器本地控制台", response);
        Assert.Contains("RemoteAdmin", response);
    }

    [Fact]
    public void RemoteScope_Denies_EvenForConsoleSender()
    {
        using (RemoteCommandGuard.RemoteExecutionScope.Enter())
        {
            Assert.True(ApikeyConsoleGuardLogic.Reject(isServerConsoleSender: true, out string response));
            Assert.Equal(RemoteCommandGuard.RemoteDenyMessage, response);
        }
        Assert.False(ApikeyConsoleGuardLogic.Reject(isServerConsoleSender: true, out _));
    }

    [Fact]
    public void RemoteScope_TakesPrecedence_OverNonConsoleSender()
    {
        using (RemoteCommandGuard.RemoteExecutionScope.Enter())
        {
            Assert.True(ApikeyConsoleGuardLogic.Reject(isServerConsoleSender: false, out string response));
            Assert.Equal(RemoteCommandGuard.RemoteDenyMessage, response);
            Assert.NotEqual(ApikeyConsoleGuardLogic.LocalOnlyMessage, response);
        }
    }
}
