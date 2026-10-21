using BlockLogger.Commands;
using BlockLogger.Admin;
using BlockLogger.Analytics;
using BlockLogger.Announcements;
using BlockLogger.WebConsole;
using BlockLogger.ModUpdates;
using BlockLogger.PlayerList;
using BlockLogger.PlayerCommands;
using BlockLogger.AntiCheat;
using BlockLogger.Config;
using BlockLogger.Hooks;
using BlockLogger.Services;
using BlockLogger.ServerHealth;
using BlockLogger.Reports;
using BlockLogger.Restarts;
using BlockLogger.WebDashboard;
using BlockLogger.WebMap;
using Vintagestory.API.Server;

namespace BlockLogger;

/// <summary>
/// Server-side BlockLogger subsystem. The host owns configuration persistence;
/// this class owns logging hooks, storage, commands, retention and shutdown.
/// </summary>
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
    private WorldRepair.BlockIdRepairService? blockRepair;
    private long pruneListenerId = -1;
    private bool started;

    public BlockLoggerModule(
        ICoreServerAPI api,
        BlockLoggerConfig config,
        SuiteFeatureConfig features,
        SuiteFeatureConfig featureSettings,
        Action reloadConfig,
        WorldRepair.BlockIdRepairService? blockRepairService = null)
    {
        this.api = api;
        this.config = config;
        this.features = features;
        this.featureSettings = featureSettings;
        this.reloadConfig = reloadConfig;
        blockRepair = blockRepairService;
    }

    public WorldRepair.BlockIdRepairService? BlockRepair => blockRepair;
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
    /// <summary>Feature gates applied for this running process. Immutable for the lifetime of the module.</summary>
    public SuiteFeatureConfig Features => features;
    /// <summary>Persisted feature-gate settings that will be applied on the next server restart.</summary>
    public SuiteFeatureConfig FeatureSettings => featureSettings;


    public string StorageStartupError {get;private set;}="";

    public void Start()
    {
        if (started) return;
        started = true;

        // Supporter nicknames are presentation-only and never replace the authoritative
        // Vintage Story player/account name used by permissions, moderation or logs.
        blockRepair ??= new WorldRepair.BlockIdRepairService(api);
        blockRepair.ConfigureHistoryDatabase(Path.Combine(SuiteDataPaths.GetWorldRoot(api), "blocklogger.db"));
        supporterNicknameService = new SupporterNicknameService(api);
        supporterNicknameService.Start();

        // Always provide the tiny read-only backend used by the separate client companion.
        // This does not require or load the full suite on connecting clients.
        publicPlayerListServer = new PublicPlayerListServer(
            api,
            () => adminService,
            (uid, realName) => supporterNicknameService?.ResolveDisplayName(uid, realName) ?? realName);
        publicPlayerListServer.Register();
        supporterNicknameService.NicknameChanged += publicPlayerListServer.InvalidateDisplaySnapshot;

        if (features.WebMap)
        {
            webMapDirtyJournal = new WebMapDirtyChunkJournal(api);
        }

        if (features.BlockLogger)
        {
            try
            {
                storage = new LogStorageService(
                    api,
                    config.MaxQueuedEvents,
                    config.EmergencyOverflowCapacity,
                    config.FlushIntervalMs,
                    config.AutoMigrateLegacyLogs,
                    config.AutoDatabaseBackupIntervalHours,
                    config.DatabaseBackupRetentionCount,
                    config.FullDatabaseIntegrityCheckOnStartup);
            }
            catch (Exception error)
            {
                StorageStartupError = error.GetType().Name + ": " + error.Message;
                api.Logger.Error("[BlockLogger/Startup] Storage unavailable for this session; database files were not automatically moved or replaced. Other Suite services continue. {0}",StorageStartupError);
            }
            if (storage != null)
            {

                queries = new LogQueryService(storage);
                var snapshots = new ContainerSnapshotQueryService(storage);
                var time = new IngameTimeFormatter(api);

                Action<int, int, int>? terrainDirtyMarker = null;
                if (webMapDirtyJournal != null)
                {
                    // The Web Map master feature is available by default, but the actual map
                    // runtime defaults OFF. Do not journal terrain changes until an administrator
                    // explicitly enables the map service.
                    terrainDirtyMarker = (dimension, blockX, blockZ) =>
                    {
                        if (webMapService?.IsEnabled == true)
                            webMapDirtyJournal.MarkBlockDirty(dimension, blockX, blockZ);
                    };
                }

                blockHooks = new BlockHooks(api, config, storage, time, terrainDirtyMarker);
                containerHooks = new ContainerHooks(api, config, storage, storage, time);
                entityHooks = new EntityHooks(api, config, storage, time);

                blockHooks.Register();
                containerHooks.Register();
                entityHooks.Register();

                new BlockLoggerCommands(api, () => config, reloadConfig, storage, queries, snapshots).Register();
                rollbackCommands = new PlayerRollbackCommands(api, storage, terrainDirtyMarker);
                rollbackCommands.Register();
                pruneListenerId = api.Event.RegisterGameTickListener(_ => QueueRetention(), 3_600_000);

                api.Logger.Notification(
                    "[BlockLogger] Started. SQLite storage enabled. Pending={0}, RetentionDays={1}",
                    storage.PendingEvents,
                    config.RetentionDays);
            }
        }
        else
        {
            api.Logger.Notification("[BlockLogger] Entire subsystem disabled by {0}; no hooks, storage worker, query worker, commands, or retention timer registered.", SuiteFeatureConfig.ConfigFile);
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
            webConsoleService = new WebConsoleService(api,player=>supporterNicknameService?.ResolveDisplayName(player.PlayerUID,player.PlayerName)??player.PlayerName);
            webConsoleService.Start();
        }
        else if (features.WebConsole && !features.WebDashboard)
        {
            api.Logger.Notification("[ServerEssentials/WebConsole] not started because Web Dashboard is master-disabled.");
        }

        if (features.ModUpdateChecker && features.WebDashboard)
        {
            modUpdateChecker = new ModUpdateCheckerService(api, WebDashboardService.TargetGameVersion);
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
            api.Logger.Notification("[ServerEssentials/PlayerCommands] Player commands disabled by {0}.", SuiteFeatureConfig.ConfigFile);
        }

        // The automated restart scheduler is intentionally lightweight and has its own
        // on/off setting, matching Th3Essentials' shutdown-system model. Keep the service
        // available even when administration is master-disabled so scheduled restarts can
        // still operate and /restarttime remains usable.
        automatedRestartService = new AutomatedRestartService(api);
        automatedRestartService.Start();

        // Chat display is always available. Fresh configs bold names by default while role
        // prefixes and timestamps remain off. Supporter nicknames affect only this Suite-owned
        // presentation layer; authoritative account names remain unchanged everywhere else.
        chatDisplayService = new ChatDisplayService(
            api,
            role => publicPlayerListServer?.ResolveConfiguredRoleNameColor(role) ?? string.Empty,
            (uid, realName) => supporterNicknameService?.ResolveDisplayName(uid, realName) ?? realName);
        chatDisplayService.Start();
        webConsoleService?.StartChatCapture();

        // Lightweight privilege-gated supporter perk. Rendering is handled by the optional
        // universal Player List companion, so the Suite itself remains server-only.
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
            api.Logger.Warning("[ServerEssentials/AntiCheat] Administration is master-disabled. Client scanning/reporting still runs, but moderation consequences that require AdminService cannot be applied.");

        if (features.Reports && !features.Administration)
            api.Logger.Notification("[ServerEssentials/Reports] Administration is master-disabled. Saved report data/web review can remain available, but player /report and staff report commands are not registered.");

        // Patch Studio is server-owned and persists independently from the WebPanel. Enabled
        // declarative patches are restored at startup; the WebPanel is only their control surface.
        customPatchService = new CustomPatchService(api);
        customPatchService.Start();

        if (features.WebDashboard)
        {
            webDashboard = new WebDashboardService(api, this);
            webDashboard.Start();
        }

        if (features.WebMap)
        {
            webMapService = new WebMapBridgeService(
                api,
                () => queries,
                () => webDashboard?.GetBadBlockEntitySnapshotForMap(),
                () => webDashboard?.Config.Port ?? 0,
                () => adminService?.GetOnlinePlayerSummaries(),
                () => adminService?.GetPlayers(),
                webMapDirtyJournal);
            webMapService.Start();
            new WebMapCommands(api, webMapService).Register();
        }
    }

    public void ApplyRuntimeSettings()
    {
        storage?.UpdateRuntimeSettings(
            config.MaxQueuedEvents,
            config.EmergencyOverflowCapacity,
            config.FlushIntervalMs,
            config.AutoDatabaseBackupIntervalHours,
            config.DatabaseBackupRetentionCount);
        containerHooks?.ApplyRuntimeSettings();
        serverHealth?.SetEnabled(config.ProfilerEnabled);
    }

    public void SaveRuntimeConfig()
    {
        config.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, config, "NewfiesBlocklogger.json");
        ApplyRuntimeSettings();
    }

    private void QueueRetention()
    {
        if (storage == null || !config.Enabled || config.RetentionDays <= 0) return;
        _ = storage.PruneAsync(config.RetentionDays);
    }

    public void Dispose()
    {
        if (!started) return;
        started = false;
        if (supporterNicknameService != null && publicPlayerListServer != null)
            supporterNicknameService.NicknameChanged -= publicPlayerListServer.InvalidateDisplaySnapshot;
        blockRepair?.Dispose();
        publicPlayerListServer?.Dispose();
        publicPlayerListServer = null;

        if (pruneListenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(pruneListenerId);
            pruneListenerId = -1;
        }

        // Stop gameplay producers before shutting down their consumers/storage. This prevents
        // new audit rows from arriving while the web/dashboard/storage layers are draining.
        containerHooks?.Unregister();
        blockHooks?.Unregister();
        entityHooks?.Unregister();
        containerHooks = null;
        blockHooks = null;
        entityHooks = null;

        webMapService?.Dispose();
        webMapService = null;

        // Dispose flushes the in-memory dirty set/journal before storage is released.
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
