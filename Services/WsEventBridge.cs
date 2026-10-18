// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System;
using System.Linq;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Arguments.WarheadEvents;
using Newtonsoft.Json.Linq;
using SLDataAPI.Control;
using Player = LabApi.Features.Wrappers.Player;

namespace SLDataAPI.Services;

/// <summary>
/// WS 事件流扩展：封禁 / 踢出 / 重生波次 / 去污 / 核弹 / 发电机 / SCP 收容播报 / 逃脱 / 角色变化 / 举报。
/// 只接低频事件（不接移动、开火、语音等高频事件）。每个处理器都吞掉异常：推送失败只丢事件，不影响游戏流程。
/// 事件数据不含 IP。
/// </summary>
public static class WsEventBridge
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        LabApi.Events.Handlers.PlayerEvents.Banned += OnBanned;
        LabApi.Events.Handlers.PlayerEvents.Kicked += OnKicked;
        LabApi.Events.Handlers.PlayerEvents.Escaped += OnEscaped;
        LabApi.Events.Handlers.PlayerEvents.ChangedRole += OnChangedRole;
        LabApi.Events.Handlers.PlayerEvents.ReportedPlayer += OnReportedPlayer;
        LabApi.Events.Handlers.PlayerEvents.ReportedCheater += OnReportedCheater;
        LabApi.Events.Handlers.ServerEvents.WaveRespawned += OnWaveRespawned;
        LabApi.Events.Handlers.ServerEvents.LczDecontaminationStarted += OnDecontaminationStarted;
        LabApi.Events.Handlers.ServerEvents.GeneratorActivated += OnGeneratorActivated;
        LabApi.Events.Handlers.ServerEvents.CassieQueuedScpTermination += OnScpTermination;
        LabApi.Events.Handlers.WarheadEvents.Started += OnWarheadStarted;
        LabApi.Events.Handlers.WarheadEvents.Stopped += OnWarheadStopped;
        LabApi.Events.Handlers.WarheadEvents.Detonated += OnWarheadDetonated;
    }

    public static void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        LabApi.Events.Handlers.PlayerEvents.Banned -= OnBanned;
        LabApi.Events.Handlers.PlayerEvents.Kicked -= OnKicked;
        LabApi.Events.Handlers.PlayerEvents.Escaped -= OnEscaped;
        LabApi.Events.Handlers.PlayerEvents.ChangedRole -= OnChangedRole;
        LabApi.Events.Handlers.PlayerEvents.ReportedPlayer -= OnReportedPlayer;
        LabApi.Events.Handlers.PlayerEvents.ReportedCheater -= OnReportedCheater;
        LabApi.Events.Handlers.ServerEvents.WaveRespawned -= OnWaveRespawned;
        LabApi.Events.Handlers.ServerEvents.LczDecontaminationStarted -= OnDecontaminationStarted;
        LabApi.Events.Handlers.ServerEvents.GeneratorActivated -= OnGeneratorActivated;
        LabApi.Events.Handlers.ServerEvents.CassieQueuedScpTermination -= OnScpTermination;
        LabApi.Events.Handlers.WarheadEvents.Started -= OnWarheadStarted;
        LabApi.Events.Handlers.WarheadEvents.Stopped -= OnWarheadStopped;
        LabApi.Events.Handlers.WarheadEvents.Detonated -= OnWarheadDetonated;
    }

    private static void Emit(string type, Func<JObject> build)
    {
        try
        {
            WsControlService.BroadcastEvent(type, build());
        }
        catch (Exception ex)
        {
            Log.Debug($"[SLDataAPI] {type} 事件推送异常（忽略）: {ex.Message}");
        }
    }

    /// <summary>玩家标识块：nickname / userid / player_id（不含 IP）。prefix 用于 issuer_ / target_ 等。</summary>
    private static void PutPlayer(JObject o, Player? p, string prefix = "")
    {
        if (p == null) return;
        o[prefix + "nickname"] = p.Nickname ?? "?";
        o[prefix + "userid"] = p.UserId ?? "";
        o[prefix + "player_id"] = p.PlayerId;
    }

    private static void OnBanned(PlayerBannedEventArgs ev) => Emit("player_banned", () =>
    {
        var o = new JObject();
        PutPlayer(o, ev.Player);
        // 离线封禁时 Player 为空：只在 PlayerId 是账号 ID（含 @）时回传，避免带出 IP
        if (ev.Player == null && !string.IsNullOrEmpty(ev.PlayerId) && ev.PlayerId.Contains("@"))
            o["userid"] = ev.PlayerId;
        PutPlayer(o, ev.Issuer, "issuer_");
        o["reason"] = ev.Reason ?? "";
        o["duration_seconds"] = ev.Duration;
        return o;
    });

    private static void OnKicked(PlayerKickedEventArgs ev) => Emit("player_kicked", () =>
    {
        var o = new JObject();
        PutPlayer(o, ev.Player);
        PutPlayer(o, ev.Issuer, "issuer_");
        o["reason"] = ev.Reason ?? "";
        return o;
    });

    private static void OnEscaped(PlayerEscapedEventArgs ev) => Emit("player_escaped", () =>
    {
        var o = new JObject();
        PutPlayer(o, ev.Player);
        o["old_role"] = ev.OldRole.ToString();
        o["new_role"] = ev.NewRole.ToString();
        o["scenario"] = ev.EscapeScenarioType.ToString();
        return o;
    });

    private static void OnChangedRole(PlayerChangedRoleEventArgs ev) => Emit("player_role_changed", () =>
    {
        var o = new JObject();
        PutPlayer(o, ev.Player);
        o["old_role"] = ev.OldRole.ToString();
        o["new_role"] = ev.NewRole?.RoleTypeId.ToString() ?? "";
        o["reason"] = ev.ChangeReason.ToString();
        return o;
    });

    private static void OnReportedPlayer(PlayerReportedPlayerEventArgs ev) => Emit("player_reported", () =>
        Report("player", ev.Player, ev.Target, ev.Reason));

    private static void OnReportedCheater(PlayerReportedCheaterEventArgs ev) => Emit("player_reported", () =>
        Report("cheater", ev.Player, ev.Target, ev.Reason));

    private static JObject Report(string kind, Player? reporter, Player? target, string? reason)
    {
        var o = new JObject { ["kind"] = kind, ["source"] = "game" };
        PutPlayer(o, reporter, "reporter_");
        PutPlayer(o, target, "target_");
        o["reason"] = reason ?? "";
        return o;
    }

    private static void OnWaveRespawned(WaveRespawnedEventArgs ev) => Emit("wave_respawned", () =>
    {
        var players = ev.Players?.Where(p => p != null).ToList() ?? new System.Collections.Generic.List<Player>();
        var arr = new JArray();
        foreach (var p in players)
        {
            var po = new JObject();
            PutPlayer(po, p);
            arr.Add(po);
        }
        return new JObject
        {
            ["faction"] = ev.Wave?.Faction.ToString() ?? "",
            ["count"] = players.Count,
            ["players"] = arr,
        };
    });

    private static void OnDecontaminationStarted() => Emit("decontamination_started", () => new JObject());

    private static void OnGeneratorActivated(GeneratorActivatedEventArgs ev) => Emit("generator_activated", () =>
    {
        int engaged = 0, total = 0;
        try
        {
            var all = LabApi.Features.Wrappers.Generator.List.Where(g => g != null).ToList();
            total = all.Count;
            engaged = all.Count(g => g.Engaged);
        }
        catch { /* 统计失败不影响推送 */ }
        return new JObject
        {
            ["room"] = ev.Generator?.Room?.Name.ToString() ?? "",
            ["engaged"] = engaged,
            ["total"] = total,
        };
    });

    private static void OnScpTermination(CassieQueuedScpTerminationEventArgs ev) => Emit("scp_terminated", () =>
    {
        var o = new JObject();
        PutPlayer(o, ev.Player);
        o["role"] = ev.Player?.Role.ToString() ?? "";
        o["announcement"] = ev.Announcement ?? "";
        return o;
    });

    private static void OnWarheadStarted(WarheadStartedEventArgs ev) => Emit("warhead_started", () =>
    {
        var o = new JObject { ["automatic"] = ev.IsAutomatic };
        PutPlayer(o, ev.Player);
        return o;
    });

    private static void OnWarheadStopped(WarheadStoppedEventArgs ev) => Emit("warhead_stopped", () =>
    {
        var o = new JObject();
        PutPlayer(o, ev.Player);
        return o;
    });

    private static void OnWarheadDetonated(WarheadDetonatedEventArgs ev) => Emit("warhead_detonated", () =>
    {
        var o = new JObject();
        PutPlayer(o, ev.Player);
        return o;
    });

    /// <summary>插件自带 SSS 举报提交成功后由 ReportService 调用（不含举报人 IP）。</summary>
    public static void EmitPluginReport(string id, string reporterName, string reporterId, string targetName, string targetId, string reason) =>
        Emit("player_reported", () => new JObject
        {
            ["kind"] = "player",
            ["source"] = "plugin",
            ["id"] = id,
            ["reporter_nickname"] = reporterName,
            ["reporter_userid"] = reporterId,
            ["target_nickname"] = targetName,
            ["target_userid"] = targetId,
            ["reason"] = reason,
        });
}
