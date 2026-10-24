// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

namespace SLDataAPI.Auth;

/// <summary>
/// apikey CLI 本地控制台策略（纯逻辑，无 CommandSystem 依赖）。
/// 命令层将 <c>sender is ServerConsoleSender</c> 传入；与 cert 对齐：RemoteAdmin / 远程通道一律拒绝。
/// </summary>
public static class ApikeyConsoleGuardLogic
{
    public const string LocalOnlyMessage =
        "拒绝执行：sldataapi apikey 仅限服务器本地控制台（LocalAdmin）使用，RemoteAdmin 与远程控制通道均不可用。";

    /// <summary>
    /// 若应拒绝则返回 true 并写出 response。
    /// 先拦远程执行上下文，再要求本地控制台 sender。
    /// </summary>
    public static bool Reject(bool isServerConsoleSender, out string response)
    {
        if (RemoteCommandGuard.RejectIfRemote(out string remoteErr))
        {
            response = remoteErr;
            return true;
        }
        if (!isServerConsoleSender)
        {
            response = LocalOnlyMessage;
            return true;
        }
        response = "";
        return false;
    }
}
