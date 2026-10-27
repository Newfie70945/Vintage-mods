using NewfiesServerEssentials.Discord.Config;
using NewfiesServerEssentials.Discord.Discord;
using NewfiesServerEssentials.Discord.Integration;
using NewfiesServerEssentials.Discord.Linking;
using NewfiesServerEssentials.Discord.Relay;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace NewfiesServerEssentials.Discord;

public sealed class DiscordIntegrationModSystem : ModSystem
{
    public const string ConfigFile = "NewfiesServerEssentialsDiscord.json";
    public const string LegacyLinkConfigFile = "NewfiesDiscordLink.json";

    private ICoreServerAPI? sapi;
    private DiscordIntegrationConfig config = new();
    private DiscordBridgeService? discord;
    private DiscordLinkService? linkService;
    private DiscordRoleSyncService? roleSyncService;
    private GameRelayService? relay;
    private SuiteEventBridge? suiteBridge;
    private MotdDiscordProvider? motdProvider;
    public static MotdDiscordProvider? CurrentMotdProvider { get; private set; }
    private int serverStoppingRelayed;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        DiscordConfigStorage.InitializeAndRelocateLegacyFiles(api);
        LoadConfig();

        linkService = new DiscordLinkService(api, () => config.Linking);
        roleSyncService = new DiscordRoleSyncService(api, () => config.Linking.RoleSync, linkService);
        discord = new DiscordBridgeService(api, () => config, linkService, roleSyncService);
        roleSyncService.SetSyncRequester(discord.RequestRoleSyncAsync);
        relay = new GameRelayService(api, discord, () => config);
        suiteBridge = new SuiteEventBridge(api, discord, () => config, linkService, ReloadFromSuite, SaveFromSuite);
        motdProvider = new MotdDiscordProvider(discord, () => config,
            error => api.Logger.Warning("[NSE Discord] MOTD feed refresh failed; retaining the last successful feed and retrying later: {0}", error.Message));
        CurrentMotdProvider = motdProvider;

