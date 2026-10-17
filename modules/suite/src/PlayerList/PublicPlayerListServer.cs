using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BlockLogger.Admin;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerList;

/// <summary>
/// Public, read-only player-list bridge used by the separate Newfie's Player List
/// companion mod. The suite remains optional on clients.
///
/// The public roster reuses Player Management's online summary cache whenever the
/// Administration subsystem is available, keeping session/AFK/ping data consistent
/// across the suite. A world-player fallback remains available if Administration is off.
/// </summary>
public sealed class PublicPlayerListServer : IDisposable
{
    public const string ChannelName = "newfies-public-playerlist-v1";
    public const string ConfigFile = "NewfiesPlayerList.json";

    private const long SnapshotCacheMs = 750;
    private const long MinimumRequestIntervalMs = 750;

    private readonly ICoreServerAPI api;
    private readonly Func<AdminService?> getAdmin;
    private readonly global::System.Func<string, string, string>? resolveDisplayName;
    private readonly Dictionary<string, long> lastRequestAtByPlayer = new(StringComparer.Ordinal);
    private readonly object presenceGate = new();
    private readonly Dictionary<string, PublicPlayerListClientPresence> clientPresenceByUid = new(StringComparer.OrdinalIgnoreCase);

    private IServerNetworkChannel? channel;
    private List<PublicPlayerListEntry> cachedPlayers = new();
    private string cachedServerName = "";
    private string cachedRosterSource = "Fallback";
    private int cachedMaxPlayers;
    private long cachedSnapshotAtMs = long.MinValue;
    private bool disposed;
    private long requestsReceived;
    private long responsesSent;
    private long requestsThrottled;
    private long responseSendFailures;
    private long requestBuildFailures;
    private long clientHellosReceived;
    private long reservedSlotRejections;

    public long RequestsReceived => Interlocked.Read(ref requestsReceived);
    public long ResponsesSent => Interlocked.Read(ref responsesSent);
    public long RequestsThrottled => Interlocked.Read(ref requestsThrottled);
    public long ResponseSendFailures => Interlocked.Read(ref responseSendFailures);
    public long RequestBuildFailures => Interlocked.Read(ref requestBuildFailures);
    public long ClientHellosReceived => Interlocked.Read(ref clientHellosReceived);
    public long ReservedSlotRejections => Interlocked.Read(ref reservedSlotRejections);
    public int EffectiveReservedSlots => Config.ReservedSlotsEnabled
        ? Math.Clamp(Config.ReservedSlots, 0, Math.Max(0, api.Server.Config.MaxClients))
        : 0;
    public int PublicSlotLimit => Math.Max(0, Math.Max(0, api.Server.Config.MaxClients) - EffectiveReservedSlots);
    public int ReservedEligibleOnlineCount => CountOnlinePlayers(reservedOnly: true);
    public int PublicOnlineCount => CountOnlinePlayers(reservedOnly: false);
    public string RosterSource => getAdmin() != null ? "Player Management" : cachedRosterSource;
    public int CachedPlayerCount => cachedPlayers.Count;
    public long CachedSnapshotAgeMs => cachedSnapshotAtMs == long.MinValue
        ? -1
        : Math.Max(0, api.Server.ServerUptimeMilliseconds - cachedSnapshotAtMs);

    public PublicPlayerListConfig Config { get; private set; } = new();

    public PublicPlayerListServer(
        ICoreServerAPI api,
        Func<AdminService?> getAdmin,
        global::System.Func<string, string, string>? resolveDisplayName = null)
    {
        this.api = api;
        this.getAdmin = getAdmin;
        this.resolveDisplayName = resolveDisplayName;
    }

    /// <summary>Returns only an administrator-configured role override, or empty when no override applies.</summary>
    public string ResolveConfiguredRoleNameColor(IPlayerRole? role)
    {
        if (!Config.UseRoleNameColors || role == null) return string.Empty;
        return TryGetRoleColorOverride(role) ?? string.Empty;
    }

