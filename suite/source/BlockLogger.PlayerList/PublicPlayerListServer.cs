using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BlockLogger.Admin;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerList;

public sealed class PublicPlayerListServer : IDisposable
{
	public const string ChannelName = "newfies-public-playerlist-v1";

	public const string ConfigFile = "NewfiesPlayerList.json";

	private const long SnapshotCacheMs = 750L;

	private const long MinimumRequestIntervalMs = 750L;

	private readonly ICoreServerAPI api;

	private readonly Func<AdminService?> getAdmin;

	private readonly System.Func<string, string, string>? resolveDisplayName;

	private readonly Dictionary<string, long> lastRequestAtByPlayer = new Dictionary<string, long>(StringComparer.Ordinal);

	private readonly object presenceGate = new object();

	private readonly Dictionary<string, PublicPlayerListClientPresence> clientPresenceByUid = new Dictionary<string, PublicPlayerListClientPresence>(StringComparer.OrdinalIgnoreCase);

	private IServerNetworkChannel? channel;

	private List<PublicPlayerListEntry> cachedPlayers = new List<PublicPlayerListEntry>();

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

	public long RequestsReceived => Interlocked.Read(in requestsReceived);

	public long ResponsesSent => Interlocked.Read(in responsesSent);

	public long RequestsThrottled => Interlocked.Read(in requestsThrottled);

	public long ResponseSendFailures => Interlocked.Read(in responseSendFailures);

	public long RequestBuildFailures => Interlocked.Read(in requestBuildFailures);

	public long ClientHellosReceived => Interlocked.Read(in clientHellosReceived);

	public long ReservedSlotRejections => Interlocked.Read(in reservedSlotRejections);

	public int EffectiveReservedSlots => Config.ReservedSlotsEnabled ? Math.Clamp(Config.ReservedSlots, 0, Math.Max(0, api.Server.Config.MaxClients)) : 0;

	public int PublicSlotLimit => Math.Max(0, Math.Max(0, api.Server.Config.MaxClients) - EffectiveReservedSlots);

	public int ReservedEligibleOnlineCount => CountOnlinePlayers(reservedOnly: true);

	public int PublicOnlineCount => CountOnlinePlayers(reservedOnly: false);

	public string RosterSource => (getAdmin() != null) ? "Player Management" : cachedRosterSource;

	public int CachedPlayerCount => cachedPlayers.Count;

	public long CachedSnapshotAgeMs => (cachedSnapshotAtMs == long.MinValue) ? (-1) : Math.Max(0L, api.Server.ServerUptimeMilliseconds - cachedSnapshotAtMs);

	public PublicPlayerListConfig Config { get; private set; } = new PublicPlayerListConfig();

	public string DisplayServerName => string.IsNullOrWhiteSpace(Config.ServerName) ? (api.Server.Config.ServerName ?? "") : Config.ServerName;

	public int DetectedClientCount
	{
		get
		{
			lock (presenceGate)
			{
				return clientPresenceByUid.Count;
			}
		}
	}

	public PublicPlayerListServer(ICoreServerAPI api, Func<AdminService?> getAdmin, System.Func<string, string, string>? resolveDisplayName = null)
	{
		this.api = api;
		this.getAdmin = getAdmin;
		this.resolveDisplayName = resolveDisplayName;
	}

	public string ResolveConfiguredRoleNameColor(IPlayerRole? role)
	{
		if (!Config.UseRoleNameColors || role == null)
		{
			return string.Empty;
		}
		return TryGetRoleColorOverride(role) ?? string.Empty;
	}

	public string ResolvePlayerListNameColor(IPlayerRole? role)
	{
		string text = ResolveConfiguredRoleNameColor(role);
		return (text.Length > 0) ? text : RoleColorHex(role);
	}

