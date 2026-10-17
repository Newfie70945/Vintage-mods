using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

/// <summary>
/// Small privilege-gated supporter perk. The server owns authorization/persistence;
/// the optional universal Player List companion renders the dynamic player light.
/// </summary>
public sealed class SupporterLampService : IDisposable
{
    public const string ChannelName = "newfies-supporter-lamp-v1";
    public const string PrivilegeCode = "supporterlamp";
    public const byte LightHue = 33;
    public const byte LightSaturation = 7;
    public const byte LightBrightness = 16; // clearly visible dynamic light while remaining below the brightest handheld sources
    public const string WatchedEnabledKey = "nssSupporterLampEnabled";
    public const string WatchedHueKey = "nssSupporterLampHue";
    public const string WatchedSaturationKey = "nssSupporterLampSaturation";
    public const string WatchedBrightnessKey = "nssSupporterLampBrightness";
    private const string StateFile = "NewfiesSupporterLampState.json";

    private readonly ICoreServerAPI api;
    private readonly HashSet<string> enabledUids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> readyClients = new(StringComparer.OrdinalIgnoreCase);
    private IServerNetworkChannel? channel;
    private bool started;

    public SupporterLampService(ICoreServerAPI api) => this.api = api;

    public void Start()
    {
        if (started) return;
        started = true;

        LoadState();
        api.Permissions.RegisterPrivilege(PrivilegeCode, "Allows the player to toggle the supporter /lamp light perk.");

        channel = api.Network.GetChannel(ChannelName) as IServerNetworkChannel
            ?? api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<SupporterLampHelloPacket>()
                .RegisterMessageType<SupporterLampStatePacket>();
        channel.SetMessageHandler<SupporterLampHelloPacket>(OnClientHello);

        if (api.ChatCommands.Get("lamp") == null)
        {
            api.ChatCommands.Create("lamp")
                .WithDescription("Toggle your supporter lamp.")
                .RequiresPlayer()
                .RequiresPrivilege(PrivilegeCode)
                .HandleWith(OnLampCommand);
        }
        else
        {
            api.Logger.Warning("[ServerEssentials/SupporterLamp] /lamp is already registered by another mod; supporter lamp command was not registered.");
        }

        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;

        api.Logger.Notification(
            "[ServerEssentials/SupporterLamp] Ready. Privilege={0}, light HSV={1}/{2}/{3}. Player state defaults OFF.",
            PrivilegeCode, LightHue, LightSaturation, LightBrightness);
    }

    private TextCommandResult OnLampCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        bool enabled;
        lock (enabledUids)
        {
            if (enabledUids.Contains(player.PlayerUID))
            {
                enabledUids.Remove(player.PlayerUID);
                enabled = false;
            }
            else
            {
                enabledUids.Add(player.PlayerUID);
                enabled = true;
            }
        }

        SaveState();
        PublishEntityState(player, enabled);
        BroadcastState(player.PlayerUID, enabled);

        bool rendererReady;
        lock (readyClients) rendererReady = readyClients.Contains(player.PlayerUID);
        if (enabled && !rendererReady)
        {
            return TextCommandResult.Success(
                "Supporter lamp enabled. Player List has not completed the optional lamp handshake yet; synchronized entity state will still be used when a compatible renderer is installed.");
        }

