// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SLDataAPI.Services;

/// <summary>
/// 强名称真验签：P/Invoke <c>StrongNameSignatureVerificationEx</c>（mscoree.dll）。
/// 公钥令牌比对只读元数据、不验签；本类强制验签，供自动更新安装前 fail-closed 使用。
/// </summary>
public static class StrongNameVerifier
{
    /// <summary>测试钩子：非 null 时跳过原生调用。生产代码勿设置。</summary>
    public static Func<string, StrongNameVerifyStatus>? VerifyOverride { get; set; }

    /// <summary>
    /// 对磁盘上的程序集文件做强制强名称验签。
    /// 文件缺失/不可读 → Invalid；原生 API 不可用或异常 → Unavailable；验签失败 → Invalid。
    /// </summary>
    public static StrongNameVerifyStatus VerifyFile(string? path)
    {
        if (VerifyOverride != null)
            return VerifyOverride(path ?? "");

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return StrongNameVerifyStatus.Invalid;

        try
        {
            // fForceVerification=true：忽略「跳过强名称校验」的机器策略，必须真正验签
            bool ok = Native.StrongNameSignatureVerificationEx(path, true, out bool wasVerified);
            if (ok && wasVerified)
                return StrongNameVerifyStatus.Verified;
            return StrongNameVerifyStatus.Invalid;
        }
        catch (DllNotFoundException)
        {
            return StrongNameVerifyStatus.Unavailable;
        }
        catch (EntryPointNotFoundException)
        {
            return StrongNameVerifyStatus.Unavailable;
        }
        catch (BadImageFormatException)
        {
            return StrongNameVerifyStatus.Invalid;
        }
        catch
        {
            // 其它原生/互操作失败：fail-closed，视为验签通道不可用
            return StrongNameVerifyStatus.Unavailable;
        }
    }

    private static class Native
    {
        [DllImport("mscoree.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StrongNameSignatureVerificationEx(
            [MarshalAs(UnmanagedType.LPWStr)] string wszFilePath,
            [MarshalAs(UnmanagedType.Bool)] bool fForceVerification,
            [MarshalAs(UnmanagedType.Bool)] out bool pfWasVerified);
    }
}
