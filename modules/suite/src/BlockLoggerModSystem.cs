using System;
using BlockLogger.Config;
using BlockLogger.Frontend;
using BlockLogger.PlayerList;
using BlockLogger.Commands;
using BlockLogger.Services;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Packets;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger;

public sealed class BlockLoggerModSystem : ModSystem
{
    private ICoreServerAPI? serverApi;
    private readonly BlockLoggerConfig config = new();
    private SuiteFeatureConfig features = new();
    private BlockLoggerModule? module;
    private InvestigatorClient? investigatorClient;
    private EventBusListenerDelegate? motdStatusListener;
    private const string MotdStatusEvent = "newfiesserveressentials:motd-status";

    public override bool ShouldLoad(EnumAppSide side) => true;

    public override void Start(ICoreAPI api)
    {
        // Register the public player-list protocol on both sides during the common Start stage.
        // The server advertises this channel during custom-channel negotiation, so any client
        // loading Server Essentials must register the same message types even when it does not
        // use the optional public-player-list UI.  Server-side handlers are still installed only
        // by PublicPlayerListServer.Register(); this client registration is protocol-only.
        if (api.Network.GetChannel(PublicPlayerListServer.ChannelName) == null)
        {
            api.Network.RegisterChannel(PublicPlayerListServer.ChannelName)
                .RegisterMessageType<PublicPlayerListRequest>()
                .RegisterMessageType<PublicPlayerListClientHello>()
                .RegisterMessageType<PublicPlayerListResponse>();
        }

        // Ghost visibility is transported on a tiny dedicated channel owned by Server Essentials.
        // The required Client Integrity companion consumes it client-side and removes ghosted
        // remote player entities from the client LoadedEntities cache, preventing entity scanners
        // such as BlockOverlay from seeing the admin at all.
        if (api.Side == EnumAppSide.Server && api.Network.GetChannel(SupporterLampService.ChannelName) == null)
        {
            api.Network.RegisterChannel(SupporterLampService.ChannelName)
                .RegisterMessageType<SupporterLampHelloPacket>()
                .RegisterMessageType<SupporterLampStatePacket>();
        }

        if (api.Side == EnumAppSide.Server && api.Network.GetChannel(GhostVisibilityProtocol.ChannelName) == null)
        {
            api.Network.RegisterChannel(GhostVisibilityProtocol.ChannelName)
                .RegisterMessageType<GhostVisibilityPacket>();
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        serverApi = api;
        SuiteConfigStorage.InitializeAndRelocateLegacyFiles(api);
        LoadConfig();
        features = SuiteFeatureConfig.Load(api);
        var activeFeatures = features.Clone();
        new SuiteFeatureCommands(api, features, activeFeatures).Register();

        foreach (var loadedMod in api.ModLoader.Mods)
        {
            if (loadedMod?.Info == null ||
                !string.Equals(loadedMod.Info.ModID, "blocklogger", StringComparison.OrdinalIgnoreCase))
                continue;

            api.Logger.Error(
                "[ServerEssentials] Legacy BlockLogger conflict detected ({0}). Remove the old blocklogger/NewfiesBlockloggingSuite ZIP before running Newfies Server Essentials Suite; both mods otherwise execute together and can duplicate logging, diagnostics, and Harmony integrations.",
                loadedMod.FileName ?? "blocklogger");
            break;
        }

        foreach (var loadedMod in api.ModLoader.Mods)
        {
            if (loadedMod?.Info == null ||
                !string.Equals(loadedMod.Info.ModID, "newfiesanticheatserver", StringComparison.OrdinalIgnoreCase))
                continue;

            api.Logger.Error(
                "[ServerEssentials] Standalone Newfie's Anti-Cheat Server conflict detected ({0}). Anti-cheat server networking is integrated into Server Essentials Suite as of 1.5.7. Remove the standalone newfiesanticheatserver ZIP to avoid duplicate ownership of the newfiesanticheat channel.",
                loadedMod.FileName ?? "newfiesanticheatserver");
            break;
        }

        module = new BlockLoggerModule(api, config, activeFeatures, features, ReloadConfig);
        module.Start();

        motdStatusListener = OnMotdStatusRequest;
        api.Event.RegisterEventBusListener(motdStatusListener, filterByEventName: MotdStatusEvent);

        if (activeFeatures.IngameAdminGui)
        {
            var investigator = new InvestigatorServerBridge(api, module);
            investigator.Register();
        }
        else
        {
            api.Logger.Notification("[ServerEssentials] In-game administration GUI disabled by {0}.", SuiteFeatureConfig.ConfigFile);
        }

        api.Logger.Notification(
            "[ServerEssentials] Feature gates active. Disabled={0}",
            string.IsNullOrWhiteSpace(activeFeatures.DisabledSummary()) ? "none" : activeFeatures.DisabledSummary());
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        // The public Tab player list is intentionally a separate companion mod as of 1.5.47.
        // Keeping it out of this client entry point lets requiredOnClient remain false for the suite.
        investigatorClient = new InvestigatorClient(api);
        investigatorClient.Register();
    }

    public override void Dispose()
    {
        TryUnregisterMotdStatusListener();
        investigatorClient?.Dispose();
        investigatorClient = null;

        module?.Dispose();
        module = null;
        base.Dispose();
    }


    private void OnMotdStatusRequest(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (serverApi == null || data is not ITreeAttribute tree) return;
        tree.SetBool("available", true);
        tree.SetInt("protocol", 1);
        int online = serverApi.World.AllOnlinePlayers?.Length ?? 0;
        tree.SetString(
            "vtml",
            $"<strong>Suite:</strong> <font color='#8FDA72'>Online</font> · {online} player{(online == 1 ? "" : "s")} connected");
        handling = EnumHandling.PreventSubsequent;
    }

    private void TryUnregisterMotdStatusListener()
    {
        if (serverApi == null || motdStatusListener == null) return;
        try
        {
            var method = serverApi.Event.GetType().GetMethod("UnregisterEventBusListener", new[] { typeof(EventBusListenerDelegate) });
            method?.Invoke(serverApi.Event, new object[] { motdStatusListener });
        }
        catch { }
        motdStatusListener = null;
    }

    private void ReloadConfig()
    {
        LoadConfig();
        module?.ApplyRuntimeSettings();
        serverApi?.Logger.Notification("[BlockLogger] Configuration reloaded.");
    }

    private void LoadConfig()
    {
        if (serverApi == null) return;

        const string configPath = "NewfiesBlocklogger.json";
        const string legacyConfigPath = "blocklogger-v2.json";

        // Prefer the new Newfie's-prefixed filename so BlockLogger sits beside
        // NewfiesAnimalLimiter.json in the server config directory.
        var loaded = ModConfigRecovery.LoadOrNull<BlockLoggerConfig>(
            serverApi,
            configPath,
            "BlockLogger");
        if (loaded == null)
        {
            // Preserve existing server settings on upgrade. The legacy file is
            // read once when the new file does not yet exist, then the normalized
            // config is written to NewfiesBlocklogger.json.
            loaded = ModConfigRecovery.LoadOrNull<BlockLoggerConfig>(
                serverApi,
                legacyConfigPath,
                "BlockLogger");
            if (loaded != null)
            {
                serverApi.Logger.Notification(
                    "[BlockLogger] Migrating configuration from {0} to {1}.",
                    legacyConfigPath, configPath);
            }
        }

        loaded ??= new BlockLoggerConfig();

        // Schema migrations are applied centrally by ConfigMigrationService before this point.
        loaded.SchemaVersion = Math.Max(BlockLoggerConfig.CurrentSchemaVersion, loaded.SchemaVersion);
        loaded.Clamp();
        config.ApplyFrom(loaded);
        BlockLogger.Services.SuiteConfigStorage.Store(serverApi, config, configPath);
    }
}
