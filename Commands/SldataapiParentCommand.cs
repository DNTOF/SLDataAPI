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
    public override string Description => "SLDataAPI 管理命令（API Key 等）";

    public override void LoadGeneratedCommands()
    {
        RegisterCommand(new ApikeyParentCommand());
    }

    protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "用法: sldataapi apikey <create|revoke|list> …";
        return false;
    }
}