        return TextCommandResult.Success(enabled
            ? "Supporter lamp enabled. Use /lamp again to turn it off."
            : "Supporter lamp disabled.");
    }

    private void OnClientHello(IServerPlayer player, SupporterLampHelloPacket packet)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        if (packet == null || packet.ProtocolVersion != 1)
        {
            api.Logger.Warning("[ServerEssentials/SupporterLamp] Ignoring incompatible lamp renderer hello from {0}; protocol={1}.", player.PlayerName, packet?.ProtocolVersion ?? -1);
            return;
        }

        lock (readyClients) readyClients.Add(player.PlayerUID);
        api.Logger.Debug("[ServerEssentials/SupporterLamp] Lamp renderer ready for {0} ({1}).", player.PlayerName, player.PlayerUID);

        // Send the complete active set to the newly ready renderer.
        string[] active;
        lock (enabledUids) active = enabledUids.ToArray();
        foreach (string uid in active)
        {
            var target = api.World.PlayerByUid(uid) as IServerPlayer;
            if (target?.ConnectionState == EnumClientState.Playing && target.HasPrivilege(PrivilegeCode))
                SendState(player, uid, true);
        }
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        bool enabled;
        lock (enabledUids) enabled = enabledUids.Contains(player.PlayerUID);

        // Revoking the privilege also revokes any persisted enabled state.
        if (enabled && !player.HasPrivilege(PrivilegeCode))
        {
            lock (enabledUids) enabledUids.Remove(player.PlayerUID);
            SaveState();
            enabled = false;
        }

        PublishEntityState(player, enabled);
        BroadcastState(player.PlayerUID, enabled);
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        lock (readyClients) readyClients.Remove(player.PlayerUID);
        // Clear render state for other online clients, but keep the persisted toggle.
        PublishEntityState(player, false);
        BroadcastState(player.PlayerUID, false);
    }


    private static void PublishEntityState(IServerPlayer player, bool enabled)
    {
        try
        {
            if (player?.Entity?.WatchedAttributes == null) return;
            player.Entity.WatchedAttributes.SetBool(WatchedEnabledKey, enabled);
            player.Entity.WatchedAttributes.SetInt(WatchedHueKey, LightHue);
            player.Entity.WatchedAttributes.SetInt(WatchedSaturationKey, LightSaturation);
            player.Entity.WatchedAttributes.SetInt(WatchedBrightnessKey, LightBrightness);
        }
        catch
        {
            // The packet channel remains as a compatibility path if an unusual player entity
            // implementation rejects watched-attribute updates.
        }
    }

    private void BroadcastState(string playerUid, bool enabled)
    {
        if (channel == null || string.IsNullOrWhiteSpace(playerUid)) return;

        IServerPlayer[] recipients;
        lock (readyClients)
        {
            recipients = api.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .Where(p => p.ConnectionState == EnumClientState.Playing && readyClients.Contains(p.PlayerUID))
                .ToArray();
        }

        foreach (var recipient in recipients) SendState(recipient, playerUid, enabled);
    }

    private void SendState(IServerPlayer recipient, string playerUid, bool enabled)
    {
        try
        {
            channel?.SendPacket(new SupporterLampStatePacket
            {
                PlayerUid = playerUid,
                Enabled = enabled,
                Hue = LightHue,
                Saturation = LightSaturation,
                Brightness = LightBrightness
            }, recipient);
        }
        catch (Exception ex)
        {
            api.Logger.Debug("[ServerEssentials/SupporterLamp] State send to {0} skipped: {1}: {2}", recipient.PlayerName, ex.GetType().Name, ex.Message);
        }
    }

    private void LoadState()
    {
        try
        {
            var model = SuiteConfigStorage.Load<SupporterLampStateModel>(api, StateFile) ?? new SupporterLampStateModel();
            lock (enabledUids)
            {
                enabledUids.Clear();
                foreach (string uid in model.EnabledPlayerUids ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(uid)) enabledUids.Add(uid.Trim());
            }
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/SupporterLamp] Could not load {0}; starting with all lamps off. {1}: {2}", StateFile, ex.GetType().Name, ex.Message);
        }
    }

    private void SaveState()
    {
        try
        {
            string[] uids;
            lock (enabledUids) uids = enabledUids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            SuiteConfigStorage.Store(api, new SupporterLampStateModel { EnabledPlayerUids = uids.ToList() }, StateFile);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/SupporterLamp] Could not save {0}: {1}: {2}", StateFile, ex.GetType().Name, ex.Message);
        }
    }

    public void Dispose()
    {
        if (!started) return;
        started = false;
        try { api.Event.PlayerNowPlaying -= OnPlayerNowPlaying; } catch { }
        try { api.Event.PlayerDisconnect -= OnPlayerDisconnect; } catch { }
        lock (readyClients) readyClients.Clear();
    }

    private sealed class SupporterLampStateModel
    {
        public List<string> EnabledPlayerUids { get; set; } = new();
    }
}
