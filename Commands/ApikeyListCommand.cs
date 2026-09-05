using System;
using System.Text;
using CommandSystem;
using SLDataAPI.Auth;

namespace SLDataAPI.Commands;

public sealed class ApikeyListCommand : ICommand
{
    public string Command => "list";
    public string[] Aliases => new[] { "ls" };
    public string Description => "列出 API Key（无明文）";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        var list = ApiKeyService.List();
        if (list.Count == 0)
        {
            response = "当前没有 API Key。用 sldataapi apikey create <id> <duty|admin> 创建。";
            return true;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"共 {list.Count} 把 Key：");
        foreach (var k in list)
        {
            sb.AppendLine($"- id={k.Id}  template={k.Template}  created={k.CreatedAt}  note={k.Note}");
        }
        response = sb.ToString();
        return true;
    }
}