	public void Register()
	{
		LoadConfig();
		channel = api.Network.GetChannel("newfies-public-playerlist-v1");
		if (channel == null)
		{
			channel = api.Network.RegisterChannel("newfies-public-playerlist-v1").RegisterMessageType<PublicPlayerListRequest>().RegisterMessageType<PublicPlayerListClientHello>()
				.RegisterMessageType<PublicPlayerListResponse>();
		}
		channel.SetMessageHandler<PublicPlayerListClientHello>(HandleClientHello);
		channel.SetMessageHandler<PublicPlayerListRequest>(HandleRequest);
		api.Event.PlayerJoin += OnPlayerJoin;
		api.Event.PlayerNowPlaying += OnPlayerRosterChanged;
		api.Event.PlayerDisconnect += OnPlayerDisconnected;
		api.Logger.Notification("[ServerEssentials/PlayerList] Companion backend ready. Header={0}. Suite clients are not required.", string.IsNullOrWhiteSpace(Config.ServerName) ? "serverconfig name" : Config.ServerName);
	}

	public void SetDisplaySettings(string? serverName, double backgroundOpacity, bool showRoles, bool showPing, bool showSessionTime)
	{
		PublicPlayerListConfig publicPlayerListConfig = CloneConfig(Config);
		publicPlayerListConfig.ServerName = serverName ?? "";
		publicPlayerListConfig.BackgroundOpacity = backgroundOpacity;
		publicPlayerListConfig.ShowRoles = showRoles;
		publicPlayerListConfig.ShowPing = showPing;
		publicPlayerListConfig.ShowSessionTime = showSessionTime;
		ApplyConfig(publicPlayerListConfig);
	}

	public void SetServerName(string? serverName)
	{
		SetDisplaySettings(serverName, Config.BackgroundOpacity, Config.ShowRoles, Config.ShowPing, Config.ShowSessionTime);
	}

	public void ApplyConfig(PublicPlayerListConfig incoming)
	{
		if (incoming == null)
		{
			incoming = new PublicPlayerListConfig();
		}
		incoming.Clamp();
		bool flag = AppearanceChanged(Config, incoming);
		Config = incoming;
		SaveConfig();
		if (flag)
		{
			InvalidateSnapshot();
		}
	}

