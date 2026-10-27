using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace NewfiesPlayerList;

public sealed class PublicPlayerListClient : IDisposable
{
    public const string ChannelName = "newfies-public-playerlist-v1";
    public const string ClientVersion = "1.1.3";
    private const int RefreshIntervalMs = 2000;
    private const int BackendWarningAfterMs = 3000;
    private const int HelloRetryMs = 1000;
    private const int HelloMaxAttempts = 5;

    private readonly ICoreClientAPI api;
    private IClientNetworkChannel? channel;
    private PlayerListKeyHandler? keyHandler;
    private PublicPlayerListHud? hud;
    private long refreshListenerId = -1;
    private long helloListenerId = -1;
    private long requestId;
    private long lastAcceptedRequestId;
    private long lastRequestAtMs = long.MinValue;
    private long waitingForBackendSinceMs = long.MinValue;
    private bool backendWarningShown;
    private bool requestSentThisHold;
    private bool helloSent;
    private int helloAttempts;
    private bool disposed;
    private EnumChannelState lastChannelState = EnumChannelState.NotFound;

    public PublicPlayerListClient(ICoreClientAPI api)
    {
        this.api = api;
    }

    public void Register()
    {
        // Registering the optional suite channel is safe on a server that does not
        // advertise it. In that case the mod simply stays in standalone mode.
        channel = api.Network.GetChannel(ChannelName);
        if (channel == null)
        {
            channel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<PublicPlayerListRequest>()
                .RegisterMessageType<PublicPlayerListClientHello>()
                .RegisterMessageType<PublicPlayerListResponse>();
        }
        channel.SetMessageHandler<PublicPlayerListResponse>(OnResponse);

        lastChannelState = SafeChannelState();

        keyHandler = new PlayerListKeyHandler(api);
        keyHandler.Pressed += OnPlayerListPressed;
        keyHandler.Released += OnPlayerListReleased;

        hud = new PublicPlayerListHud(api, keyHandler);
        api.Gui.RegisterDialog(hud);

        api.Event.LevelFinalize += OnLevelFinalize;
        StartHelloHandshake();
        if (api.PlayerReadyFired)
        {
            EnsureHudOpen();
            TrySendHello();
        }

        api.Logger.Notification(
            "[NewfiesPlayerList] Ready. Hold the configured Player List key (Tab by default). Mode={0}.",
            lastChannelState == EnumChannelState.Connected ? "Suite-enhanced" : "Standalone client");
    }

    private void OnLevelFinalize()
    {
        helloSent = false;
        helloAttempts = 0;
        EnsureHudOpen();
        UpdateChannelState(logChange: true);
        StartHelloHandshake();
        TrySendHello();
    }

    private void StartHelloHandshake()
    {
        if (disposed || helloSent || helloListenerId >= 0) return;

        var state = UpdateChannelState(logChange: false);
        if (state == EnumChannelState.Connected)
        {
            TrySendHello();
            return;
        }

        // A registered channel may still be negotiating during world join. Retry
        // briefly in that case. A missing server channel is a normal standalone
        // configuration and must not create an idle retry loop or warning spam.
        if (state != EnumChannelState.Registered) return;
        helloAttempts = 0;
        helloListenerId = api.Event.RegisterGameTickListener(OnHelloTick, HelloRetryMs);
    }

    private void OnHelloTick(float _)
    {
        if (disposed || helloSent)
        {
            StopHelloHandshake();
            return;
        }

        var state = UpdateChannelState(logChange: false);
        if (state == EnumChannelState.NotConnected || state == EnumChannelState.NotFound)
        {
            StopHelloHandshake();
            return;
        }

        if (++helloAttempts >= HelloMaxAttempts && state != EnumChannelState.Connected)
        {
            StopHelloHandshake();
            return;
        }

        TrySendHello();
    }

    private void StopHelloHandshake()
    {
        if (helloListenerId < 0) return;
        api.Event.UnregisterGameTickListener(helloListenerId);
        helloListenerId = -1;
    }

