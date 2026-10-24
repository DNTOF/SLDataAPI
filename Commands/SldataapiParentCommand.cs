// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using CommandSystem;
using RemoteAdmin;

namespace SLDataAPI.Commands;

/// <summary>根命令：sldataapi …（游戏控制台 / RemoteAdmin）。</summary>
[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class SldataapiParentCommand : ParentCommand
{
    public SldataapiParentCommand() => LoadGeneratedCommands();

    public override string Command => "sldataapi";
    public override string[] Aliases => new[] { "slda" };
    public override string Description =>
        "SLDataAPI 管理命令（API Key / TLS 证书）。远程 /control/console 已硬拒绝；若用权限插件收窄 RemoteAdmin，请同时拒绝 sldataapi / slda。";

    public override void LoadGeneratedCommands()
    {
        RegisterCommand(new ApikeyParentCommand());
        RegisterCommand(new CertParentCommand());
    }

    protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "用法: sldataapi apikey <create|revoke|list> … | sldataapi cert <show|regen>（cert 仅限本地控制台）";
        return false;
    }
}