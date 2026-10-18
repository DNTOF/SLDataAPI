// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System;
using System.Globalization;

namespace SLDataAPI.Control;

/// <summary>新增控制端点的入参校验与上限（纯逻辑，无 Unity 依赖，可单测）。</summary>
public static class FeatureInputRules
{
    /// <summary>单次 add 物品上限（游戏背包 8 格）。</summary>
    public const int MaxItemAdd = 8;
    /// <summary>单次 remove 上限。</summary>
    public const int MaxItemRemove = 8;
    /// <summary>单次给糖上限（SCP-330 袋最多 6 颗）。</summary>
    public const int MaxCandyGive = 6;
    /// <summary>灯光闪烁（熄灯）时长上限，秒。</summary>
    public const float MaxFlickerSeconds = 300f;

    public static readonly string[] InventoryActions = { "list", "add", "remove", "clear" };

    public static bool IsInventoryAction(string? action, out string normalized)
    {
        normalized = (action ?? "list").Trim().ToLowerInvariant();
        if (normalized.Length == 0) normalized = "list";
        return Array.IndexOf(InventoryActions, normalized) >= 0;
    }

    /// <summary>数量校验：&lt;=0 视为 1；超过上限返回 false（拒绝，不静默截断）。</summary>
    public static bool TryValidateAmount(int amount, int max, out int value)
    {
        value = amount <= 0 ? 1 : amount;
        return value <= max;
    }

    /// <summary>闪烁时长：钳制到 [0.1, MaxFlickerSeconds]；NaN/∞ 返回 false。</summary>
    public static bool TryClampFlicker(float seconds, out float value)
    {
        value = 0f;
        if (float.IsNaN(seconds) || float.IsInfinity(seconds)) return false;
        value = Math.Max(0.1f, Math.Min(MaxFlickerSeconds, seconds));
        return true;
    }

    /// <summary>
    /// 解析灯光颜色：#RGB / #RRGGBB / #RRGGBBAA（# 可省略）。"reset" / "none" / "clear" → 清除覆盖色（isReset=true，全 0）。
    /// 返回 0-1 浮点分量。
    /// </summary>
    public static bool TryParseColor(string? input, out float r, out float g, out float b, out float a, out bool isReset)
    {
        r = g = b = a = 0f;
        isReset = false;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input!.Trim();
        if (s.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            isReset = true;
            return true;
        }
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
        if (s.Length == 3)
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        if (s.Length != 6 && s.Length != 8) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        if (s.Length == 6) v = (v << 8) | 0xFF;
        r = ((v >> 24) & 0xFF) / 255f;
        g = ((v >> 16) & 0xFF) / 255f;
        b = ((v >> 8) & 0xFF) / 255f;
        a = (v & 0xFF) / 255f;
        return true;
    }
}