    private bool TrySendHello()
    {
        if (disposed || helloSent || channel == null) return helloSent;
        if (UpdateChannelState(logChange: false) != EnumChannelState.Connected) return false;

        try
        {
            channel.SendPacket(new PublicPlayerListClientHello
            {
                ClientVersion = ClientVersion,
                ProtocolVersion = 1,
                Capabilities = "standalone-client-roster;optional-suite-enhancement;player-management-roster;session-time;afk;polished-roster-v3;ping-bars;layout-config;role-name-colors"
            });
            helloSent = true;
            StopHelloHandshake();
            api.Logger.Debug("[NewfiesPlayerList] Optional Suite enhancement channel connected.");
            return true;
        }
        catch (Exception ex)
        {
            api.Logger.Debug("[NewfiesPlayerList] Suite hello not ready: {0}: {1}", ex.GetType().Name, ex.Message);
            return false;
        }
    }

    private bool EnsureHudOpen()
    {
        if (disposed || hud == null) return false;
        if (hud.IsOpened()) return true;

        bool opened = false;
        try
        {
            hud.EnsureComposed();
            opened = hud.TryOpen(false);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[NewfiesPlayerList] Failed to open player-list HUD: {0}: {1}", ex.GetType().Name, ex.Message);
        }

        if (!opened)
            api.Logger.Debug("[NewfiesPlayerList] Player-list HUD was not opened yet; it will retry on the next Tab press.");

        return opened;
    }

    private void OnPlayerListPressed()
    {
        if (disposed || hud == null) return;

        EnsureHudOpen();
        requestSentThisHold = false;
        waitingForBackendSinceMs = long.MinValue;
        backendWarningShown = false;

        // Always render immediately from information Vintage Story already exposes
        // to the client. This is the complete standalone path and requires no server mod.
        UpdateStandaloneSnapshot();

        var state = UpdateChannelState(logChange: true);
        if (state == EnumChannelState.Connected)
        {
            TrySendHello();
            waitingForBackendSinceMs = api.ElapsedMilliseconds;
            RequestSnapshot(force: true);
        }
        else
        {
            StartHelloHandshake();
        }

        StartRefreshLoop();
    }

    private void OnPlayerListReleased()
    {
        StopRefreshLoop();
        waitingForBackendSinceMs = long.MinValue;
        backendWarningShown = false;
        requestSentThisHold = false;
    }

    private void StartRefreshLoop()
    {
        if (refreshListenerId >= 0) return;
        refreshListenerId = api.Event.RegisterGameTickListener(OnRefreshTick, RefreshIntervalMs);
    }

    private void StopRefreshLoop()
    {
        if (refreshListenerId < 0) return;
        api.Event.UnregisterGameTickListener(refreshListenerId);
        refreshListenerId = -1;
    }

    private void OnRefreshTick(float _)
    {
        if (keyHandler?.ValidateHeldState() != true)
        {
            StopRefreshLoop();
            return;
        }

        var state = UpdateChannelState(logChange: true);
        if (state != EnumChannelState.Connected)
        {
            UpdateStandaloneSnapshot();
            StartHelloHandshake();
            return;
        }

        TrySendHello();

        if (!requestSentThisHold)
        {
            waitingForBackendSinceMs = api.ElapsedMilliseconds;
            backendWarningShown = false;
            RequestSnapshot(force: true);
            return;
        }

        if (!backendWarningShown && waitingForBackendSinceMs != long.MinValue &&
            api.ElapsedMilliseconds - waitingForBackendSinceMs >= BackendWarningAfterMs)
        {
            backendWarningShown = true;
            // A Suite channel that stops responding must not break the player list.
            // Fall back locally and only leave a diagnostic in the client log.
            UpdateStandaloneSnapshot();
            api.Logger.Warning(
                "[NewfiesPlayerList] Optional Suite backend did not answer within {0} ms. Continuing in standalone mode.",
                BackendWarningAfterMs);
        }

        RequestSnapshot(force: false);
    }

    private EnumChannelState SafeChannelState()
    {
        try
        {
            return api.Network.GetChannelState(ChannelName);
        }
        catch
        {
            return EnumChannelState.NotFound;
        }
    }

    private EnumChannelState UpdateChannelState(bool logChange)
    {
        var state = SafeChannelState();
        if (state != lastChannelState)
        {
            if (logChange)
            {
                api.Logger.Notification(
                    "[NewfiesPlayerList] Optional Suite channel: {0} -> {1}. Player list remains available in standalone mode.",
                    lastChannelState,
                    state);
            }
            lastChannelState = state;
        }
        return state;
    }

