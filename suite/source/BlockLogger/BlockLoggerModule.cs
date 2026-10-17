using System;
using BlockLogger.Admin;
using BlockLogger.Analytics;
using BlockLogger.Announcements;
using BlockLogger.AntiCheat;
using BlockLogger.Commands;
using BlockLogger.Config;
using BlockLogger.Hooks;
using BlockLogger.ModUpdates;
using BlockLogger.PlayerCommands;
using BlockLogger.PlayerList;
using BlockLogger.Reports;
using BlockLogger.Restarts;
using BlockLogger.ServerHealth;
using BlockLogger.Services;
using BlockLogger.WebConsole;
using BlockLogger.WebDashboard;
using BlockLogger.WebMap;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger;

public sealed class BlockLoggerModule : IDisposable
{
	private readonly ICoreServerAPI api;

	private readonly BlockLoggerConfig config;

	private readonly SuiteFeatureConfig features;

	private readonly SuiteFeatureConfig featureSettings;

	private readonly Action reloadConfig;

	private LogStorageService? storage;

	private LogQueryService? queries;

	private EntityHooks? entityHooks;

	private BlockHooks? blockHooks;

	private PlayerRollbackCommands? rollbackCommands;

	private ContainerHooks? containerHooks;

	private ServerHealthService? serverHealth;

	private PlayerCountAnalyticsService? playerAnalytics;

	private ReportsService? reportsService;

	private AnnouncementService? announcementService;

	private WebConsoleService? webConsoleService;

	private WebMapBridgeService? webMapService;

	private WebMapDirtyChunkJournal? webMapDirtyJournal;

	private ModUpdateCheckerService? modUpdateChecker;

	private AntiCheatService? antiCheatService;

	private PublicPlayerListServer? publicPlayerListServer;

	private AdminService? adminService;

	private PlayerCommandsService? playerCommandsService;

	private AutomatedRestartService? automatedRestartService;

	private ChatDisplayService? chatDisplayService;

	private SupporterLampService? supporterLampService;

	private SupporterNicknameService? supporterNicknameService;

	private CustomPatchService? customPatchService;

	private WebDashboardService? webDashboard;

	private long pruneListenerId = -1L;

	private bool started;

	public LogStorageService? Storage => storage;

	public LogQueryService? Queries => queries;

	public ServerHealthService? ServerHealth => serverHealth;

	public PlayerCountAnalyticsService? PlayerAnalytics => playerAnalytics;

	public ReportsService? Reports => reportsService;

	public AnnouncementService? Announcements => announcementService;

	public WebConsoleService? WebConsole => webConsoleService;

	public WebMapBridgeService? WebMap => webMapService;

	public ModUpdateCheckerService? ModUpdates => modUpdateChecker;

	public AntiCheatService? AntiCheat => antiCheatService;

	public PublicPlayerListServer? PlayerList => publicPlayerListServer;

	public int ActiveContainerSessions => containerHooks?.ActiveSessionCount ?? 0;

	public long ContainerSessionsEvicted => containerHooks?.SessionsEvictedForCapacity ?? 0;

	public long ContainerSessionsWithExpiredInventory => containerHooks?.SessionsDroppedBecauseInventoryExpired ?? 0;

	public AdminService? Admin => adminService;

	public PlayerCommandsService? PlayerCommands => playerCommandsService;

	public AutomatedRestartService? AutomatedRestarts => automatedRestartService;

	public ChatDisplayService? ChatDisplay => chatDisplayService;

	public SupporterLampService? SupporterLamp => supporterLampService;

	public SupporterNicknameService? SupporterNickname => supporterNicknameService;

	public CustomPatchService? CustomPatches => customPatchService;

	public WebDashboardService? WebDashboard => webDashboard;

	public BlockLoggerConfig Config => config;

	public SuiteFeatureConfig Features => features;

	public SuiteFeatureConfig FeatureSettings => featureSettings;

	public BlockLoggerModule(ICoreServerAPI api, BlockLoggerConfig config, SuiteFeatureConfig features, SuiteFeatureConfig featureSettings, Action reloadConfig)
	{
		this.api = api;
		this.config = config;
		this.features = features;
		this.featureSettings = featureSettings;
		this.reloadConfig = reloadConfig;
	}

