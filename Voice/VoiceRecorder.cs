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
using VoiceChat;

namespace SLDataAPI.Voice;

/// <summary>
/// 语音录音取证（v2.5 / FI-STM）：每局游戏自动保存为一个压缩包
///   voice_round_&lt;局号&gt;_&lt;开始时间&gt;.zip，内含：
///   1) 按频道分轨的音轨：&lt;局名&gt;.&lt;频道&gt;.wav（48kHz / 16bit / 单声道 PCM，
///      如 .Proximity / .Radio / .Intercom / .Scp …）
///   2) 时间轴日志：&lt;局名&gt;.timeline.log（谁在什么时候说了多久，含 steamid / 角色 / 频道）
/// 设计：
///   - **频道隔离**：SCP 频道与人类频道（近距离/对讲机/Intercom）是游戏里独立的听觉流，
///     分轨保存互不混合。
///   - **连续拼接（保真）**：音频按语音包到达顺序连续写入（累计样本数定位），与转发管线一致——
///     不受"处理时刻"的网络抖动影响，杜绝音质失真（按处理时刻定位会让每帧位置错乱，产生金属噪声）。
///     同一频道多人同时说话表现为交错拼接（与 WebUI 转发听感一致），任何一方的声音都不丢失。
///   - 时间轴以「文件内采样号」精确对齐（采样号 = WAV 内字节位置 ÷ 2），可精确定位/切段；
///     静默不占文件空间（墙钟秒仅作参考）。
///   - HandlePcm / OnSpeakerGone / BeginRound / EndRound 均由主线程调用；磁盘写入在独立
///     后台线程完成（开轨控制消息与音频帧同队列串行，无共享字典竞态）。
///   - 定稿与 zip 打包在后台线程完成（快照隔离，不占主线程、不阻塞下一局）；服务器停服时
///     同步定稿打包。按 voice_record_max_rounds 清理最旧局。
///   - 依赖语音管线运行（voice_enabled=true）；本类不自行订阅事件，由 Plugin 驱动。
/// </summary>
public static class VoiceRecorder
{
    private const int SampleRate = 48000;
    private const int BytesPerSample = 2;        // 16bit PCM
    private const int MaxQueuedFrames = 400;     // 有界队列（每帧缓冲 ≤23KB ≈ 9MB 待写上限），超出丢帧告警
    private const float BurstGapSeconds = 0.8f;  // 与语音转发一致：间隔超过视为新一轮讲话

    private static bool _enabled;
    private static int _maxRounds = 10;
    private static string _dir = "";

    private static int _roundNumber;
    private static DateTime _roundStart;
    private static string _baseName = "";

    // ★ 录音时序：高精度单调时钟（double 秒，仅用于时间轴/讲话段判定的参考时刻）+ 累计样本定位（连续拼接）
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static double Now => Clock.Elapsed.TotalSeconds;
    private static double _roundStartTime;
    private static long _cumulativeSamples; // 全局累计样本数（内容时长用，主线程独占）
    private static readonly Dictionary<byte, long> ChannelSamples = new(); // 每频道累计样本数（时间轴文件采样号按频道独立，与各轨 WAV 实际位置一致，主线程独占）

    private static BlockingCollection<object>? _queue;  // VoiceFrame（音频帧）与 VoiceOpenChannel（开轨控制消息）
    private static Thread? _writer;
    private static long _droppedFrames;
    private static double _lastDropWarn;

    private static StringBuilder? _timeline;
    private static Task? _finalizeTask; // 上一局的打包任务（Disable 时同步等待）

    // 讲话段状态（全部主线程访问）
    private static readonly Dictionary<uint, (double StartTime, long StartSample)> OpenBursts = new();
    private static readonly Dictionary<uint, double> LastPacket = new();
    private static readonly Dictionary<uint, (string Name, string UserId, string Role, byte Channel)> BurstMeta = new();

    // 本局已请求开轨的频道（主线程独占；写盘线程经控制消息串行建档，无共享字典）
    private static readonly HashSet<byte> PendingChannels = new();

    // 写盘线程结束时产出的本局轨道列表；主线程 EndRound 在 Join 成功后才读取（happens-before 保证）
    private static List<ChannelTrack>? _writerTracks;

    private struct VoiceFrame
    {
        public float[] Data;
        public int Count;
        public byte Channel;
    }

