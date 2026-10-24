// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using CommandSystem;
using SLDataAPI.Auth;

namespace SLDataAPI.Commands;

public sealed class ApikeyRevokeCommand : ICommand, IUsageProvider
{
    public string Command => "revoke";
    public string[] Aliases => new[] { "delete", "remove" };
    public string Description => "吊销 API Key";
    public string[] Usage => new[] { "<id>" };

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (ApikeyCommandGuard.Reject(sender, out response))
            return false;

        if (arguments.Count < 1)
        {
            response = "用法: sldataapi apikey revoke <id>";
            return false;
        }

        string id = arguments.Array![arguments.Offset];

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