    /// <summary>Player List uses configured overrides first, then the native Vintage Story role color.</summary>
    public string ResolvePlayerListNameColor(IPlayerRole? role)
    {
        string configured = ResolveConfiguredRoleNameColor(role);
        return configured.Length > 0 ? configured : RoleColorHex(role);
    }

    public void Register()
    {
        LoadConfig();

        // Message types are normally pre-registered from ModSystem.Start(). Keep
        // this fallback so unusual loaders/reloads still produce a usable channel.
        channel = api.Network.GetChannel(ChannelName);
        if (channel == null)
        {
            channel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<PublicPlayerListRequest>()
                .RegisterMessageType<PublicPlayerListClientHello>()
                .RegisterMessageType<PublicPlayerListResponse>();
        }
        channel.SetMessageHandler<PublicPlayerListClientHello>(HandleClientHello);
        channel.SetMessageHandler<PublicPlayerListRequest>(HandleRequest);

        api.Event.PlayerJoin += OnPlayerJoin;
        api.Event.PlayerNowPlaying += OnPlayerRosterChanged;
        api.Event.PlayerDisconnect += OnPlayerDisconnected;

        api.Logger.Notification(
            "[ServerEssentials/PlayerList] Companion backend ready. Header={0}. Suite clients are not required.",
            string.IsNullOrWhiteSpace(Config.ServerName) ? "serverconfig name" : Config.ServerName);
    }

    public string DisplayServerName => string.IsNullOrWhiteSpace(Config.ServerName)
        ? (api.Server.Config.ServerName ?? "")
        : Config.ServerName;

    public void SetDisplaySettings(string? serverName, double backgroundOpacity, bool showRoles, bool showPing, bool showSessionTime)
    {
        var incoming = CloneConfig(Config);
        incoming.ServerName = serverName ?? "";
        incoming.BackgroundOpacity = backgroundOpacity;
        incoming.ShowRoles = showRoles;
        incoming.ShowPing = showPing;
        incoming.ShowSessionTime = showSessionTime;
        ApplyConfig(incoming);
    }

    public void SetServerName(string? serverName) =>
        SetDisplaySettings(serverName, Config.BackgroundOpacity, Config.ShowRoles, Config.ShowPing, Config.ShowSessionTime);

    public void ApplyConfig(PublicPlayerListConfig incoming)
    {
        incoming ??= new PublicPlayerListConfig();
        incoming.Clamp();

        bool appearanceChanged = AppearanceChanged(Config, incoming);
        Config = incoming;
        SaveConfig();

        if (appearanceChanged)
            InvalidateSnapshot();
    }

