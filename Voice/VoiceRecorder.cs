using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VoiceChat;

namespace SLDataAPI.Voice;

/// <summary>
/// 语音录音取证（v2.5 / Yagami Light）：每局游戏自动保存为一个压缩包
///   voice_round_&lt;局号&gt;_&lt;开始时间&gt;.zip，内含：
///   1) 按频道分轨的音轨：&lt;局名&gt;.&lt;频道&gt;.wav（48kHz / 16bit / 单声道 PCM，
///      如 .Proximity / .Radio / .Intercom / .Scp …）
///   2) 时间轴日志：&lt;局名&gt;.timeline.log（谁在什么时候说了多久，含 steamid / 角色 / 频道）
/// 设计：
///   - **频道隔离**：SCP 频道与人类频道（近距离/对讲机/Intercom）是游戏里独立的听觉流，
///     分轨保存互不混合；同一频道内多人同时说话才逐采样求和混合（钳制防溢出）。
///   - 采样级对齐：帧间补静默，采样号 = 回合内秒 × 48000 = 任一频道文件内的精确位置。
///   - HandlePcm / OnSpeakerGone / BeginRound / EndRound 均由主线程调用；
///     磁盘写入在独立后台线程完成，主线程只入队（有界队列，满则丢帧并告警）。
///   - 定稿与 zip 打包在后台线程完成（快照隔离，不占主线程、不阻塞下一局）；
///     服务器停服时同步等待打包结束。按 voice_record_max_rounds 清理最旧局。
///   - 依赖语音管线运行（voice_enabled=true）；本类不自行订阅事件，由 Plugin 驱动。
/// </summary>
public static class VoiceRecorder
{
    private const int SampleRate = 48000;
    private const int BytesPerSample = 2;        // 16bit PCM
    private const int MaxQueuedFrames = 400;     // 有界队列（每帧缓冲 ≤23KB ≈ 9MB 待写上限），超出丢帧告警
    private const float BurstGapSeconds = 0.8f;  // 与语音转发一致：间隔超过视为新一轮讲话
    private const int MixWindowSamples = 48000 * 4 / 10; // 混合窗口 0.4s：同频道重叠帧在此窗口内求和混合后延迟落盘

    private static bool _enabled;
    private static int _maxRounds = 10;
    private static string _dir = "";

    private static int _roundNumber;
    private static DateTime _roundStart;
    private static float _roundStartTime;
    private static string _baseName = "";

    private static BlockingCollection<VoiceFrame>? _queue;
    private static Thread? _writer;
    private static long _droppedFrames;
    private static float _lastDropWarn;

    private static StringBuilder? _timeline;
    private static Task? _finalizeTask; // 上一局的打包任务（Disable 时同步等待）

    // 讲话段状态（全部主线程访问）
    private static readonly Dictionary<uint, (float StartTime, long StartSample)> OpenBursts = new();
    private static readonly Dictionary<uint, float> LastPacket = new();
    private static readonly Dictionary<uint, (string Name, string UserId, string Role, byte Channel)> BurstMeta = new();

    // 频道分轨：主线程在首包到达时创建（含建档日志），写盘线程只读
    private static readonly Dictionary<byte, ChannelTrack> Tracks = new();

    private struct VoiceFrame
    {
        public float[] Data;
        public int Count;
        public long StartSample; // 本帧在回合时间轴上的起始采样位置（采样号 = 回合内秒 × 48000）
        public byte Channel;
    }

    private sealed class ChannelTrack
    {
        public string FilePath = "";
        public BinaryWriter? Writer;
        public long SamplesWritten;
        public readonly short[] Pending = new short[MixWindowSamples];
        public long PendingStart;
        public int PendingLen;
    }

    public static void Configure(bool enabled, int maxRounds, string dir)
    {
        _enabled = enabled;
        _maxRounds = maxRounds;
        _dir = string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCP Secret Laboratory", "SLDataAPI", "VoiceRecords")
            : dir;
    }

    // ────────────── 回合生命周期（主线程） ──────────────

