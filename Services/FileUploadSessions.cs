// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SLDataAPI.Services;

/// <summary>
/// 内测（beta_file_root_ws）：/control/files/write_chunk 的分块上传会话（纯内存，无 Unity / LabAPI 依赖，可单测）。
/// 设计目的：WS 控制通道单消息上限 256KB、HTTP 请求体上限 64KB，单次 files/write 无法覆盖 512KB 写入上限；
/// 分块上传让两条传输都能可靠写满上限，并用 sha256 做端到端完整性校验。
/// 约束：
///   - 会话绑定 (actor, path)：其他 Key 无法续写或提交他人的会话；
///   - 偏移必须严格等于已接收字节数（不支持乱序 / 重叠，重复块直接报错）；
///   - 总大小 ≤ maxBytes；全局并发会话 ≤ MaxSessions，每个 actor ≤ MaxSessionsPerActor；
///   - 空闲超过 Ttl 自动作废（防止半截上传长期占内存）。
/// 线程安全：所有公开方法内部加锁。
/// </summary>
public sealed class FileUploadSessions
{
    public const int MaxSessions = 8;
    public const int MaxSessionsPerActor = 2;
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(120);

    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly int _maxBytes;
    private readonly Func<DateTime> _clock;

    public FileUploadSessions(int maxBytes, Func<DateTime>? clock = null)
    {
        _maxBytes = maxBytes;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    private sealed class Session
    {
        public string Id = "";
        public string Actor = "";
        public string Path = "";
        public MemoryStream Data = new();
        public DateTime LastTouched;
    }

    public int Count
    {
        get { lock (_lock) { SweepUnlocked(); return _sessions.Count; } }
    }

    /// <summary>开启新会话，返回 upload_id。</summary>
    public string Begin(string actor, string path)
    {
        lock (_lock)
        {
            SweepUnlocked();
            int mine = 0;
            foreach (var s in _sessions.Values)
                if (string.Equals(s.Actor, actor, StringComparison.Ordinal)) mine++;
            if (mine >= MaxSessionsPerActor)
                throw new InvalidOperationException($"该 Key 未完成的上传会话过多（上限 {MaxSessionsPerActor}），请先完成或等待 {Ttl.TotalSeconds:F0}s 过期");
            if (_sessions.Count >= MaxSessions)
                throw new InvalidOperationException($"服务器未完成的上传会话过多（上限 {MaxSessions}）");

            string id = NewId();
            _sessions[id] = new Session { Id = id, Actor = actor ?? "", Path = NormalizePath(path), LastTouched = _clock() };
            return id;
        }
    }

    /// <summary>追加一块；返回追加后的总字节数。offset 必须等于已接收字节数。</summary>
    public long Append(string uploadId, string actor, string path, long offset, byte[] chunk)
    {
        lock (_lock)
        {
            SweepUnlocked();
            var s = GetOwnedUnlocked(uploadId, actor, path);
            if (offset != s.Data.Length)
                throw new ArgumentException($"offset 不连续：期望 {s.Data.Length}，收到 {offset}");
            if (s.Data.Length + chunk.Length > _maxBytes)
            {
                Remove(uploadId);
                throw new InvalidOperationException($"上传内容过大（> 上限 {_maxBytes} 字节），会话已作废");
            }
            s.Data.Write(chunk, 0, chunk.Length);
            s.LastTouched = _clock();
            return s.Data.Length;
        }
    }

    /// <summary>取出完整内容并结束会话；expectedSha256（可选，hex）不匹配时作废会话并抛异常。</summary>
    public byte[] Complete(string uploadId, string actor, string path, string? expectedSha256)
    {
        lock (_lock)
        {
            SweepUnlocked();
            var s = GetOwnedUnlocked(uploadId, actor, path);
            byte[] bytes = s.Data.ToArray();
            _sessions.Remove(uploadId);
            if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                !string.Equals(Sha256Hex(bytes), expectedSha256!.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("sha256 校验失败：上传内容与声明不一致，未写入");
            return bytes;
        }
    }

    /// <summary>调用方主动取消：仅能取消自己（同一 actor）的会话。</summary>
    public bool Abort(string uploadId, string actor)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(uploadId ?? "", out var s) && string.Equals(s.Actor, actor ?? "", StringComparison.Ordinal))
                return _sessions.Remove(uploadId!);
            return false;
        }
    }

    public bool Remove(string uploadId)
    {
        lock (_lock) return _sessions.Remove(uploadId ?? "");
    }

    public void Clear()
    {
        lock (_lock) _sessions.Clear();
    }

    public static string Sha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        byte[] h = sha.ComputeHash(data);
        var sb = new StringBuilder(h.Length * 2);
        foreach (byte b in h) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private Session GetOwnedUnlocked(string uploadId, string actor, string path)
    {
        if (string.IsNullOrEmpty(uploadId) || !_sessions.TryGetValue(uploadId, out var s))
            throw new ArgumentException("upload_id 不存在或已过期");
        if (!string.Equals(s.Actor, actor ?? "", StringComparison.Ordinal))
            throw new ArgumentException("upload_id 不属于当前 Key");
        if (!string.Equals(s.Path, NormalizePath(path), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("upload_id 与 path 不匹配");
        return s;
    }

    private void SweepUnlocked()
    {
        DateTime now = _clock();
        List<string>? dead = null;
        foreach (var kv in _sessions)
            if (now - kv.Value.LastTouched > Ttl)
                (dead ??= new List<string>()).Add(kv.Key);
        if (dead != null)
            foreach (var k in dead) _sessions.Remove(k);
    }

    private static string NormalizePath(string? p) => (p ?? "").Replace('\\', '/').Trim('/');

    private static string NewId()
    {
        byte[] b = new byte[12];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        var sb = new StringBuilder("up_");
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }
}
