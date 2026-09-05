using System;
using System.Text;
using CommandSystem;
using SLDataAPI.Auth;

namespace SLDataAPI.Commands;

public sealed class ApikeyCreateCommand : ICommand, IUsageProvider
{
    public string Command => "create";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "创建 API Key（明文只显示一次）";
    public string[] Usage => new[] { "<id> <duty|admin> [note]" };

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (arguments.Count < 2)
        {
            response = "用法: sldataapi apikey create <id> <duty|admin> [note]\n关闭窗口后无法再查看明文，请立即保存。";
            return false;
        }

        string id = arguments.Array![arguments.Offset];
        string template = arguments.Array![arguments.Offset + 1];
        string note = arguments.Count >= 3
            ? string.Join(" ", arguments.Array!, arguments.Offset + 2, arguments.Count - 2)
            : "";

        if (!ApiKeyService.TryCreate(id, template, note, out string plaintext, out string error))
        {
            response = "创建失败: " + error;
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== API Key 已创建（明文只显示这一次，请立即保存）===");
        sb.AppendLine($"id:        {id}");
        sb.AppendLine($"template:  {template.Trim().ToLowerInvariant()}");
        if (!string.IsNullOrEmpty(note)) sb.AppendLine($"note:      {note}");
        sb.AppendLine($"api_key:   {plaintext}");
        sb.AppendLine("请求头: Authorization: Bearer <api_key>  或  X-SLDataAPI-Key: <api_key>");
        sb.AppendLine("丢失只能 revoke 后重新 create，无法找回明文。");
        response = sb.ToString();
        Log.Info($"[SLDataAPI] 已创建 API Key id={id} template={template}（明文仅回显给命令发送者）");
        return true;
    }
}