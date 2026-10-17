using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Admin;
using BlockLogger.Hooks;
using BlockLogger.Services;
using BlockLogger.WebDashboard;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

public sealed class WebMapBridgeService : IDisposable
{
	private sealed class TerrainFileCacheEntry
	{
		public byte[] Bytes { get; }

		public long LastAccessUnixMilliseconds { get; set; }

		public TerrainFileCacheEntry(byte[] bytes, long lastAccessUnixMilliseconds)
		{
			Bytes = bytes;
			LastAccessUnixMilliseconds = lastAccessUnixMilliseconds;
		}
	}

	private sealed record HttpGetRequest(string Target, string IfNoneMatch);

	public const string ConfigFile = "NewfiesWebMap.json";

	private const int ActivitySnapshotIntervalMs = 60000;

	private const int PublicRefreshSeconds = 30;

	private const int MaxHeaderBytes = 16384;

	private const int SupportedTerrainManifestVersion = 7;

	private const int CurrentTerrainManifestVersion = 13;

	private readonly ICoreServerAPI api;

	private readonly Func<LogQueryService?> queryProvider;

	private readonly Func<BadBlockEntitySnapshot?> badBlockProvider;

	private readonly Func<int> dashboardPortProvider;

	private readonly Func<IReadOnlyList<AdminPlayerSummary>?> adminPlayersProvider;

	private readonly Func<IReadOnlyList<AdminPlayerSummary>?> allAdminPlayersProvider;

	private readonly WebMapDirtyChunkJournal? dirtyJournal;

	private readonly WebMapWaypointStore waypointStore;

	private readonly WebMapPlayerPrivacyStore playerPrivacyStore;

	private readonly WebMapLeaderboardService leaderboardService;

	private readonly object gate = new object();

	private readonly SemaphoreSlim connectionSlots = new SemaphoreSlim(8, 8);

	private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly object terrainFileCacheGate = new object();

	private readonly Dictionary<string, TerrainFileCacheEntry> terrainFileCache = new Dictionary<string, TerrainFileCacheEntry>(StringComparer.Ordinal);

	private long terrainFileCacheBytes;

	private const int MaxTerrainFileCacheEntries = 128;

	private const long MaxTerrainFileCacheBytes = 16777216L;

	private const long MaxTerrainFileCacheEntryBytes = 524288L;

	private WebMapConfig config;

	private int runtimeEnabled;

	private List<WebMapPlayerMarker> playerCache = new List<WebMapPlayerMarker>();

	private readonly HashSet<string> onlinePlayerUids = new HashSet<string>(StringComparer.Ordinal);

	private bool playerCountHandlersRegistered;

	private List<WebMapActivityMarker> activityCache = new List<WebMapActivityMarker>();

	private long playerCacheUnixMilliseconds;

	private long activityCacheUnixMilliseconds;

	private int activityRefreshInFlight;

	private long activityRefreshGeneration;

	private long playerListenerId = -1L;

	private int playerListenerIntervalMs;

	private int forcedTerrainExportInFlight;

	private CancellationTokenSource? forcedTerrainExportCts;

	private int coordinateOriginX;

	private int coordinateOriginZ;

	private int spawnX;

	private int spawnZ;

	private long ingameDateListenerId = -1L;

	private string ingameDate = "unknown";

	private int ingameYear;

	private int ingameMonth;

	private int ingameDay;

	private float ingameHour;

	private TcpListener? listener;

	private CancellationTokenSource? listenerCts;

	private Task? listenerTask;

	private string lastError = "";

	private readonly string mapDataRoot;

	private WebMapTerrainExportStatus terrainStatus = new WebMapTerrainExportStatus();

	private WebMapTerrainManifest? terrainManifestCache;

	private byte[] publicPageCache = Array.Empty<byte>();

	private byte[] publicSnapshotCache = Array.Empty<byte>();

	private long publicSnapshotCacheUnixMilliseconds;

	private long publicSnapshotGeneration;

	private long lastPublicViewerUnixMilliseconds;

	private int playerRefreshQueued;

	private readonly object publicSnapshotBuildGate = new object();

	private bool startupTerrainHandlerRegistered;

	private int startupTerrainHandlerInvoked;

	private bool terrainServerLockActive;

	private bool disposed;

	public WebMapConfig Config
	{
		get
		{
			lock (gate)
			{
				return config.Clone();
			}
		}
	}

	public bool IsEnabled => Volatile.Read(in runtimeEnabled) != 0;

	public bool IsRunning
	{
		get
		{
			lock (gate)
			{
				return listener != null;
			}
		}
	}

	public bool IsTerrainExportInProgress
	{
		get
		{
			lock (gate)
			{
				return string.Equals(terrainStatus.State, "exporting", StringComparison.OrdinalIgnoreCase) || Volatile.Read(in forcedTerrainExportInFlight) != 0;
			}
		}
	}

	public string LastError
	{
		get
		{
			lock (gate)
			{
				return lastError;
			}
		}
	}

	public string ListenAddress
	{
		get
		{
			WebMapConfig webMapConfig = Config;
			string bindAddress = webMapConfig.BindAddress;
			bool flag = ((bindAddress == "0.0.0.0" || bindAddress == "::") ? true : false);
			string value = (flag ? "0.0.0.0" : webMapConfig.BindAddress);
			return $"http://{value}:{webMapConfig.Port}/";
		}
	}

	public WebMapBridgeService(ICoreServerAPI api, Func<LogQueryService?> queryProvider, Func<BadBlockEntitySnapshot?> badBlockProvider, Func<int> dashboardPortProvider, Func<IReadOnlyList<AdminPlayerSummary>?> adminPlayersProvider, Func<IReadOnlyList<AdminPlayerSummary>?> allAdminPlayersProvider, WebMapDirtyChunkJournal? dirtyJournal = null)
	{
		this.api = api;
		this.queryProvider = queryProvider;
		this.badBlockProvider = badBlockProvider;
		this.dashboardPortProvider = dashboardPortProvider;
		this.adminPlayersProvider = adminPlayersProvider;
		this.allAdminPlayersProvider = allAdminPlayersProvider;
		this.dirtyJournal = dirtyJournal;
		mapDataRoot = api.GetOrCreateDataPath("NewfiesServerEssentialsWebMap");
		waypointStore = new WebMapWaypointStore(api, mapDataRoot);
		playerPrivacyStore = new WebMapPlayerPrivacyStore(api, mapDataRoot);
		leaderboardService = new WebMapLeaderboardService(api, allAdminPlayersProvider, (string uid) => playerPrivacyStore.IsHidden(uid), GetPublicVisiblePlayerUids, () => Config);
		config = LoadConfig();
		runtimeEnabled = (config.Enabled ? 1 : 0);
		terrainStatus = LoadInitialTerrainStatus(config);
	}

