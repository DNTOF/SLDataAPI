using System;
using CommandSystem;

namespace SLDataAPI.Commands;

/// <summary>sldataapi apikey …</summary>
public sealed class ApikeyParentCommand : ParentCommand
{
    public ApikeyParentCommand() => LoadGeneratedCommands();

    public override string Command => "apikey";
    public override string[] Aliases => new[] { "key", "keys" };
    public override string Description => "API Key 管理（create / revoke / list / confirmtest）";

    public override void LoadGeneratedCommands()
    {
        RegisterCommand(new ApikeyCreateCommand());
        RegisterCommand(new ApikeyRevokeCommand());
        RegisterCommand(new ApikeyListCommand());
        RegisterCommand(new ApikeyConfirmTestCommand());
    }

    protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        response = "用法:\n  sldataapi apikey create <id> <duty|admin> [note]\n  sldataapi apikey revoke <id>\n" +
                   "  sldataapi apikey list\n  sldataapi apikey confirmtest（自检确认窗口，不动任何 Key）";
        return false;
    }
}