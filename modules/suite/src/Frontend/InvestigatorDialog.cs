using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BlockLogger.Protocol;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockLogger.Frontend;

public sealed partial class InvestigatorDialog : GuiDialog
{
    private const double ResultsX = 34;
    private const double ResultsY = 250;
    private const double ResultsWidth = 780;
    private const double ResultsHeight = 385;
    private const double ScrollbarX = 822;
    private bool disposed;
    private readonly Action<InvestigatorQueryRequest> sendQuery;
    private readonly Action<InvestigatorStatusRequest> sendStatus;
    private readonly Action<BlockLoggerSettingsRequest> sendBlockLoggerSettings;
    private readonly Action<AnimalLimiterRequest> sendAnimalLimiter;
    private readonly Action<ProfilerRequest> sendProfiler;
    private readonly Action<LoginProtectionRequest> sendLoginProtection;
    private readonly Action<WebDashboardRequest> sendWebDashboard;
    private readonly Action<AntiCheatGuiRequest> sendAntiCheat;
    private readonly Action<AdminCommandGuiRequest> sendAdminCommand;
    private readonly Action<DiscordIntegrationGuiRequest> sendDiscordIntegration;

    private string section = "home";
    private string view = "blocks";
    private string blockLoggerView = "blocks";
    private string blockSettingsView = "events";
    private string animalView = "limiter";
    private string loginView = "status";
    private string webView = "webpanel";
    private string discordView = "overview";
    private string motdView = "general";
    private string antiCheatView = "overview";
    private string antiCheatPacketView = "general";
    private string adminCommandView = "players";
    private string adminServerView = "operations";
    private string adminCommandTarget = "";
    private AdminPlayerChoices adminPlayerChoices = AdminPlayerChoices.Empty;
    private string adminCommandDuration = "30m";
    private string adminCommandReason = "";
    private string adminCommandInventoryId = "backpack";
    private string adminCommandItemCode = "";
    private string adminCommandItemSearch = "";
    private string adminCommandInventoryFingerprint = "";
    private int adminCommandQuantity = 1;
    private int adminCommandSlot;
    private long adminCommandRecordId;
    private int adminCommandRadius = 3;
    private string adminAutoRestartTimes = "00:00:00";
    private string adminAutoRestartWarnings = "1,2,3,4,5,10,20,30";
    private string adminAutoRestartReason = "Scheduled server restart";
    private bool adminAutoRestartGameAnnouncements = true;
    private bool adminAutoRestartDiscordAnnouncements = true;
    private bool adminChatShowRole;
    private bool adminChatBoldName;
    private bool adminChatShowTimestamp;
    private bool adminChatUseUtc;
    private bool adminChatFormatGeneral = true;
    private bool adminChatFormatOtherGroups;
    private string adminChatAdditionalGroupIds = "";
    private string adminChatTimestampFormat = "HH:mm";
    private string adminCommandReportValue = "Investigating";
    private double adminInventoryScrollValue;
    private double adminInventoryViewportHeight;
    private double adminInventoryContentHeight;
    private readonly Dictionary<string, double> adminInventoryElementBaseY = new(StringComparer.Ordinal);
    private string pendingAdminConfirmOperation = "";
    private DateTime pendingAdminConfirmUntilUtc = DateTime.MinValue;
    private string antiCheatPlayerSearch = "";
    private string antiCheatReportSearch = "";
    private string antiCheatBlacklistModId = "";
    private string antiCheatBlacklistSourcePlayer = "";
    private bool antiCheatEnabled = true;
    private bool applyingAntiCheatControls;
    private readonly Dictionary<string, string> antiCheatPlayerSummary = new(StringComparer.OrdinalIgnoreCase);

    // Save-gated in-game enforcement-rule editor. Rules are copied from the server,
    // edited locally, then saved as one validated set so a half-edited policy can never
    // become active accidentally.
    private readonly List<AntiCheatConsequenceRuleGuiPacket> antiCheatEnforcementRules = new();
    private int antiCheatEnforcementSelectedIndex = -1;
    private string acRuleName = "";
    private bool acRuleEnabled = true;
    private string acRuleTriggerType = "FindingCode";
    private string acRuleTriggerValue = "FORBIDDEN_MOD";
    private string acRuleSubject = "";
    private string acRuleAction = "Log";
    private string acRuleDuration = "30m";
    private string acRuleMessage = "Anti-cheat policy matched: {code} {subject}.";
    private bool acRuleApplyCurrent;
    private bool applyingAntiCheatEnforcementControls;

    // Save-gated Anti-Cheat settings mirrored from the server. Editing controls only
    // changes these local values until Save Settings is pressed.
    private bool acRequireClient = true;
    private int acRescanMinutes = 15;
    private bool acRuntimeRescanOnChange = true;
    private int acRuntimeCooldownSeconds = 30;
    private bool acSimilarityDetection = true;
    private int acSimilarityReviewPercent = 72;
    private int acSimilarityHighPercent = 88;
    private bool acPacketGuardEnabled = true;
    private bool acPacketIngressEnabled;
    private bool acPacketIngressActive;
    private string acPacketIngressStatus = "Unknown";
    private bool acPacketWarningsEnabled = false;
    private int acPacketWarningCooldownSeconds = 15;
    private int acPacketAccelerationSampleMinMs = 220;
    private double acPacketAccelerationSmoothing = 0.25;
    private bool acPacketNoclipEnabled = false;
    private int acPacketNoclipSamples = 3;
    private double acPacketNoclipPenetration = 0.18;
    private double acPacketNoclipEndpointMovement = 0.45;
    private double acPacketMountAccelerationRatio = 0.85;
    private bool acPacketDiagnosticContext = true;
    private bool acPacketDiagnosticSuppression = true;
    private int acPacketDiagnosticSpeedHistory = 8;
    private int acPacketMovementSampleMs = 75;
    private int acPacketMovementConfirmSamples = 2;
    private int acPacketMovementConfirmWindowMs = 600;
    private double acPacketMovementExtremeMultiplier = 2.75;
    private int acPacketCorrectionGraceMs = 1200;
    private bool acPacketExemptAdmins = true;
    private bool acPacketExemptMountedPlayers = true;
    private int acPacketMountGraceMs = 2000;
    private int acPacketEngineTeleportGraceMs = 5000;
    private bool acPacketTPRunesCompatibility = true;
    private int acPacketTPRunesGraceMs = 8000;
    private bool acPacketTPRunesDetected;
    private bool acPacketReachLatencyCompensation = true;
    private double acPacketReachMaxLatencyCompensation = 1.25;
    private bool acPacketReachPositionHistoryEnabled = true;
    private int acPacketReachPositionHistoryMs = 650;
    private int acPacketReachMaxRewindMs = 350;
    private double acPacketReachUseExtraTolerance = 0.20;
    private double acPacketReachBreakExtraTolerance = 0.00;
    private int acPacketReachNearThresholdStrikes = 3;
    private int acPacketReachStrikeWindowMs = 3500;
    private int acPacketReachStrikeDecayMs = 1800;
    private int acPacketVeinWindowMs = 2200;
    private int acPacketConnectedBreakLimit = 18;
    private int acPacketMiningDuplicateTargetMs = 450;
    private double acPacketMiningCadenceMinExpectedMs = 250;
    private int acPacketMiningCadenceConfirmationWindowMs = 8000;
    private bool acPerFamilyRiskEnabled = true;
    private int acMovementRiskDecaySeconds = 180;
    private int acMovementRiskReviewThreshold = 25;
    private int acMovementRiskSuspiciousThreshold = 50;
    private int acReachRiskDecaySeconds = 300;
    private int acReachRiskReviewThreshold = 25;
    private int acReachRiskSuspiciousThreshold = 50;
    private int acMiningRiskDecaySeconds = 300;
    private int acMiningRiskReviewThreshold = 25;
    private int acMiningRiskSuspiciousThreshold = 50;
    private int acPacketAbuseRiskDecaySeconds = 600;
    private int acPacketAbuseRiskReviewThreshold = 25;
    private int acPacketAbuseRiskSuspiciousThreshold = 50;
    private int acClientIntegrityRiskDecaySeconds = 1800;
    private int acClientIntegrityRiskReviewThreshold = 10;
    private int acClientIntegrityRiskSuspiciousThreshold = 50;
    private bool acPacketTpsReady;
    private double acPacketTpsTarget;
    private double acPacketTpsEstimated;
    private double acPacketTpsHealthPercent;
    private string acPacketTpsBand = "Disabled";
    private bool acMovementEnabled = true;
    private double acHardMoveDistance = 32;
    private bool acRejectUnauthorizedMovement = true;
    private bool acTh3EssentialsSpawnCompatibility = true;
    private int acTh3EssentialsSpawnGraceMs = 8000;
    private bool acTh3EssentialsDetected;
    private bool acReachEnabled = true;
    private double acReachTolerance = 1.25;
    private double acReachHardExcess = 1.5;
    private bool acRejectOutOfReach = true;
    private bool acMiningEnabled = true;
    private bool acMiningCadenceEnabled = false;
    private double acMiningCadenceRatio = 0.38;
    private int acMiningCadenceConfirmations = 3;
    private bool acRejectConnectedBursts = true;
    private bool acRejectImpossibleCadence;
    private bool acTpsAwarenessEnabled = true;
    private double acTpsDegradedPercent = 85;
    private double acTpsCriticalPercent = 60;
    private double acTpsMaxMultiplier = 2.5;
    private bool acTpsSuppressCriticalRejects = true;
    private bool acIncidentCorrelationEnabled = true;
    private int acIncidentWindowSeconds = 90;
    private int acIncidentRequiredCategories = 2;
    private int acIncidentMaxRiskBonus = 20;
    private bool acHistoryEnabled = true;
    private int acHistoryRetentionDays = 90;
    private int acHistoryMaxRecords = 500;
    private bool useSelection = true;
    private int radius = 5;
    private int limit = 20;
    private string search = "";
    private string playerSearch = "";
    private string action = "any";

    // BlockLogger settings mirrored from the server.
    private bool blockLoggerEnabled = true;
    private bool blockLogBreak = true;
    private bool blockLogPlace = true;
    private bool blockLogUse = true;
    private bool blockLogContainerAccess = true;
    private bool blockLogContainerItems = true;
    private bool blockLogEntityInteract = true;
    private bool blockLogEntitySpawn = true;
    private bool blockLogEntityDeath = true;
    private bool blockLogEntityDespawn = true;
    private bool blockAggregateSpawnDespawn = false;
    private int blockRetentionDays = 180;
    private int blockFlushIntervalMs = 1000;
    private int blockMaxQueuedEvents = 5000;
    private int blockEmergencyOverflowCapacity = 20000;
    private int blockDefaultRadius = 5;
    private int blockEntityRadius = 3;
    private int blockMaxRadius = 64;
    private int blockDefaultRows = 10;
    private int blockDefaultEntityRows = 10;
    private int blockMaxDisplayCount = 100;
    private bool applyingBlockLoggerControls;

    private string animalPattern = "";
    private int animalLimit = 20;
    private bool animalEnabled;
    private int animalRadiusChunks = 3;          // global/default fallback radius
    private int animalRuleRadiusChunks = 3;      // radius used when setting/previewing a rule
    private int animalDefaultLimit = 20;
    private int animalPlayerProtectionRadius = 24;
    private int animalReconciliationSeconds = 60;
    private bool animalLimitAllEntities;
    private bool animalProtectPets = true;
    private bool animalProtectFarmAnimals = true;
    private bool animalProtectNamedEntities = true;
    private bool animalKeepNearestToPlayers = true;
    private bool animalLogActions = true;
    private bool animalLogToCsv = true;
    private bool applyingAnimalControls;

    // Login Protection settings mirrored from the server.
    private bool loginEnabled = true;
    private int loginProtectionSeconds = 60;
    private bool loginProtectOnJoin = true;
    private bool loginProtectOnRespawn = true;
    private bool loginAnnounceOnJoin = true;
    private bool loginAnnounceOnEnd = true;
    private bool loginBlockPvP = true;
    private bool loginCancelOnAttack = true;
    private bool loginProtectFallDamage = true;
    private bool loginExtinguishFire = true;
    private bool loginRecoverSwimmingOnLogin = true;
    private int loginActiveProtectedCount;
    private bool applyingLoginControls;

    // Web Server settings mirrored from the server.
    private bool webEnabled;
    private bool webRunning;
    private string webBindAddress = "127.0.0.1";
    private int webPort = 9465;
    private string webLocalUrl = "http://127.0.0.1:9465/";
    private string webPublicUrl = "";
    private string webAdminUsername = "admin";
    private bool webPasswordConfigured;
    private int webActiveSessions;
    private int webSessionTimeoutMinutes = 60;
    private bool webSecureCookies;
    private bool webLocalAnonymousMode;
    private string webLastError = "";
    private bool applyingWebControls;

    // Web Map settings mirrored from NewfiesWebMap.json. Unlike the WebPanel listener
    // controls, the map page is save-gated so several related privacy/export values
    // can be reviewed before they are applied together.
    private bool webMapEnabled;
    private bool webMapRunning;
    private string webMapBindAddress = "0.0.0.0";
    private int webMapPort = 9466;
    private string webMapTitle = "Newfie's Server Map";
    private string webMapPublicUrl = "";
    private string webMapDashboardUrl = "";
    private bool webMapShowPlayers;
    private bool webMapShowWaypoints = true;
    private bool webMapShowActivity;
    private bool webMapShowBadBlockEntities;
    private int webMapPlayerMajorWaypointLimit = 5;
    private int webMapPlayerMinorWaypointLimit = 10;
    private int webMapMaximumZoomOutSpanBlocks = 50000;
    private int webMapDefaultViewSpanBlocks = 8000;
    private int webMapTerrainTileSize = 256;
    private int webMapActivityWindowMinutes = 60;
    private int webMapMaxActivityMarkers = 150;
    private bool webMapExportTerrainOnStart;
    private bool webMapAutomaticStartupExportEnabled;
    private int webMapAutomaticStartupExportIntervalDays = 7;
    private bool webMapUseIncrementalTerrainExport = true;
    private bool webMapLockServerDuringTerrainExport = true;
    private bool webMapTerrainExportInProgress;
    private string webMapLastError = "";
    private bool applyingWebMapControls;

    // Discord settings are owned by the unified server-side Discord module.
    // Shift+L only mirrors their event-bus snapshot and never receives the bot token itself.
    private DiscordIntegrationGuiSettingsPacket discordSettings = new();
    private bool discordAvailable;
    private bool applyingDiscordControls;
    private string discordStatusMessage = "Discord integration status has not been loaded yet.";

    // Community MOTD is edited here through the Suite's existing event-bus companion bridge.
    // No direct reference to the MOTD assembly is required.
    private bool motdAvailable;
    private string motdVersion = "";
    private string motdStatusMessage = "Community MOTD status has not been loaded yet.";
    private JObject motdConfig = new();
    private bool applyingMotdControls;

    private DateTime cullConfirmUntilUtc = DateTime.MinValue;
    private long requestId;
    private long lastResponseId;
    private long lastDiscordResponseId;
    private string pageHeaderText = SuiteLocalization.Get("gui.runtime.readyblockhistory");
    private string resultText = SuiteLocalization.Get("gui.runtime.noresults");

    // Generic investigator result panes use explicit top-anchored paged rows instead
    // of a single scrollable Richtext element. Vintage Story can vertically offset a
    // tall Richtext child even when the scrollbar itself is at zero.
    private readonly List<string> genericResultLines = new();
    private double genericScrollValue;
    private double genericResultsY;
    private double genericResultsHeight;
    private const double GenericResultRowHeight = 30;
    private const double AntiCheatRowActionWidth = 118;

    private double CurrentGenericResultRowHeight => section == "admincommands"
        ? (adminCommandView == "inventory" ? 58 : 42)
        : section == "anticheat"
            ? antiCheatView switch
            {
                "reports" => 168,
                "player" => 168,
                "enforcement" => 138,
                "players" => 60,
                "blacklist" => 64,
                "settings" => 40,
                _ => GenericResultRowHeight
            }
            : GenericResultRowHeight;

    // Server profiler/diagnostics state. The server keeps the rolling history;
    // the client only receives a downsampled snapshot when this tab is refreshed.
    private readonly List<ProfilerSamplePacket> profilerSamples = new();
    private readonly List<ProfilerSlowSpikePacket> profilerSlowSpikes = new();
    private readonly List<ProfilerHotspotPacket> profilerHotspots = new();
    private readonly List<ProfilerModHotspotPacket> profilerModHotspots = new();
    private readonly List<ProfilerPhasePacket> profilerPhases = new();
    private readonly List<ProfilerProfileSlowTickPacket> profilerProfileSlowTicks = new();
    private readonly List<ProfilerOffThreadReportPacket> profilerOffThreadReports = new();
    private readonly List<ProfilerDiagnosticIncidentPacket> profilerDiagnosticIncidents = new();
    private ProfilerDiagnosticSummaryPacket profilerDiagnosticSummary = new();
    private string profilerView = "overview";
    private int profilerLoadedModCount;

    // Live updates are client-side polling only. No extra server sampler is
    // created; this just refreshes the existing snapshot while the Profiler page
    // is open.
    private const int ProfilerLiveRefreshMs = 2000;
    private bool profilerLiveUpdates;
    private bool profilerLiveRequestPending;
    private int profilerLiveGeneration;
    private ProfilerSamplePacket profilerCurrent = new();
    private ProfilerProfileSummaryPacket profilerLastProfile = new();
    private bool profilerEnabled;
    private bool applyingProfilerControls;
    private bool profilerRunning;
    private int profilerRemainingSeconds;
    private string profilerMessage = SuiteLocalization.Get("gui.runtime.profilerprompt");

    private readonly List<string> removalLogLines = new();
    private int removalLogFirstIndex;
    private const int RemovalLogVisibleRows = 9;
    private const double RemovalLogRowHeight = 50;

    private readonly List<PopulationDisplayRow> populationRows = new();
    private readonly Dictionary<string, PopulationMobTotal> populationMobTotals = new(StringComparer.OrdinalIgnoreCase);
    private int populationFirstIndex;
    private string populationMode = "";
    private const int PopulationVisibleRows = 16;
    private const double PopulationRowHeight = 27;

    private sealed record PopulationDisplayRow(
        string MobId,
        string Region,
        string Rule,
        int Tracked,
        int Limit,
        int Radius,
        int Protected,
        int Eligible);

    private sealed record PopulationMobTotal(
        int Tracked,
        int Protected,
        int Eligible,
        int Regions,
        int OverRegions);

    private readonly List<AnimalRuleDisplayRow> animalRuleRows = new();
    private int animalRulePage;
    private int animalRuleCount;
    private int animalExclusionCount;
    private const int AnimalRulesPerPage = 18;

    private sealed record AnimalRuleDisplayRow(string Pattern, string Limit, string Radius, bool IsExclusion);

    public InvestigatorDialog(
        ICoreClientAPI api,
        Action<InvestigatorQueryRequest> sendQuery,
        Action<InvestigatorStatusRequest> sendStatus,
        Action<BlockLoggerSettingsRequest> sendBlockLoggerSettings,
        Action<AnimalLimiterRequest> sendAnimalLimiter,
        Action<ProfilerRequest> sendProfiler,
        Action<LoginProtectionRequest> sendLoginProtection,
        Action<WebDashboardRequest> sendWebDashboard,
        Action<AntiCheatGuiRequest> sendAntiCheat,
        Action<AdminCommandGuiRequest> sendAdminCommand,
        Action<DiscordIntegrationGuiRequest> sendDiscordIntegration) : base(api)
    {
        this.sendQuery = sendQuery;
        this.sendStatus = sendStatus;
        this.sendBlockLoggerSettings = sendBlockLoggerSettings;
        this.sendAnimalLimiter = sendAnimalLimiter;
        this.sendProfiler = sendProfiler;
        this.sendLoginProtection = sendLoginProtection;
        this.sendWebDashboard = sendWebDashboard;
        this.sendAntiCheat = sendAntiCheat;
        this.sendAdminCommand = sendAdminCommand;
        this.sendDiscordIntegration = sendDiscordIntegration;
    }

    public override string ToggleKeyCombinationCode => "blocklogger-investigator";

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        ComposeDialog();
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        StopProfilerLiveUpdates();
        ClearComposers();
    }

    private void StopProfilerLiveUpdates()
    {
        profilerLiveUpdates = false;
        profilerLiveRequestPending = false;
        profilerLiveGeneration++;
    }

    public override bool TryOpen(bool withFocus) => !disposed && base.TryOpen(withFocus);

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopProfilerLiveUpdates();
        ClearComposers();
        capi.Gui.LoadedGuis.Remove(this);
        base.Dispose();
    }

    public void Show(InvestigatorQueryResponse response)
    {
        if (response.RequestId < lastResponseId) return;
        lastResponseId = response.RequestId;

        if (section != "blocklogger" ||
            !string.Equals(view, response.View, StringComparison.OrdinalIgnoreCase))
            return;

        pageHeaderText = response.Success ? Escape(response.Header) : "BlockLogger";
        SetGenericResults(response.Success ? response.Lines : new List<string> { response.Error });
        ComposeDialog();
    }

    public void Show(InvestigatorStatusResponse response)
    {
        if (response.RequestId < lastResponseId) return;
        lastResponseId = response.RequestId;

        if (section != "blocklogger" || view != "status") return;

        pageHeaderText = SuiteLocalization.Get("gui.runtime.blockloggerstatus");
        SetGenericResults(response.Success ? response.Lines : new List<string> { response.Error });
        ComposeDialog();
    }

    public void Show(BlockLoggerSettingsResponse response)
    {
        if (response.RequestId < lastResponseId) return;
        lastResponseId = response.RequestId;

        if (response.Success)
        {
            blockLoggerEnabled = response.Enabled;
            blockLogBreak = response.LogBlockBreak;
            blockLogPlace = response.LogBlockPlace;
            blockLogUse = response.LogBlockUse;
            blockLogContainerAccess = response.LogContainerAccess;
            blockLogContainerItems = response.LogContainerItemChanges;
            blockLogEntityInteract = response.LogEntityInteract;
            blockLogEntitySpawn = response.LogEntitySpawn;
            blockLogEntityDeath = response.LogEntityDeath;
            blockLogEntityDespawn = response.LogEntityDespawn;
            blockAggregateSpawnDespawn = response.AggregateEntitySpawnDespawn;
            blockRetentionDays = response.RetentionDays;
            blockFlushIntervalMs = response.FlushIntervalMs;
            blockMaxQueuedEvents = response.MaxQueuedEvents;
            blockEmergencyOverflowCapacity = response.EmergencyOverflowCapacity;
            blockDefaultRadius = response.DefaultRadius;
            blockEntityRadius = response.EntityRadius;
            blockMaxRadius = response.MaxRadius;
            blockDefaultRows = response.DefaultDisplayCount;
            blockDefaultEntityRows = response.DefaultEntityDisplayCount;
            blockMaxDisplayCount = response.MaxDisplayCount;
        }

        if (section != "blocklogger" || view != "settings") return;

        pageHeaderText = response.Success ? SuiteLocalization.Get("gui.runtime.blockloggersettings") : SuiteLocalization.Get("gui.runtime.blockloggersettingserror");
        ComposeDialog();
        ApplyBlockLoggerControlValues();
    }

    public void Show(AnimalLimiterResponse response)
    {
        if (response.RequestId < lastResponseId) return;
        lastResponseId = response.RequestId;

        if (response.Success)
        {
            animalEnabled = response.Enabled;
            animalRadiusChunks = response.RadiusChunks;
            if (string.IsNullOrWhiteSpace(animalPattern))
                animalRuleRadiusChunks = response.RadiusChunks;
            animalDefaultLimit = response.DefaultPerEntityLimit;
            animalPlayerProtectionRadius = response.PlayerProtectionRadiusBlocks;
            animalReconciliationSeconds = response.ReconciliationSeconds;
            animalLimitAllEntities = response.LimitAllEntities;
            animalProtectPets = response.ProtectPets;
            animalProtectFarmAnimals = response.ProtectFarmAnimals;
            animalProtectNamedEntities = response.ProtectNamedEntities;
            animalKeepNearestToPlayers = response.KeepNearestToPlayers;
            animalLogActions = response.LogActions;
            animalLogToCsv = response.LogToCsv;
        }

        pageHeaderText = response.Success ? Escape(response.Message) : "Animal Limiter";

        if (section == "animals" && animalView == "rules" && response.Success)
        {
            ParseAnimalRuleLines(response.Lines);
        }
        else if (section == "animals" && animalView == "population")
        {
            ParsePopulationLines(response.Success ? response.Lines : new List<string>());
        }
        else if (section == "animals" && animalView == "history")
        {
            SetRemovalLogResults(response.Success ? response.Lines : new List<string> { response.Message });
        }
        else
        {
            SetGenericResults(response.Success ? response.Lines : new List<string> { response.Message });
        }

        ComposeDialog();
        ApplyAnimalControlValues();
    }

    public void Show(WebDashboardResponse response)
    {
        if (response.RequestId < lastResponseId) return;
        lastResponseId = response.RequestId;

        webEnabled = response.Enabled;
        webRunning = response.Running;
        webBindAddress = string.IsNullOrWhiteSpace(response.BindAddress)
            ? "127.0.0.1"
            : response.BindAddress;
        webPort = response.Port > 0 ? response.Port : 9465;
        webLocalUrl = string.IsNullOrWhiteSpace(response.LocalUrl)
            ? $"http://127.0.0.1:{webPort}/"
            : response.LocalUrl;
        webPublicUrl = response.PublicUrl ?? "";
        webAdminUsername = string.IsNullOrWhiteSpace(response.AdminUsername)
            ? "admin"
            : response.AdminUsername;
        webPasswordConfigured = response.PasswordConfigured;
        webActiveSessions = response.ActiveSessions;
        webSessionTimeoutMinutes = response.SessionTimeoutMinutes > 0
            ? response.SessionTimeoutMinutes
            : 60;
        webSecureCookies = response.SecureCookies;
        webLocalAnonymousMode = response.LocalAnonymousMode;
        webLastError = response.LastError ?? "";

        if (response.WebMapSettings != null)
            CopyWebMapSettings(response.WebMapSettings);
        else if (webView == "webmap" && !response.Success)
            webMapLastError = response.Message ?? "Web Map is unavailable.";

        if (section != "webserver") return;

        pageHeaderText = webView == "webmap"
            ? (response.Success ? Escape(response.Message) : SuiteLocalization.Get("gui.runtime.webmapconfig"))
            : SuiteLocalization.Get("gui.runtime.webpanelconfig");

        ComposeDialog();
        ApplyWebControlValues();
        ApplyWebMapControlValues();
    }

    public void Show(DiscordIntegrationGuiResponse response)
    {
        if (response.RequestId < lastDiscordResponseId) return;
        lastDiscordResponseId = response.RequestId;

        discordSettings = response.Settings ?? new DiscordIntegrationGuiSettingsPacket();
        discordAvailable = discordSettings.TransportAvailable || discordSettings.LinkAvailable;
        discordStatusMessage = string.IsNullOrWhiteSpace(response.Message)
            ? (discordAvailable ? "Discord integration status loaded." : "The unified Discord module is not installed.")
            : response.Message;

        motdAvailable = discordSettings.MotdAvailable;
        motdVersion = discordSettings.MotdVersion ?? "";
        motdStatusMessage = string.IsNullOrWhiteSpace(discordSettings.MotdMessage)
            ? (motdAvailable ? "Community MOTD companion is connected." : "Community MOTD companion is not installed.")
            : discordSettings.MotdMessage;
        try
        {
            motdConfig = JObject.Parse(string.IsNullOrWhiteSpace(discordSettings.MotdConfigJson) ? "{}" : discordSettings.MotdConfigJson);
        }
        catch
        {
            motdConfig = new JObject();
            motdStatusMessage = "Community MOTD returned an invalid configuration payload.";
        }

        if (section != "webserver") return;

        if (webView == "discord")
        {
            pageHeaderText = "Discord Integration";
        }

        ComposeDialog();
        ApplyWebControlValues();
        ApplyWebMapControlValues();
        ApplyDiscordControlValues();
    }

    public void Show(AntiCheatGuiResponse response)
    {
        if (response.RequestId < lastResponseId) return;
        lastResponseId = response.RequestId;

        if (!string.IsNullOrWhiteSpace(response.SelectedPlayer))
            antiCheatPlayerSearch = response.SelectedPlayer;
        antiCheatEnabled = response.Enabled;
        if (response.Settings != null)
            CopyAntiCheatSettings(response.Settings);
        if (response.PacketSettings != null)
            CopyAntiCheatPacketProtectionSettings(response.PacketSettings);

        string responseOperation = (response.Operation ?? "").Trim().ToLowerInvariant();
        if (responseOperation == "enforcement")
            CopyAntiCheatEnforcementRules(response.ConsequenceRules);

        if (section != "anticheat") return;

        // Fingerprint resolution is asynchronous. If staff navigated away while it
        // was running, do not pull them back to the blacklist when the result arrives.
        if (responseOperation == "blacklistresolve" && antiCheatView != "blacklist") return;

        antiCheatView = responseOperation switch
        {
            "overview" => "overview",
            "players" => "players",
            "player" => "player",
            "reports" => "reports",
            "blacklist" or "blacklistresolve" => "blacklist",
            "enforcement" => "enforcement",
            "settings" => "settings",
            "packet" => "packet",
            _ => antiCheatView
        };

        pageHeaderText = response.Success ? Escape(response.Message) : "Anti-Cheat action failed";
        if (response.Success)
        {
            if (responseOperation == "player")
                SetAntiCheatPlayerResults(response.Lines);
            else
                SetGenericResults(response.Lines);
        }
        else
        {
            if (responseOperation == "player") antiCheatPlayerSummary.Clear();
            var failedLines = new List<string> { $"Error: {response.Message}" };
            if (response.Lines != null && response.Lines.Count > 0) failedLines.AddRange(response.Lines);
            SetGenericResults(failedLines);
        }
        ComposeDialog();
        SingleComposer?.GetTextInput("anticheat-player-search")?.SetValue(antiCheatPlayerSearch);
        SingleComposer?.GetTextInput("anticheat-report-search")?.SetValue(antiCheatReportSearch);
        SingleComposer?.GetTextInput("anticheat-blacklist-mod")?.SetValue(antiCheatBlacklistModId);
        SingleComposer?.GetTextInput("anticheat-blacklist-source")?.SetValue(antiCheatBlacklistSourcePlayer);
        ApplyAntiCheatControlValues();
        ApplyAntiCheatEnforcementControlValues();
    }

    public void Show(LoginProtectionResponse response)
    {
        if (response.RequestId < lastResponseId) return;
        lastResponseId = response.RequestId;

        if (response.Success)
        {
            loginEnabled = response.Enabled;
            loginProtectionSeconds = response.ProtectionSeconds;
            loginProtectOnJoin = response.ProtectOnJoin;
            loginProtectOnRespawn = response.ProtectOnRespawn;
            loginAnnounceOnJoin = response.AnnounceOnJoin;
            loginAnnounceOnEnd = response.AnnounceOnEnd;
            loginBlockPvP = response.BlockPvP;
            loginCancelOnAttack = response.CancelOnAttack;
            loginProtectFallDamage = response.ProtectFallDamage;
            loginExtinguishFire = response.ExtinguishFire;
            loginRecoverSwimmingOnLogin = response.RecoverSwimmingOnLogin;
            loginActiveProtectedCount = response.ActiveProtectedCount;
        }

        if (section != "loginprotection") return;

        pageHeaderText = response.Success
            ? Escape(response.Message)
            : "Login Protection";

        if (loginView == "status")
            SetGenericResults(response.Success ? response.Lines : new List<string> { response.Message });

        ComposeDialog();
        ApplyLoginProtectionControlValues();
    }

    public void Show(ProfilerResponse response)
    {
        if (response.RequestId < lastResponseId) return;
        lastResponseId = response.RequestId;
        profilerLiveRequestPending = false;

        profilerEnabled = response.Enabled;

        if (response.Compact)
        {
            if (response.Success || response.ProfileRunning)
            {
                profilerCurrent = response.Current ?? new ProfilerSamplePacket();
                profilerRunning = response.ProfileRunning;
                profilerRemainingSeconds = response.ProfileRemainingSeconds;
            }

            // Compact live packets intentionally do not replace history or rebuild
            // the whole Shift+L composer. That full rebuild caused visible client hitches.
            if (section == "profiler" && IsOpened())
                UpdateProfilerLiveElements();
            return;
        }

        if (response.Success || response.ProfileRunning)
        {
            profilerSamples.Clear();
            if (response.Samples != null) profilerSamples.AddRange(response.Samples);

            profilerSlowSpikes.Clear();
            if (response.SlowSpikes != null) profilerSlowSpikes.AddRange(response.SlowSpikes);

            profilerHotspots.Clear();
            if (response.Hotspots != null) profilerHotspots.AddRange(response.Hotspots);

            profilerModHotspots.Clear();
            if (response.ModHotspots != null) profilerModHotspots.AddRange(response.ModHotspots);

            profilerPhases.Clear();
            if (response.Phases != null) profilerPhases.AddRange(response.Phases);

            profilerProfileSlowTicks.Clear();
            if (response.ProfileSlowTicks != null) profilerProfileSlowTicks.AddRange(response.ProfileSlowTicks);

            profilerOffThreadReports.Clear();
            if (response.OffThreadReports != null) profilerOffThreadReports.AddRange(response.OffThreadReports);

            profilerDiagnosticIncidents.Clear();
            if (response.DiagnosticIncidents != null) profilerDiagnosticIncidents.AddRange(response.DiagnosticIncidents);
            profilerDiagnosticSummary = response.DiagnosticSummary ?? new ProfilerDiagnosticSummaryPacket();

            profilerLoadedModCount = response.LoadedModCount;
            if (!string.IsNullOrWhiteSpace(response.LocalWebUrl))
                webLocalUrl = response.LocalWebUrl;
            webPublicUrl = response.PublicWebUrl ?? "";
            profilerCurrent = response.Current ?? new ProfilerSamplePacket();
            profilerLastProfile = response.LastProfile ?? new ProfilerProfileSummaryPacket();
            profilerRunning = response.ProfileRunning;
            profilerRemainingSeconds = response.ProfileRemainingSeconds;
        }

        profilerMessage = string.IsNullOrWhiteSpace(response.Message)
            ? (response.Success ? SuiteLocalization.Get("gui.runtime.profilerdiagnostics") : SuiteLocalization.Get("gui.runtime.profilerfailed"))
            : response.Message;

        if (section != "profiler") return;

        pageHeaderText = SuiteLocalization.Get("gui.runtime.profilerdiagnostics");
        ComposeDialog();
    }

    public void Show(AdminCommandGuiResponse response)
    {
        if (response.RequestId < lastResponseId) return;
        lastResponseId = response.RequestId;
        if (section != "admincommands") return;

        pageHeaderText = response.Success ? SuiteLocalization.Get("gui.runtime.admincompleted") : SuiteLocalization.Get("gui.runtime.adminfailed");
        var rows = new List<string>
        {
            $"ADMINSTATUS\t{(response.Success ? "OK" : "ERROR")}\t{response.Message ?? ""}"
        };
        if (response.Lines != null)
        {
            RefreshSelectedAdminInventoryState(response.Lines);
            foreach (string line in response.Lines)
            {
                if (line.StartsWith("AUTORESTART\t", StringComparison.Ordinal))
                {
                    ApplyAutoRestartStatusLine(line);
                    continue;
                }
                if (line.StartsWith("CHATDISPLAY\t", StringComparison.Ordinal))
                {
                    ApplyChatDisplayStatusLine(line);
                    continue;
                }
                if (line.StartsWith("INV\t", StringComparison.Ordinal) || line.StartsWith("ITEM\t", StringComparison.Ordinal) || line.StartsWith("ADMINPLAYER\t", StringComparison.Ordinal)) rows.Add(line);
                else rows.Add($"ADMINLINE\t{line}");
            }
        }
        SetGenericResults(rows);
        ComposeDialog();
    }

    private void ComposeDialog()
    {
        RefreshAdminPlayerChoices();
        // Late replies can still update the saved view state after closing, but
        // must not rebuild and retain textures for an invisible dialog.
        if (disposed || !IsOpened()) return;
        RememberWorkspaceNavigation();
        ClearComposers();

        if (section != "animals" || animalView != "rules")
        {
            if (genericResultLines.Count == 0 && !string.IsNullOrWhiteSpace(resultText))
                SetGenericResults(new List<string> { WebUtility.HtmlDecode(resultText) });
        }

        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        // Wide diagnostic tables retain their full columns; investigation and
        // settings pages use a narrower workspace without scaling input hitboxes.
        const double secondaryTabY = 82;
        const double contentX = 16;
        const double innerX = 28;
        double shellWidth = section is "home" or "blocklogger" || (section == "anticheat" && antiCheatView == "settings") ? 1060 : section == "loginprotection" ? 1100 : section == "animals" ? 1240 : 1360;
        double shellHeight = section == "anticheat" && antiCheatView != "settings" ? 840 : 760;
        double contentWidth = shellWidth - 32;
        double innerWidth = shellWidth - 56;

        var shellBounds = ElementBounds.Fixed(0, 0, shellWidth, shellHeight);
        var composer = capi.Gui.CreateCompo("blocklogger-investigator", dialogBounds)
            .AddStaticElement(new SuitePanelElement(capi, shellBounds, section));
        BuildSuiteNavigation(composer, shellWidth);

        // Default to an empty results area. Individual views override these values when they use it.
        double resultsY = 0;
        double resultsHeight = 0;
        bool useGenericResults = true;

        if (section == "home")
        {
            useGenericResults = false;
            BuildSuiteHome(composer);
        }
        else if (section == "blocklogger")
        {
            // Give each label enough room at Vintage Story GUI scaling. Text buttons do not
            // reliably clip their captions, so undersized buttons visually overlap neighbors.
            composer
                .AddSuiteButton(SuiteLocalization.Get("gui.blocks"), () => SelectView("blocks"), ElementBounds.Fixed(22, secondaryTabY, 90, 30), view == "blocks")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.0530e3ff67"),
                    CairoFont.WhiteSmallText(), 320, ElementBounds.Fixed(22, secondaryTabY, 90, 30), "tip-blocklogger-tab-blocks")
                .AddSuiteButton(SuiteLocalization.Get("gui.containers"), () => SelectView("containers"), ElementBounds.Fixed(124, secondaryTabY, 130, 30), view == "containers")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.3aea1a9f96"),
                    CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(124, secondaryTabY, 130, 30), "tip-blocklogger-tab-containers")
                .AddSuiteButton(SuiteLocalization.Get("gui.entities"), () => SelectView("entities"), ElementBounds.Fixed(266, secondaryTabY, 100, 30), view == "entities")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.af9320bb26"),
                    CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(266, secondaryTabY, 100, 30), "tip-blocklogger-tab-entities")
                .AddSuiteButton(SuiteLocalization.Get("gui.players"), () => SelectView("players"), ElementBounds.Fixed(378, secondaryTabY, 100, 30), view == "players")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.9946631405"),
                    CairoFont.WhiteSmallText(), 370, ElementBounds.Fixed(378, secondaryTabY, 100, 30), "tip-blocklogger-tab-players")
                .AddSuiteButton(SuiteLocalization.Get("gui.status"), SelectStatus, ElementBounds.Fixed(490, secondaryTabY, 90, 30), view == "status")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.9a17c03715"),
                    CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(490, secondaryTabY, 90, 30), "tip-blocklogger-tab-status")
                .AddSuiteButton(SuiteLocalization.Get("gui.settings"), SelectBlockLoggerSettings, ElementBounds.Fixed(592, secondaryTabY, 110, 30), view == "settings")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.1b4a801586"),
                    CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(592, secondaryTabY, 110, 30), "tip-blocklogger-tab-settings");

            if (view == "settings")
            {
                useGenericResults = false;
                BuildBlockLoggerSettings(composer);
            }
            else if (view == "status")
            {
                // Status has no search/filter controls.
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, 122, contentWidth, 618))
                    .AddRichtext($"<strong>{pageHeaderText}</strong>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 136, 980, 26), "page-header");

                resultsY = 170;
                resultsHeight = 550;
            }
            else if (view == "players")
            {
                const double panelY = 122;
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, panelY, contentWidth, 142))
                    .AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 16, 58, 24))
                    .AddTextInput(ElementBounds.Fixed(92, panelY + 11, 330, 30),
                        value => playerSearch = value ?? "", CairoFont.WhiteSmallText(), "player-search")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.bbc09c39b1"),
                        CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(92, panelY + 11, 330, 30), "tip-player-search")
                    .AddRichtext(SuiteLocalization.Get("gui.rowslabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(446, panelY + 16, 48, 24))
                    .AddNumberInput(ElementBounds.Fixed(494, panelY + 11, 72, 30),
                        OnLimitChanged, CairoFont.WhiteSmallText(), "limit")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.0d9bb2527a"),
                        CairoFont.WhiteSmallText(), 300, ElementBounds.Fixed(494, panelY + 11, 72, 30), "tip-player-rows")

                    .AddRichtext(SuiteLocalization.Get("gui.activitylabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 57, 66, 24))
                    .AddDropDown(
                        new[] { "any", "blocks", "containers", "entities" },
                        new[] { "Any", "Blocks", "Containers", "Entities" },
                        PlayerActionIndex(),
                        OnActionChanged,
                        ElementBounds.Fixed(100, panelY + 52, 180, 30),
                        CairoFont.WhiteSmallText(),
                        "action")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.79dee41707"),
                        CairoFont.WhiteSmallText(), 330, ElementBounds.Fixed(100, panelY + 52, 180, 30), "tip-player-activity")
                    .AddSuiteButton(SuiteLocalization.Get("gui.search"), Search, ElementBounds.Fixed(300, panelY + 52, 120, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.fc8ce888e6"),
                        CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(300, panelY + 52, 120, 30), "tip-player-search-button")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), Refresh, ElementBounds.Fixed(434, panelY + 52, 120, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.9e397c52be"),
                        CairoFont.WhiteSmallText(), 300, ElementBounds.Fixed(434, panelY + 52, 120, 30), "tip-player-refresh")
                    .AddRichtext(SuiteLocalization.Get("gui.playerhistoryhelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 101, 760, 24))

                    .AddSuiteInset(ElementBounds.Fixed(contentX, 276, contentWidth, 394))
                    .AddRichtext($"<strong>{pageHeaderText}</strong>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 290, 980, 26), "page-header");

                resultsY = 324;
                resultsHeight = 396;
            }
            else
            {
                const double panelY = 122;
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, panelY, contentWidth, 142))

                    .AddRichtext(SuiteLocalization.Get("gui.targetlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 16, 58, 24))
                    .AddDropDown(
                        new[] { "selection", "current" },
                        new[] { "Looked at", "Current pos" },
                        useSelection ? 0 : 1,
                        OnTargetChanged,
                        ElementBounds.Fixed(92, panelY + 11, 145, 30),
                        CairoFont.WhiteSmallText(),
                        "target")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.b959e8adec"),
                        CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(92, panelY + 11, 145, 30), "tip-history-target")
                    .AddRichtext(SuiteLocalization.Get("gui.radiuslabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(258, panelY + 16, 58, 24))
                    .AddNumberInput(ElementBounds.Fixed(314, panelY + 11, 70, 30),
                        OnRadiusChanged, CairoFont.WhiteSmallText(), "radius")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.19f29fbee6"),
                        CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(314, panelY + 11, 70, 30), "tip-history-radius")
                    .AddRichtext(SuiteLocalization.Get("gui.rowslabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(406, panelY + 16, 48, 24))
                    .AddNumberInput(ElementBounds.Fixed(452, panelY + 11, 70, 30),
                        OnLimitChanged, CairoFont.WhiteSmallText(), "limit")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.0d9bb2527a"),
                        CairoFont.WhiteSmallText(), 300, ElementBounds.Fixed(452, panelY + 11, 70, 30), "tip-history-rows")

                    .AddRichtext(SuiteLocalization.Get("gui.actionlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 57, 58, 24))
                    .AddDropDown(ActionValues(), ActionNames(), ActionIndex(), OnActionChanged,
                        ElementBounds.Fixed(92, panelY + 52, 170, 30), CairoFont.WhiteSmallText(), "action")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.d08509d9bf"),
                        CairoFont.WhiteSmallText(), 310, ElementBounds.Fixed(92, panelY + 52, 170, 30), "tip-history-action")

                    .AddRichtext(SuiteLocalization.Get("gui.searchlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(286, panelY + 57, 58, 24))
                    .AddTextInput(ElementBounds.Fixed(342, panelY + 52, 280, 30),
                        value => search = value ?? "", CairoFont.WhiteSmallText(), "search")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.ff60708cd5"),
                        CairoFont.WhiteSmallText(), 380, ElementBounds.Fixed(342, panelY + 52, 280, 30), "tip-history-search")
                    .AddSuiteButton(SuiteLocalization.Get("gui.search"), Search, ElementBounds.Fixed(638, panelY + 52, 100, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.38c24a07a0"),
                        CairoFont.WhiteSmallText(), 350, ElementBounds.Fixed(638, panelY + 52, 100, 30), "tip-history-search-button")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), Refresh, ElementBounds.Fixed(752, panelY + 52, 100, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.96a78eac6e"),
                        CairoFont.WhiteSmallText(), 300, ElementBounds.Fixed(752, panelY + 52, 100, 30), "tip-history-refresh")

                    .AddRichtext(SuiteLocalization.Get("gui.searchoptionalhelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 101, 650, 24))

                    .AddSuiteInset(ElementBounds.Fixed(contentX, 276, contentWidth, 394))
                    .AddRichtext($"<strong>{pageHeaderText}</strong>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 290, 980, 26), "page-header");

                resultsY = 324;
                resultsHeight = 396;
            }
        }
        else if (section == "admincommands")
        {
            var playersTab = ElementBounds.Fixed(22, secondaryTabY, 100, 30);
            var moderationTab = ElementBounds.Fixed(134, secondaryTabY, 126, 30);
            var teleportTab = ElementBounds.Fixed(272, secondaryTabY, 102, 30);
            var inventoryTab = ElementBounds.Fixed(386, secondaryTabY, 106, 30);
            var serverTab = ElementBounds.Fixed(504, secondaryTabY, 92, 30);
            var reportsTab = ElementBounds.Fixed(608, secondaryTabY, 96, 30);

            composer
                .AddSuiteButton(SuiteLocalization.Get("gui.players"), () => SelectAdminCommandView("players"), playersTab, adminCommandView == "players")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.d06a61d8e6"), CairoFont.WhiteSmallText(), 390, playersTab.FlatCopy(), "tip-admincmd-players")
                .AddSuiteButton(SuiteLocalization.Get("gui.moderation"), () => SelectAdminCommandView("moderation"), moderationTab, adminCommandView == "moderation")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.96db369cb3"), CairoFont.WhiteSmallText(), 390, moderationTab.FlatCopy(), "tip-admincmd-moderation")
                .AddSuiteButton(SuiteLocalization.Get("gui.teleport"), () => SelectAdminCommandView("teleport"), teleportTab, adminCommandView == "teleport")
                .AddHoverText(SuiteLocalization.Get("gui.teleporttabhelp"), CairoFont.WhiteSmallText(), 390, teleportTab.FlatCopy(), "tip-admincmd-teleport")
                .AddSuiteButton(SuiteLocalization.Get("gui.inventory"), () => SelectAdminCommandView("inventory"), inventoryTab, adminCommandView == "inventory")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.d56abac13b"), CairoFont.WhiteSmallText(), 400, inventoryTab.FlatCopy(), "tip-admincmd-inventory")
                .AddSuiteButton(SuiteLocalization.Get("gui.server"), () => SelectAdminCommandView("server"), serverTab, adminCommandView == "server")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.cf7edc12b6"), CairoFont.WhiteSmallText(), 400, serverTab.FlatCopy(), "tip-admincmd-server")
                .AddSuiteButton(SuiteLocalization.Get("gui.reports"), () => SelectAdminCommandView("reports"), reportsTab, adminCommandView == "reports")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.64bc59063d"), CairoFont.WhiteSmallText(), 360, reportsTab.FlatCopy(), "tip-admincmd-reports");

            const double panelY = 122;
            if (adminCommandView == "players")
            {
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, panelY, contentWidth, 176))
                    .AddRichtext(SuiteLocalization.Get("gui.playeradministration"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 12, 330, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 48, 100, 24))
                    .AddDropDown(AdminTeleportPlayerValues(), AdminTeleportPlayerNames(), AdminTeleportPlayerIndex(), OnAdminTeleportPlayerChanged,
                        ElementBounds.Fixed(142, panelY + 42, 220, 30), CairoFont.WhiteSmallText(), "admincmd-player-picker")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RefreshAdminTeleportPlayers, ElementBounds.Fixed(372, panelY + 42, 84, 30))
                    .AddTextInput(ElementBounds.Fixed(468, panelY + 42, 190, 30), v => adminCommandTarget = v ?? "", CairoFont.WhiteSmallText(), "admincmd-target")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.40da68424b"), CairoFont.WhiteSmallText(), 380, ElementBounds.Fixed(468, panelY + 42, 190, 30), "tip-admincmd-player-fallback")
                    .AddSuiteButton(SuiteLocalization.Get("gui.playerinfo"), () => RunAdminCommand("player-info"), ElementBounds.Fixed(670, panelY + 42, 118, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.seen"), () => RunAdminCommand("seen"), ElementBounds.Fixed(800, panelY + 42, 80, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.playtime"), () => RunAdminCommand("playtime"), ElementBounds.Fixed(890, panelY + 42, 92, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.afk"), () => RunAdminCommand("afk"), ElementBounds.Fixed(992, panelY + 42, 72, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.history"), () => RunAdminCommand("history"), ElementBounds.Fixed(1074, panelY + 42, 88, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.warnings"), () => RunAdminCommand("warnings"), ElementBounds.Fixed(1172, panelY + 42, 108, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.notes"), () => RunAdminCommand("notes"), ElementBounds.Fixed(38, panelY + 86, 96, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.heal"), () => RunAdminCommand("heal"), ElementBounds.Fixed(146, panelY + 86, 90, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.feed"), () => RunAdminCommand("feed"), ElementBounds.Fixed(248, panelY + 86, 90, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.revive"), () => RunAdminCommand("revive"), ElementBounds.Fixed(350, panelY + 86, 96, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.ghost"), () => RunAdminCommand("ghost"), ElementBounds.Fixed(458, panelY + 86, 96, 30))
                    .AddRichtext(SuiteLocalization.Get("gui.playeradminpermissions"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(566, panelY + 91, 690, 24))
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 312, 980, 26), "page-header");
                resultsY = 346;
                resultsHeight = 374;
            }
            else if (adminCommandView == "moderation")
            {
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, panelY, contentWidth, 258))
                    .AddRichtext(SuiteLocalization.Get("gui.moderationtitle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 12, 240, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 48, 100, 24))
                    .AddDropDown(AdminTeleportPlayerValues(), AdminTeleportPlayerNames(), AdminTeleportPlayerIndex(), OnAdminTeleportPlayerChanged,
                        ElementBounds.Fixed(142, panelY + 42, 200, 30), CairoFont.WhiteSmallText(), "admincmd-player-picker")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RefreshAdminTeleportPlayers, ElementBounds.Fixed(352, panelY + 42, 84, 30))
                    .AddTextInput(ElementBounds.Fixed(446, panelY + 42, 170, 30), v => adminCommandTarget = v ?? "", CairoFont.WhiteSmallText(), "admincmd-target")
                    .AddRichtext(SuiteLocalization.Get("gui.durationlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(628, panelY + 48, 72, 24))
                    .AddTextInput(ElementBounds.Fixed(704, panelY + 42, 100, 30), v => adminCommandDuration = v ?? "", CairoFont.WhiteSmallText(), "admincmd-duration")
                    .AddRichtext(SuiteLocalization.Get("gui.recordid"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(816, panelY + 48, 78, 24))
                    .AddNumberInput(ElementBounds.Fixed(898, panelY + 42, 82, 30), OnAdminRecordIdChanged, CairoFont.WhiteSmallText(), "admincmd-record-id")
                    .AddRichtext(SuiteLocalization.Get("gui.reasonnote"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 87, 108, 24))
                    .AddTextInput(ElementBounds.Fixed(150, panelY + 81, 666, 30), v => adminCommandReason = v ?? "", CairoFont.WhiteSmallText(), "admincmd-reason")
                    .AddSuiteButton(SuiteLocalization.Get("gui.warn"), () => RunAdminCommand("warn"), ElementBounds.Fixed(838, panelY + 81, 84, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.removewarn"), () => RunAdminCommand("unwarn"), ElementBounds.Fixed(934, panelY + 81, 120, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.mute"), () => RunAdminCommand("mute"), ElementBounds.Fixed(1066, panelY + 81, 82, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.unmute"), () => RunAdminCommand("unmute"), ElementBounds.Fixed(1160, panelY + 81, 92, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.muteinfo"), () => RunAdminCommand("muteinfo"), ElementBounds.Fixed(838, panelY + 120, 104, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.tempban"), () => RunAdminCommand("tempban"), ElementBounds.Fixed(954, panelY + 120, 104, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.baninfo"), () => RunAdminCommand("baninfo"), ElementBounds.Fixed(1070, panelY + 120, 96, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.unban"), () => RunAdminCommand("unban"), ElementBounds.Fixed(1178, panelY + 120, 82, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.freeze"), () => RunAdminCommand("freeze"), ElementBounds.Fixed(38, panelY + 160, 94, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.unfreeze"), () => RunAdminCommand("unfreeze"), ElementBounds.Fixed(144, panelY + 160, 102, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.kick"), () => RunAdminCommand("kick", true), ElementBounds.Fixed(258, panelY + 160, 86, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.addnote"), () => RunAdminCommand("note-add"), ElementBounds.Fixed(356, panelY + 160, 100, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.removenote"), () => RunAdminCommand("note-remove"), ElementBounds.Fixed(468, panelY + 160, 116, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.smite"), () => RunAdminCommand("smite"), ElementBounds.Fixed(596, panelY + 160, 90, 30))
                    .AddRichtext(SuiteLocalization.Get("gui.moderationdurationhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(700, panelY + 166, 556, 24))
                    .AddSuiteButton(SuiteLocalization.Get("gui.jail"), () => RunAdminCommand("jail", true), ElementBounds.Fixed(38, panelY + 200, 90, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.unjail"), () => RunAdminCommand("unjail", true), ElementBounds.Fixed(140, panelY + 200, 96, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.jailinfo"), () => RunAdminCommand("jailinfo"), ElementBounds.Fixed(248, panelY + 200, 104, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.setjail"), () => RunAdminCommand("setjail", true), ElementBounds.Fixed(364, panelY + 200, 104, 30))
                    .AddRichtext(SuiteLocalization.Get("gui.jailhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(482, panelY + 206, 770, 24))
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 392, 980, 26), "page-header");
                resultsY = 426;
                resultsHeight = 294;
            }
            else if (adminCommandView == "teleport")
            {
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, panelY, contentWidth, 178))
                    .AddRichtext(SuiteLocalization.Get("gui.teleporthelpers"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 12, 280, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 52, 100, 24))
                    .AddDropDown(
                        AdminTeleportPlayerValues(),
                        AdminTeleportPlayerNames(),
                        AdminTeleportPlayerIndex(),
                        OnAdminTeleportPlayerChanged,
                        ElementBounds.Fixed(142, panelY + 46, 250, 30),
                        CairoFont.WhiteSmallText(),
                        "admincmd-teleport-player")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RefreshAdminTeleportPlayers, ElementBounds.Fixed(404, panelY + 46, 88, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.gotoplayer"), () => RunAdminCommand("goto"), ElementBounds.Fixed(506, panelY + 46, 118, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.bringhere"), () => RunAdminCommand("bring"), ElementBounds.Fixed(636, panelY + 46, 118, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.sendback"), () => RunAdminCommand("sendback"), ElementBounds.Fixed(766, panelY + 46, 118, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.myback"), () => RunAdminCommand("back"), ElementBounds.Fixed(896, panelY + 46, 104, 30))
                    .AddRichtext(SuiteLocalization.Get("gui.mapteleporthelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 94, 920, 20))
                    .AddRichtext(SuiteLocalization.Get("gui.teleportsafetyhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 120, 900, 20))
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 312, 980, 26), "page-header");
                resultsY = 346;
                resultsHeight = 374;
            }
            else if (adminCommandView == "inventory")
            {
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, panelY, contentWidth, 324))
                    .AddRichtext(SuiteLocalization.Get("gui.inventorymanager"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 12, 360, 24))
                    .AddStaticText(SuiteLocalization.Get("gui.inventoryeditorhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(410, panelY + 14, 780, 22))

                    .AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 48, 100, 24))
                    .AddDropDown(AdminTeleportPlayerValues(), AdminTeleportPlayerNames(), AdminTeleportPlayerIndex(), OnAdminTeleportPlayerChanged,
                        ElementBounds.Fixed(142, panelY + 42, 200, 30), CairoFont.WhiteSmallText(), "admincmd-player-picker")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RefreshAdminTeleportPlayers, ElementBounds.Fixed(352, panelY + 42, 84, 30))
                    .AddTextInput(ElementBounds.Fixed(446, panelY + 42, 160, 30), v => adminCommandTarget = v ?? "", CairoFont.WhiteSmallText(), "admincmd-target")
                    .AddSuiteButton(SuiteLocalization.Get("gui.loadinventory"), () => RunAdminCommand("invsee"), ElementBounds.Fixed(618, panelY + 42, 126, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.snapshot"), () => RunAdminCommand("invsnapshot"), ElementBounds.Fixed(756, panelY + 42, 104, 30))
                    .AddRichtext($"<font color=\"#91a0ae\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.selectedslot", adminCommandInventoryId, adminCommandSlot, adminCommandQuantity))}</strong></font>",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(876, panelY + 48, 380, 24))

                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(34, panelY + 82, 1248, 88)))
                    .AddStaticText(SuiteLocalization.Get("gui.itemfinder"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, panelY + 92, 110, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.search"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, panelY + 125, 54, 22))
                    .AddTextInput(ElementBounds.Fixed(108, panelY + 119, 250, 28), v => adminCommandItemSearch = v ?? "", CairoFont.WhiteSmallText(), "admincmd-item-search")
                    .AddSuiteButton(SuiteLocalization.Get("gui.searchitems"), () => RunAdminCommand("itemsearch"), ElementBounds.Fixed(370, panelY + 119, 112, 28))
                    .AddStaticText(SuiteLocalization.Get("gui.itemcode"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(504, panelY + 125, 72, 22))
                    .AddTextInput(ElementBounds.Fixed(580, panelY + 119, 330, 28), v => adminCommandItemCode = v ?? "", CairoFont.WhiteSmallText(), "admincmd-item-code")
                    .AddStaticText(SuiteLocalization.Get("gui.quantity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(928, panelY + 125, 34, 22))
                    .AddNumberInput(ElementBounds.Fixed(966, panelY + 119, 70, 28), OnAdminQuantityChanged, CairoFont.WhiteSmallText(), "admincmd-qty")
                    .AddSuiteButton(SuiteLocalization.Get("gui.giveitem"), () => RunAdminCommand("invgive"), ElementBounds.Fixed(1050, panelY + 119, 104, 28))
                    .AddRichtext(SuiteLocalization.Get("gui.itemsearchhelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, panelY + 151, 1190, 24))

                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(34, panelY + 181, 1248, 76)))
                    .AddStaticText(SuiteLocalization.Get("gui.selectedslot"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, panelY + 191, 120, 22))
                    .AddRichtext(SuiteLocalization.Get("gui.inventorybackuphelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(186, panelY + 189, 1056, 28))
                    .AddStaticText(SuiteLocalization.Get("gui.inventory"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, panelY + 224, 68, 22))
                    .AddTextInput(ElementBounds.Fixed(124, panelY + 218, 190, 28), v => adminCommandInventoryId = v ?? "", CairoFont.WhiteSmallText(), "admincmd-inventory-id")
                    .AddStaticText(SuiteLocalization.Get("gui.slot"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(330, panelY + 224, 36, 22))
                    .AddNumberInput(ElementBounds.Fixed(370, panelY + 218, 66, 28), OnAdminSlotChanged, CairoFont.WhiteSmallText(), "admincmd-slot")
                    .AddSuiteButton(SuiteLocalization.Get("gui.setqty"), () => RunAdminCommand("invqty"), ElementBounds.Fixed(452, panelY + 218, 94, 28))
                    .AddSuiteButton(SuiteLocalization.Get("gui.replaceslot"), () => RunAdminCommand("invreplace", true), ElementBounds.Fixed(558, panelY + 218, 114, 28))
                    .AddSuiteButton(SuiteLocalization.Get("gui.clearslot"), () => RunAdminCommand("invremove", true), ElementBounds.Fixed(684, panelY + 218, 104, 28))

                    .AddStaticText(SuiteLocalization.Get("gui.snapshotid"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 276, 82, 22))
                    .AddNumberInput(ElementBounds.Fixed(124, panelY + 270, 90, 30), OnAdminRecordIdChanged, CairoFont.WhiteSmallText(), "admincmd-record-id")
                    .AddStaticText(SuiteLocalization.Get("gui.reason"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(230, panelY + 276, 68, 22))
                    .AddTextInput(ElementBounds.Fixed(302, panelY + 270, 334, 30), v => adminCommandReason = v ?? "", CairoFont.WhiteSmallText(), "admincmd-reason")
                    .AddSuiteButton(SuiteLocalization.Get("gui.restoresnapshot"), () => RunAdminCommand("invrestore", true), ElementBounds.Fixed(650, panelY + 270, 148, 30))
                    .AddRichtext(SuiteLocalization.Get("gui.inventoryslothelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(820, panelY + 267, 430, 42))
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 458, 1100, 26), "page-header");
                resultsY = 492;
                resultsHeight = 320;
            }
            else if (adminCommandView == "server")
            {
                // Each settings page owns the space above the shared result pane.
                // Stacking all three cards placed the old result header inside Chat Display.
                const double serverPanelY = panelY + 44;
                composer
                    .AddSuiteButton(SuiteLocalization.Get("gui.serveroperationstab"), () => SelectAdminServerView("operations"), ElementBounds.Fixed(34, panelY, 200, 30), adminServerView == "operations")
                    .AddSuiteButton(SuiteLocalization.Get("gui.serverrestartstab"), () => SelectAdminServerView("restarts"), ElementBounds.Fixed(246, panelY, 240, 30), adminServerView == "restarts")
                    .AddSuiteButton(SuiteLocalization.Get("gui.serverchattab"), () => SelectAdminServerView("chat"), ElementBounds.Fixed(498, panelY, 200, 30), adminServerView == "chat")
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(34, serverPanelY, 1248, 220)));
                if (adminServerView == "restarts")
                {
                    composer
                        .AddRichtext(SuiteLocalization.Get("gui.automatedrestartstitle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, serverPanelY + 10, 320, 24))
                        .AddStaticText(SuiteLocalization.Get("gui.restarttimes"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, serverPanelY + 46, 350, 22))
                        .AddTextInput(ElementBounds.Fixed(52, serverPanelY + 68, 350, 28), v => adminAutoRestartTimes = v ?? "", CairoFont.WhiteSmallText(), "autorestart-times")
                        .AddStaticText(SuiteLocalization.Get("gui.restartwarnings"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(424, serverPanelY + 46, 320, 22))
                        .AddTextInput(ElementBounds.Fixed(424, serverPanelY + 68, 320, 28), v => adminAutoRestartWarnings = v ?? "", CairoFont.WhiteSmallText(), "autorestart-warnings")
                        .AddStaticText(SuiteLocalization.Get("gui.reason"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(766, serverPanelY + 46, 496, 22))
                        .AddTextInput(ElementBounds.Fixed(766, serverPanelY + 68, 496, 28), v => adminAutoRestartReason = v ?? "", CairoFont.WhiteSmallText(), "autorestart-reason")
                        .AddSwitch(v => adminAutoRestartGameAnnouncements = v, ElementBounds.Fixed(52, serverPanelY + 112, 52, 26), "autorestart-game")
                        .AddStaticText(SuiteLocalization.Get("gui.gamewarnings"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(110, serverPanelY + 116, 200, 22))
                        .AddSwitch(v => adminAutoRestartDiscordAnnouncements = v, ElementBounds.Fixed(322, serverPanelY + 112, 52, 26), "autorestart-discord")
                        .AddStaticText(SuiteLocalization.Get("gui.discordwarnings"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(380, serverPanelY + 116, 220, 22))
                        .AddSuiteButton(SuiteLocalization.Get("gui.status"), () => RunAdminCommand("autorestart-status"), ElementBounds.Fixed(620, serverPanelY + 110, 84, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => RunAdminCommand("autorestart-save"), ElementBounds.Fixed(714, serverPanelY + 110, 82, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.enable"), () => RunAdminCommand("autorestart-enable"), ElementBounds.Fixed(806, serverPanelY + 110, 86, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.disable"), () => RunAdminCommand("autorestart-disable"), ElementBounds.Fixed(902, serverPanelY + 110, 86, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.skipnext"), () => RunAdminCommand("autorestart-skip", true), ElementBounds.Fixed(998, serverPanelY + 110, 106, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.reload"), () => RunAdminCommand("autorestart-reload"), ElementBounds.Fixed(1114, serverPanelY + 110, 88, 30))
                        .AddStaticText(SuiteLocalization.Get("gui.restartrelaunchhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, serverPanelY + 164, 1160, 40));
                }
                else if (adminServerView == "chat")
                {
                    composer
                        .AddRichtext(SuiteLocalization.Get("gui.serverchattab"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, serverPanelY + 12, 300, 24))
                        .AddSwitch(v => adminChatShowRole = v, ElementBounds.Fixed(52, serverPanelY + 48, 52, 26), "chatdisplay-role")
                        .AddStaticText("Role before name", CairoFont.WhiteSmallText(), ElementBounds.Fixed(110, serverPanelY + 52, 126, 22))
                        .AddSwitch(v => adminChatBoldName = v, ElementBounds.Fixed(400, serverPanelY + 48, 52, 26), "chatdisplay-bold")
                        .AddStaticText("Bold player name", CairoFont.WhiteSmallText(), ElementBounds.Fixed(458, serverPanelY + 52, 126, 22))
                        .AddSwitch(v => adminChatShowTimestamp = v, ElementBounds.Fixed(596, serverPanelY + 48, 52, 26), "chatdisplay-time")
                        .AddStaticText("Timestamp", CairoFont.WhiteSmallText(), ElementBounds.Fixed(654, serverPanelY + 52, 82, 22))
                        .AddStaticText("Format", CairoFont.WhiteSmallText(), ElementBounds.Fixed(748, serverPanelY + 52, 48, 22))
                        .AddTextInput(ElementBounds.Fixed(800, serverPanelY + 46, 92, 28), v => adminChatTimestampFormat = v ?? "", CairoFont.WhiteSmallText(), "chatdisplay-format")
                        .AddSwitch(v => adminChatUseUtc = v, ElementBounds.Fixed(906, serverPanelY + 48, 52, 26), "chatdisplay-utc")
                        .AddStaticText("UTC", CairoFont.WhiteSmallText(), ElementBounds.Fixed(964, serverPanelY + 52, 34, 22))
                        .AddSuiteButton(SuiteLocalization.Get("gui.status"), () => RunAdminCommand("chatdisplay-status"), ElementBounds.Fixed(1012, serverPanelY + 46, 82, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => RunAdminCommand("chatdisplay-save"), ElementBounds.Fixed(1104, serverPanelY + 46, 82, 30))
                        .AddSwitch(v => adminChatFormatGeneral = v, ElementBounds.Fixed(52, serverPanelY + 92, 52, 26), "chatdisplay-general")
                        .AddStaticText("Format General chat", CairoFont.WhiteSmallText(), ElementBounds.Fixed(110, serverPanelY + 96, 160, 22))
                        .AddSwitch(v => adminChatFormatOtherGroups = v, ElementBounds.Fixed(286, serverPanelY + 92, 52, 26), "chatdisplay-other-groups")
                        .AddStaticText("Format all other groups", CairoFont.WhiteSmallText(), ElementBounds.Fixed(344, serverPanelY + 96, 180, 22))
                        .AddStaticText("Extra group IDs", CairoFont.WhiteSmallText(), ElementBounds.Fixed(550, serverPanelY + 96, 110, 22))
                        .AddTextInput(ElementBounds.Fixed(664, serverPanelY + 90, 270, 28), v => adminChatAdditionalGroupIds = v ?? "", CairoFont.WhiteSmallText(), "chatdisplay-extra-groups")
                        .AddStaticText("General-only is the safe default for proximity/RP chat mods. Extra IDs are comma-separated.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, serverPanelY + 132, 850, 22))
                        .AddStaticText("Role/name/timestamp formatting remains OFF until enabled above.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, serverPanelY + 164, 850, 22));
                }
                else
                {
                    composer
                        .AddRichtext(SuiteLocalization.Get("gui.serveroperations"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, serverPanelY + 12, 300, 24))
                        .AddRichtext(SuiteLocalization.Get("gui.delayduration"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, serverPanelY + 40, 236, 22))
                        .AddTextInput(ElementBounds.Fixed(38, serverPanelY + 64, 236, 28), v => adminCommandDuration = v ?? "", CairoFont.WhiteSmallText(), "admincmd-duration")
                        .AddRichtext(SuiteLocalization.Get("gui.lagradius"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(292, serverPanelY + 40, 160, 22))
                        .AddNumberInput(ElementBounds.Fixed(292, serverPanelY + 64, 160, 28), OnAdminRadiusChanged, CairoFont.WhiteSmallText(), "admincmd-radius")
                        .AddRichtext(SuiteLocalization.Get("gui.reasonlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(470, serverPanelY + 40, 780, 22))
                        .AddTextInput(ElementBounds.Fixed(470, serverPanelY + 64, 780, 28), v => adminCommandReason = v ?? "", CairoFont.WhiteSmallText(), "admincmd-reason")
                        .AddSuiteButton(SuiteLocalization.Get("gui.laghere"), () => RunAdminCommand("laghere"), ElementBounds.Fixed(38, serverPanelY + 104, 104, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.cleanupscan"), () => RunAdminCommand("cleanup-scan"), ElementBounds.Fixed(154, serverPanelY + 104, 122, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.removedrops"), () => RunAdminCommand("cleanup-drops", true), ElementBounds.Fixed(288, serverPanelY + 104, 122, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.maintenanceon"), () => RunAdminCommand("maintenance-on"), ElementBounds.Fixed(422, serverPanelY + 104, 132, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.maintenanceoff"), () => RunAdminCommand("maintenance-off"), ElementBounds.Fixed(566, serverPanelY + 104, 132, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.maintenancestatus"), () => RunAdminCommand("maintenance-status"), ElementBounds.Fixed(710, serverPanelY + 104, 148, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.scheduleshutdown"), () => RunAdminCommand("shutdown", true), ElementBounds.Fixed(38, serverPanelY + 144, 176, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.schedulerestart"), () => RunAdminCommand("restart", true), ElementBounds.Fixed(226, serverPanelY + 144, 164, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.cancelshutdown"), () => RunAdminCommand("shutdown-cancel"), ElementBounds.Fixed(402, serverPanelY + 144, 154, 30))
                        .AddSuiteButton(SuiteLocalization.Get("gui.undolast"), () => RunAdminCommand("undo", true), ElementBounds.Fixed(568, serverPanelY + 144, 112, 30))
                        .AddRichtext(SuiteLocalization.Get("gui.serverconfirmhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, serverPanelY + 184, 1210, 32));
                }
                composer.AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>",
                    CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, serverPanelY + 236, innerWidth, 26), "page-header");
                resultsY = serverPanelY + 270;
                resultsHeight = shellHeight - 28 - resultsY;
            }
            else
            {
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, panelY, contentWidth, 210))
                    .AddRichtext(SuiteLocalization.Get("gui.issuereportqueue"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 12, 300, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.reportid"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 48, 72, 24))
                    .AddNumberInput(ElementBounds.Fixed(114, panelY + 42, 92, 30), OnAdminRecordIdChanged, CairoFont.WhiteSmallText(), "admincmd-record-id")
                    .AddRichtext(SuiteLocalization.Get("gui.statusassignee"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(224, panelY + 48, 150, 24))
                    .AddTextInput(ElementBounds.Fixed(378, panelY + 42, 196, 30), v => adminCommandReportValue = v ?? "", CairoFont.WhiteSmallText(), "admincmd-report-value")
                    .AddRichtext(SuiteLocalization.Get("gui.privatenote"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 88, 88, 24))
                    .AddTextInput(ElementBounds.Fixed(130, panelY + 82, 650, 30), v => adminCommandReason = v ?? "", CairoFont.WhiteSmallText(), "admincmd-reason")
                    .AddSuiteButton(SuiteLocalization.Get("gui.openreports"), () => RunAdminCommand("reports-open"), ElementBounds.Fixed(596, panelY + 42, 120, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.allreports"), () => RunAdminCommand("reports-all"), ElementBounds.Fixed(728, panelY + 42, 112, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.setstatus"), () => RunAdminCommand("report-status"), ElementBounds.Fixed(852, panelY + 42, 104, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.assign"), () => RunAdminCommand("report-assign"), ElementBounds.Fixed(968, panelY + 42, 92, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.addnote"), () => RunAdminCommand("report-note"), ElementBounds.Fixed(1072, panelY + 42, 100, 30))
                    .AddRichtext(SuiteLocalization.Get("gui.reportstatushelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(804, panelY + 88, 480, 46))
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 342, 980, 26), "page-header");
                resultsY = 376;
                resultsHeight = 344;
            }
        }
        else if (section == "anticheat")
        {
            var overviewTabBounds = ElementBounds.Fixed(22, secondaryTabY, 104, 30);
            var playersTabBounds = ElementBounds.Fixed(138, secondaryTabY, 92, 30);
            var reportsTabBounds = ElementBounds.Fixed(242, secondaryTabY, 96, 30);
            var blacklistTabBounds = ElementBounds.Fixed(350, secondaryTabY, 106, 30);
            var enforcementTabBounds = ElementBounds.Fixed(468, secondaryTabY, 126, 30);
            var settingsTabBounds = ElementBounds.Fixed(606, secondaryTabY, 104, 30);
            var packetTabBounds = ElementBounds.Fixed(722, secondaryTabY, 136, 30);
            var playerTabBounds = ElementBounds.Fixed(870, secondaryTabY, 134, 30);

            composer
                .AddSuiteButton(SuiteLocalization.Get("gui.overview"), () => SelectAntiCheatView("overview"), overviewTabBounds, antiCheatView == "overview")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.aebbaa0887"),
                    CairoFont.WhiteSmallText(), 420, overviewTabBounds.FlatCopy(), "tip-anticheat-overview")
                .AddSuiteButton(SuiteLocalization.Get("gui.players"), () => SelectAntiCheatView("players"), playersTabBounds, antiCheatView == "players")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.4b9572bfa2"),
                    CairoFont.WhiteSmallText(), 360, playersTabBounds.FlatCopy(), "tip-anticheat-players")
                .AddSuiteButton(SuiteLocalization.Get("gui.reports"), () => SelectAntiCheatView("reports"), reportsTabBounds, antiCheatView == "reports")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.68a0c30ff9"),
                    CairoFont.WhiteSmallText(), 430, reportsTabBounds.FlatCopy(), "tip-anticheat-reports")
                .AddSuiteButton(SuiteLocalization.Get("gui.blacklist"), () => SelectAntiCheatView("blacklist"), blacklistTabBounds, antiCheatView == "blacklist")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.8c1a3f0a7b"),
                    CairoFont.WhiteSmallText(), 430, blacklistTabBounds.FlatCopy(), "tip-anticheat-blacklist")
                .AddSuiteButton(SuiteLocalization.Get("gui.enforcement"), () => SelectAntiCheatView("enforcement"), enforcementTabBounds, antiCheatView == "enforcement")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.a15d2de091"),
                    CairoFont.WhiteSmallText(), 470, enforcementTabBounds.FlatCopy(), "tip-anticheat-enforcement")
                .AddSuiteButton(SuiteLocalization.Get("gui.settings"), () => SelectAntiCheatView("settings"), settingsTabBounds, antiCheatView == "settings")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.b2735f8bd0"),
                    CairoFont.WhiteSmallText(), 470, settingsTabBounds.FlatCopy(), "tip-anticheat-settings")
                .AddSuiteButton(SuiteLocalization.Get("gui.packetprotection"), () => SelectAntiCheatView("packet"), packetTabBounds, antiCheatView == "packet")
                .AddHoverText(SuiteLocalization.Get("gui.packetprotectiontip"),
                    CairoFont.WhiteSmallText(), 470, packetTabBounds.FlatCopy(), "tip-anticheat-packet")
                .AddSuiteButton(SuiteLocalization.Get("gui.playerdetail"), () => SelectAntiCheatView("player"), playerTabBounds, antiCheatView == "player")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.8db2d2e264"),
                    CairoFont.WhiteSmallText(), 440, playerTabBounds.FlatCopy(), "tip-anticheat-player");

            if (antiCheatView == "enforcement")
            {
                const double panelY = 122;
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, panelY, contentWidth, 244))
                    .AddRichtext(SuiteLocalization.Get("gui.enforcementrules"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 8, 280, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.acrulesstagedhelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(320, panelY + 9, 680, 24))
                    .AddSuiteButton(SuiteLocalization.Get("gui.newrule"), NewAntiCheatEnforcementRule, ElementBounds.Fixed(1010, panelY + 6, 104, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => RequestAntiCheat("enforcement"), ElementBounds.Fixed(1126, panelY + 6, 100, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.saverules"), SaveAntiCheatEnforcementRules, ElementBounds.Fixed(1238, panelY + 6, 100, 30))

                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(34, panelY + 46, 1248, 154)))
                    .AddStaticText(SuiteLocalization.Get("gui.ruleeditor"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, panelY + 56, 130, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.name"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, panelY + 88, 44, 22))
                    .AddTextInput(ElementBounds.Fixed(100, panelY + 82, 300, 28), v => acRuleName = v ?? "", CairoFont.WhiteSmallText(), "ac-rule-name")
                    .AddStaticText(SuiteLocalization.Get("gui.enabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(418, panelY + 88, 62, 22))
                    .AddSwitch(v => { if (!applyingAntiCheatEnforcementControls) acRuleEnabled = v; }, ElementBounds.Fixed(486, panelY + 82, 52, 26), "ac-rule-enabled")
                    .AddStaticText(SuiteLocalization.Get("gui.literal.d3f06a581b"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550, panelY + 88, 76, 22))
                    .AddDropDown(new[] { "FindingCode", SuiteLocalization.Get("gui.statusplain"), "RiskAtLeast" }, new[] { "Finding code", "Player status", "Risk at least" },
                        AntiCheatRuleTriggerIndex(), OnAntiCheatRuleTriggerChanged, ElementBounds.Fixed(630, panelY + 82, 150, 28), CairoFont.WhiteSmallText(), "ac-rule-trigger")
                    .AddStaticText(SuiteLocalization.Get("gui.literal.8dce170de2"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(794, panelY + 88, 42, 22))
                    .AddTextInput(ElementBounds.Fixed(840, panelY + 82, 172, 28), v => acRuleTriggerValue = v ?? "", CairoFont.WhiteSmallText(), "ac-rule-trigger-value")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.aca1997b5f"),
                        CairoFont.WhiteSmallText(), 520, ElementBounds.Fixed(840, panelY + 82, 172, 28), "tip-ac-rule-trigger-value")
                    .AddStaticText(SuiteLocalization.Get("gui.subject"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(1030, panelY + 88, 56, 22))
                    .AddTextInput(ElementBounds.Fixed(1090, panelY + 82, 170, 28), v => acRuleSubject = v ?? "", CairoFont.WhiteSmallText(), "ac-rule-subject")

                    .AddStaticText(SuiteLocalization.Get("gui.action"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, panelY + 124, 48, 22))
                    .AddDropDown(new[] { "Log", "Kick", "TempBan", "Freeze" }, new[] { "Log only", "Kick", "Temporary ban", "Freeze" },
                        AntiCheatRuleActionIndex(), OnAntiCheatRuleActionChanged, ElementBounds.Fixed(104, panelY + 118, 156, 28), CairoFont.WhiteSmallText(), "ac-rule-action")
                    .AddStaticText(SuiteLocalization.Get("gui.duration"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(278, panelY + 124, 62, 22))
                    .AddTextInput(ElementBounds.Fixed(344, panelY + 118, 100, 28), v => acRuleDuration = v ?? "", CairoFont.WhiteSmallText(), "ac-rule-duration")
                    .AddStaticText(SuiteLocalization.Get("gui.message"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(462, panelY + 124, 72, 22))
                    .AddTextInput(ElementBounds.Fixed(540, panelY + 118, 720, 28), v => acRuleMessage = v ?? "", CairoFont.WhiteSmallText(), "ac-rule-message")
                    .AddHoverText(SuiteLocalization.Get("gui.acmessageplaceholders"),
                        CairoFont.WhiteSmallText(), 460, ElementBounds.Fixed(540, panelY + 118, 720, 28), "tip-ac-rule-message")

                    .AddSuiteButton(SuiteLocalization.Get("gui.literal.ff2d081ab2"), UpdateAntiCheatEnforcementRule, ElementBounds.Fixed(52, panelY + 158, 138, 28))
                    .AddSuiteButton(SuiteLocalization.Get("gui.deleteselected"), DeleteAntiCheatEnforcementRule, ElementBounds.Fixed(202, panelY + 158, 132, 28))
                    .AddStaticText(SuiteLocalization.Get("gui.acapplycurrentsessions"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(360, panelY + 163, 350, 22))
                    .AddSwitch(v => { if (!applyingAntiCheatEnforcementControls) acRuleApplyCurrent = v; }, ElementBounds.Fixed(716, panelY + 157, 52, 26), "ac-rule-apply-current")
                    .AddRichtext(SuiteLocalization.Get("gui.acautomaticwarning"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(790, panelY + 161, 470, 38))
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 338, 1100, 26), "page-header");

                resultsY = 374;
                resultsHeight = 438;
            }
            else if (antiCheatView == "settings")
            {
                useGenericResults = false;
                BuildAntiCheatClientSettings(composer);
            }
            else if (antiCheatView == "packet")
            {
                useGenericResults = false;
                const double panelY = 122;
                var ingressFont = CairoFont.WhiteSmallText();
                ingressFont.UnscaledFontsize = 13;
                composer
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(34, panelY + 2, 700, 26), "page-header")
                    .AddRichtext(BuildPacketIngressStatusText(), ingressFont, ElementBounds.Fixed(760, panelY + 5, 256, 24))
                    .AddHoverText(SuiteLocalization.Get("gui.workspace.ingressdetail", acPacketIngressStatus), CairoFont.WhiteSmallText(), 520,
                        ElementBounds.Fixed(760, panelY + 2, 256, 28), "tip-packet-ingress-status")
                    .AddSuiteButton(SuiteLocalization.Get("gui.savepacket"), SaveAntiCheatPacketProtectionSettings, ElementBounds.Fixed(1030, panelY, 132, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.reload"), () => RequestAntiCheat("packet"), ElementBounds.Fixed(1176, panelY, 112, 30))
                    .AddRichtext(SuiteLocalization.Get("gui.packetprotectionhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 34, 1180, 34))
                    .AddSuiteButton(SuiteLocalization.Get("gui.packetgeneral"), () => SelectAntiCheatPacketView("general"), ElementBounds.Fixed(34, 174, 130, 30), antiCheatPacketView == "general")
                    .AddSuiteButton(SuiteLocalization.Get("gui.packetmovement"), () => SelectAntiCheatPacketView("movement"), ElementBounds.Fixed(176, 174, 140, 30), antiCheatPacketView == "movement")
                    .AddSuiteButton(SuiteLocalization.Get("gui.packetreach"), () => SelectAntiCheatPacketView("reach"), ElementBounds.Fixed(328, 174, 120, 30), antiCheatPacketView == "reach")
                    .AddSuiteButton(SuiteLocalization.Get("gui.packetminingtps"), () => SelectAntiCheatPacketView("mining"), ElementBounds.Fixed(460, 174, 150, 30), antiCheatPacketView == "mining")
                    .AddSuiteButton(SuiteLocalization.Get("gui.packetrisk"), () => SelectAntiCheatPacketView("risk"), ElementBounds.Fixed(622, 174, 130, 30), antiCheatPacketView == "risk")
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(28, 216, 622, 526)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(670, 216, 622, 526)));

                if (antiCheatPacketView == "general")
                {
                    composer
                        .AddStaticText(SuiteLocalization.Get("gui.packetfeedbackcollision"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48, 230, 340, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.packetguardmaster"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 270, 300, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("master", v), ElementBounds.Fixed(576, 264, 52, 26), "ac-pkt-master")
                        .AddRichtext(SuiteLocalization.Get("gui.playerchatwarnings"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 310, 300, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("warnings", v), ElementBounds.Fixed(576, 304, 52, 26), "ac-pkt-warnings")
                        .AddRichtext(SuiteLocalization.Get("gui.warningcooldown"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 350, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 344, 96, 28), v => SetAntiCheatPacketInt("warning-cooldown", v), CairoFont.WhiteSmallText(), "ac-pkt-warning-cooldown")
                        .AddRichtext(SuiteLocalization.Get("gui.noclipcollision"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 402, 320, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("noclip", v), ElementBounds.Fixed(576, 396, 52, 26), "ac-pkt-noclip")
                        .AddHoverText(SuiteLocalization.Get("gui.noclipwarningtip"), CairoFont.WhiteSmallText(), 500, ElementBounds.Fixed(52, 396, 430, 30), "tip-ac-pkt-noclip")
                        .AddRichtext(SuiteLocalization.Get("gui.collisionsamples"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 442, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 436, 96, 28), v => SetAntiCheatPacketInt("noclip-samples", v), CairoFont.WhiteSmallText(), "ac-pkt-noclip-samples")
                        .AddRichtext(SuiteLocalization.Get("gui.penetrationdepth"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 482, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 476, 96, 28), v => SetAntiCheatPacketDouble("noclip-penetration", v), CairoFont.WhiteSmallText(), "ac-pkt-noclip-penetration")
                        .AddRichtext(SuiteLocalization.Get("gui.endpointmovement"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 522, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 516, 96, 28), v => SetAntiCheatPacketDouble("noclip-endpoint", v), CairoFont.WhiteSmallText(), "ac-pkt-noclip-endpoint")

                        .AddStaticText(SuiteLocalization.Get("gui.packetfphardening"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690, 230, 340, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.accelsampling"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 270, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 264, 96, 28), v => SetAntiCheatPacketInt("accel-sample", v), CairoFont.WhiteSmallText(), "ac-pkt-accel-sample")
                        .AddRichtext(SuiteLocalization.Get("gui.accelsmoothing"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 310, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 304, 96, 28), v => SetAntiCheatPacketDouble("accel-smoothing", v), CairoFont.WhiteSmallText(), "ac-pkt-accel-smoothing")
                        .AddRichtext(SuiteLocalization.Get("gui.mountaccelratio"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 350, 330, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 344, 96, 28), v => SetAntiCheatPacketDouble("mount-ratio", v), CairoFont.WhiteSmallText(), "ac-pkt-mount-ratio")
                        .AddStaticText(SuiteLocalization.Get("gui.packetdiagnostics"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690, 402, 340, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.diagnosticcontext"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 442, 300, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("diag-context", v), ElementBounds.Fixed(1214, 436, 52, 26), "ac-pkt-diag-context")
                        .AddRichtext(SuiteLocalization.Get("gui.suppressiontelemetry"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 482, 320, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("diag-suppression", v), ElementBounds.Fixed(1214, 476, 52, 26), "ac-pkt-diag-suppression")
                        .AddRichtext(SuiteLocalization.Get("gui.speedhistorysamples"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 522, 320, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 516, 96, 28), v => SetAntiCheatPacketInt("diag-history", v), CairoFont.WhiteSmallText(), "ac-pkt-diag-history")
                        .AddRichtext(SuiteLocalization.Get("gui.packetadvancednowingame"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 574, 550, 54));
                }
                else if (antiCheatPacketView == "movement")
                {
                    composer
                        .AddStaticText(SuiteLocalization.Get("gui.packetmovementvalidation"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48, 230, 360, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.movementvalidator"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 270, 300, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("movement", v), ElementBounds.Fixed(576, 264, 52, 26), "ac-pkt-movement")
                        .AddRichtext(SuiteLocalization.Get("gui.movementsamplems"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 310, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 304, 96, 28), v => SetAntiCheatPacketInt("movement-sample", v), CairoFont.WhiteSmallText(), "ac-pkt-movement-sample")
                        .AddRichtext(SuiteLocalization.Get("gui.hardmovedistance"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 350, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 344, 96, 28), v => SetAntiCheatPacketDouble("hard-move", v), CairoFont.WhiteSmallText(), "ac-pkt-hard-move")
                        .AddRichtext(SuiteLocalization.Get("gui.movementconfirmsamples"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 390, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 384, 96, 28), v => SetAntiCheatPacketInt("movement-confirm-samples", v), CairoFont.WhiteSmallText(), "ac-pkt-movement-confirm-samples")
                        .AddRichtext(SuiteLocalization.Get("gui.movementconfirmwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 430, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 424, 96, 28), v => SetAntiCheatPacketInt("movement-confirm-window", v), CairoFont.WhiteSmallText(), "ac-pkt-movement-confirm-window")
                        .AddRichtext(SuiteLocalization.Get("gui.extrememultiplier"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 470, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 464, 96, 28), v => SetAntiCheatPacketDouble("movement-extreme", v), CairoFont.WhiteSmallText(), "ac-pkt-movement-extreme")
                        .AddRichtext(SuiteLocalization.Get("gui.correctiongrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 510, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 504, 96, 28), v => SetAntiCheatPacketInt("correction-grace", v), CairoFont.WhiteSmallText(), "ac-pkt-correction-grace")
                        .AddRichtext(SuiteLocalization.Get("gui.rejectunauthorized"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 550, 360, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("reject-move", v), ElementBounds.Fixed(576, 544, 52, 26), "ac-pkt-reject-move")
                        .AddRichtext(SuiteLocalization.Get("gui.exemptadmins"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 590, 300, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("exempt-admins", v), ElementBounds.Fixed(576, 584, 52, 26), "ac-pkt-exempt-admins")

                        .AddStaticText(SuiteLocalization.Get("gui.packetmountteleport"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690, 230, 360, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.exemptmounted"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 270, 320, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("exempt-mounted", v), ElementBounds.Fixed(1214, 264, 52, 26), "ac-pkt-exempt-mounted")
                        .AddRichtext(SuiteLocalization.Get("gui.mountgrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 310, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 304, 96, 28), v => SetAntiCheatPacketInt("mount-grace", v), CairoFont.WhiteSmallText(), "ac-pkt-mount-grace")
                        .AddRichtext(SuiteLocalization.Get("gui.engineteleportgrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 350, 330, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 344, 96, 28), v => SetAntiCheatPacketInt("engine-teleport-grace", v), CairoFont.WhiteSmallText(), "ac-pkt-engine-teleport-grace")
                        .AddRichtext(SuiteLocalization.Get("gui.mountaccelratio"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 390, 330, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 384, 96, 28), v => SetAntiCheatPacketDouble("mount-ratio", v), CairoFont.WhiteSmallText(), "ac-pkt-mount-ratio")
                        .AddRichtext(SuiteLocalization.Get("gui.tprunescompat"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 430, 320, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("tprunes", v), ElementBounds.Fixed(1214, 424, 52, 26), "ac-pkt-tprunes")
                        .AddRichtext(SuiteLocalization.Get("gui.tprunesgrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 470, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 464, 96, 28), v => SetAntiCheatPacketInt("tprunes-grace", v), CairoFont.WhiteSmallText(), "ac-pkt-tprunes-grace")
                        .AddRichtext(acPacketTPRunesDetected ? SuiteLocalization.Get("gui.tprunesdetected") : SuiteLocalization.Get("gui.tprunesnotdetected"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 510, 430, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.th3spawncompat"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 550, 320, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("th3spawn", v), ElementBounds.Fixed(1214, 544, 52, 26), "ac-pkt-th3spawn")
                        .AddRichtext(SuiteLocalization.Get("gui.th3spawngrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 590, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 584, 96, 28), v => SetAntiCheatPacketInt("th3spawn-grace", v), CairoFont.WhiteSmallText(), "ac-pkt-th3spawn-grace")
                        .AddRichtext(acTh3EssentialsDetected ? SuiteLocalization.Get("gui.th3detected") : SuiteLocalization.Get("gui.th3notdetected"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 630, 430, 22));
                }
                else if (antiCheatPacketView == "reach")
                {
                    composer
                        .AddStaticText(SuiteLocalization.Get("gui.packetreachvalidation"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48, 230, 360, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.reachvalidator"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 270, 300, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("reach", v), ElementBounds.Fixed(576, 264, 52, 26), "ac-pkt-reach")
                        .AddRichtext(SuiteLocalization.Get("gui.reachtolerance"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 310, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 304, 96, 28), v => SetAntiCheatPacketDouble("reach-tolerance", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-tolerance")
                        .AddRichtext(SuiteLocalization.Get("gui.reachlatencycomp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 350, 340, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("reach-latency", v), ElementBounds.Fixed(576, 344, 52, 26), "ac-pkt-reach-latency")
                        .AddRichtext(SuiteLocalization.Get("gui.reachmaxlatency"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 390, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 384, 96, 28), v => SetAntiCheatPacketDouble("reach-max-latency", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-max-latency")
                        .AddRichtext(SuiteLocalization.Get("gui.reachhistory"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 430, 340, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("reach-history", v), ElementBounds.Fixed(576, 424, 52, 26), "ac-pkt-reach-history")
                        .AddRichtext(SuiteLocalization.Get("gui.reachhistoryms"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 470, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 464, 96, 28), v => SetAntiCheatPacketInt("reach-history-ms", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-history-ms")
                        .AddRichtext(SuiteLocalization.Get("gui.reachrewindms"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 510, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 504, 96, 28), v => SetAntiCheatPacketInt("reach-rewind-ms", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-rewind-ms")

                        .AddStaticText(SuiteLocalization.Get("gui.packetreachthresholds"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690, 230, 360, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.reachusetolerance"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 270, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 264, 96, 28), v => SetAntiCheatPacketDouble("reach-use-extra", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-use-extra")
                        .AddRichtext(SuiteLocalization.Get("gui.reachbreaktolerance"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 310, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 304, 96, 28), v => SetAntiCheatPacketDouble("reach-break-extra", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-break-extra")
                        .AddRichtext(SuiteLocalization.Get("gui.reachnearstrikes"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 350, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 344, 96, 28), v => SetAntiCheatPacketInt("reach-strikes", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-strikes")
                        .AddRichtext(SuiteLocalization.Get("gui.reachstrikewindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 390, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 384, 96, 28), v => SetAntiCheatPacketInt("reach-strike-window", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-strike-window")
                        .AddRichtext(SuiteLocalization.Get("gui.reachstrikedecay"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 430, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 424, 96, 28), v => SetAntiCheatPacketInt("reach-strike-decay", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-strike-decay")
                        .AddRichtext(SuiteLocalization.Get("gui.hardexcess"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 470, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 464, 96, 28), v => SetAntiCheatPacketDouble("reach-hard", v), CairoFont.WhiteSmallText(), "ac-pkt-reach-hard")
                        .AddRichtext(SuiteLocalization.Get("gui.rejectreach"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 510, 340, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("reject-reach", v), ElementBounds.Fixed(1214, 504, 52, 26), "ac-pkt-reject-reach")
                        .AddRichtext(SuiteLocalization.Get("gui.reachthresholdhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 562, 550, 54));
                }
                else if (antiCheatPacketView == "mining")
                {
                    composer
                        .AddStaticText(SuiteLocalization.Get("gui.packetminingvalidation"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48, 230, 360, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.miningvalidator"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 270, 300, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("mining", v), ElementBounds.Fixed(576, 264, 52, 26), "ac-pkt-mining")
                        .AddRichtext(SuiteLocalization.Get("gui.veinwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 310, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 304, 96, 28), v => SetAntiCheatPacketInt("vein-window", v), CairoFont.WhiteSmallText(), "ac-pkt-vein-window")
                        .AddRichtext(SuiteLocalization.Get("gui.connectedbreaklimit"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 350, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 344, 96, 28), v => SetAntiCheatPacketInt("connected-break-limit", v), CairoFont.WhiteSmallText(), "ac-pkt-connected-break-limit")
                        .AddRichtext(SuiteLocalization.Get("gui.duplicatetargetwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 390, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 384, 96, 28), v => SetAntiCheatPacketInt("duplicate-target", v), CairoFont.WhiteSmallText(), "ac-pkt-duplicate-target")
                        .AddRichtext(SuiteLocalization.Get("gui.rejectbursts"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 430, 340, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("reject-bursts", v), ElementBounds.Fixed(576, 424, 52, 26), "ac-pkt-reject-bursts")
                        .AddRichtext(SuiteLocalization.Get("gui.miningcadence"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 470, 340, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("mining-cadence", v), ElementBounds.Fixed(576, 464, 52, 26), "ac-pkt-mining-cadence")
                        .AddRichtext(SuiteLocalization.Get("gui.cadenceminexpected"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 510, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 504, 96, 28), v => SetAntiCheatPacketDouble("cadence-min", v), CairoFont.WhiteSmallText(), "ac-pkt-cadence-min")
                        .AddRichtext(SuiteLocalization.Get("gui.suspiciousratio"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 550, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 544, 96, 28), v => SetAntiCheatPacketDouble("mining-ratio", v), CairoFont.WhiteSmallText(), "ac-pkt-mining-ratio")
                        .AddRichtext(SuiteLocalization.Get("gui.confirmations"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 590, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 584, 96, 28), v => SetAntiCheatPacketInt("mining-confirmations", v), CairoFont.WhiteSmallText(), "ac-pkt-mining-confirmations")
                        .AddRichtext(SuiteLocalization.Get("gui.cadenceconfirmwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 630, 340, 22))
                        .AddNumberInput(ElementBounds.Fixed(532, 624, 96, 28), v => SetAntiCheatPacketInt("cadence-confirm-window", v), CairoFont.WhiteSmallText(), "ac-pkt-cadence-confirm-window")
                        .AddRichtext(SuiteLocalization.Get("gui.rejectcadence"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 670, 340, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("reject-cadence", v), ElementBounds.Fixed(576, 664, 52, 26), "ac-pkt-reject-cadence")

                        .AddStaticText(SuiteLocalization.Get("gui.packettpsawareness"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690, 230, 360, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.tpsaware"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 270, 300, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("tps", v), ElementBounds.Fixed(1214, 264, 52, 26), "ac-pkt-tps")
                        .AddRichtext(SuiteLocalization.Get("gui.degradedpct"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 310, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 304, 96, 28), v => SetAntiCheatPacketDouble("tps-degraded", v), CairoFont.WhiteSmallText(), "ac-pkt-tps-degraded")
                        .AddRichtext(SuiteLocalization.Get("gui.criticalpct"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 350, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 344, 96, 28), v => SetAntiCheatPacketDouble("tps-critical", v), CairoFont.WhiteSmallText(), "ac-pkt-tps-critical")
                        .AddRichtext(SuiteLocalization.Get("gui.maxmultiplier"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 390, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 384, 96, 28), v => SetAntiCheatPacketDouble("tps-max", v), CairoFont.WhiteSmallText(), "ac-pkt-tps-max")
                        .AddRichtext(SuiteLocalization.Get("gui.suppresscriticalrejects"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 430, 360, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("tps-suppress", v), ElementBounds.Fixed(1214, 424, 52, 26), "ac-pkt-tps-suppress")
                        .AddStaticText(SuiteLocalization.Get("gui.tpslivestatus"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690, 486, 360, 22))
                        .AddRichtext(BuildPacketTpsStatusText(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 524, 550, 100));
                }
                else
                {
                    composer
                        .AddStaticText(SuiteLocalization.Get("gui.packetfamilyrisk"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48, 230, 360, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.perfamilyriskenabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 270, 300, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("family-risk", v), ElementBounds.Fixed(576, 264, 52, 26), "ac-pkt-family-risk")
                        .AddStaticText(SuiteLocalization.Get("gui.riskfamily"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 318, 140, 22))
                        .AddStaticText(SuiteLocalization.Get("gui.decayseconds"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(254, 318, 84, 22))
                        .AddStaticText(SuiteLocalization.Get("gui.reviewplain"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(388, 318, 72, 22))
                        .AddStaticText(SuiteLocalization.Get("gui.suspiciousplain"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(510, 318, 96, 22))
                        .AddStaticText(SuiteLocalization.Get("gui.movement"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 356, 180, 22))
                        .AddNumberInput(ElementBounds.Fixed(248, 350, 86, 28), v => SetAntiCheatPacketInt("risk-move-decay", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-move-decay")
                        .AddNumberInput(ElementBounds.Fixed(382, 350, 76, 28), v => SetAntiCheatPacketInt("risk-move-review", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-move-review")
                        .AddNumberInput(ElementBounds.Fixed(516, 350, 76, 28), v => SetAntiCheatPacketInt("risk-move-suspicious", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-move-suspicious")
                        .AddStaticText(SuiteLocalization.Get("gui.reach"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 398, 180, 22))
                        .AddNumberInput(ElementBounds.Fixed(248, 392, 86, 28), v => SetAntiCheatPacketInt("risk-reach-decay", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-reach-decay")
                        .AddNumberInput(ElementBounds.Fixed(382, 392, 76, 28), v => SetAntiCheatPacketInt("risk-reach-review", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-reach-review")
                        .AddNumberInput(ElementBounds.Fixed(516, 392, 76, 28), v => SetAntiCheatPacketInt("risk-reach-suspicious", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-reach-suspicious")
                        .AddStaticText(SuiteLocalization.Get("gui.mining"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 440, 180, 22))
                        .AddNumberInput(ElementBounds.Fixed(248, 434, 86, 28), v => SetAntiCheatPacketInt("risk-mining-decay", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-mining-decay")
                        .AddNumberInput(ElementBounds.Fixed(382, 434, 76, 28), v => SetAntiCheatPacketInt("risk-mining-review", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-mining-review")
                        .AddNumberInput(ElementBounds.Fixed(516, 434, 76, 28), v => SetAntiCheatPacketInt("risk-mining-suspicious", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-mining-suspicious")
                        .AddStaticText(SuiteLocalization.Get("gui.packetabuse"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 482, 180, 22))
                        .AddNumberInput(ElementBounds.Fixed(248, 476, 86, 28), v => SetAntiCheatPacketInt("risk-packet-decay", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-packet-decay")
                        .AddNumberInput(ElementBounds.Fixed(382, 476, 76, 28), v => SetAntiCheatPacketInt("risk-packet-review", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-packet-review")
                        .AddNumberInput(ElementBounds.Fixed(516, 476, 76, 28), v => SetAntiCheatPacketInt("risk-packet-suspicious", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-packet-suspicious")
                        .AddStaticText(SuiteLocalization.Get("gui.clientintegrity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 524, 180, 22))
                        .AddNumberInput(ElementBounds.Fixed(248, 518, 86, 28), v => SetAntiCheatPacketInt("risk-client-decay", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-client-decay")
                        .AddNumberInput(ElementBounds.Fixed(382, 518, 76, 28), v => SetAntiCheatPacketInt("risk-client-review", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-client-review")
                        .AddNumberInput(ElementBounds.Fixed(516, 518, 76, 28), v => SetAntiCheatPacketInt("risk-client-suspicious", v), CairoFont.WhiteSmallText(), "ac-pkt-risk-client-suspicious")
                        .AddRichtext(SuiteLocalization.Get("gui.familyriskhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, 574, 550, 74))

                        .AddStaticText(SuiteLocalization.Get("gui.packetcorrelationhistory"), CairoFont.WhiteSmallText().WithColor(new[] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690, 230, 420, 22))
                        .AddRichtext(SuiteLocalization.Get("gui.incidentcorrelation"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 270, 330, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("correlation", v), ElementBounds.Fixed(1214, 264, 52, 26), "ac-pkt-correlation")
                        .AddRichtext(SuiteLocalization.Get("gui.incidentwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 310, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 304, 96, 28), v => SetAntiCheatPacketInt("incident-window", v), CairoFont.WhiteSmallText(), "ac-pkt-incident-window")
                        .AddRichtext(SuiteLocalization.Get("gui.requiredcategories"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 350, 320, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 344, 96, 28), v => SetAntiCheatPacketInt("incident-units", v), CairoFont.WhiteSmallText(), "ac-pkt-incident-units")
                        .AddRichtext(SuiteLocalization.Get("gui.maxriskbonus"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 390, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 384, 96, 28), v => SetAntiCheatPacketInt("incident-bonus", v), CairoFont.WhiteSmallText(), "ac-pkt-incident-bonus")
                        .AddRichtext(SuiteLocalization.Get("gui.persistentreporthistory"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 442, 330, 22))
                        .AddSwitch(v => SetAntiCheatPacketBool("history", v), ElementBounds.Fixed(1214, 436, 52, 26), "ac-pkt-history")
                        .AddRichtext(SuiteLocalization.Get("gui.retentiondays"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 482, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 476, 96, 28), v => SetAntiCheatPacketInt("history-days", v), CairoFont.WhiteSmallText(), "ac-pkt-history-days")
                        .AddRichtext(SuiteLocalization.Get("gui.maxrecords"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 522, 300, 22))
                        .AddNumberInput(ElementBounds.Fixed(1170, 516, 96, 28), v => SetAntiCheatPacketInt("history-max", v), CairoFont.WhiteSmallText(), "ac-pkt-history-max")
                        .AddRichtext(SuiteLocalization.Get("gui.correlationriskhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, 574, 550, 74));
                }

                resultsY = 0;
                resultsHeight = 0;
            }
            else if (antiCheatView == "player")
            {
                const double panelY = 122;
                composer
                    .AddRichtext(SuiteLocalization.Get("gui.playerinvestigation"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 7, 260, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 39, 58, 24))
                    .AddTextInput(ElementBounds.Fixed(96, panelY + 33, 330, 30),
                        value => antiCheatPlayerSearch = value ?? "", CairoFont.WhiteSmallText(), "anticheat-player-search")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.28c348c602"),
                        CairoFont.WhiteSmallText(), 330, ElementBounds.Fixed(96, panelY + 33, 330, 30), "tip-anticheat-player-search")
                    .AddSuiteButton(SuiteLocalization.Get("gui.load"), LoadAntiCheatPlayer, ElementBounds.Fixed(442, panelY + 33, 100, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.deeprescan"), RescanAntiCheatPlayer, ElementBounds.Fixed(556, panelY + 33, 140, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.e95320215e"),
                        CairoFont.WhiteSmallText(), 440, ElementBounds.Fixed(556, panelY + 33, 140, 30), "tip-anticheat-player-rescan")
                    .AddSuiteButton(SuiteLocalization.Get("gui.backtoplayers"), () => SelectAntiCheatView("players"), ElementBounds.Fixed(714, panelY + 33, 150, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.031c7cf4b7"),
                        CairoFont.WhiteSmallText(), 330, ElementBounds.Fixed(714, panelY + 33, 150, 30), "tip-anticheat-player-back")
                    .AddRichtext(SuiteLocalization.Get("gui.acsummaryhelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 73, 1090, 28))
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 226, 1100, 26), "page-header")

                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(28, 254, 306, 142)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(346, 254, 306, 142)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(664, 254, 306, 142)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(982, 254, 310, 142)))
                    .AddRichtext(BuildAntiCheatIdentityCard(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, 268, 266, 116))
                    .AddRichtext(BuildAntiCheatRiskCard(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(366, 268, 266, 116))
                    .AddRichtext(BuildAntiCheatClientCard(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, 268, 266, 116))
                    .AddRichtext(BuildAntiCheatRuntimeCard(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(1002, 268, 270, 116))
                    .AddRichtext(SuiteLocalization.Get("gui.evidencetimeline"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, 410, 360, 24));

                resultsY = 438;
                resultsHeight = 366;
            }
            else if (antiCheatView == "reports")
            {
                const double panelY = 122;
                composer
                    .AddRichtext(SuiteLocalization.Get("gui.persistentincidents"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 7, 330, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.playerfilter"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 39, 92, 24))
                    .AddTextInput(ElementBounds.Fixed(132, panelY + 33, 300, 30),
                        value => antiCheatReportSearch = value ?? "", CairoFont.WhiteSmallText(), "anticheat-report-search")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.3e23cb28f4"),
                        CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(132, panelY + 33, 300, 30), "tip-anticheat-report-search")
                    .AddSuiteButton(SuiteLocalization.Get("gui.loadreports"), LoadAntiCheatReports, ElementBounds.Fixed(448, panelY + 33, 132, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), LoadAntiCheatReports, ElementBounds.Fixed(594, panelY + 33, 108, 30))
                    .AddRichtext(SuiteLocalization.Get("gui.reportpersistencehelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 73, 1060, 28))
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 258, 980, 26), "page-header");

                resultsY = 292;
                resultsHeight = 520;
            }
            else if (antiCheatView == "blacklist")
            {
                const double panelY = 122;
                composer
                    .AddRichtext(SuiteLocalization.Get("gui.clientmodblacklist"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, panelY + 7, 300, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.literal.6364065a59"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 39, 58, 24))
                    .AddTextInput(ElementBounds.Fixed(96, panelY + 33, 300, 30),
                        value => antiCheatBlacklistModId = value ?? "", CairoFont.WhiteSmallText(), "anticheat-blacklist-mod")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.8b9d6c1c0f"),
                        CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(96, panelY + 33, 300, 30), "tip-anticheat-blacklist-mod")
                    .AddRichtext(SuiteLocalization.Get("gui.sourceplayer"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(418, panelY + 39, 118, 24))
                    .AddTextInput(ElementBounds.Fixed(540, panelY + 33, 238, 30),
                        value => antiCheatBlacklistSourcePlayer = value ?? "", CairoFont.WhiteSmallText(), "anticheat-blacklist-source")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.3e5fc090d6"),
                        CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(540, panelY + 33, 238, 30), "tip-anticheat-blacklist-source")
                    .AddSuiteButton(SuiteLocalization.Get("gui.add"), AddAntiCheatBlacklist, ElementBounds.Fixed(794, panelY + 33, 90, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.remove"), RemoveAntiCheatBlacklist, ElementBounds.Fixed(898, panelY + 33, 100, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), LoadAntiCheatBlacklist, ElementBounds.Fixed(1012, panelY + 33, 110, 30))
                    .AddRichtext("<font color=\"#ffd166\"><strong>Controlserver required.</strong></font> <font color=\"#91a0ae\">Core/required mods are protected from accidental blacklist changes. Existing accepted scans are re-evaluated immediately.</font>",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 73, 1085, 28))
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 258, 980, 26), "page-header");

                resultsY = 292;
                resultsHeight = 520;
            }
            else
            {
                composer
                    .AddRichtext($"<font color=\"#7db6ff\"><strong>{pageHeaderText}</strong></font>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 132, 700, 26), "page-header")
                    .AddRichtext(SuiteLocalization.Get("gui.protection"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(760, 134, 92, 24))
                    .AddSwitch(SetAntiCheatEnabled,
                        ElementBounds.Fixed(856, 128, 60, 30), "anticheat-enabled")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.37e73850bc"),
                        CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(856, 128, 60, 30), "tip-anticheat-enabled")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => RequestAntiCheat(antiCheatView),
                        ElementBounds.Fixed(1030, 128, 120, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.092e0a3272"),
                        CairoFont.WhiteSmallText(), 330, ElementBounds.Fixed(1030, 128, 120, 30), "tip-anticheat-refresh");

                if (antiCheatView == "players")
                {
                    composer.AddRichtext(
                        "<font color=\"#91a0ae\"><strong>Risk rating:</strong> 0-9 Low • 10-24 Guarded • 25-49 Elevated • 50-74 High • 75-100 Critical. Ratings explain the score; they do not change enforcement rules.</font>",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 161, 1080, 28));
                    resultsY = 194;
                    resultsHeight = 610;
                }
                else
                {
                    resultsY = 174;
                    resultsHeight = 546;
                }
            }
        }
        else if (section == "webserver")
        {
            var webPanelTabBounds = ElementBounds.Fixed(22, secondaryTabY, 122, 30);
            var webMapTabBounds = ElementBounds.Fixed(158, secondaryTabY, 122, 30);
            var discordTabBounds = ElementBounds.Fixed(294, secondaryTabY, 122, 30);

            composer
                .AddSuiteButton(SuiteLocalization.Get("gui.webpanel"), () => SelectWebView("webpanel"), webPanelTabBounds, webView == "webpanel")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.dd066c8356"),
                    CairoFont.WhiteSmallText(), 410, webPanelTabBounds.FlatCopy(), "tip-web-tab-panel")
                .AddSuiteButton(SuiteLocalization.Get("gui.webmap"), () => SelectWebView("webmap"), webMapTabBounds, webView == "webmap")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.affb85d245"),
                    CairoFont.WhiteSmallText(), 440, webMapTabBounds.FlatCopy(), "tip-web-tab-map");

            if (discordAvailable)
            {
                composer
                    .AddSuiteButton(SuiteLocalization.Get("gui.discord"), () => SelectWebView("discord"), discordTabBounds, webView == "discord")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.discord"), CairoFont.WhiteSmallText(), 440, discordTabBounds.FlatCopy(), "tip-web-tab-discord");
            }

            if (webView == "discord")
            {
                BuildDiscordIntegrationPage(composer);
                useGenericResults = false;
            }
            else if (webView == "webmap")
            {
                useGenericResults = false;
                const double sy = 122;
                const double leftX = 28;
                const double rightX = 670;
                const double cardW = 622;

                composer
                    .AddRichtext(SuiteLocalization.Get("gui.webmapconfig"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(34, sy + 8, 400, 26), "page-header")
                    .AddSuiteButton(SuiteLocalization.Get("gui.literal.e7ad076271"), SaveWebMapSettings, ElementBounds.Fixed(1030, sy + 4, 160, 30))
                    .AddSuiteButton(SuiteLocalization.Get("gui.reload"), () => RequestWebDashboard("map"), ElementBounds.Fixed(1202, sy + 4, 90, 30))
                    .AddStaticText(SuiteLocalization.Get("gui.mapsettingsstaged"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 36, 1040, 26))

                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(leftX, sy + 68, cardW, 252)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(rightX, sy + 68, cardW, 252)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(leftX, sy + 332, cardW, 250)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(rightX, sy + 332, cardW, 250)))

                    .AddStaticText(SuiteLocalization.Get("gui.hostidentity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, sy + 80, 220, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.mapruntime"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 112, 150, 22))
                    .AddSwitch(v => SetWebMapBool("enabled", v), ElementBounds.Fixed(576, sy + 106, 52, 26), "webmap-enabled")
                    .AddStaticText(SuiteLocalization.Get("gui.bindaddress"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 146, 120, 22))
                    .AddTextInput(ElementBounds.Fixed(180, sy + 140, 250, 28), v => webMapBindAddress = v ?? "", CairoFont.WhiteSmallText(), "webmap-bind")
                    .AddStaticText(SuiteLocalization.Get("gui.port"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(448, sy + 146, 52, 22))
                    .AddNumberInput(ElementBounds.Fixed(502, sy + 140, 126, 28), v => SetWebMapInt("port", v), CairoFont.WhiteSmallText(), "webmap-port")
                    .AddStaticText(SuiteLocalization.Get("gui.maptitle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 180, 110, 22))
                    .AddTextInput(ElementBounds.Fixed(180, sy + 174, 448, 28), v => webMapTitle = v ?? "", CairoFont.WhiteSmallText(), "webmap-title")
                    .AddStaticText(SuiteLocalization.Get("gui.publicurl"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 214, 110, 22))
                    .AddTextInput(ElementBounds.Fixed(180, sy + 208, 448, 28), v => webMapPublicUrl = v ?? "", CairoFont.WhiteSmallText(), "webmap-public-url")
                    .AddStaticText(SuiteLocalization.Get("gui.panelurl"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 248, 110, 22))
                    .AddTextInput(ElementBounds.Fixed(180, sy + 242, 448, 28), v => webMapDashboardUrl = v ?? "", CairoFont.WhiteSmallText(), "webmap-dashboard-url")
                    .AddStaticText(SuiteLocalization.Get(webMapRunning ? "gui.runningyes" : "gui.runningno"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 282, 170, 22))

                    .AddStaticText(SuiteLocalization.Get("gui.publiclayersprivacy"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(690, sy + 80, 260, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.publishplayers"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 112, 210, 22))
                    .AddSwitch(v => SetWebMapBool("players", v), ElementBounds.Fixed(1228, sy + 106, 52, 26), "webmap-players")
                    .AddStaticText(SuiteLocalization.Get("gui.waypoints"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 146, 180, 22))
                    .AddSwitch(v => SetWebMapBool("waypoints", v), ElementBounds.Fixed(1228, sy + 140, 52, 26), "webmap-waypoints")
                    .AddStaticText(SuiteLocalization.Get("gui.blockloggeractivity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 180, 210, 22))
                    .AddSwitch(v => SetWebMapBool("activity", v), ElementBounds.Fixed(1228, sy + 174, 52, 26), "webmap-activity")
                    .AddStaticText(SuiteLocalization.Get("gui.badblockentities"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 214, 210, 22))
                    .AddSwitch(v => SetWebMapBool("badbe", v), ElementBounds.Fixed(1228, sy + 208, 52, 26), "webmap-badbe")
                    .AddStaticText(SuiteLocalization.Get("gui.playerwaypointlimits"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 248, 180, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.major"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(900, sy + 248, 52, 22))
                    .AddNumberInput(ElementBounds.Fixed(952, sy + 242, 72, 28), v => SetWebMapInt("major", v), CairoFont.WhiteSmallText(), "webmap-major")
                    .AddStaticText(SuiteLocalization.Get("gui.minor"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(1042, sy + 248, 52, 22))
                    .AddNumberInput(ElementBounds.Fixed(1094, sy + 242, 72, 28), v => SetWebMapInt("minor", v), CairoFont.WhiteSmallText(), "webmap-minor")
                    .AddStaticText(SuiteLocalization.Get("gui.livecoordswarning"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 282, 500, 22))

                    .AddStaticText(SuiteLocalization.Get("gui.mapviewactivity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, sy + 344, 240, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.maxzoom"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 378, 220, 22))
                    .AddNumberInput(ElementBounds.Fixed(500, sy + 372, 128, 28), v => SetWebMapInt("maxzoom", v), CairoFont.WhiteSmallText(), "webmap-maxzoom")
                    .AddStaticText(SuiteLocalization.Get("gui.defaultspan"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 412, 220, 22))
                    .AddNumberInput(ElementBounds.Fixed(500, sy + 406, 128, 28), v => SetWebMapInt("defaultspan", v), CairoFont.WhiteSmallText(), "webmap-defaultspan")
                    .AddStaticText(SuiteLocalization.Get("gui.tilesize"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 446, 180, 22))
                    .AddNumberInput(ElementBounds.Fixed(500, sy + 440, 128, 28), v => SetWebMapInt("tilesize", v), CairoFont.WhiteSmallText(), "webmap-tilesize")
                    .AddStaticText(SuiteLocalization.Get("gui.activitywindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 480, 220, 22))
                    .AddNumberInput(ElementBounds.Fixed(500, sy + 474, 128, 28), v => SetWebMapInt("activitywindow", v), CairoFont.WhiteSmallText(), "webmap-activitywindow")
                    .AddStaticText(SuiteLocalization.Get("gui.maxmarkers"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 514, 220, 22))
                    .AddNumberInput(ElementBounds.Fixed(500, sy + 508, 128, 28), v => SetWebMapInt("activitymarkers", v), CairoFont.WhiteSmallText(), "webmap-activitymarkers")

                    .AddStaticText(SuiteLocalization.Get("gui.startupexport"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(690, sy + 344, 280, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.exportnext"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 378, 240, 22))
                    .AddSwitch(v => SetWebMapBool("exportnext", v), ElementBounds.Fixed(1228, sy + 372, 52, 26), "webmap-exportnext")
                    .AddStaticText(SuiteLocalization.Get("gui.autoexports"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 412, 240, 22))
                    .AddSwitch(v => SetWebMapBool("autoexport", v), ElementBounds.Fixed(1228, sy + 406, 52, 26), "webmap-autoexport")
                    .AddStaticText(SuiteLocalization.Get("gui.intervaldays"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 446, 140, 22))
                    .AddNumberInput(ElementBounds.Fixed(900, sy + 440, 92, 28), v => SetWebMapInt("exportdays", v), CairoFont.WhiteSmallText(), "webmap-exportdays")
                    .AddStaticText(SuiteLocalization.Get("gui.incrementalexport"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 480, 200, 22))
                    .AddSwitch(v => SetWebMapBool("incremental", v), ElementBounds.Fixed(1228, sy + 474, 52, 26), "webmap-incremental")
                    .AddStaticText(SuiteLocalization.Get("gui.lockexport"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 514, 240, 22))
                    .AddSwitch(v => SetWebMapBool("lockexport", v), ElementBounds.Fixed(1228, sy + 508, 52, 26), "webmap-lockexport")
                    .AddStaticText(webMapTerrainExportInProgress ? SuiteLocalization.Get("gui.runtime.terrainrunning") : SuiteLocalization.Get("gui.runtime.terrainidle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 548, 250, 22));

                if (!string.IsNullOrWhiteSpace(webMapLastError))
                {
                    composer.AddRichtext($"<font color=\"#ff7070\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.lastmaperror"))}</strong> {Escape(webMapLastError)}</font>",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(930, sy + 548, 350, 24));
                }

                resultsY = 0;
                resultsHeight = 0;
            }
            else
            {
                useGenericResults = false;
                const double sy = 122;
                const double leftX = 28;
                const double rightX = 670;
                const double cardW = 622;

                composer
                    .AddRichtext(SuiteLocalization.Get("gui.webpaneltitle"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(34, sy + 8, 400, 26), "page-header")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => RequestWebDashboard("panel"), ElementBounds.Fixed(1182, sy + 4, 110, 30))
                    .AddStaticText(SuiteLocalization.Get("gui.webpanelsettingshelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(450, sy + 10, 700, 24))

                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(leftX, sy + 50, cardW, 162)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(rightX, sy + 50, cardW, 162)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(leftX, sy + 224, cardW, 318)))
                    .AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(rightX, sy + 224, cardW, 318)))

                    .AddStaticText(SuiteLocalization.Get("gui.runtimeaccess"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, sy + 62, 230, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.webpanelenabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 96, 170, 22))
                    .AddSwitch(value => SetWebBool("enabled", value), ElementBounds.Fixed(576, sy + 90, 52, 26), "web-enabled")
                    .AddRichtext(
                        $"<strong>Runtime:</strong> {(webRunning ? "<font color=\"#8fda72\">RUNNING</font>" : "<font color=\"#ff9d66\">STOPPED</font>")}<br>" +
                        $"<strong>Authentication:</strong> {(webLocalAnonymousMode ? "<font color=\"#ffd166\">LOCAL / NO LOGIN</font>" : webPasswordConfigured ? "<font color=\"#8fda72\">PASSWORD</font>" : "<font color=\"#ff7070\">PASSWORD REQUIRED</font>")}<br>" +
                        $"<strong>Active sessions:</strong> {webActiveSessions}",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 126, 520, 74))

                    .AddStaticText(SuiteLocalization.Get("gui.listenerpublic"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(690, sy + 62, 280, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.bindaddress"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 96, 110, 22))
                    .AddTextInput(ElementBounds.Fixed(808, sy + 90, 250, 28), value => webBindAddress = value ?? "", CairoFont.WhiteSmallText(), "web-bind")
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebBind, ElementBounds.Fixed(1070, sy + 90, 84, 28))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.ebc02e7008"),
                        CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(808, sy + 90, 250, 28), "tip-web-bind")
                    .AddStaticText(SuiteLocalization.Get("gui.port"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 134, 70, 22))
                    .AddNumberInput(ElementBounds.Fixed(808, sy + 128, 110, 28), OnWebPortChanged, CairoFont.WhiteSmallText(), "web-port")
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebPort, ElementBounds.Fixed(930, sy + 128, 84, 28))
                    .AddStaticText(SuiteLocalization.Get("gui.publicurl"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 172, 90, 22))
                    .AddTextInput(ElementBounds.Fixed(808, sy + 166, 346, 28), value => webPublicUrl = value ?? "", CairoFont.WhiteSmallText(), "web-public-url")
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebPublicUrl, ElementBounds.Fixed(1166, sy + 166, 84, 28))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.5200fa9b50"),
                        CairoFont.WhiteSmallText(), 440, ElementBounds.Fixed(808, sy + 166, 346, 28), "tip-web-public-url")

                    .AddStaticText(SuiteLocalization.Get("gui.authsession"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48, sy + 236, 280, 22))
                    .AddStaticText(SuiteLocalization.Get("gui.adminusername"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 270, 130, 22))
                    .AddTextInput(ElementBounds.Fixed(184, sy + 264, 244, 28), value => webAdminUsername = value ?? "", CairoFont.WhiteSmallText(), "web-username")
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebUsername, ElementBounds.Fixed(440, sy + 264, 84, 28))
                    .AddStaticText(SuiteLocalization.Get("gui.sessiontimeout"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 308, 130, 22))
                    .AddNumberInput(ElementBounds.Fixed(184, sy + 302, 110, 28), OnWebSessionChanged, CairoFont.WhiteSmallText(), "web-session-minutes")
                    .AddStaticText(SuiteLocalization.Get("gui.minutes"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(302, sy + 308, 62, 22))
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebSessionTimeout, ElementBounds.Fixed(440, sy + 302, 84, 28))
                    .AddStaticText(SuiteLocalization.Get("gui.securecookies"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 346, 130, 22))
                    .AddSwitch(value => SetWebBool("securecookies", value), ElementBounds.Fixed(184, sy + 340, 52, 26), "web-secure-cookies")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.50061c51de"),
                        CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(184, sy + 340, 52, 26), "tip-web-secure")
                    .AddRichtext(SuiteLocalization.Get("gui.setreplacepassword"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 386, 520, 48))
                    .AddStaticText(
                        webLocalAnonymousMode
                            ? "Loopback-only anonymous mode is active. Set a password before binding to a LAN or public interface."
                            : "Password authentication is configured. Bootstrap Password in NewfiesWebServer.json is hashed and cleared after reload.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(52, sy + 444, 548, 70))

                    .AddStaticText(SuiteLocalization.Get("gui.endpointssecurity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(690, sy + 236, 260, 22))
                    .AddRichtext(SuiteLocalization.Get("gui.localendpoint"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 270, 150, 22))
                    .AddStaticText(webLocalUrl, CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 296, 560, 28))
                    .AddRichtext(SuiteLocalization.Get("gui.publicendpoint"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 338, 150, 22))
                    .AddStaticText(string.IsNullOrWhiteSpace(webPublicUrl) ? SuiteLocalization.Get("gui.notconfigured") : webPublicUrl,
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 364, 560, 42))
                    .AddRichtext(
                        webSecureCookies
                            ? "<font color=\"#8fda72\"><strong>HTTPS cookie protection enabled.</strong></font>"
                            : "<font color=\"#ffd166\"><strong>Secure cookies are off.</strong></font>",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 420, 520, 24))
                    .AddStaticText(SuiteLocalization.Get("gui.httpsecuritywarning"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 454, 548, 58));

                if (!string.IsNullOrWhiteSpace(webLastError))
                {
                    composer.AddRichtext($"<font color=\"#ff7070\"><strong>Last error:</strong> {Escape(webLastError)}</font>",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(694, sy + 520, 548, 22));
                }

                resultsY = 0;
                resultsHeight = 0;
            }
        }
        else if (section == "loginprotection")
        {
            var loginStatusTabBounds = ElementBounds.Fixed(22, secondaryTabY, 100, 30);
            var loginSettingsTabBounds = ElementBounds.Fixed(136, secondaryTabY, 110, 30);

            composer
                .AddSuiteButton(SuiteLocalization.Get("gui.statusplain"), () => SelectLoginProtectionView("status"), loginStatusTabBounds, loginView == "status")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.d7c5e4f976"),
                    CairoFont.WhiteSmallText(), 350, loginStatusTabBounds.FlatCopy(), "tip-login-tab-status")
                .AddSuiteButton(SuiteLocalization.Get("gui.settings"), () => SelectLoginProtectionView("settings"), loginSettingsTabBounds, loginView == "settings")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.ec4f7d425d"),
                    CairoFont.WhiteSmallText(), 380, loginSettingsTabBounds.FlatCopy(), "tip-login-tab-settings");

            if (loginView == "settings")
            {
                useGenericResults = false;
                const double sy = 122;

                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, sy, contentWidth, 618))
                    .AddRichtext(SuiteLocalization.Get("gui.loginsettings"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, sy + 14, 390, 26), "page-header")
                    .AddStaticText(SuiteLocalization.Get("gui.loginsettingsimmediate"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(450, sy + 16, 590, 24))

                    .AddRichtext(SuiteLocalization.Get("gui.loginenabled"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 58, 195, 24))
                    .AddSwitch(value => SetLoginProtectionBool("enabled", value),
                        ElementBounds.Fixed(244, sy + 51, 60, 30), "login-enabled")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.e086e1a075"),
                        CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(244, sy + 51, 60, 30), "tip-login-enabled")
                    .AddRichtext(
                        $"<strong>Currently protected:</strong> {loginActiveProtectedCount}",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(350, sy + 58, 240, 24))

                    .AddRichtext(SuiteLocalization.Get("gui.protection"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 103, 220, 24))
                    .AddSuiteInset(ElementBounds.Fixed(40, sy + 128, 980, 2))

                    .AddRichtext(SuiteLocalization.Get("gui.durationseconds"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 148, 145, 24))
                    .AddNumberInput(ElementBounds.Fixed(192, sy + 143, 105, 30),
                        OnLoginProtectionSecondsChanged, CairoFont.WhiteSmallText(), "login-seconds")
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveLoginProtectionSeconds,
                        ElementBounds.Fixed(309, sy + 143, 84, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.30ec458fbb"),
                        CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(192, sy + 143, 105, 30), "tip-login-seconds")

                    .AddStaticText(SuiteLocalization.Get("gui.literal.6fbdb570c7"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 194, 145, 24))
                    .AddSwitch(value => SetLoginProtectionBool("protectonjoin", value),
                        ElementBounds.Fixed(192, sy + 187, 60, 30), "login-join")

                    .AddStaticText(SuiteLocalization.Get("gui.literal.e6740dca48"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(350, sy + 194, 155, 24))
                    .AddSwitch(value => SetLoginProtectionBool("protectonrespawn", value),
                        ElementBounds.Fixed(512, sy + 187, 60, 30), "login-respawn")

                    .AddStaticText(SuiteLocalization.Get("gui.literal.988a176e0c"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 236, 175, 24))
                    .AddSwitch(value => SetLoginProtectionBool("announceonjoin", value),
                        ElementBounds.Fixed(222, sy + 229, 60, 30), "login-announce-start")

                    .AddStaticText(SuiteLocalization.Get("gui.literal.4861beee2a"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(350, sy + 236, 170, 24))
                    .AddSwitch(value => SetLoginProtectionBool("announceonend", value),
                        ElementBounds.Fixed(526, sy + 229, 60, 30), "login-announce-end")

                    .AddRichtext(SuiteLocalization.Get("gui.antiabuse"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 292, 280, 24))
                    .AddSuiteInset(ElementBounds.Fixed(40, sy + 317, 980, 2))

                    .AddStaticText(SuiteLocalization.Get("gui.literal.0cd64ebaf0"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 340, 180, 24))
                    .AddSwitch(value => SetLoginProtectionBool("blockpvp", value),
                        ElementBounds.Fixed(226, sy + 333, 60, 30), "login-block-pvp")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.72ab6b60b7"),
                        CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(226, sy + 333, 60, 30), "tip-login-pvp")

                    .AddStaticText(SuiteLocalization.Get("gui.literal.d5301154dd"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(350, sy + 340, 190, 24))
                    .AddSwitch(value => SetLoginProtectionBool("cancelonattack", value),
                        ElementBounds.Fixed(548, sy + 333, 60, 30), "login-cancel-attack")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.7d98eccc9d"),
                        CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(548, sy + 333, 60, 30), "tip-login-cancel-attack")

                    .AddStaticText(SuiteLocalization.Get("gui.literal.d28221cfe4"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 382, 150, 24))
                    .AddSwitch(value => SetLoginProtectionBool("protectfalldamage", value),
                        ElementBounds.Fixed(196, sy + 375, 60, 30), "login-fall")

                    .AddStaticText(SuiteLocalization.Get("gui.literal.58b47c10a3"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(350, sy + 382, 205, 24))
                    .AddSwitch(value => SetLoginProtectionBool("extinguishfire", value),
                        ElementBounds.Fixed(562, sy + 375, 60, 30), "login-fire")

                    .AddStaticText(SuiteLocalization.Get("gui.literal.d84ddc92d7"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 424, 185, 24))
                    .AddSwitch(value => SetLoginProtectionBool("recoverswimmingonlogin", value),
                        ElementBounds.Fixed(232, sy + 417, 60, 30), "login-water-recovery")
                    .AddHoverText(
                        "If a player reconnects while in liquid, restore server-side swimming/detached movement once at login so normal liquid physics takes over cleanly.",
                        CairoFont.WhiteSmallText(), 430,
                        ElementBounds.Fixed(232, sy + 417, 60, 30), "tip-login-water-recovery")

                    .AddRichtext(SuiteLocalization.Get("gui.behavior"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, sy + 470, 220, 24))
                    .AddSuiteInset(ElementBounds.Fixed(40, sy + 495, 980, 2))
                    .AddStaticText(
                        "Protection is UID-authoritative and is armed as early as PlayerJoin, then revalidated at PlayerNowPlaying and PlayerReady when available. " +
                        "A lightweight guard keeps the current player entity bound to the protection behavior while the shield is active; water-login recovery remains bounded.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, sy + 512, 835, 54))
                    .AddSuiteButton(SuiteLocalization.Get("gui.refreshsettings"), () => RequestLoginProtection("settings"),
                        ElementBounds.Fixed(886, sy + 526, 150, 32));

                resultsY = sy + 560;
                resultsHeight = 1;
            }
            else
            {
                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, 122, contentWidth, 618))
                    .AddRichtext($"<strong>{pageHeaderText}</strong>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 136, 650, 26), "page-header")
                    .AddSuiteButton(SuiteLocalization.Get("gui.literal.28a0e95aff"), () => RequestLoginProtection("status"),
                        ElementBounds.Fixed(920, 132, 150, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.4db1f0411e"),
                        CairoFont.WhiteSmallText(), 380, ElementBounds.Fixed(920, 132, 150, 30), "tip-login-status-refresh");

                resultsY = 170;
                resultsHeight = 550;
            }
        }
        else if (section == "profiler")
        {
            useGenericResults = false;
            resultsY = 0;
            resultsHeight = 0;

            // Keep generous gaps between profiler tabs. Button captions are not
            // reliably clipped by the VS GUI and may spill into neighbors when scaled.
            var overviewTabBounds = ElementBounds.Fixed(22, secondaryTabY, 112, 30);
            var modsTabBounds = ElementBounds.Fixed(148, secondaryTabY, 96, 30);
            var phasesTabBounds = ElementBounds.Fixed(258, secondaryTabY, 132, 30);
            var slowTicksTabBounds = ElementBounds.Fixed(404, secondaryTabY, 132, 30);
            var hotspotsTabBounds = ElementBounds.Fixed(550, secondaryTabY, 120, 30);
            var offThreadTabBounds = ElementBounds.Fixed(684, secondaryTabY, 132, 30);
            var lagEventsTabBounds = ElementBounds.Fixed(830, secondaryTabY, 132, 30);
            var liveUpdatesBounds = ElementBounds.Fixed(976, secondaryTabY, 190, 30);

            var refreshProfilerBounds = ElementBounds.Fixed(842, 132, 104, 30);
            var profile10Bounds = ElementBounds.Fixed(960, 132, 126, 30);
            var profile30Bounds = ElementBounds.Fixed(1100, 132, 126, 30);

            string profileState = BuildProfilerStateText();

            composer
                .AddSuiteButton(SuiteLocalization.Get("gui.overview"), () => SelectProfilerView("overview"), overviewTabBounds, profilerView == "overview")
                .AddHoverText(SuiteLocalization.Get("gui.tip.profiler.overview"),
                    CairoFont.WhiteSmallText(), 320, overviewTabBounds.FlatCopy(), "tip-profiler-overview")
                .AddSuiteButton(SuiteLocalization.Get("gui.mods"), () => SelectProfilerView("mods"), modsTabBounds, profilerView == "mods")
                .AddHoverText(SuiteLocalization.Get("gui.tip.profiler.mods"),
                    CairoFont.WhiteSmallText(), 350, modsTabBounds.FlatCopy(), "tip-profiler-mods")
                .AddSuiteButton(SuiteLocalization.Get("gui.tickphases"), () => SelectProfilerView("phases"), phasesTabBounds, profilerView == "phases")
                .AddHoverText(SuiteLocalization.Get("gui.tip.profiler.phases"),
                    CairoFont.WhiteSmallText(), 390, phasesTabBounds.FlatCopy(), "tip-profiler-phases")
                .AddSuiteButton(SuiteLocalization.Get("gui.slowticks"), () => SelectProfilerView("slowticks"), slowTicksTabBounds, profilerView == "slowticks")
                .AddHoverText(SuiteLocalization.Get("gui.tip.profiler.slowticks"),
                    CairoFont.WhiteSmallText(), 410, slowTicksTabBounds.FlatCopy(), "tip-profiler-slowticks")
                .AddSuiteButton(SuiteLocalization.Get("gui.hotspots"), () => SelectProfilerView("hotspots"), hotspotsTabBounds, profilerView == "hotspots")
                .AddHoverText(SuiteLocalization.Get("gui.tip.profiler.hotspots"),
                    CairoFont.WhiteSmallText(), 410, hotspotsTabBounds.FlatCopy(), "tip-profiler-hotspots")
                .AddSuiteButton(SuiteLocalization.Get("gui.offthread"), () => SelectProfilerView("offthread"), offThreadTabBounds, profilerView == "offthread")
                .AddHoverText(SuiteLocalization.Get("gui.tip.profiler.offthread"),
                    CairoFont.WhiteSmallText(), 420, offThreadTabBounds.FlatCopy(), "tip-profiler-offthread")
                .AddSuiteButton(SuiteLocalization.Get("gui.lagevents"), () => SelectProfilerView("lagevents"), lagEventsTabBounds, profilerView == "lagevents")
                .AddHoverText(SuiteLocalization.Get("gui.tip.profiler.lagevents"),
                    CairoFont.WhiteSmallText(), 440, lagEventsTabBounds.FlatCopy(), "tip-profiler-lag-events")
                .AddSuiteButton(
                    profilerLiveUpdates ? "Live Updates: ON" : "Live Updates: OFF",
                    ToggleProfilerLiveUpdates,
                    liveUpdatesBounds, profilerLiveUpdates)
                .AddHoverText(
                    "When enabled, refresh this Profiler page every 2 seconds. This does not enable the expensive engine profiler or create another server sampler.",
                    CairoFont.WhiteSmallText(), 450, liveUpdatesBounds.FlatCopy(), "tip-profiler-live")

                .AddSuiteInset(ElementBounds.Fixed(contentX, 122, contentWidth, 618))
                .AddRichtext(SuiteLocalization.Get("gui.profilerdiag"), CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(innerX, 136, 450, 26), "page-header")
                .AddRichtext(SuiteLocalization.Get("gui.profilerenabled"), CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(500, 138, 125, 24))
                .AddSwitch(SetProfilerEnabled,
                    ElementBounds.Fixed(628, 130, 60, 30), "profiler-enabled")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.cb0ce573a0"),
                    CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(628, 130, 60, 30), "tip-profiler-enabled")
                .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RequestProfilerSnapshot, refreshProfilerBounds)
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.83687144bb"),
                    CairoFont.WhiteSmallText(), 370, refreshProfilerBounds.FlatCopy(), "tip-profiler-refresh")
                .AddSuiteButton(SuiteLocalization.Get("gui.profile10"), () => StartProfilerCapture(10), profile10Bounds)
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.0dd9da1f7c"),
                    CairoFont.WhiteSmallText(), 430, profile10Bounds.FlatCopy(), "tip-profiler-10")
                .AddSuiteButton(SuiteLocalization.Get("gui.profile30"), () => StartProfilerCapture(30), profile30Bounds)
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.48382235a5"),
                    CairoFont.WhiteSmallText(), 410, profile30Bounds.FlatCopy(), "tip-profiler-30")
                .AddDynamicText(
                    SuiteProfilerTextElement.FitUiLine(SuiteLocalization.Get("gui.statusvalue", profilerMessage), 1180),
                    CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 166, 1180, 24), "profiler-live-status")
                .AddDynamicText(SuiteProfilerTextElement.FitUiLine(profileState, 760), CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(innerX, 190, 760, 26), "profiler-live-state")
                .AddSuiteButton(SuiteLocalization.Get("gui.localweb"), OpenLocalProfilerWeb,
                    ElementBounds.Fixed(842, 184, 156, 30))
                .AddHoverText(
                    "Open the built-in loopback dashboard. This works when the game client is running on the same machine as the server.",
                    CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(842, 184, 156, 30), "tip-profiler-local-web")
                .AddSuiteButton(
                    string.IsNullOrWhiteSpace(webPublicUrl) ? "Public: Not Set" : "Public Web",
                    OpenPublicProfilerWeb,
                    ElementBounds.Fixed(1024, 184, 166, 30))
                .AddHoverText(
                    string.IsNullOrWhiteSpace(webPublicUrl)
                        ? "Configure PublicUrl in Web Server settings or with /serverutils web publicurl <https://...>."
                        : "Open the configured public/reverse-proxy dashboard URL.",
                    CairoFont.WhiteSmallText(), 450, ElementBounds.Fixed(1024, 184, 166, 30), "tip-profiler-public-web");

            if (profilerView == "mods")
            {
                double attributedTime =
                    profilerLastProfile.AttributedMs +
                    profilerLastProfile.UnattributedMs;
                double coverage = attributedTime <= 0
                    ? 0
                    : profilerLastProfile.AttributedMs * 100d / attributedTime;

                composer
                    .AddProfilerRichText(
                        $"<strong>Mod attribution</strong> — {profilerLoadedModCount} loaded mods; " +
                        $"time attribution coverage <strong>{coverage:0}%</strong>. " +
                        "High/Medium confidence comes from mod IDs, asset domains, assemblies, namespaces, or ModSystem types.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(36, 224, 1025, 40))
                    .AddSuiteInset(ElementBounds.Fixed(30, 268, 1040, 32))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.modidname"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 274, 300, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.literal.1947566e2b"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(350, 274, 70, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.literal.3ae2469ed0"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(430, 274, 70, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.calls"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(510, 274, 62, 24))
                    .AddProfilerRichText("<strong>≥10ms</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(578, 274, 62, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.share"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(646, 274, 68, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.literal.5b7820560e"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(720, 274, 96, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.topmarker"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(822, 274, 238, 24));

                if (profilerModHotspots.Count == 0)
                {
                    composer.AddProfilerText(
                        "Run a 10s or 30s profile to build the per-mod attribution table.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 314, 900, 24));
                }
                else
                {
                    int count = Math.Min(15, profilerModHotspots.Count);
                    for (int i = 0; i < count; i++)
                    {
                        var row = profilerModHotspots[i];
                        double y = 306 + i * 27;
                        if ((i & 1) == 1)
                            composer.AddSuiteInset(ElementBounds.Fixed(32, y - 2, 1036, 27));

                        string identity = row.ModId == "<unattributed>"
                            ? "<font color=\"#ffad66\"><strong>Unattributed</strong></font>"
                            : row.ModId.Equals("newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase)
                                ? $"<strong>{Escape(row.ModId)}</strong> — utility/profiler self"
                                : $"<strong>{Escape(row.ModId)}</strong> — {Escape(row.ModName)}";
                        string confidence = ProfilerConfidenceMarkup(row.Confidence);

                        composer
                            .AddProfilerRichText(identity, CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, y, 300, 24))
                            .AddProfilerText($"{row.TotalMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(350, y, 72, 24))
                            .AddProfilerText($"{row.MaxSingleMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(430, y, 72, 24))
                            .AddProfilerText($"{row.Calls:N0}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(510, y, 62, 24))
                            .AddProfilerText($"{row.SlowSegments:N0}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(578, y, 62, 24))
                            .AddProfilerText($"{row.PercentOfProfileTickTime:0.0}%", CairoFont.WhiteSmallText(), ElementBounds.Fixed(646, y, 68, 24))
                            .AddProfilerRichText(confidence, CairoFont.WhiteSmallText(), ElementBounds.Fixed(720, y, 96, 24))
                            .AddProfilerText(row.TopMarker, CairoFont.WhiteSmallText(), ElementBounds.Fixed(822, y, 238, 24));
                    }
                }

                composer.AddProfilerText(
                    "Attribution is evidence-based, not guessed: generic engine markers remain Unattributed. " +
                    "A mod that adds its own FrameProfiler.Mark/Enter points will be identified much more precisely.",
                    CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 718, 1010, 24));
            }
            else if (profilerView == "phases")
            {
                composer
                    .AddProfilerRichText(
                        "<strong>Tick phase breakdown</strong> — root-level FrameProfiler marks/ranges from the captured server ticks. " +
                        "Rows are inclusive profiler phases and can overlap, so their percentages are diagnostic rather than expected to sum to 100%.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(36, 224, 1020, 40))
                    .AddSuiteInset(ElementBounds.Fixed(30, 268, 1040, 32))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.tickmarker"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 274, 430, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.avgtick"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(480, 274, 82, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.max"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(570, 274, 72, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.total"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(650, 274, 72, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.share"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(730, 274, 72, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.literal.b6d3b7123b"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(812, 274, 238, 24));

                if (profilerPhases.Count == 0)
                {
                    composer.AddProfilerText(
                        "Run a manual profile to capture the server tick phase breakdown.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 314, 900, 24));
                }
                else
                {
                    int count = Math.Min(15, profilerPhases.Count);
                    for (int i = 0; i < count; i++)
                    {
                        var row = profilerPhases[i];
                        double y = 306 + i * 27;
                        if ((i & 1) == 1)
                            composer.AddSuiteInset(ElementBounds.Fixed(32, y - 2, 1036, 27));

                        string owner = string.IsNullOrWhiteSpace(row.ModId)
                            ? "engine / unattributed"
                            : row.ModId + " (" + row.AttributionConfidence + ")";

                        composer
                            .AddProfilerRichText($"<strong>{Escape(row.Code)}</strong>",
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, y, 430, 24))
                            .AddProfilerText($"{row.AveragePerTickMs:0.00}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(480, y, 82, 24))
                            .AddProfilerText($"{row.MaxSingleMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(570, y, 72, 24))
                            .AddProfilerText($"{row.TotalMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(650, y, 72, 24))
                            .AddProfilerText($"{row.PercentOfProfileTickTime:0.0}%", CairoFont.WhiteSmallText(), ElementBounds.Fixed(730, y, 72, 24))
                            .AddProfilerText(owner, CairoFont.WhiteSmallText(), ElementBounds.Fixed(812, y, 238, 24));
                    }
                }
            }
            else if (profilerView == "slowticks")
            {
                composer
                    .AddProfilerRichText(
                        "<strong>Worst profiled ticks</strong> — ticks ≥50ms captured during the last manual profile, " +
                        "sorted slowest first with up to four leading leaf-work causes.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(36, 224, 1020, 40))
                    .AddSuiteInset(ElementBounds.Fixed(30, 268, 1040, 32))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.literal.4ecb92ecf9"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 274, 82, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.tick"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(130, 274, 85, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.leadingcauses"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(225, 274, 830, 24));

                if (profilerProfileSlowTicks.Count == 0)
                {
                    composer.AddProfilerText(
                        "No ≥50ms ticks were captured in the last profile, or no profile has been run yet.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 314, 950, 24));
                }
                else
                {
                    int count = Math.Min(13, profilerProfileSlowTicks.Count);
                    for (int i = 0; i < count; i++)
                    {
                        var row = profilerProfileSlowTicks[i];
                        double y = 306 + i * 31;
                        if ((i & 1) == 1)
                            composer.AddSuiteInset(ElementBounds.Fixed(32, y - 2, 1036, 31));

                        string when = DateTimeOffset.FromUnixTimeMilliseconds(row.UnixMilliseconds)
                            .ToLocalTime().ToString("HH:mm:ss");

                        composer
                            .AddProfilerRichText($"<strong>{when}</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, y, 82, 24))
                            .AddProfilerRichText($"<font color=\"#ff8080\"><strong>{row.TickMs:0.0}ms</strong></font>",
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(130, y, 85, 24))
                            .AddProfilerText(row.TopCauseSummary,
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(225, y, 830, 28));
                    }
                }
            }
            else if (profilerView == "lagevents")
            {
                string stateColor = profilerDiagnosticSummary.State == "Investigate"
                    ? "#ff7070"
                    : profilerDiagnosticSummary.State == "Watch"
                        ? "#ffd166"
                        : "#8fda72";

                composer
                    .AddProfilerRichText(
                        $"<strong>5-minute diagnostic state:</strong> " +
                        $"<font color=\"{stateColor}\"><strong>{Escape(profilerDiagnosticSummary.State)}</strong></font>   " +
                        $"<strong>Worst scheduler delay:</strong> {profilerDiagnosticSummary.WorstDelayMs:0.0}ms   " +
                        $"<strong>Avg/Max CPU:</strong> {profilerDiagnosticSummary.AverageCpuPercent:0.0}% / {profilerDiagnosticSummary.MaxCpuPercent:0.0}%   " +
                        $"<strong>Gen2:</strong> +{profilerDiagnosticSummary.Gen2Collections}",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(36, 224, 1180, 26))
                    // Evidence can legitimately wrap. Reserve a full two-line row so it
                    // never collides with the table header below it.
                    .AddProfilerRichText(
                        $"<strong>Evidence:</strong> {Escape(profilerDiagnosticSummary.Summary)}",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(36, 250, 1180, 48))
                    .AddSuiteInset(ElementBounds.Fixed(30, 306, 1200, 32))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.time"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 312, 78, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.literal.a37f4283db"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(126, 312, 82, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.delay"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(218, 312, 70, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.cpu"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(296, 312, 58, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.gc012"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(362, 312, 92, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.blqueuewriter"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(464, 312, 156, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.entitiesdelta"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(630, 312, 100, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.chunksdelta"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(740, 312, 100, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.evidencecorr"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(850, 312, 360, 24));

                if (profilerDiagnosticIncidents.Count == 0)
                {
                    composer.AddProfilerText(
                        "No correlated lag events recorded in the current rolling diagnostic history.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 352, 1120, 24));
                }
                else
                {
                    int count = Math.Min(13, profilerDiagnosticIncidents.Count);
                    for (int i = 0; i < count; i++)
                    {
                        var row = profilerDiagnosticIncidents[i];
                        double y = 344 + i * 29;
                        if ((i & 1) == 1)
                            composer.AddSuiteInset(ElementBounds.Fixed(32, y - 2, 1196, 29));

                        string when = DateTimeOffset.FromUnixTimeMilliseconds(row.UnixMilliseconds)
                            .ToLocalTime().ToString("HH:mm:ss");
                        string sevColor = row.Severity == "Critical"
                            ? "#ff7070"
                            : row.Severity == "High"
                                ? "#ffad66"
                                : "#ffd166";

                        composer
                            .AddProfilerRichText($"<strong>{when}</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, y, 78, 24))
                            .AddProfilerRichText($"<font color=\"{sevColor}\"><strong>{Escape(row.Severity)}</strong></font>",
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(126, y, 82, 24))
                            .AddProfilerText($"{row.DelayMs:0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(218, y, 70, 24))
                            .AddProfilerText($"{row.CpuPercent:0}%", CairoFont.WhiteSmallText(), ElementBounds.Fixed(296, y, 58, 24))
                            .AddProfilerText($"+{row.Gen0Delta}/+{row.Gen1Delta}/+{row.Gen2Delta}",
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(362, y, 92, 24))
                            .AddProfilerText($"{row.BlockLoggerQueue:N0} / {row.BlockLoggerWriterMs}ms",
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(464, y, 156, 24))
                            .AddProfilerText($"{row.LoadedEntities:N0} ({row.EntityDelta:+#;-#;0})",
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(630, y, 100, 24))
                            .AddProfilerText($"{row.LoadedChunks:N0} ({row.ChunkDelta:+#;-#;0})",
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(740, y, 100, 24))
                            .AddProfilerText(row.Evidence,
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(850, y, 360, 24));
                    }
                }

                composer.AddProfilerText(
                    "Lag Events are evidence correlations, not automatic blame. Scheduler delay is callback lateness; use a manual 10s/30s profile for exact tick timing and mod attribution.",
                    CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 718, 1170, 24));
            }
            else if (profilerView == "offthread")
            {
                composer
                    .AddProfilerRichText(
                        "<strong>Off-thread slow reports</strong> — Vintage Story worker-thread slow profiles captured during the manual window " +
                        "(50ms threshold unless the server already had a stricter threshold). These can expose physics, worldgen, or background work.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(36, 224, 1020, 40))
                    .AddSuiteInset(ElementBounds.Fixed(30, 268, 1040, 32))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.durationlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 274, 85, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.literal.764355d4a7"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(132, 274, 170, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.confidence"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(310, 274, 100, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.workerprofilerreport"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(420, 274, 635, 24));

                if (profilerOffThreadReports.Count == 0)
                {
                    composer.AddProfilerText(
                        "No worker-thread slow reports were produced during the last manual profile.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 314, 950, 24));
                }
                else
                {
                    int count = Math.Min(14, profilerOffThreadReports.Count);
                    for (int i = 0; i < count; i++)
                    {
                        var row = profilerOffThreadReports[i];
                        double y = 306 + i * 29;
                        if ((i & 1) == 1)
                            composer.AddSuiteInset(ElementBounds.Fixed(32, y - 2, 1196, 29));

                        string owner = string.IsNullOrWhiteSpace(row.ModId)
                            ? "unattributed"
                            : row.ModId;

                        composer
                            .AddProfilerRichText($"<font color=\"#ff8080\"><strong>{row.DurationMs:0.0}ms</strong></font>",
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, y, 85, 24))
                            .AddProfilerText(owner,
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(132, y, 170, 24))
                            .AddProfilerRichText(ProfilerConfidenceMarkup(row.Confidence),
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(310, y, 100, 24))
                            .AddProfilerText(row.Summary,
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(420, y, 635, 24));
                    }
                }
            }
            else if (profilerView == "hotspots")
            {
                composer
                    .AddProfilerRichText(
                        "<strong>Leaf work hotspots</strong> — deepest profiler markers/ranges are ranked without also charging their parent ranges, " +
                        "which avoids the old parent/child double-counting problem.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(36, 224, 1020, 40))
                    .AddSuiteInset(ElementBounds.Fixed(30, 268, 1040, 32))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.profilerpathwork"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 274, 470, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.mod"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(520, 274, 155, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.total"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(682, 274, 72, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.max"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(762, 274, 72, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.calls"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(842, 274, 72, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.share"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(922, 274, 70, 24))
                    .AddProfilerRichText(SuiteLocalization.Get("gui.confshort"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(996, 274, 60, 24));

                if (profilerHotspots.Count == 0)
                {
                    composer.AddProfilerText(
                        "Run a manual profile to collect leaf-work hotspots.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 314, 900, 24));
                }
                else
                {
                    int count = Math.Min(15, profilerHotspots.Count);
                    for (int i = 0; i < count; i++)
                    {
                        var row = profilerHotspots[i];
                        double y = 306 + i * 27;
                        if ((i & 1) == 1)
                            composer.AddSuiteInset(ElementBounds.Fixed(32, y - 2, 1036, 27));

                        string owner = string.IsNullOrWhiteSpace(row.ModId)
                            ? "—"
                            : row.ModId;

                        composer
                            .AddProfilerRichText($"<strong>{Escape(row.Code)}</strong>",
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, y, 470, 24))
                            .AddProfilerText(owner, CairoFont.WhiteSmallText(), ElementBounds.Fixed(520, y, 155, 24))
                            .AddProfilerText($"{row.TotalMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(682, y, 72, 24))
                            .AddProfilerText($"{row.MaxSingleMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(762, y, 72, 24))
                            .AddProfilerText($"{row.Calls:N0}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(842, y, 72, 24))
                            .AddProfilerText($"{row.PercentOfProfileTickTime:0.0}%", CairoFont.WhiteSmallText(), ElementBounds.Fixed(922, y, 70, 24))
                            .AddProfilerRichText(ProfilerConfidenceMarkup(row.AttributionConfidence),
                                CairoFont.WhiteSmallText(), ElementBounds.Fixed(996, y, 62, 24));
                    }
                }
            }
            else
            {
                composer
                    .AddDynamicText(
                        BuildProfilerCurrentPrimaryText(),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 220, 1020, 24), "profiler-live-current-primary")
                    .AddDynamicText(
                        BuildProfilerCurrentSecondaryText(),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 244, 1020, 24), "profiler-live-current-secondary")

                    .AddRichtext(
                        $"<strong>5m diagnosis:</strong> {Escape(profilerDiagnosticSummary.State)} — {Escape(ShortProfilerCode(profilerDiagnosticSummary.Summary, 118))}",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 266, 1020, 24))

                    .AddRichtext(SuiteLocalization.Get("gui.mainthreaddelay"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(32, 292, 320, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.processcpu"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(382, 292, 320, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.literal.eedf82d87d"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(732, 292, 320, 24))

                    // Keep the in-game Profiler dependency-free. Earlier
                    // versions used AddDynamicCustomDraw here, which exposed
                    // Cairo.Context/ImageSurface in this mod assembly and
                    // forced every Visual Studio build to resolve Cairo.dll
                    // separately. The web dashboard remains the interactive
                    // graphing surface; the game GUI shows the same rolling
                    // metrics as normal Vintage Story GUI text.
                    .AddSuiteInset(ElementBounds.Fixed(32, 316, 330, 136))
                    .AddStaticText(
                        ProfilerMetricSummary(p => p.MainThreadDelayMs, "ms"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(46, 328, 300, 112))
                    .AddSuiteInset(ElementBounds.Fixed(382, 316, 330, 136))
                    .AddStaticText(
                        ProfilerMetricSummary(p => p.CpuPercent, "%"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(396, 328, 300, 112))
                    .AddSuiteInset(ElementBounds.Fixed(732, 316, 330, 136))
                    .AddStaticText(
                        ProfilerMetricSummary(p => p.ManagedHeapMb, " MB"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(746, 328, 300, 112))

                    .AddSuiteInset(ElementBounds.Fixed(28, 472, 520, 252))
                    .AddRichtext(SuiteLocalization.Get("gui.recentstalls"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, 484, 480, 24))
                    .AddStaticText(SuiteLocalization.Get("gui.lightmonitorcontext"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 508, 480, 22))

                    .AddSuiteInset(ElementBounds.Fixed(560, 472, 520, 252))
                    .AddRichtext(SuiteLocalization.Get("gui.problemmod"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(572, 484, 480, 24))
                    .AddStaticText(
                        "Run a manual profile, then use Mods / Tick Phases / Slow Ticks / Hotspots / Off-thread.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(572, 508, 480, 38));

                if (profilerSlowSpikes.Count == 0)
                {
                    composer.AddStaticText(
                        "No ≥100ms scheduler stalls recorded in the rolling history.",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, 540, 480, 24));
                }
                else
                {
                    int count = Math.Min(7, profilerSlowSpikes.Count);
                    for (int i = 0; i < count; i++)
                    {
                        var spike = profilerSlowSpikes[i];
                        string when = DateTimeOffset.FromUnixTimeMilliseconds(spike.UnixMilliseconds)
                            .ToLocalTime()
                            .ToString("HH:mm:ss");

                        composer.AddRichtext(
                            $"<strong>{when}</strong>  " +
                            $"<font color=\"#ff8080\"><strong>{spike.DelayMs:0}ms</strong></font>  " +
                            $"CPU {spike.CpuPercent:0}%  entities {spike.LoadedEntities:N0}  " +
                            $"GC +{spike.Gen0Delta}/+{spike.Gen1Delta}/+{spike.Gen2Delta}  " +
                            $"BL {spike.BlockLoggerQueue:N0}/{spike.BlockLoggerWriterMs:N0}ms",
                            CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(40, 540 + i * 25, 492, 24));
                    }
                }

                double attributedTime =
                    profilerLastProfile.AttributedMs +
                    profilerLastProfile.UnattributedMs;
                double coverage = attributedTime <= 0
                    ? 0
                    : profilerLastProfile.AttributedMs * 100d / attributedTime;
                var topThirdParty = profilerModHotspots.FirstOrDefault(x =>
                    !x.IsCore &&
                    x.ModId != "<unattributed>" &&
                    !x.ModId.Equals("newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase));
                string topThirdPartyText = topThirdParty == null
                    ? "(none yet)"
                    : Escape(topThirdParty.ModId) + " — " + topThirdParty.TotalMs.ToString("0.0") + "ms";

                composer
                    .AddRichtext(
                        $"<strong>Loaded mods:</strong> {profilerLoadedModCount}<br>" +
                        $"<strong>Attribution coverage:</strong> {coverage:0}% of captured leaf time<br>" +
                        $"<strong>Top non-core attribution:</strong> {topThirdPartyText}<br>" +
                        $"<strong>Profiled slow ticks:</strong> {profilerProfileSlowTicks.Count}",
                        CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(572, 558, 480, 112));
            }
        }
        else
        {
            var limiterTabBounds = ElementBounds.Fixed(22, secondaryTabY, 100, 30);
            var populationTabBounds = ElementBounds.Fixed(136, secondaryTabY, 140, 30);
            var rulesTabBounds = ElementBounds.Fixed(290, secondaryTabY, 90, 30);
            var settingsTabBounds = ElementBounds.Fixed(394, secondaryTabY, 110, 30);
            var removalLogsTabBounds = ElementBounds.Fixed(518, secondaryTabY, 150, 30);

            composer
                .AddSuiteButton("Limiter", () => SelectAnimalView("limiter"), limiterTabBounds, animalView == "limiter")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.5f42cd52fb"),
                    CairoFont.WhiteSmallText(), 340, limiterTabBounds.FlatCopy(), "tip-animal-tab-limiter")
                .AddSuiteButton(SuiteLocalization.Get("gui.population"), () => SelectAnimalView("population"), populationTabBounds, animalView == "population")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.4606e4ee1a"),
                    CairoFont.WhiteSmallText(), 420, populationTabBounds.FlatCopy(), "tip-animal-tab-population")
                .AddSuiteButton(SuiteLocalization.Get("gui.rules"), () => SelectAnimalView("rules"), rulesTabBounds, animalView == "rules")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.fe0e2ad42c"),
                    CairoFont.WhiteSmallText(), 360, rulesTabBounds.FlatCopy(), "tip-animal-tab-rules")
                .AddSuiteButton(SuiteLocalization.Get("gui.settings"), () => SelectAnimalView("settings"), settingsTabBounds, animalView == "settings")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.6e8d88253c"),
                    CairoFont.WhiteSmallText(), 370, settingsTabBounds.FlatCopy(), "tip-animal-tab-settings")
                .AddSuiteButton(SuiteLocalization.Get("gui.removallogs"), () => SelectAnimalView("history"), removalLogsTabBounds, animalView == "history")
                .AddHoverText(SuiteLocalization.Get("gui.tip.more.0f3892a697"),
                    CairoFont.WhiteSmallText(), 370, removalLogsTabBounds.FlatCopy(), "tip-animal-tab-history");

            if (animalView == "population")
            {
                useGenericResults = false;

                var scanPopulationBounds = ElementBounds.Fixed(900, 130, 180, 32);

                int totalTracked = populationMobTotals.Count > 0
                    ? populationMobTotals.Values.Sum(x => x.Tracked)
                    : populationRows.Sum(x => x.Tracked);
                int totalProtected = populationMobTotals.Count > 0
                    ? populationMobTotals.Values.Sum(x => x.Protected)
                    : populationRows.Sum(x => x.Protected);
                int overRegions = populationRows.Count(x => x.Eligible > x.Limit);
                int mobTypes = populationMobTotals.Count > 0
                    ? populationMobTotals.Count
                    : populationRows.Select(x => x.MobId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, 122, contentWidth, 618))
                    .AddRichtext($"<strong>{pageHeaderText}</strong>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 136, 620, 26), "page-header")
                    .AddSuiteButton(SuiteLocalization.Get("gui.scanpopulation"), ScanAnimalPopulation, scanPopulationBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.0c9abcefa0"),
                        CairoFont.WhiteSmallText(), 430, scanPopulationBounds.FlatCopy(), "tip-population-scan")
                    .AddRichtext(
                        $"<strong>{mobTypes}</strong> mobs  |  " +
                        $"<strong>{populationRows.Count}</strong> regions  |  " +
                        $"<strong>{totalTracked}</strong> tracked  |  " +
                        $"<strong>{totalProtected}</strong> protected  |  " +
                        $"<strong>{overRegions}</strong> over limit",
                        CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 168, 740, 24))
                    .AddStaticText(populationMode,
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(790, 168, 390, 24))
                    .AddStaticText(SuiteLocalization.Get("gui.mobtotalhelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 190, 1140, 24))

                    .AddSuiteInset(ElementBounds.Fixed(innerX - 4, 216, 1160, 34))
                    .AddRichtext(SuiteLocalization.Get("gui.mobid"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(34, 222, 300, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.region"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(338, 222, 82, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.matchedrule"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(424, 222, 205, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.tracked"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(633, 222, 70, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.mobtotal"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(707, 222, 82, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.limitlabel"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(793, 222, 54, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.radiuslabel"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(851, 222, 68, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.protected"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(923, 222, 84, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.statustitle"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(1011, 222, 140, 24))
                    .AddSuiteInset(ElementBounds.Fixed(innerX - 4, 253, 1160, 2));

                const double populationRowStartY = 260;

                for (int slot = 0; slot < PopulationVisibleRows; slot++)
                {
                    double y = populationRowStartY + slot * PopulationRowHeight;

                    if ((slot & 1) == 1)
                        composer.AddSuiteInset(ElementBounds.Fixed(innerX - 4, y - 1, 1160, PopulationRowHeight));

                    composer
                        .AddRichtext("", CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(34, y, 300, 24), $"population-mob-{slot}")
                        .AddRichtext("", CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(338, y, 82, 24), $"population-region-{slot}")
                        .AddRichtext("", CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(424, y, 205, 24), $"population-rule-{slot}")
                        .AddRichtext("", CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(633, y, 70, 24), $"population-tracked-{slot}")
                        .AddRichtext("", CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(707, y, 82, 24), $"population-mobtotal-{slot}")
                        .AddRichtext("", CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(793, y, 54, 24), $"population-limit-{slot}")
                        .AddRichtext("", CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(851, y, 68, 24), $"population-radius-{slot}")
                        .AddRichtext("", CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(923, y, 84, 24), $"population-protected-{slot}")
                        .AddRichtext("", CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(1011, y, 140, 24), $"population-status-{slot}");
                }

                composer.AddVerticalScrollbar(
                    OnPopulationScroll,
                    ElementBounds.Fixed(1196, populationRowStartY - 5, 18,
                        PopulationVisibleRows * PopulationRowHeight),
                    "population-scroll");

                resultsY = 260;
                resultsHeight = PopulationVisibleRows * PopulationRowHeight;
            }
            else if (animalView == "history")
            {
                useGenericResults = false;

                var refreshLogsBounds = ElementBounds.Fixed(930, 130, 150, 32);
                var prevLogsBounds = ElementBounds.Fixed(842, 698, 100, 30);
                var nextLogsBounds = ElementBounds.Fixed(954, 698, 100, 30);

                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, 122, contentWidth, 618))
                    .AddRichtext($"<strong>{pageHeaderText}</strong>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 136, 650, 26), "page-header")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refreshlogs"), () => SendAnimalLimiterOperation("history"), refreshLogsBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.22891d3a07"),
                        CairoFont.WhiteSmallText(), 360, refreshLogsBounds.FlatCopy(), "tip-animal-refresh-logs")
                    .AddRichtext(SuiteLocalization.Get("gui.newestremovals"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 168, 280, 24))
                    .AddSuiteInset(ElementBounds.Fixed(innerX, 190, 1020, 2));

                const double removalRowStartY = 202;

                for (int slot = 0; slot < RemovalLogVisibleRows; slot++)
                {
                    double y = removalRowStartY + slot * RemovalLogRowHeight;

                    if ((slot & 1) == 1)
                        composer.AddSuiteInset(ElementBounds.Fixed(innerX, y - 2, 1018, RemovalLogRowHeight - 2));

                    composer.AddRichtext(
                        "",
                        CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX + 8, y, 982, RemovalLogRowHeight - 4),
                        $"removal-row-{slot}");
                }

                composer
                    .AddVerticalScrollbar(
                        OnRemovalLogScroll,
                        ElementBounds.Fixed(1072, removalRowStartY - 7, 18,
                            RemovalLogVisibleRows * RemovalLogRowHeight),
                        "removal-scroll")
                    .AddRichtext(
                        $"<strong>{removalLogLines.Count}</strong> removal record{(removalLogLines.Count == 1 ? "" : "s")}",
                        CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX + 8, 703, 240, 24))
                    .AddSuiteButton(SuiteLocalization.Get("gui.previous"), ScrollRemovalLogsPrevious, prevLogsBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.489ab7b8ef"),
                        CairoFont.WhiteSmallText(), 330, prevLogsBounds.FlatCopy(), "tip-removal-prev")
                    .AddSuiteButton(SuiteLocalization.Get("gui.next"), ScrollRemovalLogsNext, nextLogsBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.acdeede5e3"),
                        CairoFont.WhiteSmallText(), 330, nextLogsBounds.FlatCopy(), "tip-removal-next");

                resultsY = 202;
                resultsHeight = 450;
            }
            else if (animalView == "rules")
            {
                useGenericResults = false;

                var refreshRulesBounds = ElementBounds.Fixed(890, 130, 160, 32);
                var prevRulesBounds = ElementBounds.Fixed(850, 698, 90, 30);
                var nextRulesBounds = ElementBounds.Fixed(954, 698, 90, 30);

                int totalPages = Math.Max(1, (animalRuleRows.Count + AnimalRulesPerPage - 1) / AnimalRulesPerPage);
                animalRulePage = Math.Clamp(animalRulePage, 0, totalPages - 1);
                int firstRow = animalRulePage * AnimalRulesPerPage;
                int lastRow = Math.Min(firstRow + AnimalRulesPerPage, animalRuleRows.Count);

                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, 122, contentWidth, 618))
                    .AddRichtext($"<strong>{pageHeaderText}</strong>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 136, 620, 26), "page-header")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refreshrules"), () => SendAnimalLimiterOperation("rules"), refreshRulesBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.774473a941"),
                        CairoFont.WhiteSmallText(), 330, refreshRulesBounds.FlatCopy(), "tip-animal-refresh-rules")

                    .AddRichtext($"<strong>{Escape(SuiteLocalization.Get("gui.runtime.rulesexclusions", animalRuleCount, animalExclusionCount))}</strong>",
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX, 168, 350, 24))
                    .AddStaticText(SuiteLocalization.Get("gui.customradiushelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(400, 168, 430, 24))

                    .AddSuiteInset(ElementBounds.Fixed(innerX - 4, 198, 1000, 34))
                    .AddRichtext(SuiteLocalization.Get("gui.mobid"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX + 6, 204, 650, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.limitlabel"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(720, 204, 80, 24))
                    .AddRichtext(SuiteLocalization.Get("gui.radiuslabel"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(830, 204, 160, 24))
                    .AddSuiteInset(ElementBounds.Fixed(innerX - 4, 235, 1000, 2));

                const double rowStartY = 244;
                const double rowHeight = 24;

                if (animalRuleRows.Count == 0)
                {
                    composer.AddStaticText(SuiteLocalization.Get("gui.noanimalrules"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX + 6, rowStartY + 6, 700, 24));
                }
                else
                {
                    for (int rowIndex = firstRow; rowIndex < lastRow; rowIndex++)
                    {
                        var row = animalRuleRows[rowIndex];
                        double y = rowStartY + (rowIndex - firstRow) * rowHeight;

                        if (((rowIndex - firstRow) & 1) == 1)
                            composer.AddSuiteInset(ElementBounds.Fixed(innerX - 4, y - 2, 1000, rowHeight));

                        if (row.IsExclusion)
                        {
                            composer
                                .AddRichtext($"<font color=\"#ff6666\">{Escape(row.Pattern)}</font>",
                                    CairoFont.WhiteSmallText(),
                                    ElementBounds.Fixed(innerX + 6, y, 660, 24))
                                .AddRichtext(SuiteLocalization.Get("gui.excludedred"),
                                    CairoFont.WhiteSmallText(),
                                    ElementBounds.Fixed(720, y, 90, 24))
                                .AddRichtext("<font color=\"#ff6666\">—</font>",
                                    CairoFont.WhiteSmallText(),
                                    ElementBounds.Fixed(830, y, 165, 24));
                        }
                        else
                        {
                            composer
                                .AddStaticText(row.Pattern, CairoFont.WhiteSmallText(),
                                    ElementBounds.Fixed(innerX + 6, y, 660, 24))
                                .AddStaticText(row.Limit, CairoFont.WhiteSmallText(),
                                    ElementBounds.Fixed(720, y, 90, 24))
                                .AddStaticText(row.Radius, CairoFont.WhiteSmallText(),
                                    ElementBounds.Fixed(830, y, 165, 24));
                        }
                    }
                }

                composer
                    .AddStaticText(SuiteLocalization.Get("gui.runtime.pageof", animalRulePage + 1, totalPages),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX + 6, 702, 160, 24))
                    .AddSuiteButton(SuiteLocalization.Get("gui.previous"), PreviousAnimalRulePage, prevRulesBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.a4681fde39"),
                        CairoFont.WhiteSmallText(), 310, prevRulesBounds.FlatCopy(), "tip-animal-rules-prev")
                    .AddSuiteButton(SuiteLocalization.Get("gui.next"), NextAnimalRulePage, nextRulesBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.cbc85af9f6"),
                        CairoFont.WhiteSmallText(), 300, nextRulesBounds.FlatCopy(), "tip-animal-rules-next");

                resultsY = 244;
                resultsHeight = 364;
            }
            else if (animalView == "settings")
            {
                useGenericResults = false;

                const double settingsY = 122;
                var refreshSettingsBounds = ElementBounds.Fixed(900, settingsY + 500, 180, 32);

                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, settingsY, contentWidth, 618))
                    .AddRichtext($"<strong>{pageHeaderText}</strong>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, settingsY + 14, 500, 26), "page-header")
                    .AddStaticText(SuiteLocalization.Get("gui.animalsettingsimmediate"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(560, settingsY + 16, 500, 24))

                    // Master safety switch.
                    .AddRichtext(SuiteLocalization.Get("gui.animallimiterenabled"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 55, 195, 24))
                    .AddSwitch(value => SetAnimalBool("enabled", value),
                        ElementBounds.Fixed(246, settingsY + 48, 60, 30), "animal-enabled")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.31c5d12121"),
                        CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(246, settingsY + 48, 60, 30), "tip-setting-enabled")
                    .AddStaticText(SuiteLocalization.Get("gui.animalfreshdisabled"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(340, settingsY + 55, 560, 24))

                    // Defaults and safety.
                    .AddRichtext(SuiteLocalization.Get("gui.defaultssafety"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 88, 260, 24))
                    .AddSuiteInset(ElementBounds.Fixed(40, settingsY + 110, 980, 2))

                    .AddRichtext(SuiteLocalization.Get("gui.defaultlimit"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 126, 140, 24))
                    .AddNumberInput(ElementBounds.Fixed(188, settingsY + 119, 105, 30),
                        OnAnimalDefaultLimitChanged, CairoFont.WhiteSmallText(), "animal-default-limit")
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveAnimalDefaultLimit, ElementBounds.Fixed(304, settingsY + 119, 84, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.e774c4f784"),
                        CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(188, settingsY + 119, 105, 30), "tip-setting-default-limit")

                    .AddRichtext(SuiteLocalization.Get("gui.defaultradius"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(560, settingsY + 126, 145, 24))
                    .AddNumberInput(ElementBounds.Fixed(716, settingsY + 119, 105, 30),
                        OnAnimalRadiusChanged, CairoFont.WhiteSmallText(), "animal-default-radius")
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveAnimalRadius, ElementBounds.Fixed(832, settingsY + 119, 84, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.75d21c2041"),
                        CairoFont.WhiteSmallText(), 380, ElementBounds.Fixed(716, settingsY + 119, 105, 30), "tip-setting-default-radius")

                    .AddRichtext(SuiteLocalization.Get("gui.playerprotectionradius"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 169, 180, 24))
                    .AddNumberInput(ElementBounds.Fixed(226, settingsY + 162, 105, 30),
                        OnAnimalProtectionRadiusChanged, CairoFont.WhiteSmallText(), "animal-protection-radius")
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveAnimalProtectionRadius, ElementBounds.Fixed(342, settingsY + 162, 84, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.d8d9af6a21"),
                        CairoFont.WhiteSmallText(), 400, ElementBounds.Fixed(226, settingsY + 162, 105, 30), "tip-setting-player-protection")

                    .AddRichtext(SuiteLocalization.Get("gui.reconcileseconds"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(560, settingsY + 169, 145, 24))
                    .AddNumberInput(ElementBounds.Fixed(716, settingsY + 162, 105, 30),
                        OnAnimalReconciliationChanged, CairoFont.WhiteSmallText(), "animal-reconcile-seconds")
                    .AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveAnimalReconciliation, ElementBounds.Fixed(832, settingsY + 162, 84, 30))
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.e398a316e6"),
                        CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(716, settingsY + 162, 105, 30), "tip-setting-reconcile")

                    // Protections and behavior.
                    .AddRichtext(SuiteLocalization.Get("gui.protectionsbehavior"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 214, 300, 24))
                    .AddSuiteInset(ElementBounds.Fixed(40, settingsY + 236, 980, 2))

                    .AddRichtext(SuiteLocalization.Get("gui.limitallentities"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 253, 165, 24))
                    .AddSwitch(value => SetAnimalBool("limitallentities", value),
                        ElementBounds.Fixed(214, settingsY + 246, 60, 30), "animal-limit-all")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.c89bb0294b"),
                        CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(214, settingsY + 246, 60, 30), "tip-setting-limit-all")

                    .AddRichtext(SuiteLocalization.Get("gui.protectpets"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(560, settingsY + 253, 180, 24))
                    .AddSwitch(value => SetAnimalBool("protectpets", value),
                        ElementBounds.Fixed(750, settingsY + 246, 60, 30), "animal-protect-pets")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.2c30215595"),
                        CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(750, settingsY + 246, 60, 30), "tip-setting-protect-pets")

                    .AddRichtext(SuiteLocalization.Get("gui.protectfarm"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 300, 180, 24))
                    .AddSwitch(value => SetAnimalBool("protectfarmanimals", value),
                        ElementBounds.Fixed(214, settingsY + 293, 60, 30), "animal-protect-farm")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.986427c052"),
                        CairoFont.WhiteSmallText(), 350, ElementBounds.Fixed(214, settingsY + 293, 60, 30), "tip-setting-protect-farm")

                    .AddRichtext(SuiteLocalization.Get("gui.protectnamed"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(560, settingsY + 300, 155, 24))
                    .AddSwitch(value => SetAnimalBool("protectnamedentities", value),
                        ElementBounds.Fixed(750, settingsY + 293, 60, 30), "animal-protect-named")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.49d2f18662"),
                        CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(750, settingsY + 293, 60, 30), "tip-setting-protect-named")

                    .AddRichtext(SuiteLocalization.Get("gui.keepnearest"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 347, 195, 24))
                    .AddSwitch(value => SetAnimalBool("keepnearesttoplayers", value),
                        ElementBounds.Fixed(244, settingsY + 340, 60, 30), "animal-keep-nearest")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.f1c59bbadf"),
                        CairoFont.WhiteSmallText(), 370, ElementBounds.Fixed(244, settingsY + 340, 60, 30), "tip-setting-keep-nearest")

                    // Logging.
                    .AddRichtext(SuiteLocalization.Get("gui.logging"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 394, 180, 24))
                    .AddSuiteInset(ElementBounds.Fixed(40, settingsY + 416, 980, 2))

                    .AddRichtext(SuiteLocalization.Get("gui.logactions"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(40, settingsY + 433, 120, 24))
                    .AddSwitch(value => SetAnimalBool("logactions", value),
                        ElementBounds.Fixed(170, settingsY + 426, 60, 30), "animal-log-actions")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.97d9ac81c8"),
                        CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(170, settingsY + 426, 60, 30), "tip-setting-log-actions")

                    .AddRichtext(SuiteLocalization.Get("gui.writecsv"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(560, settingsY + 433, 145, 24))
                    .AddSwitch(value => SetAnimalBool("logtocsv", value),
                        ElementBounds.Fixed(716, settingsY + 426, 60, 30), "animal-log-csv")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.a49f84c10a"),
                        CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(716, settingsY + 426, 60, 30), "tip-setting-log-csv")

                    .AddStaticText(SuiteLocalization.Get("gui.perruleradiushelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, settingsY + 486, 800, 24))
                    .AddStaticText(SuiteLocalization.Get("gui.switchessavehelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, settingsY + 520, 800, 24))
                    .AddSuiteButton(SuiteLocalization.Get("gui.refreshsettings"), () => SendAnimalLimiterOperation("settings"), refreshSettingsBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.e141999b1b"),
                        CairoFont.WhiteSmallText(), 340, refreshSettingsBounds.FlatCopy(), "tip-animal-settings-refresh");

                resultsY = settingsY + 560;
                resultsHeight = 1;
            }
            else
            {
                const double panelY = 122;
                var setRuleBounds = ElementBounds.Fixed(38, panelY + 54, 135, 34);
                var removeRuleBounds = ElementBounds.Fixed(190, panelY + 54, 165, 34);
                var previewBounds = ElementBounds.Fixed(372, panelY + 54, 130, 34);
                var cullBounds = ElementBounds.Fixed(519, panelY + 54, 135, 34);
                var targetBounds = ElementBounds.Fixed(38, panelY + 101, 165, 34);
                var refreshBounds = ElementBounds.Fixed(220, panelY + 101, 130, 34);
                var ruleRadiusBounds = ElementBounds.Fixed(194, panelY + 151, 90, 30);

                composer
                    .AddSuiteInset(ElementBounds.Fixed(contentX, panelY, contentWidth, 224))

                    // Row 1: rule + limit.
                    .AddRichtext(SuiteLocalization.Get("gui.rulepattern"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, panelY + 16, 95, 24))
                    .AddTextInput(ElementBounds.Fixed(138, panelY + 11, 390, 30),
                        value => animalPattern = value ?? "", CairoFont.WhiteSmallText(), "animal-pattern")
                    .AddRichtext(SuiteLocalization.Get("gui.limitlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550, panelY + 16, 42, 24))
                    .AddNumberInput(ElementBounds.Fixed(596, panelY + 11, 90, 30),
                        OnAnimalLimitChanged, CairoFont.WhiteSmallText(), "animal-limit")

                    // Row 2: rule actions.
                    .AddSuiteButton(SuiteLocalization.Get("gui.setrule"), () => SendAnimalLimiterOperation("set"), setRuleBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.55676587cc"),
                        CairoFont.WhiteSmallText(), 390, setRuleBounds.FlatCopy(), "tip-animal-set-rule")
                    .AddSuiteButton(SuiteLocalization.Get("gui.removerule"), () => SendAnimalLimiterOperation("remove"), removeRuleBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.b73cac663a"),
                        CairoFont.WhiteSmallText(), 350, removeRuleBounds.FlatCopy(), "tip-animal-remove-rule")
                    .AddSuiteButton(SuiteLocalization.Get("gui.preview"), () => SendAnimalLimiterOperation("preview"), previewBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.a77a8f75d1"),
                        CairoFont.WhiteSmallText(), 360, previewBounds.FlatCopy(), "tip-animal-preview")
                    .AddSuiteButton(SuiteLocalization.Get("gui.cullnow"), ConfirmCull, cullBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.dab2caacc6"),
                        CairoFont.WhiteSmallText(), 370, cullBounds.FlatCopy(), "tip-animal-cull")

                    // Row 3: target / refresh.
                    .AddSuiteButton(SuiteLocalization.Get("gui.limittarget"), () => SendAnimalLimiterOperation("target"), targetBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.2d9cdfee11"),
                        CairoFont.WhiteSmallText(), 370, targetBounds.FlatCopy(), "tip-animal-target")
                    .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => SendAnimalLimiterOperation("status"), refreshBounds)
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.df85d66407"),
                        CairoFont.WhiteSmallText(), 330, refreshBounds.FlatCopy(), "tip-animal-refresh")
                    .AddStaticText(SuiteLocalization.Get("gui.limittargethelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(372, panelY + 108, 440, 24))

                    // Row 4: radius stored with this rule.
                    .AddRichtext(SuiteLocalization.Get("gui.ruleradiuschunks"), CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(38, panelY + 157, 150, 24))
                    .AddNumberInput(ruleRadiusBounds,
                        OnAnimalRuleRadiusChanged, CairoFont.WhiteSmallText(), "animal-rule-radius")
                    .AddHoverText(SuiteLocalization.Get("gui.tip.more.beb9e4b5f3"),
                        CairoFont.WhiteSmallText(), 380, ruleRadiusBounds.FlatCopy(), "tip-animal-rule-radius")
                    .AddStaticText(SuiteLocalization.Get("gui.radiusscalehelp"),
                        CairoFont.WhiteSmallText(), ElementBounds.Fixed(306, panelY + 157, 500, 24))

                    .AddSuiteInset(ElementBounds.Fixed(contentX, 358, contentWidth, 312))
                    .AddRichtext($"<strong>{pageHeaderText}</strong>", CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(innerX, 372, 980, 26), "page-header");

                resultsY = 406;
                resultsHeight = 314;
            }
        }

        // A compact shell may shorten a result pane, but its scrollbar still makes
        // every row available. Keep the footer inside the window on inventory pages.
        resultsHeight = Math.Min(resultsHeight, Math.Max(0, shellHeight - 28 - resultsY));
        bool showAdminInventoryViewer = useGenericResults && IsAdminInventoryViewerResult();
        if (useGenericResults)
        {
            if (showAdminInventoryViewer) AddAdminInventoryViewer(composer, innerX, innerWidth, resultsY, resultsHeight);
            else AddScrollableResults(composer, innerX, innerWidth, resultsY, resultsHeight);
        }

        composer.Compose();
        SingleComposer = composer;

        if (useGenericResults && showAdminInventoryViewer)
        {
            ConfigureAdminInventoryScrollbar();
        }
        else if (useGenericResults)
        {
            ConfigureGenericResultsScrollbar();
        }
        else if (section == "animals" && animalView == "population")
        {
            ConfigurePopulationScrollbar();
            UpdatePopulationSlots();
        }
        else if (section == "animals" && animalView == "history")
        {
            ConfigureRemovalLogScrollbar();
            UpdateRemovalLogSlots();
        }

        // Never request controls that are not present in the active layout. The old history
        // branch fell through to the BlockLogger initializer and dereferenced a null NumberInput.
        if (section == "animals")
        {
            if (animalView == "limiter")
            {
                composer.GetNumberInput("animal-limit")?.SetValue(animalLimit.ToString());
                composer.GetTextInput("animal-pattern")?.SetValue(animalPattern);
                composer.GetNumberInput("animal-rule-radius")?.SetValue(animalRuleRadiusChunks.ToString());
            }
            else if (animalView == "settings")
            {
                composer.GetNumberInput("animal-default-limit")?.SetValue(animalDefaultLimit.ToString());
                composer.GetNumberInput("animal-default-radius")?.SetValue(animalRadiusChunks.ToString());
                composer.GetNumberInput("animal-protection-radius")?.SetValue(animalPlayerProtectionRadius.ToString());
                composer.GetNumberInput("animal-reconcile-seconds")?.SetValue(animalReconciliationSeconds.ToString());
            }
        }
        else if (section == "admincommands")
        {
            ApplyAdminCommandControlValues();
        }
        else if (section == "anticheat")
        {
            SingleComposer?.GetTextInput("anticheat-player-search")?.SetValue(antiCheatPlayerSearch);
            SingleComposer?.GetTextInput("anticheat-report-search")?.SetValue(antiCheatReportSearch);
            SingleComposer?.GetTextInput("anticheat-blacklist-mod")?.SetValue(antiCheatBlacklistModId);
            SingleComposer?.GetTextInput("anticheat-blacklist-source")?.SetValue(antiCheatBlacklistSourcePlayer);
            ApplyAntiCheatControlValues();
            ApplyAntiCheatEnforcementControlValues();
        }
        else if (section == "webserver")
        {
            ApplyWebControlValues();
            ApplyWebMapControlValues();
            ApplyDiscordControlValues();
        }
        else if (section == "loginprotection")
        {
            ApplyLoginProtectionControlValues();
        }
        else if (section == "profiler")
        {
            applyingProfilerControls = true;
            try
            {
                composer.GetSwitch("profiler-enabled")?.SetValue(profilerEnabled);
            }
            finally
            {
                applyingProfilerControls = false;
            }
        }
        else if (view == "settings")
        {
            ApplyBlockLoggerControlValues();
        }
        else if (view == "players")
        {
            composer.GetNumberInput("limit")?.SetValue(limit.ToString());
            composer.GetTextInput("player-search")?.SetValue(playerSearch);
        }
        else if (view != "status")
        {
            composer.GetNumberInput("radius")?.SetValue(radius.ToString());
            composer.GetNumberInput("limit")?.SetValue(limit.ToString());
            composer.GetTextInput("search")?.SetValue(search);
        }

    }

    private bool SelectSection(string selected)
    {
        if (selected == section) return true;
        if (section == "admincommands") CaptureAdminCommandInputs();
        if (selected != "profiler" && profilerLiveUpdates)
        {
            profilerLiveUpdates = false;
            profilerLiveRequestPending = false;
            profilerLiveGeneration++;
        }
        previousWorkspace = section;
        if (selected != "home") lastWorkspace = selected;
        section = selected;
        genericScrollValue = 0;

        // Remember each workspace's last page while refreshing its server data.
        // Every request still passes through the original permission-checked routes.
        switch (selected)
        {
            case "home":
                ComposeDialog();
                return true;
            case "animals":
                return SelectAnimalView(animalView);
            case "profiler":
                SelectProfilerView(profilerView);
                return RequestProfilerSnapshot();
            case "loginprotection":
                return SelectLoginProtectionView(loginView);
            case "anticheat":
                return SelectAntiCheatView(antiCheatView);
            case "admincommands":
                return SelectAdminCommandView(adminCommandView);
            case "webserver":
                return SelectWebView(webView);
            default:
                section = "blocklogger";
                if (blockLoggerView == "settings") return SelectBlockLoggerSettings();
                if (blockLoggerView == "status") return SelectStatus();
                return SelectView(blockLoggerView);
        }
    }

    private bool SelectAntiCheatView(string selected)
    {
        section = "anticheat";
        view = "anticheat";
        antiCheatView = selected is "players" or "player" or "reports" or "blacklist" or "enforcement" or "settings" or "packet" ? selected : "overview";
        genericScrollValue = 0;
        pageHeaderText = antiCheatView switch
        {
            "players" => "Anti-Cheat players & risk ratings",
            "player" => "Anti-Cheat player investigation",
            "reports" => "Persistent Anti-Cheat reports",
            "blacklist" => "Client mod blacklist",
            "enforcement" => "Anti-Cheat enforcement rules",
            "settings" => "Anti-Cheat settings",
            "packet" => SuiteLocalization.Get("gui.packetprotectiontitle"),
            _ => SuiteLocalization.Get("gui.runtime.anticheatoverview")
        };

        string loading = antiCheatView switch
        {
            "player" => "Enter a player name or UID and press Load.",
            "reports" => "Loading persistent Anti-Cheat reports...",
            "blacklist" => "Loading client mod blacklist...",
            "enforcement" => "Loading Anti-Cheat enforcement rules...",
            "settings" => "Loading Anti-Cheat settings...",
            "packet" => SuiteLocalization.Get("gui.loadingpacketprotection"),
            _ => "Loading anti-cheat data..."
        };
        SetGenericResults(new List<string> { loading });
        ComposeDialog();

        if (antiCheatView == "reports") return RequestAntiCheat("reports", antiCheatReportSearch);
        if (antiCheatView == "blacklist") return RequestAntiCheat("blacklist");
        if (antiCheatView == "enforcement") return RequestAntiCheat("enforcement");
        if (antiCheatView == "settings") return RequestAntiCheat("settings");
        if (antiCheatView == "packet") return RequestAntiCheat("packet");
        if (antiCheatView != "player") return RequestAntiCheat(antiCheatView);
        return true;
    }

    private bool SelectAntiCheatPacketView(string selected)
    {
        antiCheatPacketView = selected is "movement" or "reach" or "mining" or "risk" ? selected : "general";
        pageHeaderText = SuiteLocalization.Get("gui.packetprotectiontitle");
        ComposeDialog();
        return true;
    }

    private string BuildPacketTpsStatusText()
    {
        string readiness = acPacketTpsReady ? SuiteLocalization.Get("gui.ready") : SuiteLocalization.Get("gui.warmingup");
        string band = string.IsNullOrWhiteSpace(acPacketTpsBand) ? "Disabled" : acPacketTpsBand;
        return $"<strong>{SuiteLocalization.Get("gui.statusplain")}:</strong> {readiness}<br>" +
               $"<strong>{SuiteLocalization.Get("gui.tpsband")}:</strong> {WebUtility.HtmlEncode(band)}<br>" +
               $"<strong>{SuiteLocalization.Get("gui.targettps")}:</strong> {acPacketTpsTarget:0.##}<br>" +
               $"<strong>{SuiteLocalization.Get("gui.estimatedtps")}:</strong> {acPacketTpsEstimated:0.##}<br>" +
               $"<strong>{SuiteLocalization.Get("gui.healthpercent")}:</strong> {acPacketTpsHealthPercent:0.#}%";
    }

    private string BuildPacketIngressStatusText()
    {
        bool unknown = string.IsNullOrWhiteSpace(acPacketIngressStatus) || acPacketIngressStatus == "Unknown";
        string color = unknown ? "#aab8c7" : acPacketIngressActive ? "#8fda72" : acPacketIngressEnabled ? "#ff8080" : "#aab8c7";
        string status = unknown ? SuiteLocalization.Get("gui.workspace.awaitingdata") : ShortDisplay(acPacketIngressStatus, 28);
        return $"<font color=\"{color}\"><strong>{Escape(SuiteLocalization.Get("gui.workspace.ingressstatus", status))}</strong></font>";
    }

    private bool RequestAntiCheat(string operation, string? search = null, string? secondary = null)
    {
        var id = ++requestId;
        lastResponseId = id;
        sendAntiCheat(new AntiCheatGuiRequest
        {
            RequestId = id,
            Operation = operation,
            Search = search ?? antiCheatPlayerSearch,
            Secondary = secondary ?? ""
        });
        return true;
    }

    private void SetAntiCheatEnabled(bool value)
    {
        if (applyingAntiCheatControls) return;

        antiCheatEnabled = value;
        pageHeaderText = value ? SuiteLocalization.Get("gui.enablinganticheat") : SuiteLocalization.Get("gui.disablinganticheat");

        var id = ++requestId;
        lastResponseId = id;
        sendAntiCheat(new AntiCheatGuiRequest
        {
            RequestId = id,
            Operation = "setenabled",
            BoolValue = value
        });
    }

    private void ApplyAntiCheatControlValues()
    {
        if (SingleComposer == null) return;
        applyingAntiCheatControls = true;
        try
        {
            SingleComposer.GetSwitch("anticheat-enabled")?.SetValue(antiCheatEnabled);
            if (section != "anticheat") return;
            if (antiCheatView == "packet")
            {
                SingleComposer.GetSwitch("ac-pkt-master")?.SetValue(acPacketGuardEnabled);
                SingleComposer.GetSwitch("ac-pkt-warnings")?.SetValue(acPacketWarningsEnabled);
                SingleComposer.GetNumberInput("ac-pkt-warning-cooldown")?.SetValue(acPacketWarningCooldownSeconds.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-accel-sample")?.SetValue(acPacketAccelerationSampleMinMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-accel-smoothing")?.SetValue(acPacketAccelerationSmoothing.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-noclip")?.SetValue(acPacketNoclipEnabled);
                SingleComposer.GetNumberInput("ac-pkt-noclip-samples")?.SetValue(acPacketNoclipSamples.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-noclip-penetration")?.SetValue(acPacketNoclipPenetration.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-noclip-endpoint")?.SetValue(acPacketNoclipEndpointMovement.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-mount-ratio")?.SetValue(acPacketMountAccelerationRatio.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-diag-context")?.SetValue(acPacketDiagnosticContext);
                SingleComposer.GetSwitch("ac-pkt-diag-suppression")?.SetValue(acPacketDiagnosticSuppression);
                SingleComposer.GetNumberInput("ac-pkt-diag-history")?.SetValue(acPacketDiagnosticSpeedHistory.ToString(CultureInfo.InvariantCulture));

                SingleComposer.GetSwitch("ac-pkt-movement")?.SetValue(acMovementEnabled);
                SingleComposer.GetNumberInput("ac-pkt-movement-sample")?.SetValue(acPacketMovementSampleMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-hard-move")?.SetValue(acHardMoveDistance.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-movement-confirm-samples")?.SetValue(acPacketMovementConfirmSamples.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-movement-confirm-window")?.SetValue(acPacketMovementConfirmWindowMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-movement-extreme")?.SetValue(acPacketMovementExtremeMultiplier.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-correction-grace")?.SetValue(acPacketCorrectionGraceMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-reject-move")?.SetValue(acRejectUnauthorizedMovement);
                SingleComposer.GetSwitch("ac-pkt-exempt-admins")?.SetValue(acPacketExemptAdmins);
                SingleComposer.GetSwitch("ac-pkt-exempt-mounted")?.SetValue(acPacketExemptMountedPlayers);
                SingleComposer.GetNumberInput("ac-pkt-mount-grace")?.SetValue(acPacketMountGraceMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-engine-teleport-grace")?.SetValue(acPacketEngineTeleportGraceMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-tprunes")?.SetValue(acPacketTPRunesCompatibility);
                SingleComposer.GetNumberInput("ac-pkt-tprunes-grace")?.SetValue(acPacketTPRunesGraceMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-th3spawn")?.SetValue(acTh3EssentialsSpawnCompatibility);
                SingleComposer.GetNumberInput("ac-pkt-th3spawn-grace")?.SetValue(acTh3EssentialsSpawnGraceMs.ToString(CultureInfo.InvariantCulture));

                SingleComposer.GetSwitch("ac-pkt-reach")?.SetValue(acReachEnabled);
                SingleComposer.GetNumberInput("ac-pkt-reach-tolerance")?.SetValue(acReachTolerance.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-reach-latency")?.SetValue(acPacketReachLatencyCompensation);
                SingleComposer.GetNumberInput("ac-pkt-reach-max-latency")?.SetValue(acPacketReachMaxLatencyCompensation.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-reach-history")?.SetValue(acPacketReachPositionHistoryEnabled);
                SingleComposer.GetNumberInput("ac-pkt-reach-history-ms")?.SetValue(acPacketReachPositionHistoryMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-reach-rewind-ms")?.SetValue(acPacketReachMaxRewindMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-reach-use-extra")?.SetValue(acPacketReachUseExtraTolerance.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-reach-break-extra")?.SetValue(acPacketReachBreakExtraTolerance.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-reach-strikes")?.SetValue(acPacketReachNearThresholdStrikes.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-reach-strike-window")?.SetValue(acPacketReachStrikeWindowMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-reach-strike-decay")?.SetValue(acPacketReachStrikeDecayMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-reach-hard")?.SetValue(acReachHardExcess.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-reject-reach")?.SetValue(acRejectOutOfReach);

                SingleComposer.GetSwitch("ac-pkt-mining")?.SetValue(acMiningEnabled);
                SingleComposer.GetNumberInput("ac-pkt-vein-window")?.SetValue(acPacketVeinWindowMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-connected-break-limit")?.SetValue(acPacketConnectedBreakLimit.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-duplicate-target")?.SetValue(acPacketMiningDuplicateTargetMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-reject-bursts")?.SetValue(acRejectConnectedBursts);
                SingleComposer.GetSwitch("ac-pkt-mining-cadence")?.SetValue(acMiningCadenceEnabled);
                SingleComposer.GetNumberInput("ac-pkt-cadence-min")?.SetValue(acPacketMiningCadenceMinExpectedMs.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-mining-ratio")?.SetValue(acMiningCadenceRatio.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-mining-confirmations")?.SetValue(acMiningCadenceConfirmations.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-cadence-confirm-window")?.SetValue(acPacketMiningCadenceConfirmationWindowMs.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-reject-cadence")?.SetValue(acRejectImpossibleCadence);

                SingleComposer.GetSwitch("ac-pkt-tps")?.SetValue(acTpsAwarenessEnabled);
                SingleComposer.GetNumberInput("ac-pkt-tps-degraded")?.SetValue(acTpsDegradedPercent.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-tps-critical")?.SetValue(acTpsCriticalPercent.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-tps-max")?.SetValue(acTpsMaxMultiplier.ToString("0.###", CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-tps-suppress")?.SetValue(acTpsSuppressCriticalRejects);

                SingleComposer.GetSwitch("ac-pkt-family-risk")?.SetValue(acPerFamilyRiskEnabled);
                SingleComposer.GetNumberInput("ac-pkt-risk-move-decay")?.SetValue(acMovementRiskDecaySeconds.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-move-review")?.SetValue(acMovementRiskReviewThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-move-suspicious")?.SetValue(acMovementRiskSuspiciousThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-reach-decay")?.SetValue(acReachRiskDecaySeconds.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-reach-review")?.SetValue(acReachRiskReviewThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-reach-suspicious")?.SetValue(acReachRiskSuspiciousThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-mining-decay")?.SetValue(acMiningRiskDecaySeconds.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-mining-review")?.SetValue(acMiningRiskReviewThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-mining-suspicious")?.SetValue(acMiningRiskSuspiciousThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-packet-decay")?.SetValue(acPacketAbuseRiskDecaySeconds.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-packet-review")?.SetValue(acPacketAbuseRiskReviewThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-packet-suspicious")?.SetValue(acPacketAbuseRiskSuspiciousThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-client-decay")?.SetValue(acClientIntegrityRiskDecaySeconds.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-client-review")?.SetValue(acClientIntegrityRiskReviewThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-risk-client-suspicious")?.SetValue(acClientIntegrityRiskSuspiciousThreshold.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-correlation")?.SetValue(acIncidentCorrelationEnabled);
                SingleComposer.GetNumberInput("ac-pkt-incident-window")?.SetValue(acIncidentWindowSeconds.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-incident-units")?.SetValue(acIncidentRequiredCategories.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-incident-bonus")?.SetValue(acIncidentMaxRiskBonus.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("ac-pkt-history")?.SetValue(acHistoryEnabled);
                SingleComposer.GetNumberInput("ac-pkt-history-days")?.SetValue(acHistoryRetentionDays.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("ac-pkt-history-max")?.SetValue(acHistoryMaxRecords.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (antiCheatView != "settings") return;

            SingleComposer.GetSwitch("ac-set-require-client")?.SetValue(acRequireClient);
            SingleComposer.GetNumberInput("ac-set-rescan")?.SetValue(acRescanMinutes.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetSwitch("ac-set-runtime-rescan")?.SetValue(acRuntimeRescanOnChange);
            SingleComposer.GetNumberInput("ac-set-runtime-cooldown")?.SetValue(acRuntimeCooldownSeconds.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetSwitch("ac-set-similarity")?.SetValue(acSimilarityDetection);
            SingleComposer.GetNumberInput("ac-set-sim-review")?.SetValue(acSimilarityReviewPercent.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("ac-set-sim-high")?.SetValue(acSimilarityHighPercent.ToString(CultureInfo.InvariantCulture));

            SingleComposer.GetSwitch("ac-set-packetguard")?.SetValue(acPacketGuardEnabled);
            SingleComposer.GetSwitch("ac-set-movement")?.SetValue(acMovementEnabled);
            SingleComposer.GetNumberInput("ac-set-hard-move")?.SetValue(acHardMoveDistance.ToString("0.###", CultureInfo.InvariantCulture));
            SingleComposer.GetSwitch("ac-set-reject-move")?.SetValue(acRejectUnauthorizedMovement);
            SingleComposer.GetSwitch("ac-set-th3spawn")?.SetValue(acTh3EssentialsSpawnCompatibility);
            SingleComposer.GetNumberInput("ac-set-th3spawn-grace")?.SetValue(acTh3EssentialsSpawnGraceMs.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetSwitch("ac-set-reach")?.SetValue(acReachEnabled);
            SingleComposer.GetNumberInput("ac-set-reach-tolerance")?.SetValue(acReachTolerance.ToString("0.###", CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("ac-set-reach-hard")?.SetValue(acReachHardExcess.ToString("0.###", CultureInfo.InvariantCulture));
            SingleComposer.GetSwitch("ac-set-reject-reach")?.SetValue(acRejectOutOfReach);

            SingleComposer.GetSwitch("ac-set-mining")?.SetValue(acMiningEnabled);
            SingleComposer.GetSwitch("ac-set-reject-bursts")?.SetValue(acRejectConnectedBursts);
            SingleComposer.GetSwitch("ac-set-mining-cadence")?.SetValue(acMiningCadenceEnabled);
            SingleComposer.GetNumberInput("ac-set-mining-ratio")?.SetValue(acMiningCadenceRatio.ToString("0.###", CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("ac-set-mining-confirmations")?.SetValue(acMiningCadenceConfirmations.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetSwitch("ac-set-reject-cadence")?.SetValue(acRejectImpossibleCadence);

            SingleComposer.GetSwitch("ac-set-tps")?.SetValue(acTpsAwarenessEnabled);
            SingleComposer.GetNumberInput("ac-set-tps-degraded")?.SetValue(acTpsDegradedPercent.ToString("0.###", CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("ac-set-tps-critical")?.SetValue(acTpsCriticalPercent.ToString("0.###", CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("ac-set-tps-max")?.SetValue(acTpsMaxMultiplier.ToString("0.###", CultureInfo.InvariantCulture));
            SingleComposer.GetSwitch("ac-set-tps-suppress")?.SetValue(acTpsSuppressCriticalRejects);
            SingleComposer.GetSwitch("ac-set-correlation")?.SetValue(acIncidentCorrelationEnabled);
            SingleComposer.GetNumberInput("ac-set-incident-window")?.SetValue(acIncidentWindowSeconds.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("ac-set-incident-units")?.SetValue(acIncidentRequiredCategories.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("ac-set-incident-bonus")?.SetValue(acIncidentMaxRiskBonus.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetSwitch("ac-set-history")?.SetValue(acHistoryEnabled);
            SingleComposer.GetNumberInput("ac-set-history-days")?.SetValue(acHistoryRetentionDays.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("ac-set-history-max")?.SetValue(acHistoryMaxRecords.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            applyingAntiCheatControls = false;
        }
    }

    private void CopyAntiCheatPacketProtectionSettings(AntiCheatPacketProtectionGuiPacket settings)
    {
        acPacketGuardEnabled = settings.PacketGuardEnabled;
        acPacketIngressEnabled = settings.IngressEnabled;
        acPacketIngressActive = settings.IngressActive;
        acPacketIngressStatus = settings.IngressStatus ?? "Unknown";
        acPacketWarningsEnabled = settings.PlayerWarningEnabled;
        acPacketWarningCooldownSeconds = settings.PlayerWarningCooldownSeconds;
        acPacketAccelerationSampleMinMs = settings.AccelerationSampleMinMs;
        acPacketAccelerationSmoothing = settings.AccelerationSmoothingFactor;
        acPacketNoclipEnabled = settings.NoclipEnabled;
        acPacketNoclipSamples = settings.NoclipRequiredCollisionSamples;
        acPacketNoclipPenetration = settings.NoclipMinPenetrationDepth;
        acPacketNoclipEndpointMovement = settings.NoclipEndpointMinNetMovement;
        acPacketMountAccelerationRatio = settings.MountAccelerationMinSpeedRatio;
        acPacketDiagnosticContext = settings.DiagnosticContextEnabled;
        acPacketDiagnosticSuppression = settings.DiagnosticSuppressionEnabled;
        acPacketDiagnosticSpeedHistory = settings.DiagnosticSpeedHistorySamples;

        acMovementEnabled = settings.MovementEnabled;
        acPacketMovementSampleMs = settings.MovementSampleMs;
        acHardMoveDistance = settings.HardMoveDistance;
        acPacketMovementConfirmSamples = settings.MovementConfirmSamples;
        acPacketMovementConfirmWindowMs = settings.MovementConfirmWindowMs;
        acPacketMovementExtremeMultiplier = settings.MovementExtremeMultiplier;
        acPacketCorrectionGraceMs = settings.CorrectionGraceMs;
        acRejectUnauthorizedMovement = settings.RejectUnauthorizedMovement;
        acPacketExemptAdmins = settings.ExemptAdmins;
        acPacketExemptMountedPlayers = settings.ExemptMountedPlayers;
        acPacketMountGraceMs = settings.MountGraceMs;
        acPacketEngineTeleportGraceMs = settings.EngineTeleportGraceMs;
        acPacketTPRunesCompatibility = settings.TPRunesCompatibility;
        acPacketTPRunesGraceMs = settings.TPRunesGraceMs;
        acPacketTPRunesDetected = settings.TPRunesDetected;
        acTh3EssentialsSpawnCompatibility = settings.Th3EssentialsSpawnCompatibility;
        acTh3EssentialsSpawnGraceMs = settings.Th3EssentialsSpawnGraceMs;
        acTh3EssentialsDetected = settings.Th3EssentialsDetected;

        acReachEnabled = settings.ReachEnabled;
        acReachTolerance = settings.ReachTolerance;
        acPacketReachLatencyCompensation = settings.ReachLatencyCompensation;
        acPacketReachMaxLatencyCompensation = settings.ReachMaxLatencyCompensation;
        acPacketReachPositionHistoryEnabled = settings.ReachPositionHistoryEnabled;
        acPacketReachPositionHistoryMs = settings.ReachPositionHistoryMs;
        acPacketReachMaxRewindMs = settings.ReachMaxRewindMs;
        acPacketReachUseExtraTolerance = settings.ReachUseExtraTolerance;
        acPacketReachBreakExtraTolerance = settings.ReachBreakExtraTolerance;
        acPacketReachNearThresholdStrikes = settings.ReachNearThresholdStrikes;
        acPacketReachStrikeWindowMs = settings.ReachStrikeWindowMs;
        acPacketReachStrikeDecayMs = settings.ReachStrikeDecayMs;
        acReachHardExcess = settings.ReachHardExcess;
        acRejectOutOfReach = settings.RejectOutOfReachActions;

        acMiningEnabled = settings.MiningEnabled;
        acPacketVeinWindowMs = settings.VeinWindowMs;
        acPacketConnectedBreakLimit = settings.ConnectedBreakLimit;
        acPacketMiningDuplicateTargetMs = settings.MiningDuplicateTargetMs;
        acRejectConnectedBursts = settings.RejectConnectedBreakBursts;
        acMiningCadenceEnabled = settings.MiningCadenceValidationEnabled;
        acPacketMiningCadenceMinExpectedMs = settings.MiningCadenceMinExpectedMs;
        acMiningCadenceRatio = settings.MiningCadenceSuspiciousRatio;
        acMiningCadenceConfirmations = settings.MiningCadenceConfirmations;
        acPacketMiningCadenceConfirmationWindowMs = settings.MiningCadenceConfirmationWindowMs;
        acRejectImpossibleCadence = settings.RejectImpossibleMiningCadence;

        acTpsAwarenessEnabled = settings.TpsAwarenessEnabled;
        acTpsDegradedPercent = settings.TpsDegradedPercent;
        acTpsCriticalPercent = settings.TpsCriticalPercent;
        acTpsMaxMultiplier = settings.TpsMaxTimingMultiplier;
        acTpsSuppressCriticalRejects = settings.TpsSuppressTimingRejectsWhenCritical;
        acPacketTpsReady = settings.TpsReady;
        acPacketTpsTarget = settings.TpsTarget;
        acPacketTpsEstimated = settings.TpsEstimated;
        acPacketTpsHealthPercent = settings.TpsHealthPercent;
        acPacketTpsBand = settings.TpsBand ?? "Disabled";

        acPerFamilyRiskEnabled = settings.PerFamilyRiskEnabled;
        acMovementRiskDecaySeconds = settings.MovementRiskDecaySeconds;
        acMovementRiskReviewThreshold = settings.MovementRiskReviewThreshold;
        acMovementRiskSuspiciousThreshold = settings.MovementRiskSuspiciousThreshold;
        acReachRiskDecaySeconds = settings.ReachRiskDecaySeconds;
        acReachRiskReviewThreshold = settings.ReachRiskReviewThreshold;
        acReachRiskSuspiciousThreshold = settings.ReachRiskSuspiciousThreshold;
        acMiningRiskDecaySeconds = settings.MiningRiskDecaySeconds;
        acMiningRiskReviewThreshold = settings.MiningRiskReviewThreshold;
        acMiningRiskSuspiciousThreshold = settings.MiningRiskSuspiciousThreshold;
        acPacketAbuseRiskDecaySeconds = settings.PacketAbuseRiskDecaySeconds;
        acPacketAbuseRiskReviewThreshold = settings.PacketAbuseRiskReviewThreshold;
        acPacketAbuseRiskSuspiciousThreshold = settings.PacketAbuseRiskSuspiciousThreshold;
        acClientIntegrityRiskDecaySeconds = settings.ClientIntegrityRiskDecaySeconds;
        acClientIntegrityRiskReviewThreshold = settings.ClientIntegrityRiskReviewThreshold;
        acClientIntegrityRiskSuspiciousThreshold = settings.ClientIntegrityRiskSuspiciousThreshold;

        acIncidentCorrelationEnabled = settings.IncidentCorrelationEnabled;
        acIncidentWindowSeconds = settings.IncidentCorrelationWindowSeconds;
        acIncidentRequiredCategories = settings.IncidentCorrelationRequiredCategories;
        acIncidentMaxRiskBonus = settings.IncidentCorrelationMaxRiskBonus;
        acHistoryEnabled = settings.HistoryEnabled;
        acHistoryRetentionDays = settings.HistoryRetentionDays;
        acHistoryMaxRecords = settings.HistoryMaxRecords;
    }

    private void SetAntiCheatPacketBool(string key, bool value)
    {
        if (applyingAntiCheatControls) return;
        switch (key)
        {
            case "master": acPacketGuardEnabled = value; break;
            case "warnings": acPacketWarningsEnabled = value; break;
            case "noclip": acPacketNoclipEnabled = value; break;
            case "diag-context": acPacketDiagnosticContext = value; break;
            case "diag-suppression": acPacketDiagnosticSuppression = value; break;
            case "movement": acMovementEnabled = value; break;
            case "reject-move": acRejectUnauthorizedMovement = value; break;
            case "exempt-admins": acPacketExemptAdmins = value; break;
            case "exempt-mounted": acPacketExemptMountedPlayers = value; break;
            case "tprunes": acPacketTPRunesCompatibility = value; break;
            case "th3spawn": acTh3EssentialsSpawnCompatibility = value; break;
            case "reach": acReachEnabled = value; break;
            case "reach-latency": acPacketReachLatencyCompensation = value; break;
            case "reach-history": acPacketReachPositionHistoryEnabled = value; break;
            case "reject-reach": acRejectOutOfReach = value; break;
            case "mining": acMiningEnabled = value; break;
            case "reject-bursts": acRejectConnectedBursts = value; break;
            case "mining-cadence": acMiningCadenceEnabled = value; break;
            case "reject-cadence": acRejectImpossibleCadence = value; break;
            case "tps": acTpsAwarenessEnabled = value; break;
            case "tps-suppress": acTpsSuppressCriticalRejects = value; break;
            case "family-risk": acPerFamilyRiskEnabled = value; break;
            case "correlation": acIncidentCorrelationEnabled = value; break;
            case "history": acHistoryEnabled = value; break;
        }
    }

    private void SetAntiCheatPacketInt(string key, string value)
    {
        if (applyingAntiCheatControls || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return;
        switch (key)
        {
            case "warning-cooldown": acPacketWarningCooldownSeconds = parsed; break;
            case "accel-sample": acPacketAccelerationSampleMinMs = parsed; break;
            case "noclip-samples": acPacketNoclipSamples = parsed; break;
            case "diag-history": acPacketDiagnosticSpeedHistory = parsed; break;
            case "movement-sample": acPacketMovementSampleMs = parsed; break;
            case "movement-confirm-samples": acPacketMovementConfirmSamples = parsed; break;
            case "movement-confirm-window": acPacketMovementConfirmWindowMs = parsed; break;
            case "correction-grace": acPacketCorrectionGraceMs = parsed; break;
            case "mount-grace": acPacketMountGraceMs = parsed; break;
            case "engine-teleport-grace": acPacketEngineTeleportGraceMs = parsed; break;
            case "tprunes-grace": acPacketTPRunesGraceMs = parsed; break;
            case "th3spawn-grace": acTh3EssentialsSpawnGraceMs = parsed; break;
            case "reach-history-ms": acPacketReachPositionHistoryMs = parsed; break;
            case "reach-rewind-ms": acPacketReachMaxRewindMs = parsed; break;
            case "reach-strikes": acPacketReachNearThresholdStrikes = parsed; break;
            case "reach-strike-window": acPacketReachStrikeWindowMs = parsed; break;
            case "reach-strike-decay": acPacketReachStrikeDecayMs = parsed; break;
            case "vein-window": acPacketVeinWindowMs = parsed; break;
            case "connected-break-limit": acPacketConnectedBreakLimit = parsed; break;
            case "duplicate-target": acPacketMiningDuplicateTargetMs = parsed; break;
            case "mining-confirmations": acMiningCadenceConfirmations = parsed; break;
            case "cadence-confirm-window": acPacketMiningCadenceConfirmationWindowMs = parsed; break;
            case "risk-move-decay": acMovementRiskDecaySeconds = parsed; break;
            case "risk-move-review": acMovementRiskReviewThreshold = parsed; break;
            case "risk-move-suspicious": acMovementRiskSuspiciousThreshold = parsed; break;
            case "risk-reach-decay": acReachRiskDecaySeconds = parsed; break;
            case "risk-reach-review": acReachRiskReviewThreshold = parsed; break;
            case "risk-reach-suspicious": acReachRiskSuspiciousThreshold = parsed; break;
            case "risk-mining-decay": acMiningRiskDecaySeconds = parsed; break;
            case "risk-mining-review": acMiningRiskReviewThreshold = parsed; break;
            case "risk-mining-suspicious": acMiningRiskSuspiciousThreshold = parsed; break;
            case "risk-packet-decay": acPacketAbuseRiskDecaySeconds = parsed; break;
            case "risk-packet-review": acPacketAbuseRiskReviewThreshold = parsed; break;
            case "risk-packet-suspicious": acPacketAbuseRiskSuspiciousThreshold = parsed; break;
            case "risk-client-decay": acClientIntegrityRiskDecaySeconds = parsed; break;
            case "risk-client-review": acClientIntegrityRiskReviewThreshold = parsed; break;
            case "risk-client-suspicious": acClientIntegrityRiskSuspiciousThreshold = parsed; break;
            case "incident-window": acIncidentWindowSeconds = parsed; break;
            case "incident-units": acIncidentRequiredCategories = parsed; break;
            case "incident-bonus": acIncidentMaxRiskBonus = parsed; break;
            case "history-days": acHistoryRetentionDays = parsed; break;
            case "history-max": acHistoryMaxRecords = Math.Clamp(parsed, 100, 1000); break;
        }
    }

    private void SetAntiCheatPacketDouble(string key, string value)
    {
        if (applyingAntiCheatControls) return;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            !double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed)) return;
        switch (key)
        {
            case "accel-smoothing": acPacketAccelerationSmoothing = parsed; break;
            case "noclip-penetration": acPacketNoclipPenetration = parsed; break;
            case "noclip-endpoint": acPacketNoclipEndpointMovement = parsed; break;
            case "mount-ratio": acPacketMountAccelerationRatio = parsed; break;
            case "hard-move": acHardMoveDistance = parsed; break;
            case "movement-extreme": acPacketMovementExtremeMultiplier = parsed; break;
            case "reach-tolerance": acReachTolerance = parsed; break;
            case "reach-max-latency": acPacketReachMaxLatencyCompensation = parsed; break;
            case "reach-use-extra": acPacketReachUseExtraTolerance = parsed; break;
            case "reach-break-extra": acPacketReachBreakExtraTolerance = parsed; break;
            case "reach-hard": acReachHardExcess = parsed; break;
            case "cadence-min": acPacketMiningCadenceMinExpectedMs = parsed; break;
            case "mining-ratio": acMiningCadenceRatio = parsed; break;
            case "tps-degraded": acTpsDegradedPercent = parsed; break;
            case "tps-critical": acTpsCriticalPercent = parsed; break;
            case "tps-max": acTpsMaxMultiplier = parsed; break;
        }
    }

    private bool SaveAntiCheatPacketProtectionSettings()
    {
        pageHeaderText = SuiteLocalization.Get("gui.savingpacketprotection");
        var id = ++requestId;
        lastResponseId = id;
        sendAntiCheat(new AntiCheatGuiRequest
        {
            RequestId = id,
            Operation = "packetsave",
            PacketSettings = new AntiCheatPacketProtectionGuiPacket
            {
                PacketGuardEnabled = acPacketGuardEnabled,
                PlayerWarningEnabled = acPacketWarningsEnabled,
                PlayerWarningCooldownSeconds = acPacketWarningCooldownSeconds,
                AccelerationSampleMinMs = acPacketAccelerationSampleMinMs,
                AccelerationSmoothingFactor = acPacketAccelerationSmoothing,
                NoclipEnabled = acPacketNoclipEnabled,
                NoclipRequiredCollisionSamples = acPacketNoclipSamples,
                NoclipMinPenetrationDepth = acPacketNoclipPenetration,
                NoclipEndpointMinNetMovement = acPacketNoclipEndpointMovement,
                MountAccelerationMinSpeedRatio = acPacketMountAccelerationRatio,
                DiagnosticContextEnabled = acPacketDiagnosticContext,
                DiagnosticSuppressionEnabled = acPacketDiagnosticSuppression,
                DiagnosticSpeedHistorySamples = acPacketDiagnosticSpeedHistory,
                MovementEnabled = acMovementEnabled,
                MovementSampleMs = acPacketMovementSampleMs,
                HardMoveDistance = acHardMoveDistance,
                MovementConfirmSamples = acPacketMovementConfirmSamples,
                MovementConfirmWindowMs = acPacketMovementConfirmWindowMs,
                MovementExtremeMultiplier = acPacketMovementExtremeMultiplier,
                CorrectionGraceMs = acPacketCorrectionGraceMs,
                RejectUnauthorizedMovement = acRejectUnauthorizedMovement,
                ExemptAdmins = acPacketExemptAdmins,
                ExemptMountedPlayers = acPacketExemptMountedPlayers,
                MountGraceMs = acPacketMountGraceMs,
                EngineTeleportGraceMs = acPacketEngineTeleportGraceMs,
                TPRunesCompatibility = acPacketTPRunesCompatibility,
                TPRunesGraceMs = acPacketTPRunesGraceMs,
                TPRunesDetected = acPacketTPRunesDetected,
                Th3EssentialsSpawnCompatibility = acTh3EssentialsSpawnCompatibility,
                Th3EssentialsSpawnGraceMs = acTh3EssentialsSpawnGraceMs,
                Th3EssentialsDetected = acTh3EssentialsDetected,
                ReachEnabled = acReachEnabled,
                ReachTolerance = acReachTolerance,
                ReachLatencyCompensation = acPacketReachLatencyCompensation,
                ReachMaxLatencyCompensation = acPacketReachMaxLatencyCompensation,
                ReachPositionHistoryEnabled = acPacketReachPositionHistoryEnabled,
                ReachPositionHistoryMs = acPacketReachPositionHistoryMs,
                ReachMaxRewindMs = acPacketReachMaxRewindMs,
                ReachUseExtraTolerance = acPacketReachUseExtraTolerance,
                ReachBreakExtraTolerance = acPacketReachBreakExtraTolerance,
                ReachNearThresholdStrikes = acPacketReachNearThresholdStrikes,
                ReachStrikeWindowMs = acPacketReachStrikeWindowMs,
                ReachStrikeDecayMs = acPacketReachStrikeDecayMs,
                ReachHardExcess = acReachHardExcess,
                RejectOutOfReachActions = acRejectOutOfReach,
                MiningEnabled = acMiningEnabled,
                VeinWindowMs = acPacketVeinWindowMs,
                ConnectedBreakLimit = acPacketConnectedBreakLimit,
                MiningDuplicateTargetMs = acPacketMiningDuplicateTargetMs,
                RejectConnectedBreakBursts = acRejectConnectedBursts,
                MiningCadenceValidationEnabled = acMiningCadenceEnabled,
                MiningCadenceMinExpectedMs = acPacketMiningCadenceMinExpectedMs,
                MiningCadenceSuspiciousRatio = acMiningCadenceRatio,
                MiningCadenceConfirmations = acMiningCadenceConfirmations,
                MiningCadenceConfirmationWindowMs = acPacketMiningCadenceConfirmationWindowMs,
                RejectImpossibleMiningCadence = acRejectImpossibleCadence,
                TpsAwarenessEnabled = acTpsAwarenessEnabled,
                TpsDegradedPercent = acTpsDegradedPercent,
                TpsCriticalPercent = acTpsCriticalPercent,
                TpsMaxTimingMultiplier = acTpsMaxMultiplier,
                TpsSuppressTimingRejectsWhenCritical = acTpsSuppressCriticalRejects,
                TpsReady = acPacketTpsReady,
                TpsTarget = acPacketTpsTarget,
                TpsEstimated = acPacketTpsEstimated,
                TpsHealthPercent = acPacketTpsHealthPercent,
                TpsBand = acPacketTpsBand,
                PerFamilyRiskEnabled = acPerFamilyRiskEnabled,
                MovementRiskDecaySeconds = acMovementRiskDecaySeconds,
                MovementRiskReviewThreshold = acMovementRiskReviewThreshold,
                MovementRiskSuspiciousThreshold = acMovementRiskSuspiciousThreshold,
                ReachRiskDecaySeconds = acReachRiskDecaySeconds,
                ReachRiskReviewThreshold = acReachRiskReviewThreshold,
                ReachRiskSuspiciousThreshold = acReachRiskSuspiciousThreshold,
                MiningRiskDecaySeconds = acMiningRiskDecaySeconds,
                MiningRiskReviewThreshold = acMiningRiskReviewThreshold,
                MiningRiskSuspiciousThreshold = acMiningRiskSuspiciousThreshold,
                PacketAbuseRiskDecaySeconds = acPacketAbuseRiskDecaySeconds,
                PacketAbuseRiskReviewThreshold = acPacketAbuseRiskReviewThreshold,
                PacketAbuseRiskSuspiciousThreshold = acPacketAbuseRiskSuspiciousThreshold,
                ClientIntegrityRiskDecaySeconds = acClientIntegrityRiskDecaySeconds,
                ClientIntegrityRiskReviewThreshold = acClientIntegrityRiskReviewThreshold,
                ClientIntegrityRiskSuspiciousThreshold = acClientIntegrityRiskSuspiciousThreshold,
                IncidentCorrelationEnabled = acIncidentCorrelationEnabled,
                IncidentCorrelationWindowSeconds = acIncidentWindowSeconds,
                IncidentCorrelationRequiredCategories = acIncidentRequiredCategories,
                IncidentCorrelationMaxRiskBonus = acIncidentMaxRiskBonus,
                HistoryEnabled = acHistoryEnabled,
                HistoryRetentionDays = acHistoryRetentionDays,
                HistoryMaxRecords = acHistoryMaxRecords
            }
        });
        return true;
    }

    private void CopyAntiCheatSettings(AntiCheatGuiSettingsPacket settings)
    {
        acRequireClient = settings.RequireClient;
        acRescanMinutes = settings.RescanMinutes;
        acRuntimeRescanOnChange = settings.RuntimeRescanOnChange;
        acRuntimeCooldownSeconds = settings.RuntimeChangeRescanCooldownSeconds;
        acSimilarityDetection = settings.SimilarityDetectionEnabled;
        acSimilarityReviewPercent = settings.SimilarityReviewThresholdPercent;
        acSimilarityHighPercent = settings.SimilarityHighThresholdPercent;
        acPacketGuardEnabled = settings.PacketGuardEnabled;
        acMovementEnabled = settings.MovementEnabled;
        acHardMoveDistance = settings.HardMoveDistance;
        acRejectUnauthorizedMovement = settings.RejectUnauthorizedMovement;
        acTh3EssentialsSpawnCompatibility = settings.Th3EssentialsSpawnCompatibility;
        acTh3EssentialsSpawnGraceMs = settings.Th3EssentialsSpawnGraceMs;
        acTh3EssentialsDetected = settings.Th3EssentialsDetected;
        acReachEnabled = settings.ReachEnabled;
        acReachTolerance = settings.ReachTolerance;
        acReachHardExcess = settings.ReachHardExcess;
        acRejectOutOfReach = settings.RejectOutOfReachActions;
        acMiningEnabled = settings.MiningEnabled;
        acMiningCadenceEnabled = settings.MiningCadenceValidationEnabled;
        acMiningCadenceRatio = settings.MiningCadenceSuspiciousRatio;
        acMiningCadenceConfirmations = settings.MiningCadenceConfirmations;
        acRejectConnectedBursts = settings.RejectConnectedBreakBursts;
        acRejectImpossibleCadence = settings.RejectImpossibleMiningCadence;
        acTpsAwarenessEnabled = settings.TpsAwarenessEnabled;
        acTpsDegradedPercent = settings.TpsDegradedPercent;
        acTpsCriticalPercent = settings.TpsCriticalPercent;
        acTpsMaxMultiplier = settings.TpsMaxTimingMultiplier;
        acTpsSuppressCriticalRejects = settings.TpsSuppressTimingRejectsWhenCritical;
        acIncidentCorrelationEnabled = settings.IncidentCorrelationEnabled;
        acIncidentWindowSeconds = settings.IncidentCorrelationWindowSeconds;
        acIncidentRequiredCategories = settings.IncidentCorrelationRequiredCategories;
        acIncidentMaxRiskBonus = settings.IncidentCorrelationMaxRiskBonus;
        acHistoryEnabled = settings.HistoryEnabled;
        acHistoryRetentionDays = settings.HistoryRetentionDays;
        acHistoryMaxRecords = settings.HistoryMaxRecords;
    }

    private void SetAntiCheatSettingBool(string key, bool value)
    {
        if (applyingAntiCheatControls) return;
        switch (key)
        {
            case "requireclient": acRequireClient = value; break;
            case "runtime-rescan": acRuntimeRescanOnChange = value; break;
            case "similarity": acSimilarityDetection = value; break;
            case "packetguard": acPacketGuardEnabled = value; break;
            case "movement": acMovementEnabled = value; break;
            case "reject-move": acRejectUnauthorizedMovement = value; break;
            case "th3spawn": acTh3EssentialsSpawnCompatibility = value; break;
            case "reach": acReachEnabled = value; break;
            case "reject-reach": acRejectOutOfReach = value; break;
            case "mining": acMiningEnabled = value; break;
            case "reject-bursts": acRejectConnectedBursts = value; break;
            case "mining-cadence": acMiningCadenceEnabled = value; break;
            case "reject-cadence": acRejectImpossibleCadence = value; break;
            case "tps": acTpsAwarenessEnabled = value; break;
            case "tps-suppress": acTpsSuppressCriticalRejects = value; break;
            case "correlation": acIncidentCorrelationEnabled = value; break;
            case "history": acHistoryEnabled = value; break;
        }
    }

    private void SetAntiCheatSettingInt(string key, string value)
    {
        if (applyingAntiCheatControls || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return;
        switch (key)
        {
            case "rescan": acRescanMinutes = parsed; break;
            case "runtime-cooldown": acRuntimeCooldownSeconds = parsed; break;
            case "similarity-review": acSimilarityReviewPercent = parsed; break;
            case "similarity-high": acSimilarityHighPercent = parsed; break;
            case "th3spawn-grace": acTh3EssentialsSpawnGraceMs = parsed; break;
            case "mining-confirmations": acMiningCadenceConfirmations = parsed; break;
            case "incident-window": acIncidentWindowSeconds = parsed; break;
            case "incident-units": acIncidentRequiredCategories = parsed; break;
            case "incident-bonus": acIncidentMaxRiskBonus = parsed; break;
            case "history-days": acHistoryRetentionDays = parsed; break;
            case "history-max": acHistoryMaxRecords = Math.Clamp(parsed, 100, 1000); break;
        }
    }

    private void SetAntiCheatSettingDouble(string key, string value)
    {
        if (applyingAntiCheatControls) return;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            !double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed)) return;
        switch (key)
        {
            case "hard-move": acHardMoveDistance = parsed; break;
            case "reach-tolerance": acReachTolerance = parsed; break;
            case "reach-hard": acReachHardExcess = parsed; break;
            case "mining-ratio": acMiningCadenceRatio = parsed; break;
            case "tps-degraded": acTpsDegradedPercent = parsed; break;
            case "tps-critical": acTpsCriticalPercent = parsed; break;
            case "tps-max": acTpsMaxMultiplier = parsed; break;
        }
    }

    private bool SaveAntiCheatSettings()
    {
        pageHeaderText = SuiteLocalization.Get("gui.savinganticheatsettings");
        var id = ++requestId;
        lastResponseId = id;
        sendAntiCheat(new AntiCheatGuiRequest
        {
            RequestId = id,
            Operation = "settingssave",
            Settings = new AntiCheatGuiSettingsPacket
            {
                RequireClient = acRequireClient,
                RescanMinutes = acRescanMinutes,
                RuntimeRescanOnChange = acRuntimeRescanOnChange,
                RuntimeChangeRescanCooldownSeconds = acRuntimeCooldownSeconds,
                SimilarityDetectionEnabled = acSimilarityDetection,
                SimilarityReviewThresholdPercent = acSimilarityReviewPercent,
                SimilarityHighThresholdPercent = acSimilarityHighPercent
            }
        });
        return true;
    }

    private void CopyAntiCheatEnforcementRules(List<AntiCheatConsequenceRuleGuiPacket>? rules)
    {
        string selectedId = antiCheatEnforcementSelectedIndex >= 0 && antiCheatEnforcementSelectedIndex < antiCheatEnforcementRules.Count
            ? antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex].Id
            : "";

        antiCheatEnforcementRules.Clear();
        if (rules != null)
        {
            foreach (var rule in rules.Take(64))
                antiCheatEnforcementRules.Add(CloneAntiCheatRulePacket(rule));
        }

        antiCheatEnforcementSelectedIndex = !string.IsNullOrWhiteSpace(selectedId)
            ? antiCheatEnforcementRules.FindIndex(x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            : (antiCheatEnforcementRules.Count > 0 ? 0 : -1);
        if (antiCheatEnforcementSelectedIndex < 0 && antiCheatEnforcementRules.Count > 0)
            antiCheatEnforcementSelectedIndex = 0;

        LoadAntiCheatRuleEditorFromSelection();
    }

    private static AntiCheatConsequenceRuleGuiPacket CloneAntiCheatRulePacket(AntiCheatConsequenceRuleGuiPacket rule) => new()
    {
        Id = rule.Id ?? "",
        Name = rule.Name ?? "",
        Enabled = rule.Enabled,
        TriggerType = rule.TriggerType ?? "FindingCode",
        TriggerValue = rule.TriggerValue ?? "",
        Subject = rule.Subject ?? "",
        Action = rule.Action ?? "Log",
        Duration = rule.Duration ?? "30m",
        Message = rule.Message ?? ""
    };

    private void LoadAntiCheatRuleEditorFromSelection()
    {
        if (antiCheatEnforcementSelectedIndex < 0 || antiCheatEnforcementSelectedIndex >= antiCheatEnforcementRules.Count)
        {
            acRuleName = "";
            acRuleEnabled = true;
            acRuleTriggerType = "FindingCode";
            acRuleTriggerValue = "FORBIDDEN_MOD";
            acRuleSubject = "";
            acRuleAction = "Log";
            acRuleDuration = "30m";
            acRuleMessage = "Anti-cheat policy matched: {code} {subject}.";
            return;
        }

        var rule = antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex];
        acRuleName = rule.Name ?? "";
        acRuleEnabled = rule.Enabled;
        acRuleTriggerType = string.IsNullOrWhiteSpace(rule.TriggerType) ? "FindingCode" : rule.TriggerType;
        acRuleTriggerValue = rule.TriggerValue ?? "";
        acRuleSubject = rule.Subject ?? "";
        acRuleAction = string.IsNullOrWhiteSpace(rule.Action) ? "Log" : rule.Action;
        acRuleDuration = rule.Duration ?? "30m";
        acRuleMessage = rule.Message ?? "";
    }

    private int AntiCheatRuleTriggerIndex() => acRuleTriggerType switch
    {
        "Status" => 1,
        "RiskAtLeast" => 2,
        _ => 0
    };

    private int AntiCheatRuleActionIndex() => acRuleAction switch
    {
        "Kick" => 1,
        "TempBan" => 2,
        "Freeze" => 3,
        _ => 0
    };

    private void OnAntiCheatRuleTriggerChanged(string value, bool selected)
    {
        if (!selected || applyingAntiCheatEnforcementControls) return;
        acRuleTriggerType = value ?? "FindingCode";
    }

    private void OnAntiCheatRuleActionChanged(string value, bool selected)
    {
        if (!selected || applyingAntiCheatEnforcementControls) return;
        acRuleAction = value ?? "Log";
    }

    private void CaptureAntiCheatEnforcementControlValues()
    {
        if (SingleComposer == null) return;
        acRuleName = SingleComposer.GetTextInput("ac-rule-name")?.GetText() ?? acRuleName;
        acRuleTriggerValue = SingleComposer.GetTextInput("ac-rule-trigger-value")?.GetText() ?? acRuleTriggerValue;
        acRuleSubject = SingleComposer.GetTextInput("ac-rule-subject")?.GetText() ?? acRuleSubject;
        acRuleDuration = SingleComposer.GetTextInput("ac-rule-duration")?.GetText() ?? acRuleDuration;
        acRuleMessage = SingleComposer.GetTextInput("ac-rule-message")?.GetText() ?? acRuleMessage;
    }

    private void ApplyAntiCheatEnforcementControlValues()
    {
        if (SingleComposer == null || section != "anticheat" || antiCheatView != "enforcement") return;
        applyingAntiCheatEnforcementControls = true;
        try
        {
            SingleComposer.GetTextInput("ac-rule-name")?.SetValue(acRuleName);
            SingleComposer.GetTextInput("ac-rule-trigger-value")?.SetValue(acRuleTriggerValue);
            SingleComposer.GetTextInput("ac-rule-subject")?.SetValue(acRuleSubject);
            SingleComposer.GetTextInput("ac-rule-duration")?.SetValue(acRuleDuration);
            SingleComposer.GetTextInput("ac-rule-message")?.SetValue(acRuleMessage);
            SingleComposer.GetSwitch("ac-rule-enabled")?.SetValue(acRuleEnabled);
            SingleComposer.GetSwitch("ac-rule-apply-current")?.SetValue(acRuleApplyCurrent);
        }
        finally
        {
            applyingAntiCheatEnforcementControls = false;
        }
    }

    private bool SelectAntiCheatEnforcementRule(int index)
    {
        if (index < 0 || index >= antiCheatEnforcementRules.Count) return true;
        antiCheatEnforcementSelectedIndex = index;
        LoadAntiCheatRuleEditorFromSelection();
        pageHeaderText = SuiteLocalization.Get("gui.editingrule", antiCheatEnforcementRules[index].Name);
        ComposeDialog();
        return true;
    }

    private bool NewAntiCheatEnforcementRule()
    {
        antiCheatEnforcementSelectedIndex = -1;
        LoadAntiCheatRuleEditorFromSelection();
        acRuleName = SuiteLocalization.Get("gui.newconsequence");
        pageHeaderText = SuiteLocalization.Get("gui.newruleeditorready");
        ComposeDialog();
        return true;
    }

    private bool UpdateAntiCheatEnforcementRule()
    {
        CaptureAntiCheatEnforcementControlValues();
        if (string.IsNullOrWhiteSpace(acRuleName))
        {
            pageHeaderText = SuiteLocalization.Get("gui.rulenamerequired");
            ComposeDialog();
            return true;
        }
        if (string.IsNullOrWhiteSpace(acRuleTriggerValue))
        {
            pageHeaderText = SuiteLocalization.Get("gui.triggervaluerequired");
            ComposeDialog();
            return true;
        }

        string id = antiCheatEnforcementSelectedIndex >= 0 && antiCheatEnforcementSelectedIndex < antiCheatEnforcementRules.Count
            ? antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex].Id
            : Guid.NewGuid().ToString("N");
        var packet = new AntiCheatConsequenceRuleGuiPacket
        {
            Id = id,
            Name = acRuleName.Trim(),
            Enabled = acRuleEnabled,
            TriggerType = acRuleTriggerType,
            TriggerValue = acRuleTriggerValue.Trim(),
            Subject = acRuleSubject.Trim(),
            Action = acRuleAction,
            Duration = acRuleDuration.Trim(),
            Message = acRuleMessage.Trim()
        };

        if (antiCheatEnforcementSelectedIndex >= 0 && antiCheatEnforcementSelectedIndex < antiCheatEnforcementRules.Count)
            antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex] = packet;
        else
        {
            antiCheatEnforcementRules.Add(packet);
            antiCheatEnforcementSelectedIndex = antiCheatEnforcementRules.Count - 1;
        }

        pageHeaderText = SuiteLocalization.Get("gui.rulestaged", packet.Name);
        RefreshAntiCheatEnforcementResultLines();
        ComposeDialog();
        return true;
    }

    private bool DeleteAntiCheatEnforcementRule()
    {
        if (antiCheatEnforcementSelectedIndex < 0 || antiCheatEnforcementSelectedIndex >= antiCheatEnforcementRules.Count)
        {
            pageHeaderText = SuiteLocalization.Get("gui.selectrulebeforedelete");
            ComposeDialog();
            return true;
        }

        string name = antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex].Name;
        antiCheatEnforcementRules.RemoveAt(antiCheatEnforcementSelectedIndex);
        if (antiCheatEnforcementRules.Count == 0) antiCheatEnforcementSelectedIndex = -1;
        else antiCheatEnforcementSelectedIndex = Math.Min(antiCheatEnforcementSelectedIndex, antiCheatEnforcementRules.Count - 1);
        LoadAntiCheatRuleEditorFromSelection();
        pageHeaderText = SuiteLocalization.Get("gui.ruleremovedstaged", name);
        RefreshAntiCheatEnforcementResultLines();
        ComposeDialog();
        return true;
    }

    private bool SaveAntiCheatEnforcementRules()
    {
        // Save the currently visible editor into its selected rule first. If New Rule was
        // pressed, create it only when it has a name; this makes Save Rules intuitive.
        CaptureAntiCheatEnforcementControlValues();
        if (antiCheatEnforcementSelectedIndex >= 0 && antiCheatEnforcementSelectedIndex < antiCheatEnforcementRules.Count)
        {
            var current = antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex];
            current.Name = acRuleName.Trim();
            current.Enabled = acRuleEnabled;
            current.TriggerType = acRuleTriggerType;
            current.TriggerValue = acRuleTriggerValue.Trim();
            current.Subject = acRuleSubject.Trim();
            current.Action = acRuleAction;
            current.Duration = acRuleDuration.Trim();
            current.Message = acRuleMessage.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(acRuleName))
        {
            antiCheatEnforcementRules.Add(new AntiCheatConsequenceRuleGuiPacket
            {
                Id = Guid.NewGuid().ToString("N"), Name = acRuleName.Trim(), Enabled = acRuleEnabled,
                TriggerType = acRuleTriggerType, TriggerValue = acRuleTriggerValue.Trim(), Subject = acRuleSubject.Trim(),
                Action = acRuleAction, Duration = acRuleDuration.Trim(), Message = acRuleMessage.Trim()
            });
            antiCheatEnforcementSelectedIndex = antiCheatEnforcementRules.Count - 1;
        }

        pageHeaderText = SuiteLocalization.Get("gui.savingenforcementrules");
        var id = ++requestId;
        lastResponseId = id;
        sendAntiCheat(new AntiCheatGuiRequest
        {
            RequestId = id,
            Operation = "enforcementsave",
            ApplyCurrent = acRuleApplyCurrent,
            ConsequenceRules = antiCheatEnforcementRules.Select(CloneAntiCheatRulePacket).ToList()
        });
        return true;
    }

    private void RefreshAntiCheatEnforcementResultLines()
    {
        var lines = new List<string>();
        for (int i = 0; i < antiCheatEnforcementRules.Count; i++)
        {
            var rule = antiCheatEnforcementRules[i];
            lines.Add($"RULE\t{i}\t{RuleField(rule.Id)}\t{RuleField(rule.Name)}\t{(rule.Enabled ? 1 : 0)}\t{RuleField(rule.TriggerType)}\t{RuleField(rule.TriggerValue)}\t{RuleField(rule.Subject)}\t{RuleField(rule.Action)}\t{RuleField(rule.Duration)}\t{RuleField(rule.Message)}");
        }
        if (lines.Count == 0) lines.Add("No automatic enforcement rules are configured. Detection and reporting still continue normally.");
        SetGenericResults(lines);
    }

    private static string RuleField(string? value) => (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static bool TryGetAntiCheatEnforcementRuleIndex(string? raw, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string[] parts = raw.Split('\t');
        return parts.Length >= 11 && parts[0] == "RULE" &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0;
    }

    private void SetAntiCheatPlayerResults(List<string>? lines)
    {
        antiCheatPlayerSummary.Clear();
        var evidence = new List<string>();

        if (lines != null)
        {
            foreach (string? raw in lines)
            {
                string text = (raw ?? "").Trim();
                if (text.Length == 0) continue;

                if (text.StartsWith("FINDING\t", StringComparison.Ordinal) ||
                    text.StartsWith("REPORT\t", StringComparison.Ordinal) ||
                    text.StartsWith("MOD\t", StringComparison.Ordinal) ||
                    text.StartsWith("Runtime + ", StringComparison.Ordinal) ||
                    text.StartsWith("Scanner ", StringComparison.Ordinal))
                {
                    evidence.Add(text);
                    continue;
                }

                int colon = text.IndexOf(':');
                if (colon > 0)
                {
                    string label = text[..colon].Trim();
                    string value = text[(colon + 1)..].Trim();
                    antiCheatPlayerSummary[label] = value;
                    continue;
                }

                evidence.Add(text);
            }
        }

        string dll = AntiCheatSummary("Client DLL SHA-256");
        string package = AntiCheatSummary("Client package SHA-256");
        string mvid = AntiCheatSummary("Client MVID");
        string runtimeDigest = AntiCheatSummary("Runtime digest");
        if (dll != "—" || package != "—" || mvid != "—" || runtimeDigest != "—")
            evidence.Insert(0, $"FINGERPRINT\t{dll}\t{package}\t{mvid}\t{runtimeDigest}");

        if (evidence.Count == 0)
            evidence.Add("No findings, runtime changes, stored incidents, or scanner warnings are present for this player.");

        SetGenericResults(evidence);
    }

    private string AntiCheatSummary(string key)
    {
        return antiCheatPlayerSummary.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "—";
    }

    private string BuildAntiCheatIdentityCard()
    {
        string status = AntiCheatSummary("Status");
        return $"<font color=\"#7db6ff\"><strong>IDENTITY</strong></font><br>" +
               $"<strong>{Escape(AntiCheatSummary("Player"))}</strong><br>" +
               $"<font color=\"{StatusColor(status)}\">{Escape(status)}</font><br>" +
               $"<font color=\"#91a0ae\">UID {Escape(AntiCheatSummary("UID"))}<br>Heartbeat {Escape(AntiCheatSummary("Last heartbeat"))}</font>";
    }

    private string BuildAntiCheatRiskCard()
    {
        string scoreText = AntiCheatSummary(SuiteLocalization.Get("gui.riskscore"));
        int risk = 0;
        int slash = scoreText.IndexOf('/');
        int.TryParse(slash > 0 ? scoreText[..slash] : scoreText, NumberStyles.Integer, CultureInfo.InvariantCulture, out risk);
        return $"<font color=\"#7db6ff\"><strong>RISK & SCAN</strong></font><br>" +
               $"<font color=\"{RiskColor(risk)}\"><strong>{Escape(scoreText)} • {Escape(AntiCheatSummary("Risk rating"))}</strong></font><br>" +
               $"Findings {Escape(AntiCheatSummary("Findings"))}<br>" +
               $"<font color=\"#91a0ae\">Last scan {Escape(AntiCheatSummary("Last scan"))}<br>Last action {Escape(AntiCheatSummary("Last consequence"))}</font>";
    }

    private string BuildAntiCheatClientCard()
    {
        string installed = AntiCheatSummary("Client installed");
        string installedColor = installed.Equals("True", StringComparison.OrdinalIgnoreCase) ? "#8fda72" : "#ff7474";
        return $"<font color=\"#7db6ff\"><strong>CLIENT BUILD</strong></font><br>" +
               $"Installed <font color=\"{installedColor}\"><strong>{Escape(installed)}</strong></font><br>" +
               $"Version {Escape(AntiCheatSummary("Client version"))}<br>" +
               $"<font color=\"#91a0ae\">Game {Escape(AntiCheatSummary("Game version"))}<br>{Escape(AntiCheatSummary("Platform"))}</font>";
    }

    private string BuildAntiCheatRuntimeCard()
    {
        return $"<font color=\"#7db6ff\"><strong>RUNTIME</strong></font><br>" +
               $"Mods / asm {Escape(AntiCheatSummary("Mods / assemblies"))}<br>" +
               $"Generation {Escape(AntiCheatSummary("Runtime generation"))}<br>" +
               $"<font color=\"#91a0ae\">{Escape(AntiCheatSummary("Runtime assemblies"))}<br>New {Escape(AntiCheatSummary("Post-baseline assemblies"))}</font>";
    }

    private bool OpenAntiCheatPlayerReport(string playerTarget)
    {
        if (string.IsNullOrWhiteSpace(playerTarget)) return true;
        antiCheatPlayerSearch = playerTarget.Trim();
        antiCheatView = "player";
        genericScrollValue = 0;
        antiCheatPlayerSummary.Clear();
        pageHeaderText = SuiteLocalization.Get("gui.loadingacreport", antiCheatPlayerSearch);
        SetGenericResults(new List<string> { "Loading findings, runtime evidence, and persistent incidents..." });
        ComposeDialog();
        return RequestAntiCheat("player", antiCheatPlayerSearch);
    }

    private bool ResolveAntiCheatBlacklistFingerprint(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return true;
        antiCheatBlacklistModId = modId.Trim();
        pageHeaderText = SuiteLocalization.Get("gui.resolvingfingerprints", antiCheatBlacklistModId);
        ComposeDialog();
        return RequestAntiCheat("blacklistresolve", antiCheatBlacklistModId);
    }

    private static bool TryGetAntiCheatPlayerActionTarget(string? raw, out string target)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string[] parts = raw.Split('\t');
        if (parts.Length < 10 || parts[0] != "PLAYER") return false;
        target = parts.Length >= 11 && !string.IsNullOrWhiteSpace(parts[10]) ? parts[10] : parts[4];
        return !string.IsNullOrWhiteSpace(target);
    }

    private static bool TryGetAdminPlayerChoice(string? raw, out string playerName)
    {
        playerName = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string[] parts = raw.Split('\t');
        if (parts.Length < 3 || parts[0] != "ADMINPLAYER") return false;
        playerName = parts[1];
        return !string.IsNullOrWhiteSpace(playerName);
    }

    private bool SelectAdminCommandPlayer(string playerName)
    {
        CaptureAdminCommandInputs();
        adminCommandTarget = playerName ?? "";
        pageHeaderText = string.IsNullOrWhiteSpace(adminCommandTarget) ? SuiteLocalization.Get("gui.selectplayer") : SuiteLocalization.Get("gui.selectedplayer", adminCommandTarget);
        ComposeDialog();
        return true;
    }

    private static bool TryGetAdminInventorySlot(string? raw, out string inventoryId, out int slot, out int quantity, out string itemCode, out string fingerprint)
    {
        inventoryId = "";
        slot = 0;
        quantity = 1;
        itemCode = "";
        fingerprint = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string[] parts = raw.Split('\t');
        if (parts.Length < 9 || parts[0] != "INV") return false;
        inventoryId = parts[1];
        int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot);
        bool empty = parts[4].Equals("1", StringComparison.OrdinalIgnoreCase) || parts[4].Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!empty && int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedQty))
            quantity = Math.Max(1, parsedQty);
        itemCode = empty ? "" : parts[7];
        fingerprint = parts.Length >= 14 ? parts[13] : "";
        return !string.IsNullOrWhiteSpace(inventoryId);
    }

    private bool SelectAdminInventorySlot(string inventoryId, int slot, int quantity, string itemCode, string fingerprint)
    {
        adminCommandInventoryId = inventoryId ?? "";
        adminCommandSlot = Math.Max(0, slot);
        adminCommandQuantity = Math.Max(1, quantity);
        adminCommandItemCode = itemCode ?? "";
        adminCommandInventoryFingerprint = fingerprint ?? "";
        pageHeaderText = $"Selected {adminCommandInventoryId}[{adminCommandSlot}]";
        ComposeDialog();
        return true;
    }

    private void RefreshSelectedAdminInventoryState(IEnumerable<string>? lines)
    {
        if (lines == null || string.IsNullOrWhiteSpace(adminCommandInventoryId)) return;
        foreach (string raw in lines)
        {
            if (!TryGetAdminInventorySlot(raw, out string inv, out int slot, out int qty, out string code, out string fingerprint)) continue;
            if (!inv.Equals(adminCommandInventoryId, StringComparison.OrdinalIgnoreCase) || slot != adminCommandSlot) continue;
            adminCommandQuantity = Math.Max(1, qty);
            adminCommandItemCode = code;
            adminCommandInventoryFingerprint = fingerprint;
            return;
        }
    }

    private static bool TryGetAdminItemSearchResult(string? raw, out string itemCode, out int maxStack)
    {
        itemCode = "";
        maxStack = 1;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string[] parts = raw.Split('\t');
        if (parts.Length < 5 || parts[0] != "ITEM") return false;
        itemCode = parts[1];
        if (int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            maxStack = Math.Max(1, parsed);
        return !string.IsNullOrWhiteSpace(itemCode);
    }

    private bool SelectAdminItem(string itemCode, int maxStack)
    {
        adminCommandItemCode = itemCode ?? "";
        adminCommandQuantity = Math.Clamp(adminCommandQuantity <= 0 ? 1 : adminCommandQuantity, 1, Math.Max(1, maxStack));
        pageHeaderText = SuiteLocalization.Get("gui.selecteditem", adminCommandItemCode);
        ComposeDialog();
        return true;
    }

    private static bool TryGetAntiCheatBlacklistActionTarget(string? raw, out string modId, out bool alreadyResolved)
    {
        modId = "";
        alreadyResolved = false;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string[] parts = raw.Split('\t');
        if (parts.Length < 8 || parts[0] != "BLACKLIST") return false;
        modId = parts[1];
        alreadyResolved = int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int releases) && releases > 0;
        return !string.IsNullOrWhiteSpace(modId);
    }

    private bool LoadAntiCheatPlayer()
    {
        antiCheatPlayerSearch = SingleComposer?.GetTextInput("anticheat-player-search")?.GetText() ?? antiCheatPlayerSearch;
        if (string.IsNullOrWhiteSpace(antiCheatPlayerSearch))
        {
            antiCheatPlayerSummary.Clear();
            pageHeaderText = SuiteLocalization.Get("gui.runtime.anticheatplayer");
            SetGenericResults(new List<string> { "Enter a player name or UID." });
            ComposeDialog();
            return true;
        }
        antiCheatPlayerSummary.Clear();
        SetGenericResults(new List<string> { "Loading client-integrity report..." });
        ComposeDialog();
        return RequestAntiCheat("player");
    }

    private bool RescanAntiCheatPlayer()
    {
        antiCheatPlayerSearch = SingleComposer?.GetTextInput("anticheat-player-search")?.GetText() ?? antiCheatPlayerSearch;
        if (string.IsNullOrWhiteSpace(antiCheatPlayerSearch))
        {
            pageHeaderText = SuiteLocalization.Get("gui.runtime.anticheatplayer");
            SetGenericResults(new List<string> { "Enter a player name or UID before requesting a rescan." });
            ComposeDialog();
            return true;
        }
        SetGenericResults(new List<string> { "Requesting a fresh deep client scan..." });
        ComposeDialog();
        return RequestAntiCheat("rescan");
    }

    private bool LoadAntiCheatReports()
    {
        antiCheatReportSearch = SingleComposer?.GetTextInput("anticheat-report-search")?.GetText() ?? antiCheatReportSearch;
        pageHeaderText = string.IsNullOrWhiteSpace(antiCheatReportSearch)
            ? "Latest persistent Anti-Cheat reports"
            : $"Persistent reports for {antiCheatReportSearch.Trim()}";
        SetGenericResults(new List<string> { "Loading persistent Anti-Cheat reports..." });
        ComposeDialog();
        return RequestAntiCheat("reports", antiCheatReportSearch);
    }

    private bool LoadAntiCheatBlacklist()
    {
        SetGenericResults(new List<string> { "Loading client mod blacklist..." });
        ComposeDialog();
        return RequestAntiCheat("blacklist");
    }

    private bool AddAntiCheatBlacklist()
    {
        antiCheatBlacklistModId = SingleComposer?.GetTextInput("anticheat-blacklist-mod")?.GetText() ?? antiCheatBlacklistModId;
        antiCheatBlacklistSourcePlayer = SingleComposer?.GetTextInput("anticheat-blacklist-source")?.GetText() ?? antiCheatBlacklistSourcePlayer;
        if (string.IsNullOrWhiteSpace(antiCheatBlacklistModId))
        {
            SetGenericResults(new List<string> { "Enter a client mod ID to blacklist." });
            ComposeDialog();
            return true;
        }

        SetGenericResults(new List<string> { $"Adding {antiCheatBlacklistModId.Trim()} to the blacklist..." });
        ComposeDialog();
        return RequestAntiCheat("blacklistadd", antiCheatBlacklistModId, antiCheatBlacklistSourcePlayer);
    }

    private bool RemoveAntiCheatBlacklist()
    {
        antiCheatBlacklistModId = SingleComposer?.GetTextInput("anticheat-blacklist-mod")?.GetText() ?? antiCheatBlacklistModId;
        if (string.IsNullOrWhiteSpace(antiCheatBlacklistModId))
        {
            SetGenericResults(new List<string> { "Enter a client mod ID to remove from the blacklist." });
            ComposeDialog();
            return true;
        }

        SetGenericResults(new List<string> { $"Removing {antiCheatBlacklistModId.Trim()} from the blacklist..." });
        ComposeDialog();
        return RequestAntiCheat("blacklistremove", antiCheatBlacklistModId);
    }

    private bool SelectAdminServerView(string selected)
    {
        CaptureAdminCommandInputs();
        adminServerView = selected is "restarts" or "chat" ? selected : "operations";
        genericScrollValue = 0;
        pendingAdminConfirmOperation = "";
        pendingAdminConfirmUntilUtc = DateTime.MinValue;
        pageHeaderText = "Server command results";
        ComposeDialog();
        return true;
    }

    private bool SelectAdminCommandView(string selected)
    {
        section = "admincommands";
        view = "admincommands";
        adminCommandView = selected is "moderation" or "teleport" or "inventory" or "server" or "reports" ? selected : "players";
        genericScrollValue = 0;
        pendingAdminConfirmOperation = "";
        pendingAdminConfirmUntilUtc = DateTime.MinValue;
        pageHeaderText = adminCommandView switch
        {
            "moderation" => "Moderation command results",
            "teleport" => "Teleport command results",
            "inventory" => "Inventory command results",
            "server" => "Server command results",
            "reports" => "Report command results",
            _ => "Player command results"
        };
        SetGenericResults(new List<string>
        {
            $"ADMINSTATUS\tOK\t{AdminCommandViewHelp(adminCommandView)}"
        });
        ComposeDialog();
        return true;
    }

    private void RefreshAdminPlayerChoices() => adminPlayerChoices = AdminPlayerChoices.Create(
        (capi.World.AllOnlinePlayers ?? Array.Empty<IPlayer>()).Where(p => p != null).Select(p => (p.PlayerUID, p.PlayerName)));
    private string[] AdminTeleportPlayerValues() => adminPlayerChoices.Values;
    private string[] AdminTeleportPlayerNames() => adminPlayerChoices.Labels;

    private int AdminTeleportPlayerIndex()
    {
        string[] values = AdminTeleportPlayerValues();
        int index = Array.FindIndex(values, p => string.Equals(p, adminCommandTarget, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) return index;
        if (values.Length > 0 && !string.IsNullOrWhiteSpace(values[0]))
        {
            adminCommandTarget = values[0];
        }
        return 0;
    }

    private void OnAdminTeleportPlayerChanged(string value, bool selected)
    {
        if (!selected || string.IsNullOrWhiteSpace(value)) return;
        adminCommandTarget = value;
        SingleComposer?.GetTextInput("admincmd-target")?.SetValue(adminCommandTarget);
        pageHeaderText = SuiteLocalization.Get("gui.selectedplayer", adminCommandTarget);
    }

    private bool RefreshAdminTeleportPlayers()
    {
        CaptureAdminCommandInputs();
        RefreshAdminPlayerChoices();
        string[] values = AdminTeleportPlayerValues();
        if (values.Length > 0 && !string.IsNullOrWhiteSpace(values[0]) &&
            !values.Any(p => string.Equals(p, adminCommandTarget, StringComparison.OrdinalIgnoreCase)))
        {
            adminCommandTarget = values[0];
        }
        ComposeDialog();
        return true;
    }

    private static string AdminCommandViewHelp(string selected) => selected switch
    {
        "moderation" => "Enter a player/UID and optional duration, record ID, or reason, then choose a moderation action.",
        "teleport" => "Choose a currently online player from the shared picker for Goto, Bring, or Send Back. Refresh updates joins/leaves. My Back is independent.",
        "inventory" => "Inventory edits are audited. Remove Slot and Restore Snapshot require a second confirmation click.",
        "server" => "Server lifecycle and cleanup actions are privilege checked and destructive actions require confirmation.",
        "reports" => "Review and update saved issue reports without typing /nse report commands.",
        _ => "Use the online-player picker for normal actions. The adjacent name/UID field is retained for offline records and history lookups."
    };

    private void ApplyAutoRestartStatusLine(string raw)
    {
        string[] p = raw.Split('\t');
        if (p.Length < 8) return;
        adminAutoRestartTimes = p[2];
        adminAutoRestartWarnings = p[3];
        adminAutoRestartReason = p[4];
        adminAutoRestartGameAnnouncements = p[5] == "1" || p[5].Equals("true", StringComparison.OrdinalIgnoreCase);
        adminAutoRestartDiscordAnnouncements = p[6] == "1" || p[6].Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyChatDisplayStatusLine(string raw)
    {
        string[] p = raw.Split('\t');
        if (p.Length < 7) return;
        adminChatShowRole = p[1] == "1" || p[1].Equals("true", StringComparison.OrdinalIgnoreCase);
        adminChatBoldName = p[2] == "1" || p[2].Equals("true", StringComparison.OrdinalIgnoreCase);
        adminChatShowTimestamp = p[3] == "1" || p[3].Equals("true", StringComparison.OrdinalIgnoreCase);
        adminChatUseUtc = p[4] == "1" || p[4].Equals("true", StringComparison.OrdinalIgnoreCase);
        adminChatTimestampFormat = p[5];
        if (p.Length >= 10)
        {
            adminChatFormatGeneral = p[7] == "1" || p[7].Equals("true", StringComparison.OrdinalIgnoreCase);
            adminChatFormatOtherGroups = p[8] == "1" || p[8].Equals("true", StringComparison.OrdinalIgnoreCase);
            adminChatAdditionalGroupIds = p[9];
        }
    }

    private void CaptureAdminCommandInputs()
    {
        if (SingleComposer == null) return;
        adminCommandTarget = SingleComposer.GetTextInput("admincmd-target")?.GetText() ?? adminCommandTarget;
        adminCommandDuration = SingleComposer.GetTextInput("admincmd-duration")?.GetText() ?? adminCommandDuration;
        adminCommandReason = SingleComposer.GetTextInput("admincmd-reason")?.GetText() ?? adminCommandReason;
        adminCommandInventoryId = SingleComposer.GetTextInput("admincmd-inventory-id")?.GetText() ?? adminCommandInventoryId;
        adminCommandItemCode = SingleComposer.GetTextInput("admincmd-item-code")?.GetText() ?? adminCommandItemCode;
        adminCommandItemSearch = SingleComposer.GetTextInput("admincmd-item-search")?.GetText() ?? adminCommandItemSearch;
        adminCommandReportValue = SingleComposer.GetTextInput("admincmd-report-value")?.GetText() ?? adminCommandReportValue;
        adminAutoRestartTimes = SingleComposer.GetTextInput("autorestart-times")?.GetText() ?? adminAutoRestartTimes;
        adminAutoRestartWarnings = SingleComposer.GetTextInput("autorestart-warnings")?.GetText() ?? adminAutoRestartWarnings;
        adminAutoRestartReason = SingleComposer.GetTextInput("autorestart-reason")?.GetText() ?? adminAutoRestartReason;
        adminChatTimestampFormat = SingleComposer.GetTextInput("chatdisplay-format")?.GetText() ?? adminChatTimestampFormat;
        adminChatAdditionalGroupIds = SingleComposer.GetTextInput("chatdisplay-extra-groups")?.GetText() ?? adminChatAdditionalGroupIds;
    }

    private void ApplyAdminCommandControlValues()
    {
        if (SingleComposer == null || section != "admincommands") return;
        SingleComposer.GetTextInput("admincmd-target")?.SetValue(adminCommandTarget);
        SingleComposer.GetTextInput("admincmd-duration")?.SetValue(adminCommandDuration);
        SingleComposer.GetTextInput("admincmd-reason")?.SetValue(adminCommandReason);
        SingleComposer.GetTextInput("admincmd-inventory-id")?.SetValue(adminCommandInventoryId);
        SingleComposer.GetTextInput("admincmd-item-code")?.SetValue(adminCommandItemCode);
        SingleComposer.GetTextInput("admincmd-item-search")?.SetValue(adminCommandItemSearch);
        SingleComposer.GetTextInput("admincmd-report-value")?.SetValue(adminCommandReportValue);
        SingleComposer.GetNumberInput("admincmd-qty")?.SetValue(adminCommandQuantity.ToString(CultureInfo.InvariantCulture));
        SingleComposer.GetNumberInput("admincmd-slot")?.SetValue(adminCommandSlot.ToString(CultureInfo.InvariantCulture));
        SingleComposer.GetNumberInput("admincmd-record-id")?.SetValue(adminCommandRecordId.ToString(CultureInfo.InvariantCulture));
        SingleComposer.GetNumberInput("admincmd-radius")?.SetValue(adminCommandRadius.ToString(CultureInfo.InvariantCulture));
        SingleComposer.GetTextInput("autorestart-times")?.SetValue(adminAutoRestartTimes);
        SingleComposer.GetTextInput("autorestart-warnings")?.SetValue(adminAutoRestartWarnings);
        SingleComposer.GetTextInput("autorestart-reason")?.SetValue(adminAutoRestartReason);
        SingleComposer.GetSwitch("autorestart-game")?.SetValue(adminAutoRestartGameAnnouncements);
        SingleComposer.GetSwitch("autorestart-discord")?.SetValue(adminAutoRestartDiscordAnnouncements);
        SingleComposer.GetSwitch("chatdisplay-role")?.SetValue(adminChatShowRole);
        SingleComposer.GetSwitch("chatdisplay-bold")?.SetValue(adminChatBoldName);
        SingleComposer.GetSwitch("chatdisplay-time")?.SetValue(adminChatShowTimestamp);
        SingleComposer.GetSwitch("chatdisplay-utc")?.SetValue(adminChatUseUtc);
        SingleComposer.GetSwitch("chatdisplay-general")?.SetValue(adminChatFormatGeneral);
        SingleComposer.GetSwitch("chatdisplay-other-groups")?.SetValue(adminChatFormatOtherGroups);
        SingleComposer.GetTextInput("chatdisplay-format")?.SetValue(adminChatTimestampFormat);
        SingleComposer.GetTextInput("chatdisplay-extra-groups")?.SetValue(adminChatAdditionalGroupIds);
    }

    private void OnAdminQuantityChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            adminCommandQuantity = Math.Clamp(parsed, 1, 100000);
    }

    private void OnAdminSlotChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            adminCommandSlot = Math.Clamp(parsed, 0, 10000);
    }

    private void OnAdminRecordIdChanged(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            adminCommandRecordId = Math.Max(0, parsed);
    }

    private void OnAdminRadiusChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            adminCommandRadius = Math.Clamp(parsed, 1, 16);
    }

    private bool RunAdminCommand(string operation, bool dangerous = false)
    {
        CaptureAdminCommandInputs();
        DateTime now = DateTime.UtcNow;
        if (dangerous &&
            (!string.Equals(pendingAdminConfirmOperation, operation, StringComparison.OrdinalIgnoreCase) || now > pendingAdminConfirmUntilUtc))
        {
            pendingAdminConfirmOperation = operation;
            pendingAdminConfirmUntilUtc = now.AddSeconds(5);
            pageHeaderText = SuiteLocalization.Get("gui.confirmationrequired");
            SetGenericResults(new List<string>
            {
                $"ADMINSTATUS\tERROR\tClick {AdminOperationName(operation)} again within 5 seconds to confirm.",
                "ADMINLINE\tNo server-side change has been made yet."
            });
            ComposeDialog();
            return true;
        }

        bool confirmed = dangerous;
        pendingAdminConfirmOperation = "";
        pendingAdminConfirmUntilUtc = DateTime.MinValue;
        pageHeaderText = SuiteLocalization.Get("gui.runningoperation", AdminOperationName(operation));
        SetGenericResults(new List<string> { $"ADMINSTATUS\tOK\tSending {AdminOperationName(operation)} to the server..." });
        ComposeDialog();

        var request = new AdminCommandGuiRequest
        {
            RequestId = ++requestId,
            Operation = operation,
            Target = adminCommandTarget.Trim(),
            Reason = adminCommandReason.Trim(),
            Confirmed = confirmed,
            LongValue = adminCommandRecordId
        };

        switch (operation)
        {
            case "mute":
            case "tempban":
            case "freeze":
            case "jail":
            case "maintenance-on":
            case "shutdown":
            case "restart":
                request.Value = adminCommandDuration.Trim();
                break;
            case "autorestart-save":
                request.Value = adminAutoRestartTimes.Trim();
                request.Secondary = adminAutoRestartWarnings.Trim();
                request.Reason = adminAutoRestartReason.Trim();
                request.IntValue = (adminAutoRestartGameAnnouncements ? 1 : 0) | (adminAutoRestartDiscordAnnouncements ? 2 : 0);
                break;
            case "chatdisplay-save":
                request.Value = adminChatTimestampFormat.Trim();
                request.Secondary = adminChatAdditionalGroupIds.Trim();
                request.IntValue = (adminChatShowRole ? 1 : 0) | (adminChatShowTimestamp ? 2 : 0) | (adminChatUseUtc ? 4 : 0) | (adminChatBoldName ? 8 : 0) | (adminChatFormatGeneral ? 16 : 0) | (adminChatFormatOtherGroups ? 32 : 0);
                break;
            case "itemsearch":
                request.Value = adminCommandItemSearch.Trim();
                break;
            case "invgive":
                request.Value = adminCommandItemCode.Trim();
                request.IntValue = adminCommandQuantity;
                break;
            case "invgive-selected":
                request.Value = adminCommandItemCode.Trim();
                request.Secondary = adminCommandInventoryId.Trim();
                request.IntValue = adminCommandQuantity;
                request.IntValue2 = adminCommandSlot;
                request.ExpectedFingerprint = adminCommandInventoryFingerprint;
                break;
            case "invqty":
                request.Value = adminCommandInventoryId.Trim();
                request.IntValue = adminCommandSlot;
                request.IntValue2 = adminCommandQuantity;
                request.ExpectedFingerprint = adminCommandInventoryFingerprint;
                break;
            case "invremove":
                request.Value = adminCommandInventoryId.Trim();
                request.IntValue = adminCommandSlot;
                request.ExpectedFingerprint = adminCommandInventoryFingerprint;
                break;
            case "invreplace":
                request.Value = adminCommandItemCode.Trim();
                request.Secondary = adminCommandInventoryId.Trim();
                request.IntValue = adminCommandSlot;
                request.IntValue2 = adminCommandQuantity;
                request.ExpectedFingerprint = adminCommandInventoryFingerprint;
                break;
            case "laghere":
                request.IntValue = adminCommandRadius;
                break;
            case "report-status":
            case "report-assign":
                request.Value = adminCommandReportValue.Trim();
                break;
        }

        lastResponseId = request.RequestId;
        sendAdminCommand(request);
        return true;
    }

    private static string AdminOperationName(string operation) => operation switch
    {
        "player-info" => "Player Info",
        "online" => "Online List",
        "seen" => "Seen",
        "playtime" => "Playtime",
        "afk" => SuiteLocalization.Get("gui.afk"),
        "history" => "History",
        "warnings" => "Warnings",
        "notes" => "Notes",
        "heal" => "Heal",
        "feed" => "Feed",
        "revive" => "Revive",
        "smite" => "Smite",
        "setjail" => "Set Jail",
        "jail" => "Jail",
        "unjail" => "Unjail",
        "jailinfo" => "Jail Info",
        "ghost" => SuiteLocalization.Get("gui.ghost"),
        "warn" => "Warn",
        "unwarn" => "Remove Warning",
        "mute" => "Mute",
        "unmute" => "Unmute",
        "muteinfo" => SuiteLocalization.Get("gui.muteinfo"),
        "tempban" => "Temp Ban",
        "baninfo" => SuiteLocalization.Get("gui.baninfo"),
        "unban" => "Unban",
        "freeze" => "Freeze",
        "unfreeze" => "Unfreeze",
        "kick" => "Kick",
        "note-add" => "Add Note",
        "note-remove" => "Remove Note",
        "goto" => "Goto Player",
        "bring" => "Bring Here",
        "back" => SuiteLocalization.Get("gui.myback"),
        "sendback" => "Send Back",
        "invsee" => "Load Inventory",
        "itemsearch" => "Search Items",
        "invsnapshot" => "Inventory Snapshot",
        "invgive" => "Give Item",
        "invgive-selected" => "Put Item Here",
        "invqty" => "Set Quantity",
        "invremove" => "Remove Selected",
        "invreplace" => "Replace Selected",
        "invrestore" => SuiteLocalization.Get("gui.restoresnapshot"),
        "laghere" => SuiteLocalization.Get("gui.laghere"),
        "cleanup-scan" => SuiteLocalization.Get("gui.cleanupscan"),
        "cleanup-drops" => SuiteLocalization.Get("gui.removedrops"),
        "maintenance-on" => SuiteLocalization.Get("gui.maintenanceon"),
        "maintenance-off" => SuiteLocalization.Get("gui.maintenanceoff"),
        "maintenance-status" => SuiteLocalization.Get("gui.maintenancestatus"),
        "shutdown" => SuiteLocalization.Get("gui.scheduleshutdown"),
        "restart" => SuiteLocalization.Get("gui.schedulerestart"),
        "shutdown-cancel" => SuiteLocalization.Get("gui.cancelshutdown"),
        "autorestart-status" => "Automated Restart Status",
        "autorestart-save" => "Save Automated Restart Schedule",
        "autorestart-enable" => "Enable Automated Restarts",
        "autorestart-disable" => "Disable Automated Restarts",
        "autorestart-skip" => "Skip Next Automated Restart",
        "autorestart-reload" => "Reload Automated Restart Config",
        "chatdisplay-status" => "Chat Display Status",
        "chatdisplay-save" => "Save Chat Display Settings",
        "undo" => SuiteLocalization.Get("gui.undolast"),
        "reports-open" => SuiteLocalization.Get("gui.openreports"),
        "reports-all" => SuiteLocalization.Get("gui.allreports"),
        "report-status" => "Set Report Status",
        "report-assign" => "Assign Report",
        "report-note" => "Add Report Note",
        _ => operation
    };

    private void BuildDiscordIntegrationPage(GuiComposer composer)
    {
        const double tabY = 122;
        var overviewBounds = ElementBounds.Fixed(22, tabY, 112, 30);
        var announcementsBounds = ElementBounds.Fixed(146, tabY, 148, 30);
        var linkingBounds = ElementBounds.Fixed(306, tabY, 102, 30);
        var routingBounds = ElementBounds.Fixed(420, tabY, 102, 30);
        var motdBounds = ElementBounds.Fixed(534, tabY, 110, 30);

        composer
            .AddSuiteButton(SuiteLocalization.Get("gui.overview"), () => SelectDiscordView("overview"), overviewBounds, discordView == "overview")
            .AddSuiteButton(SuiteLocalization.Get("gui.discordannouncements"), () => SelectDiscordView("announcements"), announcementsBounds, discordView == "announcements")
            .AddSuiteButton(SuiteLocalization.Get("gui.discordlinking"), () => SelectDiscordView("linking"), linkingBounds, discordView == "linking")
            .AddSuiteButton(SuiteLocalization.Get("gui.discordrouting"), () => SelectDiscordView("routing"), routingBounds, discordView == "routing")
            .AddSuiteButton("MOTD", () => SelectDiscordView("motd"), motdBounds, discordView == "motd")
            .AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => RequestDiscordIntegration(discordView == "motd" ? "motd-get" : "get"), ElementBounds.Fixed(1180, tabY, 110, 30));

        const double sy = 164;
        composer.AddSuiteInset(ElementBounds.Fixed(16, sy, 1328, 576));

        if (discordView == "motd")
        {
            BuildDiscordMotdPage(composer, sy);
            return;
        }

        if (discordView == "announcements")
        {
            composer
                .AddRichtext("<font color=\"#7db6ff\"><strong>Discord Announcements</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 12, 390, 26))
                .AddStaticText("Each event can be enabled independently, routed to its own channel, and formatted separately. Channel ID 0 inherits the main Discord chat channel.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 40, 1190, 42));

            double y = sy + 88;
            AddDiscordAnnouncementRow(composer, "playerJoin", "Player joined", discordSettings.PlayerJoin, y); y += 66;
            AddDiscordAnnouncementRow(composer, "playerLeave", "Player left", discordSettings.PlayerLeave, y); y += 66;
            AddDiscordAnnouncementRow(composer, "playerDeath", "Player death", discordSettings.PlayerDeath, y); y += 66;
            AddDiscordAnnouncementRow(composer, "serverStarted", "Server started", discordSettings.ServerStarted, y); y += 66;
            AddDiscordAnnouncementRow(composer, "serverStopping", "Server stopping", discordSettings.ServerStopping, y); y += 66;
            AddDiscordAnnouncementRow(composer, "discordLinked", "Discord linked", discordSettings.DiscordLinked, y); y += 66;
            AddDiscordAnnouncementRow(composer, "discordUnlinked", "Discord unlinked", discordSettings.DiscordUnlinked, y);
            return;
        }

        if (discordView == "linking")
        {
            string linkStatus = discordSettings.LinkAvailable
                ? (discordSettings.LinkEnabled ? "<font color=\"#8fda72\"><strong>AVAILABLE / ENABLED</strong></font>" : "<font color=\"#ffd166\"><strong>AVAILABLE / DISABLED</strong></font>")
                : "<font color=\"#ff7070\"><strong>UNAVAILABLE</strong></font>";

            composer
                .AddRichtext("<font color=\"#7db6ff\"><strong>Discord Account Linking</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 12, 390, 26))
                .AddRichtext($"Account linking: {linkStatus}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 48, 560, 28))
                .AddStaticText($"Linked accounts: {discordSettings.LinkedAccountCount}     Pending codes: {discordSettings.PendingLinkCodeCount}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 78, 560, 26))
                .AddStaticText("Enable account linking", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 126, 240, 24))
                .AddSwitch(value => SetDiscordBool("link.enabled", value), ElementBounds.Fixed(286, sy + 120, 52, 26), "discord-link-enabled")
                .AddStaticText("Code length", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 172, 180, 24))
                .AddNumberInput(ElementBounds.Fixed(210, sy + 166, 110, 28), value => { if (int.TryParse(value, out int v)) discordSettings.LinkCodeLength = Math.Clamp(v, 6, 12); }, CairoFont.WhiteSmallText(), "discord-link-code-length")
                .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordInt("link.codelength", discordSettings.LinkCodeLength), ElementBounds.Fixed(334, sy + 166, 84, 28))
                .AddStaticText("Code expiry (seconds)", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 214, 180, 24))
                .AddNumberInput(ElementBounds.Fixed(210, sy + 208, 110, 28), value => { if (int.TryParse(value, out int v)) discordSettings.LinkCodeExpirySeconds = Math.Clamp(v, 60, 3600); }, CairoFont.WhiteSmallText(), "discord-link-expiry")
                .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordInt("link.codeexpiryseconds", discordSettings.LinkCodeExpirySeconds), ElementBounds.Fixed(334, sy + 208, 84, 28))
                .AddStaticText("Code cooldown (seconds)", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 256, 180, 24))
                .AddNumberInput(ElementBounds.Fixed(210, sy + 250, 110, 28), value => { if (int.TryParse(value, out int v)) discordSettings.LinkCodeCooldownSeconds = Math.Clamp(v, 0, 600); }, CairoFont.WhiteSmallText(), "discord-link-cooldown")
                .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordInt("link.codecooldownseconds", discordSettings.LinkCodeCooldownSeconds), ElementBounds.Fixed(334, sy + 250, 84, 28))
                .AddStaticText("Allow one Discord account to link multiple VS accounts", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 304, 430, 24))
                .AddSwitch(value => SetDiscordBool("link.allowmultiplevs", value), ElementBounds.Fixed(476, sy + 298, 52, 26), "discord-link-multiple")
                .AddStaticText("Log link/unlink changes", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 346, 250, 24))
                .AddSwitch(value => SetDiscordBool("link.logchanges", value), ElementBounds.Fixed(286, sy + 340, 52, 26), "discord-link-log")
                .AddStaticText("Enable Discord /link commands", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 388, 250, 24))
                .AddSwitch(value => SetDiscordBool("commands.linkenabled", value), ElementBounds.Fixed(286, sy + 382, 52, 26), "discord-link-commands")
                .AddStaticText("Link records are keyed by the permanent Vintage Story PlayerUID. The Discord bot token is never sent to game clients.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 438, 900, 42));
            return;
        }

        if (discordView == "routing")
        {
            composer
                .AddRichtext("<font color=\"#7db6ff\"><strong>Discord Channels & Routing</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 12, 420, 26))
                .AddStaticText("Main chat channel ID", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 60, 210, 24))
                .AddTextInput(ElementBounds.Fixed(246, sy + 54, 250, 28), value => discordSettings.ChatChannelId = value ?? "", CairoFont.WhiteSmallText(), "discord-chat-channel")
                .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("chat.channelid", "discord-chat-channel", discordSettings.ChatChannelId), ElementBounds.Fixed(508, sy + 54, 84, 28))
                .AddStaticText("Bold VS role + player label in Discord", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 108, 290, 24))
                .AddSwitch(value => SetDiscordBool("chat.boldgamesenderlabel", value), ElementBounds.Fixed(330, sy + 102, 52, 26), "discord-bold-game-sender")
                .AddStaticText("Use VS role color in Discord (embed accent)", CairoFont.WhiteSmallText(), ElementBounds.Fixed(430, sy + 108, 330, 24))
                .AddSwitch(value => SetDiscordBool("chat.usegamerolecolor", value), ElementBounds.Fixed(766, sy + 102, 52, 26), "discord-game-role-color")
                .AddStaticText("Admin audit announcements", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 156, 250, 24))
                .AddSwitch(value => SetDiscordBool("adminaudit.enabled", value), ElementBounds.Fixed(286, sy + 150, 52, 26), "discord-admin-enabled")
                .AddStaticText("Channel ID", CairoFont.WhiteSmallText(), ElementBounds.Fixed(368, sy + 156, 100, 24))
                .AddTextInput(ElementBounds.Fixed(468, sy + 150, 250, 28), value => discordSettings.AdminAuditChannelId = value ?? "", CairoFont.WhiteSmallText(), "discord-admin-channel")
                .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("adminaudit.channelid", "discord-admin-channel", discordSettings.AdminAuditChannelId), ElementBounds.Fixed(730, sy + 150, 84, 28))
                .AddSuiteButton("Test", () => TestDiscord("admin"), ElementBounds.Fixed(826, sy + 150, 84, 28))
                .AddStaticText("Client Integrity / Anti-Cheat", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 204, 250, 24))
                .AddSwitch(value => SetDiscordBool("anticheat.enabled", value), ElementBounds.Fixed(286, sy + 198, 52, 26), "discord-ac-enabled")
                .AddStaticText("Channel ID", CairoFont.WhiteSmallText(), ElementBounds.Fixed(368, sy + 204, 100, 24))
                .AddTextInput(ElementBounds.Fixed(468, sy + 198, 250, 28), value => discordSettings.AntiCheatChannelId = value ?? "", CairoFont.WhiteSmallText(), "discord-ac-channel")
                .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("anticheat.channelid", "discord-ac-channel", discordSettings.AntiCheatChannelId), ElementBounds.Fixed(730, sy + 198, 84, 28))
                .AddSuiteButton("Test", () => TestDiscord("anticheat"), ElementBounds.Fixed(826, sy + 198, 84, 28))
                .AddStaticText("Connected guild text channels", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 262, 340, 24));

            string channels = string.IsNullOrWhiteSpace(discordSettings.Channels)
                ? "No channel list is available. Connect the bot and set the Guild ID, then refresh."
                : string.Join("\n", discordSettings.Channels.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(14).Select(line =>
                {
                    var parts = line.Split('\t', 2);
                    return parts.Length == 2 ? $"#{parts[1]}    {parts[0]}" : line;
                }));
            composer.AddStaticText(channels, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 294, 1180, 220));
            return;
        }

        string transport = discordSettings.TransportAvailable
            ? (discordSettings.TransportConnected ? "<font color=\"#8fda72\"><strong>CONNECTED</strong></font>" : "<font color=\"#ffd166\"><strong>NOT CONNECTED</strong></font>")
            : "<font color=\"#ff7070\"><strong>UNAVAILABLE</strong></font>";
        string link = discordSettings.LinkAvailable
            ? (discordSettings.LinkEnabled ? "<font color=\"#8fda72\"><strong>ENABLED</strong></font>" : "<font color=\"#ffd166\"><strong>DISABLED</strong></font>")
            : "<font color=\"#91a0ae\"><strong>UNAVAILABLE</strong></font>";

        composer
            .AddRichtext("<font color=\"#7db6ff\"><strong>Discord Overview</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 12, 440, 26))
            .AddRichtext($"Discord bot: {transport}<br>Account linking: {link}<br>Bot account: <strong>{Escape(string.IsNullOrWhiteSpace(discordSettings.BotName) ? "Not connected" : discordSettings.BotName)}</strong><br>Linked accounts: <strong>{discordSettings.LinkedAccountCount}</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 48, 560, 106))
            .AddRichtext(discordSettings.BotTokenConfigured ? "<font color=\"#8fda72\">Bot token configured on server.</font>" : "<font color=\"#ff7070\">Bot token is not configured.</font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(650, sy + 48, 540, 26))
            .AddStaticText("Bot tokens are intentionally excluded from Shift+L and all client packets.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(650, sy + 78, 600, 26))
            .AddStaticText("Discord module enabled", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 176, 240, 24))
            .AddSwitch(value => SetDiscordBool("transport.enabled", value), ElementBounds.Fixed(286, sy + 170, 52, 26), "discord-transport-enabled")
            .AddStaticText("Chat bridge enabled", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 216, 240, 24))
            .AddSwitch(value => SetDiscordBool("chat.enabled", value), ElementBounds.Fixed(286, sy + 210, 52, 26), "discord-chat-enabled")
            .AddStaticText("Game → Discord", CairoFont.WhiteSmallText(), ElementBounds.Fixed(372, sy + 216, 160, 24))
            .AddSwitch(value => SetDiscordBool("chat.gametodiscord", value), ElementBounds.Fixed(534, sy + 210, 52, 26), "discord-g2d")
            .AddStaticText("Discord → Game", CairoFont.WhiteSmallText(), ElementBounds.Fixed(620, sy + 216, 160, 24))
            .AddSwitch(value => SetDiscordBool("chat.discordtogame", value), ElementBounds.Fixed(782, sy + 210, 52, 26), "discord-d2g")
            .AddStaticText("Slash commands", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 258, 150, 24))
            .AddSwitch(value => SetDiscordBool("commands.enabled", value), ElementBounds.Fixed(184, sy + 252, 52, 26), "discord-commands-enabled")
            .AddStaticText("Player info (/players, /restarttime)", CairoFont.WhiteSmallText(), ElementBounds.Fixed(270, sy + 258, 260, 24))
            .AddSwitch(value => SetDiscordBool("commands.playerinfoenabled", value), ElementBounds.Fixed(532, sy + 252, 52, 26), "discord-playerinfo-commands")
            .AddStaticText("Staff moderation", CairoFont.WhiteSmallText(), ElementBounds.Fixed(620, sy + 258, 126, 24))
            .AddSwitch(value => SetDiscordBool("commands.staffenabled", value), ElementBounds.Fixed(748, sy + 252, 52, 26), "discord-staff-commands")
            .AddStaticText("Restart command", CairoFont.WhiteSmallText(), ElementBounds.Fixed(830, sy + 258, 150, 24))
            .AddSwitch(value => SetDiscordBool("commands.restartenabled", value), ElementBounds.Fixed(982, sy + 252, 52, 26), "discord-restart-commands")
            .AddStaticText("Guild ID", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 310, 150, 24))
            .AddTextInput(ElementBounds.Fixed(184, sy + 304, 300, 28), value => discordSettings.GuildId = value ?? "", CairoFont.WhiteSmallText(), "discord-guild-id")
            .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("transport.guildid", "discord-guild-id", discordSettings.GuildId), ElementBounds.Fixed(496, sy + 304, 84, 28))
            .AddStaticText("Invite URL", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 352, 150, 24))
            .AddTextInput(ElementBounds.Fixed(184, sy + 346, 650, 28), value => discordSettings.InviteUrl = value ?? "", CairoFont.WhiteSmallText(), "discord-invite-url")
            .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("transport.inviteurl", "discord-invite-url", discordSettings.InviteUrl), ElementBounds.Fixed(846, sy + 346, 84, 28))
            .AddSuiteButton("Test Main Channel", () => TestDiscord("chat"), ElementBounds.Fixed(34, sy + 406, 180, 30))
            .AddStaticText(discordStatusMessage, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 458, 1150, 54));
    }

    private void BuildDiscordMotdPage(GuiComposer composer, double sy)
    {
        string status = motdAvailable
            ? $"<font color=\"#8fda72\"><strong>CONNECTED{(string.IsNullOrWhiteSpace(motdVersion) ? "" : " · v" + Escape(motdVersion))}</strong></font>"
            : "<font color=\"#ff7070\"><strong>NOT INSTALLED</strong></font>";

        composer
            .AddRichtext("<font color=\"#7db6ff\"><strong>Community MOTD Configuration</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 12, 420, 26))
            .AddRichtext($"Companion: {status}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(470, sy + 12, 360, 26))
            .AddSuiteButton("General", () => SelectMotdView("general"), ElementBounds.Fixed(34, sy + 46, 108, 28), motdView == "general")
            .AddSuiteButton("Pages & Feeds", () => SelectMotdView("pages"), ElementBounds.Fixed(152, sy + 46, 132, 28), motdView == "pages")
            .AddSuiteButton("Home VTML", () => SelectMotdView("home"), ElementBounds.Fixed(294, sy + 46, 116, 28), motdView == "home")
            .AddSuiteButton("Rules VTML", () => SelectMotdView("rules"), ElementBounds.Fixed(420, sy + 46, 116, 28), motdView == "rules")
            .AddSuiteButton("Reload Disk", () => RequestDiscordIntegration("motd-reload"), ElementBounds.Fixed(900, sy + 46, 116, 28))
            .AddSuiteButton("Reset Seen", () => RequestDiscordIntegration("motd-resetseen"), ElementBounds.Fixed(1026, sy + 46, 108, 28))
            .AddSuiteButton("Save & Apply", SaveMotdFromShiftL, ElementBounds.Fixed(1144, sy + 46, 130, 28))
            .AddStaticText(motdStatusMessage, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 80, 1240, 34));

        if (!motdAvailable)
        {
            composer.AddStaticText("Install Newfie's Community MOTD on the server to enable this page. Discord and Server Suite continue to work normally when it is absent.",
                CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, sy + 132, 1160, 48));
            return;
        }

        const double y0 = 286;
        if (motdView == "home" || motdView == "rules")
        {
            string key = motdView == "home" ? "Content" : "RulesVtml";
            string label = motdView == "home" ? "Home page VTML" : "Rules page VTML";
            string editorId = motdView == "home" ? "motd-home-editor" : "motd-rules-editor";
            composer
                .AddRichtext($"<font color=\"#ffc857\"><strong>{label}</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, y0, 420, 26))
                .AddStaticText("Free-form VTML. Changes are staged locally until Save & Apply is pressed.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, y0 + 30, 850, 24))
                .AddTextArea(ElementBounds.Fixed(34, y0 + 62, 1240, 350), value => SetMotdStringLocal(key, value), CairoFont.SmallTextInput(), editorId);
            return;
        }

        if (motdView == "pages")
        {
            composer
                .AddRichtext("<font color=\"#ffc857\"><strong>Hub Pages</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, y0, 300, 24))
                .AddStaticText("News", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 40, 130, 22))
                .AddSwitch(v => SetMotdBoolLocal("EnableAnnouncementsTab", v), ElementBounds.Fixed(176, y0 + 34, 52, 26), "motd-page-news")
                .AddStaticText("Updates", CairoFont.WhiteSmallText(), ElementBounds.Fixed(262, y0 + 40, 130, 22))
                .AddSwitch(v => SetMotdBoolLocal("EnableUpdatesTab", v), ElementBounds.Fixed(400, y0 + 34, 52, 26), "motd-page-updates")
                .AddStaticText("Events", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 82, 130, 22))
                .AddSwitch(v => SetMotdBoolLocal("EnableEventsTab", v), ElementBounds.Fixed(176, y0 + 76, 52, 26), "motd-page-events")
                .AddStaticText("Rules", CairoFont.WhiteSmallText(), ElementBounds.Fixed(262, y0 + 82, 130, 22))
                .AddSwitch(v => SetMotdBoolLocal("EnableRulesTab", v), ElementBounds.Fixed(400, y0 + 76, 52, 26), "motd-page-rules")
                .AddStaticText("Server Info", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 124, 130, 22))
                .AddSwitch(v => SetMotdBoolLocal("EnableServerTab", v), ElementBounds.Fixed(176, y0 + 118, 52, 26), "motd-page-server")
                .AddStaticText("Commands", CairoFont.WhiteSmallText(), ElementBounds.Fixed(262, y0 + 124, 130, 22))
                .AddSwitch(v => SetMotdBoolLocal("EnableCommandsTab", v), ElementBounds.Fixed(400, y0 + 118, 52, 26), "motd-page-commands")
                .AddRichtext("<font color=\"#7db6ff\"><strong>Discord Feeds</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, y0 + 174, 300, 24))
                .AddStaticText("Use Discord announcements", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 210, 230, 22))
                .AddSwitch(v => SetMotdBoolLocal("UseDiscordAnnouncements", v), ElementBounds.Fixed(278, y0 + 204, 52, 26), "motd-feed-news")
                .AddStaticText("Use Discord updates", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 252, 230, 22))
                .AddSwitch(v => SetMotdBoolLocal("UseDiscordUpdates", v), ElementBounds.Fixed(278, y0 + 246, 52, 26), "motd-feed-updates")
                .AddStaticText("Use Discord events", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 294, 230, 22))
                .AddSwitch(v => SetMotdBoolLocal("UseDiscordEvents", v), ElementBounds.Fixed(278, y0 + 288, 52, 26), "motd-feed-events")
                .AddStaticText("Merge local + Discord", CairoFont.WhiteSmallText(), ElementBounds.Fixed(356, y0 + 210, 210, 22))
                .AddSwitch(v => SetMotdBoolLocal("MergeLocalAndDiscord", v), ElementBounds.Fixed(574, y0 + 204, 52, 26), "motd-feed-merge")
                .AddStaticText("Max items per feed", CairoFont.WhiteSmallText(), ElementBounds.Fixed(356, y0 + 252, 170, 22))
                .AddNumberInput(ElementBounds.Fixed(530, y0 + 246, 96, 28), value => SetMotdIntLocal("DiscordMaxItemsPerFeed", value, 1, 50, 10), CairoFont.WhiteSmallText(), "motd-feed-max")
                .AddStaticText("Use Suite status", CairoFont.WhiteSmallText(), ElementBounds.Fixed(356, y0 + 294, 170, 22))
                .AddSwitch(v => SetMotdBoolLocal("UseSuiteStatus", v), ElementBounds.Fixed(530, y0 + 288, 52, 26), "motd-suite-status")

                .AddRichtext("<font color=\"#ffc857\"><strong>Server Info Categories</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(680, y0, 330, 24))
                .AddStaticText("Server", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 40, 150, 22))
                .AddSwitch(v => SetMotdBoolLocal("ServerInfoShowServer", v), ElementBounds.Fixed(846, y0 + 34, 52, 26), "motd-info-server")
                .AddStaticText("World", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930, y0 + 40, 150, 22))
                .AddSwitch(v => SetMotdBoolLocal("ServerInfoShowWorld", v), ElementBounds.Fixed(1092, y0 + 34, 52, 26), "motd-info-world")
                .AddStaticText("Gameplay", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 82, 150, 22))
                .AddSwitch(v => SetMotdBoolLocal("ServerInfoShowGameplay", v), ElementBounds.Fixed(846, y0 + 76, 52, 26), "motd-info-gameplay")
                .AddStaticText("Prospecting", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930, y0 + 82, 150, 22))
                .AddSwitch(v => SetMotdBoolLocal("ServerInfoShowProspecting", v), ElementBounds.Fixed(1092, y0 + 76, 52, 26), "motd-info-prospecting")
                .AddStaticText("Temporal", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 124, 150, 22))
                .AddSwitch(v => SetMotdBoolLocal("ServerInfoShowTemporal", v), ElementBounds.Fixed(846, y0 + 118, 52, 26), "motd-info-temporal")
                .AddStaticText("Installed mods", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930, y0 + 124, 150, 22))
                .AddSwitch(v => SetMotdBoolLocal("ServerInfoShowMods", v), ElementBounds.Fixed(1092, y0 + 118, 52, 26), "motd-info-mods")
                .AddStaticText("These controls edit the same Community MOTD server config used by the WebPanel. Nothing is stored in Discord.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 188, 540, 52));
            return;
        }

        // General / branding page.
        composer
            .AddRichtext("<font color=\"#ffc857\"><strong>General</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, y0, 280, 24))
            .AddStaticText("Title", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 38, 120, 22))
            .AddTextInput(ElementBounds.Fixed(168, y0 + 32, 430, 28), v => SetMotdStringLocal("Title", v), CairoFont.WhiteSmallText(), "motd-title")
            .AddStaticText("Subtitle", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 78, 120, 22))
            .AddTextInput(ElementBounds.Fixed(168, y0 + 72, 430, 28), v => SetMotdStringLocal("Subtitle", v), CairoFont.WhiteSmallText(), "motd-subtitle")
            .AddStaticText("Width", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 118, 90, 22))
            .AddNumberInput(ElementBounds.Fixed(168, y0 + 112, 110, 28), v => SetMotdIntLocal("Width", v, 700, 1600, 980), CairoFont.WhiteSmallText(), "motd-width")
            .AddStaticText("Height", CairoFont.WhiteSmallText(), ElementBounds.Fixed(310, y0 + 118, 90, 22))
            .AddNumberInput(ElementBounds.Fixed(398, y0 + 112, 110, 28), v => SetMotdIntLocal("Height", v, 520, 1000, 720), CairoFont.WhiteSmallText(), "motd-height")
            .AddStaticText("Version label", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 158, 120, 22))
            .AddTextInput(ElementBounds.Fixed(168, y0 + 152, 250, 28), v => SetMotdStringLocal("VersionLabel", v), CairoFont.WhiteSmallText(), "motd-version-label")
            .AddStaticText("Open every join", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 204, 180, 22))
            .AddSwitch(v => SetMotdBoolLocal("OpenOnEveryJoin", v), ElementBounds.Fixed(224, y0 + 198, 52, 26), "motd-every-join")
            .AddStaticText("Once per session", CairoFont.WhiteSmallText(), ElementBounds.Fixed(310, y0 + 204, 170, 22))
            .AddSwitch(v => SetMotdBoolLocal("OnlyOncePerSession", v), ElementBounds.Fixed(486, y0 + 198, 52, 26), "motd-once-session")
            .AddStaticText("Once per version", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38, y0 + 246, 180, 22))
            .AddSwitch(v => SetMotdBoolLocal("OnlyOncePerVersion", v), ElementBounds.Fixed(224, y0 + 240, 52, 26), "motd-once-version")
            .AddStaticText("Allow don't show again", CairoFont.WhiteSmallText(), ElementBounds.Fixed(310, y0 + 246, 190, 22))
            .AddSwitch(v => SetMotdBoolLocal("EnableDontShowAgain", v), ElementBounds.Fixed(508, y0 + 240, 52, 26), "motd-dont-show")

            .AddRichtext("<font color=\"#7db6ff\"><strong>Branding & Links</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(680, y0, 320, 24))
            .AddStaticText("Enable logo", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 38, 150, 22))
            .AddSwitch(v => SetMotdBoolLocal("EnableLogo", v), ElementBounds.Fixed(846, y0 + 32, 52, 26), "motd-logo-enabled")
            .AddStaticText("Logo source", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930, y0 + 38, 130, 22))
            .AddTextInput(ElementBounds.Fixed(1060, y0 + 32, 180, 28), v => SetMotdStringLocal("LogoSource", v), CairoFont.WhiteSmallText(), "motd-logo-source")
            .AddStaticText("Server logo file", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 78, 150, 22))
            .AddTextInput(ElementBounds.Fixed(846, y0 + 72, 394, 28), v => SetMotdStringLocal("ServerLogoFile", v), CairoFont.WhiteSmallText(), "motd-logo-file")
            .AddStaticText("Logo width", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 118, 120, 22))
            .AddNumberInput(ElementBounds.Fixed(806, y0 + 112, 100, 28), v => SetMotdIntLocal("LogoWidth", v, 0, 400, 180), CairoFont.WhiteSmallText(), "motd-logo-width")
            .AddStaticText("Height", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930, y0 + 118, 80, 22))
            .AddNumberInput(ElementBounds.Fixed(1010, y0 + 112, 100, 28), v => SetMotdIntLocal("LogoHeight", v, 0, 400, 0), CairoFont.WhiteSmallText(), "motd-logo-height")
            .AddStaticText("Preserve aspect", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 158, 150, 22))
            .AddSwitch(v => SetMotdBoolLocal("LogoPreserveAspectRatio", v), ElementBounds.Fixed(846, y0 + 152, 52, 26), "motd-logo-aspect")
            .AddStaticText("Show link button", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 204, 150, 22))
            .AddSwitch(v => SetMotdBoolLocal("EnableLinkButton", v), ElementBounds.Fixed(846, y0 + 198, 52, 26), "motd-link-enabled")
            .AddTextInput(ElementBounds.Fixed(914, y0 + 198, 148, 28), v => SetMotdStringLocal("LinkButtonText", v), CairoFont.WhiteSmallText(), "motd-link-text")
            .AddTextInput(ElementBounds.Fixed(1072, y0 + 198, 168, 28), v => SetMotdStringLocal("LinkUrl", v), CairoFont.WhiteSmallText(), "motd-link-url")
            .AddStaticText("Show Web Map", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 246, 150, 22))
            .AddSwitch(v => SetMotdBoolLocal("EnableWebMapButton", v), ElementBounds.Fixed(846, y0 + 240, 52, 26), "motd-webmap-enabled")
            .AddTextInput(ElementBounds.Fixed(914, y0 + 240, 148, 28), v => SetMotdStringLocal("WebMapButtonText", v), CairoFont.WhiteSmallText(), "motd-webmap-text")
            .AddTextInput(ElementBounds.Fixed(1072, y0 + 240, 168, 28), v => SetMotdStringLocal("WebMapUrl", v), CairoFont.WhiteSmallText(), "motd-webmap-url")
            .AddStaticText("Logo source accepts Asset or ServerFile. Server logo paths are relative to NewfiesCommunityMOTD/.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684, y0 + 292, 550, 48));
    }

    private bool SelectMotdView(string selected)
    {
        motdView = selected is "pages" or "home" or "rules" ? selected : "general";
        ComposeDialog();
        ApplyDiscordControlValues();
        return true;
    }

    private string MotdString(string key, string fallback = "") => motdConfig[key]?.Value<string>() ?? fallback;
    private bool MotdBool(string key, bool fallback = false) => motdConfig[key]?.Value<bool?>() ?? fallback;
    private int MotdInt(string key, int fallback) => motdConfig[key]?.Value<int?>() ?? fallback;

    private void SetMotdStringLocal(string key, string? value)
    {
        if (applyingMotdControls) return;
        motdConfig[key] = value ?? "";
        if (key == "Content") motdConfig["ContentLines"] = new JArray();
    }

    private void SetMotdBoolLocal(string key, bool value)
    {
        if (applyingMotdControls) return;
        motdConfig[key] = value;
    }

    private void SetMotdIntLocal(string key, string? value, int min, int max, int fallback)
    {
        if (applyingMotdControls) return;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) parsed = fallback;
        motdConfig[key] = Math.Clamp(parsed, min, max);
    }

    private void ApplyMotdControlValues()
    {
        if (SingleComposer == null || discordView != "motd") return;
        applyingMotdControls = true;
        try
        {
            if (motdView == "home")
            {
                var area = SingleComposer.GetTextArea("motd-home-editor");
                if (area != null)
                {
                    area.Autoheight = false;
                    area.SetValue(MotdString("Content"));
                }
                return;
            }
            if (motdView == "rules")
            {
                var area = SingleComposer.GetTextArea("motd-rules-editor");
                if (area != null)
                {
                    area.Autoheight = false;
                    area.SetValue(MotdString("RulesVtml"));
                }
                return;
            }
            if (motdView == "pages")
            {
                SingleComposer.GetSwitch("motd-page-news")?.SetValue(MotdBool("EnableAnnouncementsTab", true));
                SingleComposer.GetSwitch("motd-page-updates")?.SetValue(MotdBool("EnableUpdatesTab", true));
                SingleComposer.GetSwitch("motd-page-events")?.SetValue(MotdBool("EnableEventsTab", true));
                SingleComposer.GetSwitch("motd-page-rules")?.SetValue(MotdBool("EnableRulesTab", true));
                SingleComposer.GetSwitch("motd-page-server")?.SetValue(MotdBool("EnableServerTab", true));
                SingleComposer.GetSwitch("motd-page-commands")?.SetValue(MotdBool("EnableCommandsTab", true));
                SingleComposer.GetSwitch("motd-feed-news")?.SetValue(MotdBool("UseDiscordAnnouncements", true));
                SingleComposer.GetSwitch("motd-feed-updates")?.SetValue(MotdBool("UseDiscordUpdates", true));
                SingleComposer.GetSwitch("motd-feed-events")?.SetValue(MotdBool("UseDiscordEvents", true));
                SingleComposer.GetSwitch("motd-feed-merge")?.SetValue(MotdBool("MergeLocalAndDiscord", true));
                SingleComposer.GetNumberInput("motd-feed-max")?.SetValue(MotdInt("DiscordMaxItemsPerFeed", 10).ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("motd-suite-status")?.SetValue(MotdBool("UseSuiteStatus", true));
                SingleComposer.GetSwitch("motd-info-server")?.SetValue(MotdBool("ServerInfoShowServer", true));
                SingleComposer.GetSwitch("motd-info-world")?.SetValue(MotdBool("ServerInfoShowWorld", true));
                SingleComposer.GetSwitch("motd-info-gameplay")?.SetValue(MotdBool("ServerInfoShowGameplay", true));
                SingleComposer.GetSwitch("motd-info-prospecting")?.SetValue(MotdBool("ServerInfoShowProspecting", true));
                SingleComposer.GetSwitch("motd-info-temporal")?.SetValue(MotdBool("ServerInfoShowTemporal", true));
                SingleComposer.GetSwitch("motd-info-mods")?.SetValue(MotdBool("ServerInfoShowMods", true));
                return;
            }

            SingleComposer.GetTextInput("motd-title")?.SetValue(MotdString("Title", "Message of the Day"));
            SingleComposer.GetTextInput("motd-subtitle")?.SetValue(MotdString("Subtitle"));
            SingleComposer.GetNumberInput("motd-width")?.SetValue(MotdInt("Width", 980).ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("motd-height")?.SetValue(MotdInt("Height", 720).ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetTextInput("motd-version-label")?.SetValue(MotdString("VersionLabel"));
            SingleComposer.GetSwitch("motd-every-join")?.SetValue(MotdBool("OpenOnEveryJoin"));
            SingleComposer.GetSwitch("motd-once-session")?.SetValue(MotdBool("OnlyOncePerSession", true));
            SingleComposer.GetSwitch("motd-once-version")?.SetValue(MotdBool("OnlyOncePerVersion", true));
            SingleComposer.GetSwitch("motd-dont-show")?.SetValue(MotdBool("EnableDontShowAgain", true));
            SingleComposer.GetSwitch("motd-logo-enabled")?.SetValue(MotdBool("EnableLogo"));
            SingleComposer.GetTextInput("motd-logo-source")?.SetValue(MotdString("LogoSource", "ServerFile"));
            SingleComposer.GetTextInput("motd-logo-file")?.SetValue(MotdString("ServerLogoFile", "branding/motd-logo.png"));
            SingleComposer.GetNumberInput("motd-logo-width")?.SetValue(MotdInt("LogoWidth", 180).ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("motd-logo-height")?.SetValue(MotdInt("LogoHeight", 0).ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetSwitch("motd-logo-aspect")?.SetValue(MotdBool("LogoPreserveAspectRatio", true));
            SingleComposer.GetSwitch("motd-link-enabled")?.SetValue(MotdBool("EnableLinkButton"));
            SingleComposer.GetTextInput("motd-link-text")?.SetValue(MotdString("LinkButtonText", "Discord"));
            SingleComposer.GetTextInput("motd-link-url")?.SetValue(MotdString("LinkUrl"));
            SingleComposer.GetSwitch("motd-webmap-enabled")?.SetValue(MotdBool("EnableWebMapButton"));
            SingleComposer.GetTextInput("motd-webmap-text")?.SetValue(MotdString("WebMapButtonText", "Web Map"));
            SingleComposer.GetTextInput("motd-webmap-url")?.SetValue(MotdString("WebMapUrl"));
        }
        finally
        {
            applyingMotdControls = false;
        }
    }

    private void CaptureMotdControls()
    {
        if (SingleComposer == null || discordView != "motd") return;
        if (motdView == "home")
        {
            var area = SingleComposer.GetTextArea("motd-home-editor");
            if (area != null)
            {
                motdConfig["Content"] = area.GetText() ?? "";
                motdConfig["ContentLines"] = new JArray();
            }
            return;
        }
        if (motdView == "rules")
        {
            var area = SingleComposer.GetTextArea("motd-rules-editor");
            if (area != null) motdConfig["RulesVtml"] = area.GetText() ?? "";
            return;
        }

        string Text(string id, string fallback) => SingleComposer.GetTextInput(id)?.GetText() ?? fallback;
        int Number(string id, int fallback, int min, int max)
        {
            string raw = SingleComposer.GetNumberInput(id)?.GetText() ?? fallback.ToString(CultureInfo.InvariantCulture);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? Math.Clamp(value, min, max) : fallback;
        }

        if (motdView == "pages")
        {
            motdConfig["DiscordMaxItemsPerFeed"] = Number("motd-feed-max", MotdInt("DiscordMaxItemsPerFeed", 10), 1, 50);
            return;
        }

        motdConfig["Title"] = Text("motd-title", MotdString("Title"));
        motdConfig["Subtitle"] = Text("motd-subtitle", MotdString("Subtitle"));
        motdConfig["Width"] = Number("motd-width", MotdInt("Width", 980), 700, 1600);
        motdConfig["Height"] = Number("motd-height", MotdInt("Height", 720), 520, 1000);
        motdConfig["VersionLabel"] = Text("motd-version-label", MotdString("VersionLabel"));
        motdConfig["LogoSource"] = Text("motd-logo-source", MotdString("LogoSource", "ServerFile"));
        motdConfig["ServerLogoFile"] = Text("motd-logo-file", MotdString("ServerLogoFile", "branding/motd-logo.png"));
        motdConfig["LogoWidth"] = Number("motd-logo-width", MotdInt("LogoWidth", 180), 0, 400);
        motdConfig["LogoHeight"] = Number("motd-logo-height", MotdInt("LogoHeight", 0), 0, 400);
        motdConfig["LinkButtonText"] = Text("motd-link-text", MotdString("LinkButtonText"));
        motdConfig["LinkUrl"] = Text("motd-link-url", MotdString("LinkUrl"));
        motdConfig["WebMapButtonText"] = Text("motd-webmap-text", MotdString("WebMapButtonText", "Web Map"));
        motdConfig["WebMapUrl"] = Text("motd-webmap-url", MotdString("WebMapUrl"));
    }

    private bool SaveMotdFromShiftL()
    {
        if (!motdAvailable) return true;
        CaptureMotdControls();
        RequestDiscordIntegration("motd-save", stringValue: motdConfig.ToString(Formatting.None));
        return true;
    }

    private void AddDiscordAnnouncementRow(GuiComposer composer, string prefix, string label, DiscordAnnouncementGuiPacket route, double y)
    {
        composer
            .AddStaticText(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34, y + 8, 150, 24))
            .AddSwitch(value => SetDiscordAnnouncementEnabled(prefix, value), ElementBounds.Fixed(184, y + 2, 52, 26), "discord-event-" + prefix + "-enabled")
            .AddStaticText("Channel", CairoFont.WhiteSmallText(), ElementBounds.Fixed(252, y + 8, 70, 24))
            .AddTextInput(ElementBounds.Fixed(322, y + 2, 180, 28), value => route.ChannelId = value ?? "0", CairoFont.WhiteSmallText(), "discord-event-" + prefix + "-channel")
            .AddTextInput(ElementBounds.Fixed(516, y + 2, 660, 28), value => route.Format = value ?? "", CairoFont.WhiteSmallText(), "discord-event-" + prefix + "-format")
            .AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SaveDiscordAnnouncement(prefix), ElementBounds.Fixed(1188, y + 2, 84, 28));
    }

    private bool SelectDiscordView(string selected)
    {
        discordView = selected is "announcements" or "linking" or "routing" or "motd" ? selected : "overview";
        pageHeaderText = discordView == "motd" ? "Discord Integration · Community MOTD" : "Discord Integration";
        ComposeDialog();
        ApplyDiscordControlValues();
        if (discordView == "motd") RequestDiscordIntegration("motd-get");
        return true;
    }

    private DiscordAnnouncementGuiPacket GetDiscordRoute(string prefix) => prefix.ToLowerInvariant() switch
    {
        "playerjoin" => discordSettings.PlayerJoin,
        "playerleave" => discordSettings.PlayerLeave,
        "playerdeath" => discordSettings.PlayerDeath,
        "serverstarted" => discordSettings.ServerStarted,
        "serverstopping" => discordSettings.ServerStopping,
        "discordlinked" => discordSettings.DiscordLinked,
        "discordunlinked" => discordSettings.DiscordUnlinked,
        _ => discordSettings.PlayerJoin
    };

    private void SetDiscordAnnouncementEnabled(string prefix, bool value)
    {
        if (applyingDiscordControls) return;
        GetDiscordRoute(prefix).Enabled = value;
        RequestDiscordIntegration("set", $"event.{prefix}.enabled", boolValue: value);
    }

    private bool SaveDiscordAnnouncement(string prefix)
    {
        var route = GetDiscordRoute(prefix);
        // Read the live controls at click time. GuiTextInput's change callback can lag
        // behind a button click, which previously caused a newly typed channel ID to
        // be replaced by the cached pre-edit value as soon as the response refreshed.
        route.ChannelId = SingleComposer?.GetTextInput("discord-event-" + prefix + "-channel")?.GetText() ?? route.ChannelId;
        route.Format = SingleComposer?.GetTextInput("discord-event-" + prefix + "-format")?.GetText() ?? route.Format;
        RequestDiscordIntegration("set", $"event.{prefix}.channelid", stringValue: route.ChannelId);
        RequestDiscordIntegration("set", $"event.{prefix}.format", stringValue: route.Format);
        return true;
    }

    private void SetDiscordBool(string key, bool value)
    {
        if (applyingDiscordControls) return;
        RequestDiscordIntegration("set", key, boolValue: value);
    }

    private bool SendDiscordTextInput(string key, string elementId, string fallback)
    {
        string value = SingleComposer?.GetTextInput(elementId)?.GetText() ?? fallback ?? "";
        return SendDiscordText(key, value);
    }

    private bool SendDiscordText(string key, string value)
    {
        RequestDiscordIntegration("set", key, stringValue: value ?? "");
        return true;
    }

    private bool SendDiscordInt(string key, int value)
    {
        RequestDiscordIntegration("set", key, intValue: value);
        return true;
    }

    private bool TestDiscord(string target)
    {
        RequestDiscordIntegration("test", target: target);
        return true;
    }

    private bool RequestDiscordIntegration(
        string operation,
        string key = "",
        bool boolValue = false,
        int intValue = 0,
        string stringValue = "",
        string target = "")
    {
        var id = ++requestId;
        lastDiscordResponseId = id;
        sendDiscordIntegration(new DiscordIntegrationGuiRequest
        {
            RequestId = id,
            Operation = operation,
            SettingKey = key,
            BoolValue = boolValue,
            IntValue = intValue,
            StringValue = stringValue ?? "",
            Target = target ?? ""
        });
        return true;
    }

    private void ApplyDiscordControlValues()
    {
        if (section != "webserver" || webView != "discord" || SingleComposer == null) return;
        applyingDiscordControls = true;
        try
        {
            if (discordView == "overview")
            {
                SingleComposer.GetSwitch("discord-transport-enabled")?.SetValue(discordSettings.TransportEnabled);
                SingleComposer.GetSwitch("discord-chat-enabled")?.SetValue(discordSettings.ChatEnabled);
                SingleComposer.GetSwitch("discord-g2d")?.SetValue(discordSettings.GameToDiscord);
                SingleComposer.GetSwitch("discord-d2g")?.SetValue(discordSettings.DiscordToGame);
                SingleComposer.GetSwitch("discord-commands-enabled")?.SetValue(discordSettings.CommandsEnabled);
                SingleComposer.GetSwitch("discord-playerinfo-commands")?.SetValue(discordSettings.PlayerInfoCommandsEnabled);
                SingleComposer.GetSwitch("discord-staff-commands")?.SetValue(discordSettings.StaffCommandsEnabled);
                SingleComposer.GetSwitch("discord-restart-commands")?.SetValue(discordSettings.RestartCommandsEnabled);
                SingleComposer.GetTextInput("discord-guild-id")?.SetValue(discordSettings.GuildId ?? "");
                SingleComposer.GetTextInput("discord-invite-url")?.SetValue(discordSettings.InviteUrl ?? "");
            }
            else if (discordView == "linking")
            {
                SingleComposer.GetSwitch("discord-link-enabled")?.SetValue(discordSettings.LinkEnabled);
                SingleComposer.GetNumberInput("discord-link-code-length")?.SetValue(discordSettings.LinkCodeLength.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("discord-link-expiry")?.SetValue(discordSettings.LinkCodeExpirySeconds.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetNumberInput("discord-link-cooldown")?.SetValue(discordSettings.LinkCodeCooldownSeconds.ToString(CultureInfo.InvariantCulture));
                SingleComposer.GetSwitch("discord-link-multiple")?.SetValue(discordSettings.LinkAllowMultipleVs);
                SingleComposer.GetSwitch("discord-link-log")?.SetValue(discordSettings.LinkLogChanges);
                SingleComposer.GetSwitch("discord-link-commands")?.SetValue(discordSettings.LinkCommandsEnabled);
            }
            else if (discordView == "routing")
            {
                SingleComposer.GetTextInput("discord-chat-channel")?.SetValue(discordSettings.ChatChannelId ?? "0");
                SingleComposer.GetSwitch("discord-bold-game-sender")?.SetValue(discordSettings.GameBoldSenderLabel);
                SingleComposer.GetSwitch("discord-game-role-color")?.SetValue(discordSettings.GameUseRoleColor);
                SingleComposer.GetSwitch("discord-admin-enabled")?.SetValue(discordSettings.AdminAuditEnabled);
                SingleComposer.GetTextInput("discord-admin-channel")?.SetValue(discordSettings.AdminAuditChannelId ?? "0");
                SingleComposer.GetSwitch("discord-ac-enabled")?.SetValue(discordSettings.AntiCheatDiscordEnabled);
                SingleComposer.GetTextInput("discord-ac-channel")?.SetValue(discordSettings.AntiCheatChannelId ?? "0");
            }
            else if (discordView == "announcements")
            {
                foreach (string prefix in new[] { "playerJoin", "playerLeave", "playerDeath", "serverStarted", "serverStopping", "discordLinked", "discordUnlinked" })
                {
                    var route = GetDiscordRoute(prefix);
                    SingleComposer.GetSwitch("discord-event-" + prefix + "-enabled")?.SetValue(route.Enabled);
                    SingleComposer.GetTextInput("discord-event-" + prefix + "-channel")?.SetValue(route.ChannelId ?? "0");
                    SingleComposer.GetTextInput("discord-event-" + prefix + "-format")?.SetValue(route.Format ?? "");
                }
            }
            else if (discordView == "motd")
            {
                ApplyMotdControlValues();
            }
        }
        finally
        {
            applyingDiscordControls = false;
        }
    }

    private bool SelectWebView(string selected)
    {
        section = "webserver";
        view = "webserver";
        genericScrollValue = 0;
        webView = selected is "webmap" or "discord" ? selected : "webpanel";

        if (webView == "discord")
        {
            pageHeaderText = "Discord Integration";
            ComposeDialog();
            RequestDiscordIntegration("get");
        }
        else if (webView == "webmap")
        {
            pageHeaderText = SuiteLocalization.Get("gui.runtime.webmapconfig");
            ComposeDialog();
            RequestWebDashboard("map");
        }
        else
        {
            pageHeaderText = SuiteLocalization.Get("gui.runtime.webpanelconfig");
            ComposeDialog();
            RequestWebDashboard("status");
        }

        return true;
    }

    private bool RequestWebDashboard(string operation)
    {
        var id = ++requestId;
        lastResponseId = id;

        sendWebDashboard(new WebDashboardRequest
        {
            RequestId = id,
            Operation = operation
        });

        return true;
    }

    private void SetWebBool(string key, bool value)
    {
        if (applyingWebControls) return;

        if (key == "enabled") webEnabled = value;
        if (key == "securecookies") webSecureCookies = value;

        var id = ++requestId;
        lastResponseId = id;

        sendWebDashboard(new WebDashboardRequest
        {
            RequestId = id,
            Operation = "setbool",
            SettingKey = key,
            BoolValue = value
        });
    }

    private bool SaveWebBind() =>
        SendWebText("bindaddress", webBindAddress);

    private bool SaveWebPublicUrl() =>
        SendWebText("publicurl", webPublicUrl);

    private bool SaveWebUsername() =>
        SendWebText("adminusername", webAdminUsername);

    private bool SendWebText(string key, string value)
    {
        var id = ++requestId;
        lastResponseId = id;

        sendWebDashboard(new WebDashboardRequest
        {
            RequestId = id,
            Operation = "settext",
            SettingKey = key,
            StringValue = value ?? ""
        });

        return true;
    }

    private void OnWebPortChanged(string value)
    {
        if (int.TryParse(value, out int parsed))
            webPort = Math.Clamp(parsed, 1, 65535);
    }

    private bool SaveWebPort() =>
        SendWebInt("port", webPort);

    private void OnWebSessionChanged(string value)
    {
        if (int.TryParse(value, out int parsed))
            webSessionTimeoutMinutes = Math.Clamp(parsed, 5, 1440);
    }

    private bool SaveWebSessionTimeout() =>
        SendWebInt("sessiontimeoutminutes", webSessionTimeoutMinutes);

    private bool SendWebInt(string key, int value)
    {
        var id = ++requestId;
        lastResponseId = id;

        sendWebDashboard(new WebDashboardRequest
        {
            RequestId = id,
            Operation = "setint",
            SettingKey = key,
            IntValue = value
        });

        return true;
    }

    private void ApplyWebControlValues()
    {
        if (section != "webserver" ||
            webView != "webpanel" ||
            SingleComposer == null)
            return;

        applyingWebControls = true;
        try
        {
            SingleComposer.GetSwitch("web-enabled")?.SetValue(webEnabled);
            SingleComposer.GetSwitch("web-secure-cookies")?.SetValue(webSecureCookies);
            SingleComposer.GetTextInput("web-bind")?.SetValue(webBindAddress);
            SingleComposer.GetTextInput("web-public-url")?.SetValue(webPublicUrl);
            SingleComposer.GetTextInput("web-username")?.SetValue(webAdminUsername);
            SingleComposer.GetNumberInput("web-port")?.SetValue(webPort.ToString());
            SingleComposer.GetNumberInput("web-session-minutes")?.SetValue(webSessionTimeoutMinutes.ToString());
        }
        finally
        {
            applyingWebControls = false;
        }
    }

    private void CopyWebMapSettings(WebMapGuiSettingsPacket settings)
    {
        webMapEnabled = settings.Enabled;
        webMapRunning = settings.Running;
        webMapBindAddress = string.IsNullOrWhiteSpace(settings.BindAddress) ? "0.0.0.0" : settings.BindAddress;
        webMapPort = settings.Port > 0 ? settings.Port : 9466;
        webMapTitle = string.IsNullOrWhiteSpace(settings.Title) ? "Newfie's Server Map" : settings.Title;
        webMapPublicUrl = settings.PublicUrl ?? "";
        webMapDashboardUrl = settings.DashboardUrl ?? "";
        webMapShowPlayers = settings.ShowPlayers;
        webMapShowWaypoints = settings.ShowWaypoints;
        webMapShowActivity = settings.ShowActivity;
        webMapShowBadBlockEntities = settings.ShowBadBlockEntities;
        webMapPlayerMajorWaypointLimit = settings.PlayerMajorWaypointLimit;
        webMapPlayerMinorWaypointLimit = settings.PlayerMinorWaypointLimit;
        webMapMaximumZoomOutSpanBlocks = settings.MaximumZoomOutSpanBlocks;
        webMapDefaultViewSpanBlocks = settings.DefaultViewSpanBlocks;
        webMapTerrainTileSize = settings.TerrainTileSize;
        webMapActivityWindowMinutes = settings.ActivityWindowMinutes;
        webMapMaxActivityMarkers = settings.MaxActivityMarkers;
        webMapExportTerrainOnStart = settings.ExportTerrainOnStart;
        webMapAutomaticStartupExportEnabled = settings.AutomaticStartupExportEnabled;
        webMapAutomaticStartupExportIntervalDays = settings.AutomaticStartupExportIntervalDays;
        webMapUseIncrementalTerrainExport = settings.UseIncrementalTerrainExport;
        webMapLockServerDuringTerrainExport = settings.LockServerDuringTerrainExport;
        webMapTerrainExportInProgress = settings.TerrainExportInProgress;
        webMapLastError = settings.LastError ?? "";
    }

    private void SetWebMapBool(string key, bool value)
    {
        if (applyingWebMapControls) return;

        switch (key)
        {
            case "enabled": webMapEnabled = value; break;
            case "players": webMapShowPlayers = value; break;
            case "waypoints": webMapShowWaypoints = value; break;
            case "activity": webMapShowActivity = value; break;
            case "badbe": webMapShowBadBlockEntities = value; break;
            case "exportnext": webMapExportTerrainOnStart = value; break;
            case "autoexport": webMapAutomaticStartupExportEnabled = value; break;
            case "incremental": webMapUseIncrementalTerrainExport = value; break;
            case "lockexport": webMapLockServerDuringTerrainExport = value; break;
        }
    }

    private void SetWebMapInt(string key, string value)
    {
        if (applyingWebMapControls || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return;

        switch (key)
        {
            case "port": webMapPort = Math.Clamp(parsed, 1024, 65535); break;
            case "major": webMapPlayerMajorWaypointLimit = Math.Clamp(parsed, 0, 100); break;
            case "minor": webMapPlayerMinorWaypointLimit = Math.Clamp(parsed, 0, 100); break;
            case "maxzoom": webMapMaximumZoomOutSpanBlocks = Math.Clamp(parsed, 2048, 100000); break;
            case "defaultspan": webMapDefaultViewSpanBlocks = Math.Clamp(parsed, 512, 100000); break;
            case "tilesize": webMapTerrainTileSize = Math.Clamp(parsed, 128, 512); break;
            case "activitywindow": webMapActivityWindowMinutes = Math.Clamp(parsed, 5, 1440); break;
            case "activitymarkers": webMapMaxActivityMarkers = Math.Clamp(parsed, 25, 200); break;
            case "exportdays": webMapAutomaticStartupExportIntervalDays = Math.Clamp(parsed, 1, 365); break;
        }
    }

    private bool SaveWebMapSettings()
    {
        if (SingleComposer != null)
        {
            webMapBindAddress = SingleComposer.GetTextInput("webmap-bind")?.GetText() ?? webMapBindAddress;
            webMapTitle = SingleComposer.GetTextInput("webmap-title")?.GetText() ?? webMapTitle;
            webMapPublicUrl = SingleComposer.GetTextInput("webmap-public-url")?.GetText() ?? webMapPublicUrl;
            webMapDashboardUrl = SingleComposer.GetTextInput("webmap-dashboard-url")?.GetText() ?? webMapDashboardUrl;
        }

        var id = ++requestId;
        lastResponseId = id;
        sendWebDashboard(new WebDashboardRequest
        {
            RequestId = id,
            Operation = "mapsave",
            WebMapSettings = new WebMapGuiSettingsPacket
            {
                Enabled = webMapEnabled,
                BindAddress = webMapBindAddress,
                Port = webMapPort,
                Title = webMapTitle,
                PublicUrl = webMapPublicUrl,
                DashboardUrl = webMapDashboardUrl,
                ShowPlayers = webMapShowPlayers,
                ShowWaypoints = webMapShowWaypoints,
                ShowActivity = webMapShowActivity,
                ShowBadBlockEntities = webMapShowBadBlockEntities,
                PlayerMajorWaypointLimit = webMapPlayerMajorWaypointLimit,
                PlayerMinorWaypointLimit = webMapPlayerMinorWaypointLimit,
                MaximumZoomOutSpanBlocks = webMapMaximumZoomOutSpanBlocks,
                DefaultViewSpanBlocks = webMapDefaultViewSpanBlocks,
                TerrainTileSize = webMapTerrainTileSize,
                ActivityWindowMinutes = webMapActivityWindowMinutes,
                MaxActivityMarkers = webMapMaxActivityMarkers,
                ExportTerrainOnStart = webMapExportTerrainOnStart,
                AutomaticStartupExportEnabled = webMapAutomaticStartupExportEnabled,
                AutomaticStartupExportIntervalDays = webMapAutomaticStartupExportIntervalDays,
                UseIncrementalTerrainExport = webMapUseIncrementalTerrainExport,
                LockServerDuringTerrainExport = webMapLockServerDuringTerrainExport
            }
        });
        return true;
    }

    private void ApplyWebMapControlValues()
    {
        if (section != "webserver" || webView != "webmap" || SingleComposer == null) return;

        applyingWebMapControls = true;
        try
        {
            SingleComposer.GetSwitch("webmap-enabled")?.SetValue(webMapEnabled);
            SingleComposer.GetSwitch("webmap-players")?.SetValue(webMapShowPlayers);
            SingleComposer.GetSwitch("webmap-waypoints")?.SetValue(webMapShowWaypoints);
            SingleComposer.GetSwitch("webmap-activity")?.SetValue(webMapShowActivity);
            SingleComposer.GetSwitch("webmap-badbe")?.SetValue(webMapShowBadBlockEntities);
            SingleComposer.GetSwitch("webmap-exportnext")?.SetValue(webMapExportTerrainOnStart);
            SingleComposer.GetSwitch("webmap-autoexport")?.SetValue(webMapAutomaticStartupExportEnabled);
            SingleComposer.GetSwitch("webmap-incremental")?.SetValue(webMapUseIncrementalTerrainExport);
            SingleComposer.GetSwitch("webmap-lockexport")?.SetValue(webMapLockServerDuringTerrainExport);
            SingleComposer.GetTextInput("webmap-bind")?.SetValue(webMapBindAddress);
            SingleComposer.GetTextInput("webmap-title")?.SetValue(webMapTitle);
            SingleComposer.GetTextInput("webmap-public-url")?.SetValue(webMapPublicUrl);
            SingleComposer.GetTextInput("webmap-dashboard-url")?.SetValue(webMapDashboardUrl);
            SingleComposer.GetNumberInput("webmap-port")?.SetValue(webMapPort.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("webmap-major")?.SetValue(webMapPlayerMajorWaypointLimit.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("webmap-minor")?.SetValue(webMapPlayerMinorWaypointLimit.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("webmap-maxzoom")?.SetValue(webMapMaximumZoomOutSpanBlocks.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("webmap-defaultspan")?.SetValue(webMapDefaultViewSpanBlocks.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("webmap-tilesize")?.SetValue(webMapTerrainTileSize.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("webmap-activitywindow")?.SetValue(webMapActivityWindowMinutes.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("webmap-activitymarkers")?.SetValue(webMapMaxActivityMarkers.ToString(CultureInfo.InvariantCulture));
            SingleComposer.GetNumberInput("webmap-exportdays")?.SetValue(webMapAutomaticStartupExportIntervalDays.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            applyingWebMapControls = false;
        }
    }

    private bool SelectLoginProtectionView(string selected)
    {
        section = "loginprotection";
        view = "loginprotection";
        genericScrollValue = 0;
        loginView = selected == "settings" ? "settings" : "status";

        if (loginView == "settings")
        {
            pageHeaderText = SuiteLocalization.Get("gui.runtime.loginsettings");
            resultText = SuiteLocalization.Get("gui.runtime.loadinglogin");
            ComposeDialog();
            RequestLoginProtection("settings");
        }
        else
        {
            pageHeaderText = SuiteLocalization.Get("gui.runtime.loginstatus");
            resultText = SuiteLocalization.Get("gui.loadingloginstatus");
            SetGenericResults(new List<string> { SuiteLocalization.Get("gui.loadingloginstatus") });
            ComposeDialog();
            RequestLoginProtection("status");
        }

        return true;
    }

    private bool RequestLoginProtection(string operation)
    {
        var id = ++requestId;
        lastResponseId = id;

        sendLoginProtection(new LoginProtectionRequest
        {
            RequestId = id,
            Operation = operation
        });

        return true;
    }

    private void SetLoginProtectionBool(string key, bool value)
    {
        if (applyingLoginControls) return;

        switch (key)
        {
            case "enabled": loginEnabled = value; break;
            case "protectonjoin": loginProtectOnJoin = value; break;
            case "protectonrespawn": loginProtectOnRespawn = value; break;
            case "announceonjoin": loginAnnounceOnJoin = value; break;
            case "announceonend": loginAnnounceOnEnd = value; break;
            case "blockpvp": loginBlockPvP = value; break;
            case "cancelonattack": loginCancelOnAttack = value; break;
            case "protectfalldamage": loginProtectFallDamage = value; break;
            case "extinguishfire": loginExtinguishFire = value; break;
            case "recoverswimmingonlogin": loginRecoverSwimmingOnLogin = value; break;
        }

        var id = ++requestId;
        lastResponseId = id;

        sendLoginProtection(new LoginProtectionRequest
        {
            RequestId = id,
            Operation = "setbool",
            SettingKey = key,
            BoolValue = value
        });
    }

    private void OnLoginProtectionSecondsChanged(string value)
    {
        if (int.TryParse(value, out int parsed))
            loginProtectionSeconds = Math.Clamp(parsed, 1, 3600);
    }

    private bool SaveLoginProtectionSeconds()
    {
        loginProtectionSeconds = Math.Clamp(loginProtectionSeconds, 1, 3600);

        var id = ++requestId;
        lastResponseId = id;

        sendLoginProtection(new LoginProtectionRequest
        {
            RequestId = id,
            Operation = "setint",
            SettingKey = "protectionseconds",
            IntValue = loginProtectionSeconds
        });

        return true;
    }

    private void ApplyLoginProtectionControlValues()
    {
        if (section != "loginprotection" ||
            loginView != "settings" ||
            SingleComposer == null)
            return;

        applyingLoginControls = true;
        try
        {
            SingleComposer.GetSwitch("login-enabled")?.SetValue(loginEnabled);
            SingleComposer.GetSwitch("login-join")?.SetValue(loginProtectOnJoin);
            SingleComposer.GetSwitch("login-respawn")?.SetValue(loginProtectOnRespawn);
            SingleComposer.GetSwitch("login-announce-start")?.SetValue(loginAnnounceOnJoin);
            SingleComposer.GetSwitch("login-announce-end")?.SetValue(loginAnnounceOnEnd);
            SingleComposer.GetSwitch("login-block-pvp")?.SetValue(loginBlockPvP);
            SingleComposer.GetSwitch("login-cancel-attack")?.SetValue(loginCancelOnAttack);
            SingleComposer.GetSwitch("login-fall")?.SetValue(loginProtectFallDamage);
            SingleComposer.GetSwitch("login-fire")?.SetValue(loginExtinguishFire);
            SingleComposer.GetSwitch("login-water-recovery")?.SetValue(loginRecoverSwimmingOnLogin);
            SingleComposer.GetNumberInput("login-seconds")?.SetValue(loginProtectionSeconds.ToString());
        }
        finally
        {
            applyingLoginControls = false;
        }
    }

    private bool SelectProfilerView(string selected)
    {
        section = "profiler";
        view = "profiler";
        profilerView = selected switch
        {
            "mods" => "mods",
            "phases" => "phases",
            "slowticks" => "slowticks",
            "hotspots" => "hotspots",
            "offthread" => "offthread",
            "lagevents" => "lagevents",
            _ => "overview"
        };

        ComposeDialog();
        return true;
    }

    private static string ProfilerConfidenceMarkup(string? confidence)
    {
        string value = (confidence ?? "Low").Trim();
        return value.Equals("High", StringComparison.OrdinalIgnoreCase)
            ? "<font color=\"#8fda72\"><strong>High</strong></font>"
            : value.Equals("Medium", StringComparison.OrdinalIgnoreCase)
                ? "<font color=\"#ffd166\"><strong>Medium</strong></font>"
                : "<font color=\"#ffad66\"><strong>Low</strong></font>";
    }

    private void SetProfilerEnabled(bool value)
    {
        if (applyingProfilerControls) return;

        profilerEnabled = value;
        profilerMessage = value
            ? "Enabling profiler..."
            : "Disabling profiler...";

        var id = ++requestId;
        lastResponseId = id;
        sendProfiler(new ProfilerRequest
        {
            RequestId = id,
            Operation = "setenabled",
            BoolValue = value
        });
    }

    private bool ToggleProfilerLiveUpdates()
    {
        profilerLiveUpdates = !profilerLiveUpdates;
        profilerLiveGeneration++;

        if (profilerLiveUpdates)
        {
            profilerMessage = SuiteLocalization.Get("gui.liveupdatesenabled");
            int generation = profilerLiveGeneration;
            ScheduleProfilerLiveRefresh(generation);
        }
        else
        {
            profilerLiveRequestPending = false;
            profilerMessage = SuiteLocalization.Get("gui.liveupdatesdisabled");
        }

        ComposeDialog();
        return true;
    }

    private void ScheduleProfilerLiveRefresh(int generation)
    {
        capi.Event.RegisterCallback(_ =>
        {
            if (generation != profilerLiveGeneration || !profilerLiveUpdates)
                return;

            if (!IsOpened())
            {
                profilerLiveUpdates = false;
                profilerLiveGeneration++;
                return;
            }

            if (section != "profiler")
            {
                profilerLiveUpdates = false;
                profilerLiveGeneration++;
                return;
            }

            if (!profilerLiveRequestPending)
            {
                profilerLiveRequestPending = true;
                RequestProfilerSnapshotInternal(showLoading: false, compact: true);
            }

            ScheduleProfilerLiveRefresh(generation);
        }, ProfilerLiveRefreshMs);
    }

    private bool OpenLocalProfilerWeb()
    {
        string url = string.IsNullOrWhiteSpace(webLocalUrl)
            ? $"http://127.0.0.1:{webPort}/"
            : webLocalUrl;

        capi.Gui.OpenLink(url);
        return true;
    }

    private bool OpenPublicProfilerWeb()
    {
        if (string.IsNullOrWhiteSpace(webPublicUrl))
        {
            profilerMessage =
                "Public web URL is not configured. Set it in Web Server settings or /serverutils web publicurl.";
            ComposeDialog();
            return true;
        }

        capi.Gui.OpenLink(webPublicUrl);
        return true;
    }

    private bool RequestProfilerSnapshot() =>
        RequestProfilerSnapshotInternal(showLoading: true, compact: false);

    private bool RequestProfilerSnapshotInternal(bool showLoading, bool compact)
    {
        if (showLoading)
            profilerMessage = SuiteLocalization.Get("gui.loadingdiagnostics");

        var id = ++requestId;
        lastResponseId = id;
        sendProfiler(new ProfilerRequest
        {
            RequestId = id,
            Operation = "get",
            Compact = compact
        });
        return true;
    }

    private bool StartProfilerCapture(int seconds)
    {
        if (!profilerEnabled)
        {
            profilerMessage = SuiteLocalization.Get("gui.profilerenablefirst");
            ComposeDialog();
            return true;
        }

        seconds = Math.Clamp(seconds, 5, 30);
        profilerMessage = SuiteLocalization.Get("gui.requestingengineprofile", seconds);
        ComposeDialog();

        var id = ++requestId;
        lastResponseId = id;
        sendProfiler(new ProfilerRequest
        {
            RequestId = id,
            Operation = "start",
            DurationSeconds = seconds
        });

        // The server keeps the capture. This delayed read only refreshes the GUI after
        // the requested profile should have completed.
        capi.Event.RegisterCallback(_ =>
        {
            if (IsOpened() && section == "profiler")
                RequestProfilerSnapshot();
        }, seconds * 1000 + 750);

        return true;
    }

    private string BuildProfilerStateText()
    {
        return !profilerEnabled
            ? "PROFILER DISABLED — enable it above to collect diagnostics."
            : profilerRunning
                ? $"ENGINE PROFILE RUNNING — about {profilerRemainingSeconds}s remaining"
                : profilerLastProfile.CompletedUnixMilliseconds > 0
                    ? $"Last profile: {profilerLastProfile.DurationSeconds}s / {profilerLastProfile.TickCount} ticks — " +
                      $"avg {profilerLastProfile.AverageTickMs:0.0}ms, P95 {profilerLastProfile.P95TickMs:0.0}, " +
                      $"P99 {profilerLastProfile.P99TickMs:0.0}, max {profilerLastProfile.MaxTickMs:0.0}ms"
                    : "No detailed engine profile captured yet.";
    }

    private string BuildProfilerCurrentPrimaryText() =>
        $"CPU: {profilerCurrent.CpuPercent:0.0}%   " +
        $"Working set: {profilerCurrent.WorkingSetMb:0} MB   " +
        $"Managed heap: {profilerCurrent.ManagedHeapMb:0} MB   " +
        $"Players: {profilerCurrent.Players}   " +
        $"Entities: {profilerCurrent.LoadedEntities:N0}   " +
        $"Chunks: {profilerCurrent.LoadedChunks:N0}";

    private string BuildProfilerCurrentSecondaryText() =>
        $"Main-thread delay: {profilerCurrent.MainThreadDelayMs:0.0}ms   " +
        $"BlockLogger queue: {profilerCurrent.BlockLoggerQueue:N0}   " +
        $"Writer: {profilerCurrent.BlockLoggerWriterMs:N0}ms   " +
        $"Dropped: {profilerCurrent.BlockLoggerDropped:N0}   " +
        $"Animal tracked: {profilerCurrent.AnimalTrackedEntities:N0} / {profilerCurrent.AnimalTrackedRegions:N0} regions";

    private void UpdateProfilerLiveElements()
    {
        var composer = SingleComposer;
        if (composer == null) return;

        // Dynamic text supports asynchronous texture recomposition in the 1.22.x
        // client API. This keeps the network-response callback from doing Cairo text
        // composition on the render/main thread every live refresh.
        composer.GetDynamicText("profiler-live-status")?.SetNewTextAsync(
            SuiteProfilerTextElement.FitUiLine(SuiteLocalization.Get("gui.statusvalue", profilerMessage), 1180), autoHeight: false, forceRedraw: true);
        composer.GetDynamicText("profiler-live-state")?.SetNewTextAsync(
            SuiteProfilerTextElement.FitUiLine(BuildProfilerStateText(), 760), autoHeight: false, forceRedraw: true);

        if (profilerView != "overview") return;

        composer.GetDynamicText("profiler-live-current-primary")?.SetNewTextAsync(
            BuildProfilerCurrentPrimaryText(), autoHeight: false, forceRedraw: true);
        composer.GetDynamicText("profiler-live-current-secondary")?.SetNewTextAsync(
            BuildProfilerCurrentSecondaryText(), autoHeight: false, forceRedraw: true);
    }

    private string ProfilerMetricSummary(
        System.Func<ProfilerSamplePacket, double> selector,
        string suffix)
    {
        if (profilerSamples.Count == 0)
            return "No rolling samples yet.\n\nInteractive graphs are available in the web dashboard.";

        var values = profilerSamples
            .Select(selector)
            .OrderBy(x => x)
            .ToList();

        double current = selector(profilerSamples[^1]);
        double average = values.Average();
        double maximum = values[^1];

        int p95Index = Math.Clamp(
            (int)Math.Ceiling(values.Count * 0.95) - 1,
            0,
            values.Count - 1);
        double p95 = values[p95Index];

        return
            $"Current   {current:0.0}{suffix}\n" +
            $"Average   {average:0.0}{suffix}\n" +
            $"P95       {p95:0.0}{suffix}\n" +
            $"Maximum   {maximum:0.0}{suffix}\n" +
            "Web dashboard: interactive graph";
    }

    private static string ShortProfilerCode(string? value, int maxLength)
    {
        string text = value ?? "";
        if (text.Length <= maxLength) return text;
        if (maxLength <= 3) return text[..maxLength];

        // Preserve the end of nested profiler paths because that is usually the
        // most specific engine/mod marker.
        int keep = Math.Max(1, maxLength - 3);
        return "..." + text.Substring(text.Length - keep);
    }

    private bool SelectAnimalView(string selected)
    {
        section = "animals";
        view = "animals";
        genericScrollValue = 0;
        animalView = selected switch
        {
            "history" => "history",
            "population" => "population",
            "rules" => "rules",
            "settings" => "settings",
            _ => "limiter"
        };

        if (animalView == "population")
        {
            populationFirstIndex = 0;
            populationRows.Clear();
            populationMobTotals.Clear();
            populationMode = SuiteLocalization.Get("gui.pressscanpopulation");
            pageHeaderText = SuiteLocalization.Get("gui.runtime.animallimiterpopulation");
            ComposeDialog();
        }
        else if (animalView == "history")
        {
            pageHeaderText = SuiteLocalization.Get("gui.runtime.animallimiterlogs");
            resultText = SuiteLocalization.Get("gui.loadingremovallogs");
            SetRemovalLogResults(new List<string> { SuiteLocalization.Get("gui.loadingremovallogs") });
            ComposeDialog();
            SendAnimalLimiterOperation("history");
        }
        else if (animalView == "rules")
        {
            animalRulePage = 0;
            pageHeaderText = SuiteLocalization.Get("gui.runtime.animallimiterrules");
            resultText = SuiteLocalization.Get("gui.loadingrules");
            ComposeDialog();
            SendAnimalLimiterOperation("rules");
        }
        else if (animalView == "settings")
        {
            pageHeaderText = SuiteLocalization.Get("gui.runtime.animallimitersettings");
            resultText = SuiteLocalization.Get("gui.loadingsettings");
            ComposeDialog();
            SendAnimalLimiterOperation("settings");
        }
        else
        {
            pageHeaderText = SuiteLocalization.Get("gui.runtime.animallimiter");
            resultText = SuiteLocalization.Get("gui.loadinganimallimiter");
            ComposeDialog();
            SendAnimalLimiterOperation("status");
        }

        return true;
    }

    private bool SelectView(string selected)
    {
        section = "blocklogger";
        genericScrollValue = 0;
        view = selected;
        blockLoggerView = selected;
        action = "any";
        pageHeaderText = $"Ready for {ViewTitle(selected).ToLowerInvariant()}.";
        resultText = SuiteLocalization.Get("gui.runtime.noresults");

        // Clear the shared result buffer whenever a new BlockLogger history tab is
        // selected so Status or a previous search cannot appear on another tab.
        SetGenericResults(new List<string> { SuiteLocalization.Get("gui.runtime.noresults") });
        ComposeDialog();
        return true;
    }

    private bool SelectBlockLoggerSettings()
    {
        section = "blocklogger";
        view = "settings";
        blockLoggerView = "settings";
        genericScrollValue = 0;
        pageHeaderText = "BlockLogger settings";
        ComposeDialog();
        return RequestBlockLoggerSettings();
    }

    private bool RequestBlockLoggerSettings()
    {
        var id = ++requestId;
        lastResponseId = id;
        sendBlockLoggerSettings(new BlockLoggerSettingsRequest
        {
            RequestId = id,
            Operation = "get"
        });
        return true;
    }

    private bool SelectStatus()
    {
        section = "blocklogger";
        view = "status";
        blockLoggerView = "status";
        pageHeaderText = SuiteLocalization.Get("gui.runtime.blockloggerstatus");
        resultText = SuiteLocalization.Get("gui.loadingserverstatus");
        SetGenericResults(new List<string> { SuiteLocalization.Get("gui.loadingserverstatus") });
        ComposeDialog();

        var id = ++requestId;
        lastResponseId = id;
        sendStatus(new InvestigatorStatusRequest { RequestId = id });
        return true;
    }

    private bool Search()
    {
        if (view == "status") return SelectStatus();
        if (view == "settings") return RequestBlockLoggerSettings();

        pageHeaderText = SuiteLocalization.Get("gui.searchingview", ViewTitle(view).ToLowerInvariant());
        ShowTransientResult("Searching...");

        var id = ++requestId;
        lastResponseId = id;
        sendQuery(new InvestigatorQueryRequest
        {
            RequestId = id,
            View = view,
            UseSelection = useSelection,
            Radius = radius,
            Limit = limit,
            Search = view == "players" ? playerSearch : search,
            Action = action
        });
        return true;
    }

    private bool Refresh() => view == "status" ? SelectStatus() : view == "animals" ? SendAnimalLimiterOperation("status") : Search();

    private bool ConfirmCull()
    {
        if (DateTime.UtcNow > cullConfirmUntilUtc)
        {
            cullConfirmUntilUtc = DateTime.UtcNow.AddSeconds(5);
            ShowTransientResult("<font color=\"#ffcc66\"><strong>Cull confirmation:</strong> press Cull Now again within 5 seconds to remove excess entities.</font>");
            return true;
        }

        cullConfirmUntilUtc = DateTime.MinValue;
        return SendAnimalLimiterOperation("cull");
    }

    private bool SendAnimalLimiterOperation(string operation)
    {
        pageHeaderText = operation switch
        {
            "history" => "Animal Limiter removal logs",
            "rules" => "Animal Limiter rules",
            "settings" => "Animal Limiter settings",
            _ => "Animal Limiter"
        };

        ShowTransientResult(operation switch
        {
            "history" => SuiteLocalization.Get("gui.loadingremovallogs"),
            "rules" => SuiteLocalization.Get("gui.loadingrules"),
            "settings" => SuiteLocalization.Get("gui.loadingsettings"),
            "cull" => "Running animal limiter cull...",
            "preview" => "Running preview...",
            _ => "Updating Animal Limiter..."
        });

        return SendAnimalRequest(new AnimalLimiterRequest
        {
            Operation = operation,
            Pattern = animalPattern,
            Limit = animalLimit,
            RadiusChunks = animalRuleRadiusChunks
        });
    }

    private bool SendAnimalRequest(AnimalLimiterRequest request)
    {
        var id = ++requestId;
        lastResponseId = id;
        request.RequestId = id;
        sendAnimalLimiter(request);
        return true;
    }

    private bool SaveAnimalRadius()
    {
        ShowTransientResult("Saving region radius...");
        return SendAnimalRequest(new AnimalLimiterRequest
        {
            Operation = "setradius",
            RadiusChunks = animalRadiusChunks
        });
    }

    private bool SaveAnimalDefaultLimit()
    {
        ShowTransientResult("Saving default limit...");
        return SendAnimalRequest(new AnimalLimiterRequest
        {
            Operation = "setint",
            SettingKey = "defaultlimit",
            IntValue = animalDefaultLimit
        });
    }

    private bool SaveAnimalProtectionRadius()
    {
        ShowTransientResult("Saving player protection radius...");
        return SendAnimalRequest(new AnimalLimiterRequest
        {
            Operation = "setint",
            SettingKey = "playerprotectionradius",
            IntValue = animalPlayerProtectionRadius
        });
    }

    private bool SaveAnimalReconciliation()
    {
        ShowTransientResult("Saving reconciliation interval...");
        return SendAnimalRequest(new AnimalLimiterRequest
        {
            Operation = "setint",
            SettingKey = "reconciliationseconds",
            IntValue = animalReconciliationSeconds
        });
    }

    private void SetAnimalBool(string key, bool value)
    {
        if (applyingAnimalControls) return;

        switch (key)
        {
            case "enabled": animalEnabled = value; break;
            case "limitallentities": animalLimitAllEntities = value; break;
            case "protectpets": animalProtectPets = value; break;
            case "protectfarmanimals": animalProtectFarmAnimals = value; break;
            case "protectnamedentities": animalProtectNamedEntities = value; break;
            case "keepnearesttoplayers": animalKeepNearestToPlayers = value; break;
            case "logactions": animalLogActions = value; break;
            case "logtocsv": animalLogToCsv = value; break;
        }

        SendAnimalRequest(new AnimalLimiterRequest
        {
            Operation = "setbool",
            SettingKey = key,
            BoolValue = value
        });
    }

    private void ApplyBlockLoggerControlValues()
    {
        if (SingleComposer == null || section != "blocklogger" || view != "settings") return;

        SingleComposer.GetNumberInput("bl-retention")?.SetValue(blockRetentionDays.ToString());
        SingleComposer.GetNumberInput("bl-flush")?.SetValue(blockFlushIntervalMs.ToString());
        SingleComposer.GetNumberInput("bl-queue")?.SetValue(blockMaxQueuedEvents.ToString());
        SingleComposer.GetNumberInput("bl-overflow")?.SetValue(blockEmergencyOverflowCapacity.ToString());
        SingleComposer.GetNumberInput("bl-default-radius")?.SetValue(blockDefaultRadius.ToString());
        SingleComposer.GetNumberInput("bl-entity-radius")?.SetValue(blockEntityRadius.ToString());
        SingleComposer.GetNumberInput("bl-max-radius")?.SetValue(blockMaxRadius.ToString());
        SingleComposer.GetNumberInput("bl-default-rows")?.SetValue(blockDefaultRows.ToString());
        SingleComposer.GetNumberInput("bl-default-entity-rows")?.SetValue(blockDefaultEntityRows.ToString());
        SingleComposer.GetNumberInput("bl-max-results")?.SetValue(blockMaxDisplayCount.ToString());

        applyingBlockLoggerControls = true;
        try
        {
            SingleComposer.GetSwitch("bl-enabled")?.SetValue(blockLoggerEnabled);
            SingleComposer.GetSwitch("bl-break")?.SetValue(blockLogBreak);
            SingleComposer.GetSwitch("bl-place")?.SetValue(blockLogPlace);
            SingleComposer.GetSwitch("bl-use")?.SetValue(blockLogUse);
            SingleComposer.GetSwitch("bl-container-access")?.SetValue(blockLogContainerAccess);
            SingleComposer.GetSwitch("bl-container-items")?.SetValue(blockLogContainerItems);
            SingleComposer.GetSwitch("bl-entity-interact")?.SetValue(blockLogEntityInteract);
            SingleComposer.GetSwitch("bl-entity-spawn")?.SetValue(blockLogEntitySpawn);
            SingleComposer.GetSwitch("bl-entity-death")?.SetValue(blockLogEntityDeath);
            SingleComposer.GetSwitch("bl-entity-despawn")?.SetValue(blockLogEntityDespawn);
            SingleComposer.GetSwitch("bl-aggregate")?.SetValue(blockAggregateSpawnDespawn);
        }
        finally
        {
            applyingBlockLoggerControls = false;
        }
    }

    private void SetBlockLoggerBool(string key, bool value)
    {
        if (applyingBlockLoggerControls) return;

        switch (key)
        {
            case "enabled": blockLoggerEnabled = value; break;
            case "logblockbreak": blockLogBreak = value; break;
            case "logblockplace": blockLogPlace = value; break;
            case "logblockuse": blockLogUse = value; break;
            case "logcontaineraccess": blockLogContainerAccess = value; break;
            case "logcontaineritemchanges": blockLogContainerItems = value; break;
            case "logentityinteract": blockLogEntityInteract = value; break;
            case "logentityspawn": blockLogEntitySpawn = value; break;
            case "logentitydeath": blockLogEntityDeath = value; break;
            case "logentitydespawn": blockLogEntityDespawn = value; break;
            case "aggregateentityspawndespawn": blockAggregateSpawnDespawn = value; break;
        }

        SendBlockLoggerSettingsRequest(new BlockLoggerSettingsRequest
        {
            Operation = "setbool",
            SettingKey = key,
            BoolValue = value
        });
    }

    private bool SendBlockLoggerInt(string key, int value)
    {
        return SendBlockLoggerSettingsRequest(new BlockLoggerSettingsRequest
        {
            Operation = "setint",
            SettingKey = key,
            IntValue = value
        });
    }

    private bool SendBlockLoggerSettingsRequest(BlockLoggerSettingsRequest request)
    {
        var id = ++requestId;
        lastResponseId = id;
        request.RequestId = id;
        sendBlockLoggerSettings(request);
        return true;
    }

    private bool SaveBlockRetention() => SendBlockLoggerInt("retentiondays", blockRetentionDays);
    private bool SaveBlockFlush() => SendBlockLoggerInt("flushintervalms", blockFlushIntervalMs);
    private bool SaveBlockQueue() => SendBlockLoggerInt("maxqueuedevents", blockMaxQueuedEvents);
    private bool SaveBlockOverflow() => SendBlockLoggerInt("emergencyoverflowcapacity", blockEmergencyOverflowCapacity);
    private bool SaveBlockDefaultRadius() => SendBlockLoggerInt("defaultradius", blockDefaultRadius);
    private bool SaveBlockEntityRadius() => SendBlockLoggerInt("entityradius", blockEntityRadius);
    private bool SaveBlockMaxRadius() => SendBlockLoggerInt("maxradius", blockMaxRadius);
    private bool SaveBlockDefaultRows() => SendBlockLoggerInt("defaultdisplaycount", blockDefaultRows);
    private bool SaveBlockDefaultEntityRows() => SendBlockLoggerInt("defaultentitydisplaycount", blockDefaultEntityRows);
    private bool SaveBlockMaxResults() => SendBlockLoggerInt("maxdisplaycount", blockMaxDisplayCount);

    private void OnBlockRetentionChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockRetentionDays = Math.Clamp(parsed, 0, 3650);
    }

    private void OnBlockFlushChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockFlushIntervalMs = Math.Clamp(parsed, 100, 60000);
    }

    private void OnBlockQueueChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockMaxQueuedEvents = Math.Clamp(parsed, 500, 250000);
    }

    private void OnBlockOverflowChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockEmergencyOverflowCapacity = Math.Clamp(parsed, 1000, 1000000);
    }

    private void OnBlockDefaultRadiusChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockDefaultRadius = Math.Clamp(parsed, 0, 256);
    }

    private void OnBlockEntityRadiusChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockEntityRadius = Math.Clamp(parsed, 0, 256);
    }

    private void OnBlockMaxRadiusChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockMaxRadius = Math.Clamp(parsed, 0, 512);
    }

    private void OnBlockDefaultRowsChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockDefaultRows = Math.Clamp(parsed, 1, 2000);
    }

    private void OnBlockDefaultEntityRowsChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockDefaultEntityRows = Math.Clamp(parsed, 1, 2000);
    }

    private void OnBlockMaxResultsChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) blockMaxDisplayCount = Math.Clamp(parsed, 1, 2000);
    }

    private void ApplyAnimalControlValues()
    {
        if (SingleComposer == null || section != "animals") return;

        if (animalView == "limiter")
        {
            SingleComposer.GetNumberInput("animal-rule-radius")?.SetValue(animalRuleRadiusChunks.ToString());
            return;
        }

        if (animalView != "settings") return;

        SingleComposer.GetNumberInput("animal-default-limit")?.SetValue(animalDefaultLimit.ToString());
        SingleComposer.GetNumberInput("animal-default-radius")?.SetValue(animalRadiusChunks.ToString());
        SingleComposer.GetNumberInput("animal-protection-radius")?.SetValue(animalPlayerProtectionRadius.ToString());
        SingleComposer.GetNumberInput("animal-reconcile-seconds")?.SetValue(animalReconciliationSeconds.ToString());

        applyingAnimalControls = true;
        try
        {
            SingleComposer.GetSwitch("animal-enabled")?.SetValue(animalEnabled);
            SingleComposer.GetSwitch("animal-limit-all")?.SetValue(animalLimitAllEntities);
            SingleComposer.GetSwitch("animal-protect-pets")?.SetValue(animalProtectPets);
            SingleComposer.GetSwitch("animal-protect-farm")?.SetValue(animalProtectFarmAnimals);
            SingleComposer.GetSwitch("animal-protect-named")?.SetValue(animalProtectNamedEntities);
            SingleComposer.GetSwitch("animal-keep-nearest")?.SetValue(animalKeepNearestToPlayers);
            SingleComposer.GetSwitch("animal-log-actions")?.SetValue(animalLogActions);
            SingleComposer.GetSwitch("animal-log-csv")?.SetValue(animalLogToCsv);
        }
        finally
        {
            applyingAnimalControls = false;
        }
    }

    private void OnAnimalRuleRadiusChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) animalRuleRadiusChunks = Math.Clamp(parsed, 0, 32);
    }

    private void OnAnimalRadiusChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) animalRadiusChunks = Math.Clamp(parsed, 0, 32);
    }

    private void OnAnimalDefaultLimitChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) animalDefaultLimit = Math.Clamp(parsed, 0, 1000000);
    }

    private void OnAnimalProtectionRadiusChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) animalPlayerProtectionRadius = Math.Clamp(parsed, 0, 1024);
    }

    private void OnAnimalReconciliationChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) animalReconciliationSeconds = Math.Clamp(parsed, 60, 3600);
    }

    private void OnAnimalLimitChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) animalLimit = Math.Clamp(parsed, 0, 1000000);
    }

    private void OnTargetChanged(string value, bool selected)
    {
        if (selected) useSelection = value == "selection";
    }

    private void OnRadiusChanged(string value)
    {
        if (int.TryParse(value, out var parsed)) radius = Math.Clamp(parsed, 0, 512);
    }

    private void OnLimitChanged(string value)
    {
        // The server applies NewfiesBlocklogger.json -> MaxDisplayCount.
        // Keep the client input within the same absolute range supported by config.
        if (int.TryParse(value, out var parsed)) limit = Math.Clamp(parsed, 1, 2000);
    }

    private void OnActionChanged(string value, bool selected)
    {
        if (selected) action = value;
    }

    private string[] ActionValues() => view switch
    {
        "containers" => new[] { "any", "open", "take", "put" },
        "entities" => new[] { "any", "entityspawn", "entitydeath", "entitydespawn", "entityinteract" },
        "players" => new[] { "any", "blocks", "containers", "entities" },
        _ => new[] { "any", "break", "place", "interact" }
    };

    private string[] ActionNames() => view switch
    {
        "containers" => new[] { "Any", "Open", "Take", "Put" },
        "entities" => new[] { "Any", SuiteLocalization.Get("gui.spawn"), SuiteLocalization.Get("gui.death"), SuiteLocalization.Get("gui.despawn"), SuiteLocalization.Get("gui.interact") },
        "players" => new[] { "Any", "Blocks", "Containers", "Entities" },
        _ => new[] { "Any", SuiteLocalization.Get("gui.break"), SuiteLocalization.Get("gui.place"), SuiteLocalization.Get("gui.use") }
    };

    private int PlayerActionIndex()
    {
        return action switch
        {
            "blocks" => 1,
            "containers" => 2,
            "entities" => 3,
            _ => 0
        };
    }

    private int ActionIndex()
    {
        var values = ActionValues();
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] == action) return i;
        }
        return 0;
    }

    private void ShowTransientResult(string text)
    {
        resultText = text;
        SetGenericResults(new List<string> { WebUtility.HtmlDecode(text) });
        ComposeDialog();
    }

    private void SetGenericResults(List<string>? lines)
    {
        genericResultLines.Clear();

        if (lines == null || lines.Count == 0)
        {
            genericResultLines.Add("No records found.");
        }
        else
        {
            foreach (var line in lines)
                genericResultLines.Add(string.IsNullOrEmpty(line) ? " " : line);
        }

        genericScrollValue = 0;
    }

    private bool IsAdminInventoryViewerResult()
    {
        return section == "admincommands" && adminCommandView == "inventory" &&
               genericResultLines.Any(line => line.StartsWith("INV\t", StringComparison.Ordinal));
    }

    private sealed class AdminInventoryVisualSlot
    {
        public string InventoryId { get; init; } = "";
        public string Section { get; init; } = "Other";
        public int Slot { get; init; }
        public int Quantity { get; init; } = 1;
        public string ItemCode { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public string SerializedStack { get; init; } = "";
        public bool Empty { get; init; }
    }

    private static bool TryGetAdminInventoryVisualSlot(string? raw, out AdminInventoryVisualSlot slot)
    {
        slot = new AdminInventoryVisualSlot();
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string[] parts = raw.Split('\t');
        if (parts.Length < 14 || parts[0] != "INV") return false;

        int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slotIndex);
        bool empty = parts[4].Equals("1", StringComparison.OrdinalIgnoreCase) ||
                     parts[4].Equals("true", StringComparison.OrdinalIgnoreCase);
        int qty = 1;
        if (!empty && int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedQty))
            qty = Math.Max(1, parsedQty);

        slot = new AdminInventoryVisualSlot
        {
            InventoryId = parts[1],
            Section = string.IsNullOrWhiteSpace(parts[2]) ? "Other" : parts[2],
            Slot = Math.Max(0, slotIndex),
            Empty = empty,
            Quantity = qty,
            ItemCode = empty ? "" : parts[7],
            Fingerprint = parts[13],
            SerializedStack = parts.Length >= 15 ? parts[14] : ""
        };
        return !string.IsNullOrWhiteSpace(slot.InventoryId);
    }

    private ItemStack? BuildAdminInventoryDisplayStack(AdminInventoryVisualSlot visual)
    {
        if (visual.Empty) return null;

        if (!string.IsNullOrWhiteSpace(visual.SerializedStack))
        {
            try
            {
                var stack = new ItemStack(Convert.FromBase64String(visual.SerializedStack));
                if (stack.ResolveBlockOrItem(capi.World))
                {
                    string resolvedCode = stack.Collectible?.Code?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(visual.ItemCode) ||
                        resolvedCode.Equals(visual.ItemCode, StringComparison.OrdinalIgnoreCase))
                    {
                        return stack;
                    }
                }
            }
            catch
            {
                // Older servers/clients or a malformed remote stack fall back to code resolution below.
            }
        }

        try
        {
            string code = visual.ItemCode.Contains(':') ? visual.ItemCode : "game:" + visual.ItemCode;
            var location = new AssetLocation(code);
            CollectibleObject? collectible = (CollectibleObject?)capi.World.GetItem(location) ?? capi.World.GetBlock(location);
            return collectible == null ? null : new ItemStack(collectible, Math.Max(1, visual.Quantity));
        }
        catch
        {
            return null;
        }
    }

    private void AddAdminInventoryViewer(
        GuiComposer composer,
        double innerX,
        double innerWidth,
        double resultsY,
        double resultsHeight)
    {
        var visuals = genericResultLines
            .Select(raw => TryGetAdminInventoryVisualSlot(raw, out var value) ? value : null)
            .Where(value => value != null)
            .Cast<AdminInventoryVisualSlot>()
            .ToList();

        composer.AddSuiteInset(ElementBounds.Fixed(innerX, resultsY, innerWidth, resultsHeight));
        adminInventoryElementBaseY.Clear();
        adminInventoryViewportHeight = Math.Max(80, resultsHeight - 34);

        if (visuals.Count == 0)
        {
            composer.AddStaticText(SuiteLocalization.Get("gui.noinventoryslots"), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(innerX + 12, resultsY + 12, 500, 24));
            adminInventoryContentHeight = adminInventoryViewportHeight;
            return;
        }

        var groups = visuals
            .GroupBy(v => v.InventoryId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                InventoryId = g.Key,
                Section = g.Select(v => v.Section).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Other",
                Slots = g.OrderBy(v => v.Slot).ToList()
            })
            .OrderBy(g => AdminInventorySectionOrder(g.Section))
            .ThenBy(g => g.InventoryId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        const double padX = 10;
        const double headerHeight = 20;
        const double sectionGap = 8;
        const double columnGap = 16;
        double slotStep = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGrid.unscaledSlotPadding;
        double usableWidth = innerWidth - 52;
        double columnWidth = (usableWidth - columnGap) / 2;
        double[] colY = { 8, 8 };
        int renderedGroups = 0;

        composer.BeginClip(ElementBounds.Fixed(innerX + 4, resultsY + 4, innerWidth - 30, adminInventoryViewportHeight));

        foreach (var group in groups)
        {
            int count = Math.Max(1, group.Slots.Max(v => v.Slot) + 1);
            int cols = group.Section.Equals("Crafting", StringComparison.OrdinalIgnoreCase) ? 3 :
                       group.Section.Equals("Hotbar", StringComparison.OrdinalIgnoreCase) ? Math.Min(10, count) :
                       group.Section.Equals("Equipment", StringComparison.OrdinalIgnoreCase) ? Math.Min(5, count) :
                       Math.Max(1, Math.Min(8, count));
            int rows = (int)Math.Ceiling(count / (double)cols);
            double groupHeight = headerHeight + rows * slotStep + sectionGap;
            int column = colY[0] <= colY[1] ? 0 : 1;
            double x = padX + column * (columnWidth + columnGap);
            double baseY = colY[column];

            var inventory = new InventoryGeneric(count, "nssinspect-" + renderedGroups, capi);
            var bySlot = group.Slots.ToDictionary(v => v.Slot);
            foreach (var visual in group.Slots)
            {
                if (visual.Slot < 0 || visual.Slot >= inventory.Count) continue;
                inventory[visual.Slot].Itemstack = BuildAdminInventoryDisplayStack(visual);
            }

            string labelKey = "admin-inventory-label-" + renderedGroups;
            string gridKey = "admin-inventory-grid-" + renderedGroups;
            string label = group.Section + "  •  " + group.InventoryId;
            composer.AddRichtext($"<font color=\"#91a0ae\"><strong>{Escape(label)}</strong></font>",
                CairoFont.WhiteSmallText(), ElementBounds.Fixed(x, baseY, columnWidth, 18), labelKey);

            var gridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, x, baseY + headerHeight, cols, rows);
            var grid = new ReadonlyAdminInventoryGrid(capi, inventory, cols, gridBounds, slotIndex =>
            {
                if (!bySlot.TryGetValue(slotIndex, out var selected)) return;
                SelectAdminInventorySlot(selected.InventoryId, selected.Slot, selected.Quantity, selected.ItemCode, selected.Fingerprint);
            });
            if (group.InventoryId.Equals(adminCommandInventoryId, StringComparison.OrdinalIgnoreCase) &&
                adminCommandSlot >= 0 && adminCommandSlot < inventory.Count) grid.HighlightSlot(adminCommandSlot);
            composer.AddInteractiveElement(grid, gridKey);

            adminInventoryElementBaseY[labelKey] = baseY;
            adminInventoryElementBaseY[gridKey] = baseY + headerHeight;
            colY[column] += groupHeight;
            renderedGroups++;
        }

        composer.EndClip();

        adminInventoryContentHeight = Math.Max(adminInventoryViewportHeight, Math.Max(colY[0], colY[1]) + 4);
        composer.AddVerticalScrollbar(OnAdminInventoryScroll,
            ElementBounds.Fixed(innerX + innerWidth - 20, resultsY + 4, 18, adminInventoryViewportHeight),
            "admin-inventory-scroll");

        int occupied = visuals.Count(v => !v.Empty);
        composer.AddRichtext(
            $"<font color=\"#91a0ae\">{visuals.Count} slots • {occupied} occupied • scroll for all inventories • click a slot to select it</font>",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX + 12, resultsY + resultsHeight - 25, innerWidth - 40, 22));
    }

    private void ConfigureAdminInventoryScrollbar()
    {
        var scrollbar = SingleComposer?.GetScrollbar("admin-inventory-scroll");
        if (scrollbar == null) return;
        double maxScroll = Math.Max(0, adminInventoryContentHeight - adminInventoryViewportHeight);
        adminInventoryScrollValue = Math.Clamp(adminInventoryScrollValue, 0, maxScroll);
        scrollbar.SetHeights((float)adminInventoryViewportHeight, (float)adminInventoryContentHeight);
        scrollbar.SetScrollbarPosition((int)Math.Round(adminInventoryScrollValue));
        PositionAdminInventoryElements(adminInventoryScrollValue);
    }

    private void OnAdminInventoryScroll(float value)
    {
        adminInventoryScrollValue = Math.Max(0, value);
        PositionAdminInventoryElements(adminInventoryScrollValue);
    }

    private void PositionAdminInventoryElements(double scrollValue)
    {
        if (SingleComposer == null) return;
        foreach (var pair in adminInventoryElementBaseY)
        {
            var element = SingleComposer.GetElement(pair.Key);
            if (element == null) continue;
            element.Bounds.fixedY = pair.Value - scrollValue;
            element.Bounds.CalcWorldBounds();
        }
    }

    private static int AdminInventorySectionOrder(string section) => (section ?? "").ToLowerInvariant() switch
    {
        "hotbar" => 0,
        "backpack" => 1,
        "equipment" => 2,
        "crafting" => 3,
        "cursor" => 4,
        _ => 5
    };

    private void AddScrollableResults(
        GuiComposer composer,
        double innerX,
        double innerWidth,
        double resultsY,
        double resultsHeight)
    {
        // Reserve a footer for the record count and Previous/Next jump buttons.
        genericResultsY = resultsY + 4;
        genericResultsHeight = Math.Max(40, resultsHeight - 48);

        // BeginClip establishes a local coordinate space for its child elements.
        // Result rows must therefore start at local X/Y values, not the dialog-level
        // innerX/resultsY coordinates. Using absolute values here double-offset the
        // rows and caused the first record to render near the bottom of the pane.
        const double rowLocalX = 6;
        const double rowLocalY = 4;
        double rowWidth = innerWidth - 42;
        double rowHeight = CurrentGenericResultRowHeight;

        composer.BeginClip(
            ElementBounds.Fixed(innerX, genericResultsY, innerWidth - 24, genericResultsHeight));

        if (genericResultLines.Count == 0)
        {
            composer.AddRichtext(
                SuiteLocalization.Get("gui.literal.7cd1d44b3c"),
                CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(rowLocalX, rowLocalY, rowWidth, rowHeight - 2),
                "result-row-empty");
        }
        else
        {
            for (int i = 0; i < genericResultLines.Count; i++)
            {
                double y = rowLocalY + i * rowHeight;
                string raw = genericResultLines[i];
                string playerTarget = "";
                string blacklistModId = "";
                string inventoryId = "";
                string inventoryItemCode = "";
                string inventoryFingerprint = "";
                string searchedItemCode = "";
                string adminPlayerChoice = "";
                int inventorySlot = 0;
                int inventoryQuantity = 1;
                int searchedItemMaxStack = 1;
                int enforcementRuleIndex = -1;
                bool alreadyResolved = false;
                bool hasPlayerAction = section == "anticheat" && antiCheatView == "players" && TryGetAntiCheatPlayerActionTarget(raw, out playerTarget);
                bool hasBlacklistAction = section == "anticheat" && antiCheatView == "blacklist" && TryGetAntiCheatBlacklistActionTarget(raw, out blacklistModId, out alreadyResolved);
                bool hasInventoryAction = section == "admincommands" && adminCommandView == "inventory" &&
                    TryGetAdminInventorySlot(raw, out inventoryId, out inventorySlot, out inventoryQuantity, out inventoryItemCode, out inventoryFingerprint);
                bool hasItemAction = section == "admincommands" && adminCommandView == "inventory" &&
                    TryGetAdminItemSearchResult(raw, out searchedItemCode, out searchedItemMaxStack);
                bool hasAdminPlayerChoice = section == "admincommands" &&
                    TryGetAdminPlayerChoice(raw, out adminPlayerChoice);
                bool hasEnforcementAction = section == "anticheat" && antiCheatView == "enforcement" &&
                    TryGetAntiCheatEnforcementRuleIndex(raw, out enforcementRuleIndex);
                bool hasAction = hasPlayerAction || hasBlacklistAction || hasInventoryAction || hasItemAction || hasAdminPlayerChoice || hasEnforcementAction;
                double textWidth = hasAction ? rowWidth - AntiCheatRowActionWidth - 14 : rowWidth;

                composer.AddInteractiveElement(new SuiteResultRowElement(capi,
                    ElementBounds.Fixed(rowLocalX - 4, y - 2, rowWidth + 8, rowHeight - 2), (i & 1) == 1),
                    $"result-background-{i}");
                composer.AddRichtext(
                    FormatGenericResultLine(raw),
                    CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(rowLocalX, y, textWidth, rowHeight - 2),
                    $"result-row-{i}");

                if (hasPlayerAction)
                {
                    string target = playerTarget;
                    composer.AddSuiteInlineAction(
                        "View Report",
                        () => OpenAntiCheatPlayerReport(target),
                        ElementBounds.Fixed(rowLocalX + rowWidth - AntiCheatRowActionWidth, y + 16, AntiCheatRowActionWidth, 28),
                        $"result-action-{i}");
                }
                else if (hasBlacklistAction)
                {
                    string modId = blacklistModId;
                    composer.AddSuiteInlineAction(
                        alreadyResolved ? "Refresh FP" : "Resolve FP",
                        () => ResolveAntiCheatBlacklistFingerprint(modId),
                        ElementBounds.Fixed(rowLocalX + rowWidth - AntiCheatRowActionWidth, y + 18, AntiCheatRowActionWidth, 28),
                        $"result-action-{i}");
                }
                else if (hasInventoryAction)
                {
                    string inv = inventoryId;
                    string code = inventoryItemCode;
                    string fingerprint = inventoryFingerprint;
                    int slotIndex = inventorySlot;
                    int qty = inventoryQuantity;
                    composer.AddSuiteInlineAction(
                        "Select Slot",
                        () => SelectAdminInventorySlot(inv, slotIndex, qty, code, fingerprint),
                        ElementBounds.Fixed(rowLocalX + rowWidth - AntiCheatRowActionWidth, y + 14, AntiCheatRowActionWidth, 28),
                        $"result-action-{i}");
                }
                else if (hasItemAction)
                {
                    string code = searchedItemCode;
                    int maxStack = searchedItemMaxStack;
                    composer.AddSuiteInlineAction(
                        "Use Item",
                        () => SelectAdminItem(code, maxStack),
                        ElementBounds.Fixed(rowLocalX + rowWidth - AntiCheatRowActionWidth, y + 14, AntiCheatRowActionWidth, 28),
                        $"result-action-{i}");
                }
                else if (hasAdminPlayerChoice)
                {
                    string target = adminPlayerChoice;
                    composer.AddSuiteInlineAction(
                        "Select",
                        () => SelectAdminCommandPlayer(target),
                        ElementBounds.Fixed(rowLocalX + rowWidth - AntiCheatRowActionWidth, y + 14, AntiCheatRowActionWidth, 28),
                        $"result-action-{i}");
                }
                else if (hasEnforcementAction)
                {
                    int ruleIndex = enforcementRuleIndex;
                    composer.AddSuiteInlineAction(
                        "Edit Rule",
                        () => SelectAntiCheatEnforcementRule(ruleIndex),
                        ElementBounds.Fixed(rowLocalX + rowWidth - AntiCheatRowActionWidth, y + 30, AntiCheatRowActionWidth, 28),
                        $"result-action-{i}");
                }
            }
        }

        composer.EndClip();

        var scrollbarBounds = ElementBounds.Fixed(
            innerX + innerWidth - 18,
            genericResultsY - 5,
            18,
            genericResultsHeight);

        double footerY = resultsY + resultsHeight - 36;
        var previousBounds = ElementBounds.Fixed(innerX + innerWidth - 238, footerY, 100, 30);
        var nextBounds = ElementBounds.Fixed(innerX + innerWidth - 126, footerY, 100, 30);

        composer
            .AddVerticalScrollbar(OnGenericResultsScroll, scrollbarBounds, "results-scroll")
            .AddDynamicText(
                SuiteLocalization.Get("gui.workspace.resultcount", genericResultLines.Count),
                CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(innerX + 6, footerY + 5, 520, 24), "results-page-count")
            .AddSuiteButton(SuiteLocalization.Get("gui.previous"), ScrollGenericResultsPrevious, previousBounds)
            .AddHoverText(
                SuiteLocalization.Get("gui.workspace.previoushelp"),
                CairoFont.WhiteSmallText(), 290, previousBounds.FlatCopy(), "tip-results-prev")
            .AddSuiteButton(SuiteLocalization.Get("gui.next"), ScrollGenericResultsNext, nextBounds)
            .AddHoverText(
                SuiteLocalization.Get("gui.workspace.nexthelp"),
                CairoFont.WhiteSmallText(), 300, nextBounds.FlatCopy(), "tip-results-next");
    }

    private void ConfigureGenericResultsScrollbar()
    {
        var scrollbar = SingleComposer?.GetScrollbar("results-scroll");
        if (scrollbar == null) return;

        double contentHeight = Math.Max(
            genericResultsHeight,
            genericResultLines.Count * CurrentGenericResultRowHeight + 8);

        double maxScroll = Math.Max(0, contentHeight - genericResultsHeight);
        genericScrollValue = Math.Clamp(genericScrollValue, 0, maxScroll);

        scrollbar.SetHeights((float)genericResultsHeight, (float)contentHeight);
        scrollbar.SetScrollbarPosition((int)Math.Round(genericScrollValue));

        // SetScrollbarPosition may call the scroll handler. Pin once more so the
        // freshly composed rows always agree with the stored scroll position.
        PositionGenericResultRows(genericScrollValue);
    }

    private void OnGenericResultsScroll(float value)
    {
        genericScrollValue = Math.Max(0, value);
        PositionGenericResultRows(genericScrollValue);
    }

    private void PositionGenericResultRows(double scrollValue)
    {
        if (SingleComposer == null) return;

        const double rowLocalY = 4;

        for (int i = 0; i < genericResultLines.Count; i++)
        {
            double rowY = rowLocalY + i * CurrentGenericResultRowHeight - scrollValue;
            var background = SingleComposer.GetElement($"result-background-{i}");
            if (background != null)
            {
                background.Bounds.fixedY = rowY - 2;
                background.Bounds.CalcWorldBounds();
            }
            var row = SingleComposer.GetRichtext($"result-row-{i}");
            if (row != null)
            {
                row.Bounds.fixedY = rowY;
                row.Bounds.CalcWorldBounds();
            }

            // Row action buttons move with the row while scrolling inside the results clip.
            var action = SingleComposer.GetElement($"result-action-{i}");
            if (action != null)
            {
                action.Bounds.fixedY = rowY + (section == "admincommands" && (adminCommandView == "inventory" || adminCommandView == "players") ? 14 : antiCheatView == "enforcement" ? 30 : antiCheatView == "players" ? 16 : 18);
                action.Bounds.CalcWorldBounds();
            }
        }

        var emptyRow = SingleComposer.GetRichtext("result-row-empty");
        if (emptyRow != null)
        {
            emptyRow.Bounds.fixedY = rowLocalY - scrollValue;
            emptyRow.Bounds.CalcWorldBounds();
        }

        int firstVisible = genericResultLines.Count == 0 ? 0 : Math.Min(genericResultLines.Count, (int)(scrollValue / CurrentGenericResultRowHeight) + 1);
        int lastVisible = Math.Min(genericResultLines.Count, (int)Math.Ceiling((scrollValue + genericResultsHeight) / CurrentGenericResultRowHeight));
        SingleComposer.GetDynamicText("results-page-count")?.SetNewText(
            SuiteLocalization.Get("gui.workspace.resultrange", firstVisible, lastVisible, genericResultLines.Count));
    }

    private bool ScrollGenericResultsPrevious()
    {
        SetGenericScrollPosition(genericScrollValue - genericResultsHeight);
        return true;
    }

    private bool ScrollGenericResultsNext()
    {
        SetGenericScrollPosition(genericScrollValue + genericResultsHeight);
        return true;
    }

    private void SetGenericScrollPosition(double value)
    {
        double contentHeight = Math.Max(
            genericResultsHeight,
            genericResultLines.Count * CurrentGenericResultRowHeight + 8);
        double maxScroll = Math.Max(0, contentHeight - genericResultsHeight);

        genericScrollValue = Math.Clamp(value, 0, maxScroll);

        var scrollbar = SingleComposer?.GetScrollbar("results-scroll");
        if (scrollbar != null)
            scrollbar.SetScrollbarPosition((int)Math.Round(genericScrollValue));

        PositionGenericResultRows(genericScrollValue);
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        base.OnMouseWheel(args);
        if (args.IsHandled) return;

        if (section == "admincommands" && adminCommandView == "inventory" && IsAdminInventoryViewerResult())
        {
            double invDelta = Math.Abs(args.deltaPrecise) > 0.001f ? args.deltaPrecise : args.delta;
            if (Math.Abs(invDelta) > 0.001)
            {
                double maxScroll = Math.Max(0, adminInventoryContentHeight - adminInventoryViewportHeight);
                adminInventoryScrollValue = Math.Clamp(adminInventoryScrollValue - invDelta * 42, 0, maxScroll);
                var invScrollbar = SingleComposer?.GetScrollbar("admin-inventory-scroll");
                if (invScrollbar != null) invScrollbar.SetScrollbarPosition((int)Math.Round(adminInventoryScrollValue));
                PositionAdminInventoryElements(adminInventoryScrollValue);
                args.SetHandled();
            }
            return;
        }

        if (SingleComposer?.GetScrollbar("results-scroll") == null || genericResultLines.Count <= 1) return;

        double delta = Math.Abs(args.deltaPrecise) > 0.001f ? args.deltaPrecise : args.delta;
        if (Math.Abs(delta) <= 0.001) return;

        SetGenericScrollPosition(genericScrollValue - delta * CurrentGenericResultRowHeight * 2.5);
        args.SetHandled();
    }

    private bool ScanAnimalPopulation()
    {
        populationFirstIndex = 0;
        populationRows.Clear();
        populationMobTotals.Clear();
        populationMode = SuiteLocalization.Get("gui.scanningpopulation");
        pageHeaderText = SuiteLocalization.Get("gui.runtime.animallimiterpopulation");
        ComposeDialog();
        SendAnimalLimiterOperation("population");
        return true;
    }

    private void ParsePopulationLines(List<string>? lines)
    {
        populationRows.Clear();
        populationMobTotals.Clear();
        populationMode = "";

        if (lines == null)
        {
            populationMode = SuiteLocalization.Get("gui.nopopulationdata");
            populationFirstIndex = 0;
            return;
        }

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split('\t');

            if (parts.Length >= 2 && parts[0] == "MODE")
            {
                populationMode = parts[1];
                continue;
            }

            if (parts.Length >= 7 && parts[0] == "TOTAL")
            {
                if (int.TryParse(parts[2], out int totalTracked) &&
                    int.TryParse(parts[3], out int totalProtected) &&
                    int.TryParse(parts[4], out int totalEligible) &&
                    int.TryParse(parts[5], out int totalRegions) &&
                    int.TryParse(parts[6], out int totalOverRegions))
                {
                    populationMobTotals[parts[1]] = new PopulationMobTotal(
                        totalTracked, totalProtected, totalEligible, totalRegions, totalOverRegions);
                }
                continue;
            }

            if (parts.Length < 9 || parts[0] != "ROW") continue;

            if (!int.TryParse(parts[4], out int tracked)) continue;
            if (!int.TryParse(parts[5], out int rowLimit)) continue;
            if (!int.TryParse(parts[6], out int rowRadius)) continue;
            if (!int.TryParse(parts[7], out int protectedCount)) continue;
            if (!int.TryParse(parts[8], out int eligible)) continue;

            populationRows.Add(new PopulationDisplayRow(
                parts[1],
                parts[2],
                parts[3],
                tracked,
                rowLimit,
                rowRadius,
                protectedCount,
                eligible));
        }

        populationFirstIndex = 0;
    }

    private void ConfigurePopulationScrollbar()
    {
        var scrollbar = SingleComposer?.GetScrollbar("population-scroll");
        if (scrollbar == null) return;

        int total = Math.Max(PopulationVisibleRows, populationRows.Count);
        int maxFirst = Math.Max(0, populationRows.Count - PopulationVisibleRows);
        populationFirstIndex = Math.Clamp(populationFirstIndex, 0, maxFirst);

        scrollbar.SetHeights(PopulationVisibleRows, total);
        scrollbar.SetScrollbarPosition(populationFirstIndex);
    }

    private void OnPopulationScroll(float value)
    {
        int maxFirst = Math.Max(0, populationRows.Count - PopulationVisibleRows);
        int newFirst = Math.Clamp((int)Math.Round(value), 0, maxFirst);
        if (newFirst == populationFirstIndex) return;

        populationFirstIndex = newFirst;
        UpdatePopulationSlots();
    }

    private void UpdatePopulationSlots()
    {
        if (SingleComposer == null) return;

        for (int slot = 0; slot < PopulationVisibleRows; slot++)
        {
            int sourceIndex = populationFirstIndex + slot;
            PopulationDisplayRow? row = sourceIndex < populationRows.Count
                ? populationRows[sourceIndex]
                : null;

            SetPopulationCell($"population-mob-{slot}", row == null ? "" : $"<strong>{Escape(row.MobId)}</strong>");
            SetPopulationCell($"population-region-{slot}", row == null ? "" : Escape(row.Region));
            SetPopulationCell($"population-rule-{slot}", row == null ? "" : Escape(row.Rule));
            SetPopulationCell($"population-tracked-{slot}", row == null ? "" : row.Tracked.ToString());

            string mobTotalText = "";
            if (row != null)
            {
                mobTotalText = populationMobTotals.TryGetValue(row.MobId, out var mobTotal)
                    ? $"<strong>{mobTotal.Tracked}</strong>"
                    : $"<strong>{row.Tracked}</strong>";
            }
            SetPopulationCell($"population-mobtotal-{slot}", mobTotalText);

            SetPopulationCell($"population-limit-{slot}", row == null ? "" : row.Limit.ToString());
            SetPopulationCell($"population-radius-{slot}", row == null ? "" : row.Radius + " ch");
            SetPopulationCell(
                $"population-protected-{slot}",
                row == null
                    ? ""
                    : row.Protected > 0
                        ? $"<font color=\"#ffd166\"><strong>{row.Protected}</strong></font>"
                        : "0");

            if (row == null)
            {
                SetPopulationCell($"population-status-{slot}", "");
            }
            else
            {
                int excess = Math.Max(0, row.Eligible - row.Limit);
                string status = excess > 0
                    ? $"<font color=\"#ff6b6b\"><strong>OVER +{excess}</strong></font>"
                    : "<font color=\"#8fda72\"><strong>OK</strong></font>";
                SetPopulationCell($"population-status-{slot}", status);
            }
        }
    }

    private void SetPopulationCell(string key, string text)
    {
        SingleComposer?.GetRichtext(key)?.SetNewText(text, CairoFont.WhiteSmallText());
    }

    private void SetRemovalLogResults(List<string>? lines)
    {
        removalLogLines.Clear();

        if (lines == null || lines.Count == 0)
            removalLogLines.Add("No removal records found.");
        else
            removalLogLines.AddRange(lines);

        removalLogFirstIndex = 0;
    }

    private void ConfigureRemovalLogScrollbar()
    {
        var scrollbar = SingleComposer?.GetScrollbar("removal-scroll");
        if (scrollbar == null) return;

        int total = Math.Max(RemovalLogVisibleRows, removalLogLines.Count);
        int maxFirst = Math.Max(0, removalLogLines.Count - RemovalLogVisibleRows);
        removalLogFirstIndex = Math.Clamp(removalLogFirstIndex, 0, maxFirst);

        scrollbar.SetHeights(RemovalLogVisibleRows, total);
        scrollbar.SetScrollbarPosition(removalLogFirstIndex);
    }

    private void OnRemovalLogScroll(float value)
    {
        int maxFirst = Math.Max(0, removalLogLines.Count - RemovalLogVisibleRows);
        int newFirst = Math.Clamp((int)Math.Round(value), 0, maxFirst);
        if (newFirst == removalLogFirstIndex) return;

        removalLogFirstIndex = newFirst;
        UpdateRemovalLogSlots();
    }

    private void UpdateRemovalLogSlots()
    {
        if (SingleComposer == null) return;

        for (int slot = 0; slot < RemovalLogVisibleRows; slot++)
        {
            var row = SingleComposer.GetRichtext($"removal-row-{slot}");
            if (row == null) continue;

            int sourceIndex = removalLogFirstIndex + slot;
            string text = sourceIndex < removalLogLines.Count
                ? Escape(removalLogLines[sourceIndex])
                : "";

            row.SetNewText(text, CairoFont.WhiteSmallText());
        }
    }

    private bool ScrollRemovalLogsPrevious()
    {
        SetRemovalLogPosition(removalLogFirstIndex - RemovalLogVisibleRows);
        return true;
    }

    private bool ScrollRemovalLogsNext()
    {
        SetRemovalLogPosition(removalLogFirstIndex + RemovalLogVisibleRows);
        return true;
    }

    private void SetRemovalLogPosition(int firstIndex)
    {
        int maxFirst = Math.Max(0, removalLogLines.Count - RemovalLogVisibleRows);
        removalLogFirstIndex = Math.Clamp(firstIndex, 0, maxFirst);

        var scrollbar = SingleComposer?.GetScrollbar("removal-scroll");
        if (scrollbar != null)
            scrollbar.SetScrollbarPosition(removalLogFirstIndex);

        UpdateRemovalLogSlots();
    }

    private void ParseAnimalRuleLines(List<string>? lines)
    {
        animalRuleRows.Clear();
        animalRuleCount = 0;
        animalExclusionCount = 0;

        if (lines == null) return;

        bool inRules = false;
        bool inExclusions = false;

        foreach (var raw in lines)
        {
            var line = raw?.Trim() ?? "";

            if (line.StartsWith("Rules:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line.Substring(6).Trim(), out animalRuleCount);
                continue;
            }

            if (line.StartsWith("Exclusions:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line.Substring(11).Trim(), out animalExclusionCount);
                continue;
            }

            if (line.Equals("LIMIT RULES", StringComparison.OrdinalIgnoreCase))
            {
                inRules = true;
                inExclusions = false;
                continue;
            }

            if (line.Equals("EXCLUSIONS", StringComparison.OrdinalIgnoreCase))
            {
                inRules = false;
                inExclusions = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) || line == "(none)") continue;

            if (inRules)
            {
                var parts = line.Split('|');
                var pattern = parts.Length > 0 ? parts[0].Trim() : line;
                var limitText = parts.Length > 1 ? parts[1].Trim() : "";
                var radiusText = parts.Length > 2 ? parts[2].Trim() : "";
                animalRuleRows.Add(new AnimalRuleDisplayRow(pattern, limitText, radiusText, false));
            }
            else if (inExclusions)
            {
                animalRuleRows.Add(new AnimalRuleDisplayRow(line, "Excluded", "—", true));
            }
        }

        int totalPages = Math.Max(1, (animalRuleRows.Count + AnimalRulesPerPage - 1) / AnimalRulesPerPage);
        animalRulePage = Math.Clamp(animalRulePage, 0, totalPages - 1);
    }

    private bool PreviousAnimalRulePage()
    {
        if (animalRulePage > 0)
        {
            animalRulePage--;
            ComposeDialog();
        }
        return true;
    }

    private bool NextAnimalRulePage()
    {
        int totalPages = Math.Max(1, (animalRuleRows.Count + AnimalRulesPerPage - 1) / AnimalRulesPerPage);
        if (animalRulePage + 1 < totalPages)
        {
            animalRulePage++;
            ComposeDialog();
        }
        return true;
    }

    private void GetResultsViewport(out double y, out double height)
    {
        if (section == "animals")
        {
            if (animalView == "history")
            {
                y = 176;
                height = 474;
            }
            else if (animalView == "rules")
            {
                y = 232;
                height = 418;
            }
            else if (animalView == "settings")
            {
                y = 560;
                height = 92;
            }
            else
            {
                y = 406;
                height = 244;
            }
            return;
        }

        if (view == "status")
        {
            y = 170;
            height = 480;
        }
        else
        {
            y = 324;
            height = 326;
        }
    }

    private static string ViewTitle(string value) => value switch
    {
        "containers" => "Container history",
        "entities" => "Entity history",
        "players" => "Player history",
        "settings" => "BlockLogger settings",
        "animals" => "Animal Limiter",
        _ => "Block history"
    };

    private string FormatGenericResultLine(string? raw)
    {
        if (section == "blocklogger" && view == "status")
            return FormatStatusResultLine(raw);

        if (section == "animals" && animalView == "limiter")
            return FormatStatusResultLine(raw);

        if (section == "loginprotection" && loginView == "status")
            return FormatStatusResultLine(raw);

        if (section == "webserver" && webView == "webpanel")
            return FormatStatusResultLine(raw);

        if (section == "anticheat")
            return FormatAntiCheatResultLine(raw);

        if (section == "admincommands")
            return FormatAdminCommandResultLine(raw);

        return FormatAuditResultLine(raw);
    }

    private static string FormatAdminCommandResultLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = raw.Trim();
        string[] parts = text.Split('\t');
        if (parts.Length >= 9 && parts[0] == "INV")
        {
            string inventory = parts[1];
            string sectionName = parts[2];
            string slot = parts[3];
            bool empty = parts[4] == "1" || parts[4].Equals("true", StringComparison.OrdinalIgnoreCase);
            if (empty)
            {
                return $"<font color=\"#7db6ff\"><strong>{Escape(sectionName)}</strong></font>  " +
                       $"<strong>{Escape(inventory)}[{Escape(slot)}]</strong>  <font color=\"#91a0ae\">EMPTY SLOT</font><br>" +
                       "<font color=\"#263746\">────────────────────────────────────────────────────────────</font>";
            }

            string qty = parts[5];
            string max = parts[6];
            string code = parts[7];
            string name = parts[8];
            string detail = "";
            if (parts.Length >= 11 && int.TryParse(parts[9], out int durability) && int.TryParse(parts[10], out int maxDurability) && maxDurability > 0)
                detail += $" • durability {durability}/{maxDurability}";
            if (parts.Length >= 12 && float.TryParse(parts[11], NumberStyles.Float, CultureInfo.InvariantCulture, out float temp) && Math.Abs(temp) > 0.05f)
                detail += $" • {temp:0.#}°C";
            return $"<font color=\"#7db6ff\"><strong>{Escape(sectionName)}</strong></font>  " +
                   $"<strong>{Escape(inventory)}[{Escape(slot)}]</strong>  {Escape(qty)}x <strong>{Escape(name)}</strong><br>" +
                   $"<font color=\"#91a0ae\">{Escape(code)} • max {Escape(max)}{Escape(detail)}</font>";
        }

        if (parts.Length >= 5 && parts[0] == "ITEM")
        {
            return $"<font color=\"#7db6ff\"><strong>{Escape(parts[2])}</strong></font><br>" +
                   $"<font color=\"#91a0ae\">{Escape(parts[1])} • {Escape(parts[3])} • max stack {Escape(parts[4])}</font><br>" +
                   "<font color=\"#263746\">────────────────────────────────────────────────────────────</font>";
        }

        if (parts.Length >= 7 && parts[0] == "ADMINPLAYER")
        {
            return $"<font color=\"#7db6ff\"><strong>{Escape(parts[2])}</strong></font> " +
                   $"<font color=\"#77d18b\">{Escape(SuiteLocalization.Get("gui.runtime.online"))}</font><br>" +
                   $"<font color=\"#91a0ae\">{Escape(parts[3])} ms • {Escape(parts[4])} • {Escape(parts[5])} • HP {Escape(parts[6])}</font>";
        }

        if (parts.Length >= 3 && parts[0] == "ADMINSTATUS")
        {
            bool ok = string.Equals(parts[1], "OK", StringComparison.OrdinalIgnoreCase);
            string color = ok ? "#77d18b" : "#ff7b7b";
            return $"<font color=\"{color}\"><strong>{Escape(SuiteLocalization.Get(ok ? "gui.runtime.success" : "gui.runtime.attention"))}</strong></font>  {Escape(parts[2])}";
        }
        if (parts.Length >= 2 && parts[0] == "ADMINLINE")
            return $"<font color=\"#b8c6d4\">{Escape(parts[1])}</font>";
        return Escape(text);
    }

    private static string FormatAntiCheatResultLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = raw.Trim();
        string[] parts = text.Split('\t');

        if (parts.Length >= 10 && parts[0] == "PLAYER")
        {
            int.TryParse(parts[1], out int risk);
            string rating = parts[2];
            string status = parts[3];
            string player = parts[4];
            string client = parts[5];
            string findings = parts[6];
            string runtime = parts[7];
            string mods = parts[8];
            string assemblies = parts[9];
            string riskColor = RiskColor(risk);
            string statusColor = StatusColor(status);
            return $"<font color=\"{riskColor}\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.risk"))} {risk}/100 • {Escape(rating)}</strong></font>   " +
                   $"<strong>{Escape(player)}</strong> <font color=\"{statusColor}\">[{Escape(status)}]</font><br>" +
                   $"<font color=\"#91a0ae\">{Escape(SuiteLocalization.Get("gui.runtime.findings"))} {Escape(findings)} • {Escape(SuiteLocalization.Get("gui.runtime.runtime"))} +{Escape(runtime)} • {Escape(SuiteLocalization.Get("gui.runtime.client"))} {Escape(client)} • {Escape(SuiteLocalization.Get("gui.runtime.mods"))} {Escape(mods)} • {Escape(SuiteLocalization.Get("gui.runtime.assemblies"))} {Escape(assemblies)}</font><br>" +
                   "<font color=\"#263746\">────────────────────────────────────────────────────────────</font>";
        }

        if (parts.Length >= 13 && parts[0] == "REPORT")
        {
            int.TryParse(parts[3], out int before);
            int.TryParse(parts[4], out int after);
            int.TryParse(parts[7], out int confidenceScore);
            int.TryParse(parts[8], out int severity);
            string riskColor = RiskColor(after);
            string confidenceColor = ConfidenceColor(parts[6]);
            string severityColor = SeverityColor(severity);
            string summary = ShortDisplay(parts[11], 220);
            string recommendation = string.IsNullOrWhiteSpace(parts[12]) ? SuiteLocalization.Get("gui.runtime.noactionrecommendation") : ShortDisplay(parts[12], 180);
            return $"<font color=\"#7db6ff\"><strong>{Escape(parts[2])}</strong></font>  <font color=\"#91a0ae\">{Escape(parts[1])}</font><br>" +
                   $"<strong>{Escape(SuiteLocalization.Get("gui.runtime.risklabel"))}:</strong> {before} → <font color=\"{riskColor}\"><strong>{after}/100 • {Escape(parts[5])}</strong></font><br>" +
                   $"<strong>{Escape(SuiteLocalization.Get("gui.runtime.detector"))}:</strong> <font color=\"{severityColor}\"><strong>{Escape(parts[9])} / {Escape(parts[10])}</strong></font>   " +
                   $"<strong>{Escape(SuiteLocalization.Get("gui.runtime.confidence"))}:</strong> <font color=\"{confidenceColor}\"><strong>{Escape(parts[6])} {confidenceScore}%</strong></font><br>" +
                   $"<strong>{Escape(SuiteLocalization.Get("gui.runtime.incident"))}:</strong> {Escape(summary)}<br>" +
                   $"<font color=\"#91a0ae\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.recommended"))}:</strong> {Escape(recommendation)}</font><br>" +
                   "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
        }

        if (parts.Length >= 11 && parts[0] == "RULE")
        {
            bool enabled = parts[4] == "1" || parts[4].Equals("true", StringComparison.OrdinalIgnoreCase);
            string stateColor = enabled ? "#8fda72" : "#91a0ae";
            string subject = string.IsNullOrWhiteSpace(parts[7]) ? SuiteLocalization.Get("gui.runtime.anysubject") : parts[7];
            string duration = string.IsNullOrWhiteSpace(parts[9]) ? "n/a" : parts[9];
            string message = string.IsNullOrWhiteSpace(parts[10]) ? SuiteLocalization.Get("gui.runtime.defaultacmessage") : ShortDisplay(parts[10], 150);
            return $"<font color=\"{stateColor}\"><strong>{Escape(SuiteLocalization.Get(enabled ? "gui.runtime.enabledcaps" : "gui.runtime.disabledcaps"))}</strong></font>  " +
                   $"<font color=\"#7db6ff\"><strong>{Escape(parts[3])}</strong></font><br>" +
                   $"<strong>{Escape(SuiteLocalization.Get("gui.runtime.when"))}:</strong> {Escape(parts[5])} = {Escape(parts[6])}   <strong>{Escape(SuiteLocalization.Get("gui.runtime.subject"))}:</strong> {Escape(subject)}<br>" +
                   $"<strong>{Escape(SuiteLocalization.Get("gui.runtime.action"))}:</strong> {Escape(parts[8])}   <strong>{Escape(SuiteLocalization.Get("gui.runtime.duration"))}:</strong> {Escape(duration)}<br>" +
                   $"<font color=\"#91a0ae\">{Escape(SuiteLocalization.Get("gui.runtime.message"))}: {Escape(message)}</font><br>" +
                   "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
        }

        if (parts.Length >= 8 && parts[0] == "BLACKLIST")
        {
            string releaseInfo = parts.Length >= 9 && !string.IsNullOrWhiteSpace(parts[8])
                ? $" • {Escape(SuiteLocalization.Get("gui.runtime.latest"))} {Escape(parts[8])}"
                : "";
            string resolvedAt = parts.Length >= 10 && !string.IsNullOrWhiteSpace(parts[9]) && !parts[9].Equals("never", StringComparison.OrdinalIgnoreCase)
                ? $" • {Escape(SuiteLocalization.Get("gui.runtime.refreshed"))} {Escape(parts[9])}"
                : "";
            return $"<font color=\"#ffad66\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.blacklisted"))} • {Escape(parts[1])}</strong></font>   <font color=\"#b8c5cf\">{Escape(parts[2])}</font><br>" +
                   $"<font color=\"#91a0ae\">{Escape(SuiteLocalization.Get("gui.runtime.packagecontent"))} {Escape(parts[3])} / {Escape(parts[4])} • {Escape(SuiteLocalization.Get("gui.runtime.assemblyfp"))} {Escape(parts[5])} • {Escape(SuiteLocalization.Get("gui.runtime.manual"))} {Escape(parts[6])} • {Escape(SuiteLocalization.Get("gui.runtime.releases"))} {Escape(parts[7])}{releaseInfo}{resolvedAt}</font><br>" +
                   "<font color=\"#263746\">────────────────────────────────────────────────────────────</font>";
        }

        if (parts.Length >= 5 && parts[0] == "FINGERPRINT")
        {
            return $"<font color=\"#7db6ff\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.clientruntimefps"))}</strong></font><br>" +
                   $"<font color=\"#91a0ae\">DLL {Escape(parts[1])} • package {Escape(parts[2])}<br>MVID {Escape(parts[3])} • runtime {Escape(parts[4])}</font><br>" +
                   "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
        }

        if (parts.Length >= 5 && parts[0] == "MOD")
        {
            return $"<font color=\"#7db6ff\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.clientmod"))}</strong></font>  <strong>{Escape(parts[1])}</strong> {Escape(parts[2])}<br>" +
                   $"<font color=\"#91a0ae\">{Escape(parts[3])} • {Escape(parts[4])}</font><br>" +
                   "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
        }

        if (parts.Length >= 9 && parts[0] == "FINDING")
        {
            int.TryParse(parts[1], out int severity);
            int.TryParse(parts[3], out int confidenceScore);
            string confidenceColor = ConfidenceColor(parts[2]);
            string severityColor = SeverityColor(severity);
            return $"<font color=\"{severityColor}\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.findingcaps"))} • {Escape(parts[4])} / {Escape(parts[5])}</strong></font>   " +
                   $"<font color=\"{confidenceColor}\"><strong>{Escape(parts[2])} {confidenceScore}%</strong></font><br>" +
                   $"{Escape(parts[6])}<br><font color=\"#91a0ae\">{Escape(SuiteLocalization.Get("gui.runtime.subject"))}: {Escape(parts[7])}" +
                   (string.IsNullOrWhiteSpace(parts[8]) ? "" : $" • {Escape(SuiteLocalization.Get("gui.runtime.recommended"))}: {Escape(parts[8])}") +
                   "</font><br><font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
        }

        if (text.StartsWith("Runtime + ", StringComparison.Ordinal))
        {
            return $"<font color=\"#c69cff\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.runtimechange"))}</strong></font><br>{Escape(text[10..])}<br>" +
                   "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
        }

        if (text.StartsWith("Scanner ", StringComparison.Ordinal))
        {
            return $"<font color=\"#7db6ff\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.scannermetric"))}</strong></font><br>{Escape(text[8..])}<br>" +
                   "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
        }

        int colon = text.IndexOf(':');
        if (colon > 0)
        {
            string labelRaw = text[..colon].Trim();
            string valueRaw = text[(colon + 1)..].Trim();
            string color = labelRaw.Equals("Error", StringComparison.OrdinalIgnoreCase)
                ? "#ff7474"
                : labelRaw.Equals("Risk rating", StringComparison.OrdinalIgnoreCase)
                    ? RiskRatingColor(valueRaw)
                    : labelRaw.Equals("Status", StringComparison.OrdinalIgnoreCase)
                        ? StatusColor(valueRaw)
                        : labelRaw.Contains("Blacklisted", StringComparison.OrdinalIgnoreCase)
                            ? "#ffad66"
                            : labelRaw.Contains("Persistent reports", StringComparison.OrdinalIgnoreCase)
                                ? "#7db6ff"
                                : "#edf3f8";
            string displayLabel = labelRaw.Equals("Error", StringComparison.OrdinalIgnoreCase) ? SuiteLocalization.Get("gui.runtime.error")
                : labelRaw.Equals("Risk rating", StringComparison.OrdinalIgnoreCase) ? SuiteLocalization.Get("gui.runtime.riskrating")
                : labelRaw.Equals("Status", StringComparison.OrdinalIgnoreCase) ? SuiteLocalization.Get("gui.runtime.status")
                : labelRaw.Equals("Persistent reports", StringComparison.OrdinalIgnoreCase) ? SuiteLocalization.Get("gui.runtime.persistentreports")
                : labelRaw;
            return $"<strong>{Escape(displayLabel)}:</strong> <font color=\"{color}\">{Escape(valueRaw)}</font>";
        }

        return Escape(text);
    }

    private static string ShortDisplay(string? value, int maxChars)
    {
        string text = (value ?? "").Trim();
        if (text.Length <= maxChars) return text;
        return text[..Math.Max(1, maxChars - 1)].TrimEnd() + "…";
    }

    private static string RiskColor(int risk) => risk >= 75 ? "#ff7474" : risk >= 50 ? "#ffad66" : risk >= 25 ? "#ffd166" : risk >= 10 ? "#c69cff" : "#8fda72";
    private static string RiskRatingColor(string rating) => rating.Trim().ToLowerInvariant() switch
    {
        "critical" => "#ff7474",
        "high" => "#ffad66",
        "elevated" => "#ffd166",
        "guarded" => "#c69cff",
        _ => "#8fda72"
    };
    private static string StatusColor(string status) => status.Trim().ToLowerInvariant() switch
    {
        "suspicious" or "failed" or "protocolmismatch" => "#ff7474",
        "review" or "timedout" => "#ffd166",
        "clean" => "#8fda72",
        _ => "#7db6ff"
    };
    private static string ConfidenceColor(string confidence) => confidence.Trim().ToLowerInvariant() switch
    {
        "critical" => "#ff7474",
        "high" => "#ffad66",
        "medium" => "#ffd166",
        "low" => "#c69cff",
        _ => "#91a0ae"
    };
    private static string SeverityColor(int severity) => severity >= 4 ? "#ff7474" : severity >= 3 ? "#ffad66" : severity >= 2 ? "#ffd166" : severity >= 1 ? "#c69cff" : "#91a0ae";

    private static string FormatStatusResultLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw.Trim();
        int colon = text.IndexOf(':');

        if (colon > 0)
        {
            var label = Escape(text[..colon]);
            var value = Escape(text[(colon + 1)..].TrimStart());
            return $"<strong>{label}:</strong> {value}";
        }

        // A few status lines may not use a colon. Keep those readable without
        // guessing at their structure.
        return Escape(text);
    }

    private static string FormatAuditResultLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // Audit rows are emitted as:
        // timestamp | actor | action detail | x,y,z | optional relative coords | ingame date
        // Non-audit/status rows fall back to plain escaped text.
        var parts = raw.Split(new[] { " | " }, StringSplitOptions.None);
        if (parts.Length < 5) return Escape(raw);

        var timestamp = Escape(parts[0]);
        var actor = Escape(parts[1]);
        var detail = Escape(parts[2]);
        var coords = Escape(parts[3]);

        string color = ActionColor(parts[2]);
        string coloredDetail = $"<font color=\"{color}\">{detail}</font>";

        var tail = "";
        for (int i = 4; i < parts.Length; i++)
            tail += $" | {Escape(parts[i])}";

        return $"{timestamp} | <strong>{actor}</strong> | {coloredDetail} | <strong>{coords}</strong>{tail}";
    }

    private static string ActionColor(string? detail)
    {
        var value = detail?.Trim().ToLowerInvariant() ?? "";

        // Blocks
        if (value.StartsWith("broke ")) return "#ff7070";
        if (value.StartsWith("placed ")) return "#8fda72";
        if (value.StartsWith("used ")) return "#ffd166";

        // Containers
        if (value.StartsWith("opened ")) return "#72c7ff";
        if (value.StartsWith("took ")) return "#ffad66";
        if (value.StartsWith("put ")) return "#66d9c2";

        // Entities
        if (value.StartsWith("interacted with ")) return "#c69cff";
        if (value.StartsWith("spawned ")) return "#78dcca";
        if (value.EndsWith(" died", StringComparison.Ordinal)) return "#ff6b6b";
        if (value.StartsWith("despawned ")) return "#aeb8d4";

        return "#e8e0cc";
    }

    private static string FormatLines(List<string>? lines)
    {
        if (lines == null || lines.Count == 0) return "No records found.";
        var escaped = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++) escaped[i] = Escape(lines[i]);
        return string.Join("<br>", escaped);
    }

    private static string ErrorText(string? text) => $"<font color=\"#ff8080\">{Escape(text)}</font>";
    private static string Escape(string? text) => WebUtility.HtmlEncode(text ?? "");
}
