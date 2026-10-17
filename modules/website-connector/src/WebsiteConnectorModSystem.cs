using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Newfies.WebsiteConnector;

public sealed class WebsiteConnectorModSystem : ModSystem
{
    public const string ConfigFile = "NewfiesWebsiteConnector.json";
    private ICoreServerAPI? api;
    private ConnectorServer? server;
    private long tick = -1;
    private string gameVersion = "";
    private object[] mods = [];
    private byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(new { available = false, online = false }, ConnectorServer.Json);
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;
    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => Reload());
        api.ChatCommands.Create("websitelink").WithDescription("Confirms a private website account linking code using your current game identity.")
            .RequiresPlayer().RequiresPrivilege(Privilege.chat).WithArgs(api.ChatCommands.Parsers.Word("code"))
            .HandleWith(args => args.Caller.Player is IServerPlayer player && server != null
                ? TextCommandResult.Success(server.Links.Confirm(((string)args[0]).ToUpperInvariant(), player.PlayerUID, player.PlayerName))
                : TextCommandResult.Error("Website Connector is unavailable."));
        api.ChatCommands.Create("websitestatus").WithDescription("Shows Website Connector status without revealing its access token.")
            .RequiresPrivilege("controlserver").HandleWith(_ => TextCommandResult.Success(server == null ? "Website Connector is disabled or not configured." : server.Status + $" Game version={gameVersion}."));
        api.ChatCommands.Create("websitereload").WithDescription("Reloads the separate Website Connector configuration and supporter board.")
            .RequiresPrivilege("controlserver").HandleWith(_ => Reload() ? TextCommandResult.Success("Website Connector configuration reloaded.") : TextCommandResult.Error("Reload failed. Check the server log; the previous connector remains active if configuration validation failed."));
    }
    private bool Reload()
    {
        if (api == null) return false;
        ConnectorConfig config;
        try
        {
            config = api.LoadModConfig<ConnectorConfig>(ConfigFile) ?? new ConnectorConfig();
            try { config.Normalize(); }
            catch (ArgumentException e)
            {
                // Normalize messages are fixed validation text; never log raw config or tokens.
                api.Logger.Error("[WebsiteConnector] Invalid configuration: {0}", e.Message);
                return false;
            }
            api.StoreModConfig(config, ConfigFile);
        }
        catch (Exception e)
        {
            api.Logger.Error("[WebsiteConnector] Cannot load configuration ({0}). Validate IP, ports, token length and supporter fields.", e.GetType().Name);
            return false;
        }
        server?.Dispose(); server = null;
        if (tick >= 0) { api.Event.UnregisterGameTickListener(tick); tick = -1; }
        if (!config.Enabled) { api.Logger.Notification("[WebsiteConnector] Disabled by its own configuration."); return true; }
        try
        {
            gameVersion = RuntimeGameVersion.Read(typeof(GameVersion));
            if (gameVersion.Length == 0) api.Logger.Warning("[WebsiteConnector] Loaded game API did not provide a version; website version will be unavailable.");
            mods = api.ModLoader.Mods.Select(mod => (object)new { id = mod.Info.ModID, version = mod.Info.Version }).ToArray();
            Capture();
            server = new ConnectorServer(config, () => Volatile.Read(ref snapshot));
            server.Start();
            tick = api.Event.RegisterGameTickListener(_ => Capture(), 10000);
            api.Logger.Notification("[WebsiteConnector] Ready on {0}:{1}. Server-only read access; access token is stored in ModConfig/{2}.", config.BindAddress, config.Port, ConfigFile);
            return true;
        }
        catch (Exception e)
        {
            server?.Dispose(); server = null;
            if (tick >= 0) { api.Event.UnregisterGameTickListener(tick); tick = -1; }
            api.Logger.Error("[WebsiteConnector] Listener could not start ({0}). Check bind address and port conflicts.", e.GetType().Name); return false;
        }
    }
    private void Capture()
    {
        if (api == null) return;
        var body = JsonSerializer.SerializeToUtf8Bytes(new {
            available = true, online = true, players = api.World.AllOnlinePlayers.Length,
            maxPlayers = api.Server.Config.MaxClients, serverName = api.Server.Config.ServerName,
            mods,
            version = gameVersion, updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            source = "Newfies Website Connector", connectorVersion = ConnectorServer.Version
        }, ConnectorServer.Json);
        Volatile.Write(ref snapshot, body);
    }
    public override void Dispose()
    {
        if (api != null && tick >= 0) api.Event.UnregisterGameTickListener(tick);
        tick = -1; server?.Dispose(); server = null;
        base.Dispose();
    }
}
