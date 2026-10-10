// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace SLDataAPI.Services;

/// <summary>TLS 工作模式（config.yml 的 <c>tls_mode</c>）。</summary>
public enum TlsMode
{
    /// <summary>不生成证书、不嗅探首字节，行为与引入 TLS 之前完全一致。</summary>
    Off,
    /// <summary>同端口同时接受 TLS 与明文（默认）。</summary>
    Optional,
    /// <summary>只接受 TLS；明文 HTTP / WS 一律 426 + <c>data.code = tls_required</c>。</summary>
    Required,
}

/// <summary>
/// 自签 TLS 的纯逻辑（无 Unity / LabAPI 依赖，可供单元测试直接链接）：
/// 模式解析、首字节嗅探判定、证书指纹格式、426 信封。
/// </summary>
public static class TlsPolicy
{
    /// <summary>TLS 记录层 handshake 类型字节：ClientHello 的第一个字节。</summary>
    public const byte TlsHandshakeRecordType = 0x16;

    /// <summary>服务端接受的协议：TLS 1.2 | TLS 1.3（Unity 的 UnityTls 实际只协商到 1.2）。</summary>
    public const SslProtocols ServerProtocols = (SslProtocols)(3072 | 12288);

    /// <summary>给上游客户端的机器可读码（426 响应 data.code）。</summary>
    public const string TlsRequiredCode = "tls_required";

    public const string TlsRequiredMessage =
        "该端口要求 TLS（tls_mode: required）：请改用 https:// / wss:// 并校验证书指纹";

    /// <summary>
    /// 解析 <c>tls_mode</c>：off / optional / required（大小写、首尾空白不敏感）。
    /// 空值 = 默认 optional；非法值 valid=false 并按 optional 处理（调用方负责告警）。
    /// </summary>
    public static TlsMode ParseMode(string? value, out bool valid)
    {
        valid = true;
        string v = (value ?? "").Trim().ToLowerInvariant();
        switch (v)
        {
            case "": return TlsMode.Optional;
            case "off": case "false": case "disabled": return TlsMode.Off;
            case "optional": return TlsMode.Optional;
            case "required": return TlsMode.Required;
            default:
                valid = false;
                return TlsMode.Optional;
        }
    }

    /// <summary>配置文件里的规范写法。</summary>
    public static string ToConfigString(TlsMode mode) => mode switch
    {
        TlsMode.Off => "off",
        TlsMode.Required => "required",
        _ => "optional",
    };

    /// <summary>该模式下是否需要嗅探首字节（off 完全不碰连接，保持旧行为）。</summary>
    public static bool ShouldSniff(TlsMode mode) => mode != TlsMode.Off;

    /// <summary>首字节判定：仅在启用 TLS 且首字节是 TLS handshake 记录时走 SslStream。</summary>
    public static bool IsTlsClientHello(TlsMode mode, int firstByte) =>
        mode != TlsMode.Off && firstByte == TlsHandshakeRecordType;

    /// <summary>required 模式下的明文连接必须 426。</summary>
    public static bool IsPlaintextRejected(TlsMode mode, bool connectionIsTls) =>
        mode == TlsMode.Required && !connectionIsTls;

    /// <summary>证书指纹：SHA-256(证书 DER)，大写十六进制、冒号分隔（AA:BB:...，共 32 组）。</summary>
    public static string ComputeFingerprint(byte[] certificateDer)
    {
        if (certificateDer == null) throw new ArgumentNullException(nameof(certificateDer));
        using var sha = SHA256.Create();
        return FormatFingerprint(sha.ComputeHash(certificateDer));
    }

    /// <summary>把哈希字节格式化为大写十六进制冒号分隔。</summary>
    public static string FormatFingerprint(byte[] hash)
    {
        if (hash == null) throw new ArgumentNullException(nameof(hash));
        var sb = new StringBuilder(hash.Length * 3);
        for (int i = 0; i < hash.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(hash[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 426 响应体：与 transport_mismatch 同一信封（ControlResponse：success / message / data）。
    /// 手写 JSON，保证字段顺序与 Newtonsoft 序列化 ControlResponse 一致，且本文件不依赖 Newtonsoft。
    /// </summary>
    public static string BuildTlsRequiredJson(string? message = null)
    {
        return "{\"success\":false,\"message\":\"" + JsonEscape(message ?? TlsRequiredMessage) +
               "\",\"data\":{\"code\":\"" + TlsRequiredCode + "\"}}";
    }

    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