    /// <summary>开轨控制消息：写盘线程收到后创建频道文件（与音频帧同队列串行，天然无竞态）。</summary>
    private struct VoiceOpenChannel
    {
        public byte Channel;
    }

    private sealed class ChannelTrack
    {
        public byte Channel;
        public string FilePath = "";
        public BinaryWriter? Writer;
        public long SamplesWritten;
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
        _roundStartTime = Now;
        _baseName = $"voice_round_{_roundNumber}_{_roundStart:yyyyMMdd_HHmmss}";

        try
        {
            _timeline = new StringBuilder();
            AppendTimelineHeader();
            AppendTimeline("回合开始");
            _queue = new BlockingCollection<object>(MaxQueuedFrames);
            _droppedFrames = 0;
            _cumulativeSamples = 0;
            ChannelSamples.Clear();
            PendingChannels.Clear();
            // L-01n：每局闭包捕获独立 holder——跨局旧写盘线程只会写自己捕获的列表，
            // 不会覆盖新局的 _writerTracks（跨局覆盖窗口关闭）
            var holder = new List<ChannelTrack>();
            _writerTracks = holder;
            _writer = new Thread(() => WriterLoop(holder)) { IsBackground = true, Name = "SLDataAPI-VoiceRecorder" };
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
    /// 回合结束：关闭所有讲话段，把定稿工作（补头/打包 zip/清理旧局）快照后交给
    /// 后台线程执行——压缩大文件耗时，绝不能占主线程。waitFinalize=true 时（服务器停服）
    /// 同步定稿打包，防止进程退出打断。
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
                CloseBurst(netId, Now);

            roundEndSample = _cumulativeSamples;
            AppendTimeline("回合结束",
                $"内容时长={roundEndSample / (double)SampleRate:F3}s 丢帧={_droppedFrames} 终点采样={roundEndSample}");

            // 让写盘线程耗尽队列（含开轨消息）——必须 Join 成功，否则定稿会与其并发写/双重关闭。
            // waitFinalize（服务器停服）时给更长的等待窗口，尽力在进程退出前完成。
            _queue?.CompleteAdding();
            bool joined = _writer?.Join(waitFinalize ? 15000 : 3000) ?? true;
            if (!joined)
            {
                Log.Error(
                    $"[SLDataAPI] 录音写盘线程未及时退出，本局 {_baseName} 不定稿（散件文件保留在录音目录，下局清理兜底）");
                return;
            }

            // 写盘线程已退出并产出轨道列表（happens-before：Join）
            tracks = _writerTracks ?? new List<ChannelTrack>();
            foreach (var t in tracks)
                AppendTimeline("通道归档",
                    $"{SafeChannelName(t.Channel)}\t{Path.GetFileName(t.FilePath)}\t样本数={t.SamplesWritten}");
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
            PendingChannels.Clear();
            _queue = null;
            _writer = null;
            _writerTracks = null;
            _timeline = null;
        }

        // 定稿 + 打包（快照独立，与下一局完全隔离）。
        // waitFinalize（服务器停服）时同步执行：进程退出时 Task.Run 后台线程可能被强杀，导致 zip 丢失
        if (waitFinalize)
        {
            FinalizeRound(tracks, baseName, timelineText, roundEndSample);
        }
        else
        {
            var task = Task.Run(() => FinalizeRound(tracks, baseName, timelineText, roundEndSample));
            _finalizeTask = task;
        }
    }