        linkService.Register();
        roleSyncService.Register();
        relay.Register();
        suiteBridge.Register();
        RegisterCommands(api);

        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            _ = SafeStartDiscordAsync();
            motdProvider?.QueueRefresh();
            RelayServerStarted();
        });

        api.Logger.Notification("[NSE Discord] Unified module 1.0.16 loaded. Config: {0}; linked accounts: {1}; role sync: {2}", DiscordConfigStorage.RelativePath(ConfigFile), linkService.LinkedAccountCount, config.Linking.RoleSync.Enabled ? "enabled" : "disabled");
    }

    public override void Dispose()
    {
        if (sapi != null)
        {
            RelayServerStoppingCritical();
        }

        if (ReferenceEquals(CurrentMotdProvider, motdProvider)) CurrentMotdProvider = null;
        motdProvider?.Dispose();
        relay?.Unregister();
        suiteBridge?.Unregister();
        roleSyncService?.Unregister();
        linkService?.Unregister();

        if (discord != null)
        {
            try { discord.StopAsync(shutdown: true).GetAwaiter().GetResult(); }
            catch { }
        }

        discord = null;
        linkService = null;
        roleSyncService = null;
        relay = null;
        suiteBridge = null;
        motdProvider = null;
        sapi = null;
        base.Dispose();
    }

    private void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands.Create("discord")
            .WithDescription("Shows the server Discord invite link.")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (string.IsNullOrWhiteSpace(config.InviteUrl))
                    return TextCommandResult.Success("Discord invite has not been configured yet.");
                return TextCommandResult.Success($"Join our Discord: {config.InviteUrl}");
            });

        api.ChatCommands.Create("discordstatus")
            .WithDescription("Shows Discord Integration status.")
            .RequiresPrivilege("controlserver")
            .HandleWith(args => TextCommandResult.Success(
                $"Discord enabled: {config.Enabled}; connected: {discord?.IsConnected == true}; bot: {discord?.ConnectedBotName ?? ""}; game channel: {config.Chat.ChannelId}; linking: {config.Linking.Enabled}; linked accounts: {linkService?.LinkedAccountCount ?? 0}; role sync: {config.Linking.RoleSync.Enabled}; managed roles: {roleSyncService?.ManagedPlayerCount ?? 0}; manual overrides: {roleSyncService?.SuspendedPlayerCount ?? 0}"));

        api.ChatCommands.Create("discordreload")
            .WithDescription("Reloads Newfie's Server Essentials Discord configuration.")
            .RequiresPrivilege("controlserver")
            .HandleWith(args =>
            {
                ReloadFromSuite();
                return TextCommandResult.Success("Discord Integration configuration reload started.");
            });
    }

    private void LoadConfig()
    {
        if (sapi == null) return;
        try
        {
            config = DiscordConfigStorage.Load<DiscordIntegrationConfig>(sapi, ConfigFile) ?? new DiscordIntegrationConfig();
            config.Linking ??= new DiscordLinkConfig();
            MigrateLegacyLinkConfigIfNeeded();
            // Normalize again after legacy migration because an older JSON file can explicitly
            // contain null for fields that did not exist when it was written.
            config.Linking ??= new DiscordLinkConfig();
            config.Linking.RoleSync ??= new DiscordRoleSyncConfig();
            config.Linking.RoleSync.EligibleBaseRoleCodes ??= ["suplayer"];
            config.Linking.RoleSync.Mappings ??= new List<DiscordRoleMapping>();
            config.Events ??= new DiscordIntegrationConfig.EventRoutingConfig();
            config.Events.PlayerDeath ??= new(false,0,"☠️ **{message}**");
            config.Events.TemporalStorm ??= new(false,0,"🌀 **{message}**");
            config.Motd ??= new DiscordIntegrationConfig.MotdFeedConfig();
            MigrateStockAnnouncementFormats();
            DiscordConfigStorage.Store(sapi, config, ConfigFile);
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[NSE Discord] Failed to load config; defaults will be used: {0}", e);
            config = new DiscordIntegrationConfig();
            DiscordConfigStorage.Store(sapi, config, ConfigFile);
        }
    }

    private void MigrateStockAnnouncementFormats()
    {
        if(config.Events.PlayerDeath.Format=="☠️ **{player} died**")config.Events.PlayerDeath.Format="☠️ **{message}**";
        // Upgrade only the exact historical stock strings. Any administrator-edited
        // custom format is preserved byte-for-byte.
        if (string.Equals(config.Events.PlayerJoin.Format, "🟢 **{player} joined the server**", StringComparison.Ordinal))
            config.Events.PlayerJoin.Format = "📥 ***Player {player} connected ({online}/{max})***";

        if (string.Equals(config.Events.PlayerLeave.Format, "🔴 **{player} left the server**", StringComparison.Ordinal))
            config.Events.PlayerLeave.Format = "📤 ***Player {player} disconnected ({online}/{max})***";

        // Player-name bolding is now controlled by the Suite Chat Display setting.
        // Upgrade only the exact historical stock format so custom formats remain untouched.
        if (string.Equals(config.Chat.GameToDiscordFormat, "**{player}**: {message}", StringComparison.Ordinal))
            config.Chat.GameToDiscordFormat = "{player}: {message}";

        // The [Discord] marker is now independently controlled by Chat.ShowDiscordTag.
        // Upgrade only the exact historical stock format so custom formats are untouched.
        if (string.Equals(config.Chat.DiscordToGameFormat, "[Discord] {user}: {message}", StringComparison.Ordinal))
            config.Chat.DiscordToGameFormat = "{user}: {message}";
    }

    private void MigrateLegacyLinkConfigIfNeeded()
    {
        if (sapi == null || config.LegacyDiscordLinkConfigMigrationComplete) return;

        try
        {
            var legacy = DiscordConfigStorage.Load<DiscordLinkConfig>(sapi, LegacyLinkConfigFile);
            if (legacy != null)
            {
                config.Linking = legacy;
                sapi.Logger.Notification("[NSE Discord] Migrated legacy {0} settings into {1}. The old standalone Discord Link mod can be removed.", LegacyLinkConfigFile, ConfigFile);
            }
        }
        catch (Exception e)
        {
            // A broken legacy file must not prevent the unified Discord module from starting.
            sapi.Logger.Warning("[NSE Discord] Could not migrate legacy Discord Link config; keeping unified defaults/current settings: {0}", e.Message);
        }

        config.LegacyDiscordLinkConfigMigrationComplete = true;
    }

    private void SaveFromSuite(bool restart)
    {
        if (sapi == null) return;
        DiscordConfigStorage.Store(sapi, config, ConfigFile);
        if (restart && discord != null)
        {
            _ = RestartDiscordAsync();
        }
    }

    private void ReloadFromSuite()
    {
        if (sapi == null || discord == null) return;
        sapi.Event.EnqueueMainThreadTask(() =>
        {
            LoadConfig();
            _ = RestartDiscordAsync();
        }, "nse-discord-reload");
    }

    private async Task RestartDiscordAsync()
    {
        if (discord == null) return;
        try
        {
            await discord.StopAsync();
            await discord.StartAsync();
            motdProvider?.QueueRefresh();
        }
        catch (Exception e)
        {
            sapi?.Logger.Error("[NSE Discord] Failed to restart Discord connection: {0}", e);
        }
    }

    private async Task SafeStartDiscordAsync()
    {
        if (discord == null) return;
        try { await discord.StartAsync(); }
        catch (Exception e) { sapi?.Logger.Error("[NSE Discord] Discord startup failed: {0}", e); }
    }

    private void RelayServerStarted()
    {
        if (discord == null) return;
        var evt = config.Events.ServerStarted;
        if (!config.Enabled || !evt.Enabled) return;
        discord.Enqueue(evt.ChannelId == 0 ? config.Chat.ChannelId : evt.ChannelId, evt.Format);
    }

    private void RelayServerStoppingCritical()
    {
        if (discord == null) return;
        if (Interlocked.Exchange(ref serverStoppingRelayed, 1) != 0) return;

        var evt = config.Events.ServerStopping;
        if (!config.Enabled || !evt.Enabled) return;

        ulong channelId = evt.ChannelId == 0 ? config.Chat.ChannelId : evt.ChannelId;
        if (channelId == 0) return;

        try
        {
            // Do not enqueue the final stopping notice. Dispose immediately tears down
            // the gateway, so a queued message can be lost before the worker sends it.
            // Give Discord a short bounded window to acknowledge the message, then
            // continue shutdown even if the network is slow or unavailable.
            discord.SendCriticalAsync(channelId, evt.Format, 2500).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            sapi?.Logger.Warning("[NSE Discord] Final server-stopping announcement could not be completed: {0}", e.Message);
        }
    }
}
