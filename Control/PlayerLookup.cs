// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections.Generic;
using System.Linq;

namespace SLDataAPI.Control;

/// <summary>
/// 玩家标识解析顺序（纯逻辑，无 Unity / LabAPI 依赖，可单测）。
/// 顺序：PlayerId → UserId（忽略大小写）→ IP（仅 allowIp）→ 昵称精确（忽略大小写）。
/// 模糊昵称匹配由调用方用 LabAPI 兜底。
/// </summary>
public static class PlayerLookup
{
    public static T? Find<T>(string? target, IEnumerable<T> players,
        Func<T, int> id, Func<T, string?> userId, Func<T, string?> ip, Func<T, string?> nickname,
        bool allowIp) where T : class
    {
        if (string.IsNullOrWhiteSpace(target) || players == null) return null;
        string t = target!.Trim();
        var list = players.Where(p => p != null).ToList();

        if (int.TryParse(t, out int pid))
        {
            var byId = list.FirstOrDefault(p => id(p) == pid);
            if (byId != null) return byId;
        }

        var byUser = list.FirstOrDefault(p => string.Equals(userId(p), t, StringComparison.OrdinalIgnoreCase));
        if (byUser != null) return byUser;

        if (allowIp)
        {
            var byIp = list.FirstOrDefault(p => string.Equals(ip(p), t, StringComparison.OrdinalIgnoreCase));
            if (byIp != null) return byIp;
        }

        return list.FirstOrDefault(p => string.Equals(nickname(p), t, StringComparison.OrdinalIgnoreCase));
    }
}
