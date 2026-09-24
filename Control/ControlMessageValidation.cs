using System;

namespace SLDataAPI.Control;

/// <summary>
/// /control/broadcast 与 /control/staffchat 的请求校验与时长钳制。
/// 时长规则与 /control/moderation/msg 一致：≤0 回落到 5 秒，上限 60 秒。
/// 无 Unity / LabAPI 依赖，可供单元测试直接链接。
/// </summary>
public static class ControlMessageValidation
{
    public const int MaxMessageLength = 500;
    public const float DefaultDurationSeconds = 5f;
    public const float MaxDurationSeconds = 60f;

    /// <summary>
    /// 校验必填 message：空白 → 缺少字段；超长 → 带实际长度的中文错误。
    /// </summary>
    public static bool TryValidateMessage(string? message, out string error)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            error = "缺少 message 字段";
            return false;
        }

        if (message!.Length > MaxMessageLength)
        {
            error = $"message 过长（{message.Length} 字符，上限 {MaxMessageLength}）";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// 与 /control/moderation/msg 相同的显示时长钳制。
    /// </summary>
    public static float ClampDurationSeconds(float durationSeconds) =>
        durationSeconds <= 0 ? DefaultDurationSeconds : Math.Min(durationSeconds, MaxDurationSeconds);
}
