using System;
using CommandSystem;
using SLDataAPI.Auth;
using SLDataAPI.Services;

namespace SLDataAPI.Commands;

public sealed class ApikeyRevokeCommand : ICommand, IUsageProvider
{
    public string Command => "revoke";
    public string[] Aliases => new[] { "delete", "remove" };
    public string Description => "吊销 API Key";
    public string[] Usage => new[] { "<id>" };

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (arguments.Count < 1)
        {
            response = "用法: sldataapi apikey revoke <id>";
            return false;
        }

        string id = arguments.Array![arguments.Offset];

        // 层 2：与 create 同理——吊销同样是密钥面变更（可被用来把值班 Key 踢掉后重铸），需要人工确认
        if (!OperatorConfirmService.Confirm($"Confirm revoke API key id={id} ?", out string denyReason))
        {
            response = $"已中止吊销 API Key（未获服务端确认）：{denyReason}\n确认提示出现在服务器控制台（LocalAdmin），请在那里回答 y。";
            Log.Warn($"[SLDataAPI] API Key 吊销未获确认，已中止 id={id}：{denyReason}");
            return false;
        }

        if (!ApiKeyService.TryRevoke(id, out string error))
        {
            response = "吊销失败: " + error;
            return false;
        }

        response = $"已吊销 API Key: {id}";
        Log.Info($"[SLDataAPI] 已吊销 API Key id={id}");
        return true;
    }
}