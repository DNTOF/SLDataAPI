using System;
using System.Collections.Generic;
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
            statusCallback: () => JsonConvert.SerializeObject(new { ok = true, message = "sample online" }),
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
            Logger.Warn($"[AdaptedPluginSample] register failed: {error}");
        else
            Logger.Info($"[AdaptedPluginSample] registered as {AdaptedId}");
    }

    public override void Disable()
    {
        PluginEndpointRegistry.Unregister(AdaptedId);
        Logger.Info($"[AdaptedPluginSample] unregistered {AdaptedId}");
    }
}