	public void Start()
	{
		if (started)
		{
			return;
		}
		started = true;
		supporterNicknameService = new SupporterNicknameService(api);
		supporterNicknameService.Start();
		publicPlayerListServer = new PublicPlayerListServer(api, () => adminService, (string uid, string realName) => supporterNicknameService?.ResolveDisplayName(uid, realName) ?? realName);
		publicPlayerListServer.Register();
		supporterNicknameService.NicknameChanged += publicPlayerListServer.InvalidateDisplaySnapshot;
		if (features.WebMap)
		{
			webMapDirtyJournal = new WebMapDirtyChunkJournal(api);
		}
		if (features.BlockLogger)
		{
			storage = new LogStorageService(api, config.MaxQueuedEvents, config.EmergencyOverflowCapacity, config.FlushIntervalMs, config.AutoMigrateLegacyLogs, config.AutoDatabaseBackupIntervalHours, config.DatabaseBackupRetentionCount);
			queries = new LogQueryService(storage);
			ContainerSnapshotQueryService snapshotQueryService = new ContainerSnapshotQueryService(storage);
			IngameTimeFormatter timeFormatter = new IngameTimeFormatter(api);
			Action<int, int, int> action = null;
			if (webMapDirtyJournal != null)
			{
				action = (int dimension, int blockX, int blockZ) =>
				{
					WebMapBridgeService? webMapBridgeService = webMapService;
					if (webMapBridgeService != null && webMapBridgeService.IsEnabled)
					{
						webMapDirtyJournal.MarkBlockDirty(dimension, blockX, blockZ);
					}
				};
			}
			blockHooks = new BlockHooks(api, config, storage, timeFormatter, action);
			containerHooks = new ContainerHooks(api, config, storage, storage, timeFormatter);
			entityHooks = new EntityHooks(api, config, storage, timeFormatter);
			blockHooks.Register();
			containerHooks.Register();
			entityHooks.Register();
			new BlockLoggerCommands(api, () => config, reloadConfig, storage, queries, snapshotQueryService).Register();
			rollbackCommands = new PlayerRollbackCommands(api, storage, action);
			rollbackCommands.Register();
			pruneListenerId = api.Event.RegisterGameTickListener((float _) =>
			{
				QueueRetention();
			}, 3600000);
			api.Logger.Notification("[BlockLogger] Started. SQLite storage enabled. Pending={0}, RetentionDays={1}", storage.PendingEvents, config.RetentionDays);
		}
		else
		{
			api.Logger.Notification("[BlockLogger] Entire subsystem disabled by {0}; no hooks, storage worker, query worker, commands, or retention timer registered.", "NewfiesServerEssentialsFeatures.json");
		}
		if (features.PerformanceDiagnostics)
		{
			serverHealth = new ServerHealthService(api, () => storage);
			serverHealth.Start(config.ProfilerEnabled);
		}
		if (features.PlayerAnalytics)
		{
			playerAnalytics = new PlayerCountAnalyticsService(api);
			playerAnalytics.Start();
		}
		if (features.Reports)
		{
			reportsService = new ReportsService(api);
			reportsService.Start();
		}
		if (features.Announcements)
		{
			announcementService = new AnnouncementService(api);
			announcementService.Start();
		}
		if (features.WebConsole && features.WebDashboard)
		{
			webConsoleService = new WebConsoleService(api, (IServerPlayer player) => supporterNicknameService?.ResolveDisplayName(player.PlayerUID, player.PlayerName) ?? player.PlayerName);
			webConsoleService.Start();
		}
		else if (features.WebConsole && !features.WebDashboard)
		{
			api.Logger.Notification("[ServerEssentials/WebConsole] not started because Web Dashboard is master-disabled.");
		}
		if (features.ModUpdateChecker && features.WebDashboard)
		{
			modUpdateChecker = new ModUpdateCheckerService(api, "1.22.7");
			modUpdateChecker.Start();
		}
		else if (features.ModUpdateChecker && !features.WebDashboard)
		{
			api.Logger.Notification("[ServerEssentials/ModUpdates] not started because Web Dashboard is master-disabled.");
		}
		if (features.PlayerCommands)
		{
			playerCommandsService = new PlayerCommandsService(api, () => adminService);
			playerCommandsService.Start();
		}
		else
		{
			api.Logger.Notification("[ServerEssentials/PlayerCommands] Player commands disabled by {0}.", "NewfiesServerEssentialsFeatures.json");
		}
		automatedRestartService = new AutomatedRestartService(api);
		automatedRestartService.Start();
		chatDisplayService = new ChatDisplayService(api, (IPlayerRole? role) => publicPlayerListServer?.ResolveConfiguredRoleNameColor(role) ?? string.Empty, (string uid, string realName) => supporterNicknameService?.ResolveDisplayName(uid, realName) ?? realName);
		chatDisplayService.Start();
		webConsoleService?.StartChatCapture();
		supporterLampService = new SupporterLampService(api);
		supporterLampService.Start();
		if (features.Administration)
		{
			adminService = new AdminService(api, () => serverHealth, () => queries, reportsService, playerCommandsService, () => automatedRestartService);
			adminService.Start();
		}
		if (features.AntiCheat)
		{
			antiCheatService = new AntiCheatService(api, () => adminService);
			antiCheatService.Start();
		}
		if (features.AntiCheat && !features.Administration)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Administration is master-disabled. Client scanning/reporting still runs, but moderation consequences that require AdminService cannot be applied.");
		}
		if (features.Reports && !features.Administration)
		{
			api.Logger.Notification("[ServerEssentials/Reports] Administration is master-disabled. Saved report data/web review can remain available, but player /report and staff report commands are not registered.");
		}
		customPatchService = new CustomPatchService(api);
		customPatchService.Start();
		if (features.WebDashboard)
		{
			webDashboard = new WebDashboardService(api, this);
			webDashboard.Start();
		}
		if (features.WebMap)
		{
			webMapService = new WebMapBridgeService(api, () => queries, () => webDashboard?.GetBadBlockEntitySnapshotForMap(), () => webDashboard?.Config.Port ?? 0, () => adminService?.GetOnlinePlayerSummaries(), () => adminService?.GetPlayers(), webMapDirtyJournal);
			webMapService.Start();
			new WebMapCommands(api, webMapService).Register();
		}
	}

	public void ApplyRuntimeSettings()
	{
		storage?.UpdateRuntimeSettings(config.MaxQueuedEvents, config.EmergencyOverflowCapacity, config.FlushIntervalMs, config.AutoDatabaseBackupIntervalHours, config.DatabaseBackupRetentionCount);
		containerHooks?.ApplyRuntimeSettings();
		serverHealth?.SetEnabled(config.ProfilerEnabled);
	}

	public void SaveRuntimeConfig()
	{
		config.Clamp();
		SuiteConfigStorage.Store(api, config, "NewfiesBlocklogger.json");
		ApplyRuntimeSettings();
	}

	private void QueueRetention()
	{
		if (storage != null && config.Enabled && config.RetentionDays > 0)
		{
			storage.PruneAsync(config.RetentionDays);
		}
	}

	public void Dispose()
	{
		if (started)
		{
			started = false;
			if (supporterNicknameService != null && publicPlayerListServer != null)
			{
				supporterNicknameService.NicknameChanged -= publicPlayerListServer.InvalidateDisplaySnapshot;
			}
			publicPlayerListServer?.Dispose();
			publicPlayerListServer = null;
			if (pruneListenerId >= 0)
			{
				api.Event.UnregisterGameTickListener(pruneListenerId);
				pruneListenerId = -1L;
			}
			containerHooks?.Unregister();
			blockHooks?.Unregister();
			entityHooks?.Unregister();
			containerHooks = null;
			blockHooks = null;
			entityHooks = null;
			webMapService?.Dispose();
			webMapService = null;
			webMapDirtyJournal?.Dispose();
			webMapDirtyJournal = null;
			webDashboard?.Dispose();
			webDashboard = null;
			customPatchService?.Dispose();
			customPatchService = null;
			antiCheatService?.Dispose();
			antiCheatService = null;
			supporterLampService?.Dispose();
			supporterLampService = null;
			supporterNicknameService?.Dispose();
			supporterNicknameService = null;
			chatDisplayService?.Dispose();
			chatDisplayService = null;
			automatedRestartService?.Dispose();
			automatedRestartService = null;
			adminService?.Dispose();
			playerCommandsService?.Dispose();
			adminService = null;
			playerCommandsService = null;
			modUpdateChecker?.Dispose();
			modUpdateChecker = null;
			webConsoleService?.Dispose();
			webConsoleService = null;
			announcementService?.Dispose();
			announcementService = null;
			reportsService?.Dispose();
			reportsService = null;
			playerAnalytics?.Dispose();
			playerAnalytics = null;
			serverHealth?.Dispose();
			serverHealth = null;
			rollbackCommands?.Dispose();
			storage?.Dispose();
			storage = null;
			queries = null;
		}
	}
}