    private void RequestSnapshot(bool force)
    {
        if (channel == null) return;
        if (UpdateChannelState(logChange: false) != EnumChannelState.Connected) return;

        long now = api.ElapsedMilliseconds;
        if (!force && now - lastRequestAtMs < RefreshIntervalMs - 100) return;

        lastRequestAtMs = now;
        try
        {
            channel.SendPacket(new PublicPlayerListRequest { RequestId = ++requestId });
            requestSentThisHold = true;
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[NewfiesPlayerList] Optional Suite roster request failed: {0}: {1}. Using standalone roster.", ex.GetType().Name, ex.Message);
            UpdateStandaloneSnapshot();
        }
    }

    private void OnResponse(PublicPlayerListResponse response)
    {
        if (disposed || response == null || response.RequestId < lastAcceptedRequestId) return;

        lastAcceptedRequestId = response.RequestId;
        waitingForBackendSinceMs = long.MinValue;
        backendWarningShown = false;

        if (!string.IsNullOrWhiteSpace(response.BackendError))
        {
            api.Logger.Warning("[NewfiesPlayerList] Optional Suite backend returned an error: {0}. Using standalone roster.", response.BackendError);
            UpdateStandaloneSnapshot();
            return;
        }

        hud?.UpdateSnapshot(response);
    }

    private void UpdateStandaloneSnapshot()
    {
        hud?.UpdateSnapshot(BuildStandaloneSnapshot());
    }

    private PublicPlayerListResponse BuildStandaloneSnapshot()
    {
        var players = new List<PublicPlayerListEntry>();
        try
        {
            var onlinePlayers = api.World?.AllOnlinePlayers;
            if (onlinePlayers != null)
            {
                players.Capacity = onlinePlayers.Length;
                foreach (var player in onlinePlayers)
                {
                    if (player == null || string.IsNullOrWhiteSpace(player.PlayerName)) continue;

                    players.Add(new PublicPlayerListEntry
                    {
                        PlayerName = player.PlayerName,
                        RoleName = player.Role?.Name ?? "Player",
                        NameColorHex = RoleColorHex(player.Role),
                        PingMs = -1,
                        SessionSeconds = -1,
                        Afk = false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            api.Logger.Debug("[NewfiesPlayerList] Standalone roster read failed: {0}: {1}", ex.GetType().Name, ex.Message);
        }

        players.Sort(static (left, right) =>
        {
            int role = StringComparer.OrdinalIgnoreCase.Compare(left.RoleName ?? "", right.RoleName ?? "");
            if (role != 0) return role;
            return StringComparer.OrdinalIgnoreCase.Compare(left.PlayerName ?? "", right.PlayerName ?? "");
        });

        return new PublicPlayerListResponse
        {
            RequestId = 0,
            ServerName = "Player List",
            MaxPlayers = 0,
            Players = players,
            BackgroundOpacity = 0.72f,
            ShowRoles = true,
            ShowPing = false,
            ShowSessionTime = false,
            StyleVersion = 5,
            LayoutScale = 1.0f,
            RowsPerColumn = 8,
            MaxColumns = 4,
            Alignment = "CenterTop",
            OffsetX = 0,
            OffsetY = 22,
            ShowPlayerInitials = true,
            ShowCapacityBar = false,
            ShowColumnHeaders = false,
            CompactRows = false,
            ShowPingBars = false,
            ShowFooterHint = false,
            AccentHex = "#D6AE63",
            RosterSource = "Vintage Story client roster",
            BackendError = "",
            BackendBuild = "standalone-client-1.1.3"
        };
    }

    private static string RoleColorHex(Vintagestory.API.Common.IPlayerRole? role)
    {
        if (role == null) return string.Empty;
        return $"#{role.Color.R:X2}{role.Color.G:X2}{role.Color.B:X2}";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        api.Event.LevelFinalize -= OnLevelFinalize;

        if (keyHandler != null)
        {
            keyHandler.Pressed -= OnPlayerListPressed;
            keyHandler.Released -= OnPlayerListReleased;
        }

        StopRefreshLoop();
        StopHelloHandshake();

        if (hud != null)
        {
            hud.Dispose();
            api.Gui.LoadedGuis.Remove(hud);
        }
        hud = null;

        keyHandler?.Dispose();
        keyHandler = null;
        channel = null;
    }
}
