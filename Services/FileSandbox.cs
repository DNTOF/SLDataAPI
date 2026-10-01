// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System;
using System.IO;

namespace SLDataAPI.Services;

/// <summary>
/// FileRoot 路径沙箱（纯 IO 逻辑，无 Unity / LabAPI 依赖，可供单元测试直接链接）。
/// HTTP POST /control/files/* 与 WS call 共用同一实现，保证两条传输的越界判定完全一致：
///   1. 拒绝含 ':' 的相对路径（NTFS 备用数据流 / 盘符注入）；
///   2. 规范化后必须仍在 FileRoot 内（防 ".." 穿越、绝对路径拼接出界）；
///   3. 内测（beta）起：FileRoot 之下的路径分量不得是重解析点（符号链接 / 目录联接 junction），
///      防止在根内放一个指向根外的链接绕过前缀判定。FileRoot 本身允许是链接（由服主配置）。
/// </summary>
public static class FileSandbox
{
    /// <summary>
    /// 把相对路径解析为根目录内的绝对路径；越界 / 非法路径 / 根目录不存在 / 经过链接时抛异常。
    /// 错误信息不回显完整绝对路径（会直达客户端）。
    /// </summary>
    public static string Resolve(string root, string? relPath, bool rejectReparsePoints = true)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("文件端点未启用（服务器未配置 FileRoot）");

        string fullRoot;
        try { fullRoot = Path.GetFullPath(root.Trim()); }
        catch (Exception ex) { throw new InvalidOperationException($"FileRoot 非法: {ex.Message}"); }

        if (!Directory.Exists(fullRoot))
            throw new InvalidOperationException($"FileRoot 目录不存在（{Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar))}）");

        // X-09：拒绝含冒号的相对路径（Windows 盘符外的 ":" 即 NTFS 备用数据流）
        if ((relPath ?? "").IndexOf(':') >= 0)
            throw new ArgumentException("路径含非法字符 ':'");

        // 统一分隔符后去掉首尾斜杠，避免 ".." 或绝对路径被 Combine 拼接出界
        string rel = (relPath ?? "").Replace('\\', '/').Trim('/');
        string full;
        try { full = Path.GetFullPath(Path.Combine(fullRoot, rel)); }
        catch (Exception ex) { throw new ArgumentException($"路径非法: {ex.Message}"); }

        string rootTrim = fullRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (!IsInside(rootTrim, full))
            throw new ArgumentException("路径越界：不允许访问 FileRoot 之外的目录");

        if (rejectReparsePoints)
            EnsureNoReparsePoints(rootTrim, full);

        return full;
    }

    /// <summary>full 是否等于 root 或位于 root 之下（大小写不敏感，与 Windows 文件系统一致）。</summary>
    public static bool IsInside(string rootNoTrailingSep, string full)
    {
        string prefix = rootNoTrailingSep + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
               full.TrimEnd(Path.DirectorySeparatorChar).Equals(rootNoTrailingSep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 自 root 的下一级起逐级检查已存在的路径分量：任何一级是重解析点（symlink / junction）即拒绝。
    /// 不存在的分量（新建文件 / 目录）跳过——其父级已逐级校验过。
    /// </summary>
    public static void EnsureNoReparsePoints(string rootNoTrailingSep, string full)
    {
        if (full.TrimEnd(Path.DirectorySeparatorChar).Equals(rootNoTrailingSep, StringComparison.OrdinalIgnoreCase))
            return;

        string rest = full.Substring(rootNoTrailingSep.Length).TrimStart(Path.DirectorySeparatorChar);
        string current = rootNoTrailingSep;
        foreach (string part in rest.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = current + Path.DirectorySeparatorChar + part;
            FileAttributes attrs;
            try
            {
                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    // 悬空链接：File/Directory.Exists 均为 false，但 GetAttributes 仍能读到链接本身
                    try { attrs = File.GetAttributes(current); }
                    catch { return; } // 真的不存在：后续分量也不存在
                }
                else
                {
                    attrs = File.GetAttributes(current);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"路径不可访问: {ex.Message}");
            }

            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("路径经过符号链接或目录联接（junction），已拒绝（防止越出 FileRoot）");
        }
    }
}