	private void CleanupInterruptedTerrainWork()
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			string[] array = new string[2]
			{
				Path.Combine(mapDataRoot, "terrain-next"),
				Path.Combine(mapDataRoot, "terrain-next-rollback")
			};
			foreach (string path in array)
			{
				try
				{
					if (Directory.Exists(path))
					{
						Directory.Delete(path, recursive: true);
					}
				}
				catch (Exception ex)
				{
					api.Logger.Warning("[ServerEssentials/WebMap] stale terrain staging cleanup failed for {0}: {1}", Path.GetFileName(path), ex.Message);
				}
			}
			string path2 = Path.Combine(mapDataRoot, ".cache-maintenance");
			if (File.Exists(path2) && !(DateTime.UtcNow - File.GetLastWriteTimeUtc(path2) >= TimeSpan.FromHours(24)))
			{
				return;
			}
			string path3 = Path.Combine(mapDataRoot, "terrain");
			if (Directory.Exists(path3))
			{
				foreach (string item in Directory.EnumerateFiles(path3, "*.tmp", SearchOption.AllDirectories))
				{
					try
					{
						File.Delete(item);
					}
					catch
					{
					}
				}
			}
			try
			{
				File.WriteAllText(path2, DateTime.UtcNow.ToString("O"));
			}
			catch
			{
			}
		}
		finally
		{
			stopwatch.Stop();
			SuiteTimingRegistry.Record("Web Map cache maintenance", stopwatch.Elapsed.TotalMilliseconds);
		}
	}

	private void PruneDisabledTerrainOutputs(WebMapConfig c)
	{
		try
		{
			string text = Path.Combine(mapDataRoot, "terrain");
			if (!Directory.Exists(text))
			{
				return;
			}
			if (!c.ExportSepiaTerrain)
			{
				string path = Path.Combine(text, "styles", "sepia");
				if (Directory.Exists(path))
				{
					Directory.Delete(path, recursive: true);
				}
			}
			if (!c.ExportTopographicTerrain)
			{
				string path2 = Path.Combine(text, "styles", "topo");
				if (Directory.Exists(path2))
				{
					Directory.Delete(path2, recursive: true);
				}
			}
			if (c.ExportNormalTerrain)
			{
				return;
			}
			string path3 = Path.Combine(text, "overview.png");
			if (File.Exists(path3))
			{
				File.Delete(path3);
			}
			string path4 = Path.Combine(text, "lod");
			if (Directory.Exists(path4))
			{
				Directory.Delete(path4, recursive: true);
			}
			foreach (string item in Directory.EnumerateDirectories(text))
			{
				string fileName = Path.GetFileName(item);
				if (int.TryParse(fileName, out var _))
				{
					Directory.Delete(item, recursive: true);
				}
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/WebMap] could not prune disabled terrain outputs: {0}: {1}", ex.GetType().Name, ex.Message);
		}
	}

	public WebMapTerrainExportStatus GetTerrainStatusSnapshot()
	{
		lock (gate)
		{
			return terrainStatus.Clone();
		}
	}

	public WebMapLeaderboardSnapshot GetLeaderboardSnapshot()
	{
		return leaderboardService.GetSnapshot();
	}

	public void Start()
	{
		if (!disposed)
		{
			leaderboardService.Start();
			if (!IsEnabled)
			{
				api.Logger.Notification("[ServerEssentials/WebMap] master feature available; public Web Map runtime is OFF. No map listener, polling timer, player tracking, terrain journaling, or automatic export work will run until it is enabled.");
			}
			else
			{
				ActivateRuntime();
			}
		}
	}

	private void ActivateRuntime()
	{
		if (!disposed && IsEnabled)
		{
			CleanupInterruptedTerrainWork();
			RefreshTerrainManifestCache();
			WebMapTerrainExportStatus webMapTerrainExportStatus = LoadInitialTerrainStatus(Config);
			lock (gate)
			{
				terrainStatus = webMapTerrainExportStatus;
			}
			RefreshCoordinateOrigin();
			ConfigurePlayerCountTracking();
			ConfigureIngameDateTracking();
			ConfigurePlayerListener();
			leaderboardService.Start();
			RestartListener();
			ConfigureStartupTerrainHook();
		}
	}

	private void ConfigureStartupTerrainHook()
	{
		if (startupTerrainHandlerRegistered || disposed || !IsEnabled)
		{
			return;
		}
		startupTerrainHandlerRegistered = true;
		EnumServerRunPhase currentRunPhase = api.Server.CurrentRunPhase;
		int num = (int)currentRunPhase;
		int num2 = 7;
		int num3 = 8;
		if (num < num2)
		{
			api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, OnStartupTerrainPhase);
			api.Logger.Notification("[ServerEssentials/WebMap] startup terrain hook registered for WorldReady. Current phase={0}; oneShot={1}, intervalEnabled={2}.", currentRunPhase, Config.ExportTerrainOnStart, Config.AutomaticStartupExportEnabled);
		}
		else if (num < num3)
		{
			api.Logger.Warning("[ServerEssentials/WebMap] Web Map enabled after the WorldReady event but before RunGame (phase={0}); evaluating queued startup terrain export immediately.", currentRunPhase);
			OnStartupTerrainPhase();
		}
		else
		{
			WebMapConfig webMapConfig = Config;
			if (webMapConfig.ExportTerrainOnStart || IsAutomaticStartupExportDue(webMapConfig, LoadTerrainManifest(), out var _))
			{
				api.Logger.Warning("[ServerEssentials/WebMap] a startup terrain export is queued, but Web Map was enabled at phase {0} after the blocking-export window. The request remains queued for the next restart; use Update Changed Terrain for routine live updates or Full Rebuild Map when a cache rebuild is required.", currentRunPhase);
			}
		}
	}

	private void DeactivateRuntime()
	{
		CancellationTokenSource cancellationTokenSource;
		lock (gate)
		{
			cancellationTokenSource = forcedTerrainExportCts;
		}
		try
		{
			cancellationTokenSource?.Cancel();
		}
		catch
		{
		}
		if (playerListenerId >= 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(playerListenerId);
			}
			catch
			{
			}
			playerListenerId = -1L;
		}
		if (ingameDateListenerId >= 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(ingameDateListenerId);
			}
			catch
			{
			}
			ingameDateListenerId = -1L;
		}
		if (playerCountHandlersRegistered)
		{
			try
			{
				api.Event.PlayerNowPlaying -= OnPlayerCountNowPlaying;
			}
			catch
			{
			}
			try
			{
				api.Event.PlayerDisconnect -= OnPlayerCountDisconnect;
			}
			catch
			{
			}
			playerCountHandlersRegistered = false;
		}
		StopListener();
		ClearTerrainFileCache();
		lock (gate)
		{
			playerCache.Clear();
			onlinePlayerUids.Clear();
			activityCache.Clear();
			activityRefreshGeneration++;
			playerCacheUnixMilliseconds = 0L;
			activityCacheUnixMilliseconds = 0L;
			InvalidatePublicSnapshotNoLock();
			ingameDate = "unknown";
		}
	}

	public WebMapConfig SaveConfig(WebMapConfig incoming)
	{
		if (incoming == null)
		{
			incoming = new WebMapConfig();
		}
		if (!incoming.AllowLivePlayerData)
		{
			incoming.ShowPlayers = false;
		}
		lock (gate)
		{
			incoming.LastSuccessfulTerrainExportUnixMilliseconds = config.LastSuccessfulTerrainExportUnixMilliseconds;
			incoming.LastTerrainExportDurationMilliseconds = config.LastTerrainExportDurationMilliseconds;
			incoming.UseDirectSaveDatabaseTerrain = config.UseDirectSaveDatabaseTerrain;
			incoming.TerrainPyramidWorkers = config.TerrainPyramidWorkers;
			incoming.TerrainBaseRenderWorkers = config.TerrainBaseRenderWorkers;
		}
		incoming.Clamp();
		EnsureSeparateDashboardPort(incoming);
		WebMapConfig webMapConfig;
		lock (gate)
		{
			webMapConfig = config;
			config = incoming.Clone();
			Volatile.Write(ref runtimeEnabled, incoming.Enabled ? 1 : 0);
			publicPageCache = Array.Empty<byte>();
			InvalidatePublicSnapshotNoLock();
			SuiteConfigStorage.Store(api, config, "NewfiesWebMap.json");
		}
		if (webMapConfig.ExportTerrainOnStart != incoming.ExportTerrainOnStart)
		{
			api.Logger.Notification("[ServerEssentials/WebMap] one-shot next-start terrain export changed: {0}. Setting was persisted to {1}.", incoming.ExportTerrainOnStart ? "QUEUED" : "OFF", "NewfiesWebMap.json");
		}
		bool flag = webMapConfig.Port != incoming.Port || !string.Equals(webMapConfig.BindAddress, incoming.BindAddress, StringComparison.OrdinalIgnoreCase);
		if (webMapConfig.Enabled && !incoming.Enabled)
		{
			DeactivateRuntime();
			api.Logger.Notification("[ServerEssentials/WebMap] public Web Map runtime turned OFF; listener and map background tracking stopped.");
		}
		else if (!webMapConfig.Enabled && incoming.Enabled)
		{
			ActivateRuntime();
			api.Logger.Notification("[ServerEssentials/WebMap] public Web Map runtime turned ON.");
		}
		else if (incoming.Enabled)
		{
			if (flag)
			{
				RestartListener();
			}
			if (webMapConfig.AllowLivePlayerData != incoming.AllowLivePlayerData || webMapConfig.ShowPlayers != incoming.ShowPlayers || webMapConfig.PlayerIconRefreshSeconds != incoming.PlayerIconRefreshSeconds)
			{
				ConfigurePlayerListener();
			}
		}
		if (webMapConfig.ShowLeaderboards != incoming.ShowLeaderboards || webMapConfig.LeaderboardShowXSkills != incoming.LeaderboardShowXSkills || webMapConfig.LeaderboardShowPlaytime != incoming.LeaderboardShowPlaytime || webMapConfig.LeaderboardTopPlayers != incoming.LeaderboardTopPlayers || webMapConfig.LeaderboardRefreshSeconds != incoming.LeaderboardRefreshSeconds)
		{
			leaderboardService.RequestRefresh();
		}
		if (webMapConfig.ShowActivity != incoming.ShowActivity || webMapConfig.ActivityWindowMinutes != incoming.ActivityWindowMinutes || webMapConfig.MaxActivityMarkers != incoming.MaxActivityMarkers)
		{
			lock (gate)
			{
				activityCache.Clear();
				activityRefreshGeneration++;
				activityCacheUnixMilliseconds = 0L;
			}
		}
		return incoming.Clone();
	}

	public WebMapConfig SaveExportSchedule(bool exportOnNextStart, bool automaticEnabled, int intervalDays, bool useIncremental, bool lockDuringExport)
	{
		bool exportTerrainOnStart;
		WebMapConfig webMapConfig;
		lock (gate)
		{
			exportTerrainOnStart = config.ExportTerrainOnStart;
			config.ExportTerrainOnStart = exportOnNextStart;
			config.AutomaticStartupExportEnabled = automaticEnabled;
			config.AutomaticStartupExportIntervalDays = Math.Clamp(intervalDays, 1, 365);
			config.UseIncrementalTerrainExport = useIncremental;
			config.LockServerDuringTerrainExport = lockDuringExport;
			config.Clamp();
			SuiteConfigStorage.Store(api, config, "NewfiesWebMap.json");
			InvalidatePublicSnapshotNoLock();
			webMapConfig = config.Clone();
		}
		if (exportTerrainOnStart != webMapConfig.ExportTerrainOnStart)
		{
			api.Logger.Notification("[ServerEssentials/WebMap] one-shot next-start terrain export changed: {0}. Export schedule save preserved the Web Map enabled/listener settings.", webMapConfig.ExportTerrainOnStart ? "QUEUED" : "OFF");
		}
		return webMapConfig;
	}

	public WebMapWaypoint SaveWaypoint(WebMapWaypoint incoming, string actor)
	{
		if (disposed)
		{
			throw new ObjectDisposedException("WebMapBridgeService");
		}
		WebMapWaypoint result = waypointStore.Upsert(incoming, actor);
		lock (gate)
		{
			InvalidatePublicSnapshotNoLock();
		}
		return result;
	}

	public bool DeleteWaypoint(string id)
	{
		if (disposed)
		{
			return false;
		}
		bool flag = waypointStore.Delete(id);
		if (flag)
		{
			InvalidatePublicSnapshot();
		}
		return flag;
	}

	public string AddPlayerWaypoint(IServerPlayer player, string type, string icon, string name)
	{
		if (disposed)
		{
			return "Native Web Map service is unavailable.";
		}
		WebMapConfig webMapConfig = Config;
		string text = WebMapWaypointStore.NormalizeType(type);
		int num = ((text == "major") ? webMapConfig.PlayerMajorWaypointLimit : webMapConfig.PlayerMinorWaypointLimit);
		if (num <= 0)
		{
			return "Player-created " + text + " Web Map waypoints are disabled by the server.";
		}
		if (player == null)
		{
			return "Player is unavailable.";
		}
		Entity playerEntity = EntityRuntimeAccessors.GetPlayerEntity(player);
		if (!EntityPositionResolver.TryGetCoordinates(playerEntity, out var x, out var y, out var z, out var dimension))
		{
			return "Your current position is unavailable.";
		}
		string text2 = (name ?? "").Trim();
		if (text2.Length == 0)
		{
			return "Usage: /webmap waypoint add <major|minor> <name>";
		}
		try
		{
			WebMapWaypoint webMapWaypoint = waypointStore.CreatePlayerWaypoint(player.PlayerUID, player.PlayerName, text, text2, icon, x - (double)coordinateOriginX, y, z - (double)coordinateOriginZ, dimension, num);
			InvalidatePublicSnapshot();
			int value = waypointStore.CountPlayerWaypoints(player.PlayerUID, text);
			return $"Added {webMapWaypoint.Type} Web Map waypoint '{webMapWaypoint.Name}' at X {Math.Round(webMapWaypoint.X):N0}, Y {Math.Round(webMapWaypoint.Y):N0}, Z {Math.Round(webMapWaypoint.Z):N0}. ({value}/{num})";
		}
		catch (InvalidOperationException ex)
		{
			return ex.Message;
		}
	}

	public string RemovePlayerWaypoint(IServerPlayer player, string nameOrId)
	{
		if (disposed)
		{
			return "Native Web Map service is unavailable.";
		}
		if (player == null)
		{
			return "Player is unavailable.";
		}
		string text = (nameOrId ?? "").Trim();
		if (text.Length == 0)
		{
			return "Usage: /webmap waypoint remove <name>";
		}
		if (!waypointStore.DeletePlayerWaypoint(player.PlayerUID, text, out WebMapWaypoint deleted) || deleted == null)
		{
			return "You do not have a Web Map waypoint named '" + text + "'.";
		}
		InvalidatePublicSnapshot();
		int num = waypointStore.CountPlayerWaypoints(player.PlayerUID);
		return $"Removed Web Map waypoint '{deleted.Name}'. Major {GetPlayerWaypointCount(player, "major")}/{Config.PlayerMajorWaypointLimit}, minor {GetPlayerWaypointCount(player, "minor")}/{Config.PlayerMinorWaypointLimit}.";
	}

	public int GetPlayerWaypointCount(IServerPlayer player)
	{
		return (player != null) ? waypointStore.CountPlayerWaypoints(player.PlayerUID) : 0;
	}

	public int GetPlayerWaypointCount(IServerPlayer player, string type)
	{
		return (player != null) ? waypointStore.CountPlayerWaypoints(player.PlayerUID, type) : 0;
	}

	public bool IsPlayerHiddenFromLiveMap(IServerPlayer player)
	{
		return player != null && playerPrivacyStore.IsHidden(player.PlayerUID);
	}

	public string SetPlayerLiveMapVisibility(IServerPlayer player, bool visible)
	{
		if (player == null)
		{
			return "Player is unavailable.";
		}
		bool hidden = !visible;
		try
		{
			playerPrivacyStore.SetHidden(player.PlayerUID, hidden);
		}
		finally
		{
			CapturePlayers();
			leaderboardService.RequestRefresh();
			InvalidatePublicSnapshot();
		}
		return visible ? "You are now visible on the Web Map live-player layer when that layer is enabled." : "You are now hidden from the Web Map live-player layer. Your name, position, ping and playtime are omitted from its live data.";
	}

	private void InvalidatePublicSnapshotNoLock()
	{
		publicSnapshotCache = Array.Empty<byte>();
		publicSnapshotCacheUnixMilliseconds = 0L;
		publicSnapshotGeneration++;
	}

	private void InvalidatePublicSnapshot()
	{
		lock (gate)
		{
			InvalidatePublicSnapshotNoLock();
		}
	}

	private HashSet<string> GetPublicVisiblePlayerUids()
	{
		lock (gate)
		{
			return (from p in playerCache
				select (p.Uid ?? "").Trim() into uid
				where uid.Length > 0
				select uid).ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
	}

	public WebMapSnapshot GetSnapshot(BadBlockEntitySnapshot? badBlocks = null, bool includeWaypointAudit = true, bool includeBadBlockSnapshot = true)
	{
		QueueActivityRefreshIfNeeded();
		WebMapConfig webMapConfig;
		List<WebMapPlayerMarker> players;
		List<WebMapActivityMarker> activity;
		int activePlayerCount;
		string text;
		int num;
		int num2;
		int num3;
		float num4;
		long playerSnapshotUnixMilliseconds;
		long activitySnapshotUnixMilliseconds;
		string text2;
		bool flag;
		WebMapTerrainExportStatus terrain;
		lock (gate)
		{
			webMapConfig = config.Clone();
			players = ((webMapConfig.AllowLivePlayerData && webMapConfig.ShowPlayers) ? ClonePlayers(playerCache) : new List<WebMapPlayerMarker>());
			activity = (webMapConfig.ShowActivity ? CloneActivity(activityCache) : new List<WebMapActivityMarker>());
			activePlayerCount = ((webMapConfig.AllowLivePlayerData && webMapConfig.ShowPlayers) ? playerCache.Count : 0);
			text = ingameDate;
			num = ingameYear;
			num2 = ingameMonth;
			num3 = ingameDay;
			num4 = ingameHour;
			playerSnapshotUnixMilliseconds = playerCacheUnixMilliseconds;
			activitySnapshotUnixMilliseconds = activityCacheUnixMilliseconds;
			text2 = string.Join(" ", new string[3] { lastError, playerPrivacyStore.StorageError, waypointStore.StorageError }.Where((string value) => !string.IsNullOrWhiteSpace(value)));
			flag = listener != null;
			terrain = terrainStatus.Clone();
		}
		WebMapTerrainManifest webMapTerrainManifest = LoadTerrainManifest();
		bool automaticStartupExportDue = IsAutomaticStartupExportDue(webMapConfig, webMapTerrainManifest, out var nextDueUnixMilliseconds);
		WebMapSnapshot webMapSnapshot = new WebMapSnapshot
		{
			Enabled = webMapConfig.Enabled,
			Provider = "Newfie's Native Web Map",
			ProviderDetected = true,
			ProviderVersion = "15",
			Running = flag,
			BindAddress = webMapConfig.BindAddress,
			Port = webMapConfig.Port,
			MapUrl = webMapConfig.PublicUrl,
			DashboardUrl = webMapConfig.DashboardUrl,
			DashboardPort = dashboardPortProvider(),
			Title = webMapConfig.Title,
			CoordinateOriginX = coordinateOriginX,
			CoordinateOriginZ = coordinateOriginZ,
			SpawnX = spawnX,
			SpawnZ = spawnZ,
			GeneratedUnixMilliseconds = NowMs(),
			ActivePlayerCount = activePlayerCount,
			IngameDate = text,
			IngameYear = num,
			IngameMonth = num2,
			IngameDay = num3,
			IngameHour = num4,
			PlayerSnapshotUnixMilliseconds = playerSnapshotUnixMilliseconds,
			ActivitySnapshotUnixMilliseconds = activitySnapshotUnixMilliseconds,
			PublicRefreshSeconds = 30,
			PlayerRefreshSeconds = webMapConfig.PlayerIconRefreshSeconds,
			ActivityRefreshSeconds = 60,
			ActivityWindowMinutes = webMapConfig.ActivityWindowMinutes,
			MaxActivityMarkers = webMapConfig.MaxActivityMarkers,
			AllowLivePlayerData = webMapConfig.AllowLivePlayerData,
			ShowPlayers = (webMapConfig.AllowLivePlayerData && webMapConfig.ShowPlayers),
			ShowBadBlockEntities = webMapConfig.ShowBadBlockEntities,
			ShowActivity = webMapConfig.ShowActivity,
			ShowWaypoints = webMapConfig.ShowWaypoints,
			ShowLeaderboards = webMapConfig.ShowLeaderboards,
			LeaderboardShowPlaytime = webMapConfig.LeaderboardShowPlaytime,
			LeaderboardShowXSkills = webMapConfig.LeaderboardShowXSkills,
			LeaderboardTopPlayers = webMapConfig.LeaderboardTopPlayers,
			LeaderboardRefreshSeconds = webMapConfig.LeaderboardRefreshSeconds,
			ExportNormalTerrain = webMapConfig.ExportNormalTerrain,
			ExportSepiaTerrain = webMapConfig.ExportSepiaTerrain,
			ExportTopographicTerrain = webMapConfig.ExportTopographicTerrain,
			PlayerMajorWaypointLimit = webMapConfig.PlayerMajorWaypointLimit,
			PlayerMinorWaypointLimit = webMapConfig.PlayerMinorWaypointLimit,
			LastTerrainExportDurationMilliseconds = webMapConfig.LastTerrainExportDurationMilliseconds,
			MaximumZoomOutSpanBlocks = webMapConfig.MaximumZoomOutSpanBlocks,
			DefaultViewSpanBlocks = webMapConfig.DefaultViewSpanBlocks,
			ExportTerrainOnStart = webMapConfig.ExportTerrainOnStart,
			AutomaticStartupExportEnabled = webMapConfig.AutomaticStartupExportEnabled,
			AutomaticStartupExportIntervalDays = webMapConfig.AutomaticStartupExportIntervalDays,
			AutomaticStartupExportDue = automaticStartupExportDue,
			NextAutomaticStartupExportUnixMilliseconds = nextDueUnixMilliseconds,
			LockServerDuringTerrainExport = webMapConfig.LockServerDuringTerrainExport,
			UseIncrementalTerrainExport = webMapConfig.UseIncrementalTerrainExport,
			TerrainTileSize = webMapConfig.TerrainTileSize,
			Terrain = terrain,
			PendingDirtyMapChunks = (dirtyJournal?.Count ?? 0),
			TerrainManifest = webMapTerrainManifest,
			LastError = text2,
			Players = players,
			Activity = activity,
			Waypoints = (webMapConfig.ShowWaypoints ? GetWaypointSnapshot(includeWaypointAudit) : new List<WebMapWaypoint>())
		};
		BadBlockEntitySnapshot badBlockEntitySnapshot = (includeBadBlockSnapshot ? (badBlocks ?? SafeGetBadBlocks()) : null);
		if (webMapConfig.ShowBadBlockEntities && badBlockEntitySnapshot != null)
		{
			webMapSnapshot.BadBlockEntities = BuildBadBlockMarkers(badBlockEntitySnapshot.Entries, 100);
		}
		WebMapSnapshot webMapSnapshot2 = webMapSnapshot;
		string note;
		if (flag)
		{
			note = ((webMapConfig.AllowLivePlayerData && webMapConfig.ShowPlayers) ? "Native map host is running on its own port. Terrain is served from the published cache; live player coordinates are enabled and use the conservative player snapshot cache." : "Native map host is running on its own port. Terrain is cached; the Players layer is currently disabled.");
		}
		else
		{
			note = (webMapConfig.Enabled ? "Native map host is enabled but not listening. Check LastError and the configured bind/port." : "Native map host is disabled.");
		}
		webMapSnapshot2.Note = note;
		return webMapSnapshot;
	}

	private void RefreshCoordinateOrigin()
	{
		try
		{
			Vec3i defaultSpawnOrWorldCenter = CoordinateDisplayService.GetDefaultSpawnOrWorldCenter(api);
			lock (gate)
			{
				bool flag = coordinateOriginX != defaultSpawnOrWorldCenter.X || coordinateOriginZ != defaultSpawnOrWorldCenter.Z || spawnX != defaultSpawnOrWorldCenter.X || spawnZ != defaultSpawnOrWorldCenter.Z;
				coordinateOriginX = defaultSpawnOrWorldCenter.X;
				coordinateOriginZ = defaultSpawnOrWorldCenter.Z;
				spawnX = defaultSpawnOrWorldCenter.X;
				spawnZ = defaultSpawnOrWorldCenter.Z;
				if (flag)
				{
					InvalidatePublicSnapshotNoLock();
				}
			}
		}
		catch
		{
		}
	}

	private List<WebMapWaypoint> GetWaypointSnapshot(bool includeAudit)
	{
		List<WebMapWaypoint> list = waypointStore.Snapshot();
		if (includeAudit)
		{
			return list;
		}
		for (int i = 0; i < list.Count; i++)
		{
			list[i].CreatedBy = "";
			list[i].UpdatedBy = "";
			list[i].OwnerPlayerUid = "";
		}
		return list;
	}

	private static List<WebMapPlayerMarker> ClonePlayers(List<WebMapPlayerMarker> source)
	{
		List<WebMapPlayerMarker> list = new List<WebMapPlayerMarker>(source.Count);
		for (int i = 0; i < source.Count; i++)
		{
			list.Add(source[i].Clone());
		}
		return list;
	}

	private static List<WebMapActivityMarker> CloneActivity(List<WebMapActivityMarker> source)
	{
		List<WebMapActivityMarker> list = new List<WebMapActivityMarker>(source.Count);
		for (int i = 0; i < source.Count; i++)
		{
			list.Add(source[i].Clone());
		}
		return list;
	}

	private static List<WebMapBadBlockMarker> BuildBadBlockMarkers(List<BadBlockEntityRecord> entries, int max)
	{
		List<BadBlockEntityRecord> list = new List<BadBlockEntityRecord>(Math.Min(max, entries.Count));
		for (int i = 0; i < entries.Count; i++)
		{
			BadBlockEntityRecord badBlockEntityRecord = entries[i];
			if (badBlockEntityRecord.Tracking)
			{
				list.Add(badBlockEntityRecord);
			}
		}
		list.Sort((BadBlockEntityRecord a, BadBlockEntityRecord b) => b.LastSeenUnixMilliseconds.CompareTo(a.LastSeenUnixMilliseconds));
		if (list.Count > max)
		{
			list.RemoveRange(max, list.Count - max);
		}
		List<WebMapBadBlockMarker> list2 = new List<WebMapBadBlockMarker>(list.Count);
		for (int num = 0; num < list.Count; num++)
		{
			BadBlockEntityRecord badBlockEntityRecord2 = list[num];
			list2.Add(new WebMapBadBlockMarker
			{
				Key = badBlockEntityRecord2.Key,
				X = badBlockEntityRecord2.X,
				Y = badBlockEntityRecord2.Y,
				Z = badBlockEntityRecord2.Z,
				Dimension = badBlockEntityRecord2.Dimension,
				BlockCode = badBlockEntityRecord2.BlockCode,
				BlockEntityType = badBlockEntityRecord2.BlockEntityType,
				Occurrences = badBlockEntityRecord2.Occurrences,
				LastSeenUnixMilliseconds = badBlockEntityRecord2.LastSeenUnixMilliseconds,
				LastException = badBlockEntityRecord2.LastException
			});
		}
		return list2;
	}

	private BadBlockEntitySnapshot? SafeGetBadBlocks()
	{
		try
		{
			return badBlockProvider();
		}
		catch
		{
			return null;
		}
	}

	private void ConfigurePlayerCountTracking()
	{
		if (!playerCountHandlersRegistered)
		{
			playerCountHandlersRegistered = true;
			api.Event.PlayerNowPlaying += OnPlayerCountNowPlaying;
			api.Event.PlayerDisconnect += OnPlayerCountDisconnect;
			RefreshOnlinePlayerCount();
		}
	}

	private void RefreshOnlinePlayerCount()
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
			if (allOnlinePlayers != null)
			{
				for (int i = 0; i < allOnlinePlayers.Length; i++)
				{
					if (allOnlinePlayers[i] is IServerPlayer { PlayerUID: var text })
					{
						if (text == null)
						{
							text = "";
						}
						string text2 = text;
						if (text2.Length != 0)
						{
							hashSet.Add(text2);
						}
					}
				}
			}
		}
		catch
		{
		}
		lock (gate)
		{
			onlinePlayerUids.Clear();
			foreach (string item in hashSet)
			{
				onlinePlayerUids.Add(item);
			}
			InvalidatePublicSnapshotNoLock();
		}
	}

	private void OnPlayerCountNowPlaying(IServerPlayer player)
	{
		string text = player?.PlayerUID ?? "";
		if (text.Length == 0)
		{
			return;
		}
		lock (gate)
		{
			onlinePlayerUids.Add(text);
			InvalidatePublicSnapshotNoLock();
		}
	}

	private void OnPlayerCountDisconnect(IServerPlayer player)
	{
		string text = player?.PlayerUID ?? "";
		if (text.Length == 0)
		{
			return;
		}
		lock (gate)
		{
			onlinePlayerUids.Remove(text);
			InvalidatePublicSnapshotNoLock();
		}
	}

	private void ConfigureIngameDateTracking()
	{
		CaptureIngameDate();
		if (ingameDateListenerId < 0)
		{
			ingameDateListenerId = api.Event.RegisterGameTickListener((float _) =>
			{
				CaptureIngameDate();
			}, (Exception ex) =>
			{
				api.Logger.Warning("[ServerEssentials/WebMap] in-game date snapshot failed: {0}: {1}", ex.GetType().Name, ex.Message);
			}, 30000);
		}
	}

	private void CaptureIngameDate()
	{
		RefreshCoordinateOrigin();
		string b = "unknown";
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		float num4 = 0f;
		try
		{
			IGameCalendar calendar = api.World.Calendar;
			if (calendar != null)
			{
				num = calendar.Year;
				num2 = calendar.Month;
				num3 = calendar.DayOfYear % Math.Max(1, calendar.DaysPerMonth) + 1;
				num4 = calendar.HourOfDay;
				b = $"Year {num}, Month {num2}, Day {num3}, {num4:0.00}h";
			}
		}
		catch
		{
		}
		lock (gate)
		{
			if (!string.Equals(ingameDate, b, StringComparison.Ordinal) || ingameYear != num || ingameMonth != num2 || ingameDay != num3 || Math.Abs(ingameHour - num4) >= 0.01f)
			{
				ingameDate = b;
				ingameYear = num;
				ingameMonth = num2;
				ingameDay = num3;
				ingameHour = num4;
				InvalidatePublicSnapshotNoLock();
			}
		}
	}

	private void ConfigurePlayerListener()
	{
		WebMapConfig webMapConfig = Config;
		bool flag = webMapConfig.Enabled && webMapConfig.AllowLivePlayerData && webMapConfig.ShowPlayers;
		int num = webMapConfig.PlayerIconRefreshSeconds * 1000;
		if (playerListenerId >= 0 && (!flag || playerListenerIntervalMs != num))
		{
			api.Event.UnregisterGameTickListener(playerListenerId);
			playerListenerId = -1L;
		}
		if (flag)
		{
			CapturePlayers();
			if (playerListenerId >= 0)
			{
				return;
			}
			playerListenerId = api.Event.RegisterGameTickListener((float _) =>
			{
				if (HasRecentPublicViewer())
				{
					CapturePlayersIfDue();
				}
			}, (Exception ex) =>
			{
				api.Logger.Warning("[ServerEssentials/WebMap] player snapshot failed: {0}: {1}", ex.GetType().Name, ex.Message);
			}, num);
			playerListenerIntervalMs = num;
			return;
		}
		if (playerListenerId >= 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(playerListenerId);
			}
			catch
			{
			}
			playerListenerId = -1L;
		}
		lock (gate)
		{
			playerCache.Clear();
			playerCacheUnixMilliseconds = NowMs();
		}
	}

	private void CapturePlayersIfDue()
	{
		if (NowMs() - Interlocked.Read(in playerCacheUnixMilliseconds) >= (long)Config.PlayerIconRefreshSeconds * 1000L)
		{
			CapturePlayers();
		}
	}

	private void CapturePlayers()
	{
		if (disposed)
		{
			return;
		}
		WebMapConfig webMapConfig = Config;
		if (!webMapConfig.Enabled || !webMapConfig.AllowLivePlayerData || !webMapConfig.ShowPlayers)
		{
			lock (gate)
			{
				playerCache.Clear();
				playerCacheUnixMilliseconds = NowMs();
				return;
			}
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
		List<WebMapPlayerMarker> list = new List<WebMapPlayerMarker>(allOnlinePlayers?.Length ?? 0);
		IReadOnlyList<AdminPlayerSummary> readOnlyList = null;
		try
		{
			readOnlyList = adminPlayersProvider();
		}
		catch
		{
		}
		Dictionary<string, AdminPlayerSummary> dictionary = readOnlyList?.Where((AdminPlayerSummary p) => p.Online && !string.IsNullOrWhiteSpace(p.PlayerUid)).ToDictionary((AdminPlayerSummary p) => p.PlayerUid, StringComparer.OrdinalIgnoreCase);
		if (allOnlinePlayers != null)
		{
			for (int num = 0; num < allOnlinePlayers.Length; num++)
			{
				if (allOnlinePlayers[num] is IServerPlayer { Entity: not null } serverPlayer && !playerPrivacyStore.IsHidden(serverPlayer.PlayerUID) && EntityPositionResolver.TryGetPose(serverPlayer.Entity, out var x, out var y, out var z, out var dimension, out var yaw, out var _))
				{
					AdminPlayerSummary value = null;
					dictionary?.TryGetValue(serverPlayer.PlayerUID ?? "", out value);
					list.Add(new WebMapPlayerMarker
					{
						Name = (serverPlayer.PlayerName ?? ""),
						Uid = (serverPlayer.PlayerUID ?? ""),
						X = x,
						Y = y,
						Z = z,
						Dimension = dimension,
						PingMilliseconds = (value?.PingMilliseconds ?? 0),
						TotalPlaySeconds = (value?.TotalPlaySeconds ?? 0),
						SessionSeconds = (value?.SessionSeconds ?? 0),
						YawRadians = yaw
					});
				}
			}
		}
		lock (gate)
		{
			playerCache = list;
			playerCacheUnixMilliseconds = NowMs();
			InvalidatePublicSnapshotNoLock();
		}
		stopwatch.Stop();
		SuiteTimingRegistry.Record("Web Map player snapshot", stopwatch.Elapsed.TotalMilliseconds);
	}

	private void QueueActivityRefreshIfNeeded()
	{
		long num = NowMs();
		WebMapConfig c;
		long generation;
		lock (gate)
		{
			if (disposed || !config.Enabled || !config.ShowActivity || (activityCacheUnixMilliseconds > 0 && num - activityCacheUnixMilliseconds < 60000) || Interlocked.CompareExchange(ref activityRefreshInFlight, 1, 0) != 0)
			{
				return;
			}
			c = config.Clone();
			generation = activityRefreshGeneration;
		}
		Task.Run(async () =>
		{
			try
			{
				lock (gate)
				{
					if (!IsActivityRefreshCurrentNoLock(generation, c))
					{
						return;
					}
				}
				LogQueryService queries = queryProvider();
				if (queries == null)
				{
					lock (gate)
					{
						if (IsActivityRefreshCurrentNoLock(generation, c))
						{
							activityCacheUnixMilliseconds = NowMs();
						}
					}
				}
				else
				{
					List<PerformanceBlockActivityHotspot> rows = await queries.QueryRecentBlockActivityHotspotsAsync(c.ActivityWindowMinutes, c.MaxActivityMarkers).ConfigureAwait(continueOnCapturedContext: false);
					List<WebMapActivityMarker> mapped = new List<WebMapActivityMarker>(rows.Count);
					for (int i = 0; i < rows.Count; i++)
					{
						PerformanceBlockActivityHotspot x = rows[i];
						mapped.Add(new WebMapActivityMarker
						{
							X = x.X,
							Y = x.Y,
							Z = x.Z,
							Dimension = x.Dimension,
							BlockCode = x.BlockCode,
							EventCount = x.EventCount,
							DistinctActions = x.DistinctActions,
							LastTimestampTicks = x.LastTimestampTicks
						});
					}
					lock (gate)
					{
						if (!IsActivityRefreshCurrentNoLock(generation, c))
						{
							return;
						}
						activityCache = mapped;
						activityCacheUnixMilliseconds = NowMs();
						InvalidatePublicSnapshotNoLock();
					}
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				lock (gate)
				{
					if (!IsActivityRefreshCurrentNoLock(generation, c))
					{
						return;
					}
					activityCacheUnixMilliseconds = NowMs();
				}
				api.Logger.Warning("[ServerEssentials/WebMap] BlockLogger activity snapshot failed: {0}: {1}", ex2.GetType().Name, ex2.Message);
			}
			finally
			{
				Interlocked.Exchange(ref activityRefreshInFlight, 0);
			}
		});
	}

	private bool IsActivityRefreshCurrentNoLock(long generation, WebMapConfig requested)
	{
		return !disposed && config.Enabled && config.ShowActivity && generation == activityRefreshGeneration && config.ActivityWindowMinutes == requested.ActivityWindowMinutes && config.MaxActivityMarkers == requested.MaxActivityMarkers;
	}

	private WebMapConfig LoadConfig()
	{
		WebMapConfig webMapConfig = ModConfigRecovery.LoadOrNull<WebMapConfig>(api, "NewfiesWebMap.json", "WebMap") ?? new WebMapConfig();
		webMapConfig.Clamp();
		EnsureSeparateDashboardPort(webMapConfig);
		SuiteConfigStorage.Store(api, webMapConfig, "NewfiesWebMap.json");
		return webMapConfig;
	}

	private WebMapTerrainExportStatus LoadInitialTerrainStatus(WebMapConfig c)
	{
		WebMapTerrainManifest webMapTerrainManifest = LoadTerrainManifest();
		bool flag = IsAutomaticStartupExportDue(c, webMapTerrainManifest, out var nextDueUnixMilliseconds);
		if (!(c.ExportTerrainOnStart | flag))
		{
			string text = ((c.AutomaticStartupExportEnabled && nextDueUnixMilliseconds > 0) ? (" Automatic startup export becomes due " + FormatUtcInstant(nextDueUnixMilliseconds) + ".") : "");
			return new WebMapTerrainExportStatus
			{
				State = ((webMapTerrainManifest != null) ? "ready" : "disabled"),
				Message = ((webMapTerrainManifest != null) ? ("Using the existing terrain cache; no export is queued for the next server start." + text) : ("No terrain export is queued for the next server start." + text)),
				LastSuccessfulUnixMilliseconds = (webMapTerrainManifest?.GeneratedUnixMilliseconds ?? 0),
				ExportMode = ((webMapTerrainManifest != null) ? "cached" : "disabled"),
				WrittenTiles = 0,
				ReusedTiles = (webMapTerrainManifest?.TileCount ?? 0),
				TotalTiles = (webMapTerrainManifest?.TileCount ?? 0),
				ProcessedChunkColumns = (webMapTerrainManifest?.ChunkColumnCount ?? 0),
				TotalChunkColumns = (webMapTerrainManifest?.ChunkColumnCount ?? 0),
				TileSize = (webMapTerrainManifest?.TileSize ?? c.TerrainTileSize)
			};
		}
		string text2 = (c.ExportTerrainOnStart ? "A one-shot terrain export is queued for WorldReady." : $"Automatic startup interval is due ({c.AutomaticStartupExportIntervalDays} day(s)); terrain export is queued for WorldReady.");
		return new WebMapTerrainExportStatus
		{
			State = ((webMapTerrainManifest != null) ? "ready" : "pending"),
			Message = ((webMapTerrainManifest != null) ? ("Existing terrain cache loaded; " + text2) : text2),
			LastSuccessfulUnixMilliseconds = (webMapTerrainManifest?.GeneratedUnixMilliseconds ?? 0),
			ExportMode = ((webMapTerrainManifest != null) ? "cached" : "pending"),
			WrittenTiles = 0,
			ReusedTiles = (webMapTerrainManifest?.TileCount ?? 0),
			TotalTiles = (webMapTerrainManifest?.TileCount ?? 0),
			ProcessedChunkColumns = (webMapTerrainManifest?.ChunkColumnCount ?? 0),
			TotalChunkColumns = (webMapTerrainManifest?.ChunkColumnCount ?? 0),
			TileSize = (webMapTerrainManifest?.TileSize ?? c.TerrainTileSize)
		};
	}

	private bool IsAutomaticStartupExportDue(WebMapConfig c, WebMapTerrainManifest? manifest, out long nextDueUnixMilliseconds)
	{
		nextDueUnixMilliseconds = 0L;
		if (!c.AutomaticStartupExportEnabled)
		{
			return false;
		}
		int num = Math.Clamp(c.AutomaticStartupExportIntervalDays, 1, 365);
		long num2 = (long)num * 86400000L;
		long num3 = ((c.LastSuccessfulTerrainExportUnixMilliseconds > 0) ? c.LastSuccessfulTerrainExportUnixMilliseconds : (manifest?.GeneratedUnixMilliseconds ?? 0));
		if (num3 <= 0)
		{
			return true;
		}
		nextDueUnixMilliseconds = ((num3 > long.MaxValue - num2) ? long.MaxValue : (num3 + num2));
		return NowMs() >= nextDueUnixMilliseconds;
	}

	private static string FormatUtcInstant(long unixMilliseconds)
	{
		if (unixMilliseconds <= 0)
		{
			return "on the next server start";
		}
		try
		{
			return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToString("yyyy-MM-dd HH:mm 'UTC'");
		}
		catch
		{
			return "on the next server start";
		}
	}

	private WebMapTerrainManifest? LoadTerrainManifest()
	{
		lock (gate)
		{
			return ClonePublicManifest(terrainManifestCache);
		}
	}

	private void RefreshTerrainManifestCache()
	{
		WebMapTerrainManifest webMapTerrainManifest = null;
		try
		{
			string path = Path.Combine(mapDataRoot, "terrain", "manifest.json");
			if (File.Exists(path))
			{
				using FileStream utf8Json = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.SequentialScan);
				WebMapTerrainManifest webMapTerrainManifest2 = JsonSerializer.Deserialize<WebMapTerrainManifest>(utf8Json);
				if (webMapTerrainManifest2 != null && webMapTerrainManifest2.Version >= 7 && webMapTerrainManifest2.TileCount > 0)
				{
					webMapTerrainManifest = ClonePublicManifest(webMapTerrainManifest2);
				}
				else if (webMapTerrainManifest2 != null)
				{
					api.Logger.Warning("[ServerEssentials/WebMap] ignoring obsolete/empty terrain cache manifest v{0}; current public terrain requires v{1} with at least one tile. Queue a new export from the Web Map page.", webMapTerrainManifest2.Version, 7);
				}
			}
		}
		catch
		{
		}
		lock (gate)
		{
			terrainManifestCache = webMapTerrainManifest;
		}
	}

	private static WebMapTerrainManifest? ClonePublicManifest(WebMapTerrainManifest? source)
	{
		if (source == null)
		{
			return null;
		}
		return new WebMapTerrainManifest
		{
			Version = source.Version,
			WorldName = source.WorldName,
			SavegameIdentifier = source.SavegameIdentifier,
			GeneratedUnixMilliseconds = source.GeneratedUnixMilliseconds,
			TileSize = source.TileSize,
			TileCount = source.TileCount,
			ChunkColumnCount = source.ChunkColumnCount,
			MinTileX = source.MinTileX,
			MaxTileX = source.MaxTileX,
			MinTileZ = source.MinTileZ,
			MaxTileZ = source.MaxTileZ,
			MinWorldX = source.MinWorldX,
			MaxWorldXExclusive = source.MaxWorldXExclusive,
			MinWorldZ = source.MinWorldZ,
			MaxWorldZExclusive = source.MaxWorldZExclusive,
			Style = source.Style,
			OverviewAvailable = source.OverviewAvailable,
			OverviewWidth = source.OverviewWidth,
			OverviewHeight = source.OverviewHeight,
			OverviewTileStride = source.OverviewTileStride,
			OverviewCellSizeBlocks = source.OverviewCellSizeBlocks,
			LodFactors = (source.LodFactors?.ToArray() ?? Array.Empty<int>()),
			LodTilePixelSize = source.LodTilePixelSize,
			Tiles = new Dictionary<string, WebMapTerrainTileMetadata>(StringComparer.Ordinal)
		};
	}

	private void PublishTerrainStatus(WebMapTerrainExportStatus status)
	{
		bool flag3;
		lock (gate)
		{
			status.ServerLockActive = terrainServerLockActive;
			if (status.LastSuccessfulUnixMilliseconds <= 0 && terrainStatus.LastSuccessfulUnixMilliseconds > 0)
			{
				status.LastSuccessfulUnixMilliseconds = terrainStatus.LastSuccessfulUnixMilliseconds;
			}
			bool flag = string.Equals(terrainStatus.State, "exporting", StringComparison.OrdinalIgnoreCase);
			bool flag2 = string.Equals(status.State, "exporting", StringComparison.OrdinalIgnoreCase);
			flag3 = flag && !flag2;
			terrainStatus = status.Clone();
			if (!flag2 || !flag)
			{
				InvalidatePublicSnapshotNoLock();
			}
		}
		if (flag3)
		{
			ClearTerrainFileCache();
		}
	}

	public string UpdateTerrainNow()
	{
		return StartLiveTerrainExport(fullRebuild: false);
	}

	public string ForceTerrainExportNow()
	{
		return StartLiveTerrainExport(fullRebuild: true);
	}

	private string StartLiveTerrainExport(bool fullRebuild)
	{
		if (disposed)
		{
			return "Native Web Map service is disposed.";
		}
		WebMapConfig c = Config;
		if (!c.Enabled)
		{
			return "Enable the Web Map before starting a terrain export.";
		}
		if (!fullRebuild)
		{
			WebMapTerrainManifest webMapTerrainManifest;
			lock (gate)
			{
				webMapTerrainManifest = terrainManifestCache;
			}
			string b = api.WorldManager.SaveGame.SavegameIdentifier ?? "";
			if (webMapTerrainManifest == null || webMapTerrainManifest.Version < 13 || webMapTerrainManifest.TileSize != c.TerrainTileSize || !string.Equals(webMapTerrainManifest.SavegameIdentifier ?? "", b, StringComparison.Ordinal) || !Directory.Exists(Path.Combine(mapDataRoot, "terrain", "lod")))
			{
				return "Changed terrain update requires Full Rebuild Map first because the current terrain cache is missing or incompatible with the v11 raised-relief fast-patch/true-pyramid renderer cache.";
			}
		}
		if (Interlocked.CompareExchange(ref forcedTerrainExportInFlight, 1, 0) != 0)
		{
			return "A terrain export is already running.";
		}
		api.Logger.Warning(fullRebuild ? "[ServerEssentials/WebMap] administrator requested a FULL live terrain rebuild." : "[ServerEssentials/WebMap] administrator requested a changed-terrain live update.");
		CancellationTokenSource exportCts = new CancellationTokenSource();
		lock (gate)
		{
			forcedTerrainExportCts = exportCts;
		}
		Task.Run(async () =>
		{
			try
			{
				using (EntityHooks.SuppressLifecycleAuditForBulkWorldLoad())
				{
					WebMapTerrainExporter exporter = new WebMapTerrainExporter(api, mapDataRoot, PublishTerrainStatus, dirtyJournal);
					if (await exporter.ExportRuntimeForcedAsync(c, exportCts.Token, fullRebuild).ConfigureAwait(continueOnCapturedContext: false))
					{
						TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
						api.Event.EnqueueMainThreadTask(() =>
						{
							try
							{
								PruneDisabledTerrainOutputs(c);
								RefreshTerrainManifestCache();
								ClearNextStartExportAfterSuccess(fullRebuild ? "full live rebuild" : "changed-terrain live update");
								tcs.TrySetResult(result: true);
							}
							catch (Exception exception)
							{
								tcs.TrySetException(exception);
							}
						}, "nses-webmap-force-export-complete");
						await tcs.Task.ConfigureAwait(continueOnCapturedContext: false);
					}
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				api.Logger.Error("[ServerEssentials/WebMap] live terrain export dispatch failed: {0}: {1}", ex2.GetType().Name, ex2.Message);
			}
			finally
			{
				lock (gate)
				{
					if (forcedTerrainExportCts == exportCts)
					{
						forcedTerrainExportCts = null;
					}
				}
				exportCts.Dispose();
				Interlocked.Exchange(ref forcedTerrainExportInFlight, 0);
			}
		});
		return fullRebuild ? "Full terrain rebuild started. Progress and ETA will appear in the server console and Web Map terrain status." : "Changed terrain update started. Only dirty/new/missing tiles will be rendered when the current cache is compatible.";
	}

	private void ClearNextStartExportAfterSuccess(string reason)
	{
		bool flag = false;
		long lastSuccessfulTerrainExportUnixMilliseconds = NowMs();
		lock (gate)
		{
			config.LastSuccessfulTerrainExportUnixMilliseconds = lastSuccessfulTerrainExportUnixMilliseconds;
			config.LastTerrainExportDurationMilliseconds = Math.Max(0L, terrainStatus.TotalElapsedMilliseconds);
			if (config.ExportTerrainOnStart)
			{
				config.ExportTerrainOnStart = false;
				flag = true;
			}
			SuiteConfigStorage.Store(api, config, "NewfiesWebMap.json");
		}
		if (flag)
		{
			api.Logger.Notification("[ServerEssentials/WebMap] cleared one-shot 'export on next server start' flag after successful {0}; automatic startup interval clock was reset.", reason);
		}
		else
		{
			api.Logger.Notification("[ServerEssentials/WebMap] recorded successful {0}; automatic startup interval clock was reset.", reason);
		}
	}

	private void OnStartupTerrainPhase()
	{
		if (Interlocked.Exchange(ref startupTerrainHandlerInvoked, 1) == 0)
		{
			RunStartupTerrainExport();
		}
	}

	private void RunStartupTerrainExport()
	{
		if (disposed)
		{
			return;
		}
		RefreshCoordinateOrigin();
		WebMapConfig webMapConfig = Config;
		WebMapTerrainManifest manifest = LoadTerrainManifest();
		bool flag = IsAutomaticStartupExportDue(webMapConfig, manifest, out var nextDueUnixMilliseconds);
		bool exportTerrainOnStart = webMapConfig.ExportTerrainOnStart;
		bool flag2 = webMapConfig.Enabled & flag;
		if (!webMapConfig.Enabled || !(exportTerrainOnStart | flag))
		{
			api.Logger.Notification("[ServerEssentials/WebMap] startup terrain decision: no export. enabled={0}, oneShot={1}, intervalEnabled={2}, intervalDue={3}, incremental={4}, phase={5}.", webMapConfig.Enabled, exportTerrainOnStart, webMapConfig.AutomaticStartupExportEnabled, flag, webMapConfig.UseIncrementalTerrainExport, api.Server.CurrentRunPhase);
			lock (gate)
			{
				if (terrainStatus.State == "pending")
				{
					terrainStatus.State = "disabled";
					if (!webMapConfig.Enabled & exportTerrainOnStart)
					{
						terrainStatus.Message = "Terrain export is queued, but Native Web Map is OFF. Exporting is paused until the map is enabled.";
					}
					else if ((!webMapConfig.Enabled && webMapConfig.AutomaticStartupExportEnabled) & flag)
					{
						terrainStatus.Message = "Automatic startup export is due, but Native Web Map is OFF. Exporting remains paused until the map is enabled.";
					}
					else if (webMapConfig.AutomaticStartupExportEnabled && nextDueUnixMilliseconds > 0)
					{
						terrainStatus.Message = "No terrain export is due on this start. Automatic startup interval is next due " + FormatUtcInstant(nextDueUnixMilliseconds) + ".";
					}
					else
					{
						terrainStatus.Message = "No terrain export is queued for the next server start.";
					}
				}
				return;
			}
		}
		string text = (exportTerrainOnStart ? "startup-one-shot" : "startup-interval");
		api.Logger.Notification("[ServerEssentials/WebMap] startup terrain decision: enabled={0}, oneShot={1}, intervalEnabled={2}, intervalDue={3}, incremental={4}, trigger={5}, phase={6}.", webMapConfig.Enabled, webMapConfig.ExportTerrainOnStart, webMapConfig.AutomaticStartupExportEnabled, flag, webMapConfig.UseIncrementalTerrainExport, text, api.Server.CurrentRunPhase);
		if (flag2 && !exportTerrainOnStart)
		{
			api.Logger.Notification("[ServerEssentials/WebMap] automatic startup terrain interval is due ({0} day(s) since the last successful export window); starting the export during this server start.", webMapConfig.AutomaticStartupExportIntervalDays);
		}
		string password = api.Server.Config.Password ?? "";
		bool flag3 = false;
		try
		{
			if (webMapConfig.LockServerDuringTerrainExport)
			{
				string password2 = "NSE-MAP-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
				api.Server.Config.Password = password2;
				flag3 = true;
				lock (gate)
				{
					terrainServerLockActive = true;
					terrainStatus.ServerLockActive = true;
				}
				IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
				if (allOnlinePlayers != null)
				{
					List<IServerPlayer> list = new List<IServerPlayer>(allOnlinePlayers.Length);
					for (int i = 0; i < allOnlinePlayers.Length; i++)
					{
						if (allOnlinePlayers[i] is IServerPlayer item)
						{
							list.Add(item);
						}
					}
					for (int j = 0; j < list.Count; j++)
					{
						try
						{
							if (!PlayerDisconnectCompat.DisconnectWithReason(list[j], "Disconnected while WebMap terrain export is in progress.", "WebMap export in progress. Please reconnect in a few minutes.", out string _))
							{
								list[j].Disconnect("WebMap export in progress. Please reconnect in a few minutes.");
							}
						}
						catch
						{
						}
					}
				}
				api.Logger.Notification("[ServerEssentials/WebMap] temporarily locked new game-server joins while the startup terrain export runs.");
			}
			using (EntityHooks.SuppressLifecycleAuditForBulkWorldLoad())
			{
				WebMapTerrainExporter webMapTerrainExporter = new WebMapTerrainExporter(api, mapDataRoot, PublishTerrainStatus, dirtyJournal);
				if (webMapTerrainExporter.Export(webMapConfig, text))
				{
					PruneDisabledTerrainOutputs(webMapConfig);
					RefreshTerrainManifestCache();
					ClearNextStartExportAfterSuccess("startup terrain export");
				}
			}
		}
		finally
		{
			if (flag3)
			{
				api.Server.Config.Password = password;
				lock (gate)
				{
					terrainServerLockActive = false;
					terrainStatus.ServerLockActive = false;
				}
				api.Logger.Notification("[ServerEssentials/WebMap] restored the original game-server password after terrain export.");
			}
		}
	}

	private void EnsureSeparateDashboardPort(WebMapConfig mapConfig)
	{
		int num = dashboardPortProvider();
		if (num > 0 && mapConfig.Port == num)
		{
			int num2 = ((num < 65535) ? (num + 1) : (num - 1));
			api.Logger.Warning("[ServerEssentials/WebMap] map port {0} matched the admin dashboard port; using {1} so the listeners remain separate.", num, num2);
			mapConfig.Port = num2;
		}
	}

	private void RestartListener()
	{
		StopListener();
		if (disposed)
		{
			return;
		}
		WebMapConfig webMapConfig = Config;
		if (!webMapConfig.Enabled)
		{
			return;
		}
		try
		{
			IPAddress localaddr = ParseBindAddress(webMapConfig.BindAddress);
			TcpListener active = new TcpListener(localaddr, webMapConfig.Port);
			active.Start(32);
			CancellationTokenSource cts = new CancellationTokenSource();
			lock (gate)
			{
				listener = active;
				listenerCts = cts;
				lastError = "";
				listenerTask = Task.Run(() => AcceptLoopAsync(active, cts.Token), CancellationToken.None);
			}
			api.Logger.Notification("[ServerEssentials/WebMap] native public map listening on {0}:{1}; marker refresh={2}s, player snapshot={3}s, activity cache={4}s.", webMapConfig.BindAddress, webMapConfig.Port, 30, webMapConfig.PlayerIconRefreshSeconds, 60);
		}
		catch (Exception ex)
		{
			lock (gate)
			{
				lastError = ex.GetType().Name + ": " + ex.Message;
			}
			api.Logger.Error("[ServerEssentials/WebMap] failed to listen on {0}:{1}: {2}: {3}", webMapConfig.BindAddress, webMapConfig.Port, ex.GetType().Name, ex.Message);
		}
	}

	private static IPAddress ParseBindAddress(string value)
	{
		value = (value ?? "").Trim();
		if (value.Length == 0 || value == "*" || value == "+" || value == "0.0.0.0")
		{
			return IPAddress.Any;
		}
		if (value == "::")
		{
			return IPAddress.IPv6Any;
		}
		if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
		{
			return IPAddress.Loopback;
		}
		IPAddress address;
		return IPAddress.TryParse(value, out address) ? address : IPAddress.Any;
	}

	private void StopListener()
	{
		TcpListener tcpListener;
		CancellationTokenSource cancellationTokenSource;
		Task task;
		lock (gate)
		{
			tcpListener = listener;
			cancellationTokenSource = listenerCts;
			task = listenerTask;
			listener = null;
			listenerCts = null;
			listenerTask = null;
		}
		try
		{
			cancellationTokenSource?.Cancel();
		}
		catch
		{
		}
		try
		{
			tcpListener?.Stop();
		}
		catch
		{
		}
		try
		{
			task?.Wait(TimeSpan.FromSeconds(2L));
		}
		catch
		{
		}
		try
		{
			cancellationTokenSource?.Dispose();
		}
		catch
		{
		}
	}

	private async Task AcceptLoopAsync(TcpListener active, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			TcpClient client = null;
			try
			{
				client = await active.AcceptTcpClientAsync(token).ConfigureAwait(continueOnCapturedContext: false);
				await connectionSlots.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
				TcpClient accepted = client;
				client = null;
				Task.Run(async () =>
				{
					try
					{
						await HandleClientAsync(accepted, token).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (OperationCanceledException)
					{
					}
					catch (Exception ex5)
					{
						Exception ex6 = ex5;
						api.Logger.Warning("[ServerEssentials/WebMap] request failed: {0}: {1}", ex6.GetType().Name, ex6.Message);
					}
					finally
					{
						try
						{
							accepted.Dispose();
						}
						catch
						{
						}
						connectionSlots.Release();
					}
				}, CancellationToken.None);
			}
			catch (OperationCanceledException)
			{
				try
				{
					client?.Dispose();
					break;
				}
				catch
				{
					break;
				}
			}
			catch (ObjectDisposedException)
			{
				try
				{
					client?.Dispose();
					break;
				}
				catch
				{
					break;
				}
			}
			catch (Exception ex3)
			{
				try
				{
					client?.Dispose();
				}
				catch
				{
				}
				if (!token.IsCancellationRequested)
				{
					api.Logger.Warning("[ServerEssentials/WebMap] accept failed: {0}: {1}", ex3.GetType().Name, ex3.Message);
					await Task.Delay(500, token).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken token)
	{
		client.NoDelay = true;
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token);
		linked.CancelAfter(TimeSpan.FromSeconds(10L));
		NetworkStream stream = client.GetStream();
		HttpGetRequest request = await ReadGetRequestAsync(stream, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
		if (!(request == null))
		{
			string path = request.Target.Split('?', 2)[0];
			string styledTilePath;
			string styledLodTilePath;
			string tilePath;
			string lodTilePath;
			if (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/leaderboard", StringComparison.OrdinalIgnoreCase) || path.Equals("/leaderboard/", StringComparison.OrdinalIgnoreCase) || path.Equals("/leaderboard.html", StringComparison.OrdinalIgnoreCase))
			{
				byte[] html = GetPublicPageBytes();
				await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", html, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (path.Equals("/api/map", StringComparison.OrdinalIgnoreCase))
			{
				MarkPublicViewer();
				byte[] snapshot = GetPublicSnapshotBytes();
				await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", snapshot, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (path.Equals("/api/players", StringComparison.OrdinalIgnoreCase))
			{
				MarkPublicViewer();
				byte[] snapshot2 = GetPublicPlayerSnapshotBytes();
				await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", snapshot2, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (path.Equals("/api/leaderboard", StringComparison.OrdinalIgnoreCase))
			{
				MarkPublicViewer();
				WebMapLeaderboardSnapshot publicLeaderboard = (Config.ShowLeaderboards ? leaderboardService.GetPublicSnapshot() : WebMapLeaderboardSnapshot.Disabled("Public leaderboards are disabled in the Web Map configuration."));
				byte[] body = JsonSerializer.SerializeToUtf8Bytes(publicLeaderboard, jsonOptions);
				await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", body, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (path.Equals("/api/terrain-status", StringComparison.OrdinalIgnoreCase))
			{
				byte[] body2 = JsonSerializer.SerializeToUtf8Bytes(GetTerrainStatusSnapshot(), jsonOptions);
				await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", body2, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (TryResolveStyledTerrainTile(path, out styledTilePath) && styledTilePath != null)
			{
				await WriteCachedFileResponseAsync(stream, styledTilePath, request.IfNoneMatch, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (TryResolveStyledTerrainLodTile(path, out styledLodTilePath) && styledLodTilePath != null)
			{
				await WriteCachedFileResponseAsync(stream, styledLodTilePath, request.IfNoneMatch, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (Config.ExportNormalTerrain && path.Equals("/overview.png", StringComparison.OrdinalIgnoreCase))
			{
				string overviewPath = Path.Combine(mapDataRoot, "terrain", "overview.png");
				await WriteCachedFileResponseAsync(stream, overviewPath, request.IfNoneMatch, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (Config.ExportNormalTerrain && TryResolveTerrainTile(path, out tilePath) && tilePath != null)
			{
				await WriteCachedFileResponseAsync(stream, tilePath, request.IfNoneMatch, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (Config.ExportNormalTerrain && TryResolveTerrainLodTile(path, out lodTilePath) && lodTilePath != null)
			{
				await WriteCachedFileResponseAsync(stream, lodTilePath, request.IfNoneMatch, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
			{
				byte[] body3 = Encoding.UTF8.GetBytes("OK");
				await WriteResponseAsync(stream, 200, "text/plain; charset=utf-8", body3, linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			else
			{
				await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"), linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private bool TryResolveStyledTerrainTile(string path, out string? tilePath)
	{
		tilePath = null;
		ReadOnlySpan<char> span = path;
		if (!span.StartsWith("/styles/", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		span = span.Slice(8);
		int num = span.IndexOf('/');
		if (num <= 0 || num >= span.Length - 1)
		{
			return false;
		}
		string text = span.Slice(0, num).ToString().ToLowerInvariant();
		if (text != "sepia" && text != "topo")
		{
			return false;
		}
		WebMapConfig webMapConfig = Config;
		if (text == "sepia" && !webMapConfig.ExportSepiaTerrain)
		{
			return false;
		}
		if (text == "topo" && !webMapConfig.ExportTopographicTerrain)
		{
			return false;
		}
		span = span.Slice(num + 1);
		if (!span.StartsWith("tiles/", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		span = span.Slice(6);
		int num2 = span.IndexOf('/');
		if (num2 <= 0 || num2 >= span.Length - 1)
		{
			return false;
		}
		ReadOnlySpan<char> s = span.Slice(0, num2);
		ReadOnlySpan<char> span2 = span.Slice(num2 + 1);
		if (!span2.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		ReadOnlySpan<char> s2 = span2.Slice(0, span2.Length - 4);
		if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return false;
		}
		if (!int.TryParse(s2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2))
		{
			return false;
		}
		InlineArray6<string> buffer = default;
		buffer[0] = mapDataRoot;
		buffer[1] = "terrain";
		buffer[2] = "styles";
		buffer[3] = text;
		buffer[4] = result.ToString(CultureInfo.InvariantCulture);
		buffer[5] = result2.ToString(CultureInfo.InvariantCulture) + ".png";
		tilePath = Path.Combine(buffer);
		return true;
	}

	private bool TryResolveStyledTerrainLodTile(string path, out string? tilePath)
	{
		tilePath = null;
		ReadOnlySpan<char> span = path;
		if (!span.StartsWith("/styles/", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		span = span.Slice(8);
		int num = span.IndexOf('/');
		if (num <= 0 || num >= span.Length - 1)
		{
			return false;
		}
		string text = span.Slice(0, num).ToString().ToLowerInvariant();
		if (text != "sepia" && text != "topo")
		{
			return false;
		}
		WebMapConfig webMapConfig = Config;
		if (text == "sepia" && !webMapConfig.ExportSepiaTerrain)
		{
			return false;
		}
		if (text == "topo" && !webMapConfig.ExportTopographicTerrain)
		{
			return false;
		}
		span = span.Slice(num + 1);
		if (!span.StartsWith("lod/", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		span = span.Slice(4);
		int num2 = span.IndexOf('/');
		if (num2 <= 0 || num2 >= span.Length - 1)
		{
			return false;
		}
		ReadOnlySpan<char> s = span.Slice(0, num2);
		span = span.Slice(num2 + 1);
		int num3 = span.IndexOf('/');
		if (num3 <= 0 || num3 >= span.Length - 1)
		{
			return false;
		}
		ReadOnlySpan<char> s2 = span.Slice(0, num3);
		ReadOnlySpan<char> span2 = span.Slice(num3 + 1);
		if (!span2.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		ReadOnlySpan<char> s3 = span2.Slice(0, span2.Length - 4);
		if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < 2 || result > 1048576 || (result & (result - 1)) != 0)
		{
			return false;
		}
		if (!int.TryParse(s2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2))
		{
			return false;
		}
		if (!int.TryParse(s3, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result3))
		{
			return false;
		}
		InlineArray8<string> buffer = default;
		buffer[0] = mapDataRoot;
		buffer[1] = "terrain";
		buffer[2] = "styles";
		buffer[3] = text;
		buffer[4] = "lod";
		buffer[5] = result.ToString(CultureInfo.InvariantCulture);
		buffer[6] = result2.ToString(CultureInfo.InvariantCulture);
		buffer[7] = result3.ToString(CultureInfo.InvariantCulture) + ".png";
		tilePath = Path.Combine(buffer);
		return true;
	}

	private bool TryResolveTerrainTile(string path, out string? tilePath)
	{
		tilePath = null;
		ReadOnlySpan<char> span = path;
		if (!span.StartsWith("/tiles/", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		span = span.Slice(7);
		int num = span.IndexOf('/');
		if (num <= 0 || num >= span.Length - 1)
		{
			return false;
		}
		ReadOnlySpan<char> s = span.Slice(0, num);
		ReadOnlySpan<char> span2 = span.Slice(num + 1);
		if (!span2.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		ReadOnlySpan<char> s2 = span2.Slice(0, span2.Length - 4);
		if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return false;
		}
		if (!int.TryParse(s2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2))
		{
			return false;
		}
		tilePath = Path.Combine(mapDataRoot, "terrain", result.ToString(CultureInfo.InvariantCulture), result2.ToString(CultureInfo.InvariantCulture) + ".png");
		return true;
	}

	private bool TryResolveTerrainLodTile(string path, out string? tilePath)
	{
		tilePath = null;
		ReadOnlySpan<char> span = path;
		if (!span.StartsWith("/lod/", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		span = span.Slice(5);
		int num = span.IndexOf('/');
		if (num <= 0 || num >= span.Length - 1)
		{
			return false;
		}
		ReadOnlySpan<char> s = span.Slice(0, num);
		span = span.Slice(num + 1);
		int num2 = span.IndexOf('/');
		if (num2 <= 0 || num2 >= span.Length - 1)
		{
			return false;
		}
		ReadOnlySpan<char> s2 = span.Slice(0, num2);
		ReadOnlySpan<char> span2 = span.Slice(num2 + 1);
		if (!span2.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		ReadOnlySpan<char> s3 = span2.Slice(0, span2.Length - 4);
		if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < 2 || result > 1048576 || (result & (result - 1)) != 0)
		{
			return false;
		}
		if (!int.TryParse(s2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2))
		{
			return false;
		}
		if (!int.TryParse(s3, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result3))
		{
			return false;
		}
		InlineArray6<string> buffer = default;
		buffer[0] = mapDataRoot;
		buffer[1] = "terrain";
		buffer[2] = "lod";
		buffer[3] = result.ToString(CultureInfo.InvariantCulture);
		buffer[4] = result2.ToString(CultureInfo.InvariantCulture);
		buffer[5] = result3.ToString(CultureInfo.InvariantCulture) + ".png";
		tilePath = Path.Combine(buffer);
		return true;
	}

	private static async Task<HttpGetRequest?> ReadGetRequestAsync(NetworkStream stream, CancellationToken token)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(16388);
		try
		{
			int total = 0;
			while (total < 16384)
			{
				int read = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(2048, 16384 - total)), token).ConfigureAwait(continueOnCapturedContext: false);
				if (read <= 0)
				{
					return null;
				}
				total += read;
				int end = FindHeaderEnd(buffer, total);
				if (end < 0)
				{
					continue;
				}
				string header = Encoding.ASCII.GetString(buffer, 0, end);
				string[] lines = header.Split(new string[1] { "\r\n" }, StringSplitOptions.None);
				if (lines.Length == 0)
				{
					return null;
				}
				string[] parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}
				string ifNoneMatch = "";
				for (int i = 1; i < lines.Length; i++)
				{
					int colon = lines[i].IndexOf(':');
					if (colon > 0 && MemoryExtensions.Equals(lines[i].AsSpan(0, colon).Trim(), "If-None-Match", StringComparison.OrdinalIgnoreCase))
					{
						ifNoneMatch = lines[i].Substring(colon + 1).Trim();
						if (ifNoneMatch.Length > 200)
						{
							ifNoneMatch = ifNoneMatch.Substring(0, 200);
						}
						break;
					}
				}
				return new HttpGetRequest(parts[1], ifNoneMatch);
			}
			return null;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private static int FindHeaderEnd(byte[] data, int length)
	{
		for (int i = 0; i <= length - 4; i++)
		{
			if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
			{
				return i;
			}
		}
		return -1;
	}

	private async Task WriteCachedFileResponseAsync(NetworkStream stream, string path, string ifNoneMatch, CancellationToken token)
	{
		if (!File.Exists(path))
		{
			await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Map image not found"), token).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		FileInfo info = new FileInfo(path);
		string etag = $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";
		string extra = $"ETag: {etag}\r\nLast-Modified: {info.LastWriteTimeUtc:R}\r\n";
		if (!string.IsNullOrWhiteSpace(ifNoneMatch) && string.Equals(ifNoneMatch.Trim(), etag, StringComparison.Ordinal))
		{
			await WriteResponseAsync(stream, 304, "image/png", Array.Empty<byte>(), token, "public, max-age=31536000, immutable", extra).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		byte[] cachedBody = null;
		string cacheKey = path + "\n" + etag;
		if (info.Length <= 524288)
		{
			lock (terrainFileCacheGate)
			{
				if (terrainFileCache.TryGetValue(cacheKey, out TerrainFileCacheEntry entry))
				{
					entry.LastAccessUnixMilliseconds = NowMs();
					cachedBody = entry.Bytes;
				}
			}
			if (cachedBody == null)
			{
				byte[] loaded = await File.ReadAllBytesAsync(path, token).ConfigureAwait(continueOnCapturedContext: false);
				cachedBody = loaded;
				if (loaded.LongLength <= 524288)
				{
					AddTerrainFileCacheEntry(cacheKey, loaded);
				}
			}
		}
		long contentLength = cachedBody?.LongLength ?? info.Length;
		string headers = $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {contentLength}\r\nConnection: close\r\nCache-Control: public, max-age=31536000, immutable\r\n{extra}X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(headers).AsMemory(), token).ConfigureAwait(continueOnCapturedContext: false);
		if (cachedBody != null)
		{
			await stream.WriteAsync(cachedBody.AsMemory(), token).ConfigureAwait(continueOnCapturedContext: false);
		}
		else
		{
			using FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
			await file.CopyToAsync(stream, 65536, token).ConfigureAwait(continueOnCapturedContext: false);
		}
		await stream.FlushAsync(token).ConfigureAwait(continueOnCapturedContext: false);
	}

	private void AddTerrainFileCacheEntry(string key, byte[] bytes)
	{
		long lastAccessUnixMilliseconds = NowMs();
		lock (terrainFileCacheGate)
		{
			if (terrainFileCache.TryGetValue(key, out TerrainFileCacheEntry value))
			{
				value.LastAccessUnixMilliseconds = lastAccessUnixMilliseconds;
				return;
			}
			terrainFileCache[key] = new TerrainFileCacheEntry(bytes, lastAccessUnixMilliseconds);
			terrainFileCacheBytes += bytes.LongLength;
			while ((terrainFileCache.Count > 128 || terrainFileCacheBytes > 16777216) && terrainFileCache.Count > 0)
			{
				string text = null;
				long num = long.MaxValue;
				foreach (KeyValuePair<string, TerrainFileCacheEntry> item in terrainFileCache)
				{
					if (item.Value.LastAccessUnixMilliseconds < num)
					{
						num = item.Value.LastAccessUnixMilliseconds;
						text = item.Key;
					}
				}
				if (text == null || !terrainFileCache.Remove(text, out TerrainFileCacheEntry value2))
				{
					break;
				}
				terrainFileCacheBytes -= value2.Bytes.LongLength;
			}
		}
	}

	private void ClearTerrainFileCache()
	{
		lock (terrainFileCacheGate)
		{
			terrainFileCache.Clear();
			terrainFileCacheBytes = 0L;
		}
	}

	private static async Task WriteJsonResponseAsync<T>(NetworkStream stream, T value, JsonSerializerOptions options, CancellationToken token)
	{
		await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'\r\n\r\n").AsMemory(), token).ConfigureAwait(continueOnCapturedContext: false);
		await JsonSerializer.SerializeAsync((Stream)stream, value, options, token).ConfigureAwait(continueOnCapturedContext: false);
		await stream.FlushAsync(token).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken token, string cacheControl = "no-store", string extraHeaders = "")
	{
		if (1 == 0)
		{
		}
		string text = status switch
		{
			200 => "OK", 
			304 => "Not Modified", 
			404 => "Not Found", 
			_ => "Error", 
		};
		if (1 == 0)
		{
		}
		string reason = text;
		string headers = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: {cacheControl}\r\n{extraHeaders}X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(headers).AsMemory(), token).ConfigureAwait(continueOnCapturedContext: false);
		if (body.Length != 0)
		{
			await stream.WriteAsync(body.AsMemory(), token).ConfigureAwait(continueOnCapturedContext: false);
		}
		await stream.FlushAsync(token).ConfigureAwait(continueOnCapturedContext: false);
	}

	private byte[] GetPublicPageBytes()
	{
		lock (gate)
		{
			if (publicPageCache.Length != 0)
			{
				return publicPageCache;
			}
		}
		byte[] bytes = Encoding.UTF8.GetBytes(BuildPublicPage());
		lock (gate)
		{
			if (publicPageCache.Length == 0)
			{
				publicPageCache = bytes;
			}
			return publicPageCache;
		}
	}

	private bool HasRecentPublicViewer()
	{
		long num = Interlocked.Read(in lastPublicViewerUnixMilliseconds);
		return num > 0 && NowMs() - num <= Math.Max(90000L, 90000L);
	}

	private void MarkPublicViewer()
	{
		Interlocked.Exchange(ref lastPublicViewerUnixMilliseconds, NowMs());
		WebMapConfig webMapConfig = Config;
		if (!webMapConfig.Enabled || !webMapConfig.AllowLivePlayerData || !webMapConfig.ShowPlayers || NowMs() - Interlocked.Read(in playerCacheUnixMilliseconds) < (long)webMapConfig.PlayerIconRefreshSeconds * 1000L || Interlocked.CompareExchange(ref playerRefreshQueued, 1, 0) != 0)
		{
			return;
		}
		try
		{
			api.Event.EnqueueMainThreadTask(() =>
			{
				try
				{
					CapturePlayersIfDue();
				}
				finally
				{
					Interlocked.Exchange(ref playerRefreshQueued, 0);
				}
			}, "nses-webmap-viewer-player-refresh");
		}
		catch
		{
			Interlocked.Exchange(ref playerRefreshQueued, 0);
		}
	}

	private byte[] GetPublicPlayerSnapshotBytes()
	{
		lock (gate)
		{
			bool flag = config.Enabled && config.AllowLivePlayerData && config.ShowPlayers;
			return JsonSerializer.SerializeToUtf8Bytes(new
			{
				playerRefreshSeconds = config.PlayerIconRefreshSeconds,
				playerSnapshotUnixMilliseconds = playerCacheUnixMilliseconds,
				showPlayers = flag,
				activePlayerCount = (flag ? playerCache.Count : 0),
				players = (flag ? ClonePlayers(playerCache) : new List<WebMapPlayerMarker>())
			}, jsonOptions);
		}
	}

	private byte[] GetPublicSnapshotBytes()
	{
		long num = NowMs();
		lock (gate)
		{
			if (publicSnapshotCache.Length != 0 && num - publicSnapshotCacheUnixMilliseconds < 5000)
			{
				return publicSnapshotCache;
			}
		}
		lock (publicSnapshotBuildGate)
		{
			num = NowMs();
			lock (gate)
			{
				if (publicSnapshotCache.Length != 0 && num - publicSnapshotCacheUnixMilliseconds < 5000)
				{
					return publicSnapshotCache;
				}
			}
			long num2;
			lock (gate)
			{
				num2 = publicSnapshotGeneration;
			}
			Stopwatch stopwatch = Stopwatch.StartNew();
			byte[] result = JsonSerializer.SerializeToUtf8Bytes(GetSnapshot(null, includeWaypointAudit: false), jsonOptions);
			stopwatch.Stop();
			SuiteTimingRegistry.Record("Web Map public snapshot", stopwatch.Elapsed.TotalMilliseconds);
			lock (gate)
			{
				if (!disposed && num2 == publicSnapshotGeneration)
				{
					publicSnapshotCache = result;
					publicSnapshotCacheUnixMilliseconds = num;
				}
				return result;
			}
		}
	}

	private string BuildPublicPage()
	{
		WebMapConfig webMapConfig = Config;
		string value = Html(webMapConfig.Title);
		string value2 = Html(webMapConfig.DashboardUrl);
		int value3 = Math.Clamp(dashboardPortProvider(), 0, 65535);
		return $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover,user-scalable=no\"><title>{value}</title>\n<style>\n:root{{color-scheme:dark;font-family:Inter,system-ui,Arial,sans-serif;--map-bg:#10161d;--body:#10161d;--text:#e8edf4;--top:#121922;--border:#2b3949;--muted:#94a3b8;--button:#1f2937;--buttonBorder:#475569;--panel:#111923f2;--grid:#2736477a;--mapLabel:#f8fafc;--markerStroke:#0b0f14;--accent:#38bdf8;--good:#22c55e;--major:#f59e0b;--danger:#ef4444;--shadow:#0007}}\n:root[data-theme=\"light\"]{{color-scheme:light;--map-bg:#e8edf3;--body:#e8edf3;--text:#0f172a;--top:#ffffff;--border:#cbd5e1;--muted:#475569;--button:#f8fafc;--buttonBorder:#94a3b8;--panel:#fffffff5;--grid:#64748b55;--mapLabel:#0f172a;--markerStroke:#ffffff;--shadow:#33415533}}\n*{{box-sizing:border-box}}html,body{{width:100%;height:100%;margin:0;overscroll-behavior:none}}@supports(height:100dvh){{html,body{{height:100dvh}}}}body{{background:var(--map-bg);color:var(--text);overflow:hidden}}.top{{height:56px;display:flex;align-items:center;gap:10px;padding:0 14px;background:var(--top);border-bottom:1px solid var(--border);position:relative;z-index:20}}.brandWrap{{min-width:0}}.brand{{font-weight:850;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:36vw}}.sub{{font-size:10px;color:var(--muted);white-space:nowrap}}.grow{{flex:1}}.status{{font-size:11px;color:var(--muted);font-variant-numeric:tabular-nums;white-space:nowrap}}.topStats{{display:flex;gap:6px}}.pill{{border:1px solid var(--border);background:var(--panel);border-radius:999px;padding:6px 9px;font-size:11px;white-space:nowrap}}.button,.iconBtn,.admin{{border:1px solid var(--buttonBorder);background:var(--button);color:var(--text);border-radius:10px;cursor:pointer;text-decoration:none;display:inline-flex;align-items:center;justify-content:center}}.button{{padding:8px 11px}}.leaderboardTopBtn{{gap:6px;font-weight:850;white-space:nowrap}}.leaderboardTopBtn[aria-pressed=\"true\"]{{border-color:#c99a48;box-shadow:0 0 0 1px #c99a4855 inset}}.langSelect{{border:1px solid var(--buttonBorder);background:var(--button);color:var(--text);border-radius:10px;padding:7px 8px;font-weight:700}}.iconBtn{{width:42px;height:42px;font-size:18px;box-shadow:0 5px 18px var(--shadow)}}.map{{position:absolute;left:0;right:0;top:56px;bottom:0;background:var(--map-bg);overflow:hidden}}.canvas{{width:100%;height:100%;display:block;background:var(--map-bg);touch-action:none;user-select:none;-webkit-user-select:none}}.toolbar{{position:absolute;right:12px;top:12px;display:flex;flex-direction:column;gap:8px;z-index:10}}.rightMapStack{{position:absolute;right:12px;bottom:12px;width:min(380px,calc(100vw - 24px));display:flex;flex-direction:column;align-items:stretch;gap:10px;z-index:12;pointer-events:none}}.rightMapStack>*{{pointer-events:auto}}.layerDock{{display:flex;justify-content:flex-end;z-index:12}}.layerDock .iconBtn{{width:168px;height:50px;padding:0 14px;font-size:14px;font-weight:850;gap:9px;justify-content:flex-start;background:linear-gradient(180deg,var(--button),var(--top));border-color:color-mix(in srgb,var(--accent) 38%,var(--buttonBorder));box-shadow:0 8px 24px var(--shadow)}}.layerDock .iconBtn:hover{{border-color:var(--accent);transform:translateY(-1px)}}.layerCount{{margin-left:auto;min-width:25px;padding:3px 7px;border-radius:999px;background:#0ea5e933;border:1px solid #38bdf877;color:var(--text);font-size:10px;text-align:center}}.panel{{position:absolute;background:var(--panel);border:1px solid var(--border);border-radius:12px;box-shadow:0 10px 32px var(--shadow);z-index:11;overflow:hidden}}.panelHead{{display:flex;align-items:center;gap:8px;padding:10px 12px;font-weight:800;border-bottom:1px solid var(--border)}}.panelBody{{padding:10px 12px;overflow:auto}}.closeBtn{{margin-left:auto;border:0;background:transparent;color:var(--muted);font-size:20px;cursor:pointer}}.floatingPanel{{right:64px;top:12px;width:min(360px,calc(100vw - 90px));max-height:70vh;display:none}}#helpPanel{{position:fixed;right:72px;top:76px;width:min(390px,calc(100vw - 96px));max-height:calc(100vh - 92px)}}#layersPanel{{position:relative;right:auto;top:auto;bottom:auto;width:100%;max-height:min(60vh,590px);align-self:stretch;border-color:color-mix(in srgb,var(--accent) 28%,var(--border));backdrop-filter:blur(14px)}}#layersPanel .panelHead{{background:linear-gradient(90deg,#0ea5e91a,transparent)}}#layersPanel .panelBody>.small{{font-weight:800;text-transform:uppercase;letter-spacing:.07em;font-size:9px}}#layersPanel .layerRow{{margin:3px 0;padding:9px 10px;border:1px solid transparent;border-radius:9px;background:#ffffff05}}#layersPanel .layerRow:hover{{border-color:var(--border);background:#ffffff0a}}#layersPanel select{{background:var(--button);color:var(--text);border:1px solid var(--buttonBorder);border-radius:9px;padding:9px}}#layersPanel button{{padding:8px 10px;border:1px solid var(--buttonBorder);border-radius:8px;background:var(--button);color:var(--text);cursor:pointer}}.floatingPanel.open{{display:block}}.layerRow{{display:flex;align-items:center;gap:10px;padding:9px 0;font-size:14px}}.layerRow input{{width:20px;height:20px}}.searchRow{{display:flex;gap:7px}}.searchRow input{{flex:1;min-width:0;background:var(--button);color:var(--text);border:1px solid var(--buttonBorder);border-radius:8px;padding:9px}}.searchResults{{margin-top:8px}}.searchItem{{display:block;width:100%;text-align:left;padding:8px;border:0;border-bottom:1px solid var(--border);background:transparent;color:var(--text);cursor:pointer}}.searchItem:last-child{{border-bottom:0}}.helpCode{{display:block;padding:7px 8px;margin:5px 0;background:var(--button);border:1px solid var(--border);border-radius:7px;font:12px ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere}}.helpNote{{font-size:11px;color:var(--muted);line-height:1.45}}.playerPanel{{position:relative;right:auto;bottom:auto;width:100%;max-height:48vh}}.playerPanel.collapsed .playerPanelBody{{display:none}}.playerPanelBody{{max-height:calc(48vh - 44px);overflow:auto}}.playerRow{{padding:9px 11px;border-bottom:1px solid var(--border);display:grid;grid-template-columns:1fr auto;gap:3px 10px;font-size:12px;cursor:pointer}}.playerRow:hover{{background:#ffffff08}}.playerName{{font-weight:800}}.playerMeta{{color:var(--muted);font-size:10px}}.playerPing{{font-weight:700;text-align:right}}.exportProgress{{display:none;position:absolute;left:12px;top:12px;width:min(480px,calc(100vw - 460px));min-width:290px;padding:10px 12px;background:var(--panel);border:1px solid var(--border);border-radius:11px;box-shadow:0 8px 30px var(--shadow);z-index:9}}.exportProgress.active{{display:block}}.exportProgressHead{{display:flex;gap:8px;font-size:12px;font-weight:800}}.exportProgressPct{{margin-left:auto}}.exportProgressTrack{{height:11px;margin-top:7px;border-radius:999px;background:var(--button);border:1px solid var(--buttonBorder);overflow:hidden}}.exportProgressFill{{height:100%;width:0;background:linear-gradient(90deg,#3b82f6,#22c55e);transition:width .3s ease}}.exportProgressMeta{{display:flex;gap:8px;flex-wrap:wrap;margin-top:6px;font-size:10px;color:var(--muted)}}.coords{{position:absolute;left:12px;bottom:12px;background:var(--panel);border:1px solid var(--border);border-radius:999px;padding:7px 10px;font-size:11px;font-weight:750;font-variant-numeric:tabular-nums;z-index:8}}.markerCard{{display:none;left:14px;bottom:54px;width:min(380px,calc(100vw - 28px));padding:14px 15px 15px;border-color:color-mix(in srgb,var(--accent) 30%,var(--border));border-radius:14px;backdrop-filter:blur(14px);box-shadow:0 16px 42px var(--shadow)}}.markerCard:before{{content:\"\";position:absolute;left:0;top:12px;bottom:12px;width:3px;border-radius:0 4px 4px 0;background:var(--accent)}}.markerCard.open{{display:block;animation:markerIn .14s ease-out}}@keyframes markerIn{{from{{opacity:0;transform:translateY(6px)}}to{{opacity:1;transform:translateY(0)}}}}.markerCard .closeBtn{{position:absolute;right:7px;top:5px;width:34px;height:34px;border-radius:8px}}.markerTitle{{font-weight:850;font-size:16px;padding-right:34px}}.markerType{{display:inline-block;margin-left:6px;font-size:9px;border:1px solid var(--border);border-radius:999px;padding:2px 6px;color:var(--muted);vertical-align:2px}}.markerText{{margin-top:7px;font-size:12px;line-height:1.45}}.markerActions{{display:flex;gap:7px;margin-top:10px;flex-wrap:wrap}}.markerActions button{{padding:7px 9px;border:1px solid var(--buttonBorder);border-radius:8px;background:var(--button);color:var(--text);cursor:pointer}}.markerActions button,.contextMenu button,.searchRow .button,#layersPanel button,.layerDock .iconBtn{{white-space:normal;overflow-wrap:anywhere;word-break:normal;line-height:1.15;min-width:0;max-width:100%;height:auto;min-height:38px;text-align:center}}.layerDock .iconBtn{{min-height:50px}}.panelHead{{min-width:0}}.panelHead>span{{min-width:0;overflow-wrap:anywhere}}.toast{{position:absolute;left:50%;bottom:18px;transform:translateX(-50%) translateY(20px);opacity:0;background:var(--panel);border:1px solid var(--border);border-radius:999px;padding:8px 12px;font-size:11px;z-index:30;transition:.2s;pointer-events:none}}.toast.show{{opacity:1;transform:translateX(-50%) translateY(0)}}.iconBtn.active{{outline:2px solid var(--accent);background:#0ea5e933}}.contextMenu{{display:none;position:absolute;min-width:190px;padding:6px;background:var(--panel);border:1px solid var(--border);border-radius:11px;box-shadow:0 12px 34px var(--shadow);z-index:40}}.contextMenu.open{{display:block}}.contextMenu button{{display:block;width:100%;border:0;background:transparent;color:var(--text);text-align:left;padding:9px 10px;border-radius:7px;cursor:pointer;font-size:12px}}.contextMenu button:hover{{background:var(--button)}}.contextCoords{{padding:5px 10px 7px;color:var(--muted);font:11px ui-monospace,SFMono-Regular,Consolas,monospace;border-bottom:1px solid var(--border);margin-bottom:4px}}.measureReadout{{position:absolute;left:50%;top:12px;transform:translateX(-50%);background:var(--panel);border:1px solid var(--border);border-radius:999px;padding:7px 11px;font-size:11px;font-weight:800;z-index:8;display:none}}.measureReadout.active{{display:block}}\n@media(max-width:1050px){{.status{{display:none}}.brand{{max-width:30vw}}.exportProgress{{width:min(430px,calc(100vw - 420px))}}}}\n@media(max-width:760px),(hover:none) and (pointer:coarse){{.top{{height:50px;padding:0 max(8px,env(safe-area-inset-right)) 0 max(9px,env(safe-area-inset-left));gap:6px}}.map{{top:50px;bottom:0}}.brand{{font-size:13px;max-width:47vw}}.sub,.datePill,.themeToggle,.admin,.status{{display:none}}.leaderboardTopLabel{{display:none}}.leaderboardTopBtn{{width:42px;height:38px;padding:0}}.langSelect{{max-width:92px;padding:6px}}.pill{{padding:5px 8px;font-size:10px}}.toolbar{{right:max(8px,env(safe-area-inset-right));top:8px;display:grid;grid-template-columns:repeat(2,44px);gap:6px}}.rightMapStack{{left:max(8px,env(safe-area-inset-left));right:max(8px,env(safe-area-inset-right));bottom:max(8px,env(safe-area-inset-bottom));width:auto;gap:8px}}.layerDock{{justify-content:flex-end}}.layerDock .iconBtn{{width:138px;height:48px;font-size:14px}}.iconBtn{{width:44px;height:44px;touch-action:manipulation}}.floatingPanel,#helpPanel{{position:fixed;left:max(8px,env(safe-area-inset-left));right:max(8px,env(safe-area-inset-right));top:auto;bottom:max(8px,env(safe-area-inset-bottom));width:auto;max-height:min(68dvh,560px);border-radius:16px}}#layersPanel{{position:fixed;left:max(8px,env(safe-area-inset-left));right:max(8px,env(safe-area-inset-right));top:auto;bottom:max(8px,env(safe-area-inset-bottom));width:auto;max-height:min(68dvh,560px);align-self:auto}}.panelHead{{min-height:48px;padding:11px 14px}}.panelBody{{padding:12px 14px}}.closeBtn{{min-width:40px;min-height:40px}}.layerRow{{min-height:44px;padding:8px 0}}.playerPanel{{position:fixed;left:max(8px,env(safe-area-inset-left));right:max(8px,env(safe-area-inset-right));top:auto;bottom:max(8px,env(safe-area-inset-bottom));width:auto;max-height:min(62dvh,520px);border-radius:16px;display:none}}.playerPanel.mobileOpen{{display:block}}.playerPanelBody{{max-height:calc(min(62dvh,520px) - 48px)}}.playerRow{{padding:11px 13px;min-height:48px}}.coords{{left:max(8px,env(safe-area-inset-left));bottom:max(8px,env(safe-area-inset-bottom));font-size:10px}}.markerCard{{position:fixed;left:max(8px,env(safe-area-inset-left));right:max(8px,env(safe-area-inset-right));bottom:calc(max(8px,env(safe-area-inset-bottom)) + 52px);width:auto;max-height:48dvh;overflow:auto;border-radius:16px}}.exportProgress{{left:max(8px,env(safe-area-inset-left));right:112px;top:8px;width:auto;min-width:0;padding:8px 10px}}.exportProgressMeta{{display:none}}.toast{{bottom:calc(max(12px,env(safe-area-inset-bottom)) + 52px);max-width:calc(100vw - 24px);text-align:center}}.contextMenu{{max-width:calc(100vw - 16px)}}.measureReadout{{top:8px;max-width:calc(100vw - 125px);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}}.searchRow input,.searchRow .button,select,button{{min-height:42px}}}}\n:root.mobileUi .top{{height:50px;padding:0 max(8px,env(safe-area-inset-right)) 0 max(9px,env(safe-area-inset-left));gap:6px}}:root.mobileUi .map{{top:50px}}:root.mobileUi .brand{{font-size:13px;max-width:47vw}}:root.mobileUi .sub,:root.mobileUi .datePill,:root.mobileUi .themeToggle,:root.mobileUi .admin,:root.mobileUi .status{{display:none}}:root.mobileUi .pill{{padding:5px 8px;font-size:10px}}:root.mobileUi .toolbar{{right:max(8px,env(safe-area-inset-right));top:8px;display:grid;grid-template-columns:repeat(2,44px);gap:6px}}:root.mobileUi .iconBtn{{width:44px;height:44px;touch-action:manipulation}}:root.mobileUi .rightMapStack{{left:max(8px,env(safe-area-inset-left));right:max(8px,env(safe-area-inset-right));bottom:max(8px,env(safe-area-inset-bottom));width:auto;gap:8px}}:root.mobileUi .layerDock{{justify-content:flex-end}}:root.mobileUi .layerDock .iconBtn{{width:138px;height:48px;font-size:14px}}:root.mobileUi .floatingPanel,:root.mobileUi #helpPanel,:root.mobileUi #layersPanel{{position:fixed;left:max(8px,env(safe-area-inset-left));right:max(8px,env(safe-area-inset-right));top:auto;bottom:max(8px,env(safe-area-inset-bottom));width:auto;max-height:min(68dvh,560px);border-radius:16px}}:root.mobileUi #layersPanel{{align-self:auto}}:root.mobileUi .panelHead{{min-height:48px;padding:11px 14px}}:root.mobileUi .panelBody{{padding:12px 14px}}:root.mobileUi .closeBtn{{min-width:40px;min-height:40px}}:root.mobileUi .layerRow{{min-height:44px;padding:8px 0}}:root.mobileUi .playerPanel{{position:fixed;left:max(8px,env(safe-area-inset-left));right:max(8px,env(safe-area-inset-right));top:auto;bottom:max(8px,env(safe-area-inset-bottom));width:auto;max-height:min(62dvh,520px);border-radius:16px;display:none}}:root.mobileUi .playerPanel.mobileOpen{{display:block}}:root.mobileUi .playerPanelBody{{max-height:calc(min(62dvh,520px) - 48px)}}:root.mobileUi .playerRow{{padding:11px 13px;min-height:48px}}:root.mobileUi .coords{{left:max(8px,env(safe-area-inset-left));bottom:max(8px,env(safe-area-inset-bottom));font-size:10px}}:root.mobileUi .markerCard{{position:fixed;left:max(8px,env(safe-area-inset-left));right:max(8px,env(safe-area-inset-right));bottom:calc(max(8px,env(safe-area-inset-bottom)) + 52px);width:auto;max-height:48dvh;overflow:auto;border-radius:16px}}:root.mobileUi .exportProgress{{left:max(8px,env(safe-area-inset-left));right:112px;top:8px;width:auto;min-width:0;padding:8px 10px}}:root.mobileUi .exportProgressMeta{{display:none}}:root.mobileUi .toast{{bottom:calc(max(12px,env(safe-area-inset-bottom)) + 52px);max-width:calc(100vw - 24px);text-align:center}}:root.mobileUi .contextMenu{{max-width:calc(100vw - 16px)}}:root.mobileUi .measureReadout{{top:8px;max-width:calc(100vw - 125px);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}}\n\n/* Community leaderboard overlay */\n.leaderboardOverlay{{position:absolute;inset:0;z-index:30;display:none;padding:18px;background:linear-gradient(180deg,#071019e8,#071019f5);backdrop-filter:blur(8px);overflow:auto}}\n.leaderboardOverlay.open{{display:block}}\n.leaderboardShell{{max-width:1500px;min-height:calc(100% - 2px);margin:0 auto;background:linear-gradient(180deg,#111820,#0b1117);border:1px solid #465463;border-radius:18px;box-shadow:0 22px 70px #000b;overflow:hidden}}\n.leaderboardHero{{position:sticky;top:0;z-index:3;display:flex;align-items:center;gap:15px;padding:17px 22px;background:linear-gradient(135deg,#17252d,#0d141a);border-bottom:2px solid #c99a48}}\n.leaderboardCrown{{font-size:34px;filter:drop-shadow(0 2px 5px #000)}}\n.leaderboardTitle{{font-size:25px;font-weight:950;color:#f3d494;letter-spacing:.03em}}.leaderboardSub{{font-size:11px;color:#9fb0bb;margin-top:2px}}\n.leaderboardHero .closeBtn{{margin-left:auto;color:#e7d6b1;font-size:28px}}\n.leaderboardTabs{{display:flex;gap:8px;padding:11px 18px;background:#101821;border-bottom:1px solid #293947;position:sticky;top:71px;z-index:2}}\n.lbTab{{border:1px solid #3a4b5b;background:#17232c;color:#c9d5dc;border-radius:999px;padding:8px 15px;font-weight:850;cursor:pointer}}.lbTab.active{{background:#274d68;border-color:#5ca8d6;color:#fff;box-shadow:inset 0 0 0 1px #5ca8d633}}\n.leaderboardBody{{padding:18px}}.lbNotice{{padding:11px 13px;border:1px solid #334758;background:#0e1820;border-radius:10px;color:#9fb0bb;font-size:12px;margin-bottom:14px}}\n.lbGrid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:13px}}.lbOverviewGrid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:13px}}\n.lbCard,.skillCard{{background:linear-gradient(180deg,#121b23,#0c1319);border:1px solid #334553;border-radius:13px;overflow:hidden;box-shadow:0 8px 22px #0005}}\n.lbCard{{border-top:3px solid #c99a48}}.lbCardHead{{display:flex;align-items:center;gap:9px;padding:12px 14px;border-bottom:1px solid #293947;font-weight:900;color:#f0d39a}}.lbCardIcon{{font-size:21px}}\n.lbRows{{padding:5px 10px 9px}}.lbRow{{display:grid;grid-template-columns:30px minmax(0,1fr) auto;gap:8px;align-items:center;padding:8px 4px;border-bottom:1px solid #24333f;font-size:12px}}.lbRow:last-child{{border-bottom:0}}.lbRank{{font-weight:950;color:#f2cb72;font-size:15px}}.lbPlayer{{font-weight:800;color:#e8edf4;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;cursor:pointer}}.lbPlayer:hover{{color:#8fd4ff}}.lbOnline{{display:inline-block;width:7px;height:7px;border-radius:50%;background:#41c86a;margin-right:5px;box-shadow:0 0 7px #41c86a88}}.lbValue{{font-weight:850;color:#c7d5df;font-variant-numeric:tabular-nums;text-align:right}}\n.skillCard{{border-color:color-mix(in srgb,var(--skillAccent) 75%,#263847);border-top:3px solid var(--skillAccent);box-shadow:0 8px 22px #0005,0 0 20px color-mix(in srgb,var(--skillAccent) 12%,transparent)}}.skillHead{{display:flex;align-items:center;gap:9px;padding:11px 13px;background:color-mix(in srgb,var(--skillAccent) 12%,#101820);border-bottom:1px solid color-mix(in srgb,var(--skillAccent) 42%,#253542)}}.skillIcon{{width:35px;height:35px;display:flex;align-items:center;justify-content:center;border-radius:50%;border:1px solid var(--skillAccent);background:#081015;font-size:20px}}.skillName{{font-size:16px;font-weight:950}}.skillTableHead,.skillEntry{{display:grid;grid-template-columns:28px minmax(0,1fr) 58px 76px;gap:6px;align-items:center}}.skillTableHead{{padding:7px 11px 5px;color:#8fa2af;font-size:9px;text-transform:uppercase;letter-spacing:.06em}}.skillEntry{{padding:7px 11px;border-top:1px solid #22313c;font-size:11px}}.skillEntry .rank{{color:#f2cb72;font-weight:950}}.skillEntry .name{{font-weight:800;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;cursor:pointer}}.skillEntry .name:hover{{color:#8fd4ff}}.skillEntry .level,.skillEntry .xp{{text-align:right;font-variant-numeric:tabular-nums}}.skillEntry .level{{font-weight:900;color:#e6edf3}}.skillEntry .xp{{color:#aab9c4}}\n.lbStatusBar{{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:14px}}.lbStatusChip{{display:inline-flex;align-items:center;gap:6px;padding:7px 10px;border:1px solid #33495a;border-radius:999px;background:#0d1820;color:#b7c7d1;font-size:10px;font-weight:800}}.lbStatusChip.good{{border-color:#356844;color:#9de6af;background:#102018}}.lbStatusChip.gold{{border-color:#6d5931;color:#f0d08b;background:#211b10}}.lbPodiumWrap{{margin:0 0 14px;padding:14px;border:1px solid #4a4534;border-radius:14px;background:linear-gradient(135deg,#1a1b17,#0b1117);box-shadow:inset 0 1px 0 #fff1}}.lbPodiumTitle{{text-align:center;font-size:11px;font-weight:950;letter-spacing:.14em;text-transform:uppercase;color:#d7bd80;margin-bottom:12px}}.lbPodium{{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:9px;align-items:end}}.lbPodiumPlace{{position:relative;padding:12px 10px;text-align:center;border:1px solid #334553;border-radius:12px;background:linear-gradient(180deg,#15202a,#0b1218);min-height:105px;cursor:pointer;transition:transform .15s ease,border-color .15s ease}}.lbPodiumPlace:hover{{transform:translateY(-2px);border-color:#6d8ba2}}.lbPodiumPlace.first{{min-height:126px;border-color:#b68a38;box-shadow:0 0 24px #c99a4824}}.lbPodiumPlace.second{{border-color:#697886}}.lbPodiumPlace.third{{border-color:#785238}}.lbPodiumMedal{{font-size:24px;line-height:1}}.lbPodiumPlayer{{font-weight:950;font-size:14px;margin-top:5px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}}.lbPodiumValue{{font-size:11px;color:#aab9c4;margin-top:4px}}.lbRank.rank1{{color:#ffd86b;text-shadow:0 0 8px #d9a92844}}.lbRank.rank2{{color:#cdd7df}}.lbRank.rank3{{color:#d6a06c}}.lbTools{{display:flex;align-items:end;gap:9px;flex-wrap:wrap;margin-bottom:13px;padding:10px;border:1px solid #2d414f;border-radius:12px;background:#0d161d}}.lbTools label{{display:grid;gap:4px;color:#91a5b2;font-size:9px;font-weight:900;text-transform:uppercase;letter-spacing:.08em}}.lbTools input,.lbTools select{{min-height:36px;border:1px solid #405566;border-radius:9px;background:#111d25;color:#e8edf4;padding:7px 9px}}.lbTools input{{min-width:240px}}.lbTools .lbToolCount{{margin-left:auto;color:#8fa2af;font-size:10px;padding-bottom:9px}}.skillCard{{transition:transform .15s ease,border-color .15s ease}}.skillCard:hover{{transform:translateY(-2px)}}.skillEntry:nth-child(3){{background:linear-gradient(90deg,color-mix(in srgb,var(--skillAccent) 8%,transparent),transparent)}}.lbCard{{transition:transform .15s ease,border-color .15s ease}}.lbCard:hover{{transform:translateY(-2px);border-color:#586b79}}.lbTopSkill{{border-left:3px solid var(--skillAccent,#4a6171)}}.lbSectionTitle{{margin:18px 0 9px;font-size:11px;font-weight:950;text-transform:uppercase;letter-spacing:.12em;color:#90a4b2}}.lbEmpty{{padding:18px;text-align:center;color:#8295a2;font-size:12px}}.lbProfile{{max-width:820px;margin:0 auto}}.lbProfileHero{{padding:18px;border:1px solid #496071;border-top:3px solid #c99a48;border-radius:14px;background:linear-gradient(135deg,#16232b,#0c141a)}}.lbProfileName{{font-size:28px;font-weight:950;color:#f3d494}}.lbProfileStats{{display:grid;grid-template-columns:repeat(auto-fit,minmax(145px,1fr));gap:9px;margin-top:14px}}.lbStat{{padding:11px;border:1px solid #314452;border-radius:10px;background:#0b131a}}.lbStatLabel{{font-size:9px;text-transform:uppercase;letter-spacing:.08em;color:#879aa7}}.lbStatValue{{font-size:17px;font-weight:900;margin-top:4px}}.lbTopSkills{{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:9px;margin-top:13px}}.lbTopSkill{{display:flex;gap:10px;align-items:center;padding:10px;border:1px solid #30424f;border-radius:10px;background:#0b141a}}.lbBack{{margin-bottom:12px}}.lbFooterNote{{margin-top:14px;color:#80929e;font-size:10px;text-align:center}}\n@media(max-width:760px){{.leaderboardOverlay{{padding:0}}.leaderboardShell{{border-radius:0;border-left:0;border-right:0}}.leaderboardHero{{padding:12px 14px}}.leaderboardCrown{{font-size:27px}}.leaderboardTitle{{font-size:20px}}.leaderboardSub{{display:none}}.leaderboardTabs{{top:57px;padding:8px 10px;overflow:auto}}.lbTab{{padding:7px 12px;white-space:nowrap}}.leaderboardBody{{padding:10px}}.lbGrid,.lbOverviewGrid{{grid-template-columns:1fr}}.skillTableHead,.skillEntry{{grid-template-columns:25px minmax(0,1fr) 48px 68px}}.leaderboardOverlay .closeBtn{{min-width:38px;min-height:38px}}.lbPodium{{grid-template-columns:1fr}}.lbPodiumPlace,.lbPodiumPlace.first{{min-height:0}}.lbTools{{align-items:stretch}}.lbTools label,.lbTools input,.lbTools select{{width:100%;min-width:0}}.lbTools .lbToolCount{{margin-left:0;padding:0}}.lbStatusBar{{gap:6px}}.lbStatusChip{{padding:6px 8px}}}}\n\n</style></head><body>\n<div class=\"top\"><div class=\"brandWrap\"><div class=\"brand\">{value}</div><div class=\"sub\" data-i18n=\"subtitle\">Server Essentials Web Map</div></div><div class=\"grow\"></div><div id=\"status\" class=\"status\" data-i18n=\"loading\">Loading…</div><div class=\"topStats\"><div class=\"pill datePill\">\ud83d\udcc5 <span id=\"mapDate\">—</span></div><div class=\"pill\">\ud83d\udc65 <b id=\"activePlayers\">0</b></div></div><button id=\"leaderboardBtn\" class=\"button leaderboardTopBtn\" type=\"button\" title=\"Leaderboards\"><span aria-hidden=\"true\">\ud83c\udfc6</span><span class=\"leaderboardTopLabel\">Leaderboard</span></button><select id=\"langSelect\" class=\"langSelect\" title=\"Language\"><option value=\"en\">English</option><option value=\"de\">Deutsch</option><option value=\"fr\">Français</option><option value=\"ru\">Русский</option></select><button id=\"themeToggle\" class=\"button themeToggle\" type=\"button\">Light</button><a id=\"admin\" class=\"button admin\" href=\"#\" data-i18n=\"admin\">Admin</a></div>\n<div class=\"map\" id=\"mapWrap\">\n<canvas id=\"map\" class=\"canvas\"></canvas>\n<div class=\"toolbar\"><button id=\"playersBtn\" class=\"iconBtn\" title=\"Players\" data-i18n-title=\"players\">\ud83d\udc65</button><button id=\"searchBtn\" class=\"iconBtn\" title=\"Search\" data-i18n-title=\"search\">⌕</button><button id=\"rulerBtn\" class=\"iconBtn\" title=\"Measure distance\" data-i18n-title=\"measureDistance\">↔</button><button id=\"homeBtn\" class=\"iconBtn\" title=\"Home — server spawn\" data-i18n-title=\"homeSpawn\">⌂</button><button id=\"fitBtn\" class=\"iconBtn\" title=\"Fit explored map\" data-i18n-title=\"fitMap\">⛶</button><button id=\"shareBtn\" class=\"iconBtn\" title=\"Copy map link\" data-i18n-title=\"copyMapLink\">↗</button><button id=\"helpBtn\" class=\"iconBtn\" title=\"Waypoint commands\" data-i18n-title=\"waypointCommands\">i</button></div>\n<div id=\"leaderboardOverlay\" class=\"leaderboardOverlay\" aria-hidden=\"true\">\n  <div class=\"leaderboardShell\">\n    <div class=\"leaderboardHero\"><div class=\"leaderboardCrown\">♛</div><div><div class=\"leaderboardTitle\">SERVER LEADERBOARDS</div><div class=\"leaderboardSub\">Skills · Playtime · Progress · Community</div></div><button id=\"leaderboardClose\" class=\"closeBtn\" title=\"Close\">×</button></div>\n    <div class=\"leaderboardTabs\"><button class=\"lbTab active\" data-lb-tab=\"overview\">Overview</button><button class=\"lbTab\" data-lb-tab=\"skills\">Skills</button><button class=\"lbTab\" data-lb-tab=\"playtime\">Playtime</button></div>\n    <div id=\"leaderboardBody\" class=\"leaderboardBody\"><div class=\"lbEmpty\">Loading leaderboards…</div></div>\n  </div>\n</div>\n<div id=\"exportProgress\" class=\"exportProgress\" aria-live=\"polite\"><div class=\"exportProgressHead\"><span id=\"exportProgressTitle\" data-i18n=\"preparingExport\">Preparing export…</span><span id=\"exportProgressPct\" class=\"exportProgressPct\">0%</span></div><div class=\"exportProgressTrack\"><div id=\"exportProgressFill\" class=\"exportProgressFill\"></div></div><div id=\"exportProgressMeta\" class=\"exportProgressMeta\"></div></div>\n<div class=\"rightMapStack\">\n<div id=\"layersPanel\" class=\"panel floatingPanel\"><div class=\"panelHead\"><span data-i18n=\"mapLayers\">Map Layers</span><button class=\"closeBtn\" data-close>×</button></div><div class=\"panelBody\"><div class=\"small\" style=\"margin-bottom:8px\" data-i18n=\"baseMap\">Base map</div><label class=\"layerRow\"><input id=\"layerTerrain\" type=\"checkbox\" checked><span data-i18n=\"showTerrain\">Show terrain</span></label><select id=\"terrainStyle\" style=\"width:100%;margin:4px 0 12px\"><option value=\"normal\" data-i18n=\"normalColor\">Normal color</option><option value=\"sepia\" data-i18n=\"sepiaParchment\">Sepia / parchment</option><option value=\"topo\" data-i18n=\"topographic\">Topographic</option></select><div class=\"small\" style=\"margin-bottom:8px\" data-i18n=\"overlays\">Overlays</div><label class=\"layerRow\"><input id=\"layerGrid\" type=\"checkbox\" checked><span data-i18n=\"coordinateGrid\">Coordinate grid</span></label><label class=\"layerRow\"><input id=\"layerPlayers\" type=\"checkbox\" checked><span data-i18n=\"players\">Players</span></label><label class=\"layerRow\"><input id=\"layerWaypoints\" type=\"checkbox\" checked><span data-i18n=\"waypoints\">Waypoints</span></label><label class=\"layerRow\"><input id=\"layerActivity\" type=\"checkbox\" checked><span data-i18n=\"blockloggerActivity\">BlockLogger activity</span></label><label class=\"layerRow\"><input id=\"layerBad\" type=\"checkbox\" checked><span data-i18n=\"badBlockEntities\">Bad block entities</span></label><div style=\"display:flex;gap:6px;flex-wrap:wrap;margin-top:12px\"><button id=\"layersEnableAll\" data-i18n=\"enableOverlays\">Enable overlays</button><button id=\"layersDisableAll\" data-i18n=\"disableOverlays\">Disable overlays</button><button id=\"layersReset\" data-i18n=\"reset\">Reset</button></div><div id=\"layerAvailability\" class=\"small\" style=\"margin-top:10px\"></div></div></div>\n<div id=\"searchPanel\" class=\"panel floatingPanel\"><div class=\"panelHead\"><span data-i18n=\"searchMap\">Search map</span><button class=\"closeBtn\" data-close>×</button></div><div class=\"panelBody\"><div class=\"searchRow\"><input id=\"searchInput\" autocomplete=\"off\" placeholder=\"Player, waypoint, or X Z\" data-i18n-placeholder=\"searchPlaceholder\"><button id=\"searchGo\" class=\"button\" data-i18n=\"go\">Go</button></div><div id=\"searchResults\" class=\"searchResults\"></div></div></div>\n<div id=\"helpPanel\" class=\"panel floatingPanel\"><div class=\"panelHead\"><span data-i18n=\"webMapHelp\">Web Map help</span><button class=\"closeBtn\" data-close>×</button></div><div class=\"panelBody\"><div class=\"helpNote\" data-i18n=\"waypointHelpIntro\">Create a public waypoint exactly where your character is standing:</div><code class=\"helpCode\">/webmap waypoint add major Newfies base</code><code class=\"helpCode\">/webmap waypoint add minor Mine entrance</code><code class=\"helpCode\">/webmap waypoint add major icon:base Newfies Base</code><code class=\"helpCode\">/webmap waypoint remove Newfies base</code><div id=\"waypointLimitHelp\" class=\"helpNote\"></div><div class=\"helpNote\" style=\"margin-top:7px\" data-i18n=\"iconsHelp\">Icons: pin, base, settlement, shop, mine, resource, danger, landmark, road, farm.</div><div class=\"helpNote\" style=\"margin-top:7px\"><b data-i18n=\"liveMapPrivacy\">Live-map privacy:</b> <code>/webmap live off</code>, <code>/webmap live on</code>, <span data-i18n=\"or\">or</span> <code>/webmap live status</code>.</div><div id=\"exportBenchmarkHelp\" class=\"helpNote\" style=\"margin-top:7px\"></div><div class=\"helpNote\" style=\"margin-top:8px\" data-i18n-html=\"waypointVisibilityHelp\"><b>Major</b> waypoints remain visible much farther out. <b>Minor</b> waypoints appear when you zoom in closer.</div></div></div>\n<div class=\"layerDock\"><button id=\"layersBtn\" class=\"iconBtn\" title=\"Map layers\" data-i18n-title=\"mapLayers\"><span aria-hidden=\"true\">▱</span><span data-i18n=\"layers\">Layers</span><span id=\"layerCount\" class=\"layerCount\">0</span></button></div>\n<div id=\"playerPanel\" class=\"panel playerPanel\"><div id=\"playerPanelHead\" class=\"panelHead\"><span data-i18n=\"players\">Players</span><span id=\"playerPanelCount\" class=\"muted\">0 online</span><button id=\"playerPanelClose\" class=\"closeBtn\">×</button></div><div id=\"playerPanelBody\" class=\"playerPanelBody\"></div></div>\n</div>\n<div id=\"markerCard\" class=\"panel markerCard\"><button id=\"markerClose\" class=\"closeBtn\" style=\"float:right\">×</button><div id=\"markerCardBody\"></div></div>\n<div id=\"measureReadout\" class=\"measureReadout\"></div><div id=\"contextMenu\" class=\"contextMenu\"><div id=\"contextCoords\" class=\"contextCoords\"></div><button id=\"contextCopy\" data-i18n=\"copyCoordinates\">Copy coordinates</button><button id=\"contextMeasure\" data-i18n=\"measureFromHere\">Measure from here</button><button id=\"contextClearMeasure\" data-i18n=\"clearMeasurement\">Clear measurement</button></div>\n<div id=\"coords\" class=\"coords\">X 0 · Z 0</div><div id=\"toast\" class=\"toast\"></div>\n</div>\n<script>\nconst ADMIN={Js(value2)};const ADMINPORT={value3};const REFRESH={30}*1000;\nlet playerRefreshMs={webMapConfig.PlayerIconRefreshSeconds}*1000,playerPollTimer=null,playerPollBusy=false,mapLoadBusy=false,latestPlayerSnapshot=null;\nfunction applyPlayerSnapshot(snapshot){{if(!snapshot)return;const at=Number(snapshot.playerSnapshotUnixMilliseconds||0);if(latestPlayerSnapshot&&at<Number(latestPlayerSnapshot.playerSnapshotUnixMilliseconds||0))return;latestPlayerSnapshot=snapshot;playerRefreshMs=Math.max(1000,Math.min(300000,Number(snapshot.playerRefreshSeconds||60)*1000));if(!data)return;data.players=snapshot.players||[];data.showPlayers=snapshot.showPlayers===true;data.activePlayerCount=Number(snapshot.activePlayerCount||0);data.playerSnapshotUnixMilliseconds=at;activePlayersEl.textContent=data.activePlayerCount.toLocaleString();renderPlayers();applyFollow();requestDraw()}}\nfunction schedulePlayerPoll(){{clearTimeout(playerPollTimer);playerPollTimer=setTimeout(pollPlayers,playerRefreshMs)}}\nasync function pollPlayers(){{if(playerPollBusy)return;if(document.hidden){{schedulePlayerPoll();return}}playerPollBusy=true;try{{const r=await fetch('/api/players',{{cache:'no-store',signal:AbortSignal.timeout(10000)}});if(!r.ok)throw new Error(r.status);applyPlayerSnapshot(await r.json())}}catch{{}}finally{{playerPollBusy=false;schedulePlayerPoll()}}}}\nconst MAP_I18N={{\nen:{{year:'Year',month:'Month',day:'Day',unknownDate:'Unknown date',subtitle:'Server Essentials Web Map',loading:'Loading…',admin:'Admin',language:'Language',light:'Light',dark:'Dark',players:'Players',search:'Search',measureDistance:'Measure distance',homeSpawn:'Home — server spawn',fitMap:'Fit explored map',copyMapLink:'Copy map link',waypointCommands:'Waypoint commands',mapLayers:'Map Layers',preparingExport:'Preparing export…',baseMap:'Base map',showTerrain:'Show terrain',normalColor:'Normal color',sepiaParchment:'Sepia / parchment',topographic:'Topographic',overlays:'Overlays',coordinateGrid:'Coordinate grid',waypoints:'Waypoints',blockloggerActivity:'BlockLogger activity',badBlockEntities:'Bad block entities',enableOverlays:'Enable overlays',disableOverlays:'Disable overlays',reset:'Reset',searchMap:'Search map',searchPlaceholder:'Player, waypoint, or X Z',go:'Go',webMapHelp:'Web Map help',waypointHelpIntro:'Create a public waypoint exactly where your character is standing:',iconsHelp:'Icons: pin, base, settlement, shop, mine, resource, danger, landmark, road, farm.',liveMapPrivacy:'Live-map privacy:',or:'or',waypointVisibilityHelp:'<b>Major</b> waypoints remain visible much farther out. <b>Minor</b> waypoints appear when you zoom in closer.',layers:'Layers',online:'online',player:'Player',waypoint:'Waypoint',ping:'Ping',session:'Session',total:'Total',played:'played',followPlayer:'Follow player',stopFollowing:'Stop following',center:'Center',placedBy:'Placed by',mapOrigin:'Map origin',mapOriginHelp:'This is X 0 · Z 0 in the in-game HUD/map coordinate system.',badBlockEntity:'Bad block entity',blockActivity:'Block activity',followingPlayer:'Following player',followStopped:'Follow stopped',noPlayersOnline:'No players online.',noMatching:'No matching player or waypoint.',measureToHere:'Measure to here',measureFromHere:'Measure from here',copyCoordinates:'Copy coordinates',clearMeasurement:'Clear measurement',coordinatesCopied:'Coordinates copied',blocks:'blocks',tapSecondPoint:'Tap the second point',measurementCleared:'Measurement cleared',tapFirstPoint:'Tap the first point',mapLinkCopied:'Map link copied',mapLinkUpdated:'Map link updated',calculating:'calculating',phaseCoverage:'Scanning explored terrain',phaseRendering:'Rendering terrain',phaseOverview:'Building overview',phasePyramid:'Building zoom levels',phaseFinalizing:'Publishing map cache',phaseComplete:'Map update complete',phaseCancelled:'Export cancelled',phaseFailed:'Export failed',phaseWaiting:'Waiting',tiles:'tiles',chunks:'chunks',elapsed:'elapsed',eta:'ETA',lastExport:'last export',serverLimits:'Server limits',major:'major',minor:'minor',perPlayer:'per player',lastTerrainExport:'Last terrain export completed in {{0}}.',noExportBenchmark:'No completed terrain export benchmark is recorded yet.',mapFeedUnavailable:'Map feed unavailable',unknown:'unknown',serverTerrainOutputs:'Server terrain outputs',none:'none',normal:'Normal',sepia:'Sepia',topo:'Topo'}},\nde:{{year:'Jahr',month:'Monat',day:'Tag',unknownDate:'Unbekanntes Datum',subtitle:'Server Essentials Web-Karte',loading:'Laden…',admin:'Admin',language:'Sprache',light:'Hell',dark:'Dunkel',players:'Spieler',search:'Suchen',measureDistance:'Entfernung messen',homeSpawn:'Start — Server-Spawn',fitMap:'Erkundete Karte einpassen',copyMapLink:'Kartenlink kopieren',waypointCommands:'Wegpunkt-Befehle',mapLayers:'Kartenebenen',preparingExport:'Export wird vorbereitet…',baseMap:'Basiskarte',showTerrain:'Gelände anzeigen',normalColor:'Normale Farben',sepiaParchment:'Sepia / Pergament',topographic:'Topografisch',overlays:'Überlagerungen',coordinateGrid:'Koordinatengitter',waypoints:'Wegpunkte',blockloggerActivity:'BlockLogger-Aktivität',badBlockEntities:'Fehlerhafte Blockentitäten',enableOverlays:'Überlagerungen aktivieren',disableOverlays:'Überlagerungen deaktivieren',reset:'Zurücksetzen',searchMap:'Karte durchsuchen',searchPlaceholder:'Spieler, Wegpunkt oder X Z',go:'Los',webMapHelp:'Web-Karten-Hilfe',waypointHelpIntro:'Erstelle einen öffentlichen Wegpunkt genau an deiner aktuellen Position:',iconsHelp:'Symbole: pin, base, settlement, shop, mine, resource, danger, landmark, road, farm.',liveMapPrivacy:'Live-Karten-Datenschutz:',or:'oder',waypointVisibilityHelp:'<b>Haupt</b>-Wegpunkte bleiben aus größerer Entfernung sichtbar. <b>Neben</b>-Wegpunkte erscheinen beim näheren Heranzoomen.',layers:'Ebenen',online:'online',player:'Spieler',waypoint:'Wegpunkt',ping:'Ping',session:'Sitzung',total:'Gesamt',played:'gespielt',followPlayer:'Spieler folgen',stopFollowing:'Folgen beenden',center:'Zentrieren',placedBy:'Platziert von',mapOrigin:'Kartenursprung',mapOriginHelp:'Dies ist X 0 · Z 0 im Koordinatensystem der HUD-/Spielkarte.',badBlockEntity:'Fehlerhafte Blockentität',blockActivity:'Blockaktivität',followingPlayer:'Spieler wird verfolgt',followStopped:'Verfolgung beendet',noPlayersOnline:'Keine Spieler online.',noMatching:'Kein passender Spieler oder Wegpunkt.',measureToHere:'Bis hier messen',measureFromHere:'Von hier messen',copyCoordinates:'Koordinaten kopieren',clearMeasurement:'Messung löschen',coordinatesCopied:'Koordinaten kopiert',blocks:'Blöcke',tapSecondPoint:'Zweiten Punkt antippen',measurementCleared:'Messung gelöscht',tapFirstPoint:'Ersten Punkt antippen',mapLinkCopied:'Kartenlink kopiert',mapLinkUpdated:'Kartenlink aktualisiert',calculating:'wird berechnet',phaseCoverage:'Erkundetes Gelände wird gescannt',phaseRendering:'Gelände wird gerendert',phaseOverview:'Übersicht wird erstellt',phasePyramid:'Zoomstufen werden erstellt',phaseFinalizing:'Karten-Cache wird veröffentlicht',phaseComplete:'Kartenupdate abgeschlossen',phaseCancelled:'Export abgebrochen',phaseFailed:'Export fehlgeschlagen',phaseWaiting:'Warten',tiles:'Kacheln',chunks:'Chunks',elapsed:'verstrichen',eta:'Restzeit',lastExport:'letzter Export',serverLimits:'Serverlimits',major:'Haupt',minor:'Neben',perPlayer:'pro Spieler',lastTerrainExport:'Letzter Geländeexport abgeschlossen in {{0}}.',noExportBenchmark:'Noch kein abgeschlossener Geländeexport als Vergleich vorhanden.',mapFeedUnavailable:'Kartendaten nicht verfügbar',unknown:'unbekannt',serverTerrainOutputs:'Server-Geländeausgaben',none:'keine',normal:'Normal',sepia:'Sepia',topo:'Topo'}},\nfr:{{year:'Année',month:'Mois',day:'Jour',unknownDate:'Date inconnue',subtitle:'Carte Web Server Essentials',loading:'Chargement…',admin:'Admin',language:'Langue',light:'Clair',dark:'Sombre',players:'Joueurs',search:'Rechercher',measureDistance:'Mesurer la distance',homeSpawn:'Accueil — point d’apparition serveur',fitMap:'Ajuster à la zone explorée',copyMapLink:'Copier le lien de la carte',waypointCommands:'Commandes de points de repère',mapLayers:'Calques de la carte',preparingExport:'Préparation de l’export…',baseMap:'Carte de base',showTerrain:'Afficher le terrain',normalColor:'Couleurs normales',sepiaParchment:'Sépia / parchemin',topographic:'Topographique',overlays:'Superpositions',coordinateGrid:'Grille de coordonnées',waypoints:'Points de repère',blockloggerActivity:'Activité BlockLogger',badBlockEntities:'Entités de bloc défectueuses',enableOverlays:'Activer les superpositions',disableOverlays:'Désactiver les superpositions',reset:'Réinitialiser',searchMap:'Rechercher sur la carte',searchPlaceholder:'Joueur, point de repère ou X Z',go:'Aller',webMapHelp:'Aide de la carte Web',waypointHelpIntro:'Créez un point de repère public exactement à la position de votre personnage :',iconsHelp:'Icônes : pin, base, settlement, shop, mine, resource, danger, landmark, road, farm.',liveMapPrivacy:'Confidentialité de la carte en direct :',or:'ou',waypointVisibilityHelp:'Les points de repère <b>majeurs</b> restent visibles de beaucoup plus loin. Les points <b>mineurs</b> apparaissent lorsque vous zoomez.',layers:'Calques',online:'en ligne',player:'Joueur',waypoint:'Point de repère',ping:'Ping',session:'Session',total:'Total',played:'joué',followPlayer:'Suivre le joueur',stopFollowing:'Arrêter de suivre',center:'Centrer',placedBy:'Placé par',mapOrigin:'Origine de la carte',mapOriginHelp:'Voici X 0 · Z 0 dans le système de coordonnées de la carte/HUD du jeu.',badBlockEntity:'Entité de bloc défectueuse',blockActivity:'Activité de bloc',followingPlayer:'Suivi du joueur',followStopped:'Suivi arrêté',noPlayersOnline:'Aucun joueur en ligne.',noMatching:'Aucun joueur ou point de repère correspondant.',measureToHere:'Mesurer jusqu’ici',measureFromHere:'Mesurer depuis ici',copyCoordinates:'Copier les coordonnées',clearMeasurement:'Effacer la mesure',coordinatesCopied:'Coordonnées copiées',blocks:'blocs',tapSecondPoint:'Touchez le second point',measurementCleared:'Mesure effacée',tapFirstPoint:'Touchez le premier point',mapLinkCopied:'Lien de la carte copié',mapLinkUpdated:'Lien de la carte mis à jour',calculating:'calcul…',phaseCoverage:'Analyse du terrain exploré',phaseRendering:'Rendu du terrain',phaseOverview:'Création de l’aperçu',phasePyramid:'Création des niveaux de zoom',phaseFinalizing:'Publication du cache de carte',phaseComplete:'Mise à jour de la carte terminée',phaseCancelled:'Export annulé',phaseFailed:'Échec de l’export',phaseWaiting:'En attente',tiles:'tuiles',chunks:'chunks',elapsed:'écoulé',eta:'Restant',lastExport:'dernier export',serverLimits:'Limites serveur',major:'majeurs',minor:'mineurs',perPlayer:'par joueur',lastTerrainExport:'Dernier export du terrain terminé en {{0}}.',noExportBenchmark:'Aucun export de terrain terminé n’est encore enregistré.',mapFeedUnavailable:'Flux de carte indisponible',unknown:'inconnu',serverTerrainOutputs:'Sorties de terrain du serveur',none:'aucune',normal:'Normal',sepia:'Sépia',topo:'Topo'}},\nru:{{year:'Год',month:'Месяц',day:'День',unknownDate:'Неизвестная дата',subtitle:'Веб-карта Server Essentials',loading:'Загрузка…',admin:'Админ',language:'Язык',light:'Светлая',dark:'Тёмная',players:'Игроки',search:'Поиск',measureDistance:'Измерить расстояние',homeSpawn:'Домой — спавн сервера',fitMap:'Показать исследованную карту',copyMapLink:'Копировать ссылку карты',waypointCommands:'Команды меток',mapLayers:'Слои карты',preparingExport:'Подготовка экспорта…',baseMap:'Основная карта',showTerrain:'Показывать рельеф',normalColor:'Обычные цвета',sepiaParchment:'Сепия / пергамент',topographic:'Топографическая',overlays:'Наложения',coordinateGrid:'Координатная сетка',waypoints:'Метки',blockloggerActivity:'Активность BlockLogger',badBlockEntities:'Проблемные блочные сущности',enableOverlays:'Включить наложения',disableOverlays:'Отключить наложения',reset:'Сбросить',searchMap:'Поиск по карте',searchPlaceholder:'Игрок, метка или X Z',go:'Перейти',webMapHelp:'Справка веб-карты',waypointHelpIntro:'Создать публичную метку точно в позиции вашего персонажа:',iconsHelp:'Значки: pin, base, settlement, shop, mine, resource, danger, landmark, road, farm.',liveMapPrivacy:'Приватность онлайн-карты:',or:'или',waypointVisibilityHelp:'<b>Основные</b> метки видны с гораздо большего расстояния. <b>Дополнительные</b> появляются при приближении.',layers:'Слои',online:'онлайн',player:'Игрок',waypoint:'Метка',ping:'Пинг',session:'Сессия',total:'Всего',played:'сыграно',followPlayer:'Следить за игроком',stopFollowing:'Не следить',center:'Центрировать',placedBy:'Установил',mapOrigin:'Начало карты',mapOriginHelp:'Это X 0 · Z 0 в системе координат внутриигровой карты/HUD.',badBlockEntity:'Проблемная блочная сущность',blockActivity:'Активность блока',followingPlayer:'Слежение за игроком',followStopped:'Слежение остановлено',noPlayersOnline:'Нет игроков онлайн.',noMatching:'Подходящий игрок или метка не найдены.',measureToHere:'Измерить до сюда',measureFromHere:'Измерить отсюда',copyCoordinates:'Копировать координаты',clearMeasurement:'Очистить измерение',coordinatesCopied:'Координаты скопированы',blocks:'блоков',tapSecondPoint:'Нажмите вторую точку',measurementCleared:'Измерение очищено',tapFirstPoint:'Нажмите первую точку',mapLinkCopied:'Ссылка карты скопирована',mapLinkUpdated:'Ссылка карты обновлена',calculating:'расчёт',phaseCoverage:'Сканирование исследованной территории',phaseRendering:'Отрисовка территории',phaseOverview:'Создание обзора',phasePyramid:'Создание уровней масштаба',phaseFinalizing:'Публикация кэша карты',phaseComplete:'Обновление карты завершено',phaseCancelled:'Экспорт отменён',phaseFailed:'Ошибка экспорта',phaseWaiting:'Ожидание',tiles:'тайлов',chunks:'чанков',elapsed:'прошло',eta:'Осталось',lastExport:'последний экспорт',serverLimits:'Лимиты сервера',major:'основные',minor:'дополнительные',perPlayer:'на игрока',lastTerrainExport:'Последний экспорт территории завершён за {{0}}.',noExportBenchmark:'Завершённый экспорт территории пока не записан.',mapFeedUnavailable:'Данные карты недоступны',unknown:'неизвестно',serverTerrainOutputs:'Выходы рельефа сервера',none:'нет',normal:'Обычный',sepia:'Сепия',topo:'Топо'}}}};\nlet mapLang='en';try{{mapLang=localStorage.getItem('nses-map-lang')||''}}catch{{}}if(!MAP_I18N[mapLang]){{const n=String(navigator.language||'en').toLowerCase();mapLang=n.startsWith('de')?'de':n.startsWith('fr')?'fr':n.startsWith('ru')?'ru':'en'}}\nfunction tr(k,...a){{let v=(MAP_I18N[mapLang]||MAP_I18N.en)[k]??MAP_I18N.en[k]??k;return a.reduce((x,y,i)=>x.replace('{{'+i+'}}',y),String(v))}}\nfunction applyMapLanguage(){{document.documentElement.lang=mapLang;document.querySelectorAll('[data-i18n]').forEach(e=>{{const k=e.dataset.i18n;if(k)e.textContent=tr(k)}});document.querySelectorAll('[data-i18n-html]').forEach(e=>{{const k=e.dataset.i18nHtml;if(k)e.innerHTML=tr(k)}});document.querySelectorAll('[data-i18n-title]').forEach(e=>{{const k=e.dataset.i18nTitle;if(k)e.title=tr(k)}});document.querySelectorAll('[data-i18n-placeholder]').forEach(e=>{{const k=e.dataset.i18nPlaceholder;if(k)e.placeholder=tr(k)}});const ls=document.getElementById('langSelect');if(ls){{ls.value=mapLang;ls.title=tr('language')}}if(typeof theme!=='undefined')applyTheme();if(data){{renderPlayers();updateStatus();renderExportProgress(data.terrain||{{}})}}}}\n\nconst cv=document.getElementById('map'),ctx=cv.getContext('2d'),statusEl=document.getElementById('status'),admin=document.getElementById('admin'),coordsEl=document.getElementById('coords'),themeToggle=document.getElementById('themeToggle'),dateEl=document.getElementById('mapDate'),activePlayersEl=document.getElementById('activePlayers'),playerPanel=document.getElementById('playerPanel'),playerPanelBody=document.getElementById('playerPanelBody'),playerPanelCount=document.getElementById('playerPanelCount'),exportProgress=document.getElementById('exportProgress'),exportProgressTitle=document.getElementById('exportProgressTitle'),exportProgressPct=document.getElementById('exportProgressPct'),exportProgressFill=document.getElementById('exportProgressFill'),exportProgressMeta=document.getElementById('exportProgressMeta'),markerCard=document.getElementById('markerCard'),markerCardBody=document.getElementById('markerCardBody'),toastEl=document.getElementById('toast'),contextMenu=document.getElementById('contextMenu'),contextCoords=document.getElementById('contextCoords'),measureReadout=document.getElementById('measureReadout'),rulerBtn=document.getElementById('rulerBtn');\nif(ADMIN)admin.href=ADMIN;else if(ADMINPORT>0)admin.href=`http://${{location.hostname}}:${{ADMINPORT}}/`;else admin.style.display='none';const langSelect=document.getElementById('langSelect');if(langSelect)langSelect.onchange=()=>{{mapLang=langSelect.value;try{{localStorage.setItem('nses-map-lang',mapLang)}}catch{{}}applyMapLanguage()}};\nfunction detectMobileUi(){{const narrow=matchMedia('(max-width:760px)').matches,coarse=matchMedia('(pointer:coarse)').matches||Number(navigator.maxTouchPoints||0)>1,sw=Number(screen?.width||innerWidth||0),sh=Number(screen?.height||innerHeight||0),shortSide=Math.min(sw||9999,sh||9999);return narrow||(coarse&&shortSide<=900)}}let mobileUi=detectMobileUi();function syncMobileUi(){{mobileUi=detectMobileUi();document.documentElement.classList.toggle('mobileUi',mobileUi)}}const isMobile=()=>mobileUi;syncMobileUi();let data=null,scale=.18,ox=0,oz=0,terrainPollTimer=null,overviewImage=null,overviewVersion=0,lastTerrainGeneration=0,resizeTimer=null,drawQueued=false,firstLoad=true,followUid='',lastHashWrite=0;const tileCache=new Map(),pointers=new Map();let dragMoved=false,pinchStart=null,lastTapAt=0,hitMarkers=[],measureStart=null,measureEnd=null,measureMode=false,contextWorld=null,longPressTimer=null,longPressStart=null,suppressNextTap=false;\nlet theme='dark';try{{theme=localStorage.getItem('nses-map-theme')||'dark'}}catch{{}}document.documentElement.dataset.theme=theme;function applyTheme(){{document.documentElement.dataset.theme=theme;themeToggle.textContent=theme==='dark'?tr('light'):tr('dark');requestDraw()}}themeToggle.onclick=()=>{{theme=theme==='dark'?'light':'dark';try{{localStorage.setItem('nses-map-theme',theme)}}catch{{}}applyTheme()}};applyTheme();applyMapLanguage();\nconst defaultLayers={{terrain:true,grid:true,players:true,waypoints:true,activity:true,bad:true}};let layers={{...defaultLayers}},terrainStyle='normal';try{{layers={{...layers,...JSON.parse(localStorage.getItem('nses-map-layers')||'{{}}')}};terrainStyle=String(localStorage.getItem('nses-map-terrain-style')||'normal')}}catch{{}}if(!['normal','sepia','topo'].includes(terrainStyle))terrainStyle='normal';function saveLayers(){{try{{localStorage.setItem('nses-map-layers',JSON.stringify(layers));localStorage.setItem('nses-map-terrain-style',terrainStyle)}}catch{{}}}};\nfunction availableTerrainStyles(){{const d=data||{{}};return {{normal:d.exportNormalTerrain!==false,sepia:d.exportSepiaTerrain!==false,topo:d.exportTopographicTerrain!==false}}}}function ensureTerrainStyle(){{const a=availableTerrainStyles();if(!a[terrainStyle])terrainStyle=a.normal?'normal':a.sepia?'sepia':'topo'}}function syncLayerControls(){{for(const k of Object.keys(defaultLayers)){{const el=document.getElementById('layer'+k[0].toUpperCase()+k.slice(1));if(el)el.checked=layers[k]!==false}}const lc=document.getElementById('layerCount');if(lc)lc.textContent=String(Object.keys(defaultLayers).filter(k=>layers[k]!==false).length);ensureTerrainStyle();const style=document.getElementById('terrainStyle');if(style){{style.value=terrainStyle;const a=availableTerrainStyles();for(const o of style.options)o.disabled=!a[o.value]}}const av=document.getElementById('layerAvailability');if(av){{const a=availableTerrainStyles(),names=[];if(a.normal)names.push(tr('normal'));if(a.sepia)names.push(tr('sepia'));if(a.topo)names.push(tr('topo'));av.textContent=tr('serverTerrainOutputs')+': '+(names.length?names.join(', '):tr('none'))}}}}syncLayerControls();document.querySelectorAll('#layersPanel input').forEach(el=>el.onchange=()=>{{const k=el.id.replace('layer','');const key=k[0].toLowerCase()+k.slice(1);layers[key]=el.checked;saveLayers();requestDraw()}});document.getElementById('terrainStyle').onchange=e=>{{terrainStyle=e.target.value;tileCache.clear();overviewImage=null;saveLayers();requestDraw()}};document.getElementById('layersEnableAll').onclick=()=>{{for(const k of ['grid','players','waypoints','activity','bad'])layers[k]=true;saveLayers();syncLayerControls();requestDraw()}};document.getElementById('layersDisableAll').onclick=()=>{{for(const k of ['grid','players','waypoints','activity','bad'])layers[k]=false;saveLayers();syncLayerControls();requestDraw()}};document.getElementById('layersReset').onclick=()=>{{layers={{...defaultLayers}};terrainStyle='normal';ensureTerrainStyle();saveLayers();syncLayerControls();requestDraw()}};\nfunction mapBg(){{return getComputedStyle(document.documentElement).getPropertyValue('--map-bg').trim()||'#10161d'}}function maximumViewSpan(){{return Math.max(2048,Number(data?.maximumZoomOutSpanBlocks??50000)||50000)}}function defaultViewSpan(){{return Math.max(512,Math.min(maximumViewSpan(),Number(data?.defaultViewSpanBlocks??8000)||8000))}}function minAllowedScale(){{return Math.max(.001,Math.max(cv.clientWidth||1,cv.clientHeight||1)/maximumViewSpan())}}function clampScale(v){{return Math.max(minAllowedScale(),Math.min(8,v))}}function openingScale(){{return clampScale(Math.max(cv.clientWidth||1,cv.clientHeight||1)/defaultViewSpan())}}function resize(){{syncMobileUi();const d=devicePixelRatio||1,w=Math.max(1,cv.clientWidth||cv.getBoundingClientRect().width||1),h=Math.max(1,cv.clientHeight||cv.getBoundingClientRect().height||1);cv.width=Math.max(1,Math.round(w*d));cv.height=Math.max(1,Math.round(h*d));if(data)scale=clampScale(scale);requestDraw()}}addEventListener('resize',()=>{{clearTimeout(resizeTimer);resizeTimer=setTimeout(resize,120)}});addEventListener('orientationchange',()=>{{setTimeout(resize,180)}});function initializeFirstView(){{requestAnimationFrame(()=>{{resize();requestAnimationFrame(()=>{{if(!applyHash())initialView();else{{scale=clampScale(scale);requestDraw()}}requestDraw();setTimeout(requestDraw,80);setTimeout(requestDraw,260)}})}})}}\nfunction waypointAbsX(p){{return Number(p?.x||0)+(data?.coordinateOriginX||0)}}function waypointAbsZ(p){{return Number(p?.z||0)+(data?.coordinateOriginZ||0)}}function rx(x){{return x-(data?.coordinateOriginX||0)}}function rz(z){{return z-(data?.coordinateOriginZ||0)}}function sx(x){{return cv.clientWidth/2+(x-ox)*scale}}function sy(z){{return cv.clientHeight/2+(z-oz)*scale}}function worldAt(px,py){{return{{x:ox+(px-cv.clientWidth/2)/scale,z:oz+(py-cv.clientHeight/2)/scale}}}}\nfunction points(){{if(!data)return[];const absolute=[...(data.activity||[]),...(data.players||[]),...(data.badBlockEntities||[])];const wp=(data.waypoints||[]).map(p=>({{...p,x:waypointAbsX(p),z:waypointAbsZ(p)}}));return [...absolute,...wp].filter(p=>Number.isFinite(p.x)&&Number.isFinite(p.z))}}function bounds(){{const m=data?.terrainManifest;if(m&&m.tileCount>0)return{{minx:m.minWorldX,maxx:m.maxWorldXExclusive,minz:m.minWorldZ,maxz:m.maxWorldZExclusive}};const ps=points();if(!ps.length)return null;return{{minx:Math.min(...ps.map(p=>p.x)),maxx:Math.max(...ps.map(p=>p.x)),minz:Math.min(...ps.map(p=>p.z)),maxz:Math.max(...ps.map(p=>p.z))}}}}\nfunction fitScaleForBounds(b){{if(!b)return openingScale();const dx=Math.max(64,b.maxx-b.minx),dz=Math.max(64,b.maxz-b.minz),pad=isMobile()?54:100;return clampScale(Math.min(Math.max(1,cv.clientWidth-pad)/dx,Math.max(1,cv.clientHeight-pad)/dz))}}function home(){{followUid='';ox=Number(data?.spawnX??data?.coordinateOriginX??0);oz=Number(data?.spawnZ??data?.coordinateOriginZ??0);scale=clampScale(Math.max(scale,openingScale()));requestDraw();writeHashSoon()}}function fit(){{followUid='';const b=bounds();if(!b){{home();return}}ox=(b.minx+b.maxx)/2;oz=(b.minz+b.maxz)/2;scale=fitScaleForBounds(b);requestDraw();writeHashSoon()}}function initialView(){{followUid='';const b=bounds();if(!b){{home();return}}ox=(b.minx+b.maxx)/2;oz=(b.minz+b.maxz)/2;scale=clampScale(Math.max(fitScaleForBounds(b),openingScale()));requestDraw()}}\nfunction requestDraw(){{if(drawQueued)return;drawQueued=true;requestAnimationFrame(()=>{{drawQueued=false;draw()}})}}function terrainPrefix(){{return terrainStyle==='normal'?'':`/styles/${{terrainStyle}}`}}function tileImage(tx,tz,ver){{const k=`${{terrainStyle}}/${{tx}}/${{tz}}/${{ver}}`;if(tileCache.has(k))return tileCache.get(k);const im=new Image();im.onload=requestDraw;im.onerror=()=>{{im.failed=true;requestDraw()}};im.src=`${{terrainPrefix()}}/tiles/${{tx}}/${{tz}}.png?v=${{ver}}`;tileCache.set(k,im);trimCache();return im}}function lodImage(f,tx,tz,ver){{const k=`${{terrainStyle}}/lod/${{f}}/${{tx}}/${{tz}}/${{ver}}`;if(tileCache.has(k))return tileCache.get(k);const im=new Image();im.onload=requestDraw;im.onerror=()=>{{im.failed=true;requestDraw()}};im.src=`${{terrainPrefix()}}/lod/${{f}}/${{tx}}/${{tz}}.png?v=${{ver}}`;tileCache.set(k,im);trimCache();return im}}function trimCache(){{const limit=isMobile()?240:600;if(tileCache.size<=limit)return;const remove=Math.max(1,tileCache.size-Math.floor(limit*.85));for(let i=0;i<remove;i++){{const k=tileCache.keys().next().value;if(k==null)break;tileCache.delete(k)}}}}\nfunction overview(m){{if(!m?.overviewAvailable)return;if(overviewVersion!==m.generatedUnixMilliseconds){{overviewVersion=m.generatedUnixMilliseconds||0;overviewImage=new Image();overviewImage.onload=requestDraw;overviewImage.src=`/overview.png?v=${{overviewVersion}}`}}if(overviewImage?.complete&&overviewImage.naturalWidth>0){{ctx.imageSmoothingEnabled=false;ctx.drawImage(overviewImage,sx(m.minWorldX),sy(m.minWorldZ),(m.maxWorldXExclusive-m.minWorldX)*scale,(m.maxWorldZExclusive-m.minWorldZ)*scale)}}}}function chooseTerrainFactor(m){{const fs=[1,...(m.lodFactors||[]).map(Number).filter(f=>f>1)].sort((a,b)=>a-b),target=1/Math.max(scale,.000001);let best=1,score=Infinity;for(const f of fs){{const sc=Math.abs(Math.log2(f/target));if(sc<score){{best=f;score=sc}}}}return best}}\nfunction terrain(){{if(!layers.terrain)return;const m=data?.terrainManifest;if(!m||m.tileCount<=0)return;ctx.save();const l=sx(m.minWorldX),r=sx(m.maxWorldXExclusive),t=sy(m.minWorldZ),b=sy(m.maxWorldZExclusive);ctx.beginPath();ctx.rect(Math.min(l,r),Math.min(t,b),Math.abs(r-l),Math.abs(b-t));ctx.clip();if(terrainStyle==='normal')overview(m);const ts=m.tileSize||256,w=cv.clientWidth,h=cv.clientHeight,minx=ox-w/(2*scale),maxx=ox+w/(2*scale),minz=oz-h/(2*scale),maxz=oz+h/(2*scale),ver=m.generatedUnixMilliseconds||0,f=chooseTerrainFactor(m),rs=ts*f,x0=Math.floor(minx/rs),x1=Math.floor(maxx/rs),z0=Math.floor(minz/rs),z1=Math.floor(maxz/rs),count=(x1-x0+1)*(z1-z0+1);if(count<=1400){{for(let z=z0;z<=z1;z++)for(let x=x0;x<=x1;x++){{const im=f===1?tileImage(x,z,ver):lodImage(f,x,z,ver);if(im.complete&&!im.failed&&im.naturalWidth>0){{ctx.imageSmoothingEnabled=false;const px=sx(x*rs),py=sy(z*rs),d=rs*scale;ctx.drawImage(im,Math.round(px),Math.round(py),Math.ceil(d)+1,Math.ceil(d)+1)}}}}}}ctx.restore()}}\nfunction grid(){{if(!layers.grid||!data)return;const w=cv.clientWidth,h=cv.clientHeight,m=data?.terrainManifest;ctx.save();if(m&&m.tileCount>0){{const l=sx(m.minWorldX),r=sx(m.maxWorldXExclusive),t=sy(m.minWorldZ),b=sy(m.maxWorldZExclusive);ctx.beginPath();ctx.rect(Math.min(l,r),Math.min(t,b),Math.abs(r-l),Math.abs(b-t));ctx.clip()}}let step=16;while(step*scale<58)step*=2;while(step*scale>150)step/=2;ctx.strokeStyle=getComputedStyle(document.documentElement).getPropertyValue('--grid').trim();ctx.fillStyle=getComputedStyle(document.documentElement).getPropertyValue('--muted').trim();ctx.font='10px system-ui';ctx.lineWidth=1;const xmin=ox-w/(2*scale),xmax=ox+w/(2*scale),zmin=oz-h/(2*scale),zmax=oz+h/(2*scale),gx=data.coordinateOriginX||0,gz=data.coordinateOriginZ||0;for(let x=Math.floor((xmin-gx)/step)*step+gx;x<=xmax;x+=step){{const px=sx(x);ctx.beginPath();ctx.moveTo(px,0);ctx.lineTo(px,h);ctx.stroke();ctx.fillText(String(Math.round(rx(x))),px+3,12)}}for(let z=Math.floor((zmin-gz)/step)*step+gz;z<=zmax;z+=step){{const py=sy(z);ctx.beginPath();ctx.moveTo(0,py);ctx.lineTo(w,py);ctx.stroke();ctx.fillText(String(Math.round(rz(z))),3,py-3)}}ctx.restore()}}\nfunction addHit(kind,obj,x,y,r=16){{hitMarkers.push({{kind,obj,x,y,r}})}}function label(text,x,y,bold=false){{if(!text)return;ctx.font=`${{bold?'700 ':''}}11px system-ui`;const tw=ctx.measureText(text).width,light=theme==='light';ctx.fillStyle=light?'#ffffffe8':'#0b0f14df';ctx.fillRect(x,y-12,tw+8,17);ctx.fillStyle=light?'#0f172a':'#f8fafc';ctx.fillText(text,x+4,y+1)}}function circleMark(p,r,color,kind,labelText){{const x=sx(p.x),y=sy(p.z);ctx.beginPath();ctx.fillStyle=color;ctx.arc(x,y,r,0,Math.PI*2);ctx.fill();ctx.strokeStyle=theme==='light'?'#fff':'#0b0f14';ctx.lineWidth=1.5;ctx.stroke();if(labelText&&scale>.025)label(labelText,x+r+4,y+4);addHit(kind,p,x,y,r+9)}}\nfunction playerMark(p){{const x=sx(p.x),y=sy(p.z),r=9,yaw=Number(p.yawRadians||0);ctx.save();ctx.translate(x,y);ctx.rotate(-yaw);ctx.beginPath();ctx.moveTo(0,-13);ctx.lineTo(7,4);ctx.lineTo(0,1);ctx.lineTo(-7,4);ctx.closePath();ctx.fillStyle='#86efac';ctx.fill();ctx.strokeStyle=theme==='light'?'#166534':'#052e16';ctx.lineWidth=1.4;ctx.stroke();ctx.restore();ctx.beginPath();ctx.arc(x,y,r,0,Math.PI*2);ctx.fillStyle='#22c55e';ctx.fill();ctx.strokeStyle=theme==='light'?'#fff':'#0b0f14';ctx.lineWidth=2.2;ctx.stroke();ctx.beginPath();ctx.arc(x,y,3,0,Math.PI*2);ctx.fillStyle='#f8fafc';ctx.fill();if(scale>.018||followUid===p.uid)label(p.name||'player',x+r+5,y+4,true);addHit('player',p,x,y,20)}}\nfunction waypointVisible(p){{const t=(p.type||'major').toLowerCase();return t==='minor'?scale>=.04:scale>=.001}}\nfunction waypointGlyph(icon,x,y,r){{ctx.save();ctx.translate(x,y);ctx.strokeStyle='#fff';ctx.fillStyle='#fff';ctx.lineWidth=Math.max(1.2,r*.16);ctx.lineCap='round';ctx.lineJoin='round';const q=r*.62;switch(String(icon||'pin').toLowerCase()){{case'base':ctx.beginPath();ctx.moveTo(-q*.8,0);ctx.lineTo(0,-q*.75);ctx.lineTo(q*.8,0);ctx.stroke();ctx.strokeRect(-q*.55,0,q*1.1,q*.7);break;case'settlement':ctx.strokeRect(-q*.8,-q*.15,q*.65,q*.75);ctx.strokeRect(q*.1,-q*.45,q*.7,q*1.05);break;case'shop':ctx.strokeRect(-q*.75,-q*.25,q*1.5,q);ctx.beginPath();ctx.moveTo(-q*.85,-q*.25);ctx.lineTo(-q*.55,-q*.65);ctx.lineTo(q*.55,-q*.65);ctx.lineTo(q*.85,-q*.25);ctx.stroke();break;case'mine':ctx.beginPath();ctx.moveTo(-q*.7,q*.65);ctx.lineTo(q*.55,-q*.65);ctx.moveTo(-q*.3,-q*.55);ctx.lineTo(q*.7,q*.5);ctx.stroke();break;case'resource':ctx.beginPath();for(let i=0;i<6;i++){{const a=Math.PI/3*i-Math.PI/6,px=Math.cos(a)*q*.75,py=Math.sin(a)*q*.75;i?ctx.lineTo(px,py):ctx.moveTo(px,py)}}ctx.closePath();ctx.stroke();break;case'danger':ctx.beginPath();ctx.moveTo(0,-q*.85);ctx.lineTo(q*.85,q*.65);ctx.lineTo(-q*.85,q*.65);ctx.closePath();ctx.stroke();ctx.fillRect(-1,-q*.35,2,q*.5);ctx.beginPath();ctx.arc(0,q*.4,1.3,0,Math.PI*2);ctx.fill();break;case'landmark':ctx.beginPath();for(let i=0;i<10;i++){{const a=-Math.PI/2+i*Math.PI/5,rr=i%2===0?q*.85:q*.38,px=Math.cos(a)*rr,py=Math.sin(a)*rr;i?ctx.lineTo(px,py):ctx.moveTo(px,py)}}ctx.closePath();ctx.stroke();break;case'road':ctx.beginPath();ctx.moveTo(-q*.45,q*.8);ctx.lineTo(-q*.15,-q*.8);ctx.moveTo(q*.45,q*.8);ctx.lineTo(q*.15,-q*.8);ctx.stroke();break;case'farm':ctx.beginPath();ctx.moveTo(-q*.7,q*.55);ctx.quadraticCurveTo(-q*.35,-q*.7,0,q*.45);ctx.quadraticCurveTo(q*.35,-q*.7,q*.7,q*.55);ctx.stroke();break;default:ctx.beginPath();ctx.arc(0,-q*.1,q*.32,0,Math.PI*2);ctx.fill();ctx.beginPath();ctx.moveTo(0,q*.75);ctx.lineTo(0,q*.2);ctx.stroke()}}ctx.restore()}}\nfunction waypointMark(p){{if(!waypointVisible(p))return;const x=sx(waypointAbsX(p)),y=sy(waypointAbsZ(p)),major=(p.type||'major')!=='minor',r=major?10:8,color=/^#[0-9a-fA-F]{{6}}$/.test(p.color||'')?p.color:(major?'#f59e0b':'#38bdf8');ctx.beginPath();ctx.arc(x,y,r,0,Math.PI*2);ctx.fillStyle=color;ctx.fill();ctx.strokeStyle=theme==='light'?'#fff':'#f8fafc';ctx.lineWidth=major?2:1.4;ctx.stroke();ctx.beginPath();ctx.moveTo(x-3,y+r-1);ctx.lineTo(x+3,y+r-1);ctx.lineTo(x,y+r+5);ctx.closePath();ctx.fillStyle=color;ctx.fill();waypointGlyph(p.icon||'pin',x,y,r);if(major){{ctx.beginPath();ctx.arc(x,y,r+3,0,Math.PI*2);ctx.strokeStyle='#f59e0b88';ctx.lineWidth=1;ctx.stroke()}}const labelAt=major?.012:.075;if((p.name||'')&&scale>=labelAt)label(p.name,x+r+6,y+4,major);addHit('waypoint',p,x,y,20)}}\nfunction drawMeasure(){{if(!measureStart)return;const a=measureStart,b=measureEnd||measureStart,ax=sx(a.x),ay=sy(a.z),bx=sx(b.x),by=sy(b.z);ctx.save();ctx.strokeStyle='#f8fafc';ctx.lineWidth=4;ctx.setLineDash([7,5]);ctx.beginPath();ctx.moveTo(ax,ay);ctx.lineTo(bx,by);ctx.stroke();ctx.strokeStyle='#0ea5e9';ctx.lineWidth=2;ctx.setLineDash([7,5]);ctx.stroke();ctx.setLineDash([]);for(const pt of [[ax,ay],[bx,by]]){{ctx.beginPath();ctx.arc(pt[0],pt[1],5,0,Math.PI*2);ctx.fillStyle='#0ea5e9';ctx.fill();ctx.strokeStyle='#fff';ctx.lineWidth=1.5;ctx.stroke()}}ctx.restore();if(measureEnd){{const dx=rx(measureEnd.x)-rx(measureStart.x),dz=rz(measureEnd.z)-rz(measureStart.z),dist=Math.hypot(dx,dz);measureReadout.textContent=`${{Math.round(dist).toLocaleString()}} ${{tr('blocks')}} · ΔX ${{Math.round(dx).toLocaleString()}} · ΔZ ${{Math.round(dz).toLocaleString()}}`;measureReadout.classList.add('active')}}else{{measureReadout.textContent=tr('tapSecondPoint');measureReadout.classList.add('active')}}}}\nfunction originMark(){{if(!data)return;const x=sx(data.coordinateOriginX||0),y=sy(data.coordinateOriginZ||0);if(x<-30||y<-30||x>cv.clientWidth+30||y>cv.clientHeight+30)return;ctx.strokeStyle='#a78bfa';ctx.lineWidth=2;ctx.beginPath();ctx.moveTo(x-8,y);ctx.lineTo(x+8,y);ctx.moveTo(x,y-8);ctx.lineTo(x,y+8);ctx.stroke();if(scale>.012)label('0,0',x+8,y-7,true);addHit('origin',{{x:data.coordinateOriginX||0,z:data.coordinateOriginZ||0}},x,y,18)}}\nfunction draw(){{const d=devicePixelRatio||1;ctx.setTransform(d,0,0,d,0,0);ctx.fillStyle=mapBg();ctx.fillRect(0,0,cv.clientWidth,cv.clientHeight);hitMarkers=[];terrain();grid();drawMeasure();if(!data)return;if(layers.activity&&data.showActivity)(data.activity||[]).forEach(p=>circleMark(p,Math.min(11,3+Math.log2(1+(p.eventCount||1))),'#f59e0b','activity',p.blockCode||''));if(layers.bad&&data.showBadBlockEntities)(data.badBlockEntities||[]).forEach(p=>circleMark(p,6,'#ef4444','bad',p.blockCode||'bad block'));if(layers.players&&data.showPlayers)(data.players||[]).forEach(playerMark);if(layers.waypoints&&data.showWaypoints)(data.waypoints||[]).forEach(waypointMark);originMark();coordsEl.textContent=`X ${{Math.round(rx(ox)).toLocaleString()}} · Z ${{Math.round(rz(oz)).toLocaleString()}}`}}\nfunction setCenterAbs(x,z,newScale=null){{followUid='';ox=x;oz=z;if(Number.isFinite(newScale))scale=clampScale(newScale);requestDraw();writeHashSoon()}}function zoomAt(px,py,factor){{const wx=ox+(px-cv.clientWidth/2)/scale,wz=oz+(py-cv.clientHeight/2)/scale;scale=clampScale(scale*factor);ox=wx-(px-cv.clientWidth/2)/scale;oz=wz-(py-cv.clientHeight/2)/scale;followUid='';requestDraw();writeHashSoon()}}\ncv.addEventListener('wheel',e=>{{e.preventDefault();closeContextMenu();zoomAt(e.offsetX,e.offsetY,e.deltaY<0?1.18:.85)}},{{passive:false}});\nfunction cancelLongPress(){{if(longPressTimer){{clearTimeout(longPressTimer);longPressTimer=null}}longPressStart=null}}\ncv.addEventListener('pointerdown',e=>{{closeContextMenu();cv.setPointerCapture?.(e.pointerId);pointers.set(e.pointerId,{{x:e.offsetX,y:e.offsetY}});dragMoved=false;if(e.pointerType==='touch'&&pointers.size===1){{cancelLongPress();longPressStart={{id:e.pointerId,x:e.offsetX,y:e.offsetY}};longPressTimer=setTimeout(()=>{{if(longPressStart&&pointers.has(longPressStart.id)&&!dragMoved&&pointers.size===1){{suppressNextTap=true;dragMoved=true;closeMarker();openContextMenu(longPressStart.x,longPressStart.y)}}cancelLongPress()}},600)}}if(pointers.size===2){{cancelLongPress();const a=[...pointers.values()],mx=(a[0].x+a[1].x)/2,my=(a[0].y+a[1].y)/2;pinchStart={{dist:Math.hypot(a[0].x-a[1].x,a[0].y-a[1].y),scale,wx:ox+(mx-cv.clientWidth/2)/scale,wz:oz+(my-cv.clientHeight/2)/scale}};followUid=''}}}});\ncv.addEventListener('pointermove',e=>{{if(contextMenu.classList.contains('open')&&e.pointerType==='touch')return;const old=pointers.get(e.pointerId);if(!old)return;const next={{x:e.offsetX,y:e.offsetY}};if(longPressStart&&longPressStart.id===e.pointerId&&Math.hypot(next.x-longPressStart.x,next.y-longPressStart.y)>8)cancelLongPress();pointers.set(e.pointerId,next);if(pointers.size===1&&!pinchStart){{const dx=next.x-old.x,dy=next.y-old.y;if(Math.abs(dx)+Math.abs(dy)>2)dragMoved=true;ox-=dx/scale;oz-=dy/scale;followUid='';requestDraw()}}else if(pointers.size>=2&&pinchStart){{cancelLongPress();const a=[...pointers.values()].slice(0,2),mx=(a[0].x+a[1].x)/2,my=(a[0].y+a[1].y)/2,dist=Math.max(10,Math.hypot(a[0].x-a[1].x,a[0].y-a[1].y));scale=clampScale(pinchStart.scale*(dist/pinchStart.dist));ox=pinchStart.wx-(mx-cv.clientWidth/2)/scale;oz=pinchStart.wz-(my-cv.clientHeight/2)/scale;dragMoved=true;requestDraw()}}}});\nfunction endPointer(e){{cancelLongPress();const p=pointers.get(e.pointerId);pointers.delete(e.pointerId);if(pointers.size<2)pinchStart=null;if(!dragMoved&&p){{const now=Date.now();if(e.pointerType==='touch'&&now-lastTapAt<320){{zoomAt(p.x,p.y,1.7);lastTapAt=0}}else{{handleTap(p.x,p.y);lastTapAt=now}}}}if(pointers.size===0)writeHashSoon()}}\ncv.addEventListener('pointerup',endPointer);cv.addEventListener('pointercancel',endPointer);cv.addEventListener('dblclick',e=>zoomAt(e.offsetX,e.offsetY,1.7));\nfunction nearestHit(x,y){{let best=null,bd=Infinity;for(const h of hitMarkers){{const d=Math.hypot(h.x-x,h.y-y);if(d<=h.r&&d<bd){{best=h;bd=d}}}}return best}}function handleTap(x,y){{closeContextMenu();if(suppressNextTap){{suppressNextTap=false;return}}if(measureMode){{const w=worldAt(x,y);if(!measureStart||measureEnd){{measureStart=w;measureEnd=null;measureMode=true;toast(tr('tapSecondPoint'))}}else{{measureEnd=w;measureMode=false;toast(`${{Math.round(Math.hypot(rx(measureEnd.x)-rx(measureStart.x),rz(measureEnd.z)-rz(measureStart.z))).toLocaleString()}} ${{tr('blocks')}}`)}}syncMeasureButton();requestDraw();return}}const h=nearestHit(x,y);if(h)showMarker(h);else closeMarker()}}\nfunction esc(s){{return String(s??'').replace(/[&<>\"']/g,c=>({{'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',\"'\":'&#39;'}}[c]))}}function fmtPlay(sec){{sec=Math.max(0,Number(sec||0));const d=Math.floor(sec/86400),h=Math.floor((sec%86400)/3600),m=Math.floor((sec%3600)/60);return d?`${{d}}d ${{h}}h`:h?`${{h}}h ${{m}}m`:`${{m}}m`}}\nfunction showMarker(h){{let html='';if(h.kind==='player'){{const p=h.obj;html=`<div class=\"markerTitle\">${{esc(p.name||tr('player'))}}<span class=\"markerType\">${{esc(tr('player').toUpperCase())}}</span></div><div class=\"markerText\">${{tr('ping')}} <b>${{Number(p.pingMilliseconds||0)}} ms</b><br>${{tr('session')}} <b>${{fmtPlay(p.sessionSeconds)}}</b> · ${{tr('total')}} <b>${{fmtPlay(p.totalPlaySeconds)}}</b><br>X ${{Math.round(rx(p.x))}} · Y ${{Math.round(p.y||0)}} · Z ${{Math.round(rz(p.z))}}</div><div class=\"markerActions\"><button data-follow=\"${{esc(p.uid||'')}}\">${{followUid===p.uid?tr('stopFollowing'):tr('followPlayer')}}</button><button data-center=\"player\">${{tr('center')}}</button></div>`}}else if(h.kind==='waypoint'){{const p=h.obj,t=(p.type||'major').toUpperCase(),ic=(p.icon||'pin').toUpperCase();html=`<div class=\"markerTitle\">${{esc(p.name||tr('waypoint'))}}<span class=\"markerType\">${{esc(t)}} · ${{esc(ic)}}</span></div><div class=\"markerText\">${{p.description?esc(p.description)+'<br>':''}}X ${{Math.round(p.x||0)}} · Y ${{Math.round(p.y||0)}} · Z ${{Math.round(p.z||0)}}${{p.ownerPlayerName?`<br>${{tr('placedBy')}} ${{esc(p.ownerPlayerName)}}`:''}}</div><div class=\"markerActions\"><button data-center=\"waypoint\">${{tr('center')}}</button></div>`}}else if(h.kind==='origin'){{html=`<div class=\"markerTitle\">${{tr('mapOrigin')}} <span class=\"markerType\">0,0</span></div><div class=\"markerText\">${{tr('mapOriginHelp')}}</div>`}}else{{const p=h.obj;html=`<div class=\"markerTitle\">${{esc(h.kind==='bad'?tr('badBlockEntity'):tr('blockActivity'))}}</div><div class=\"markerText\">${{esc(p.blockCode||'')}}<br>X ${{Math.round(rx(p.x))}} · Y ${{Math.round(p.y||0)}} · Z ${{Math.round(rz(p.z))}}</div>`}}markerCardBody.innerHTML=html;markerCard.classList.add('open');markerCard._hit=h;markerCard.querySelectorAll('[data-follow]').forEach(b=>b.onclick=()=>{{followUid=followUid===b.dataset.follow?'':b.dataset.follow;closeMarker();applyFollow();toast(followUid?tr('followingPlayer'):tr('followStopped'))}});markerCard.querySelectorAll('[data-center]').forEach(b=>b.onclick=()=>{{if(h.kind==='waypoint')setCenterAbs(waypointAbsX(h.obj),waypointAbsZ(h.obj),Math.max(scale,.12));else if(h.obj?.x!=null)setCenterAbs(h.obj.x,h.obj.z,Math.max(scale,.12))}})}}function closeMarker(){{markerCard.classList.remove('open');markerCard._hit=null}}document.getElementById('markerClose').onclick=closeMarker;\nfunction applyFollow(){{if(!followUid||!data)return;const p=(data.players||[]).find(x=>x.uid===followUid);if(!p){{followUid='';return}}ox=p.x;oz=p.z;requestDraw()}}\nfunction renderPlayers(){{const ps=data?.showPlayers?(data.players||[]):[];playerPanelCount.textContent=`${{ps.length}} ${{tr('online')}}`;playerPanelBody.innerHTML=ps.length?ps.slice().sort((a,b)=>String(a.name||'').localeCompare(String(b.name||''))).map(p=>`<div class=\"playerRow\" data-player=\"${{esc(p.uid||'')}}\"><div><div class=\"playerName\">${{esc(p.name||tr('player').toLowerCase())}}</div><div class=\"playerMeta\">X ${{Math.round(rx(p.x))}} · Y ${{Math.round(p.y||0)}} · Z ${{Math.round(rz(p.z))}}<br>${{tr('played')}} ${{fmtPlay(p.totalPlaySeconds)}}</div></div><div class=\"playerPing\">${{Number(p.pingMilliseconds||0)}} ms<div class=\"playerMeta\">${{tr('session').toLowerCase()}} ${{fmtPlay(p.sessionSeconds)}}</div></div></div>`).join(''):`<div class=\"playerRow\"><span class=\"muted\">${{tr('noPlayersOnline')}}</span></div>`;playerPanel.querySelectorAll('[data-player]').forEach(el=>el.onclick=()=>{{const p=ps.find(x=>x.uid===el.dataset.player);if(p){{setCenterAbs(p.x,p.z,Math.max(scale,.1));showMarker({{kind:'player',obj:p,x:sx(p.x),y:sy(p.z)}})}}}})}}\nfunction openPanel(id){{document.querySelectorAll('.floatingPanel').forEach(p=>p.classList.toggle('open',p.id===id));if(isMobile()&&id!=='layersPanel')playerPanel.classList.remove('mobileOpen')}}function closePanels(){{document.querySelectorAll('.floatingPanel').forEach(p=>p.classList.remove('open'));if(isMobile())playerPanel.classList.remove('mobileOpen')}}document.querySelectorAll('[data-close]').forEach(b=>b.onclick=()=>b.closest('.floatingPanel')?.classList.remove('open'));document.getElementById('layersBtn').onclick=()=>openPanel('layersPanel');document.getElementById('searchBtn').onclick=()=>{{openPanel('searchPanel');setTimeout(()=>document.getElementById('searchInput').focus(),30)}};document.getElementById('helpBtn').onclick=()=>openPanel('helpPanel');document.getElementById('playersBtn').onclick=()=>{{if(isMobile()){{closePanels();playerPanel.classList.toggle('mobileOpen')}}else playerPanel.classList.toggle('collapsed')}};document.getElementById('playerPanelClose').onclick=()=>{{if(isMobile())playerPanel.classList.remove('mobileOpen');else playerPanel.classList.add('collapsed')}};document.getElementById('homeBtn').onclick=home;document.getElementById('fitBtn').onclick=fit;function syncMeasureButton(){{rulerBtn.classList.toggle('active',measureMode||!!measureStart)}}function clearMeasurement(){{measureStart=null;measureEnd=null;measureMode=false;measureReadout.classList.remove('active');syncMeasureButton();requestDraw()}}rulerBtn.onclick=()=>{{closePanels();closeContextMenu();if(measureStart){{clearMeasurement();toast(tr('measurementCleared'))}}else{{measureMode=true;syncMeasureButton();toast(tr('tapFirstPoint'))}}}};\nfunction search(){{const q=document.getElementById('searchInput').value.trim(),out=document.getElementById('searchResults');if(!q){{out.innerHTML='';return}}const cm=q.match(/^\\s*(-?\\d+(?:\\.\\d+)?)\\s*[, ]\\s*(-?\\d+(?:\\.\\d+)?)\\s*$/);if(cm){{const x=Number(cm[1]),z=Number(cm[2]);setCenterAbs(x+(data?.coordinateOriginX||0),z+(data?.coordinateOriginZ||0),Math.max(scale,.13));closePanels();return}}const low=q.toLowerCase(),results=[];(data?.players||[]).forEach(p=>{{if(String(p.name||'').toLowerCase().includes(low))results.push({{kind:'player',obj:p,label:`${{tr('player')}} · ${{p.name}}`}})}});(data?.waypoints||[]).forEach(p=>{{if(String(p.name||'').toLowerCase().includes(low))results.push({{kind:'waypoint',obj:p,label:`${{(p.type||'major')}} · ${{(p.icon||'pin')}} · ${{p.name}}`}})}});out.innerHTML=results.length?results.slice(0,20).map((r,i)=>`<button class=\"searchItem\" data-sr=\"${{i}}\">${{esc(r.label)}}</button>`).join(''):`<div class=\"helpNote\">${{tr('noMatching')}}</div>`;out.querySelectorAll('[data-sr]').forEach(b=>b.onclick=()=>{{const r=results[Number(b.dataset.sr)];if(!r)return;if(r.kind==='player')setCenterAbs(r.obj.x,r.obj.z,Math.max(scale,.13));else setCenterAbs(waypointAbsX(r.obj),waypointAbsZ(r.obj),Math.max(scale,.13));closePanels()}})}}document.getElementById('searchGo').onclick=search;document.getElementById('searchInput').addEventListener('keydown',e=>{{if(e.key==='Enter')search()}});\nfunction closeContextMenu(){{contextMenu.classList.remove('open');contextWorld=null}}function openContextMenu(px,py){{const w=worldAt(px,py);contextWorld=w;contextCoords.textContent=`X ${{Math.round(rx(w.x)).toLocaleString()}} · Z ${{Math.round(rz(w.z)).toLocaleString()}}`;const rect=document.getElementById('mapWrap').getBoundingClientRect(),menuW=205,menuH=150;contextMenu.style.left=`${{Math.max(6,Math.min(cv.clientWidth-menuW-6,px))}}px`;contextMenu.style.top=`${{Math.max(6,Math.min(cv.clientHeight-menuH-6,py))}}px`;document.getElementById('contextMeasure').textContent=measureStart&&!measureEnd?tr('measureToHere'):tr('measureFromHere');document.getElementById('contextClearMeasure').style.display=measureStart?'block':'none';contextMenu.classList.add('open')}}async function copyCoords(w){{const text=`${{Math.round(rx(w.x))}} ${{Math.round(rz(w.z))}}`;try{{await navigator.clipboard.writeText(text);toast(tr('coordinatesCopied'))}}catch{{toast(text)}}}}document.getElementById('contextCopy').onclick=()=>{{if(contextWorld)copyCoords(contextWorld);closeContextMenu()}};document.getElementById('contextMeasure').onclick=()=>{{if(!contextWorld)return;if(measureStart&&!measureEnd){{measureEnd={{...contextWorld}};measureMode=false;toast(`${{Math.round(Math.hypot(rx(measureEnd.x)-rx(measureStart.x),rz(measureEnd.z)-rz(measureStart.z))).toLocaleString()}} ${{tr('blocks')}}`)}}else{{measureStart={{...contextWorld}};measureEnd=null;measureMode=true;toast(tr('tapSecondPoint'))}}syncMeasureButton();closeContextMenu();requestDraw()}};document.getElementById('contextClearMeasure').onclick=()=>{{clearMeasurement();closeContextMenu()}};cv.addEventListener('contextmenu',e=>{{e.preventDefault();closeMarker();openContextMenu(e.offsetX,e.offsetY)}});document.addEventListener('pointerdown',e=>{{if(!contextMenu.contains(e.target)&&e.target!==cv)closeContextMenu()}});\nfunction toast(text){{toastEl.textContent=text;toastEl.classList.add('show');clearTimeout(toast._t);toast._t=setTimeout(()=>toastEl.classList.remove('show'),1800)}}function shareUrl(){{const u=new URL(location.href);u.hash=`x=${{Math.round(rx(ox))}}&z=${{Math.round(rz(oz))}}&s=${{scale.toFixed(5)}}`;return u.toString()}}document.getElementById('shareBtn').onclick=async()=>{{const u=shareUrl();try{{await navigator.clipboard.writeText(u);toast(tr('mapLinkCopied'))}}catch{{location.hash=u.split('#')[1];toast(tr('mapLinkUpdated'))}}}};function writeHashSoon(){{const now=Date.now();if(now-lastHashWrite<700){{clearTimeout(writeHashSoon._t);writeHashSoon._t=setTimeout(writeHashSoon,720);return}}lastHashWrite=now;history.replaceState(null,'',shareUrl())}}function applyHash(){{const q=new URLSearchParams(location.hash.replace(/^#/,'')),x=Number(q.get('x')),z=Number(q.get('z')),s=Number(q.get('s'));if(Number.isFinite(x)&&Number.isFinite(z)){{ox=x+(data?.coordinateOriginX||0);oz=z+(data?.coordinateOriginZ||0);if(Number.isFinite(s))scale=clampScale(s);return true}}return false}}\nfunction fmtEta(ms){{if(!Number.isFinite(ms)||ms<0)return tr('calculating');let s=Math.max(0,Math.round(ms/1000)),h=Math.floor(s/3600),m=Math.floor((s%3600)/60),r=s%60;return h?`${{h}}:${{String(m).padStart(2,'0')}}:${{String(r).padStart(2,'0')}}`:`${{m}}:${{String(r).padStart(2,'0')}}`}}function phaseLabel(phase){{const p=String(phase||'working').toLowerCase();return({{coverage:tr('phaseCoverage'),rendering:tr('phaseRendering'),overview:tr('phaseOverview'),pyramid:tr('phasePyramid'),finalizing:tr('phaseFinalizing'),complete:tr('phaseComplete'),cancelled:tr('phaseCancelled'),failed:tr('phaseFailed'),waiting:tr('phaseWaiting')}})[p]||p.replace(/[-_]+/g,' ').replace(/\\b\\w/g,c=>c.toUpperCase())}}function progressPercent(t){{const p=Number(t?.progressPercent);if(Number.isFinite(p))return Math.max(0,Math.min(100,p));const total=Number(t?.totalTiles||0),done=Number(t?.writtenTiles||0);return total>0?Math.max(0,Math.min(100,5+(done/total)*85)):0}}function renderExportProgress(t){{const active=t?.state==='exporting';exportProgress.classList.toggle('active',active);if(!active)return;const p=progressPercent(t),elapsed=Number(t?.startedUnixMilliseconds||0)>0?Date.now()-Number(t.startedUnixMilliseconds):Number(t?.totalElapsedMilliseconds||0),eta=Number(t?.estimatedRemainingMilliseconds);exportProgressTitle.textContent=phaseLabel(t?.phase);exportProgressPct.textContent=`${{p.toFixed(p<10?1:0)}}%`;exportProgressFill.style.width=`${{p.toFixed(2)}}%`;exportProgressMeta.innerHTML=`<span>${{Number(t?.writtenTiles||0).toLocaleString()}} / ${{Number(t?.totalTiles||0).toLocaleString()}} ${{tr('tiles')}}</span><span>${{Number(t?.processedChunkColumns||0).toLocaleString()}} ${{tr('chunks')}}</span><span>${{tr('elapsed')}} ${{fmtEta(elapsed)}}</span><span>${{tr('eta')}} ${{fmtEta(eta)}}</span>`}}\nfunction updateStatus(){{const t=data?.terrain||{{}},online=Number(data?.activePlayerCount||0),last=Number(data?.lastTerrainExportDurationMilliseconds||0);statusEl.textContent=t.state==='exporting'?`${{phaseLabel(t.phase)}} ${{progressPercent(t).toFixed(0)}}% · ${{tr('eta')}} ${{fmtEta(t.estimatedRemainingMilliseconds)}}`:`${{online}} ${{tr('online')}} · ${{data?.waypoints?.length||0}} ${{tr('waypoints').toLowerCase()}}${{last>0?` · ${{tr('lastExport')}} ${{fmtEta(last)}}`:''}}`;document.getElementById('waypointLimitHelp').textContent=`${{tr('serverLimits')}}: ${{tr('major')}} ${{Number(data?.playerMajorWaypointLimit??5)}} · ${{tr('minor')}} ${{Number(data?.playerMinorWaypointLimit??10)}} ${{tr('perPlayer')}}.`;const b=document.getElementById('exportBenchmarkHelp');if(b)b.textContent=last>0?tr('lastTerrainExport',fmtEta(last)):tr('noExportBenchmark')}}\nfunction scheduleTerrainPoll(){{if(terrainPollTimer||document.hidden||data?.terrain?.state!=='exporting')return;terrainPollTimer=setTimeout(pollTerrainStatus,1000)}}function probeTerrainStatus(){{if(document.hidden||!data||data?.terrain?.state==='exporting')return;fetch('/api/terrain-status',{{cache:'no-store'}}).then(r=>r.ok?r.json():null).then(t=>{{if(t?.state==='exporting'){{data.terrain=t;renderExportProgress(t);updateStatus();scheduleTerrainPoll()}}}}).catch(()=>{{}})}}async function pollTerrainStatus(){{terrainPollTimer=null;if(document.hidden||data?.terrain?.state!=='exporting')return;try{{const r=await fetch('/api/terrain-status',{{cache:'no-store'}}),t=await r.json();data.terrain=t;renderExportProgress(t);updateStatus();if(t.state==='exporting')scheduleTerrainPoll();else await load(false)}}catch{{scheduleTerrainPoll()}}}}\nfunction formatMapIngameDate(d){{const y=Number(d?.ingameYear||0),m=Number(d?.ingameMonth||0),day=Number(d?.ingameDay||0),h=Number(d?.ingameHour||0);if(y&&m&&day)return `${{tr('year')}} ${{y}}, ${{tr('month')}} ${{m}}, ${{tr('day')}} ${{day}}, ${{h.toFixed(2)}}h`;const raw=String(d?.ingameDate||'').trim();return raw&&raw.toLowerCase()!=='unknown'?raw:tr('unknownDate')}}\nasync function load(first=false){{if(mapLoadBusy||(document.hidden&&!first))return;mapLoadBusy=true;try{{const r=await fetch('/api/map',{{cache:'no-store'}});if(!r.ok)throw new Error(r.status);data=await r.json();const retainedPlayerSnapshot=latestPlayerSnapshot;applyPlayerSnapshot(data);applyPlayerSnapshot(retainedPlayerSnapshot);if(lbButton)lbButton.style.display=data?.showLeaderboards===false?'none':'';const gen=Number(data?.terrainManifest?.generatedUnixMilliseconds||0);if(lastTerrainGeneration&&gen&&gen!==lastTerrainGeneration){{tileCache.clear();overviewImage=null;overviewVersion=0}}lastTerrainGeneration=gen||lastTerrainGeneration;dateEl.textContent=formatMapIngameDate(data);activePlayersEl.textContent=Number(data.activePlayerCount||0).toLocaleString();renderPlayers();renderExportProgress(data.terrain||{{}});updateStatus();if(data.terrain?.state==='exporting')scheduleTerrainPoll();applyFollow();if(first){{firstLoad=false;initializeFirstView()}}else requestDraw()}}catch(e){{statusEl.textContent=tr('mapFeedUnavailable');dateEl.textContent='—';activePlayersEl.textContent='—'}}finally{{mapLoadBusy=false}}}}\n\nlet leaderboardData=null,leaderboardTab='overview',leaderboardTimer=null,leaderboardSkillSearch='',leaderboardSkillSort='name';\nconst lbOverlay=document.getElementById('leaderboardOverlay'),lbBody=document.getElementById('leaderboardBody'),lbButton=document.getElementById('leaderboardBtn');\nfunction lbNum(v){{const n=Number(v||0);if(n>=1e9)return `${{(n/1e9).toFixed(n>=1e10?1:2)}}B`;if(n>=1e6)return `${{(n/1e6).toFixed(n>=1e7?1:2)}}M`;if(n>=1e3)return `${{(n/1e3).toFixed(n>=1e4?1:2)}}K`;return Math.round(n).toLocaleString()}}\nfunction lbDuration(sec){{sec=Math.max(0,Number(sec||0));const h=Math.floor(sec/3600),m=Math.floor((sec%3600)/60);if(h>=24)return `${{Math.floor(h/24).toLocaleString()}}d ${{h%24}}h`;if(h)return `${{h.toLocaleString()}}h ${{m}}m`;return `${{m}}m`}}\nfunction lbDate(ms){{const n=Number(ms||0);if(!n)return '—';try{{return new Date(n).toLocaleDateString()}}catch{{return '—'}}}}\nfunction lbPlayerName(row){{return `<span class=\"lbPlayer\" data-lb-player=\"${{esc(row.player||'')}}\">${{row.online?'<span class=\"lbOnline\"></span>':''}}${{esc(row.player||'Unknown')}}</span>`}}\nfunction lbListRows(rows,formatter){{rows=Array.isArray(rows)?rows:[];if(!rows.length)return '<div class=\"lbEmpty\">No ranked data yet.</div>';return `<div class=\"lbRows\">${{rows.map(r=>{{const rank=Number(r.rank||0);return `<div class=\"lbRow\"><div class=\"lbRank rank${{rank<=3?rank:0}}\">${{rank||'—'}}</div><div>${{lbPlayerName(r)}}</div><div class=\"lbValue\">${{esc(formatter(r))}}</div></div>`}}).join('')}}</div>`}}\nfunction lbCard(title,icon,rows,formatter){{return `<div class=\"lbCard\"><div class=\"lbCardHead\"><span class=\"lbCardIcon\">${{icon}}</span><span>${{esc(title)}}</span></div>${{lbListRows(rows,formatter)}}</div>`}}\nfunction lbProgressLeaders(){{\n  const limit=Math.max(3,Number(leaderboardData?.topPlayers||5));\n  return (leaderboardData?.players||[]).map(p=>({{player:p.player,online:p.online,value:Number(p.skillsLeveled||0)}}))\n    .sort((a,b)=>b.value-a.value||String(a.player||'').localeCompare(String(b.player||''))).slice(0,limit)\n    .map((x,i)=>({{...x,rank:i+1,displayValue:`${{x.value.toLocaleString()}} skills`}}));\n}}\nfunction lbStatusHtml(){{\n  const players=Array.isArray(leaderboardData?.players)?leaderboardData.players:[];\n  const skills=Array.isArray(leaderboardData?.skills)?leaderboardData.skills:[];\n  const online=players.filter(p=>p.online).length;\n  return `<div class=\"lbStatusBar\"><span class=\"lbStatusChip gold\">♛ ${{Number(leaderboardData?.playerCount||0).toLocaleString()}} tracked players</span><span class=\"lbStatusChip ${{leaderboardData?.xSkillsDetected?'good':''}}\">✦ ${{leaderboardData?.xSkillsDetected?`${{skills.length}} XSkills skills`:'XSkills optional'}}</span><span class=\"lbStatusChip good\">● ${{online.toLocaleString()}} online</span><span class=\"lbStatusChip\">↻ ${{Number(leaderboardData?.refreshSeconds||60)}}s refresh</span></div>${{leaderboardData?.xSkillsStatus?`<div class=\"lbNotice\">${{esc(leaderboardData.xSkillsStatus)}}</div>`:''}}`;\n}}\nfunction lbPodium(rows,title,formatter){{\n  rows=(Array.isArray(rows)?rows:[]).slice(0,3);if(!rows.length)return '';\n  const medals=['\ud83e\udd47','\ud83e\udd48','\ud83e\udd49'],classes=['first','second','third'];\n  return `<div class=\"lbPodiumWrap\"><div class=\"lbPodiumTitle\">${{esc(title)}}</div><div class=\"lbPodium\">${{rows.map((r,i)=>`<div class=\"lbPodiumPlace ${{classes[i]}}\" data-lb-player=\"${{esc(r.player||'')}}\"><div class=\"lbPodiumMedal\">${{medals[i]}}</div><div class=\"lbPodiumPlayer\">${{r.online?'<span class=\"lbOnline\"></span>':''}}${{esc(r.player||'Unknown')}}</div><div class=\"lbPodiumValue\">${{esc(formatter(r))}}</div></div>`).join('')}}</div></div>`;\n}}\nfunction bindLbPlayers(){{lbBody.querySelectorAll('[data-lb-player]').forEach(e=>e.onclick=()=>showLeaderboardProfile(e.dataset.lbPlayer||''))}}\nfunction applyLbSkillFilter(){{\n  const query=leaderboardSkillSearch.trim().toLowerCase();let visible=0;\n  lbBody.querySelectorAll('[data-lb-skill-card]').forEach(card=>{{const show=!query||String(card.dataset.lbSkillSearch||'').includes(query);card.style.display=show?'':'none';if(show)visible++;}});\n  const count=document.getElementById('lbSkillCount');if(count)count.textContent=`${{visible}} skill${{visible===1?'':'s'}} shown`;\n}}\nfunction bindLbSkillTools(){{\n  const search=document.getElementById('lbSkillSearch'),sort=document.getElementById('lbSkillSort');\n  if(search){{search.oninput=()=>{{leaderboardSkillSearch=search.value||'';applyLbSkillFilter()}};}}\n  if(sort){{sort.onchange=()=>{{leaderboardSkillSort=sort.value||'name';renderLeaderboard()}};}}\n  applyLbSkillFilter();\n}}\nfunction renderLeaderboard(){{\n  if(!leaderboardData){{lbBody.innerHTML='<div class=\"lbEmpty\">Loading leaderboards…</div>';return}}\n  if(!leaderboardData.enabled){{lbBody.innerHTML=`<div class=\"lbNotice\">${{esc(leaderboardData.xSkillsStatus||'Leaderboards are disabled.')}}</div>`;return}}\n  document.querySelectorAll('[data-lb-tab]').forEach(b=>b.classList.toggle('active',b.dataset.lbTab===leaderboardTab));\n  const status=lbStatusHtml();\n  if(leaderboardTab==='skills'){{\n    let skills=[...(Array.isArray(leaderboardData.skills)?leaderboardData.skills:[])];\n    const topLevel=sk=>Math.max(0,...(sk.entries||[]).map(r=>Number(r.level||0)));\n    const topXp=sk=>Math.max(0,...(sk.entries||[]).map(r=>Number(r.experience||0)));\n    skills.sort((a,b)=>leaderboardSkillSort==='level'?topLevel(b)-topLevel(a)||String(a.displayName||a.name||'').localeCompare(String(b.displayName||b.name||'')):leaderboardSkillSort==='xp'?topXp(b)-topXp(a)||String(a.displayName||a.name||'').localeCompare(String(b.displayName||b.name||'')):String(a.displayName||a.name||'').localeCompare(String(b.displayName||b.name||'')));\n    const tools=`<div class=\"lbTools\"><label>Find skill<input id=\"lbSkillSearch\" value=\"${{esc(leaderboardSkillSearch)}}\" placeholder=\"Mining, farming, combat…\"></label><label>Sort skills<select id=\"lbSkillSort\"><option value=\"name\" ${{leaderboardSkillSort==='name'?'selected':''}}>Name</option><option value=\"level\" ${{leaderboardSkillSort==='level'?'selected':''}}>Highest level</option><option value=\"xp\" ${{leaderboardSkillSort==='xp'?'selected':''}}>Highest current XP</option></select></label><span id=\"lbSkillCount\" class=\"lbToolCount\">${{skills.length}} skills shown</span></div>`;\n    lbBody.innerHTML=status+tools+(skills.length?`<div class=\"lbGrid\">${{skills.map(sk=>{{\n      const entries=Array.isArray(sk.entries)?sk.entries:[];const accent=/^#[0-9a-fA-F]{{6}}$/.test(sk.accent||'')?sk.accent:'#60a5fa';\n      const searchText=String(`${{sk.displayName||''}} ${{sk.name||''}}`).toLowerCase();\n      return `<div class=\"skillCard\" data-lb-skill-card=\"1\" data-lb-skill-search=\"${{esc(searchText)}}\" style=\"--skillAccent:${{accent}}\"><div class=\"skillHead\"><div class=\"skillIcon\">${{esc(sk.icon||'★')}}</div><div><div class=\"skillName\">${{esc(sk.displayName||sk.name||'Skill')}}</div><div class=\"leaderboardSub\">${{entries.length?`Leader Lv. ${{Math.max(...entries.map(r=>Number(r.level||0)))}}`:'No ranked progress'}}</div></div></div><div class=\"skillTableHead\"><span>#</span><span>Player</span><span>Level</span><span>XP</span></div>${{entries.length?entries.map(r=>`<div class=\"skillEntry\"><span class=\"rank rank${{Number(r.rank||0)<=3?Number(r.rank||0):0}}\">${{Number(r.rank||0)}}</span><span class=\"name\" data-lb-player=\"${{esc(r.player||'')}}\">${{r.online?'<span class=\"lbOnline\"></span>':''}}${{esc(r.player||'')}}</span><span class=\"level\">${{Number(r.level||0).toLocaleString()}}</span><span class=\"xp\">${{lbNum(r.experience)}}</span></div>`).join(''):'<div class=\"lbEmpty\">No progress yet.</div>'}}</div>`;\n    }}).join('')}}</div>`:'<div class=\"lbEmpty\">No XSkills skill data is available.</div>');\n    bindLbSkillTools();\n  }}else if(leaderboardTab==='playtime'){{\n    lbBody.innerHTML=status+lbPodium(leaderboardData.playtime,'Playtime Champions',r=>r.displayValue||lbDuration(r.value))+`<div class=\"lbOverviewGrid\">${{lbCard('Most Tracked Playtime','⏱',leaderboardData.playtime,r=>r.displayValue||lbDuration(r.value))}}${{lbCard('Longest Current Session','⌛',leaderboardData.currentSession,r=>r.displayValue||lbDuration(r.value))}}${{lbCard('Server Veterans','★',leaderboardData.veterans,r=>r.displayValue||'—')}}</div><div class=\"lbFooterNote\">Playtime is tracked by Server Essentials. First-seen dates are based on the Suite player database.</div>`;\n  }}else{{\n    const podiumRows=(leaderboardData.totalLevels||[]).length?leaderboardData.totalLevels:leaderboardData.playtime;\n    const podiumFormat=(leaderboardData.totalLevels||[]).length?(r=>`${{Math.round(r.value||0).toLocaleString()}} total levels`):(r=>r.displayValue||lbDuration(r.value));\n    lbBody.innerHTML=status+lbPodium(podiumRows,(leaderboardData.totalLevels||[]).length?'Top Overall Skill Levels':'Community Leaders',podiumFormat)+`<div class=\"lbSectionTitle\">Community Rankings</div><div class=\"lbOverviewGrid\">${{lbCard('Top Total Skill Levels','♛',leaderboardData.totalLevels,r=>r.displayValue||Math.round(r.value||0).toLocaleString())}}${{lbCard('Top Cumulative Skill XP','✦',leaderboardData.totalExperience,r=>r.displayValue||lbNum(r.value))}}${{lbCard('Most Skills Progressed','◆',lbProgressLeaders(),r=>r.displayValue||`${{Math.round(r.value||0)}} skills`)}}${{lbCard('Most Tracked Playtime','⏱',leaderboardData.playtime,r=>r.displayValue||lbDuration(r.value))}}${{lbCard('Longest Current Session','⌛',leaderboardData.currentSession,r=>r.displayValue||lbDuration(r.value))}}</div><div class=\"lbFooterNote\">Click any ranked player for their public progression profile.</div>`;\n  }}\n  bindLbPlayers();\n}}\nfunction showLeaderboardProfile(name){{\n  const p=(leaderboardData?.players||[]).find(x=>String(x.player||'').toLowerCase()===String(name||'').toLowerCase());if(!p)return;\n  const skills=Array.isArray(p.topSkills)?p.topSkills:[];\n  lbBody.innerHTML=`<button class=\"button lbBack\" id=\"lbBack\">← Back to ${{esc(leaderboardTab==='skills'?'Skills':leaderboardTab==='playtime'?'Playtime':'Overview')}}</button><div class=\"lbProfile\"><div class=\"lbProfileHero\"><div class=\"lbProfileName\">${{p.online?'<span class=\"lbOnline\"></span>':''}}${{esc(p.player||'')}}</div><div class=\"leaderboardSub\">${{p.online?'Online now':'Offline'}} · Public progression profile</div><div class=\"lbProfileStats\"><div class=\"lbStat\"><div class=\"lbStatLabel\">Tracked Playtime</div><div class=\"lbStatValue\">${{lbDuration(p.totalPlaySeconds)}}</div></div><div class=\"lbStat\"><div class=\"lbStatLabel\">Current Session</div><div class=\"lbStatValue\">${{p.online?lbDuration(p.sessionSeconds):'—'}}</div></div><div class=\"lbStat\"><div class=\"lbStatLabel\">Total Skill Levels</div><div class=\"lbStatValue\">${{Number(p.totalSkillLevels||0).toLocaleString()}}</div></div><div class=\"lbStat\"><div class=\"lbStatLabel\">Cumulative Skill XP</div><div class=\"lbStatValue\">${{lbNum(p.totalSkillExperience)}}</div></div><div class=\"lbStat\"><div class=\"lbStatLabel\">Skills Progressed</div><div class=\"lbStatValue\">${{Number(p.skillsLeveled||0).toLocaleString()}}</div></div><div class=\"lbStat\"><div class=\"lbStatLabel\">First Seen</div><div class=\"lbStatValue\">${{lbDate(p.firstSeenUnixMilliseconds)}}</div></div><div class=\"lbStat\"><div class=\"lbStatLabel\">Last Seen</div><div class=\"lbStatValue\">${{p.online?'Now':lbDate(p.lastSeenUnixMilliseconds)}}</div></div></div>${{skills.length?`<div class=\"lbSectionTitle\">Top Skills</div><div class=\"lbTopSkills\">${{skills.map(sk=>{{const accent=/^#[0-9a-fA-F]{{6}}$/.test(sk.accent||'')?sk.accent:'#4a6171';return `<div class=\"lbTopSkill\" style=\"--skillAccent:${{accent}}\"><div class=\"skillIcon\">${{esc(sk.icon||'★')}}</div><div><div style=\"font-weight:900\">${{esc(sk.displayName||sk.name||'Skill')}}</div><div class=\"leaderboardSub\">Level ${{Number(sk.level||0).toLocaleString()}} · ${{lbNum(sk.experience)}} current XP</div></div></div>`}}).join('')}}</div>`:''}}</div></div>`;\n  document.getElementById('lbBack').onclick=renderLeaderboard;\n}}\nasync function fetchLeaderboard(){{\n  try{{const r=await fetch('/api/leaderboard',{{cache:'no-store'}});if(!r.ok)throw new Error(r.status);leaderboardData=await r.json();renderLeaderboard()}}catch(e){{lbBody.innerHTML='<div class=\"lbEmpty\">Leaderboard feed is unavailable.</div>'}}\n}}\nfunction scheduleLeaderboardRefresh(){{clearTimeout(leaderboardTimer);if(!lbOverlay.classList.contains('open'))return;const sec=Math.max(30,Number(leaderboardData?.refreshSeconds||60));leaderboardTimer=setTimeout(async()=>{{await fetchLeaderboard();scheduleLeaderboardRefresh()}},sec*1000)}}\nfunction isLeaderboardPath(){{return /\\/leaderboard(?:\\.html)?\\/?$/i.test(location.pathname)}}\nconst LEADERBOARD_ONLY=isLeaderboardPath();\nlet leaderboardReturnUrl=LEADERBOARD_ONLY?'/':`${{location.pathname}}${{location.search}}${{location.hash}}`;\nlet leaderboardPushedFromMap=false;\nasync function openLeaderboard(updateUrl=true){{\n  closePanels();closeContextMenu();\n  if(updateUrl&&!isLeaderboardPath()){{\n    leaderboardReturnUrl=`${{location.pathname}}${{location.search}}${{location.hash}}`;\n    history.pushState({{nssView:'leaderboard'}},'', '/leaderboard');\n    leaderboardPushedFromMap=true;\n  }}\n  leaderboardData=null;lbBody.innerHTML='<div class=\"lbEmpty\">Loading leaderboards…</div>';lbOverlay.classList.add('open');lbOverlay.setAttribute('aria-hidden','false');lbButton?.setAttribute('aria-pressed','true');await fetchLeaderboard();scheduleLeaderboardRefresh();\n}}\nfunction closeLeaderboard(updateUrl=true){{\n  lbOverlay.classList.remove('open');lbOverlay.setAttribute('aria-hidden','true');lbButton?.setAttribute('aria-pressed','false');clearTimeout(leaderboardTimer);\n  if(!updateUrl||!isLeaderboardPath())return;\n  if(leaderboardPushedFromMap){{history.back();return}}\n  location.href='/';\n}}\nlbButton.onclick=()=>openLeaderboard(true);document.getElementById('leaderboardClose').onclick=()=>closeLeaderboard(true);document.querySelectorAll('[data-lb-tab]').forEach(b=>b.onclick=()=>{{leaderboardTab=b.dataset.lbTab||'overview';renderLeaderboard()}});lbOverlay.addEventListener('click',e=>{{if(e.target===lbOverlay)closeLeaderboard(true)}});document.addEventListener('keydown',e=>{{if(e.key==='Escape'&&lbOverlay.classList.contains('open'))closeLeaderboard(true)}});\nwindow.addEventListener('popstate',()=>{{\n  if(isLeaderboardPath()){{if(!lbOverlay.classList.contains('open'))openLeaderboard(false);return}}\n  leaderboardPushedFromMap=false;leaderboardReturnUrl=`${{location.pathname}}${{location.search}}${{location.hash}}`;\n  if(lbOverlay.classList.contains('open'))closeLeaderboard(false);\n}});\n\nresize();\nif(LEADERBOARD_ONLY){{\n  document.body.classList.add('leaderboardOnly');\n  openLeaderboard(false);\n}}else{{\n  load(true);\n  pollPlayers();\n  setInterval(()=>load(false),REFRESH);\n  setInterval(probeTerrainStatus,5000);\n  document.addEventListener('visibilitychange',()=>{{if(!document.hidden){{load(false);clearTimeout(playerPollTimer);pollPlayers();probeTerrainStatus()}}}});\n}}\n</script></body></html>";
	}

	private static string Html(string value)
	{
		return WebUtility.HtmlEncode(value ?? "");
	}

	private static string Js(string value)
	{
		return JsonSerializer.Serialize(value ?? "");
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		disposed = true;
		try
		{
			forcedTerrainExportCts?.Cancel();
		}
		catch
		{
		}
		if (playerListenerId >= 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(playerListenerId);
			}
			catch
			{
			}
			playerListenerId = -1L;
		}
		if (ingameDateListenerId >= 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(ingameDateListenerId);
			}
			catch
			{
			}
			ingameDateListenerId = -1L;
		}
		if (playerCountHandlersRegistered)
		{
			try
			{
				api.Event.PlayerNowPlaying -= OnPlayerCountNowPlaying;
			}
			catch
			{
			}
			try
			{
				api.Event.PlayerDisconnect -= OnPlayerCountDisconnect;
			}
			catch
			{
			}
			playerCountHandlersRegistered = false;
		}
		leaderboardService.Dispose();
		StopListener();
		ClearTerrainFileCache();
	}
}
