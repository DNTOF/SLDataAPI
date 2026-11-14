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

    /// <summary>i18n / 机器码提示（控制台命令与 README 保持一致）。</summary>
    internal const string I18nHint =
        "输出语言：config.yml 的 language（zh-CN 默认 / en）。接口响应新增稳定 code 字段（role_code / team_code / phase_code / warhead_code / error.code）与 lang 字段。";

    public override void LoadGeneratedCommands()
    {
        RegisterCommand(new ApikeyParentCommand());
        RegisterCommand(new CertParentCommand());
    }

    protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "用法: sldataapi apikey <create|revoke|list> … | sldataapi cert <show|regen>（cert 仅限本地控制台）\n" + I18nHint;
        return false;
    }
}