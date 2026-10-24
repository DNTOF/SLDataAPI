// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using CommandSystem;
using SLDataAPI.Auth;

namespace SLDataAPI.Commands;

/// <summary>sldataapi cert …（自签 TLS 证书；仅限服务器本地控制台）</summary>
public sealed class CertParentCommand : ParentCommand
{
    public CertParentCommand() => LoadGeneratedCommands();

    public override string Command => "cert";
    public override string[] Aliases => new[] { "tls" };
    public override string Description => "自签 TLS 证书（show / regen），仅限服务器本地控制台";

    public override void LoadGeneratedCommands()
    {
        RegisterCommand(new CertShowCommand());
        RegisterCommand(new CertRegenCommand());
    }

    protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (CertCommandGuard.Reject(sender, out response))
            return false;
        response = "用法:\n  sldataapi cert show\n  sldataapi cert regen";
        return false;
    }
}

/// <summary>
/// cert 命令的执行者校验：比 apikey 更严格——RemoteAdmin（含游戏内 RA 面板）与远程控制通道一律拒绝，
/// 只接受 LocalAdmin / 服务器本地控制台（ServerConsoleSender）且不在远程执行上下文中。
/// </summary>
internal static class CertCommandGuard
{
    public const string LocalOnlyMessage =
        "拒绝执行：sldataapi cert 仅限服务器本地控制台（LocalAdmin）使用，RemoteAdmin 与远程控制通道均不可用。";

    public static bool Reject(ICommandSender sender, out string response)
    {
        if (RemoteCommandGuard.RejectIfRemote(out string remoteErr))
        {
            response = remoteErr;
            return true;
        }
        if (sender is not ServerConsoleSender)
        {
            response = LocalOnlyMessage;
            return true;
        }
        response = "";
        return false;
    }
}
