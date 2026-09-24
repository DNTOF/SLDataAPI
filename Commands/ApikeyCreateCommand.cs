using System;
using CommandSystem;
using SLDataAPI.Auth;

namespace SLDataAPI.Commands;

public sealed class ApikeyCreateCommand : ICommand, IUsageProvider
{
    public string Command => "create";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "创建 API Key（明文写入一次性文件，命令只回路径）";
    public string[] Usage => new[] { "<id> <duty|admin> [note]" };

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (arguments.Count < 2)
        {
            response = "用法: sldataapi apikey create <id> <duty|admin> [note]\n明文写入一次性 txt，命令只回路径；5 分钟后自动删除。";
            return false;
        }

        string id = arguments.Array![arguments.Offset];
        string template = arguments.Array![arguments.Offset + 1];
        string note = arguments.Count >= 3
            ? string.Join(" ", arguments.Array!, arguments.Offset + 2, arguments.Count - 2)
            : "";

        // 远程控制通道已被 RemoteCommandGuard 硬拒绝，走不到这里。
        if (!ApiKeyService.TryCreate(id, template, note, out string plaintext, out string error))
        {
            response = "创建失败: " + error;
            return false;
        }

        string configDir = ApiKeyService.ConfigDirectory;
        bool wrote = ApiKeyCreateDelivery.TryWriteOnceFile(
            configDir, id, plaintext, out string filePath, out string writeError);

        // 后台尽力复制；失败静默，不影响创建，response 不提剪贴板、不含密钥。
        ApiKeyClipboard.TryCopyInBackground(plaintext);

        if (!wrote)
        {
            response = string.IsNullOrEmpty(writeError)
                ? "已创建，但一次性文件写入失败；请 revoke 后重试。"
                : writeError;
            Log.Info($"[SLDataAPI] 已创建 API Key id={id} template={template}（一次性文件写入失败）");
            return false;
        }

        response = ApiKeyCreateDelivery.FormatConsoleResponse(filePath);
        ApiKeyCreateDelivery.ScheduleAutoDelete(filePath, result =>
        {
            if (result.Deleted)
                Log.Info($"[SLDataAPI] 已自动删除一次性 API Key 文件：{result.FilePath}");
            else
                Log.Info($"[SLDataAPI] 自动删除一次性 API Key 文件失败：{result.FilePath}" +
                         (string.IsNullOrEmpty(result.Error) ? "" : $" ({result.Error})"));
        });
        Log.Info($"[SLDataAPI] 已创建 API Key id={id} template={template}（明文已写入一次性文件，5 分钟后自动删除）");
        return true;
    }
}