    /// <summary>回合开始：开启新录音（若上一局未正常结束则先兜底定稿）。</summary>
    public static void BeginRound()
    {
        if (!_enabled) return;

        EndRound(); // 兜底：上一局未收到 RoundEnded

        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI] 无法创建录音目录 {_dir}: {ex.Message}，本局录音已跳过");
            return;
        }

        _roundNumber++;
        _roundStart = DateTime.Now;
        _roundStartTime = Time.time;
        _baseName = $"voice_round_{_roundNumber}_{_roundStart:yyyyMMdd_HHmmss}";

        try
        {
            _timeline = new StringBuilder();
            AppendTimelineHeader();
            AppendTimeline("回合开始");
            _queue = new BlockingCollection<VoiceFrame>(MaxQueuedFrames);
            _droppedFrames = 0;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "SLDataAPI-VoiceRecorder" };
            _writer.Start();
            Log.Info($"[SLDataAPI] 本局语音录音开始: {_baseName}（目录: {_dir}，频道分轨）");
        }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI] 录音初始化失败，本局录音已跳过: {ex.Message}");
            _queue = null;
            _writer = null;
            _timeline = null;
        }
    }

    /// <summary>
    /// 回合结束：关闭所有讲话段，把定稿工作（补静默/补头/打包 zip/清理旧局）快照后交给
    /// 后台线程执行——压缩大文件耗时，绝不能占主线程。waitFinalize=true 时（服务器停服）
    /// 同步等待打包完成，防止进程退出打断。
    /// </summary>
    public static void EndRound(bool waitFinalize = false)
    {
        if (_queue == null && _timeline == null)
            return;

        List<ChannelTrack>? tracks = null;
        string baseName = _baseName;
        string? timelineText = null;
        long roundEndSample = 0;

        try
        {
            foreach (uint netId in new List<uint>(OpenBursts.Keys))
                CloseBurst(netId, Time.time);

            roundEndSample = (long)((Time.time - _roundStartTime) * SampleRate);
            AppendTimeline("回合结束",
                $"时长={Time.time - _roundStartTime:F3}s 丢帧={_droppedFrames} 终点采样={roundEndSample}");

            // 先让写盘线程把队列耗尽（含各轨混合窗口刷盘）
            _queue?.CompleteAdding();
            _writer?.Join(3000);

            // 快照定稿所需的一切：主线程随后清空状态，下一局可立即开始
            tracks = Tracks.Values.ToList();
            foreach (var kv in Tracks)
                AppendTimeline("通道归档",
                    $"{SafeChannelName(kv.Key)}\t{Path.GetFileName(kv.Value.FilePath)}\t终点采样={roundEndSample}");
            timelineText = _timeline?.ToString();
        }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI] 录音收尾异常（{_baseName}）: {ex.Message}");
        }
        finally
        {
            OpenBursts.Clear();
            LastPacket.Clear();
            BurstMeta.Clear();
            Tracks.Clear();
            _queue = null;
            _writer = null;
            _timeline = null;
        }

        // 后台定稿 + 打包（快照独立，与下一局完全隔离）
        var task = Task.Run(() => FinalizeRound(tracks, baseName, timelineText, roundEndSample));
        _finalizeTask = task;
        if (waitFinalize)
        {
            try { task.Wait(20000); } catch { /* 超时则放弃等待，散件保留 */ }
        }
    }

    /// <summary>
    /// 后台线程：各频道轨补静默/补头/关闭 → 写时间轴 → 全部文件打入 zip → 删散件 → 清理旧局。
    /// 失败时散件文件保留在录音目录（日志提示），下局清理兜底。
    /// </summary>
    private static void FinalizeRound(List<ChannelTrack>? tracks, string baseName, string? timelineText, long roundEndSample)
    {
        try
        {
            // 1. 各轨收尾
            foreach (var track in tracks ?? Enumerable.Empty<ChannelTrack>())
            {
                FlushPending(track, track.PendingLen);
                WriteSilence(track, roundEndSample - track.SamplesWritten);
                PatchAndClose(track);
            }

            // 2. 时间轴落盘
            string timelinePath = Path.Combine(_dir, baseName + ".timeline.log");
            if (timelineText != null)
                File.WriteAllText(timelinePath, timelineText, Encoding.UTF8);

            // 3. 打包（PCM 压缩率约 40%-60%，同时交付各频道 + 时间轴，删散件）
            var files = new List<string>();
            foreach (var t in tracks ?? Enumerable.Empty<ChannelTrack>())
                if (t.FilePath.Length > 0 && File.Exists(t.FilePath))
                    files.Add(t.FilePath);
            if (File.Exists(timelinePath)) files.Add(timelinePath);

            if (files.Count > 0)
            {
                string zipPath = Path.Combine(_dir, baseName + ".zip");
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (string f in files)
                        zip.CreateEntryFromFile(f, Path.GetFileName(f), System.IO.Compression.CompressionLevel.Optimal);
                }
                foreach (string f in files)
                {
                    try { File.Delete(f); } catch { /* 占用则保留 */ }
                }
            }

            CleanupOldRounds();
            Log.Info($"[SLDataAPI] 本局录音已打包: {baseName}.zip（{(tracks?.Count ?? 0)} 个频道）");
        }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI] 录音定稿失败（{baseName}）: {ex.Message} —— 散件文件保留在录音目录");
        }
    }

    // ────────────── 语音包入口（主线程，VoiceService.HandleIncoming 调用） ──────────────

    /// <summary>
    /// 每收到一个解码后的语音包：维护讲话段（开始/延续/轮转）并确保频道轨存在、入队待写。
    /// pcm 数组由调用方新建（每包一个），本方法接管所有权，无需拷贝。
    /// </summary>
    public static void HandlePcm(uint netId, float[] pcm, int sampleCount,
        string nickname, string userId, string role, byte channel, float now)
    {
        if (_queue == null) return;

        // 首包到达该频道：主线程建档（写盘线程只读 Tracks）
        if (!Tracks.ContainsKey(channel))
            OpenChannel(channel);

        // 本帧在回合时间轴上的起始采样位置（时间轴秒数 × 48000 = WAV 内精确位置）
        long startSample = (long)((now - _roundStartTime) * SampleRate);

        // 讲话段判定：距该说话者最近一包超过阈值视为新一轮讲话（先收尾上一段，再开新段）
        bool continuing = LastPacket.TryGetValue(netId, out float lastPkt) &&
                          now - lastPkt <= BurstGapSeconds;
        if (!continuing)
        {
            CloseBurst(netId, now);
            OpenBursts[netId] = (now, startSample);
            AppendTimeline("说话开始",
                $"{nickname}\t{userId}\t{role}\t{channel}\t{netId}\t起点采样={startSample}");
        }
        LastPacket[netId] = now;
        BurstMeta[netId] = (nickname, userId, role, channel);

        if (!_queue.TryAdd(new VoiceFrame { Data = pcm, Count = sampleCount, StartSample = startSample, Channel = channel }))
        {
            _droppedFrames++;
            if (now - _lastDropWarn > 5f)
            {
                _lastDropWarn = now;
                Log.Warn($"[SLDataAPI] 录音写入队列已满，丢弃音频帧（累计 {_droppedFrames}）——磁盘写入过慢，请检查录音目录 {_dir}");
            }
        }
    }

    /// <summary>说话者静默超时被清理（VoiceService.Cleanup 调用）：收尾其讲话段。</summary>
    public static void OnSpeakerGone(uint netId, float now)
    {
        CloseBurst(netId, now);
        LastPacket.Remove(netId);
    }

    private static void CloseBurst(uint netId, float now)
    {
        if (OpenBursts.TryGetValue(netId, out var burst))
        {
            if (BurstMeta.TryGetValue(netId, out var meta))
            {
                long endSample = (long)((now - _roundStartTime) * SampleRate);
                AppendTimeline("说话结束",
                    $"{meta.Name}\t{meta.UserId}\t{meta.Role}\t{meta.Channel}\t{netId}\t" +
                    $"时长={now - burst.StartTime:F3}s 起点采样={burst.StartSample} 终点采样={endSample}");
            }
            OpenBursts.Remove(netId);
        }
        BurstMeta.Remove(netId);
    }

    // ────────────── 频道轨（主线程创建） ──────────────

    private static void OpenChannel(byte channel)
    {
        try
        {
            string name = SafeChannelName(channel);
            var track = new ChannelTrack
            {
                FilePath = Path.Combine(_dir, $"{_baseName}.{name}.wav"),
            };
            var stream = new FileStream(track.FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            track.Writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            WriteWavHeader(track.Writer);
            Tracks[channel] = track;
            AppendTimeline("通道开始", $"{name}\t{channel}\t{Path.GetFileName(track.FilePath)}");
            Log.Info($"[SLDataAPI] 录音通道 {name} 已创建");
        }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI] 录音通道 {channel} 创建失败: {ex.Message}");
        }
    }

    private static string SafeChannelName(byte channel)
    {
        try
        {
            string name = ((VoiceChatChannel)channel).ToString();
            if (!string.IsNullOrEmpty(name) && name.All(c => char.IsLetterOrDigit(c)))
                return name;
        }
        catch { /* 未知频道值，走兜底 */ }
        return "Ch" + channel;
    }

    // ────────────── 后台写盘线程 ──────────────

    private static void WriterLoop()
    {
        try
        {
            foreach (var frame in _queue!.GetConsumingEnumerable())
            {
                WriteFrame(frame);
            }
            // 队列耗尽：把各轨混合窗口剩余内容全部落盘（定稿前的最后一步）
            foreach (var track in Tracks.Values)
                FlushPending(track, track.PendingLen);
        }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI] 录音写盘线程异常终止: {ex.Message}");
        }
    }

    /// <summary>
    /// 按采样位置写入一帧到对应频道轨。帧间补静默保证 采样号 = 回合内秒 × 48000 = 文件位置；
    /// **同频道重叠帧（多人同时说话）逐采样混合**（求和钳制），任何一方都不丢失。
    /// 采用带混合窗口的延迟落盘：帧先混入内存窗口，只有未来帧不可能再触碰的
    /// 安全区（头部）才落盘（帧的 StartSample 随处理顺序单调不减，保证判定成立）。
    /// </summary>
    private static void WriteFrame(VoiceFrame frame)
    {
        if (!Tracks.TryGetValue(frame.Channel, out var track) || track.Writer == null || frame.Count <= 0)
            return;

        long start = frame.StartSample;

        if (start > track.PendingStart + MixWindowSamples)
        {
            // 帧离窗口太远（长时间无人说话）：整体落盘 + 补静默，从 start 重建窗口
            FlushPending(track, track.PendingLen);
            WriteSilence(track, start - track.SamplesWritten);
            track.PendingStart = start;
        }
        else if (start > track.PendingStart)
        {
            // 安全区：start 之前的样本不可能再被未来帧触碰，落盘
            FlushPending(track, (int)(start - track.PendingStart));
        }

        // 罕见长帧可能超出窗口右缘：先让出头部空间
        int offset = (int)(start - track.PendingStart);
        int need = offset + frame.Count;
        if (need > MixWindowSamples)
        {
            FlushPending(track, need - MixWindowSamples);
            offset = (int)(start - track.PendingStart);
        }

        // 混合进窗口：与已缓冲的同位置样本求和（钳制防溢出）
        int n = Math.Min(frame.Count, MixWindowSamples - offset);
        for (int i = 0; i < n; i++)
        {
            float v = frame.Data[i] * 32767f;
            short s = (short)Math.Max(-32768, Math.Min(32767, v));
            int sum = track.Pending[offset + i] + s;
            track.Pending[offset + i] = (short)Math.Max(-32768, Math.Min(32767, sum));
        }
        int end = offset + n;
        if (end > track.PendingLen) track.PendingLen = end;
    }

    /// <summary>把窗口头部 count 个采样落盘并左移剩余（只写安全区）。</summary>
    private static void FlushPending(ChannelTrack track, int count)
    {
        if (count <= 0 || track.Writer == null) return;
        count = Math.Min(count, track.PendingLen);
        for (int i = 0; i < count; i++)
            track.Writer.Write(track.Pending[i]);
        track.SamplesWritten += count;
        int rest = track.PendingLen - count;
        if (rest > 0)
            Array.Copy(track.Pending, count, track.Pending, 0, rest);
        track.PendingLen = rest;
        track.PendingStart += count;
    }

    private static void WriteSilence(ChannelTrack track, long count)
    {
        if (track.Writer == null || count <= 0) return;
        for (long i = 0; i < count; i++)
            track.Writer.Write((short)0);
        track.SamplesWritten += count;
    }

    private static void WriteWavHeader(BinaryWriter w)
    {
        // 占位 WAV 头（RIFF/data 尺寸留空，定稿时回填）
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0u);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16u);
        w.Write((ushort)1);            // PCM
        w.Write((ushort)1);            // 单声道
        w.Write(SampleRate);
        w.Write(SampleRate * BytesPerSample);
        w.Write((ushort)BytesPerSample);
        w.Write((ushort)(BytesPerSample * 8));
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(0u);
        w.Flush();
    }

    private static void PatchAndClose(ChannelTrack track)
    {
        var w = track.Writer;
        if (w == null) return;
        try
        {
            w.Seek(4, SeekOrigin.Begin);
            w.Write((uint)(36 + track.SamplesWritten * BytesPerSample));
            w.Seek(40, SeekOrigin.Begin);
            w.Write((uint)(track.SamplesWritten * BytesPerSample));
            w.Flush();
        }
        finally
        {
            w.Close();
            track.Writer = null;
        }
    }

    // ────────────── 时间轴日志 ──────────────

    private static void AppendTimelineHeader()
    {
        _timeline!.AppendLine("# SLDataAPI 语音时间轴（v2.5 Yagami Light）");
        _timeline.AppendLine($"# 局号: {_roundNumber}  回合开始: {_roundStart:yyyy-MM-dd HH:mm:ss.fff}  采样率: {SampleRate}Hz");
        _timeline.AppendLine("# 列: 回合内秒\t绝对时间\t事件\t昵称\tsteamid\t角色\t频道\tnetid\t详情");
        _timeline.AppendLine("# 对齐: 采样号 = 回合内秒 × 48000 = 任一频道 WAV 文件内精确位置（帧间已补静默，可直接切段取证）");
        _timeline.AppendLine("# 分轨: 每个语音频道（Proximity/Radio/Intercom/Spectator/Scp…）独立一个 WAV，频道间不混合；同频道多人同时说话才混合");
    }

    private static void AppendTimeline(string evt, string detail = "")
    {
        if (_timeline == null) return;
        string t = (Time.time - _roundStartTime).ToString("F3", CultureInfo.InvariantCulture);
        _timeline.Append(t).Append('\t').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append('\t')
                 .Append(evt);
        if (detail.Length > 0)
            _timeline.Append('\t').Append(detail);
        _timeline.AppendLine();
    }

    // ────────────── 旧局清理 ──────────────

    /// <summary>把文件名归到局基础名：去 .zip / .timeline.log / .wav 后缀，再去频道段（最后一个点之后）。</summary>
    private static string RoundBaseOf(string fileName)
    {
        string s = fileName;
        if (s.EndsWith(".zip", StringComparison.Ordinal))
            s = s.Substring(0, s.Length - 4);
        else if (s.EndsWith(".timeline.log", StringComparison.Ordinal))
            s = s.Substring(0, s.Length - ".timeline.log".Length);
        else if (s.EndsWith(".wav", StringComparison.Ordinal))
            s = s.Substring(0, s.Length - 4);
        int dot = s.LastIndexOf('.');
        return dot > 0 ? s.Substring(0, dot) : s;
    }

    private static void CleanupOldRounds()
    {
        if (_maxRounds <= 0) return; // 0 或负数 = 不清理
        try
        {
            string[] files = Directory.GetFiles(_dir, "voice_round_*");
            // 按局基础名分组（多频道 wav + 时间轴），按最近修改时间保留最新 N 局
            var groups = files
                .GroupBy(f => RoundBaseOf(Path.GetFileName(f)))
                .Select(g => new
                {
                    Base = g.Key,
                    Latest = g.Max(f => File.GetLastWriteTimeUtc(f)),
                })
                .OrderByDescending(x => x.Latest)
                .ToList();

            foreach (var old in groups.Skip(_maxRounds))
            {
                foreach (string f in files.Where(f => RoundBaseOf(Path.GetFileName(f)) == old.Base))
                {
                    try { File.Delete(f); } catch { /* 占用则跳过，下局再清 */ }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI] 录音旧局清理失败（忽略）: {ex.Message}");
        }
    }
}
