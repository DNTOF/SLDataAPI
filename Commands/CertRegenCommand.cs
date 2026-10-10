// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF

using System;
using CommandSystem;
using SLDataAPI.Services;

namespace SLDataAPI.Commands;

/// <summary>
/// sldataapi cert regen：重新生成自签证书并热替换（仅影响新连接；已建立的连接沿用旧证书直至断开）。
/// 仅本地控制台；写审计日志。
/// </summary>
public sealed class CertRegenCommand : ICommand
{
    public string Command => "regen";
    public string[] Aliases => new[] { "regenerate", "rotate" };
    public string Description => "重新生成自签 TLS 证书（已固定指纹的客户端必须重新确认）";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (CertCommandGuard.Reject(sender, out response))
            return false;

        bool ok = TlsCertificateService.TryRegenerate(out string oldFp, out string newFp, out string error);
        ControlLogService.Record("console:sldataapi cert regen", "", ok,
            ok ? $"old={oldFp} new={newFp}" : error, "local-console");

        if (!ok)
        {
            response = "重新生成失败: " + error;
            return false;
        }

        Log.Warn("[SLDataAPI] TLS 证书已重新生成（新连接立即生效）。已固定旧指纹的客户端必须重新确认新指纹！" +
                 $" 旧 SHA-256 {oldFp} → 新 SHA-256 {newFp}");
        response =
            "TLS 证书已重新生成并热替换（仅对新连接生效）。\n" +
            $"旧指纹: SHA-256 {oldFp}\n" +
            $"新指纹: SHA-256 {newFp}\n" +
            "警告：所有已固定（pin）旧指纹的客户端将拒绝连接，必须重新确认新指纹。";
        return true;
    }
}