    /// <summary>
    /// Updates the shared role-name color overrides without moving ownership of the
    /// Player List configuration file. Chat Display exposes these settings in the
    /// WebPanel because they affect chat and roster presentation together.
    /// </summary>
    public void ApplyRoleNameColors(bool enabled, IDictionary<string, string>? colors)
    {
        var incoming = CloneConfig(Config);
        incoming.UseRoleNameColors = enabled;
        incoming.RoleNameColors = new Dictionary<string, string>(
            colors ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        ApplyConfig(incoming);
    }

    public IReadOnlyList<PublicPlayerListClientPresence> GetDetectedClients()
    {
        lock (presenceGate)
        {
            var result = new List<PublicPlayerListClientPresence>(clientPresenceByUid.Count);
            foreach (var p in clientPresenceByUid.Values)
            {
                result.Add(ClonePresence(p));
            }
            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.PlayerName, b.PlayerName));
            return result;
        }
    }

    public int DetectedClientCount
    {
        get
        {
            lock (presenceGate) return clientPresenceByUid.Count;
        }
    }

    /// <summary>
    /// Adds ephemeral companion-presence fields to the already-built Player Management
    /// summaries. This does not create a second player database; it only decorates the
    /// existing cached view before it is serialized to the web panel.
    /// </summary>
    public void DecorateClientPresence(IEnumerable<AdminPlayerSummary> summaries)
    {
        lock (presenceGate)
        {
            foreach (var summary in summaries)
            {
                summary.PlayerListClientDetected = false;
                summary.PlayerListClientVersion = "";
                if (!summary.Online || string.IsNullOrWhiteSpace(summary.PlayerUid)) continue;
                if (!clientPresenceByUid.TryGetValue(summary.PlayerUid, out var presence)) continue;

                summary.PlayerListClientDetected = true;
                summary.PlayerListClientVersion = presence.ClientVersion;
            }
        }
    }

    private void LoadConfig()
    {
        Config = ModConfigRecovery.LoadOrNull<PublicPlayerListConfig>(
            api,
            ConfigFile,
            "Public Player List") ?? new PublicPlayerListConfig();
        SaveConfig();
        InvalidateSnapshot();
    }

    private void SaveConfig()
    {
        Config.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, Config, ConfigFile);
    }

    private void HandleClientHello(IServerPlayer player, PublicPlayerListClientHello hello)
    {
        if (disposed || player == null) return;
        Interlocked.Increment(ref clientHellosReceived);
        RecordClientPresence(player, hello?.ClientVersion ?? "", hello?.ProtocolVersion ?? 0, hello?.Capabilities ?? "");
        if (Config.DebugLogging)
            api.Logger.Debug("[ServerEssentials/PlayerList] Hello from {0}: client={1}, protocol={2}, capabilities={3}",
                player.PlayerName, hello?.ClientVersion ?? "", hello?.ProtocolVersion ?? 0, hello?.Capabilities ?? "");
    }

    private void RecordClientPresence(IServerPlayer player, string version, int protocolVersion, string capabilities)
    {
        string uid = player.PlayerUID ?? "";
        if (string.IsNullOrWhiteSpace(uid)) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (presenceGate)
        {
            if (!clientPresenceByUid.TryGetValue(uid, out var presence))
            {
                presence = new PublicPlayerListClientPresence
                {
                    PlayerUid = uid,
                    FirstSeenUnixMilliseconds = now
                };
                clientPresenceByUid[uid] = presence;
            }

            presence.PlayerName = player.PlayerName ?? presence.PlayerName;
            if (!string.IsNullOrWhiteSpace(version)) presence.ClientVersion = version.Trim();
            if (protocolVersion > 0) presence.ProtocolVersion = protocolVersion;
            if (!string.IsNullOrWhiteSpace(capabilities)) presence.Capabilities = capabilities.Trim();
            presence.LastSeenUnixMilliseconds = now;
        }
    }

    private void HandleRequest(IServerPlayer requestingPlayer, PublicPlayerListRequest request)
    {
        if (disposed || requestingPlayer == null) return;

        Interlocked.Increment(ref requestsReceived);
        // A request itself proves some version of the companion is present. The
        // proactive hello should normally provide the exact version before this.
        RecordClientPresence(requestingPlayer, "", 0, "request");

        long now = api.Server.ServerUptimeMilliseconds;
        string requesterKey = string.IsNullOrWhiteSpace(requestingPlayer.PlayerUID)
            ? $"client:{requestingPlayer.ClientId}"
            : requestingPlayer.PlayerUID;

        if (lastRequestAtByPlayer.TryGetValue(requesterKey, out long previous) &&
            now - previous < MinimumRequestIntervalMs)
        {
            Interlocked.Increment(ref requestsThrottled);
            if (Config.DebugLogging)
                api.Logger.Debug("[ServerEssentials/PlayerList] Throttled request from {0}; delta={1}ms.", requestingPlayer.PlayerName, now - previous);
            return;
        }

        lastRequestAtByPlayer[requesterKey] = now;

        PublicPlayerListResponse response;
        try
        {
            EnsureSnapshot(now);
            response = BuildResponse(request.RequestId);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref requestBuildFailures);
            api.Logger.Warning(
                "[ServerEssentials/PlayerList] Failed to build public player list for {0}: {1}: {2}",
                requestingPlayer.PlayerName,
                ex.GetType().Name,
                ex.Message);

            response = new PublicPlayerListResponse
            {
                RequestId = request.RequestId,
                ServerName = DisplayServerName,
                MaxPlayers = Math.Max(0, api.Server.Config.MaxClients),
                Players = new List<PublicPlayerListEntry>(),
                BackgroundOpacity = (float)Config.BackgroundOpacity,
                ShowRoles = Config.ShowRoles,
                ShowPing = Config.ShowPing,
                ShowSessionTime = Config.ShowSessionTime,
                StyleVersion = 5,
                LayoutScale = (float)Config.LayoutScale,
                RowsPerColumn = Config.RowsPerColumn,
                MaxColumns = Config.MaxColumns,
                Alignment = Config.Alignment,
                OffsetX = Config.OffsetX,
                OffsetY = Config.OffsetY,
                ShowPlayerInitials = Config.ShowPlayerInitials,
                ShowCapacityBar = Config.ShowCapacityBar,
                ShowColumnHeaders = Config.ShowColumnHeaders,
                CompactRows = Config.CompactRows,
                ShowPingBars = Config.ShowPingBars,
                ShowFooterHint = Config.ShowFooterHint,
                AccentHex = Config.AccentHex,
                RosterSource = cachedRosterSource,
                BackendError = $"Suite backend failed to build the roster ({ex.GetType().Name}). Check server log.",
                BackendBuild = "suite-" + SuiteRelease.Version + "-playerlist-v5"
            };
        }

        var sendChannel = channel;
        if (sendChannel == null)
        {
            Interlocked.Increment(ref responseSendFailures);
            api.Logger.Warning("[ServerEssentials/PlayerList] Request received from {0}, but the server channel reference is unavailable.", requestingPlayer.PlayerName);
            return;
        }

        try
        {
            sendChannel.SendPacket(response, requestingPlayer);
            Interlocked.Increment(ref responsesSent);
            if (Config.DebugLogging)
                api.Logger.Debug("[ServerEssentials/PlayerList] Sent request {0} to {1}: players={2}, roster={3}.",
                    response.RequestId, requestingPlayer.PlayerName, response.Players?.Count ?? 0, response.RosterSource);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref responseSendFailures);
            api.Logger.Warning(
                "[ServerEssentials/PlayerList] Failed to send public player list to {0}: {1}: {2}",
                requestingPlayer.PlayerName,
                ex.GetType().Name,
                ex.Message);
        }
    }

    private PublicPlayerListResponse BuildResponse(long requestId)
    {
        return new PublicPlayerListResponse
        {
            RequestId = requestId,
            ServerName = cachedServerName,
            MaxPlayers = cachedMaxPlayers,
            Players = cachedPlayers,
            BackgroundOpacity = (float)Config.BackgroundOpacity,
            ShowRoles = Config.ShowRoles,
            ShowPing = Config.ShowPing,
            ShowSessionTime = Config.ShowSessionTime,
            StyleVersion = 5,
            LayoutScale = (float)Config.LayoutScale,
            RowsPerColumn = Config.RowsPerColumn,
            MaxColumns = Config.MaxColumns,
            Alignment = Config.Alignment,
            OffsetX = Config.OffsetX,
            OffsetY = Config.OffsetY,
            ShowPlayerInitials = Config.ShowPlayerInitials,
            ShowCapacityBar = Config.ShowCapacityBar,
            ShowColumnHeaders = Config.ShowColumnHeaders,
            CompactRows = Config.CompactRows,
            ShowPingBars = Config.ShowPingBars,
            ShowFooterHint = Config.ShowFooterHint,
            AccentHex = Config.AccentHex,
            RosterSource = cachedRosterSource,
            BackendError = "",
            BackendBuild = "suite-" + SuiteRelease.Version + "-playerlist-v5"
        };
    }

    private void EnsureSnapshot(long now)
    {
        if (cachedSnapshotAtMs != long.MinValue && now - cachedSnapshotAtMs < SnapshotCacheMs)
            return;

        var players = new List<PublicPlayerListEntry>();
        var admin = getAdmin();
        bool usedPlayerManagement = false;

        if (admin != null)
        {
            var summaries = admin.GetOnlinePlayerSummaries();
            // If Player Management is active, its online cache is the authoritative
            // source. Empty is still valid when the server genuinely has nobody online.
            usedPlayerManagement = true;
            players.Capacity = summaries.Count;
            foreach (var summary in summaries)
            {
                if (!summary.Online || string.IsNullOrWhiteSpace(summary.Name)) continue;
                var actual = FindOnlinePlayer(summary.PlayerUid, summary.Name);
                players.Add(new PublicPlayerListEntry
                {
                    PlayerName = ResolveDisplayName(summary.PlayerUid, summary.Name),
                    RoleName = actual?.Role?.Name ?? summary.Role ?? "Player",
                    NameColorHex = ResolvePlayerListNameColor(actual?.Role),
                    SortPrivilegeLevel = actual?.Role?.PrivilegeLevel ?? 0,
                    PingMs = summary.PingMilliseconds,
                    SessionSeconds = Math.Max(0, summary.SessionSeconds),
                    Afk = summary.Afk
                });
            }

            // Startup/singleplayer safety: if the Admin cache has not populated yet
            // but the world already exposes players, fall back for this snapshot.
            if (players.Count == 0 && api.World.AllOnlinePlayers.Length > 0)
                usedPlayerManagement = false;
        }

        if (!usedPlayerManagement)
        {
            players.Clear();
            players.Capacity = Math.Max(players.Capacity, api.World.AllOnlinePlayers.Length);
            foreach (var onlinePlayer in api.World.AllOnlinePlayers)
            {
                if (onlinePlayer is not IServerPlayer player || string.IsNullOrWhiteSpace(player.PlayerName))
                    continue;

                // Dedicated clients should normally be Playing. Singleplayer uses a
                // local client id/state path, so never reject ClientId <= 0 here.
                if (player.ClientId > 0 && player.ConnectionState != EnumClientState.Playing)
                    continue;

                players.Add(new PublicPlayerListEntry
                {
                    PlayerName = ResolveDisplayName(player.PlayerUID, player.PlayerName),
                    RoleName = player.Role?.Name ?? player.ServerData?.RoleCode ?? "Player",
                    NameColorHex = ResolvePlayerListNameColor(player.Role),
                    SortPrivilegeLevel = player.Role?.PrivilegeLevel ?? 0,
                    PingMs = ToPingMilliseconds(player.Ping),
                    SessionSeconds = -1,
                    Afk = false
                });
            }
        }

        players.Sort(CompareEntries);

        cachedPlayers = players;
        cachedServerName = DisplayServerName;
        cachedMaxPlayers = Math.Max(0, api.Server.Config.MaxClients);
        cachedRosterSource = usedPlayerManagement ? "Player Management" : "Fallback";
        cachedSnapshotAtMs = now;
    }

    private IServerPlayer? FindOnlinePlayer(string uid, string name)
    {
        foreach (var onlinePlayer in api.World.AllOnlinePlayers)
        {
            if (onlinePlayer is not IServerPlayer player) continue;
            if (!string.IsNullOrWhiteSpace(uid) && string.Equals(player.PlayerUID, uid, StringComparison.OrdinalIgnoreCase))
                return player;
            if (!string.IsNullOrWhiteSpace(name) && string.Equals(player.PlayerName, name, StringComparison.OrdinalIgnoreCase))
                return player;
        }
        return null;
    }

    private static int CompareEntries(PublicPlayerListEntry left, PublicPlayerListEntry right)
    {
        int result = right.SortPrivilegeLevel.CompareTo(left.SortPrivilegeLevel);
        if (result != 0) return result;

        result = StringComparer.OrdinalIgnoreCase.Compare(left.RoleName, right.RoleName);
        if (result != 0) return result;

        return StringComparer.OrdinalIgnoreCase.Compare(left.PlayerName, right.PlayerName);
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        if (disposed || player == null || !Config.ReservedSlotsEnabled) return;

        int maxClients = Math.Max(0, api.Server.Config.MaxClients);
        int reserved = Math.Clamp(Config.ReservedSlots, 0, maxClients);
        if (maxClients <= 0 || reserved <= 0 || IsReservedSlotEligible(player)) return;

        int publicLimit = Math.Max(0, maxClients - reserved);
        int otherPublicPlayers = 0;
        foreach (var online in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (string.Equals(online.PlayerUID, player.PlayerUID, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsReservedSlotEligible(online)) continue;
            otherPublicPlayers++;
        }

        if (otherPublicPlayers < publicLimit) return;

        Interlocked.Increment(ref reservedSlotRejections);
        string role = player.Role?.Code ?? player.ServerData?.RoleCode ?? "unknown";
        api.Logger.Notification(
            "[ServerEssentials/ReservedSlots] Rejected {0} ({1}); public capacity {2}/{2}, reserved={3}, max={4}.",
            player.PlayerName, role, publicLimit, reserved, maxClients);

        player.Disconnect(Config.ReservedSlotsFullMessage);
    }

    public bool IsReservedSlotEligible(IServerPlayer? player)
    {
        if (player == null || Config.ReservedSlotRoles == null || Config.ReservedSlotRoles.Count == 0) return false;

        string roleCode = (player.Role?.Code ?? player.ServerData?.RoleCode ?? "").Trim();
        string roleName = (player.Role?.Name ?? "").Trim();
        foreach (string configured in Config.ReservedSlotRoles)
        {
            string role = (configured ?? "").Trim();
            if (role.Length == 0) continue;
            if (roleCode.Equals(role, StringComparison.OrdinalIgnoreCase) ||
                roleName.Equals(role, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private int CountOnlinePlayers(bool reservedOnly)
    {
        int count = 0;
        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            bool eligible = IsReservedSlotEligible(player);
            if (reservedOnly ? eligible : !eligible) count++;
        }
        return count;
    }

    private void OnPlayerRosterChanged(IServerPlayer _)
    {
        InvalidateSnapshot();
    }

    private void OnPlayerDisconnected(IServerPlayer player)
    {
        InvalidateSnapshot();
        if (!string.IsNullOrWhiteSpace(player.PlayerUID))
        {
            lastRequestAtByPlayer.Remove(player.PlayerUID);
            lock (presenceGate) clientPresenceByUid.Remove(player.PlayerUID);
        }
        lastRequestAtByPlayer.Remove($"client:{player.ClientId}");
    }

    public void InvalidateDisplaySnapshot() => InvalidateSnapshot();

    private string ResolveDisplayName(string? uid, string? realName)
    {
        string fallback = realName ?? string.Empty;
        if (resolveDisplayName == null) return fallback;
        try { return resolveDisplayName(uid ?? string.Empty, fallback) ?? fallback; }
        catch { return fallback; }
    }

    private void InvalidateSnapshot()
    {
        cachedSnapshotAtMs = long.MinValue;
    }

    private static PublicPlayerListClientPresence ClonePresence(PublicPlayerListClientPresence source) => new()
    {
        PlayerUid = source.PlayerUid,
        PlayerName = source.PlayerName,
        ClientVersion = source.ClientVersion,
        ProtocolVersion = source.ProtocolVersion,
        Capabilities = source.Capabilities,
        FirstSeenUnixMilliseconds = source.FirstSeenUnixMilliseconds,
        LastSeenUnixMilliseconds = source.LastSeenUnixMilliseconds
    };

    private static int ToPingMilliseconds(float pingSeconds)
    {
        if (float.IsNaN(pingSeconds) || float.IsInfinity(pingSeconds) || pingSeconds < 0) return -1;
        return (int)Math.Clamp(Math.Round(pingSeconds * 1000d), 0d, 99_999d);
    }

    private static bool AppearanceChanged(PublicPlayerListConfig left, PublicPlayerListConfig right)
    {
        return !string.Equals(left.ServerName, right.ServerName, StringComparison.Ordinal) ||
               Math.Abs(left.BackgroundOpacity - right.BackgroundOpacity) > 0.0001 ||
               left.ShowRoles != right.ShowRoles ||
               left.ShowPing != right.ShowPing ||
               left.ShowSessionTime != right.ShowSessionTime ||
               Math.Abs(left.LayoutScale - right.LayoutScale) > 0.0001 ||
               left.RowsPerColumn != right.RowsPerColumn ||
               left.MaxColumns != right.MaxColumns ||
               !string.Equals(left.Alignment, right.Alignment, StringComparison.OrdinalIgnoreCase) ||
               left.OffsetX != right.OffsetX || left.OffsetY != right.OffsetY ||
               left.ShowPlayerInitials != right.ShowPlayerInitials ||
               left.ShowCapacityBar != right.ShowCapacityBar ||
               left.ShowColumnHeaders != right.ShowColumnHeaders ||
               left.CompactRows != right.CompactRows ||
               left.ShowPingBars != right.ShowPingBars ||
               left.ShowFooterHint != right.ShowFooterHint ||
               !string.Equals(left.AccentHex, right.AccentHex, StringComparison.OrdinalIgnoreCase) ||
               left.UseRoleNameColors != right.UseRoleNameColors ||
               !RoleColorMapsEqual(left.RoleNameColors, right.RoleNameColors);
    }

    private static PublicPlayerListConfig CloneConfig(PublicPlayerListConfig source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        ServerName = source.ServerName,
        BackgroundOpacity = source.BackgroundOpacity,
        ShowRoles = source.ShowRoles,
        ShowPing = source.ShowPing,
        ShowSessionTime = source.ShowSessionTime,
        LayoutScale = source.LayoutScale,
        RowsPerColumn = source.RowsPerColumn,
        MaxColumns = source.MaxColumns,
        Alignment = source.Alignment,
        OffsetX = source.OffsetX,
        OffsetY = source.OffsetY,
        ShowPlayerInitials = source.ShowPlayerInitials,
        ShowCapacityBar = source.ShowCapacityBar,
        ShowColumnHeaders = source.ShowColumnHeaders,
        CompactRows = source.CompactRows,
        ShowPingBars = source.ShowPingBars,
        ShowFooterHint = source.ShowFooterHint,
        AccentHex = source.AccentHex,
        UseRoleNameColors = source.UseRoleNameColors,
        RoleNameColors = new Dictionary<string, string>(source.RoleNameColors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        ReservedSlotsEnabled = source.ReservedSlotsEnabled,
        ReservedSlots = source.ReservedSlots,
        ReservedSlotRoles = new List<string>(source.ReservedSlotRoles ?? new List<string>()),
        ReservedSlotsFullMessage = source.ReservedSlotsFullMessage,
        DebugLogging = source.DebugLogging
    };

    private string? TryGetRoleColorOverride(IPlayerRole role)
    {
        var map = Config.RoleNameColors;
        if (map == null || map.Count == 0) return null;

        string code = (role.Code ?? "").Trim();
        if (code.Length > 0 && map.TryGetValue(code, out string? byCode) && !string.IsNullOrWhiteSpace(byCode))
            return byCode;

        string name = (role.Name ?? "").Trim();
        if (name.Length > 0 && map.TryGetValue(name, out string? byName) && !string.IsNullOrWhiteSpace(byName))
            return byName;

        return null;
    }

    private static string RoleColorHex(IPlayerRole? role)
    {
        if (role == null) return string.Empty;
        return $"#{role.Color.R:X2}{role.Color.G:X2}{role.Color.B:X2}";
    }

    private static bool RoleColorMapsEqual(Dictionary<string, string>? left, Dictionary<string, string>? right)
    {
        left ??= new Dictionary<string, string>();
        right ??= new Dictionary<string, string>();
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out string? value) ||
                !string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        api.Event.PlayerJoin -= OnPlayerJoin;
        api.Event.PlayerNowPlaying -= OnPlayerRosterChanged;
        api.Event.PlayerDisconnect -= OnPlayerDisconnected;
        lastRequestAtByPlayer.Clear();
        lock (presenceGate) clientPresenceByUid.Clear();
        cachedPlayers.Clear();
        channel = null;
    }
}
