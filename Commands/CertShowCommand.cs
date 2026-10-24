// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using CommandSystem;
using SLDataAPI.Services;

namespace SLDataAPI.Commands;

/// <summary>sldataapi cert show：显示 TLS 模式、证书 SHA-256 指纹与有效期（仅本地控制台）。</summary>
public sealed class CertShowCommand : ICommand
{
    public string Command => "show";
    public string[] Aliases => new[] { "info" };
    public string Description => "显示 TLS 模式与证书 SHA-256 指纹";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (CertCommandGuard.Reject(sender, out response))
            return false;

        var cert = TlsCertificateService.Current;
        if (TlsCertificateService.Mode == TlsMode.Off || cert == null)
        {
            response = TlsCertificateService.Banner() +
                       (TlsCertificateService.Mode == TlsMode.Off ? "（未加载证书）" : "（证书不可用：fail-closed）");
            return true;
        }

        response =
            TlsCertificateService.Banner() + "\n" +
            $"Subject: {cert.Subject}\n" +
            $"有效期: {cert.NotBefore:yyyy-MM-dd} ~ {cert.NotAfter:yyyy-MM-dd}\n" +
            $"来源: {TlsCertificateService.Source}\n" +
            $"文件: {TlsCertificateService.PfxPath}";
        return true;
    }
}
