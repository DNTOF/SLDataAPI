using System;
using CommandSystem;
using SLDataAPI.Services;

namespace SLDataAPI.Commands;

/// <summary>
/// 确认通道自检：走完整条人工确认通道（新弹 cmd 窗口 → 行模式 → 对话框），
/// 但不碰任何 Key。装机 / 升级后用它验证确认窗口能不能正常弹出来，
/// 而不用真去铸一把 Key 再吊销。
/// </summary>
public sealed class ApikeyConfirmTestCommand : ICommand
{
    public string Command => "confirmtest";
    public string[] Aliases => new[] { "testconfirm" };
    public string Description => "自检人工确认通道（弹出确认窗口，不创建也不吊销任何 Key）";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        var model = new ConfirmPanelModel
        {
            Action = ConfirmAction.SelfTest,
            KeyId = "self-test",
            Note = "仅自检确认通道，不会写入 apikey.config",
        };

        if (!OperatorConfirmService.Confirm(model, out string reason))
        {
            response = $"确认通道自检未通过：{reason}\n" +
                       "预期行为：服务器桌面上新弹出一个 cmd 窗口显示确认面板（LocalAdmin 窗口不受影响），在该窗口按 Y。";
            return false;
        }

        response = "确认通道自检通过：已收到服务端操作者的确认（未创建、未吊销任何 Key）。";
        return true;
    }
}
