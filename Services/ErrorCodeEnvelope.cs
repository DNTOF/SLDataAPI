// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SLDataAPI.Services;

/// <summary>
/// 错误响应 code 补齐（纯逻辑，仅依赖 Newtonsoft，可供单元测试直接链接）。
///
/// 只解析 JSON 用于判断 success / 既有 code / data.code / message，然后
/// 在根对象末尾**字符串追加** <c>"code":"..."</c>，不重新序列化。
/// 这样 zh-CN 下既有字段的值、转义与顺序保持逐字不变（浮点数、\uXXXX、
/// 控制字符、Unicode 等不会被 JObject 重新格式化）。
/// </summary>
public static class ErrorCodeEnvelope
{
    public static string EnsureErrorCode(int status, string json)
    {
        if (status < 400 || string.IsNullOrEmpty(json)) return json;

        try
        {
            var o = JObject.Parse(json);
            if (o["success"]?.Type == JTokenType.Boolean && (bool)o["success"]!) return json;

            // 已有顶层 code 时不重复追加（避免产生同名键或改动既有字段）。
            if (o["code"] != null) return json;

            string? code = o["data"] is JObject d ? d["code"]?.ToString() : null;
            if (string.IsNullOrEmpty(code))
                code = Localization.ErrorCodeFor(status, o["message"]?.ToString());
            if (string.IsNullOrEmpty(code)) return json;

            int end = json.LastIndexOf('}');
            if (end < 0) return json;

            return json.Substring(0, end) + ",\"code\":" + JsonConvert.ToString(code) + json.Substring(end);
        }
        catch
        {
            // 非法 JSON / 非对象体：保持原样，绝不因补 code 破坏响应。
            return json;
        }
    }
}