	public void ApplyRoleNameColors(bool enabled, IDictionary<string, string>? colors)
	{
		PublicPlayerListConfig publicPlayerListConfig = CloneConfig(Config);
		publicPlayerListConfig.UseRoleNameColors = enabled;
		publicPlayerListConfig.RoleNameColors = new Dictionary<string, string>(colors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
		ApplyConfig(publicPlayerListConfig);
	}

	public IReadOnlyList<PublicPlayerListClientPresence> GetDetectedClients()
	{
		lock (presenceGate)
		{
			List<PublicPlayerListClientPresence> list = new List<PublicPlayerListClientPresence>(clientPresenceByUid.Count);
			foreach (PublicPlayerListClientPresence value in clientPresenceByUid.Values)
			{
				list.Add(ClonePresence(value));
			}
			list.Sort((PublicPlayerListClientPresence a, PublicPlayerListClientPresence b) => StringComparer.OrdinalIgnoreCase.Compare(a.PlayerName, b.PlayerName));
			return list;
		}
	}

	public void DecorateClientPresence(IEnumerable<AdminPlayerSummary> summaries)
	{
		lock (presenceGate)
		{
			foreach (AdminPlayerSummary summary in summaries)
			{
				summary.PlayerListClientDetected = false;
				summary.PlayerListClientVersion = "";
				if (summary.Online && !string.IsNullOrWhiteSpace(summary.PlayerUid) && clientPresenceByUid.TryGetValue(summary.PlayerUid, out PublicPlayerListClientPresence value))
				{
					summary.PlayerListClientDetected = true;
					summary.PlayerListClientVersion = value.ClientVersion;
				}
			}
		}
	}

	private void LoadConfig()
	{
		Config = ModConfigRecovery.LoadOrNull<PublicPlayerListConfig>(api, "NewfiesPlayerList.json", "Public Player List") ?? new PublicPlayerListConfig();
		SaveConfig();
		InvalidateSnapshot();
	}

	private void SaveConfig()
	{
		Config.Clamp();
		SuiteConfigStorage.Store(api, Config, "NewfiesPlayerList.json");
	}

	private void HandleClientHello(IServerPlayer player, PublicPlayerListClientHello hello)
	{
		if (!disposed && player != null)
		{
			Interlocked.Increment(ref clientHellosReceived);
			RecordClientPresence(player, hello?.ClientVersion ?? "", hello?.ProtocolVersion ?? 0, hello?.Capabilities ?? "");
			if (Config.DebugLogging)
			{
				api.Logger.Debug("[ServerEssentials/PlayerList] Hello from {0}: client={1}, protocol={2}, capabilities={3}", player.PlayerName, hello?.ClientVersion ?? "", hello?.ProtocolVersion ?? 0, hello?.Capabilities ?? "");
			}
		}
	}

	private void RecordClientPresence(IServerPlayer player, string version, int protocolVersion, string capabilities)
	{
		string text = player.PlayerUID ?? "";
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		lock (presenceGate)
		{
			if (!clientPresenceByUid.TryGetValue(text, out PublicPlayerListClientPresence value))
			{
				value = new PublicPlayerListClientPresence
				{
					PlayerUid = text,
					FirstSeenUnixMilliseconds = num
				};
				clientPresenceByUid[text] = value;
			}
			value.PlayerName = player.PlayerName ?? value.PlayerName;
			if (!string.IsNullOrWhiteSpace(version))
			{
				value.ClientVersion = version.Trim();
			}
			if (protocolVersion > 0)
			{
				value.ProtocolVersion = protocolVersion;
			}
			if (!string.IsNullOrWhiteSpace(capabilities))
			{
				value.Capabilities = capabilities.Trim();
			}
			value.LastSeenUnixMilliseconds = num;
		}
	}

	private void HandleRequest(IServerPlayer requestingPlayer, PublicPlayerListRequest request)
	{
		if (disposed || requestingPlayer == null)
		{
			return;
		}
		Interlocked.Increment(ref requestsReceived);
		RecordClientPresence(requestingPlayer, "", 0, "request");
		long serverUptimeMilliseconds = api.Server.ServerUptimeMilliseconds;
		string key = (string.IsNullOrWhiteSpace(requestingPlayer.PlayerUID) ? $"client:{requestingPlayer.ClientId}" : requestingPlayer.PlayerUID);
		if (lastRequestAtByPlayer.TryGetValue(key, out var value) && serverUptimeMilliseconds - value < 750)
		{
			Interlocked.Increment(ref requestsThrottled);
			if (Config.DebugLogging)
			{
				api.Logger.Debug("[ServerEssentials/PlayerList] Throttled request from {0}; delta={1}ms.", requestingPlayer.PlayerName, serverUptimeMilliseconds - value);
			}
			return;
		}
		lastRequestAtByPlayer[key] = serverUptimeMilliseconds;
		PublicPlayerListResponse publicPlayerListResponse;
		try
		{
			EnsureSnapshot(serverUptimeMilliseconds);
			publicPlayerListResponse = BuildResponse(request.RequestId);
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref requestBuildFailures);
			api.Logger.Warning("[ServerEssentials/PlayerList] Failed to build public player list for {0}: {1}: {2}", requestingPlayer.PlayerName, ex.GetType().Name, ex.Message);
			publicPlayerListResponse = new PublicPlayerListResponse
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
				BackendError = "Suite backend failed to build the roster (" + ex.GetType().Name + "). Check server log.",
				BackendBuild = "suite-1.2.44-playerlist-v5"
			};
		}
		IServerNetworkChannel serverNetworkChannel = channel;
		if (serverNetworkChannel == null)
		{
			Interlocked.Increment(ref responseSendFailures);
			api.Logger.Warning("[ServerEssentials/PlayerList] Request received from {0}, but the server channel reference is unavailable.", requestingPlayer.PlayerName);
			return;
		}
		try
		{
			serverNetworkChannel.SendPacket(publicPlayerListResponse, requestingPlayer);
			Interlocked.Increment(ref responsesSent);
			if (Config.DebugLogging)
			{
				api.Logger.Debug("[ServerEssentials/PlayerList] Sent request {0} to {1}: players={2}, roster={3}.", publicPlayerListResponse.RequestId, requestingPlayer.PlayerName, publicPlayerListResponse.Players?.Count ?? 0, publicPlayerListResponse.RosterSource);
			}
		}
		catch (Exception ex2)
		{
			Interlocked.Increment(ref responseSendFailures);
			api.Logger.Warning("[ServerEssentials/PlayerList] Failed to send public player list to {0}: {1}: {2}", requestingPlayer.PlayerName, ex2.GetType().Name, ex2.Message);
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
			BackendBuild = "suite-1.2.44-playerlist-v5"
		};
	}

	private void EnsureSnapshot(long now)
	{
		if (cachedSnapshotAtMs != long.MinValue && now - cachedSnapshotAtMs < 750)
		{
			return;
		}
		List<PublicPlayerListEntry> list = new List<PublicPlayerListEntry>();
		AdminService adminService = getAdmin();
		bool flag = false;
		if (adminService != null)
		{
			IReadOnlyList<AdminPlayerSummary> onlinePlayerSummaries = adminService.GetOnlinePlayerSummaries();
			flag = true;
			list.Capacity = onlinePlayerSummaries.Count;
			foreach (AdminPlayerSummary item in onlinePlayerSummaries)
			{
				if (item.Online && !string.IsNullOrWhiteSpace(item.Name))
				{
					IServerPlayer serverPlayer = FindOnlinePlayer(item.PlayerUid, item.Name);
					list.Add(new PublicPlayerListEntry
					{
						PlayerName = ResolveDisplayName(item.PlayerUid, item.Name),
						RoleName = (serverPlayer?.Role?.Name ?? item.Role ?? "Player"),
						NameColorHex = ResolvePlayerListNameColor(serverPlayer?.Role),
						SortPrivilegeLevel = (serverPlayer?.Role?.PrivilegeLevel).GetValueOrDefault(),
						PingMs = item.PingMilliseconds,
						SessionSeconds = Math.Max(0L, item.SessionSeconds),
						Afk = item.Afk
					});
				}
			}
			if (list.Count == 0 && api.World.AllOnlinePlayers.Length != 0)
			{
				flag = false;
			}
		}
		if (!flag)
		{
			list.Clear();
			list.Capacity = Math.Max(list.Capacity, api.World.AllOnlinePlayers.Length);
			IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
			foreach (IPlayer player in allOnlinePlayers)
			{
				if (player is IServerPlayer serverPlayer2 && !string.IsNullOrWhiteSpace(serverPlayer2.PlayerName) && (serverPlayer2.ClientId <= 0 || serverPlayer2.ConnectionState == EnumClientState.Connected))
				{
					list.Add(new PublicPlayerListEntry
					{
						PlayerName = ResolveDisplayName(serverPlayer2.PlayerUID, serverPlayer2.PlayerName),
						RoleName = (serverPlayer2.Role?.Name ?? serverPlayer2.ServerData?.RoleCode ?? "Player"),
						NameColorHex = ResolvePlayerListNameColor(serverPlayer2.Role),
						SortPrivilegeLevel = (serverPlayer2.Role?.PrivilegeLevel ?? 0),
						PingMs = ToPingMilliseconds(serverPlayer2.Ping),
						SessionSeconds = -1L,
						Afk = false
					});
				}
			}
		}
		list.Sort(CompareEntries);
		cachedPlayers = list;
		cachedServerName = DisplayServerName;
		cachedMaxPlayers = Math.Max(0, api.Server.Config.MaxClients);
		cachedRosterSource = (flag ? "Player Management" : "Fallback");
		cachedSnapshotAtMs = now;
	}

	private IServerPlayer? FindOnlinePlayer(string uid, string name)
	{
		IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
		foreach (IPlayer player in allOnlinePlayers)
		{
			if (player is IServerPlayer serverPlayer)
			{
				if (!string.IsNullOrWhiteSpace(uid) && string.Equals(serverPlayer.PlayerUID, uid, StringComparison.OrdinalIgnoreCase))
				{
					return serverPlayer;
				}
				if (!string.IsNullOrWhiteSpace(name) && string.Equals(serverPlayer.PlayerName, name, StringComparison.OrdinalIgnoreCase))
				{
					return serverPlayer;
				}
			}
		}
		return null;
	}

	private static int CompareEntries(PublicPlayerListEntry left, PublicPlayerListEntry right)
	{
		int num = right.SortPrivilegeLevel.CompareTo(left.SortPrivilegeLevel);
		if (num != 0)
		{
			return num;
		}
		num = StringComparer.OrdinalIgnoreCase.Compare(left.RoleName, right.RoleName);
		if (num != 0)
		{
			return num;
		}
		return StringComparer.OrdinalIgnoreCase.Compare(left.PlayerName, right.PlayerName);
	}

	private void OnPlayerJoin(IServerPlayer player)
	{
		if (disposed || player == null || !Config.ReservedSlotsEnabled)
		{
			return;
		}
		int num = Math.Max(0, api.Server.Config.MaxClients);
		int num2 = Math.Clamp(Config.ReservedSlots, 0, num);
		if (num <= 0 || num2 <= 0 || IsReservedSlotEligible(player))
		{
			return;
		}
		int num3 = Math.Max(0, num - num2);
		int num4 = 0;
		foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
		{
			if (!string.Equals(item.PlayerUID, player.PlayerUID, StringComparison.OrdinalIgnoreCase) && !IsReservedSlotEligible(item))
			{
				num4++;
			}
		}
		if (num4 >= num3)
		{
			Interlocked.Increment(ref reservedSlotRejections);
			string text = player.Role?.Code ?? player.ServerData?.RoleCode ?? "unknown";
			api.Logger.Notification("[ServerEssentials/ReservedSlots] Rejected {0} ({1}); public capacity {2}/{2}, reserved={3}, max={4}.", player.PlayerName, text, num3, num2, num);
			player.Disconnect(Config.ReservedSlotsFullMessage);
		}
	}

	public bool IsReservedSlotEligible(IServerPlayer? player)
	{
		if (player == null || Config.ReservedSlotRoles == null || Config.ReservedSlotRoles.Count == 0)
		{
			return false;
		}
		string text = (player.Role?.Code ?? player.ServerData?.RoleCode ?? "").Trim();
		string text2 = (player.Role?.Name ?? "").Trim();
		foreach (string reservedSlotRole in Config.ReservedSlotRoles)
		{
			string text3 = (reservedSlotRole ?? "").Trim();
			if (text3.Length == 0 || (!text.Equals(text3, StringComparison.OrdinalIgnoreCase) && !text2.Equals(text3, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}
			return true;
		}
		return false;
	}

	private int CountOnlinePlayers(bool reservedOnly)
	{
		int num = 0;
		foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
		{
			bool flag = IsReservedSlotEligible(item);
			if (reservedOnly ? flag : (!flag))
			{
				num++;
			}
		}
		return num;
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
			lock (presenceGate)
			{
				clientPresenceByUid.Remove(player.PlayerUID);
			}
		}
		lastRequestAtByPlayer.Remove($"client:{player.ClientId}");
	}

	public void InvalidateDisplaySnapshot()
	{
		InvalidateSnapshot();
	}

	private string ResolveDisplayName(string? uid, string? realName)
	{
		string text = realName ?? string.Empty;
		if (resolveDisplayName == null)
		{
			return text;
		}
		try
		{
			return resolveDisplayName(uid ?? string.Empty, text) ?? text;
		}
		catch
		{
			return text;
		}
	}

	private void InvalidateSnapshot()
	{
		cachedSnapshotAtMs = long.MinValue;
	}

	private static PublicPlayerListClientPresence ClonePresence(PublicPlayerListClientPresence source)
	{
		return new PublicPlayerListClientPresence
		{
			PlayerUid = source.PlayerUid,
			PlayerName = source.PlayerName,
			ClientVersion = source.ClientVersion,
			ProtocolVersion = source.ProtocolVersion,
			Capabilities = source.Capabilities,
			FirstSeenUnixMilliseconds = source.FirstSeenUnixMilliseconds,
			LastSeenUnixMilliseconds = source.LastSeenUnixMilliseconds
		};
	}

	private static int ToPingMilliseconds(float pingSeconds)
	{
		if (float.IsNaN(pingSeconds) || float.IsInfinity(pingSeconds) || pingSeconds < 0f)
		{
			return -1;
		}
		return (int)Math.Clamp(Math.Round((double)pingSeconds * 1000.0), 0.0, 99999.0);
	}

	private static bool AppearanceChanged(PublicPlayerListConfig left, PublicPlayerListConfig right)
	{
		return !string.Equals(left.ServerName, right.ServerName, StringComparison.Ordinal) || Math.Abs(left.BackgroundOpacity - right.BackgroundOpacity) > 0.0001 || left.ShowRoles != right.ShowRoles || left.ShowPing != right.ShowPing || left.ShowSessionTime != right.ShowSessionTime || Math.Abs(left.LayoutScale - right.LayoutScale) > 0.0001 || left.RowsPerColumn != right.RowsPerColumn || left.MaxColumns != right.MaxColumns || !string.Equals(left.Alignment, right.Alignment, StringComparison.OrdinalIgnoreCase) || left.OffsetX != right.OffsetX || left.OffsetY != right.OffsetY || left.ShowPlayerInitials != right.ShowPlayerInitials || left.ShowCapacityBar != right.ShowCapacityBar || left.ShowColumnHeaders != right.ShowColumnHeaders || left.CompactRows != right.CompactRows || left.ShowPingBars != right.ShowPingBars || left.ShowFooterHint != right.ShowFooterHint || !string.Equals(left.AccentHex, right.AccentHex, StringComparison.OrdinalIgnoreCase) || left.UseRoleNameColors != right.UseRoleNameColors || !RoleColorMapsEqual(left.RoleNameColors, right.RoleNameColors);
	}

	private static PublicPlayerListConfig CloneConfig(PublicPlayerListConfig source)
	{
		return new PublicPlayerListConfig
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
	}

	private string? TryGetRoleColorOverride(IPlayerRole role)
	{
		Dictionary<string, string> roleNameColors = Config.RoleNameColors;
		if (roleNameColors == null || roleNameColors.Count == 0)
		{
			return null;
		}
		string text = (role.Code ?? "").Trim();
		if (text.Length > 0 && roleNameColors.TryGetValue(text, out var value) && !string.IsNullOrWhiteSpace(value))
		{
			return value;
		}
		string text2 = (role.Name ?? "").Trim();
		if (text2.Length > 0 && roleNameColors.TryGetValue(text2, out var value2) && !string.IsNullOrWhiteSpace(value2))
		{
			return value2;
		}
		return null;
	}

	private static string RoleColorHex(IPlayerRole? role)
	{
		if (role == null)
		{
			return string.Empty;
		}
		return $"#{role.Color.R:X2}{role.Color.G:X2}{role.Color.B:X2}";
	}

	private static bool RoleColorMapsEqual(Dictionary<string, string>? left, Dictionary<string, string>? right)
	{
		if (left == null)
		{
			left = new Dictionary<string, string>();
		}
		if (right == null)
		{
			right = new Dictionary<string, string>();
		}
		if (left.Count != right.Count)
		{
			return false;
		}
		foreach (KeyValuePair<string, string> item in left)
		{
			if (!right.TryGetValue(item.Key, out string value) || !string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		return true;
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			api.Event.PlayerJoin -= OnPlayerJoin;
			api.Event.PlayerNowPlaying -= OnPlayerRosterChanged;
			api.Event.PlayerDisconnect -= OnPlayerDisconnected;
			lastRequestAtByPlayer.Clear();
			lock (presenceGate)
			{
				clientPresenceByUid.Clear();
			}
			cachedPlayers.Clear();
			channel = null;
		}
	}
}