    /// <summary>
    /// 后台线程：各频道轨补头/关闭 → 写时间轴 → 全部文件打入 zip → 删散件 → 清理旧局。
    /// 失败时散件文件保留在录音目录（日志提示），下局清理兜底。
    /// </summary>
    private static void FinalizeRound(List<ChannelTrack>? tracks, string baseName, string? timelineText, long roundEndSample)
    {
        try
        {
            // 1. 各轨补头并关闭（音频已是连续拼接，无需补静默）
            foreach (var track in tracks ?? Enumerable.Empty<ChannelTrack>())
                PatchAndClose(track);

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
    /// 每收到一个解码后的语音包：维护讲话段并连续拼接定位（累计样本数）、入队待写。
    /// pcm 数组由调用方新建（每包一个），本方法接管所有权，无需拷贝。
    /// </summary>
    public static void HandlePcm(uint netId, float[] pcm, int sampleCount,
        string nickname, string userId, string role, byte channel)
    {
        if (_queue == null) return;

        // X-01：清洗时间轴文本字段（剥 \r\n\t 与控制字符）——玩家昵称含换行/Tab
        // 可伪造或错位 TSV 证据条目，取证文件本身不能被玩家污染
        nickname = CleanField(nickname);
        userId = CleanField(userId);
        role = CleanField(role);

        double now = Now; // 录音内部时钟（高精度）

        // 首包到达该频道：主线程只记时间轴并请求写盘线程开轨（控制消息，与帧同队列串行）
        if (PendingChannels.Add(channel))
        {
            AppendTimeline("通道开始", $"{SafeChannelName(channel)}\t{channel}");
            if (!_queue.TryAdd(new VoiceOpenChannel { Channel = channel }))
            {
                PendingChannels.Remove(channel); // 开轨消息入队失败则撤销，下一包重试
                Interlocked.Increment(ref _droppedFrames);
            }
        }

        // 本帧在「所属频道文件内」的起始采样号 = 该频道累计样本数（连续拼接：与各轨 WAV 实际位置一致，
        // 时间轴的采样号可精确跳转到对应频道文件；全局累计仅用于内容时长）
        long startSample = ChannelSamples.TryGetValue(channel, out long ch) ? ch : 0;

        // 讲话段判定：距该说话者最近一包超过阈值视为新一轮讲话（先收尾上一段，再开新段）
        bool continuing = LastPacket.TryGetValue(netId, out double lastPkt) &&
                          now - lastPkt <= BurstGapSeconds;
        if (!continuing)
        {
            CloseBurst(netId, now);
            OpenBursts[netId] = (now, startSample);
            AppendTimeline("说话开始",
                $"{nickname}\t{userId}\t{role}\t{channel}\t{netId}\t文件采样号={startSample}");
        }
        LastPacket[netId] = now;
        BurstMeta[netId] = (nickname, userId, role, channel);

        if (_queue.TryAdd(new VoiceFrame { Data = pcm, Count = sampleCount, Channel = channel }))
        {
            // ★ R-01：只在实际入队的帧上推进记账——队列满丢帧时不推进，
            //   时间轴采样号与 WAV 实际位置保持严格一致（否则丢帧越多漂移越大）
            ChannelSamples[channel] = startSample + sampleCount;
            _cumulativeSamples += sampleCount;
        }
        else
        {
            Interlocked.Increment(ref _droppedFrames);
            if (now - _lastDropWarn > 5f)
            {
                _lastDropWarn = now;
                Log.Warn($"[SLDataAPI] 录音写入队列已满，丢弃音频帧（累计 {_droppedFrames}）——磁盘写入过慢，请检查录音目录 {_dir}");
            }
        }
    }

    /// <summary>说话者静默超时被清理（VoiceService.Cleanup 调用）：收尾其讲话段。</summary>
    public static void OnSpeakerGone(uint netId)
    {
        CloseBurst(netId, Now);
        LastPacket.Remove(netId);
    }

    private static void CloseBurst(uint netId, double now)
    {
        if (OpenBursts.TryGetValue(netId, out var burst))
        {
            if (BurstMeta.TryGetValue(netId, out var meta))
            {
                // F-02：终点采样用该说话者所属频道的累计样本数（与对应 WAV 文件位置一致）
                long chEnd = ChannelSamples.TryGetValue(meta.Channel, out long ce) ? ce : burst.StartSample;
                AppendTimeline("说话结束",
                    $"{meta.Name}\t{meta.UserId}\t{meta.Role}\t{meta.Channel}\t{netId}\t" +
                    $"时长={now - burst.StartTime:F3}s 文件起点采样={burst.StartSample} 文件终点采样={chEnd}");
            }
            OpenBursts.Remove(netId);
        }
        BurstMeta.Remove(netId);
    }

    // ────────────── 频道轨（写盘线程创建，与帧处理串行） ──────────────

    /// <summary>创建频道轨道文件并加入本局轨道表（写盘线程调用）。失败时不留半开句柄。</summary>
    private static void OpenChannel(byte channel, Dictionary<byte, ChannelTrack> trackMap)
    {
        ChannelTrack? track = null;
        FileStream? stream = null;
        try
        {
            string name = SafeChannelName(channel);
            track = new ChannelTrack
            {
                Channel = channel,
                FilePath = Path.Combine(_dir, $"{_baseName}.{name}.wav"),
            };
            stream = new FileStream(track.FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            track.Writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            stream = null; // 所有权已移交 BinaryWriter
            WriteWavHeader(track.Writer);
            trackMap[channel] = track;
            Log.Info($"[SLDataAPI] 录音通道 {name} 已创建");
        }
        catch (Exception ex)
        {
            // 防句柄泄漏：WriteWavHeader 或文件创建失败时释放已打开的流
            try { track?.Writer?.Dispose(); } catch { }
            try { stream?.Dispose(); } catch { }
            Log.Error($"[SLDataAPI] 录音通道 {channel} 创建失败: {ex.Message}");
        }
    }

    /// <summary>清洗时间轴文本字段：剥控制字符 / Tab / 换行（X-01，防证据污染）。</summary>
    private static string CleanField(string? s)
    {
        s ??= ""; // net48 的 IsNullOrEmpty 无 NotNullWhen 注解，编译器无法据此收窄可空性
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\r' || c == '\n' || c == '\t' || c < 0x20) continue;
            sb.Append(c);
        }
        return sb.ToString();
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

    /// <summary>
    /// 本局轨道表为写盘线程独占（无共享字典）：开轨控制消息与音频帧经同一队列串行处理，
    /// 动态注册对写盘线程天然可见。线程退出时把轨道产出给主线程（EndRound 在 Join 成功后才读取）。
    /// </summary>
    private static void WriterLoop(List<ChannelTrack> holder)
    {
        var trackMap = new Dictionary<byte, ChannelTrack>();
        try
        {
            foreach (var item in _queue!.GetConsumingEnumerable())
            {
                if (item is VoiceOpenChannel open)
                    OpenChannel(open.Channel, trackMap);
                else if (item is VoiceFrame frame)
                    WritePcm(frame, trackMap);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SLDataAPI] 录音写盘线程异常终止: {ex.Message}");
        }
        finally
        {
            // 产出到本局闭包捕获的 holder（跨局旧线程只写自己的列表，不碰静态状态）；
            // 不在此处 Close（定稿由 FinalizeRound 统一补头后关闭）。Join 超时跳过定稿时句柄由 GC 兜底。
            holder.AddRange(trackMap.Values);
        }
    }

    /// <summary>连续拼接写入一帧（float(-1..1) → 16bit 小端）。同一频道多人同时说话表现为交错拼接，与转发听感一致。</summary>
    private static void WritePcm(VoiceFrame frame, Dictionary<byte, ChannelTrack> trackMap)
    {
        if (!trackMap.TryGetValue(frame.Channel, out var track) || track.Writer == null || frame.Count <= 0)
        {
            // L-02n：落到无轨道的帧也计入丢帧（开轨失败/队列竞态的可见性），线程安全计数
            if (frame.Count > 0)
                Interlocked.Increment(ref _droppedFrames);
            return;
        }

        int n = Math.Min(frame.Count, frame.Data.Length);
        for (int i = 0; i < n; i++)
        {
            float v = frame.Data[i] * 32767f;
            short s = (short)Math.Max(-32768, Math.Min(32767, v));
            track.Writer.Write(s);
        }
        track.SamplesWritten += n;
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
        _timeline!.AppendLine("# SLDataAPI 语音时间轴（v2.5 FI-STM）");
        _timeline.AppendLine($"# 局号: {_roundNumber}  回合开始: {_roundStart:yyyy-MM-dd HH:mm:ss.fff}  采样率: {SampleRate}Hz");
        _timeline.AppendLine("# 列: 回合内秒\t绝对时间\t事件\t昵称\tsteamid\t角色\t频道\tnetid\t详情");
        _timeline.AppendLine("# 对齐: 采样号 = 所属频道 WAV 文件内字节位置 ÷ 2（连续拼接，静默不占文件空间；按频道累计，可精确跳转/切段）");
        _timeline.AppendLine("# 分轨: 每个语音频道（Proximity/Radio/Intercom/Spectator/Scp…）独立一个 WAV，频道间不混合");
    }

    private static void AppendTimeline(string evt, string detail = "")
    {
        if (_timeline == null) return;
        string t = (Now - _roundStartTime).ToString("F3", CultureInfo.InvariantCulture);
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
