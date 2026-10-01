// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;
using Newtonsoft.Json;
using SLDataAPI.Integrations;

namespace AdaptedPluginSample;

/// <summary>
/// Minimal LabAPI plugin that registers against SLDataAPI's PluginEndpointRegistry.
/// Deploy AdaptedPluginSample.dll next to SLDataAPI.dll under LabAPI/plugins/global.
/// </summary>
public class Plugin : LabApi.Loader.Features.Plugins.Plugin
{
    public const string AdaptedId = "dntof.sample_adapted";

    private static int _bumps;

    public override string Name => "AdaptedPluginSample";
    public override string Description => "SLDataAPI adapted-plugin registration sample (dntof.sample_adapted)";
    public override string Author => "DNT_OF";
    public override Version Version => new Version(1, 0, 0);
    public override Version RequiredApiVersion => new Version(1, 1, 7);

    public override void Enable()
    {
        bool ok = PluginEndpointRegistry.TryRegister(
            id: AdaptedId,
            name: "Adapted Plugin Sample",
            version: Version.ToString(),
            capabilities: new[] { "sample.hello" },
            statusCallback: () => JsonConvert.SerializeObject(new { ok = true, message = "sample online", bumps = Volatile.Read(ref _bumps) }),
            routes: new List<AdaptedRoute>
            {
                new AdaptedRoute
                {
                    Path = "hello",
                    Handler = _ => JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        plugin = AdaptedId,
                    }),
                },
            },
            out string error);

        if (!ok)
        {
            Logger.Warn($"[AdaptedPluginSample] register failed: {error}");
            return;
        }
        Logger.Info($"[AdaptedPluginSample] registered as {AdaptedId}");

        // [beta] write/action routes -> POST /control/adapted/dntof.sample_adapted/{echo|bump}
        // Older SLDataAPI builds have no TryRegisterActions: the call is isolated so the plugin still loads (read-only).
        try
        {
            RegisterActions();
        }
        catch (MissingMethodException)
        {
            Logger.Info("[AdaptedPluginSample] SLDataAPI too old for actions; running read-only");
        }
        catch (TypeLoadException)
        {
            Logger.Info("[AdaptedPluginSample] SLDataAPI too old for actions; running read-only");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterActions()
    {
        bool ok = PluginEndpointRegistry.TryRegisterActions(
            AdaptedId,
            new List<AdaptedAction>
            {
                new AdaptedAction
                {
                    Path = "echo",
                    Description = "Echo the request body back with caller info (no side effects)",
                    Handler = req => JsonConvert.SerializeObject(new
                    {
                        echo = Newtonsoft.Json.Linq.JToken.Parse(string.IsNullOrWhiteSpace(req.BodyJson) ? "{}" : req.BodyJson),
                        actor = req.Actor,
                        transport = req.Transport,
                    }),
                },
                new AdaptedAction
                {
                    Path = "bump",
                    Description = "Increment an in-memory counter (shown in status); body {\"by\":1..100}",
                    Handler = req =>
                    {
                        int by = 1;
                        try
                        {
                            var body = Newtonsoft.Json.Linq.JObject.Parse(string.IsNullOrWhiteSpace(req.BodyJson) ? "{}" : req.BodyJson);
                            if (body["by"] != null)
                                by = (int)body["by"]!;
                        }
                        catch (Exception)
                        {
                            throw new AdaptedActionException("body must be {\"by\": <int>}");
                        }
                        if (by < 1 || by > 100)
                            throw new AdaptedActionException("by must be 1..100");
                        int now = Interlocked.Add(ref _bumps, by);
                        return JsonConvert.SerializeObject(new { bumps = now });
                    },
                },
            },
            out string error);

        if (!ok)
            Logger.Warn($"[AdaptedPluginSample] action register failed: {error}");
        else
            Logger.Info($"[AdaptedPluginSample] actions registered: echo, bump");
    }

    public override void Disable()
    {
        PluginEndpointRegistry.Unregister(AdaptedId);
        Logger.Info($"[AdaptedPluginSample] unregistered {AdaptedId}");
    }
}
