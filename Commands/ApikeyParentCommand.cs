// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using CommandSystem;
using SLDataAPI.Auth;

namespace SLDataAPI.Commands;

/// <summary>sldataapi apikey …（API Key 管理；仅限服务器本地控制台）</summary>
public sealed class ApikeyParentCommand : ParentCommand
{
    public ApikeyParentCommand() => LoadGeneratedCommands();

    public override string Command => "apikey";
    public override string[] Aliases => new[] { "key", "keys" };
    public override string Description => "API Key 管理（create / revoke / list），仅限服务器本地控制台";

    public override void LoadGeneratedCommands()
    {
        RegisterCommand(new ApikeyCreateCommand());
        RegisterCommand(new ApikeyRevokeCommand());
        RegisterCommand(new ApikeyListCommand());
    }

    protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (ApikeyCommandGuard.Reject(sender, out response))
            return false;
        response = "用法:\n  sldataapi apikey create <id> <duty|admin> [note]\n  sldataapi apikey revoke <id>\n  sldataapi apikey list";
        return false;
    }
}

/// <summary>
/// apikey 命令的执行者校验：与 cert 对齐——RemoteAdmin（含游戏内 RA 面板）与远程控制通道一律拒绝，
/// 只接受 LocalAdmin / 服务器本地控制台（ServerConsoleSender）且不在远程执行上下文中。
/// </summary>
internal static class ApikeyCommandGuard
{
    public const string LocalOnlyMessage = ApikeyConsoleGuardLogic.LocalOnlyMessage;

    public static bool Reject(ICommandSender sender, out string response)
        => ApikeyConsoleGuardLogic.Reject(sender is ServerConsoleSender, out response);
}
