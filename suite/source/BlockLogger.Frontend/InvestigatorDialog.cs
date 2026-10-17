using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using BlockLogger.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockLogger.Frontend;

public sealed class InvestigatorDialog : GuiDialog
{
	private sealed record PopulationDisplayRow(string MobId, string Region, string Rule, int Tracked, int Limit, int Radius, int Protected, int Eligible);

	private sealed record PopulationMobTotal(int Tracked, int Protected, int Eligible, int Regions, int OverRegions);

	private sealed record AnimalRuleDisplayRow(string Pattern, string Limit, string Radius, bool IsExclusion);

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

	private const double ResultsX = 34.0;

	private const double ResultsY = 250.0;

	private const double ResultsWidth = 780.0;

	private const double ResultsHeight = 385.0;

	private const double ScrollbarX = 822.0;

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

	private readonly Dictionary<string, double> adminInventoryElementBaseY = new Dictionary<string, double>(StringComparer.Ordinal);

	private string pendingAdminConfirmOperation = "";

	private DateTime pendingAdminConfirmUntilUtc = DateTime.MinValue;

	private string antiCheatPlayerSearch = "";

	private string antiCheatReportSearch = "";

	private string antiCheatBlacklistModId = "";

	private string antiCheatBlacklistSourcePlayer = "";

	private bool antiCheatEnabled = true;

	private bool applyingAntiCheatControls;

	private readonly Dictionary<string, string> antiCheatPlayerSummary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly List<AntiCheatConsequenceRuleGuiPacket> antiCheatEnforcementRules = new List<AntiCheatConsequenceRuleGuiPacket>();

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

	private double acPacketReachUseExtraTolerance = 0.2;

	private double acPacketReachBreakExtraTolerance = 0.0;

	private int acPacketReachNearThresholdStrikes = 3;

	private int acPacketReachStrikeWindowMs = 3500;

	private int acPacketReachStrikeDecayMs = 1800;

	private int acPacketVeinWindowMs = 2200;

	private int acPacketConnectedBreakLimit = 18;

	private int acPacketMiningDuplicateTargetMs = 450;

	private double acPacketMiningCadenceMinExpectedMs = 250.0;

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

	private double acHardMoveDistance = 32.0;

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

	private double acTpsDegradedPercent = 85.0;

	private double acTpsCriticalPercent = 60.0;

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

	private int animalRadiusChunks = 3;

	private int animalRuleRadiusChunks = 3;

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

	private DiscordIntegrationGuiSettingsPacket discordSettings = new DiscordIntegrationGuiSettingsPacket();

	private bool discordAvailable;

	private bool applyingDiscordControls;

	private string discordStatusMessage = "Discord integration status has not been loaded yet.";

	private bool motdAvailable;

	private string motdVersion = "";

	private string motdStatusMessage = "Community MOTD status has not been loaded yet.";

	private JObject motdConfig = new JObject();

	private bool applyingMotdControls;

	private DateTime cullConfirmUntilUtc = DateTime.MinValue;

	private long requestId;

	private long lastResponseId;

	private long lastDiscordResponseId;

	private string pageHeaderText = SuiteLocalization.Get("gui.runtime.readyblockhistory");

	private string resultText = SuiteLocalization.Get("gui.runtime.noresults");

	private readonly List<string> genericResultLines = new List<string>();

	private double genericScrollValue;

	private double genericResultsY;

	private double genericResultsHeight;

	private const double GenericResultRowHeight = 30.0;

	private const double AntiCheatRowActionWidth = 118.0;

	private readonly List<ProfilerSamplePacket> profilerSamples = new List<ProfilerSamplePacket>();

	private readonly List<ProfilerSlowSpikePacket> profilerSlowSpikes = new List<ProfilerSlowSpikePacket>();

	private readonly List<ProfilerHotspotPacket> profilerHotspots = new List<ProfilerHotspotPacket>();

	private readonly List<ProfilerModHotspotPacket> profilerModHotspots = new List<ProfilerModHotspotPacket>();

	private readonly List<ProfilerPhasePacket> profilerPhases = new List<ProfilerPhasePacket>();

	private readonly List<ProfilerProfileSlowTickPacket> profilerProfileSlowTicks = new List<ProfilerProfileSlowTickPacket>();

	private readonly List<ProfilerOffThreadReportPacket> profilerOffThreadReports = new List<ProfilerOffThreadReportPacket>();

	private readonly List<ProfilerDiagnosticIncidentPacket> profilerDiagnosticIncidents = new List<ProfilerDiagnosticIncidentPacket>();

	private ProfilerDiagnosticSummaryPacket profilerDiagnosticSummary = new ProfilerDiagnosticSummaryPacket();

	private string profilerView = "overview";

	private int profilerLoadedModCount;

	private const int ProfilerLiveRefreshMs = 2000;

	private bool profilerLiveUpdates;

	private bool profilerLiveRequestPending;

	private int profilerLiveGeneration;

	private ProfilerSamplePacket profilerCurrent = new ProfilerSamplePacket();

	private ProfilerProfileSummaryPacket profilerLastProfile = new ProfilerProfileSummaryPacket();

	private bool profilerEnabled;

	private bool applyingProfilerControls;

	private bool profilerRunning;

	private int profilerRemainingSeconds;

	private string profilerMessage = SuiteLocalization.Get("gui.runtime.profilerprompt");

	private readonly List<string> removalLogLines = new List<string>();

	private int removalLogFirstIndex;

	private const int RemovalLogVisibleRows = 9;

	private const double RemovalLogRowHeight = 50.0;

	private readonly List<PopulationDisplayRow> populationRows = new List<PopulationDisplayRow>();

	private readonly Dictionary<string, PopulationMobTotal> populationMobTotals = new Dictionary<string, PopulationMobTotal>(StringComparer.OrdinalIgnoreCase);

	private int populationFirstIndex;

	private string populationMode = "";

	private const int PopulationVisibleRows = 16;

	private const double PopulationRowHeight = 27.0;

	private readonly List<AnimalRuleDisplayRow> animalRuleRows = new List<AnimalRuleDisplayRow>();

	private int animalRulePage;

	private int animalRuleCount;

	private int animalExclusionCount;

	private const int AnimalRulesPerPage = 18;

	private string previousWorkspace = "home";

	private string lastWorkspace = "blocklogger";

	private string lastComposedWorkspace = "home";

	private static readonly string[] SuiteSections = new string[8] { "home", "blocklogger", "animals", "profiler", "loginprotection", "webserver", "anticheat", "admincommands" };

	private double CurrentGenericResultRowHeight
	{
		get
		{
			if (section == "admincommands")
			{
				return (adminCommandView == "inventory") ? 58 : 42;
			}
			double result;
			if (section == "anticheat")
			{
				string text = antiCheatView;
				if (1 == 0)
				{
				}
				double num = text switch
				{
					"reports" => 168.0, 
					"player" => 168.0, 
					"enforcement" => 138.0, 
					"players" => 60.0, 
					"blacklist" => 64.0, 
					"settings" => 40.0, 
					_ => 30.0, 
				};
				if (1 == 0)
				{
				}
				result = num;
			}
			else
			{
				result = 30.0;
			}
			return result;
		}
	}

	public override string ToggleKeyCombinationCode => "blocklogger-investigator";

	public InvestigatorDialog(ICoreClientAPI api, Action<InvestigatorQueryRequest> sendQuery, Action<InvestigatorStatusRequest> sendStatus, Action<BlockLoggerSettingsRequest> sendBlockLoggerSettings, Action<AnimalLimiterRequest> sendAnimalLimiter, Action<ProfilerRequest> sendProfiler, Action<LoginProtectionRequest> sendLoginProtection, Action<WebDashboardRequest> sendWebDashboard, Action<AntiCheatGuiRequest> sendAntiCheat, Action<AdminCommandGuiRequest> sendAdminCommand, Action<DiscordIntegrationGuiRequest> sendDiscordIntegration)
		: base(api)
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

	public override bool TryOpen(bool withFocus)
	{
		return !disposed && base.TryOpen(withFocus);
	}

	public override void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			StopProfilerLiveUpdates();
			ClearComposers();
			capi.Gui.LoadedGuis.Remove(this);
			base.Dispose();
		}
	}

	public void Show(InvestigatorQueryResponse response)
	{
		if (response.RequestId >= lastResponseId)
		{
			lastResponseId = response.RequestId;
			if (!(section != "blocklogger") && string.Equals(view, response.View, StringComparison.OrdinalIgnoreCase))
			{
				pageHeaderText = (response.Success ? Escape(response.Header) : "BlockLogger");
				SetGenericResults(response.Success ? response.Lines : new List<string> { response.Error });
				ComposeDialog();
			}
		}
	}

	public void Show(InvestigatorStatusResponse response)
	{
		if (response.RequestId >= lastResponseId)
		{
			lastResponseId = response.RequestId;
			if (!(section != "blocklogger") && !(view != "status"))
			{
				pageHeaderText = SuiteLocalization.Get("gui.runtime.blockloggerstatus");
				SetGenericResults(response.Success ? response.Lines : new List<string> { response.Error });
				ComposeDialog();
			}
		}
	}

	public void Show(BlockLoggerSettingsResponse response)
	{
		if (response.RequestId >= lastResponseId)
		{
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
			if (!(section != "blocklogger") && !(view != "settings"))
			{
				pageHeaderText = (response.Success ? SuiteLocalization.Get("gui.runtime.blockloggersettings") : SuiteLocalization.Get("gui.runtime.blockloggersettingserror"));
				ComposeDialog();
				ApplyBlockLoggerControlValues();
			}
		}
	}

	public void Show(AnimalLimiterResponse response)
	{
		if (response.RequestId < lastResponseId)
		{
			return;
		}
		lastResponseId = response.RequestId;
		if (response.Success)
		{
			animalEnabled = response.Enabled;
			animalRadiusChunks = response.RadiusChunks;
			if (string.IsNullOrWhiteSpace(animalPattern))
			{
				animalRuleRadiusChunks = response.RadiusChunks;
			}
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
		pageHeaderText = (response.Success ? Escape(response.Message) : "Animal Limiter");
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
		if (response.RequestId >= lastResponseId)
		{
			lastResponseId = response.RequestId;
			webEnabled = response.Enabled;
			webRunning = response.Running;
			webBindAddress = (string.IsNullOrWhiteSpace(response.BindAddress) ? "127.0.0.1" : response.BindAddress);
			webPort = ((response.Port > 0) ? response.Port : 9465);
			webLocalUrl = (string.IsNullOrWhiteSpace(response.LocalUrl) ? $"http://127.0.0.1:{webPort}/" : response.LocalUrl);
			webPublicUrl = response.PublicUrl ?? "";
			webAdminUsername = (string.IsNullOrWhiteSpace(response.AdminUsername) ? "admin" : response.AdminUsername);
			webPasswordConfigured = response.PasswordConfigured;
			webActiveSessions = response.ActiveSessions;
			webSessionTimeoutMinutes = ((response.SessionTimeoutMinutes > 0) ? response.SessionTimeoutMinutes : 60);
			webSecureCookies = response.SecureCookies;
			webLocalAnonymousMode = response.LocalAnonymousMode;
			webLastError = response.LastError ?? "";
			if (response.WebMapSettings != null)
			{
				CopyWebMapSettings(response.WebMapSettings);
			}
			else if (webView == "webmap" && !response.Success)
			{
				webMapLastError = response.Message ?? "Web Map is unavailable.";
			}
			if (!(section != "webserver"))
			{
				ref string reference = ref pageHeaderText;
				string text;
				if (webView == "webmap")
				{
					text = (response.Success ? Escape(response.Message) : SuiteLocalization.Get("gui.runtime.webmapconfig"));
				}
				else
				{
					text = SuiteLocalization.Get("gui.runtime.webpanelconfig");
				}
				reference = text;
				ComposeDialog();
				ApplyWebControlValues();
				ApplyWebMapControlValues();
			}
		}
	}

	public void Show(DiscordIntegrationGuiResponse response)
	{
		if (response.RequestId < lastDiscordResponseId)
		{
			return;
		}
		lastDiscordResponseId = response.RequestId;
		discordSettings = response.Settings ?? new DiscordIntegrationGuiSettingsPacket();
		discordAvailable = discordSettings.TransportAvailable || discordSettings.LinkAvailable;
		ref string reference = ref discordStatusMessage;
		string text;
		if (string.IsNullOrWhiteSpace(response.Message))
		{
			text = (discordAvailable ? "Discord integration status loaded." : "The unified Discord module is not installed.");
		}
		else
		{
			text = response.Message;
		}
		reference = text;
		motdAvailable = discordSettings.MotdAvailable;
		motdVersion = discordSettings.MotdVersion ?? "";
		ref string reference2 = ref motdStatusMessage;
		string text2;
		if (string.IsNullOrWhiteSpace(discordSettings.MotdMessage))
		{
			text2 = (motdAvailable ? "Community MOTD companion is connected." : "Community MOTD companion is not installed.");
		}
		else
		{
			text2 = discordSettings.MotdMessage;
		}
		reference2 = text2;
		try
		{
			motdConfig = JObject.Parse(string.IsNullOrWhiteSpace(discordSettings.MotdConfigJson) ? "{}" : discordSettings.MotdConfigJson);
		}
		catch
		{
			motdConfig = new JObject();
			motdStatusMessage = "Community MOTD returned an invalid configuration payload.";
		}
		if (!(section != "webserver"))
		{
			if (webView == "discord")
			{
				pageHeaderText = "Discord Integration";
			}
			ComposeDialog();
			ApplyWebControlValues();
			ApplyWebMapControlValues();
			ApplyDiscordControlValues();
		}
	}

	public void Show(AntiCheatGuiResponse response)
	{
		if (response.RequestId < lastResponseId)
		{
			return;
		}
		lastResponseId = response.RequestId;
		if (!string.IsNullOrWhiteSpace(response.SelectedPlayer))
		{
			antiCheatPlayerSearch = response.SelectedPlayer;
		}
		antiCheatEnabled = response.Enabled;
		if (response.Settings != null)
		{
			CopyAntiCheatSettings(response.Settings);
		}
		if (response.PacketSettings != null)
		{
			CopyAntiCheatPacketProtectionSettings(response.PacketSettings);
		}
		string text = (response.Operation ?? "").Trim().ToLowerInvariant();
		if (text == "enforcement")
		{
			CopyAntiCheatEnforcementRules(response.ConsequenceRules);
		}
		if (section != "anticheat" || (text == "blacklistresolve" && antiCheatView != "blacklist"))
		{
			return;
		}
		if (1 == 0)
		{
		}
		string text2;
		switch (text)
		{
		case "overview":
			text2 = "overview";
			break;
		case "players":
			text2 = "players";
			break;
		case "player":
			text2 = "player";
			break;
		case "reports":
			text2 = "reports";
			break;
		case "blacklist":
		case "blacklistresolve":
			text2 = "blacklist";
			break;
		case "enforcement":
			text2 = "enforcement";
			break;
		case "settings":
			text2 = "settings";
			break;
		case "packet":
			text2 = "packet";
			break;
		default:
			text2 = antiCheatView;
			break;
		}
		if (1 == 0)
		{
		}
		antiCheatView = text2;
		pageHeaderText = (response.Success ? Escape(response.Message) : "Anti-Cheat action failed");
		if (response.Success)
		{
			if (text == "player")
			{
				SetAntiCheatPlayerResults(response.Lines);
			}
			else
			{
				SetGenericResults(response.Lines);
			}
		}
		else
		{
			if (text == "player")
			{
				antiCheatPlayerSummary.Clear();
			}
			List<string> list = new List<string> { "Error: " + response.Message };
			if (response.Lines != null && response.Lines.Count > 0)
			{
				list.AddRange(response.Lines);
			}
			SetGenericResults(list);
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
		if (response.RequestId < lastResponseId)
		{
			return;
		}
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
		if (!(section != "loginprotection"))
		{
			pageHeaderText = (response.Success ? Escape(response.Message) : "Login Protection");
			if (loginView == "status")
			{
				SetGenericResults(response.Success ? response.Lines : new List<string> { response.Message });
			}
			ComposeDialog();
			ApplyLoginProtectionControlValues();
		}
	}

	public void Show(ProfilerResponse response)
	{
		if (response.RequestId < lastResponseId)
		{
			return;
		}
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
			if (section == "profiler" && IsOpened())
			{
				UpdateProfilerLiveElements();
			}
			return;
		}
		if (response.Success || response.ProfileRunning)
		{
			profilerSamples.Clear();
			if (response.Samples != null)
			{
				profilerSamples.AddRange(response.Samples);
			}
			profilerSlowSpikes.Clear();
			if (response.SlowSpikes != null)
			{
				profilerSlowSpikes.AddRange(response.SlowSpikes);
			}
			profilerHotspots.Clear();
			if (response.Hotspots != null)
			{
				profilerHotspots.AddRange(response.Hotspots);
			}
			profilerModHotspots.Clear();
			if (response.ModHotspots != null)
			{
				profilerModHotspots.AddRange(response.ModHotspots);
			}
			profilerPhases.Clear();
			if (response.Phases != null)
			{
				profilerPhases.AddRange(response.Phases);
			}
			profilerProfileSlowTicks.Clear();
			if (response.ProfileSlowTicks != null)
			{
				profilerProfileSlowTicks.AddRange(response.ProfileSlowTicks);
			}
			profilerOffThreadReports.Clear();
			if (response.OffThreadReports != null)
			{
				profilerOffThreadReports.AddRange(response.OffThreadReports);
			}
			profilerDiagnosticIncidents.Clear();
			if (response.DiagnosticIncidents != null)
			{
				profilerDiagnosticIncidents.AddRange(response.DiagnosticIncidents);
			}
			profilerDiagnosticSummary = response.DiagnosticSummary ?? new ProfilerDiagnosticSummaryPacket();
			profilerLoadedModCount = response.LoadedModCount;
			if (!string.IsNullOrWhiteSpace(response.LocalWebUrl))
			{
				webLocalUrl = response.LocalWebUrl;
			}
			webPublicUrl = response.PublicWebUrl ?? "";
			profilerCurrent = response.Current ?? new ProfilerSamplePacket();
			profilerLastProfile = response.LastProfile ?? new ProfilerProfileSummaryPacket();
			profilerRunning = response.ProfileRunning;
			profilerRemainingSeconds = response.ProfileRemainingSeconds;
		}
		ref string reference = ref profilerMessage;
		string text;
		if (string.IsNullOrWhiteSpace(response.Message))
		{
			text = (response.Success ? SuiteLocalization.Get("gui.runtime.profilerdiagnostics") : SuiteLocalization.Get("gui.runtime.profilerfailed"));
		}
		else
		{
			text = response.Message;
		}
		reference = text;
		if (!(section != "profiler"))
		{
			pageHeaderText = SuiteLocalization.Get("gui.runtime.profilerdiagnostics");
			ComposeDialog();
		}
	}

	public void Show(AdminCommandGuiResponse response)
	{
		if (response.RequestId < lastResponseId)
		{
			return;
		}
		lastResponseId = response.RequestId;
		if (section != "admincommands")
		{
			return;
		}
		pageHeaderText = (response.Success ? SuiteLocalization.Get("gui.runtime.admincompleted") : SuiteLocalization.Get("gui.runtime.adminfailed"));
		List<string> list = new List<string> { "ADMINSTATUS\t" + (response.Success ? "OK" : "ERROR") + "\t" + response.Message };
		if (response.Lines != null)
		{
			RefreshSelectedAdminInventoryState(response.Lines);
			foreach (string line in response.Lines)
			{
				if (line.StartsWith("AUTORESTART\t", StringComparison.Ordinal))
				{
					ApplyAutoRestartStatusLine(line);
				}
				else if (line.StartsWith("CHATDISPLAY\t", StringComparison.Ordinal))
				{
					ApplyChatDisplayStatusLine(line);
				}
				else if (line.StartsWith("INV\t", StringComparison.Ordinal) || line.StartsWith("ITEM\t", StringComparison.Ordinal) || line.StartsWith("ADMINPLAYER\t", StringComparison.Ordinal))
				{
					list.Add(line);
				}
				else
				{
					list.Add("ADMINLINE\t" + line);
				}
			}
		}
		SetGenericResults(list);
		ComposeDialog();
	}

	private void ComposeDialog()
	{
		adminPlayerChoices = AdminPlayerChoices.Create((capi.World.AllOnlinePlayers ?? Array.Empty<IPlayer>()).Where(p => p != null).Select(p => (p.PlayerUID, p.PlayerName)));
		if (disposed || !IsOpened())
		{
			return;
		}
		RememberWorkspaceNavigation();
		ClearComposers();
		if ((section != "animals" || animalView != "rules") && genericResultLines.Count == 0 && !string.IsNullOrWhiteSpace(resultText))
		{
			SetGenericResults(new List<string> { WebUtility.HtmlDecode(resultText) });
		}
		ElementBounds bounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
		string text = section;
		bool flag = ((text == "home" || text == "blocklogger") ? true : false);
		double num = ((flag || (section == "anticheat" && antiCheatView == "settings")) ? 1060 : ((section == "loginprotection") ? 1100 : ((section == "animals") ? 1240 : 1360)));
		double num2 = ((section == "anticheat" && antiCheatView != "settings") ? 840 : 760);
		double fixedWidth = num - 32.0;
		double num3 = num - 56.0;
		ElementBounds bounds2 = ElementBounds.Fixed(0.0, 0.0, num, num2);
		GuiComposer guiComposer = capi.Gui.CreateCompo("blocklogger-investigator", bounds).AddStaticElement(new SuitePanelElement(capi, bounds2, section));
		BuildSuiteNavigation(guiComposer, num);
		double num4 = 0.0;
		double val = 0.0;
		bool flag2 = true;
		if (section == "home")
		{
			flag2 = false;
			BuildSuiteHome(guiComposer);
		}
		else if (section == "blocklogger")
		{
			guiComposer.AddSuiteButton(SuiteLocalization.Get("gui.blocks"), () => SelectView("blocks"), ElementBounds.Fixed(22.0, 82.0, 90.0, 30.0), view == "blocks").AddHoverText(SuiteLocalization.Get("gui.tip.more.0530e3ff67"), CairoFont.WhiteSmallText(), 320, ElementBounds.Fixed(22.0, 82.0, 90.0, 30.0), "tip-blocklogger-tab-blocks").AddSuiteButton(SuiteLocalization.Get("gui.containers"), () => SelectView("containers"), ElementBounds.Fixed(124.0, 82.0, 130.0, 30.0), view == "containers")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.3aea1a9f96"), CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(124.0, 82.0, 130.0, 30.0), "tip-blocklogger-tab-containers")
				.AddSuiteButton(SuiteLocalization.Get("gui.entities"), () => SelectView("entities"), ElementBounds.Fixed(266.0, 82.0, 100.0, 30.0), view == "entities")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.af9320bb26"), CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(266.0, 82.0, 100.0, 30.0), "tip-blocklogger-tab-entities")
				.AddSuiteButton(SuiteLocalization.Get("gui.players"), () => SelectView("players"), ElementBounds.Fixed(378.0, 82.0, 100.0, 30.0), view == "players")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.9946631405"), CairoFont.WhiteSmallText(), 370, ElementBounds.Fixed(378.0, 82.0, 100.0, 30.0), "tip-blocklogger-tab-players")
				.AddSuiteButton(SuiteLocalization.Get("gui.status"), SelectStatus, ElementBounds.Fixed(490.0, 82.0, 90.0, 30.0), view == "status")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.9a17c03715"), CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(490.0, 82.0, 90.0, 30.0), "tip-blocklogger-tab-status")
				.AddSuiteButton(SuiteLocalization.Get("gui.settings"), SelectBlockLoggerSettings, ElementBounds.Fixed(592.0, 82.0, 110.0, 30.0), view == "settings")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.1b4a801586"), CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(592.0, 82.0, 110.0, 30.0), "tip-blocklogger-tab-settings");
			if (view == "settings")
			{
				flag2 = false;
				BuildBlockLoggerSettings(guiComposer);
			}
			else if (view == "status")
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 618.0)).AddRichtext("<strong>" + pageHeaderText + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 136.0, 980.0, 26.0), "page-header");
				num4 = 170.0;
				val = 550.0;
			}
			else if (view == "players")
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 142.0)).AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 138.0, 58.0, 24.0)).AddTextInput(ElementBounds.Fixed(92.0, 133.0, 330.0, 30.0), (string text10) =>
				{
					playerSearch = text10 ?? "";
				}, CairoFont.WhiteSmallText(), "player-search")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.bbc09c39b1"), CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(92.0, 133.0, 330.0, 30.0), "tip-player-search")
					.AddRichtext(SuiteLocalization.Get("gui.rowslabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(446.0, 138.0, 48.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(494.0, 133.0, 72.0, 30.0), OnLimitChanged, CairoFont.WhiteSmallText(), "limit")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.0d9bb2527a"), CairoFont.WhiteSmallText(), 300, ElementBounds.Fixed(494.0, 133.0, 72.0, 30.0), "tip-player-rows")
					.AddRichtext(SuiteLocalization.Get("gui.activitylabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 179.0, 66.0, 24.0))
					.AddDropDown(new string[4] { "any", "blocks", "containers", "entities" }, new string[4] { "Any", "Blocks", "Containers", "Entities" }, PlayerActionIndex(), OnActionChanged, ElementBounds.Fixed(100.0, 174.0, 180.0, 30.0), CairoFont.WhiteSmallText(), "action")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.79dee41707"), CairoFont.WhiteSmallText(), 330, ElementBounds.Fixed(100.0, 174.0, 180.0, 30.0), "tip-player-activity")
					.AddSuiteButton(SuiteLocalization.Get("gui.search"), Search, ElementBounds.Fixed(300.0, 174.0, 120.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.fc8ce888e6"), CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(300.0, 174.0, 120.0, 30.0), "tip-player-search-button")
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), Refresh, ElementBounds.Fixed(434.0, 174.0, 120.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.9e397c52be"), CairoFont.WhiteSmallText(), 300, ElementBounds.Fixed(434.0, 174.0, 120.0, 30.0), "tip-player-refresh")
					.AddRichtext(SuiteLocalization.Get("gui.playerhistoryhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 223.0, 760.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(16.0, 276.0, fixedWidth, 394.0))
					.AddRichtext("<strong>" + pageHeaderText + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 290.0, 980.0, 26.0), "page-header");
				num4 = 324.0;
				val = 396.0;
			}
			else
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 142.0)).AddRichtext(SuiteLocalization.Get("gui.targetlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 138.0, 58.0, 24.0)).AddDropDown(new string[2] { "selection", "current" }, new string[2] { "Looked at", "Current pos" }, (!useSelection) ? 1 : 0, OnTargetChanged, ElementBounds.Fixed(92.0, 133.0, 145.0, 30.0), CairoFont.WhiteSmallText(), "target")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.b959e8adec"), CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(92.0, 133.0, 145.0, 30.0), "tip-history-target")
					.AddRichtext(SuiteLocalization.Get("gui.radiuslabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(258.0, 138.0, 58.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(314.0, 133.0, 70.0, 30.0), OnRadiusChanged, CairoFont.WhiteSmallText(), "radius")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.19f29fbee6"), CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(314.0, 133.0, 70.0, 30.0), "tip-history-radius")
					.AddRichtext(SuiteLocalization.Get("gui.rowslabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(406.0, 138.0, 48.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(452.0, 133.0, 70.0, 30.0), OnLimitChanged, CairoFont.WhiteSmallText(), "limit")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.0d9bb2527a"), CairoFont.WhiteSmallText(), 300, ElementBounds.Fixed(452.0, 133.0, 70.0, 30.0), "tip-history-rows")
					.AddRichtext(SuiteLocalization.Get("gui.actionlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 179.0, 58.0, 24.0))
					.AddDropDown(ActionValues(), ActionNames(), ActionIndex(), OnActionChanged, ElementBounds.Fixed(92.0, 174.0, 170.0, 30.0), CairoFont.WhiteSmallText(), "action")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.d08509d9bf"), CairoFont.WhiteSmallText(), 310, ElementBounds.Fixed(92.0, 174.0, 170.0, 30.0), "tip-history-action")
					.AddRichtext(SuiteLocalization.Get("gui.searchlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(286.0, 179.0, 58.0, 24.0))
					.AddTextInput(ElementBounds.Fixed(342.0, 174.0, 280.0, 30.0), (string text10) =>
					{
						search = text10 ?? "";
					}, CairoFont.WhiteSmallText(), "search")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.ff60708cd5"), CairoFont.WhiteSmallText(), 380, ElementBounds.Fixed(342.0, 174.0, 280.0, 30.0), "tip-history-search")
					.AddSuiteButton(SuiteLocalization.Get("gui.search"), Search, ElementBounds.Fixed(638.0, 174.0, 100.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.38c24a07a0"), CairoFont.WhiteSmallText(), 350, ElementBounds.Fixed(638.0, 174.0, 100.0, 30.0), "tip-history-search-button")
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), Refresh, ElementBounds.Fixed(752.0, 174.0, 100.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.96a78eac6e"), CairoFont.WhiteSmallText(), 300, ElementBounds.Fixed(752.0, 174.0, 100.0, 30.0), "tip-history-refresh")
					.AddRichtext(SuiteLocalization.Get("gui.searchoptionalhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 223.0, 650.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(16.0, 276.0, fixedWidth, 394.0))
					.AddRichtext("<strong>" + pageHeaderText + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 290.0, 980.0, 26.0), "page-header");
				num4 = 324.0;
				val = 396.0;
			}
		}
		else if (section == "admincommands")
		{
			ElementBounds elementBounds = ElementBounds.Fixed(22.0, 82.0, 100.0, 30.0);
			ElementBounds elementBounds2 = ElementBounds.Fixed(134.0, 82.0, 126.0, 30.0);
			ElementBounds elementBounds3 = ElementBounds.Fixed(272.0, 82.0, 102.0, 30.0);
			ElementBounds elementBounds4 = ElementBounds.Fixed(386.0, 82.0, 106.0, 30.0);
			ElementBounds elementBounds5 = ElementBounds.Fixed(504.0, 82.0, 92.0, 30.0);
			ElementBounds elementBounds6 = ElementBounds.Fixed(608.0, 82.0, 96.0, 30.0);
			guiComposer.AddSuiteButton(SuiteLocalization.Get("gui.players"), () => SelectAdminCommandView("players"), elementBounds, adminCommandView == "players").AddHoverText(SuiteLocalization.Get("gui.tip.more.d06a61d8e6"), CairoFont.WhiteSmallText(), 390, elementBounds.FlatCopy(), "tip-admincmd-players").AddSuiteButton(SuiteLocalization.Get("gui.moderation"), () => SelectAdminCommandView("moderation"), elementBounds2, adminCommandView == "moderation")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.96db369cb3"), CairoFont.WhiteSmallText(), 390, elementBounds2.FlatCopy(), "tip-admincmd-moderation")
				.AddSuiteButton(SuiteLocalization.Get("gui.teleport"), () => SelectAdminCommandView("teleport"), elementBounds3, adminCommandView == "teleport")
				.AddHoverText(SuiteLocalization.Get("gui.teleporttabhelp"), CairoFont.WhiteSmallText(), 390, elementBounds3.FlatCopy(), "tip-admincmd-teleport")
				.AddSuiteButton(SuiteLocalization.Get("gui.inventory"), () => SelectAdminCommandView("inventory"), elementBounds4, adminCommandView == "inventory")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.d56abac13b"), CairoFont.WhiteSmallText(), 400, elementBounds4.FlatCopy(), "tip-admincmd-inventory")
				.AddSuiteButton(SuiteLocalization.Get("gui.server"), () => SelectAdminCommandView("server"), elementBounds5, adminCommandView == "server")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.cf7edc12b6"), CairoFont.WhiteSmallText(), 400, elementBounds5.FlatCopy(), "tip-admincmd-server")
				.AddSuiteButton(SuiteLocalization.Get("gui.reports"), () => SelectAdminCommandView("reports"), elementBounds6, adminCommandView == "reports")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.64bc59063d"), CairoFont.WhiteSmallText(), 360, elementBounds6.FlatCopy(), "tip-admincmd-reports");
			if (adminCommandView == "players")
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 176.0)).AddRichtext(SuiteLocalization.Get("gui.playeradministration"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 134.0, 330.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 170.0, 100.0, 24.0))
					.AddDropDown(AdminTeleportPlayerValues(), AdminTeleportPlayerNames(), AdminTeleportPlayerIndex(), OnAdminTeleportPlayerChanged, ElementBounds.Fixed(142.0, 164.0, 220.0, 30.0), CairoFont.WhiteSmallText(), "admincmd-player-picker")
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RefreshAdminTeleportPlayers, ElementBounds.Fixed(372.0, 164.0, 84.0, 30.0))
					.AddTextInput(ElementBounds.Fixed(468.0, 164.0, 190.0, 30.0), (string v) =>
					{
						adminCommandTarget = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-target")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.40da68424b"), CairoFont.WhiteSmallText(), 380, ElementBounds.Fixed(468.0, 164.0, 190.0, 30.0), "tip-admincmd-player-fallback")
					.AddSuiteButton(SuiteLocalization.Get("gui.playerinfo"), () => RunAdminCommand("player-info"), ElementBounds.Fixed(670.0, 164.0, 118.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.seen"), () => RunAdminCommand("seen"), ElementBounds.Fixed(800.0, 164.0, 80.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.playtime"), () => RunAdminCommand("playtime"), ElementBounds.Fixed(890.0, 164.0, 92.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.afk"), () => RunAdminCommand("afk"), ElementBounds.Fixed(992.0, 164.0, 72.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.history"), () => RunAdminCommand("history"), ElementBounds.Fixed(1074.0, 164.0, 88.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.warnings"), () => RunAdminCommand("warnings"), ElementBounds.Fixed(1172.0, 164.0, 108.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.notes"), () => RunAdminCommand("notes"), ElementBounds.Fixed(38.0, 208.0, 96.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.heal"), () => RunAdminCommand("heal"), ElementBounds.Fixed(146.0, 208.0, 90.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.feed"), () => RunAdminCommand("feed"), ElementBounds.Fixed(248.0, 208.0, 90.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.revive"), () => RunAdminCommand("revive"), ElementBounds.Fixed(350.0, 208.0, 96.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.ghost"), () => RunAdminCommand("ghost"), ElementBounds.Fixed(458.0, 208.0, 96.0, 30.0))
					.AddRichtext(SuiteLocalization.Get("gui.playeradminpermissions"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(566.0, 213.0, 690.0, 24.0))
					.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 312.0, 980.0, 26.0), "page-header");
				num4 = 346.0;
				val = 374.0;
			}
			else if (adminCommandView == "moderation")
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 258.0)).AddRichtext(SuiteLocalization.Get("gui.moderationtitle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 134.0, 240.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 170.0, 100.0, 24.0))
					.AddDropDown(AdminTeleportPlayerValues(), AdminTeleportPlayerNames(), AdminTeleportPlayerIndex(), OnAdminTeleportPlayerChanged, ElementBounds.Fixed(142.0, 164.0, 200.0, 30.0), CairoFont.WhiteSmallText(), "admincmd-player-picker")
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RefreshAdminTeleportPlayers, ElementBounds.Fixed(352.0, 164.0, 84.0, 30.0))
					.AddTextInput(ElementBounds.Fixed(446.0, 164.0, 170.0, 30.0), (string v) =>
					{
						adminCommandTarget = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-target")
					.AddRichtext(SuiteLocalization.Get("gui.durationlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(628.0, 170.0, 72.0, 24.0))
					.AddTextInput(ElementBounds.Fixed(704.0, 164.0, 100.0, 30.0), (string v) =>
					{
						adminCommandDuration = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-duration")
					.AddRichtext(SuiteLocalization.Get("gui.recordid"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(816.0, 170.0, 78.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(898.0, 164.0, 82.0, 30.0), OnAdminRecordIdChanged, CairoFont.WhiteSmallText(), "admincmd-record-id")
					.AddRichtext(SuiteLocalization.Get("gui.reasonnote"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 209.0, 108.0, 24.0))
					.AddTextInput(ElementBounds.Fixed(150.0, 203.0, 666.0, 30.0), (string v) =>
					{
						adminCommandReason = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-reason")
					.AddSuiteButton(SuiteLocalization.Get("gui.warn"), () => RunAdminCommand("warn"), ElementBounds.Fixed(838.0, 203.0, 84.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.removewarn"), () => RunAdminCommand("unwarn"), ElementBounds.Fixed(934.0, 203.0, 120.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.mute"), () => RunAdminCommand("mute"), ElementBounds.Fixed(1066.0, 203.0, 82.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.unmute"), () => RunAdminCommand("unmute"), ElementBounds.Fixed(1160.0, 203.0, 92.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.muteinfo"), () => RunAdminCommand("muteinfo"), ElementBounds.Fixed(838.0, 242.0, 104.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.tempban"), () => RunAdminCommand("tempban"), ElementBounds.Fixed(954.0, 242.0, 104.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.baninfo"), () => RunAdminCommand("baninfo"), ElementBounds.Fixed(1070.0, 242.0, 96.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.unban"), () => RunAdminCommand("unban"), ElementBounds.Fixed(1178.0, 242.0, 82.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.freeze"), () => RunAdminCommand("freeze"), ElementBounds.Fixed(38.0, 282.0, 94.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.unfreeze"), () => RunAdminCommand("unfreeze"), ElementBounds.Fixed(144.0, 282.0, 102.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.kick"), () => RunAdminCommand("kick", dangerous: true), ElementBounds.Fixed(258.0, 282.0, 86.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.addnote"), () => RunAdminCommand("note-add"), ElementBounds.Fixed(356.0, 282.0, 100.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.removenote"), () => RunAdminCommand("note-remove"), ElementBounds.Fixed(468.0, 282.0, 116.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.smite"), () => RunAdminCommand("smite"), ElementBounds.Fixed(596.0, 282.0, 90.0, 30.0))
					.AddRichtext(SuiteLocalization.Get("gui.moderationdurationhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(700.0, 288.0, 556.0, 24.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.jail"), () => RunAdminCommand("jail", dangerous: true), ElementBounds.Fixed(38.0, 322.0, 90.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.unjail"), () => RunAdminCommand("unjail", dangerous: true), ElementBounds.Fixed(140.0, 322.0, 96.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.jailinfo"), () => RunAdminCommand("jailinfo"), ElementBounds.Fixed(248.0, 322.0, 104.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.setjail"), () => RunAdminCommand("setjail", dangerous: true), ElementBounds.Fixed(364.0, 322.0, 104.0, 30.0))
					.AddRichtext(SuiteLocalization.Get("gui.jailhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(482.0, 328.0, 770.0, 24.0))
					.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 392.0, 980.0, 26.0), "page-header");
				num4 = 426.0;
				val = 294.0;
			}
			else if (adminCommandView == "teleport")
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 178.0)).AddRichtext(SuiteLocalization.Get("gui.teleporthelpers"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 134.0, 280.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 174.0, 100.0, 24.0))
					.AddDropDown(AdminTeleportPlayerValues(), AdminTeleportPlayerNames(), AdminTeleportPlayerIndex(), OnAdminTeleportPlayerChanged, ElementBounds.Fixed(142.0, 168.0, 250.0, 30.0), CairoFont.WhiteSmallText(), "admincmd-teleport-player")
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RefreshAdminTeleportPlayers, ElementBounds.Fixed(404.0, 168.0, 88.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.gotoplayer"), () => RunAdminCommand("goto"), ElementBounds.Fixed(506.0, 168.0, 118.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.bringhere"), () => RunAdminCommand("bring"), ElementBounds.Fixed(636.0, 168.0, 118.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.sendback"), () => RunAdminCommand("sendback"), ElementBounds.Fixed(766.0, 168.0, 118.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.myback"), () => RunAdminCommand("back"), ElementBounds.Fixed(896.0, 168.0, 104.0, 30.0))
					.AddRichtext(SuiteLocalization.Get("gui.mapteleporthelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 216.0, 920.0, 20.0))
					.AddRichtext(SuiteLocalization.Get("gui.teleportsafetyhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 242.0, 900.0, 20.0))
					.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 312.0, 980.0, 26.0), "page-header");
				num4 = 346.0;
				val = 374.0;
			}
			else if (adminCommandView == "inventory")
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 324.0)).AddRichtext(SuiteLocalization.Get("gui.inventorymanager"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 134.0, 360.0, 24.0)).AddStaticText(SuiteLocalization.Get("gui.inventoryeditorhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(410.0, 136.0, 780.0, 22.0))
					.AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 170.0, 100.0, 24.0))
					.AddDropDown(AdminTeleportPlayerValues(), AdminTeleportPlayerNames(), AdminTeleportPlayerIndex(), OnAdminTeleportPlayerChanged, ElementBounds.Fixed(142.0, 164.0, 200.0, 30.0), CairoFont.WhiteSmallText(), "admincmd-player-picker")
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RefreshAdminTeleportPlayers, ElementBounds.Fixed(352.0, 164.0, 84.0, 30.0))
					.AddTextInput(ElementBounds.Fixed(446.0, 164.0, 160.0, 30.0), (string v) =>
					{
						adminCommandTarget = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-target")
					.AddSuiteButton(SuiteLocalization.Get("gui.loadinventory"), () => RunAdminCommand("invsee"), ElementBounds.Fixed(618.0, 164.0, 126.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.snapshot"), () => RunAdminCommand("invsnapshot"), ElementBounds.Fixed(756.0, 164.0, 104.0, 30.0))
					.AddRichtext("<font color=\"#91a0ae\"><strong>" + Escape(SuiteLocalization.Get("gui.runtime.selectedslot", adminCommandInventoryId, adminCommandSlot, adminCommandQuantity)) + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(876.0, 170.0, 380.0, 24.0))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(34.0, 204.0, 1248.0, 88.0)))
					.AddStaticText(SuiteLocalization.Get("gui.itemfinder"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 214.0, 110.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.search"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 247.0, 54.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(108.0, 241.0, 250.0, 28.0), (string v) =>
					{
						adminCommandItemSearch = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-item-search")
					.AddSuiteButton(SuiteLocalization.Get("gui.searchitems"), () => RunAdminCommand("itemsearch"), ElementBounds.Fixed(370.0, 241.0, 112.0, 28.0))
					.AddStaticText(SuiteLocalization.Get("gui.itemcode"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(504.0, 247.0, 72.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(580.0, 241.0, 330.0, 28.0), (string v) =>
					{
						adminCommandItemCode = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-item-code")
					.AddStaticText(SuiteLocalization.Get("gui.quantity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(928.0, 247.0, 34.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(966.0, 241.0, 70.0, 28.0), OnAdminQuantityChanged, CairoFont.WhiteSmallText(), "admincmd-qty")
					.AddSuiteButton(SuiteLocalization.Get("gui.giveitem"), () => RunAdminCommand("invgive"), ElementBounds.Fixed(1050.0, 241.0, 104.0, 28.0))
					.AddRichtext(SuiteLocalization.Get("gui.itemsearchhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 273.0, 1190.0, 24.0))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(34.0, 303.0, 1248.0, 76.0)))
					.AddStaticText(SuiteLocalization.Get("gui.selectedslot"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 313.0, 120.0, 22.0))
					.AddRichtext(SuiteLocalization.Get("gui.inventorybackuphelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(186.0, 311.0, 1056.0, 28.0))
					.AddStaticText(SuiteLocalization.Get("gui.inventory"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 346.0, 68.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(124.0, 340.0, 190.0, 28.0), (string v) =>
					{
						adminCommandInventoryId = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-inventory-id")
					.AddStaticText(SuiteLocalization.Get("gui.slot"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(330.0, 346.0, 36.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(370.0, 340.0, 66.0, 28.0), OnAdminSlotChanged, CairoFont.WhiteSmallText(), "admincmd-slot")
					.AddSuiteButton(SuiteLocalization.Get("gui.setqty"), () => RunAdminCommand("invqty"), ElementBounds.Fixed(452.0, 340.0, 94.0, 28.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.replaceslot"), () => RunAdminCommand("invreplace", dangerous: true), ElementBounds.Fixed(558.0, 340.0, 114.0, 28.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.clearslot"), () => RunAdminCommand("invremove", dangerous: true), ElementBounds.Fixed(684.0, 340.0, 104.0, 28.0))
					.AddStaticText(SuiteLocalization.Get("gui.snapshotid"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 398.0, 82.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(124.0, 392.0, 90.0, 30.0), OnAdminRecordIdChanged, CairoFont.WhiteSmallText(), "admincmd-record-id")
					.AddStaticText(SuiteLocalization.Get("gui.reason"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(230.0, 398.0, 68.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(302.0, 392.0, 334.0, 30.0), (string v) =>
					{
						adminCommandReason = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-reason")
					.AddSuiteButton(SuiteLocalization.Get("gui.restoresnapshot"), () => RunAdminCommand("invrestore", dangerous: true), ElementBounds.Fixed(650.0, 392.0, 148.0, 30.0))
					.AddRichtext(SuiteLocalization.Get("gui.inventoryslothelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(820.0, 389.0, 430.0, 42.0))
					.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 458.0, 1100.0, 26.0), "page-header");
				num4 = 492.0;
				val = 320.0;
			}
			else if (adminCommandView == "server")
			{
				guiComposer.AddSuiteButton(SuiteLocalization.Get("gui.serveroperationstab"), () => SelectAdminServerView("operations"), ElementBounds.Fixed(34.0, 122.0, 200.0, 30.0), adminServerView == "operations").AddSuiteButton(SuiteLocalization.Get("gui.serverrestartstab"), () => SelectAdminServerView("restarts"), ElementBounds.Fixed(246.0, 122.0, 240.0, 30.0), adminServerView == "restarts").AddSuiteButton(SuiteLocalization.Get("gui.serverchattab"), () => SelectAdminServerView("chat"), ElementBounds.Fixed(498.0, 122.0, 200.0, 30.0), adminServerView == "chat")
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(34.0, 166.0, 1248.0, 220.0)));
				if (adminServerView == "restarts")
				{
					guiComposer.AddRichtext(SuiteLocalization.Get("gui.automatedrestartstitle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 176.0, 320.0, 24.0)).AddStaticText(SuiteLocalization.Get("gui.restarttimes"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 212.0, 350.0, 22.0)).AddTextInput(ElementBounds.Fixed(52.0, 234.0, 350.0, 28.0), (string v) =>
					{
						adminAutoRestartTimes = v ?? "";
					}, CairoFont.WhiteSmallText(), "autorestart-times")
						.AddStaticText(SuiteLocalization.Get("gui.restartwarnings"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(424.0, 212.0, 320.0, 22.0))
						.AddTextInput(ElementBounds.Fixed(424.0, 234.0, 320.0, 28.0), (string v) =>
						{
							adminAutoRestartWarnings = v ?? "";
						}, CairoFont.WhiteSmallText(), "autorestart-warnings")
						.AddStaticText(SuiteLocalization.Get("gui.reason"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(766.0, 212.0, 496.0, 22.0))
						.AddTextInput(ElementBounds.Fixed(766.0, 234.0, 496.0, 28.0), (string v) =>
						{
							adminAutoRestartReason = v ?? "";
						}, CairoFont.WhiteSmallText(), "autorestart-reason")
						.AddSwitch((bool v) =>
						{
							adminAutoRestartGameAnnouncements = v;
						}, ElementBounds.Fixed(52.0, 278.0, 52.0, 26.0), "autorestart-game")
						.AddStaticText(SuiteLocalization.Get("gui.gamewarnings"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(110.0, 282.0, 200.0, 22.0))
						.AddSwitch((bool v) =>
						{
							adminAutoRestartDiscordAnnouncements = v;
						}, ElementBounds.Fixed(322.0, 278.0, 52.0, 26.0), "autorestart-discord")
						.AddStaticText(SuiteLocalization.Get("gui.discordwarnings"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(380.0, 282.0, 220.0, 22.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.status"), () => RunAdminCommand("autorestart-status"), ElementBounds.Fixed(620.0, 276.0, 84.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => RunAdminCommand("autorestart-save"), ElementBounds.Fixed(714.0, 276.0, 82.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.enable"), () => RunAdminCommand("autorestart-enable"), ElementBounds.Fixed(806.0, 276.0, 86.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.disable"), () => RunAdminCommand("autorestart-disable"), ElementBounds.Fixed(902.0, 276.0, 86.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.skipnext"), () => RunAdminCommand("autorestart-skip", dangerous: true), ElementBounds.Fixed(998.0, 276.0, 106.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.reload"), () => RunAdminCommand("autorestart-reload"), ElementBounds.Fixed(1114.0, 276.0, 88.0, 30.0))
						.AddStaticText(SuiteLocalization.Get("gui.restartrelaunchhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 330.0, 1160.0, 40.0));
				}
				else if (adminServerView == "chat")
				{
					guiComposer.AddRichtext(SuiteLocalization.Get("gui.serverchattab"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 178.0, 300.0, 24.0)).AddSwitch((bool v) =>
					{
						adminChatShowRole = v;
					}, ElementBounds.Fixed(52.0, 214.0, 52.0, 26.0), "chatdisplay-role").AddStaticText("Role before name", CairoFont.WhiteSmallText(), ElementBounds.Fixed(110.0, 218.0, 126.0, 22.0))
						.AddSwitch((bool v) =>
						{
							adminChatBoldName = v;
						}, ElementBounds.Fixed(400.0, 214.0, 52.0, 26.0), "chatdisplay-bold")
						.AddStaticText("Bold player name", CairoFont.WhiteSmallText(), ElementBounds.Fixed(458.0, 218.0, 126.0, 22.0))
						.AddSwitch((bool v) =>
						{
							adminChatShowTimestamp = v;
						}, ElementBounds.Fixed(596.0, 214.0, 52.0, 26.0), "chatdisplay-time")
						.AddStaticText("Timestamp", CairoFont.WhiteSmallText(), ElementBounds.Fixed(654.0, 218.0, 82.0, 22.0))
						.AddStaticText("Format", CairoFont.WhiteSmallText(), ElementBounds.Fixed(748.0, 218.0, 48.0, 22.0))
						.AddTextInput(ElementBounds.Fixed(800.0, 212.0, 92.0, 28.0), (string v) =>
						{
							adminChatTimestampFormat = v ?? "";
						}, CairoFont.WhiteSmallText(), "chatdisplay-format")
						.AddSwitch((bool v) =>
						{
							adminChatUseUtc = v;
						}, ElementBounds.Fixed(906.0, 214.0, 52.0, 26.0), "chatdisplay-utc")
						.AddStaticText("UTC", CairoFont.WhiteSmallText(), ElementBounds.Fixed(964.0, 218.0, 34.0, 22.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.status"), () => RunAdminCommand("chatdisplay-status"), ElementBounds.Fixed(1012.0, 212.0, 82.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => RunAdminCommand("chatdisplay-save"), ElementBounds.Fixed(1104.0, 212.0, 82.0, 30.0))
						.AddSwitch((bool v) =>
						{
							adminChatFormatGeneral = v;
						}, ElementBounds.Fixed(52.0, 258.0, 52.0, 26.0), "chatdisplay-general")
						.AddStaticText("Format General chat", CairoFont.WhiteSmallText(), ElementBounds.Fixed(110.0, 262.0, 160.0, 22.0))
						.AddSwitch((bool v) =>
						{
							adminChatFormatOtherGroups = v;
						}, ElementBounds.Fixed(286.0, 258.0, 52.0, 26.0), "chatdisplay-other-groups")
						.AddStaticText("Format all other groups", CairoFont.WhiteSmallText(), ElementBounds.Fixed(344.0, 262.0, 180.0, 22.0))
						.AddStaticText("Extra group IDs", CairoFont.WhiteSmallText(), ElementBounds.Fixed(550.0, 262.0, 110.0, 22.0))
						.AddTextInput(ElementBounds.Fixed(664.0, 256.0, 270.0, 28.0), (string v) =>
						{
							adminChatAdditionalGroupIds = v ?? "";
						}, CairoFont.WhiteSmallText(), "chatdisplay-extra-groups")
						.AddStaticText("General-only is the safe default for proximity/RP chat mods. Extra IDs are comma-separated.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 298.0, 850.0, 22.0))
						.AddStaticText("Role/name/timestamp formatting remains OFF until enabled above.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 330.0, 850.0, 22.0));
				}
				else
				{
					guiComposer.AddRichtext(SuiteLocalization.Get("gui.serveroperations"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 178.0, 300.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.delayduration"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 206.0, 236.0, 22.0)).AddTextInput(ElementBounds.Fixed(38.0, 230.0, 236.0, 28.0), (string v) =>
					{
						adminCommandDuration = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-duration")
						.AddRichtext(SuiteLocalization.Get("gui.lagradius"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(292.0, 206.0, 160.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(292.0, 230.0, 160.0, 28.0), OnAdminRadiusChanged, CairoFont.WhiteSmallText(), "admincmd-radius")
						.AddRichtext(SuiteLocalization.Get("gui.reasonlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(470.0, 206.0, 780.0, 22.0))
						.AddTextInput(ElementBounds.Fixed(470.0, 230.0, 780.0, 28.0), (string v) =>
						{
							adminCommandReason = v ?? "";
						}, CairoFont.WhiteSmallText(), "admincmd-reason")
						.AddSuiteButton(SuiteLocalization.Get("gui.laghere"), () => RunAdminCommand("laghere"), ElementBounds.Fixed(38.0, 270.0, 104.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.cleanupscan"), () => RunAdminCommand("cleanup-scan"), ElementBounds.Fixed(154.0, 270.0, 122.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.removedrops"), () => RunAdminCommand("cleanup-drops", dangerous: true), ElementBounds.Fixed(288.0, 270.0, 122.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.maintenanceon"), () => RunAdminCommand("maintenance-on"), ElementBounds.Fixed(422.0, 270.0, 132.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.maintenanceoff"), () => RunAdminCommand("maintenance-off"), ElementBounds.Fixed(566.0, 270.0, 132.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.maintenancestatus"), () => RunAdminCommand("maintenance-status"), ElementBounds.Fixed(710.0, 270.0, 148.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.scheduleshutdown"), () => RunAdminCommand("shutdown", dangerous: true), ElementBounds.Fixed(38.0, 310.0, 176.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.schedulerestart"), () => RunAdminCommand("restart", dangerous: true), ElementBounds.Fixed(226.0, 310.0, 164.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.cancelshutdown"), () => RunAdminCommand("shutdown-cancel"), ElementBounds.Fixed(402.0, 310.0, 154.0, 30.0))
						.AddSuiteButton(SuiteLocalization.Get("gui.undolast"), () => RunAdminCommand("undo", dangerous: true), ElementBounds.Fixed(568.0, 310.0, 112.0, 30.0))
						.AddRichtext(SuiteLocalization.Get("gui.serverconfirmhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 350.0, 1210.0, 32.0));
				}
				guiComposer.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 402.0, num3, 26.0), "page-header");
				num4 = 436.0;
				val = num2 - 28.0 - num4;
			}
			else
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 210.0)).AddRichtext(SuiteLocalization.Get("gui.issuereportqueue"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 134.0, 300.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.reportid"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 170.0, 72.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(114.0, 164.0, 92.0, 30.0), OnAdminRecordIdChanged, CairoFont.WhiteSmallText(), "admincmd-record-id")
					.AddRichtext(SuiteLocalization.Get("gui.statusassignee"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(224.0, 170.0, 150.0, 24.0))
					.AddTextInput(ElementBounds.Fixed(378.0, 164.0, 196.0, 30.0), (string v) =>
					{
						adminCommandReportValue = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-report-value")
					.AddRichtext(SuiteLocalization.Get("gui.privatenote"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 210.0, 88.0, 24.0))
					.AddTextInput(ElementBounds.Fixed(130.0, 204.0, 650.0, 30.0), (string v) =>
					{
						adminCommandReason = v ?? "";
					}, CairoFont.WhiteSmallText(), "admincmd-reason")
					.AddSuiteButton(SuiteLocalization.Get("gui.openreports"), () => RunAdminCommand("reports-open"), ElementBounds.Fixed(596.0, 164.0, 120.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.allreports"), () => RunAdminCommand("reports-all"), ElementBounds.Fixed(728.0, 164.0, 112.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.setstatus"), () => RunAdminCommand("report-status"), ElementBounds.Fixed(852.0, 164.0, 104.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.assign"), () => RunAdminCommand("report-assign"), ElementBounds.Fixed(968.0, 164.0, 92.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.addnote"), () => RunAdminCommand("report-note"), ElementBounds.Fixed(1072.0, 164.0, 100.0, 30.0))
					.AddRichtext(SuiteLocalization.Get("gui.reportstatushelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(804.0, 210.0, 480.0, 46.0))
					.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 342.0, 980.0, 26.0), "page-header");
				num4 = 376.0;
				val = 344.0;
			}
		}
		else if (section == "anticheat")
		{
			ElementBounds elementBounds7 = ElementBounds.Fixed(22.0, 82.0, 104.0, 30.0);
			ElementBounds elementBounds8 = ElementBounds.Fixed(138.0, 82.0, 92.0, 30.0);
			ElementBounds elementBounds9 = ElementBounds.Fixed(242.0, 82.0, 96.0, 30.0);
			ElementBounds elementBounds10 = ElementBounds.Fixed(350.0, 82.0, 106.0, 30.0);
			ElementBounds elementBounds11 = ElementBounds.Fixed(468.0, 82.0, 126.0, 30.0);
			ElementBounds elementBounds12 = ElementBounds.Fixed(606.0, 82.0, 104.0, 30.0);
			ElementBounds elementBounds13 = ElementBounds.Fixed(722.0, 82.0, 136.0, 30.0);
			ElementBounds elementBounds14 = ElementBounds.Fixed(870.0, 82.0, 134.0, 30.0);
			guiComposer.AddSuiteButton(SuiteLocalization.Get("gui.overview"), () => SelectAntiCheatView("overview"), elementBounds7, antiCheatView == "overview").AddHoverText(SuiteLocalization.Get("gui.tip.more.aebbaa0887"), CairoFont.WhiteSmallText(), 420, elementBounds7.FlatCopy(), "tip-anticheat-overview").AddSuiteButton(SuiteLocalization.Get("gui.players"), () => SelectAntiCheatView("players"), elementBounds8, antiCheatView == "players")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.4b9572bfa2"), CairoFont.WhiteSmallText(), 360, elementBounds8.FlatCopy(), "tip-anticheat-players")
				.AddSuiteButton(SuiteLocalization.Get("gui.reports"), () => SelectAntiCheatView("reports"), elementBounds9, antiCheatView == "reports")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.68a0c30ff9"), CairoFont.WhiteSmallText(), 430, elementBounds9.FlatCopy(), "tip-anticheat-reports")
				.AddSuiteButton(SuiteLocalization.Get("gui.blacklist"), () => SelectAntiCheatView("blacklist"), elementBounds10, antiCheatView == "blacklist")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.8c1a3f0a7b"), CairoFont.WhiteSmallText(), 430, elementBounds10.FlatCopy(), "tip-anticheat-blacklist")
				.AddSuiteButton(SuiteLocalization.Get("gui.enforcement"), () => SelectAntiCheatView("enforcement"), elementBounds11, antiCheatView == "enforcement")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.a15d2de091"), CairoFont.WhiteSmallText(), 470, elementBounds11.FlatCopy(), "tip-anticheat-enforcement")
				.AddSuiteButton(SuiteLocalization.Get("gui.settings"), () => SelectAntiCheatView("settings"), elementBounds12, antiCheatView == "settings")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.b2735f8bd0"), CairoFont.WhiteSmallText(), 470, elementBounds12.FlatCopy(), "tip-anticheat-settings")
				.AddSuiteButton(SuiteLocalization.Get("gui.packetprotection"), () => SelectAntiCheatView("packet"), elementBounds13, antiCheatView == "packet")
				.AddHoverText(SuiteLocalization.Get("gui.packetprotectiontip"), CairoFont.WhiteSmallText(), 470, elementBounds13.FlatCopy(), "tip-anticheat-packet")
				.AddSuiteButton(SuiteLocalization.Get("gui.playerdetail"), () => SelectAntiCheatView("player"), elementBounds14, antiCheatView == "player")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.8db2d2e264"), CairoFont.WhiteSmallText(), 440, elementBounds14.FlatCopy(), "tip-anticheat-player");
			if (antiCheatView == "enforcement")
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 244.0)).AddRichtext(SuiteLocalization.Get("gui.enforcementrules"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 130.0, 280.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.acrulesstagedhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(320.0, 131.0, 680.0, 24.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.newrule"), NewAntiCheatEnforcementRule, ElementBounds.Fixed(1010.0, 128.0, 104.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => RequestAntiCheat("enforcement"), ElementBounds.Fixed(1126.0, 128.0, 100.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.saverules"), SaveAntiCheatEnforcementRules, ElementBounds.Fixed(1238.0, 128.0, 100.0, 30.0))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(34.0, 168.0, 1248.0, 154.0)))
					.AddStaticText(SuiteLocalization.Get("gui.ruleeditor"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 178.0, 130.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.name"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 210.0, 44.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(100.0, 204.0, 300.0, 28.0), (string v) =>
					{
						acRuleName = v ?? "";
					}, CairoFont.WhiteSmallText(), "ac-rule-name")
					.AddStaticText(SuiteLocalization.Get("gui.enabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(418.0, 210.0, 62.0, 22.0))
					.AddSwitch((bool v) =>
					{
						if (!applyingAntiCheatEnforcementControls)
						{
							acRuleEnabled = v;
						}
					}, ElementBounds.Fixed(486.0, 204.0, 52.0, 26.0), "ac-rule-enabled")
					.AddStaticText(SuiteLocalization.Get("gui.literal.d3f06a581b"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550.0, 210.0, 76.0, 22.0))
					.AddDropDown(new string[3]
					{
						"FindingCode",
						SuiteLocalization.Get("gui.statusplain"),
						"RiskAtLeast"
					}, new string[3] { "Finding code", "Player status", "Risk at least" }, AntiCheatRuleTriggerIndex(), OnAntiCheatRuleTriggerChanged, ElementBounds.Fixed(630.0, 204.0, 150.0, 28.0), CairoFont.WhiteSmallText(), "ac-rule-trigger")
					.AddStaticText(SuiteLocalization.Get("gui.literal.8dce170de2"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(794.0, 210.0, 42.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(840.0, 204.0, 172.0, 28.0), (string v) =>
					{
						acRuleTriggerValue = v ?? "";
					}, CairoFont.WhiteSmallText(), "ac-rule-trigger-value")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.aca1997b5f"), CairoFont.WhiteSmallText(), 520, ElementBounds.Fixed(840.0, 204.0, 172.0, 28.0), "tip-ac-rule-trigger-value")
					.AddStaticText(SuiteLocalization.Get("gui.subject"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(1030.0, 210.0, 56.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(1090.0, 204.0, 170.0, 28.0), (string v) =>
					{
						acRuleSubject = v ?? "";
					}, CairoFont.WhiteSmallText(), "ac-rule-subject")
					.AddStaticText(SuiteLocalization.Get("gui.action"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 246.0, 48.0, 22.0))
					.AddDropDown(new string[4] { "Log", "Kick", "TempBan", "Freeze" }, new string[4] { "Log only", "Kick", "Temporary ban", "Freeze" }, AntiCheatRuleActionIndex(), OnAntiCheatRuleActionChanged, ElementBounds.Fixed(104.0, 240.0, 156.0, 28.0), CairoFont.WhiteSmallText(), "ac-rule-action")
					.AddStaticText(SuiteLocalization.Get("gui.duration"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(278.0, 246.0, 62.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(344.0, 240.0, 100.0, 28.0), (string v) =>
					{
						acRuleDuration = v ?? "";
					}, CairoFont.WhiteSmallText(), "ac-rule-duration")
					.AddStaticText(SuiteLocalization.Get("gui.message"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(462.0, 246.0, 72.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(540.0, 240.0, 720.0, 28.0), (string v) =>
					{
						acRuleMessage = v ?? "";
					}, CairoFont.WhiteSmallText(), "ac-rule-message")
					.AddHoverText(SuiteLocalization.Get("gui.acmessageplaceholders"), CairoFont.WhiteSmallText(), 460, ElementBounds.Fixed(540.0, 240.0, 720.0, 28.0), "tip-ac-rule-message")
					.AddSuiteButton(SuiteLocalization.Get("gui.literal.ff2d081ab2"), UpdateAntiCheatEnforcementRule, ElementBounds.Fixed(52.0, 280.0, 138.0, 28.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.deleteselected"), DeleteAntiCheatEnforcementRule, ElementBounds.Fixed(202.0, 280.0, 132.0, 28.0))
					.AddStaticText(SuiteLocalization.Get("gui.acapplycurrentsessions"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(360.0, 285.0, 350.0, 22.0))
					.AddSwitch((bool v) =>
					{
						if (!applyingAntiCheatEnforcementControls)
						{
							acRuleApplyCurrent = v;
						}
					}, ElementBounds.Fixed(716.0, 279.0, 52.0, 26.0), "ac-rule-apply-current")
					.AddRichtext(SuiteLocalization.Get("gui.acautomaticwarning"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(790.0, 283.0, 470.0, 38.0))
					.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 338.0, 1100.0, 26.0), "page-header");
				num4 = 374.0;
				val = 438.0;
			}
			else if (antiCheatView == "settings")
			{
				flag2 = false;
				BuildAntiCheatClientSettings(guiComposer);
			}
			else if (antiCheatView == "packet")
			{
				flag2 = false;
				CairoFont cairoFont = CairoFont.WhiteSmallText();
				cairoFont.UnscaledFontsize = 13.0;
				guiComposer.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 124.0, 700.0, 26.0), "page-header").AddRichtext(BuildPacketIngressStatusText(), cairoFont, ElementBounds.Fixed(760.0, 127.0, 256.0, 24.0)).AddHoverText(SuiteLocalization.Get("gui.workspace.ingressdetail", acPacketIngressStatus), CairoFont.WhiteSmallText(), 520, ElementBounds.Fixed(760.0, 124.0, 256.0, 28.0), "tip-packet-ingress-status")
					.AddSuiteButton(SuiteLocalization.Get("gui.savepacket"), SaveAntiCheatPacketProtectionSettings, ElementBounds.Fixed(1030.0, 122.0, 132.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.reload"), () => RequestAntiCheat("packet"), ElementBounds.Fixed(1176.0, 122.0, 112.0, 30.0))
					.AddRichtext(SuiteLocalization.Get("gui.packetprotectionhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 156.0, 1180.0, 34.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.packetgeneral"), () => SelectAntiCheatPacketView("general"), ElementBounds.Fixed(34.0, 174.0, 130.0, 30.0), antiCheatPacketView == "general")
					.AddSuiteButton(SuiteLocalization.Get("gui.packetmovement"), () => SelectAntiCheatPacketView("movement"), ElementBounds.Fixed(176.0, 174.0, 140.0, 30.0), antiCheatPacketView == "movement")
					.AddSuiteButton(SuiteLocalization.Get("gui.packetreach"), () => SelectAntiCheatPacketView("reach"), ElementBounds.Fixed(328.0, 174.0, 120.0, 30.0), antiCheatPacketView == "reach")
					.AddSuiteButton(SuiteLocalization.Get("gui.packetminingtps"), () => SelectAntiCheatPacketView("mining"), ElementBounds.Fixed(460.0, 174.0, 150.0, 30.0), antiCheatPacketView == "mining")
					.AddSuiteButton(SuiteLocalization.Get("gui.packetrisk"), () => SelectAntiCheatPacketView("risk"), ElementBounds.Fixed(622.0, 174.0, 130.0, 30.0), antiCheatPacketView == "risk")
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(28.0, 216.0, 622.0, 526.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(670.0, 216.0, 622.0, 526.0)));
				if (antiCheatPacketView == "general")
				{
					guiComposer.AddStaticText(SuiteLocalization.Get("gui.packetfeedbackcollision"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48.0, 230.0, 340.0, 22.0)).AddRichtext(SuiteLocalization.Get("gui.packetguardmaster"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 270.0, 300.0, 22.0)).AddSwitch((bool v) =>
					{
						SetAntiCheatPacketBool("master", v);
					}, ElementBounds.Fixed(576.0, 264.0, 52.0, 26.0), "ac-pkt-master")
						.AddRichtext(SuiteLocalization.Get("gui.playerchatwarnings"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 310.0, 300.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("warnings", v);
						}, ElementBounds.Fixed(576.0, 304.0, 52.0, 26.0), "ac-pkt-warnings")
						.AddRichtext(SuiteLocalization.Get("gui.warningcooldown"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 350.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 344.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("warning-cooldown", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-warning-cooldown")
						.AddRichtext(SuiteLocalization.Get("gui.noclipcollision"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 402.0, 320.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("noclip", v);
						}, ElementBounds.Fixed(576.0, 396.0, 52.0, 26.0), "ac-pkt-noclip")
						.AddHoverText(SuiteLocalization.Get("gui.noclipwarningtip"), CairoFont.WhiteSmallText(), 500, ElementBounds.Fixed(52.0, 396.0, 430.0, 30.0), "tip-ac-pkt-noclip")
						.AddRichtext(SuiteLocalization.Get("gui.collisionsamples"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 442.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 436.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("noclip-samples", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-noclip-samples")
						.AddRichtext(SuiteLocalization.Get("gui.penetrationdepth"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 482.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 476.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("noclip-penetration", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-noclip-penetration")
						.AddRichtext(SuiteLocalization.Get("gui.endpointmovement"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 522.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 516.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("noclip-endpoint", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-noclip-endpoint")
						.AddStaticText(SuiteLocalization.Get("gui.packetfphardening"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690.0, 230.0, 340.0, 22.0))
						.AddRichtext(SuiteLocalization.Get("gui.accelsampling"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 270.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 264.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("accel-sample", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-accel-sample")
						.AddRichtext(SuiteLocalization.Get("gui.accelsmoothing"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 310.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 304.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("accel-smoothing", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-accel-smoothing")
						.AddRichtext(SuiteLocalization.Get("gui.mountaccelratio"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 350.0, 330.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 344.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("mount-ratio", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-mount-ratio")
						.AddStaticText(SuiteLocalization.Get("gui.packetdiagnostics"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690.0, 402.0, 340.0, 22.0))
						.AddRichtext(SuiteLocalization.Get("gui.diagnosticcontext"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 442.0, 300.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("diag-context", v);
						}, ElementBounds.Fixed(1214.0, 436.0, 52.0, 26.0), "ac-pkt-diag-context")
						.AddRichtext(SuiteLocalization.Get("gui.suppressiontelemetry"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 482.0, 320.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("diag-suppression", v);
						}, ElementBounds.Fixed(1214.0, 476.0, 52.0, 26.0), "ac-pkt-diag-suppression")
						.AddRichtext(SuiteLocalization.Get("gui.speedhistorysamples"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 522.0, 320.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 516.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("diag-history", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-diag-history")
						.AddRichtext(SuiteLocalization.Get("gui.packetadvancednowingame"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 574.0, 550.0, 54.0));
				}
				else if (antiCheatPacketView == "movement")
				{
					guiComposer.AddStaticText(SuiteLocalization.Get("gui.packetmovementvalidation"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48.0, 230.0, 360.0, 22.0)).AddRichtext(SuiteLocalization.Get("gui.movementvalidator"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 270.0, 300.0, 22.0)).AddSwitch((bool v) =>
					{
						SetAntiCheatPacketBool("movement", v);
					}, ElementBounds.Fixed(576.0, 264.0, 52.0, 26.0), "ac-pkt-movement")
						.AddRichtext(SuiteLocalization.Get("gui.movementsamplems"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 310.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 304.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("movement-sample", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-movement-sample")
						.AddRichtext(SuiteLocalization.Get("gui.hardmovedistance"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 350.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 344.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("hard-move", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-hard-move")
						.AddRichtext(SuiteLocalization.Get("gui.movementconfirmsamples"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 390.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 384.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("movement-confirm-samples", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-movement-confirm-samples")
						.AddRichtext(SuiteLocalization.Get("gui.movementconfirmwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 430.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 424.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("movement-confirm-window", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-movement-confirm-window")
						.AddRichtext(SuiteLocalization.Get("gui.extrememultiplier"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 470.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 464.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("movement-extreme", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-movement-extreme")
						.AddRichtext(SuiteLocalization.Get("gui.correctiongrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 510.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 504.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("correction-grace", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-correction-grace")
						.AddRichtext(SuiteLocalization.Get("gui.rejectunauthorized"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 550.0, 360.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("reject-move", v);
						}, ElementBounds.Fixed(576.0, 544.0, 52.0, 26.0), "ac-pkt-reject-move")
						.AddRichtext(SuiteLocalization.Get("gui.exemptadmins"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 590.0, 300.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("exempt-admins", v);
						}, ElementBounds.Fixed(576.0, 584.0, 52.0, 26.0), "ac-pkt-exempt-admins")
						.AddStaticText(SuiteLocalization.Get("gui.packetmountteleport"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690.0, 230.0, 360.0, 22.0))
						.AddRichtext(SuiteLocalization.Get("gui.exemptmounted"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 270.0, 320.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("exempt-mounted", v);
						}, ElementBounds.Fixed(1214.0, 264.0, 52.0, 26.0), "ac-pkt-exempt-mounted")
						.AddRichtext(SuiteLocalization.Get("gui.mountgrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 310.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 304.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("mount-grace", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-mount-grace")
						.AddRichtext(SuiteLocalization.Get("gui.engineteleportgrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 350.0, 330.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 344.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("engine-teleport-grace", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-engine-teleport-grace")
						.AddRichtext(SuiteLocalization.Get("gui.mountaccelratio"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 390.0, 330.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 384.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("mount-ratio", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-mount-ratio")
						.AddRichtext(SuiteLocalization.Get("gui.tprunescompat"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 430.0, 320.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("tprunes", v);
						}, ElementBounds.Fixed(1214.0, 424.0, 52.0, 26.0), "ac-pkt-tprunes")
						.AddRichtext(SuiteLocalization.Get("gui.tprunesgrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 470.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 464.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("tprunes-grace", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-tprunes-grace")
						.AddRichtext(acPacketTPRunesDetected ? SuiteLocalization.Get("gui.tprunesdetected") : SuiteLocalization.Get("gui.tprunesnotdetected"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 510.0, 430.0, 22.0))
						.AddRichtext(SuiteLocalization.Get("gui.th3spawncompat"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 550.0, 320.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("th3spawn", v);
						}, ElementBounds.Fixed(1214.0, 544.0, 52.0, 26.0), "ac-pkt-th3spawn")
						.AddRichtext(SuiteLocalization.Get("gui.th3spawngrace"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 590.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 584.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("th3spawn-grace", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-th3spawn-grace")
						.AddRichtext(acTh3EssentialsDetected ? SuiteLocalization.Get("gui.th3detected") : SuiteLocalization.Get("gui.th3notdetected"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 630.0, 430.0, 22.0));
				}
				else if (antiCheatPacketView == "reach")
				{
					guiComposer.AddStaticText(SuiteLocalization.Get("gui.packetreachvalidation"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48.0, 230.0, 360.0, 22.0)).AddRichtext(SuiteLocalization.Get("gui.reachvalidator"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 270.0, 300.0, 22.0)).AddSwitch((bool v) =>
					{
						SetAntiCheatPacketBool("reach", v);
					}, ElementBounds.Fixed(576.0, 264.0, 52.0, 26.0), "ac-pkt-reach")
						.AddRichtext(SuiteLocalization.Get("gui.reachtolerance"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 310.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 304.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("reach-tolerance", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-tolerance")
						.AddRichtext(SuiteLocalization.Get("gui.reachlatencycomp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 350.0, 340.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("reach-latency", v);
						}, ElementBounds.Fixed(576.0, 344.0, 52.0, 26.0), "ac-pkt-reach-latency")
						.AddRichtext(SuiteLocalization.Get("gui.reachmaxlatency"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 390.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 384.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("reach-max-latency", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-max-latency")
						.AddRichtext(SuiteLocalization.Get("gui.reachhistory"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 430.0, 340.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("reach-history", v);
						}, ElementBounds.Fixed(576.0, 424.0, 52.0, 26.0), "ac-pkt-reach-history")
						.AddRichtext(SuiteLocalization.Get("gui.reachhistoryms"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 470.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 464.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("reach-history-ms", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-history-ms")
						.AddRichtext(SuiteLocalization.Get("gui.reachrewindms"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 510.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 504.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("reach-rewind-ms", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-rewind-ms")
						.AddStaticText(SuiteLocalization.Get("gui.packetreachthresholds"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690.0, 230.0, 360.0, 22.0))
						.AddRichtext(SuiteLocalization.Get("gui.reachusetolerance"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 270.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 264.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("reach-use-extra", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-use-extra")
						.AddRichtext(SuiteLocalization.Get("gui.reachbreaktolerance"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 310.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 304.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("reach-break-extra", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-break-extra")
						.AddRichtext(SuiteLocalization.Get("gui.reachnearstrikes"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 350.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 344.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("reach-strikes", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-strikes")
						.AddRichtext(SuiteLocalization.Get("gui.reachstrikewindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 390.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 384.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("reach-strike-window", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-strike-window")
						.AddRichtext(SuiteLocalization.Get("gui.reachstrikedecay"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 430.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 424.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("reach-strike-decay", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-strike-decay")
						.AddRichtext(SuiteLocalization.Get("gui.hardexcess"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 470.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 464.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("reach-hard", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-reach-hard")
						.AddRichtext(SuiteLocalization.Get("gui.rejectreach"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 510.0, 340.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("reject-reach", v);
						}, ElementBounds.Fixed(1214.0, 504.0, 52.0, 26.0), "ac-pkt-reject-reach")
						.AddRichtext(SuiteLocalization.Get("gui.reachthresholdhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 562.0, 550.0, 54.0));
				}
				else if (antiCheatPacketView == "mining")
				{
					guiComposer.AddStaticText(SuiteLocalization.Get("gui.packetminingvalidation"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48.0, 230.0, 360.0, 22.0)).AddRichtext(SuiteLocalization.Get("gui.miningvalidator"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 270.0, 300.0, 22.0)).AddSwitch((bool v) =>
					{
						SetAntiCheatPacketBool("mining", v);
					}, ElementBounds.Fixed(576.0, 264.0, 52.0, 26.0), "ac-pkt-mining")
						.AddRichtext(SuiteLocalization.Get("gui.veinwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 310.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 304.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("vein-window", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-vein-window")
						.AddRichtext(SuiteLocalization.Get("gui.connectedbreaklimit"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 350.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 344.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("connected-break-limit", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-connected-break-limit")
						.AddRichtext(SuiteLocalization.Get("gui.duplicatetargetwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 390.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 384.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("duplicate-target", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-duplicate-target")
						.AddRichtext(SuiteLocalization.Get("gui.rejectbursts"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 430.0, 340.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("reject-bursts", v);
						}, ElementBounds.Fixed(576.0, 424.0, 52.0, 26.0), "ac-pkt-reject-bursts")
						.AddRichtext(SuiteLocalization.Get("gui.miningcadence"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 470.0, 340.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("mining-cadence", v);
						}, ElementBounds.Fixed(576.0, 464.0, 52.0, 26.0), "ac-pkt-mining-cadence")
						.AddRichtext(SuiteLocalization.Get("gui.cadenceminexpected"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 510.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 504.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("cadence-min", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-cadence-min")
						.AddRichtext(SuiteLocalization.Get("gui.suspiciousratio"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 550.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 544.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("mining-ratio", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-mining-ratio")
						.AddRichtext(SuiteLocalization.Get("gui.confirmations"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 590.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 584.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("mining-confirmations", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-mining-confirmations")
						.AddRichtext(SuiteLocalization.Get("gui.cadenceconfirmwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 630.0, 340.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(532.0, 624.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("cadence-confirm-window", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-cadence-confirm-window")
						.AddRichtext(SuiteLocalization.Get("gui.rejectcadence"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 670.0, 340.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("reject-cadence", v);
						}, ElementBounds.Fixed(576.0, 664.0, 52.0, 26.0), "ac-pkt-reject-cadence")
						.AddStaticText(SuiteLocalization.Get("gui.packettpsawareness"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690.0, 230.0, 360.0, 22.0))
						.AddRichtext(SuiteLocalization.Get("gui.tpsaware"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 270.0, 300.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("tps", v);
						}, ElementBounds.Fixed(1214.0, 264.0, 52.0, 26.0), "ac-pkt-tps")
						.AddRichtext(SuiteLocalization.Get("gui.degradedpct"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 310.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 304.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("tps-degraded", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-tps-degraded")
						.AddRichtext(SuiteLocalization.Get("gui.criticalpct"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 350.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 344.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("tps-critical", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-tps-critical")
						.AddRichtext(SuiteLocalization.Get("gui.maxmultiplier"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 390.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 384.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketDouble("tps-max", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-tps-max")
						.AddRichtext(SuiteLocalization.Get("gui.suppresscriticalrejects"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 430.0, 360.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("tps-suppress", v);
						}, ElementBounds.Fixed(1214.0, 424.0, 52.0, 26.0), "ac-pkt-tps-suppress")
						.AddStaticText(SuiteLocalization.Get("gui.tpslivestatus"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690.0, 486.0, 360.0, 22.0))
						.AddRichtext(BuildPacketTpsStatusText(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 524.0, 550.0, 100.0));
				}
				else
				{
					guiComposer.AddStaticText(SuiteLocalization.Get("gui.packetfamilyrisk"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(48.0, 230.0, 360.0, 22.0)).AddRichtext(SuiteLocalization.Get("gui.perfamilyriskenabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 270.0, 300.0, 22.0)).AddSwitch((bool v) =>
					{
						SetAntiCheatPacketBool("family-risk", v);
					}, ElementBounds.Fixed(576.0, 264.0, 52.0, 26.0), "ac-pkt-family-risk")
						.AddStaticText(SuiteLocalization.Get("gui.riskfamily"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 318.0, 140.0, 22.0))
						.AddStaticText(SuiteLocalization.Get("gui.decayseconds"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(254.0, 318.0, 84.0, 22.0))
						.AddStaticText(SuiteLocalization.Get("gui.reviewplain"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(388.0, 318.0, 72.0, 22.0))
						.AddStaticText(SuiteLocalization.Get("gui.suspiciousplain"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(510.0, 318.0, 96.0, 22.0))
						.AddStaticText(SuiteLocalization.Get("gui.movement"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 356.0, 180.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(248.0, 350.0, 86.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-move-decay", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-move-decay")
						.AddNumberInput(ElementBounds.Fixed(382.0, 350.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-move-review", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-move-review")
						.AddNumberInput(ElementBounds.Fixed(516.0, 350.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-move-suspicious", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-move-suspicious")
						.AddStaticText(SuiteLocalization.Get("gui.reach"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 398.0, 180.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(248.0, 392.0, 86.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-reach-decay", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-reach-decay")
						.AddNumberInput(ElementBounds.Fixed(382.0, 392.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-reach-review", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-reach-review")
						.AddNumberInput(ElementBounds.Fixed(516.0, 392.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-reach-suspicious", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-reach-suspicious")
						.AddStaticText(SuiteLocalization.Get("gui.mining"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 440.0, 180.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(248.0, 434.0, 86.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-mining-decay", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-mining-decay")
						.AddNumberInput(ElementBounds.Fixed(382.0, 434.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-mining-review", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-mining-review")
						.AddNumberInput(ElementBounds.Fixed(516.0, 434.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-mining-suspicious", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-mining-suspicious")
						.AddStaticText(SuiteLocalization.Get("gui.packetabuse"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 482.0, 180.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(248.0, 476.0, 86.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-packet-decay", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-packet-decay")
						.AddNumberInput(ElementBounds.Fixed(382.0, 476.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-packet-review", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-packet-review")
						.AddNumberInput(ElementBounds.Fixed(516.0, 476.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-packet-suspicious", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-packet-suspicious")
						.AddStaticText(SuiteLocalization.Get("gui.clientintegrity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 524.0, 180.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(248.0, 518.0, 86.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-client-decay", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-client-decay")
						.AddNumberInput(ElementBounds.Fixed(382.0, 518.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-client-review", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-client-review")
						.AddNumberInput(ElementBounds.Fixed(516.0, 518.0, 76.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("risk-client-suspicious", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-risk-client-suspicious")
						.AddRichtext(SuiteLocalization.Get("gui.familyriskhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 574.0, 550.0, 74.0))
						.AddStaticText(SuiteLocalization.Get("gui.packetcorrelationhistory"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(690.0, 230.0, 420.0, 22.0))
						.AddRichtext(SuiteLocalization.Get("gui.incidentcorrelation"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 270.0, 330.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("correlation", v);
						}, ElementBounds.Fixed(1214.0, 264.0, 52.0, 26.0), "ac-pkt-correlation")
						.AddRichtext(SuiteLocalization.Get("gui.incidentwindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 310.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 304.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("incident-window", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-incident-window")
						.AddRichtext(SuiteLocalization.Get("gui.requiredcategories"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 350.0, 320.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 344.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("incident-units", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-incident-units")
						.AddRichtext(SuiteLocalization.Get("gui.maxriskbonus"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 390.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 384.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("incident-bonus", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-incident-bonus")
						.AddRichtext(SuiteLocalization.Get("gui.persistentreporthistory"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 442.0, 330.0, 22.0))
						.AddSwitch((bool v) =>
						{
							SetAntiCheatPacketBool("history", v);
						}, ElementBounds.Fixed(1214.0, 436.0, 52.0, 26.0), "ac-pkt-history")
						.AddRichtext(SuiteLocalization.Get("gui.retentiondays"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 482.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 476.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("history-days", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-history-days")
						.AddRichtext(SuiteLocalization.Get("gui.maxrecords"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 522.0, 300.0, 22.0))
						.AddNumberInput(ElementBounds.Fixed(1170.0, 516.0, 96.0, 28.0), (string v) =>
						{
							SetAntiCheatPacketInt("history-max", v);
						}, CairoFont.WhiteSmallText(), "ac-pkt-history-max")
						.AddRichtext(SuiteLocalization.Get("gui.correlationriskhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 574.0, 550.0, 74.0));
				}
				num4 = 0.0;
				val = 0.0;
			}
			else if (antiCheatView == "player")
			{
				guiComposer.AddRichtext(SuiteLocalization.Get("gui.playerinvestigation"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 129.0, 260.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.playerlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 161.0, 58.0, 24.0)).AddTextInput(ElementBounds.Fixed(96.0, 155.0, 330.0, 30.0), (string text10) =>
				{
					antiCheatPlayerSearch = text10 ?? "";
				}, CairoFont.WhiteSmallText(), "anticheat-player-search")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.28c348c602"), CairoFont.WhiteSmallText(), 330, ElementBounds.Fixed(96.0, 155.0, 330.0, 30.0), "tip-anticheat-player-search")
					.AddSuiteButton(SuiteLocalization.Get("gui.load"), LoadAntiCheatPlayer, ElementBounds.Fixed(442.0, 155.0, 100.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.deeprescan"), RescanAntiCheatPlayer, ElementBounds.Fixed(556.0, 155.0, 140.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.e95320215e"), CairoFont.WhiteSmallText(), 440, ElementBounds.Fixed(556.0, 155.0, 140.0, 30.0), "tip-anticheat-player-rescan")
					.AddSuiteButton(SuiteLocalization.Get("gui.backtoplayers"), () => SelectAntiCheatView("players"), ElementBounds.Fixed(714.0, 155.0, 150.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.031c7cf4b7"), CairoFont.WhiteSmallText(), 330, ElementBounds.Fixed(714.0, 155.0, 150.0, 30.0), "tip-anticheat-player-back")
					.AddRichtext(SuiteLocalization.Get("gui.acsummaryhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 195.0, 1090.0, 28.0))
					.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 226.0, 1100.0, 26.0), "page-header")
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(28.0, 254.0, 306.0, 142.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(346.0, 254.0, 306.0, 142.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(664.0, 254.0, 306.0, 142.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(982.0, 254.0, 310.0, 142.0)))
					.AddRichtext(BuildAntiCheatIdentityCard(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 268.0, 266.0, 116.0))
					.AddRichtext(BuildAntiCheatRiskCard(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(366.0, 268.0, 266.0, 116.0))
					.AddRichtext(BuildAntiCheatClientCard(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 268.0, 266.0, 116.0))
					.AddRichtext(BuildAntiCheatRuntimeCard(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(1002.0, 268.0, 270.0, 116.0))
					.AddRichtext(SuiteLocalization.Get("gui.evidencetimeline"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 410.0, 360.0, 24.0));
				num4 = 438.0;
				val = 366.0;
			}
			else if (antiCheatView == "reports")
			{
				guiComposer.AddRichtext(SuiteLocalization.Get("gui.persistentincidents"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 129.0, 330.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.playerfilter"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 161.0, 92.0, 24.0)).AddTextInput(ElementBounds.Fixed(132.0, 155.0, 300.0, 30.0), (string text10) =>
				{
					antiCheatReportSearch = text10 ?? "";
				}, CairoFont.WhiteSmallText(), "anticheat-report-search")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.3e23cb28f4"), CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(132.0, 155.0, 300.0, 30.0), "tip-anticheat-report-search")
					.AddSuiteButton(SuiteLocalization.Get("gui.loadreports"), LoadAntiCheatReports, ElementBounds.Fixed(448.0, 155.0, 132.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), LoadAntiCheatReports, ElementBounds.Fixed(594.0, 155.0, 108.0, 30.0))
					.AddRichtext(SuiteLocalization.Get("gui.reportpersistencehelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 195.0, 1060.0, 28.0))
					.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 258.0, 980.0, 26.0), "page-header");
				num4 = 292.0;
				val = 520.0;
			}
			else if (antiCheatView == "blacklist")
			{
				guiComposer.AddRichtext(SuiteLocalization.Get("gui.clientmodblacklist"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 129.0, 300.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.literal.6364065a59"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 161.0, 58.0, 24.0)).AddTextInput(ElementBounds.Fixed(96.0, 155.0, 300.0, 30.0), (string text10) =>
				{
					antiCheatBlacklistModId = text10 ?? "";
				}, CairoFont.WhiteSmallText(), "anticheat-blacklist-mod")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.8b9d6c1c0f"), CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(96.0, 155.0, 300.0, 30.0), "tip-anticheat-blacklist-mod")
					.AddRichtext(SuiteLocalization.Get("gui.sourceplayer"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(418.0, 161.0, 118.0, 24.0))
					.AddTextInput(ElementBounds.Fixed(540.0, 155.0, 238.0, 30.0), (string text10) =>
					{
						antiCheatBlacklistSourcePlayer = text10 ?? "";
					}, CairoFont.WhiteSmallText(), "anticheat-blacklist-source")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.3e5fc090d6"), CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(540.0, 155.0, 238.0, 30.0), "tip-anticheat-blacklist-source")
					.AddSuiteButton(SuiteLocalization.Get("gui.add"), AddAntiCheatBlacklist, ElementBounds.Fixed(794.0, 155.0, 90.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.remove"), RemoveAntiCheatBlacklist, ElementBounds.Fixed(898.0, 155.0, 100.0, 30.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), LoadAntiCheatBlacklist, ElementBounds.Fixed(1012.0, 155.0, 110.0, 30.0))
					.AddRichtext("<font color=\"#ffd166\"><strong>Controlserver required.</strong></font> <font color=\"#91a0ae\">Core/required mods are protected from accidental blacklist changes. Existing accepted scans are re-evaluated immediately.</font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 195.0, 1085.0, 28.0))
					.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 258.0, 980.0, 26.0), "page-header");
				num4 = 292.0;
				val = 520.0;
			}
			else
			{
				guiComposer.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 132.0, 700.0, 26.0), "page-header").AddRichtext(SuiteLocalization.Get("gui.protection"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(760.0, 134.0, 92.0, 24.0)).AddSwitch(SetAntiCheatEnabled, ElementBounds.Fixed(856.0, 128.0, 60.0, 30.0), "anticheat-enabled")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.37e73850bc"), CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(856.0, 128.0, 60.0, 30.0), "tip-anticheat-enabled")
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => RequestAntiCheat(antiCheatView), ElementBounds.Fixed(1030.0, 128.0, 120.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.092e0a3272"), CairoFont.WhiteSmallText(), 330, ElementBounds.Fixed(1030.0, 128.0, 120.0, 30.0), "tip-anticheat-refresh");
				if (antiCheatView == "players")
				{
					guiComposer.AddRichtext("<font color=\"#91a0ae\"><strong>Risk rating:</strong> 0-9 Low • 10-24 Guarded • 25-49 Elevated • 50-74 High • 75-100 Critical. Ratings explain the score; they do not change enforcement rules.</font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 161.0, 1080.0, 28.0));
					num4 = 194.0;
					val = 610.0;
				}
				else
				{
					num4 = 174.0;
					val = 546.0;
				}
			}
		}
		else if (section == "webserver")
		{
			ElementBounds elementBounds15 = ElementBounds.Fixed(22.0, 82.0, 122.0, 30.0);
			ElementBounds elementBounds16 = ElementBounds.Fixed(158.0, 82.0, 122.0, 30.0);
			ElementBounds elementBounds17 = ElementBounds.Fixed(294.0, 82.0, 122.0, 30.0);
			guiComposer.AddSuiteButton(SuiteLocalization.Get("gui.webpanel"), () => SelectWebView("webpanel"), elementBounds15, webView == "webpanel").AddHoverText(SuiteLocalization.Get("gui.tip.more.dd066c8356"), CairoFont.WhiteSmallText(), 410, elementBounds15.FlatCopy(), "tip-web-tab-panel").AddSuiteButton(SuiteLocalization.Get("gui.webmap"), () => SelectWebView("webmap"), elementBounds16, webView == "webmap")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.affb85d245"), CairoFont.WhiteSmallText(), 440, elementBounds16.FlatCopy(), "tip-web-tab-map");
			if (discordAvailable)
			{
				guiComposer.AddSuiteButton(SuiteLocalization.Get("gui.discord"), () => SelectWebView("discord"), elementBounds17, webView == "discord").AddHoverText(SuiteLocalization.Get("gui.tip.discord"), CairoFont.WhiteSmallText(), 440, elementBounds17.FlatCopy(), "tip-web-tab-discord");
			}
			if (webView == "discord")
			{
				BuildDiscordIntegrationPage(guiComposer);
				flag2 = false;
			}
			else if (webView == "webmap")
			{
				flag2 = false;
				guiComposer.AddRichtext(SuiteLocalization.Get("gui.webmapconfig"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 130.0, 400.0, 26.0), "page-header").AddSuiteButton(SuiteLocalization.Get("gui.literal.e7ad076271"), SaveWebMapSettings, ElementBounds.Fixed(1030.0, 126.0, 160.0, 30.0)).AddSuiteButton(SuiteLocalization.Get("gui.reload"), () => RequestWebDashboard("map"), ElementBounds.Fixed(1202.0, 126.0, 90.0, 30.0))
					.AddStaticText(SuiteLocalization.Get("gui.mapsettingsstaged"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 158.0, 1040.0, 26.0))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(28.0, 190.0, 622.0, 252.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(670.0, 190.0, 622.0, 252.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(28.0, 454.0, 622.0, 250.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(670.0, 454.0, 622.0, 250.0)))
					.AddStaticText(SuiteLocalization.Get("gui.hostidentity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 202.0, 220.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.mapruntime"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 234.0, 150.0, 22.0))
					.AddSwitch((bool v) =>
					{
						SetWebMapBool("enabled", v);
					}, ElementBounds.Fixed(576.0, 228.0, 52.0, 26.0), "webmap-enabled")
					.AddStaticText(SuiteLocalization.Get("gui.bindaddress"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 268.0, 120.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(180.0, 262.0, 250.0, 28.0), (string v) =>
					{
						webMapBindAddress = v ?? "";
					}, CairoFont.WhiteSmallText(), "webmap-bind")
					.AddStaticText(SuiteLocalization.Get("gui.port"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(448.0, 268.0, 52.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(502.0, 262.0, 126.0, 28.0), (string v) =>
					{
						SetWebMapInt("port", v);
					}, CairoFont.WhiteSmallText(), "webmap-port")
					.AddStaticText(SuiteLocalization.Get("gui.maptitle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 302.0, 110.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(180.0, 296.0, 448.0, 28.0), (string v) =>
					{
						webMapTitle = v ?? "";
					}, CairoFont.WhiteSmallText(), "webmap-title")
					.AddStaticText(SuiteLocalization.Get("gui.publicurl"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 336.0, 110.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(180.0, 330.0, 448.0, 28.0), (string v) =>
					{
						webMapPublicUrl = v ?? "";
					}, CairoFont.WhiteSmallText(), "webmap-public-url")
					.AddStaticText(SuiteLocalization.Get("gui.panelurl"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 370.0, 110.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(180.0, 364.0, 448.0, 28.0), (string v) =>
					{
						webMapDashboardUrl = v ?? "";
					}, CairoFont.WhiteSmallText(), "webmap-dashboard-url")
					.AddStaticText(SuiteLocalization.Get(webMapRunning ? "gui.runningyes" : "gui.runningno"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 404.0, 170.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.publiclayersprivacy"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(690.0, 202.0, 260.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.publishplayers"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 234.0, 210.0, 22.0))
					.AddSwitch((bool v) =>
					{
						SetWebMapBool("players", v);
					}, ElementBounds.Fixed(1228.0, 228.0, 52.0, 26.0), "webmap-players")
					.AddStaticText(SuiteLocalization.Get("gui.waypoints"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 268.0, 180.0, 22.0))
					.AddSwitch((bool v) =>
					{
						SetWebMapBool("waypoints", v);
					}, ElementBounds.Fixed(1228.0, 262.0, 52.0, 26.0), "webmap-waypoints")
					.AddStaticText(SuiteLocalization.Get("gui.blockloggeractivity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 302.0, 210.0, 22.0))
					.AddSwitch((bool v) =>
					{
						SetWebMapBool("activity", v);
					}, ElementBounds.Fixed(1228.0, 296.0, 52.0, 26.0), "webmap-activity")
					.AddStaticText(SuiteLocalization.Get("gui.badblockentities"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 336.0, 210.0, 22.0))
					.AddSwitch((bool v) =>
					{
						SetWebMapBool("badbe", v);
					}, ElementBounds.Fixed(1228.0, 330.0, 52.0, 26.0), "webmap-badbe")
					.AddStaticText(SuiteLocalization.Get("gui.playerwaypointlimits"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 370.0, 180.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.major"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(900.0, 370.0, 52.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(952.0, 364.0, 72.0, 28.0), (string v) =>
					{
						SetWebMapInt("major", v);
					}, CairoFont.WhiteSmallText(), "webmap-major")
					.AddStaticText(SuiteLocalization.Get("gui.minor"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(1042.0, 370.0, 52.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(1094.0, 364.0, 72.0, 28.0), (string v) =>
					{
						SetWebMapInt("minor", v);
					}, CairoFont.WhiteSmallText(), "webmap-minor")
					.AddStaticText(SuiteLocalization.Get("gui.livecoordswarning"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 404.0, 500.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.mapviewactivity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 466.0, 240.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.maxzoom"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 500.0, 220.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(500.0, 494.0, 128.0, 28.0), (string v) =>
					{
						SetWebMapInt("maxzoom", v);
					}, CairoFont.WhiteSmallText(), "webmap-maxzoom")
					.AddStaticText(SuiteLocalization.Get("gui.defaultspan"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 534.0, 220.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(500.0, 528.0, 128.0, 28.0), (string v) =>
					{
						SetWebMapInt("defaultspan", v);
					}, CairoFont.WhiteSmallText(), "webmap-defaultspan")
					.AddStaticText(SuiteLocalization.Get("gui.tilesize"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 568.0, 180.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(500.0, 562.0, 128.0, 28.0), (string v) =>
					{
						SetWebMapInt("tilesize", v);
					}, CairoFont.WhiteSmallText(), "webmap-tilesize")
					.AddStaticText(SuiteLocalization.Get("gui.activitywindow"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 602.0, 220.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(500.0, 596.0, 128.0, 28.0), (string v) =>
					{
						SetWebMapInt("activitywindow", v);
					}, CairoFont.WhiteSmallText(), "webmap-activitywindow")
					.AddStaticText(SuiteLocalization.Get("gui.maxmarkers"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 636.0, 220.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(500.0, 630.0, 128.0, 28.0), (string v) =>
					{
						SetWebMapInt("activitymarkers", v);
					}, CairoFont.WhiteSmallText(), "webmap-activitymarkers")
					.AddStaticText(SuiteLocalization.Get("gui.startupexport"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(690.0, 466.0, 280.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.exportnext"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 500.0, 240.0, 22.0))
					.AddSwitch((bool v) =>
					{
						SetWebMapBool("exportnext", v);
					}, ElementBounds.Fixed(1228.0, 494.0, 52.0, 26.0), "webmap-exportnext")
					.AddStaticText(SuiteLocalization.Get("gui.autoexports"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 534.0, 240.0, 22.0))
					.AddSwitch((bool v) =>
					{
						SetWebMapBool("autoexport", v);
					}, ElementBounds.Fixed(1228.0, 528.0, 52.0, 26.0), "webmap-autoexport")
					.AddStaticText(SuiteLocalization.Get("gui.intervaldays"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 568.0, 140.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(900.0, 562.0, 92.0, 28.0), (string v) =>
					{
						SetWebMapInt("exportdays", v);
					}, CairoFont.WhiteSmallText(), "webmap-exportdays")
					.AddStaticText(SuiteLocalization.Get("gui.incrementalexport"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 602.0, 200.0, 22.0))
					.AddSwitch((bool v) =>
					{
						SetWebMapBool("incremental", v);
					}, ElementBounds.Fixed(1228.0, 596.0, 52.0, 26.0), "webmap-incremental")
					.AddStaticText(SuiteLocalization.Get("gui.lockexport"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 636.0, 240.0, 22.0))
					.AddSwitch((bool v) =>
					{
						SetWebMapBool("lockexport", v);
					}, ElementBounds.Fixed(1228.0, 630.0, 52.0, 26.0), "webmap-lockexport")
					.AddStaticText(webMapTerrainExportInProgress ? SuiteLocalization.Get("gui.runtime.terrainrunning") : SuiteLocalization.Get("gui.runtime.terrainidle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 670.0, 250.0, 22.0));
				if (!string.IsNullOrWhiteSpace(webMapLastError))
				{
					guiComposer.AddRichtext($"<font color=\"#ff7070\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.lastmaperror"))}</strong> {Escape(webMapLastError)}</font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930.0, 670.0, 350.0, 24.0));
				}
				num4 = 0.0;
				val = 0.0;
			}
			else
			{
				flag2 = false;
				guiComposer.AddRichtext(SuiteLocalization.Get("gui.webpaneltitle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 130.0, 400.0, 26.0), "page-header").AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => RequestWebDashboard("panel"), ElementBounds.Fixed(1182.0, 126.0, 110.0, 30.0)).AddStaticText(SuiteLocalization.Get("gui.webpanelsettingshelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(450.0, 132.0, 700.0, 24.0))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(28.0, 172.0, 622.0, 162.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(670.0, 172.0, 622.0, 162.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(28.0, 346.0, 622.0, 318.0)))
					.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(670.0, 346.0, 622.0, 318.0)))
					.AddStaticText(SuiteLocalization.Get("gui.runtimeaccess"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 184.0, 230.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.webpanelenabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 218.0, 170.0, 22.0))
					.AddSwitch((bool value11) =>
					{
						SetWebBool("enabled", value11);
					}, ElementBounds.Fixed(576.0, 212.0, 52.0, 26.0), "web-enabled")
					.AddRichtext($"<strong>Runtime:</strong> {(webRunning ? "<font color=\"#8fda72\">RUNNING</font>" : "<font color=\"#ff9d66\">STOPPED</font>")}<br><strong>Authentication:</strong> {(webLocalAnonymousMode ? "<font color=\"#ffd166\">LOCAL / NO LOGIN</font>" : (webPasswordConfigured ? "<font color=\"#8fda72\">PASSWORD</font>" : "<font color=\"#ff7070\">PASSWORD REQUIRED</font>"))}<br><strong>Active sessions:</strong> {webActiveSessions}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 248.0, 520.0, 74.0))
					.AddStaticText(SuiteLocalization.Get("gui.listenerpublic"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(690.0, 184.0, 280.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.bindaddress"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 218.0, 110.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(808.0, 212.0, 250.0, 28.0), (string text10) =>
					{
						webBindAddress = text10 ?? "";
					}, CairoFont.WhiteSmallText(), "web-bind")
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebBind, ElementBounds.Fixed(1070.0, 212.0, 84.0, 28.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.ebc02e7008"), CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(808.0, 212.0, 250.0, 28.0), "tip-web-bind")
					.AddStaticText(SuiteLocalization.Get("gui.port"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 256.0, 70.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(808.0, 250.0, 110.0, 28.0), OnWebPortChanged, CairoFont.WhiteSmallText(), "web-port")
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebPort, ElementBounds.Fixed(930.0, 250.0, 84.0, 28.0))
					.AddStaticText(SuiteLocalization.Get("gui.publicurl"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 294.0, 90.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(808.0, 288.0, 346.0, 28.0), (string text10) =>
					{
						webPublicUrl = text10 ?? "";
					}, CairoFont.WhiteSmallText(), "web-public-url")
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebPublicUrl, ElementBounds.Fixed(1166.0, 288.0, 84.0, 28.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.5200fa9b50"), CairoFont.WhiteSmallText(), 440, ElementBounds.Fixed(808.0, 288.0, 346.0, 28.0), "tip-web-public-url")
					.AddStaticText(SuiteLocalization.Get("gui.authsession"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 358.0, 280.0, 22.0))
					.AddStaticText(SuiteLocalization.Get("gui.adminusername"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 392.0, 130.0, 22.0))
					.AddTextInput(ElementBounds.Fixed(184.0, 386.0, 244.0, 28.0), (string text10) =>
					{
						webAdminUsername = text10 ?? "";
					}, CairoFont.WhiteSmallText(), "web-username")
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebUsername, ElementBounds.Fixed(440.0, 386.0, 84.0, 28.0))
					.AddStaticText(SuiteLocalization.Get("gui.sessiontimeout"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 430.0, 130.0, 22.0))
					.AddNumberInput(ElementBounds.Fixed(184.0, 424.0, 110.0, 28.0), OnWebSessionChanged, CairoFont.WhiteSmallText(), "web-session-minutes")
					.AddStaticText(SuiteLocalization.Get("gui.minutes"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(302.0, 430.0, 62.0, 22.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveWebSessionTimeout, ElementBounds.Fixed(440.0, 424.0, 84.0, 28.0))
					.AddStaticText(SuiteLocalization.Get("gui.securecookies"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 468.0, 130.0, 22.0))
					.AddSwitch((bool value11) =>
					{
						SetWebBool("securecookies", value11);
					}, ElementBounds.Fixed(184.0, 462.0, 52.0, 26.0), "web-secure-cookies")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.50061c51de"), CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(184.0, 462.0, 52.0, 26.0), "tip-web-secure")
					.AddRichtext(SuiteLocalization.Get("gui.setreplacepassword"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 508.0, 520.0, 48.0))
					.AddStaticText(webLocalAnonymousMode ? "Loopback-only anonymous mode is active. Set a password before binding to a LAN or public interface." : "Password authentication is configured. Bootstrap Password in NewfiesWebServer.json is hashed and cleared after reload.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(52.0, 566.0, 548.0, 70.0))
					.AddStaticText(SuiteLocalization.Get("gui.endpointssecurity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(690.0, 358.0, 260.0, 22.0))
					.AddRichtext(SuiteLocalization.Get("gui.localendpoint"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 392.0, 150.0, 22.0))
					.AddStaticText(webLocalUrl, CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 418.0, 560.0, 28.0))
					.AddRichtext(SuiteLocalization.Get("gui.publicendpoint"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 460.0, 150.0, 22.0))
					.AddStaticText(string.IsNullOrWhiteSpace(webPublicUrl) ? SuiteLocalization.Get("gui.notconfigured") : webPublicUrl, CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 486.0, 560.0, 42.0))
					.AddRichtext(webSecureCookies ? "<font color=\"#8fda72\"><strong>HTTPS cookie protection enabled.</strong></font>" : "<font color=\"#ffd166\"><strong>Secure cookies are off.</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 542.0, 520.0, 24.0))
					.AddStaticText(SuiteLocalization.Get("gui.httpsecuritywarning"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 576.0, 548.0, 58.0));
				if (!string.IsNullOrWhiteSpace(webLastError))
				{
					guiComposer.AddRichtext("<font color=\"#ff7070\"><strong>Last error:</strong> " + Escape(webLastError) + "</font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(694.0, 642.0, 548.0, 22.0));
				}
				num4 = 0.0;
				val = 0.0;
			}
		}
		else if (section == "loginprotection")
		{
			ElementBounds elementBounds18 = ElementBounds.Fixed(22.0, 82.0, 100.0, 30.0);
			ElementBounds elementBounds19 = ElementBounds.Fixed(136.0, 82.0, 110.0, 30.0);
			guiComposer.AddSuiteButton(SuiteLocalization.Get("gui.statusplain"), () => SelectLoginProtectionView("status"), elementBounds18, loginView == "status").AddHoverText(SuiteLocalization.Get("gui.tip.more.d7c5e4f976"), CairoFont.WhiteSmallText(), 350, elementBounds18.FlatCopy(), "tip-login-tab-status").AddSuiteButton(SuiteLocalization.Get("gui.settings"), () => SelectLoginProtectionView("settings"), elementBounds19, loginView == "settings")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.ec4f7d425d"), CairoFont.WhiteSmallText(), 380, elementBounds19.FlatCopy(), "tip-login-tab-settings");
			if (loginView == "settings")
			{
				flag2 = false;
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 618.0)).AddRichtext(SuiteLocalization.Get("gui.loginsettings"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 136.0, 390.0, 26.0), "page-header").AddStaticText(SuiteLocalization.Get("gui.loginsettingsimmediate"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(450.0, 138.0, 590.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.loginenabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 180.0, 195.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("enabled", value11);
					}, ElementBounds.Fixed(244.0, 173.0, 60.0, 30.0), "login-enabled")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.e086e1a075"), CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(244.0, 173.0, 60.0, 30.0), "tip-login-enabled")
					.AddRichtext($"<strong>Currently protected:</strong> {loginActiveProtectedCount}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(350.0, 180.0, 240.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.protection"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 225.0, 220.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(40.0, 250.0, 980.0, 2.0))
					.AddRichtext(SuiteLocalization.Get("gui.durationseconds"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 270.0, 145.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(192.0, 265.0, 105.0, 30.0), OnLoginProtectionSecondsChanged, CairoFont.WhiteSmallText(), "login-seconds")
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveLoginProtectionSeconds, ElementBounds.Fixed(309.0, 265.0, 84.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.30ec458fbb"), CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(192.0, 265.0, 105.0, 30.0), "tip-login-seconds")
					.AddStaticText(SuiteLocalization.Get("gui.literal.6fbdb570c7"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 316.0, 145.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("protectonjoin", value11);
					}, ElementBounds.Fixed(192.0, 309.0, 60.0, 30.0), "login-join")
					.AddStaticText(SuiteLocalization.Get("gui.literal.e6740dca48"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(350.0, 316.0, 155.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("protectonrespawn", value11);
					}, ElementBounds.Fixed(512.0, 309.0, 60.0, 30.0), "login-respawn")
					.AddStaticText(SuiteLocalization.Get("gui.literal.988a176e0c"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 358.0, 175.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("announceonjoin", value11);
					}, ElementBounds.Fixed(222.0, 351.0, 60.0, 30.0), "login-announce-start")
					.AddStaticText(SuiteLocalization.Get("gui.literal.4861beee2a"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(350.0, 358.0, 170.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("announceonend", value11);
					}, ElementBounds.Fixed(526.0, 351.0, 60.0, 30.0), "login-announce-end")
					.AddRichtext(SuiteLocalization.Get("gui.antiabuse"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 414.0, 280.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(40.0, 439.0, 980.0, 2.0))
					.AddStaticText(SuiteLocalization.Get("gui.literal.0cd64ebaf0"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 462.0, 180.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("blockpvp", value11);
					}, ElementBounds.Fixed(226.0, 455.0, 60.0, 30.0), "login-block-pvp")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.72ab6b60b7"), CairoFont.WhiteSmallText(), 360, ElementBounds.Fixed(226.0, 455.0, 60.0, 30.0), "tip-login-pvp")
					.AddStaticText(SuiteLocalization.Get("gui.literal.d5301154dd"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(350.0, 462.0, 190.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("cancelonattack", value11);
					}, ElementBounds.Fixed(548.0, 455.0, 60.0, 30.0), "login-cancel-attack")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.7d98eccc9d"), CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(548.0, 455.0, 60.0, 30.0), "tip-login-cancel-attack")
					.AddStaticText(SuiteLocalization.Get("gui.literal.d28221cfe4"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 504.0, 150.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("protectfalldamage", value11);
					}, ElementBounds.Fixed(196.0, 497.0, 60.0, 30.0), "login-fall")
					.AddStaticText(SuiteLocalization.Get("gui.literal.58b47c10a3"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(350.0, 504.0, 205.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("extinguishfire", value11);
					}, ElementBounds.Fixed(562.0, 497.0, 60.0, 30.0), "login-fire")
					.AddStaticText(SuiteLocalization.Get("gui.literal.d84ddc92d7"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 546.0, 185.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetLoginProtectionBool("recoverswimmingonlogin", value11);
					}, ElementBounds.Fixed(232.0, 539.0, 60.0, 30.0), "login-water-recovery")
					.AddHoverText("If a player reconnects while in liquid, restore server-side swimming/detached movement once at login so normal liquid physics takes over cleanly.", CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(232.0, 539.0, 60.0, 30.0), "tip-login-water-recovery")
					.AddRichtext(SuiteLocalization.Get("gui.behavior"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 592.0, 220.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(40.0, 617.0, 980.0, 2.0))
					.AddStaticText("Protection is UID-authoritative and is armed as early as PlayerJoin, then revalidated at PlayerNowPlaying and PlayerReady when available. A lightweight guard keeps the current player entity bound to the protection behavior while the shield is active; water-login recovery remains bounded.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 634.0, 835.0, 54.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.refreshsettings"), () => RequestLoginProtection("settings"), ElementBounds.Fixed(886.0, 648.0, 150.0, 32.0));
				num4 = 682.0;
				val = 1.0;
			}
			else
			{
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 618.0)).AddRichtext("<strong>" + pageHeaderText + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 136.0, 650.0, 26.0), "page-header").AddSuiteButton(SuiteLocalization.Get("gui.literal.28a0e95aff"), () => RequestLoginProtection("status"), ElementBounds.Fixed(920.0, 132.0, 150.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.4db1f0411e"), CairoFont.WhiteSmallText(), 380, ElementBounds.Fixed(920.0, 132.0, 150.0, 30.0), "tip-login-status-refresh");
				num4 = 170.0;
				val = 550.0;
			}
		}
		else if (section == "profiler")
		{
			flag2 = false;
			num4 = 0.0;
			val = 0.0;
			ElementBounds elementBounds20 = ElementBounds.Fixed(22.0, 82.0, 112.0, 30.0);
			ElementBounds elementBounds21 = ElementBounds.Fixed(148.0, 82.0, 96.0, 30.0);
			ElementBounds elementBounds22 = ElementBounds.Fixed(258.0, 82.0, 132.0, 30.0);
			ElementBounds elementBounds23 = ElementBounds.Fixed(404.0, 82.0, 132.0, 30.0);
			ElementBounds elementBounds24 = ElementBounds.Fixed(550.0, 82.0, 120.0, 30.0);
			ElementBounds elementBounds25 = ElementBounds.Fixed(684.0, 82.0, 132.0, 30.0);
			ElementBounds elementBounds26 = ElementBounds.Fixed(830.0, 82.0, 132.0, 30.0);
			ElementBounds elementBounds27 = ElementBounds.Fixed(976.0, 82.0, 190.0, 30.0);
			ElementBounds elementBounds28 = ElementBounds.Fixed(842.0, 132.0, 104.0, 30.0);
			ElementBounds elementBounds29 = ElementBounds.Fixed(960.0, 132.0, 126.0, 30.0);
			ElementBounds elementBounds30 = ElementBounds.Fixed(1100.0, 132.0, 126.0, 30.0);
			string text2 = BuildProfilerStateText();
			guiComposer.AddSuiteButton(SuiteLocalization.Get("gui.overview"), () => SelectProfilerView("overview"), elementBounds20, profilerView == "overview").AddHoverText(SuiteLocalization.Get("gui.tip.profiler.overview"), CairoFont.WhiteSmallText(), 320, elementBounds20.FlatCopy(), "tip-profiler-overview").AddSuiteButton(SuiteLocalization.Get("gui.mods"), () => SelectProfilerView("mods"), elementBounds21, profilerView == "mods")
				.AddHoverText(SuiteLocalization.Get("gui.tip.profiler.mods"), CairoFont.WhiteSmallText(), 350, elementBounds21.FlatCopy(), "tip-profiler-mods")
				.AddSuiteButton(SuiteLocalization.Get("gui.tickphases"), () => SelectProfilerView("phases"), elementBounds22, profilerView == "phases")
				.AddHoverText(SuiteLocalization.Get("gui.tip.profiler.phases"), CairoFont.WhiteSmallText(), 390, elementBounds22.FlatCopy(), "tip-profiler-phases")
				.AddSuiteButton(SuiteLocalization.Get("gui.slowticks"), () => SelectProfilerView("slowticks"), elementBounds23, profilerView == "slowticks")
				.AddHoverText(SuiteLocalization.Get("gui.tip.profiler.slowticks"), CairoFont.WhiteSmallText(), 410, elementBounds23.FlatCopy(), "tip-profiler-slowticks")
				.AddSuiteButton(SuiteLocalization.Get("gui.hotspots"), () => SelectProfilerView("hotspots"), elementBounds24, profilerView == "hotspots")
				.AddHoverText(SuiteLocalization.Get("gui.tip.profiler.hotspots"), CairoFont.WhiteSmallText(), 410, elementBounds24.FlatCopy(), "tip-profiler-hotspots")
				.AddSuiteButton(SuiteLocalization.Get("gui.offthread"), () => SelectProfilerView("offthread"), elementBounds25, profilerView == "offthread")
				.AddHoverText(SuiteLocalization.Get("gui.tip.profiler.offthread"), CairoFont.WhiteSmallText(), 420, elementBounds25.FlatCopy(), "tip-profiler-offthread")
				.AddSuiteButton(SuiteLocalization.Get("gui.lagevents"), () => SelectProfilerView("lagevents"), elementBounds26, profilerView == "lagevents")
				.AddHoverText(SuiteLocalization.Get("gui.tip.profiler.lagevents"), CairoFont.WhiteSmallText(), 440, elementBounds26.FlatCopy(), "tip-profiler-lag-events")
				.AddSuiteButton(profilerLiveUpdates ? "Live Updates: ON" : "Live Updates: OFF", ToggleProfilerLiveUpdates, elementBounds27, profilerLiveUpdates)
				.AddHoverText("When enabled, refresh this Profiler page every 2 seconds. This does not enable the expensive engine profiler or create another server sampler.", CairoFont.WhiteSmallText(), 450, elementBounds27.FlatCopy(), "tip-profiler-live")
				.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 618.0))
				.AddRichtext(SuiteLocalization.Get("gui.profilerdiag"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 136.0, 450.0, 26.0), "page-header")
				.AddRichtext(SuiteLocalization.Get("gui.profilerenabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(500.0, 138.0, 125.0, 24.0))
				.AddSwitch(SetProfilerEnabled, ElementBounds.Fixed(628.0, 130.0, 60.0, 30.0), "profiler-enabled")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.cb0ce573a0"), CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(628.0, 130.0, 60.0, 30.0), "tip-profiler-enabled")
				.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RequestProfilerSnapshot, elementBounds28)
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.83687144bb"), CairoFont.WhiteSmallText(), 370, elementBounds28.FlatCopy(), "tip-profiler-refresh")
				.AddSuiteButton(SuiteLocalization.Get("gui.profile10"), () => StartProfilerCapture(10), elementBounds29)
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.0dd9da1f7c"), CairoFont.WhiteSmallText(), 430, elementBounds29.FlatCopy(), "tip-profiler-10")
				.AddSuiteButton(SuiteLocalization.Get("gui.profile30"), () => StartProfilerCapture(30), elementBounds30)
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.48382235a5"), CairoFont.WhiteSmallText(), 410, elementBounds30.FlatCopy(), "tip-profiler-30")
				.AddDynamicText(SuiteProfilerTextElement.FitUiLine(SuiteLocalization.Get("gui.statusvalue", profilerMessage), 1180.0), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 166.0, 1180.0, 24.0), "profiler-live-status")
				.AddDynamicText(SuiteProfilerTextElement.FitUiLine(text2, 760.0), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 190.0, 760.0, 26.0), "profiler-live-state")
				.AddSuiteButton(SuiteLocalization.Get("gui.localweb"), OpenLocalProfilerWeb, ElementBounds.Fixed(842.0, 184.0, 156.0, 30.0))
				.AddHoverText("Open the built-in loopback dashboard. This works when the game client is running on the same machine as the server.", CairoFont.WhiteSmallText(), 430, ElementBounds.Fixed(842.0, 184.0, 156.0, 30.0), "tip-profiler-local-web")
				.AddSuiteButton(string.IsNullOrWhiteSpace(webPublicUrl) ? "Public: Not Set" : "Public Web", OpenPublicProfilerWeb, ElementBounds.Fixed(1024.0, 184.0, 166.0, 30.0))
				.AddHoverText(string.IsNullOrWhiteSpace(webPublicUrl) ? "Configure PublicUrl in Web Server settings or with /serverutils web publicurl <https://...>." : "Open the configured public/reverse-proxy dashboard URL.", CairoFont.WhiteSmallText(), 450, ElementBounds.Fixed(1024.0, 184.0, 166.0, 30.0), "tip-profiler-public-web");
			if (profilerView == "mods")
			{
				double num5 = profilerLastProfile.AttributedMs + profilerLastProfile.UnattributedMs;
				double value = ((num5 <= 0.0) ? 0.0 : (profilerLastProfile.AttributedMs * 100.0 / num5));
				guiComposer.AddProfilerRichText($"<strong>Mod attribution</strong> — {profilerLoadedModCount} loaded mods; time attribution coverage <strong>{value:0}%</strong>. " + "High/Medium confidence comes from mod IDs, asset domains, assemblies, namespaces, or ModSystem types.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(36.0, 224.0, 1025.0, 40.0)).AddSuiteInset(ElementBounds.Fixed(30.0, 268.0, 1040.0, 32.0)).AddProfilerRichText(SuiteLocalization.Get("gui.modidname"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 274.0, 300.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.literal.1947566e2b"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(350.0, 274.0, 70.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.literal.3ae2469ed0"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(430.0, 274.0, 70.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.calls"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(510.0, 274.0, 62.0, 24.0))
					.AddProfilerRichText("<strong>≥10ms</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(578.0, 274.0, 62.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.share"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(646.0, 274.0, 68.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.literal.5b7820560e"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(720.0, 274.0, 96.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.topmarker"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(822.0, 274.0, 238.0, 24.0));
				if (profilerModHotspots.Count == 0)
				{
					guiComposer.AddProfilerText("Run a 10s or 30s profile to build the per-mod attribution table.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 314.0, 900.0, 24.0));
				}
				else
				{
					int num6 = Math.Min(15, profilerModHotspots.Count);
					for (int num7 = 0; num7 < num6; num7++)
					{
						ProfilerModHotspotPacket profilerModHotspotPacket = profilerModHotspots[num7];
						double num8 = 306 + num7 * 27;
						if ((num7 & 1) == 1)
						{
							guiComposer.AddSuiteInset(ElementBounds.Fixed(32.0, num8 - 2.0, 1036.0, 27.0));
						}
						string text3;
						if (profilerModHotspotPacket.ModId == "<unattributed>")
						{
							text3 = "<font color=\"#ffad66\"><strong>Unattributed</strong></font>";
						}
						else
						{
							text3 = (profilerModHotspotPacket.ModId.Equals("newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase) ? ("<strong>" + Escape(profilerModHotspotPacket.ModId) + "</strong> — utility/profiler self") : ("<strong>" + Escape(profilerModHotspotPacket.ModId) + "</strong> — " + Escape(profilerModHotspotPacket.ModName)));
						}
						string text4 = ProfilerConfidenceMarkup(profilerModHotspotPacket.Confidence);
						guiComposer.AddProfilerRichText(text3, CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, num8, 300.0, 24.0)).AddProfilerText($"{profilerModHotspotPacket.TotalMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(350.0, num8, 72.0, 24.0)).AddProfilerText($"{profilerModHotspotPacket.MaxSingleMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(430.0, num8, 72.0, 24.0))
							.AddProfilerText($"{profilerModHotspotPacket.Calls:N0}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(510.0, num8, 62.0, 24.0))
							.AddProfilerText($"{profilerModHotspotPacket.SlowSegments:N0}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(578.0, num8, 62.0, 24.0))
							.AddProfilerText($"{profilerModHotspotPacket.PercentOfProfileTickTime:0.0}%", CairoFont.WhiteSmallText(), ElementBounds.Fixed(646.0, num8, 68.0, 24.0))
							.AddProfilerRichText(text4, CairoFont.WhiteSmallText(), ElementBounds.Fixed(720.0, num8, 96.0, 24.0))
							.AddProfilerText(profilerModHotspotPacket.TopMarker, CairoFont.WhiteSmallText(), ElementBounds.Fixed(822.0, num8, 238.0, 24.0));
					}
				}
				guiComposer.AddProfilerText("Attribution is evidence-based, not guessed: generic engine markers remain Unattributed. A mod that adds its own FrameProfiler.Mark/Enter points will be identified much more precisely.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 718.0, 1010.0, 24.0));
			}
			else if (profilerView == "phases")
			{
				guiComposer.AddProfilerRichText("<strong>Tick phase breakdown</strong> — root-level FrameProfiler marks/ranges from the captured server ticks. Rows are inclusive profiler phases and can overlap, so their percentages are diagnostic rather than expected to sum to 100%.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(36.0, 224.0, 1020.0, 40.0)).AddSuiteInset(ElementBounds.Fixed(30.0, 268.0, 1040.0, 32.0)).AddProfilerRichText(SuiteLocalization.Get("gui.tickmarker"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 274.0, 430.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.avgtick"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(480.0, 274.0, 82.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.max"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(570.0, 274.0, 72.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.total"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(650.0, 274.0, 72.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.share"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(730.0, 274.0, 72.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.literal.b6d3b7123b"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(812.0, 274.0, 238.0, 24.0));
				if (profilerPhases.Count == 0)
				{
					guiComposer.AddProfilerText("Run a manual profile to capture the server tick phase breakdown.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 314.0, 900.0, 24.0));
				}
				else
				{
					int num9 = Math.Min(15, profilerPhases.Count);
					for (int num10 = 0; num10 < num9; num10++)
					{
						ProfilerPhasePacket profilerPhasePacket = profilerPhases[num10];
						double num11 = 306 + num10 * 27;
						if ((num10 & 1) == 1)
						{
							guiComposer.AddSuiteInset(ElementBounds.Fixed(32.0, num11 - 2.0, 1036.0, 27.0));
						}
						string text5 = (string.IsNullOrWhiteSpace(profilerPhasePacket.ModId) ? "engine / unattributed" : (profilerPhasePacket.ModId + " (" + profilerPhasePacket.AttributionConfidence + ")"));
						guiComposer.AddProfilerRichText("<strong>" + Escape(profilerPhasePacket.Code) + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, num11, 430.0, 24.0)).AddProfilerText($"{profilerPhasePacket.AveragePerTickMs:0.00}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(480.0, num11, 82.0, 24.0)).AddProfilerText($"{profilerPhasePacket.MaxSingleMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(570.0, num11, 72.0, 24.0))
							.AddProfilerText($"{profilerPhasePacket.TotalMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(650.0, num11, 72.0, 24.0))
							.AddProfilerText($"{profilerPhasePacket.PercentOfProfileTickTime:0.0}%", CairoFont.WhiteSmallText(), ElementBounds.Fixed(730.0, num11, 72.0, 24.0))
							.AddProfilerText(text5, CairoFont.WhiteSmallText(), ElementBounds.Fixed(812.0, num11, 238.0, 24.0));
					}
				}
			}
			else if (profilerView == "slowticks")
			{
				guiComposer.AddProfilerRichText("<strong>Worst profiled ticks</strong> — ticks ≥50ms captured during the last manual profile, sorted slowest first with up to four leading leaf-work causes.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(36.0, 224.0, 1020.0, 40.0)).AddSuiteInset(ElementBounds.Fixed(30.0, 268.0, 1040.0, 32.0)).AddProfilerRichText(SuiteLocalization.Get("gui.literal.4ecb92ecf9"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 274.0, 82.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.tick"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(130.0, 274.0, 85.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.leadingcauses"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(225.0, 274.0, 830.0, 24.0));
				if (profilerProfileSlowTicks.Count == 0)
				{
					guiComposer.AddProfilerText("No ≥50ms ticks were captured in the last profile, or no profile has been run yet.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 314.0, 950.0, 24.0));
				}
				else
				{
					int num12 = Math.Min(13, profilerProfileSlowTicks.Count);
					for (int num13 = 0; num13 < num12; num13++)
					{
						ProfilerProfileSlowTickPacket profilerProfileSlowTickPacket = profilerProfileSlowTicks[num13];
						double num14 = 306 + num13 * 31;
						if ((num13 & 1) == 1)
						{
							guiComposer.AddSuiteInset(ElementBounds.Fixed(32.0, num14 - 2.0, 1036.0, 31.0));
						}
						string text6 = DateTimeOffset.FromUnixTimeMilliseconds(profilerProfileSlowTickPacket.UnixMilliseconds).ToLocalTime().ToString("HH:mm:ss");
						guiComposer.AddProfilerRichText("<strong>" + text6 + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, num14, 82.0, 24.0)).AddProfilerRichText($"<font color=\"#ff8080\"><strong>{profilerProfileSlowTickPacket.TickMs:0.0}ms</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(130.0, num14, 85.0, 24.0)).AddProfilerText(profilerProfileSlowTickPacket.TopCauseSummary, CairoFont.WhiteSmallText(), ElementBounds.Fixed(225.0, num14, 830.0, 28.0));
					}
				}
			}
			else if (profilerView == "lagevents")
			{
				string value2;
				if (profilerDiagnosticSummary.State == "Investigate")
				{
					value2 = "#ff7070";
				}
				else
				{
					value2 = ((profilerDiagnosticSummary.State == "Watch") ? "#ffd166" : "#8fda72");
				}
				guiComposer.AddProfilerRichText($"<strong>5-minute diagnostic state:</strong> <font color=\"{value2}\"><strong>{Escape(profilerDiagnosticSummary.State)}</strong></font>   <strong>Worst scheduler delay:</strong> {profilerDiagnosticSummary.WorstDelayMs:0.0}ms   <strong>Avg/Max CPU:</strong> {profilerDiagnosticSummary.AverageCpuPercent:0.0}% / {profilerDiagnosticSummary.MaxCpuPercent:0.0}%   <strong>Gen2:</strong> +{profilerDiagnosticSummary.Gen2Collections}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(36.0, 224.0, 1180.0, 26.0)).AddProfilerRichText("<strong>Evidence:</strong> " + Escape(profilerDiagnosticSummary.Summary), CairoFont.WhiteSmallText(), ElementBounds.Fixed(36.0, 250.0, 1180.0, 48.0)).AddSuiteInset(ElementBounds.Fixed(30.0, 306.0, 1200.0, 32.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.time"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 312.0, 78.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.literal.a37f4283db"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(126.0, 312.0, 82.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.delay"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(218.0, 312.0, 70.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.cpu"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(296.0, 312.0, 58.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.gc012"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(362.0, 312.0, 92.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.blqueuewriter"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(464.0, 312.0, 156.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.entitiesdelta"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(630.0, 312.0, 100.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.chunksdelta"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(740.0, 312.0, 100.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.evidencecorr"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(850.0, 312.0, 360.0, 24.0));
				if (profilerDiagnosticIncidents.Count == 0)
				{
					guiComposer.AddProfilerText("No correlated lag events recorded in the current rolling diagnostic history.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 352.0, 1120.0, 24.0));
				}
				else
				{
					int num15 = Math.Min(13, profilerDiagnosticIncidents.Count);
					for (int num16 = 0; num16 < num15; num16++)
					{
						ProfilerDiagnosticIncidentPacket profilerDiagnosticIncidentPacket = profilerDiagnosticIncidents[num16];
						double num17 = 344 + num16 * 29;
						if ((num16 & 1) == 1)
						{
							guiComposer.AddSuiteInset(ElementBounds.Fixed(32.0, num17 - 2.0, 1196.0, 29.0));
						}
						string text7 = DateTimeOffset.FromUnixTimeMilliseconds(profilerDiagnosticIncidentPacket.UnixMilliseconds).ToLocalTime().ToString("HH:mm:ss");
						string value3;
						if (profilerDiagnosticIncidentPacket.Severity == "Critical")
						{
							value3 = "#ff7070";
						}
						else
						{
							value3 = ((profilerDiagnosticIncidentPacket.Severity == "High") ? "#ffad66" : "#ffd166");
						}
						guiComposer.AddProfilerRichText("<strong>" + text7 + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, num17, 78.0, 24.0)).AddProfilerRichText($"<font color=\"{value3}\"><strong>{Escape(profilerDiagnosticIncidentPacket.Severity)}</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(126.0, num17, 82.0, 24.0)).AddProfilerText($"{profilerDiagnosticIncidentPacket.DelayMs:0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(218.0, num17, 70.0, 24.0))
							.AddProfilerText($"{profilerDiagnosticIncidentPacket.CpuPercent:0}%", CairoFont.WhiteSmallText(), ElementBounds.Fixed(296.0, num17, 58.0, 24.0))
							.AddProfilerText($"+{profilerDiagnosticIncidentPacket.Gen0Delta}/+{profilerDiagnosticIncidentPacket.Gen1Delta}/+{profilerDiagnosticIncidentPacket.Gen2Delta}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(362.0, num17, 92.0, 24.0))
							.AddProfilerText($"{profilerDiagnosticIncidentPacket.BlockLoggerQueue:N0} / {profilerDiagnosticIncidentPacket.BlockLoggerWriterMs}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(464.0, num17, 156.0, 24.0))
							.AddProfilerText($"{profilerDiagnosticIncidentPacket.LoadedEntities:N0} ({profilerDiagnosticIncidentPacket.EntityDelta:+#;-#;0})", CairoFont.WhiteSmallText(), ElementBounds.Fixed(630.0, num17, 100.0, 24.0))
							.AddProfilerText($"{profilerDiagnosticIncidentPacket.LoadedChunks:N0} ({profilerDiagnosticIncidentPacket.ChunkDelta:+#;-#;0})", CairoFont.WhiteSmallText(), ElementBounds.Fixed(740.0, num17, 100.0, 24.0))
							.AddProfilerText(profilerDiagnosticIncidentPacket.Evidence, CairoFont.WhiteSmallText(), ElementBounds.Fixed(850.0, num17, 360.0, 24.0));
					}
				}
				guiComposer.AddProfilerText("Lag Events are evidence correlations, not automatic blame. Scheduler delay is callback lateness; use a manual 10s/30s profile for exact tick timing and mod attribution.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 718.0, 1170.0, 24.0));
			}
			else if (profilerView == "offthread")
			{
				guiComposer.AddProfilerRichText("<strong>Off-thread slow reports</strong> — Vintage Story worker-thread slow profiles captured during the manual window (50ms threshold unless the server already had a stricter threshold). These can expose physics, worldgen, or background work.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(36.0, 224.0, 1020.0, 40.0)).AddSuiteInset(ElementBounds.Fixed(30.0, 268.0, 1040.0, 32.0)).AddProfilerRichText(SuiteLocalization.Get("gui.durationlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 274.0, 85.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.literal.764355d4a7"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(132.0, 274.0, 170.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.confidence"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(310.0, 274.0, 100.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.workerprofilerreport"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(420.0, 274.0, 635.0, 24.0));
				if (profilerOffThreadReports.Count == 0)
				{
					guiComposer.AddProfilerText("No worker-thread slow reports were produced during the last manual profile.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 314.0, 950.0, 24.0));
				}
				else
				{
					int num18 = Math.Min(14, profilerOffThreadReports.Count);
					for (int num19 = 0; num19 < num18; num19++)
					{
						ProfilerOffThreadReportPacket profilerOffThreadReportPacket = profilerOffThreadReports[num19];
						double num20 = 306 + num19 * 29;
						if ((num19 & 1) == 1)
						{
							guiComposer.AddSuiteInset(ElementBounds.Fixed(32.0, num20 - 2.0, 1196.0, 29.0));
						}
						string text8 = (string.IsNullOrWhiteSpace(profilerOffThreadReportPacket.ModId) ? "unattributed" : profilerOffThreadReportPacket.ModId);
						guiComposer.AddProfilerRichText($"<font color=\"#ff8080\"><strong>{profilerOffThreadReportPacket.DurationMs:0.0}ms</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, num20, 85.0, 24.0)).AddProfilerText(text8, CairoFont.WhiteSmallText(), ElementBounds.Fixed(132.0, num20, 170.0, 24.0)).AddProfilerRichText(ProfilerConfidenceMarkup(profilerOffThreadReportPacket.Confidence), CairoFont.WhiteSmallText(), ElementBounds.Fixed(310.0, num20, 100.0, 24.0))
							.AddProfilerText(profilerOffThreadReportPacket.Summary, CairoFont.WhiteSmallText(), ElementBounds.Fixed(420.0, num20, 635.0, 24.0));
					}
				}
			}
			else if (profilerView == "hotspots")
			{
				guiComposer.AddProfilerRichText("<strong>Leaf work hotspots</strong> — deepest profiler markers/ranges are ranked without also charging their parent ranges, which avoids the old parent/child double-counting problem.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(36.0, 224.0, 1020.0, 40.0)).AddSuiteInset(ElementBounds.Fixed(30.0, 268.0, 1040.0, 32.0)).AddProfilerRichText(SuiteLocalization.Get("gui.profilerpathwork"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 274.0, 470.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.mod"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(520.0, 274.0, 155.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.total"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(682.0, 274.0, 72.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.max"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(762.0, 274.0, 72.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.calls"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(842.0, 274.0, 72.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.share"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(922.0, 274.0, 70.0, 24.0))
					.AddProfilerRichText(SuiteLocalization.Get("gui.confshort"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(996.0, 274.0, 60.0, 24.0));
				if (profilerHotspots.Count == 0)
				{
					guiComposer.AddProfilerText("Run a manual profile to collect leaf-work hotspots.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 314.0, 900.0, 24.0));
				}
				else
				{
					int num21 = Math.Min(15, profilerHotspots.Count);
					for (int num22 = 0; num22 < num21; num22++)
					{
						ProfilerHotspotPacket profilerHotspotPacket = profilerHotspots[num22];
						double num23 = 306 + num22 * 27;
						if ((num22 & 1) == 1)
						{
							guiComposer.AddSuiteInset(ElementBounds.Fixed(32.0, num23 - 2.0, 1036.0, 27.0));
						}
						string text9 = (string.IsNullOrWhiteSpace(profilerHotspotPacket.ModId) ? "—" : profilerHotspotPacket.ModId);
						guiComposer.AddProfilerRichText("<strong>" + Escape(profilerHotspotPacket.Code) + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, num23, 470.0, 24.0)).AddProfilerText(text9, CairoFont.WhiteSmallText(), ElementBounds.Fixed(520.0, num23, 155.0, 24.0)).AddProfilerText($"{profilerHotspotPacket.TotalMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(682.0, num23, 72.0, 24.0))
							.AddProfilerText($"{profilerHotspotPacket.MaxSingleMs:0.0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(762.0, num23, 72.0, 24.0))
							.AddProfilerText($"{profilerHotspotPacket.Calls:N0}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(842.0, num23, 72.0, 24.0))
							.AddProfilerText($"{profilerHotspotPacket.PercentOfProfileTickTime:0.0}%", CairoFont.WhiteSmallText(), ElementBounds.Fixed(922.0, num23, 70.0, 24.0))
							.AddProfilerRichText(ProfilerConfidenceMarkup(profilerHotspotPacket.AttributionConfidence), CairoFont.WhiteSmallText(), ElementBounds.Fixed(996.0, num23, 62.0, 24.0));
					}
				}
			}
			else
			{
				guiComposer.AddDynamicText(BuildProfilerCurrentPrimaryText(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 220.0, 1020.0, 24.0), "profiler-live-current-primary").AddDynamicText(BuildProfilerCurrentSecondaryText(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 244.0, 1020.0, 24.0), "profiler-live-current-secondary").AddRichtext("<strong>5m diagnosis:</strong> " + Escape(profilerDiagnosticSummary.State) + " — " + Escape(ShortProfilerCode(profilerDiagnosticSummary.Summary, 118)), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 266.0, 1020.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.mainthreaddelay"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(32.0, 292.0, 320.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.processcpu"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(382.0, 292.0, 320.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.literal.eedf82d87d"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(732.0, 292.0, 320.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(32.0, 316.0, 330.0, 136.0))
					.AddStaticText(ProfilerMetricSummary((ProfilerSamplePacket p) => p.MainThreadDelayMs, "ms"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(46.0, 328.0, 300.0, 112.0))
					.AddSuiteInset(ElementBounds.Fixed(382.0, 316.0, 330.0, 136.0))
					.AddStaticText(ProfilerMetricSummary((ProfilerSamplePacket p) => p.CpuPercent, "%"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(396.0, 328.0, 300.0, 112.0))
					.AddSuiteInset(ElementBounds.Fixed(732.0, 316.0, 330.0, 136.0))
					.AddStaticText(ProfilerMetricSummary((ProfilerSamplePacket p) => p.ManagedHeapMb, " MB"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(746.0, 328.0, 300.0, 112.0))
					.AddSuiteInset(ElementBounds.Fixed(28.0, 472.0, 520.0, 252.0))
					.AddRichtext(SuiteLocalization.Get("gui.recentstalls"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 484.0, 480.0, 24.0))
					.AddStaticText(SuiteLocalization.Get("gui.lightmonitorcontext"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 508.0, 480.0, 22.0))
					.AddSuiteInset(ElementBounds.Fixed(560.0, 472.0, 520.0, 252.0))
					.AddRichtext(SuiteLocalization.Get("gui.problemmod"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(572.0, 484.0, 480.0, 24.0))
					.AddStaticText("Run a manual profile, then use Mods / Tick Phases / Slow Ticks / Hotspots / Off-thread.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(572.0, 508.0, 480.0, 38.0));
				if (profilerSlowSpikes.Count == 0)
				{
					guiComposer.AddStaticText("No ≥100ms scheduler stalls recorded in the rolling history.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 540.0, 480.0, 24.0));
				}
				else
				{
					int num24 = Math.Min(7, profilerSlowSpikes.Count);
					for (int num25 = 0; num25 < num24; num25++)
					{
						ProfilerSlowSpikePacket profilerSlowSpikePacket = profilerSlowSpikes[num25];
						string value4 = DateTimeOffset.FromUnixTimeMilliseconds(profilerSlowSpikePacket.UnixMilliseconds).ToLocalTime().ToString("HH:mm:ss");
						guiComposer.AddRichtext($"<strong>{value4}</strong>  <font color=\"#ff8080\"><strong>{profilerSlowSpikePacket.DelayMs:0}ms</strong></font>  CPU {profilerSlowSpikePacket.CpuPercent:0}%  entities {profilerSlowSpikePacket.LoadedEntities:N0}  GC +{profilerSlowSpikePacket.Gen0Delta}/+{profilerSlowSpikePacket.Gen1Delta}/+{profilerSlowSpikePacket.Gen2Delta}  BL {profilerSlowSpikePacket.BlockLoggerQueue:N0}/{profilerSlowSpikePacket.BlockLoggerWriterMs:N0}ms", CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 540 + num25 * 25, 492.0, 24.0));
					}
				}
				double num26 = profilerLastProfile.AttributedMs + profilerLastProfile.UnattributedMs;
				double value5 = ((num26 <= 0.0) ? 0.0 : (profilerLastProfile.AttributedMs * 100.0 / num26));
				ProfilerModHotspotPacket profilerModHotspotPacket2 = profilerModHotspots.FirstOrDefault((ProfilerModHotspotPacket x) => !x.IsCore && x.ModId != "<unattributed>" && !x.ModId.Equals("newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase));
				string value6 = ((profilerModHotspotPacket2 == null) ? "(none yet)" : (Escape(profilerModHotspotPacket2.ModId) + " — " + profilerModHotspotPacket2.TotalMs.ToString("0.0") + "ms"));
				guiComposer.AddRichtext($"<strong>Loaded mods:</strong> {profilerLoadedModCount}<br><strong>Attribution coverage:</strong> {value5:0}% of captured leaf time<br><strong>Top non-core attribution:</strong> {value6}<br><strong>Profiled slow ticks:</strong> {profilerProfileSlowTicks.Count}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(572.0, 558.0, 480.0, 112.0));
			}
		}
		else
		{
			ElementBounds elementBounds31 = ElementBounds.Fixed(22.0, 82.0, 100.0, 30.0);
			ElementBounds elementBounds32 = ElementBounds.Fixed(136.0, 82.0, 140.0, 30.0);
			ElementBounds elementBounds33 = ElementBounds.Fixed(290.0, 82.0, 90.0, 30.0);
			ElementBounds elementBounds34 = ElementBounds.Fixed(394.0, 82.0, 110.0, 30.0);
			ElementBounds elementBounds35 = ElementBounds.Fixed(518.0, 82.0, 150.0, 30.0);
			guiComposer.AddSuiteButton("Limiter", () => SelectAnimalView("limiter"), elementBounds31, animalView == "limiter").AddHoverText(SuiteLocalization.Get("gui.tip.more.5f42cd52fb"), CairoFont.WhiteSmallText(), 340, elementBounds31.FlatCopy(), "tip-animal-tab-limiter").AddSuiteButton(SuiteLocalization.Get("gui.population"), () => SelectAnimalView("population"), elementBounds32, animalView == "population")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.4606e4ee1a"), CairoFont.WhiteSmallText(), 420, elementBounds32.FlatCopy(), "tip-animal-tab-population")
				.AddSuiteButton(SuiteLocalization.Get("gui.rules"), () => SelectAnimalView("rules"), elementBounds33, animalView == "rules")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.fe0e2ad42c"), CairoFont.WhiteSmallText(), 360, elementBounds33.FlatCopy(), "tip-animal-tab-rules")
				.AddSuiteButton(SuiteLocalization.Get("gui.settings"), () => SelectAnimalView("settings"), elementBounds34, animalView == "settings")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.6e8d88253c"), CairoFont.WhiteSmallText(), 370, elementBounds34.FlatCopy(), "tip-animal-tab-settings")
				.AddSuiteButton(SuiteLocalization.Get("gui.removallogs"), () => SelectAnimalView("history"), elementBounds35, animalView == "history")
				.AddHoverText(SuiteLocalization.Get("gui.tip.more.0f3892a697"), CairoFont.WhiteSmallText(), 370, elementBounds35.FlatCopy(), "tip-animal-tab-history");
			if (animalView == "population")
			{
				flag2 = false;
				ElementBounds elementBounds36 = ElementBounds.Fixed(900.0, 130.0, 180.0, 32.0);
				int value7 = ((populationMobTotals.Count > 0) ? populationMobTotals.Values.Sum((PopulationMobTotal x) => x.Tracked) : populationRows.Sum((PopulationDisplayRow x) => x.Tracked));
				int value8 = ((populationMobTotals.Count > 0) ? populationMobTotals.Values.Sum((PopulationMobTotal x) => x.Protected) : populationRows.Sum((PopulationDisplayRow x) => x.Protected));
				int value9 = populationRows.Count((PopulationDisplayRow x) => x.Eligible > x.Limit);
				int value10 = ((populationMobTotals.Count > 0) ? populationMobTotals.Count : populationRows.Select((PopulationDisplayRow x) => x.MobId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 618.0)).AddRichtext("<strong>" + pageHeaderText + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 136.0, 620.0, 26.0), "page-header").AddSuiteButton(SuiteLocalization.Get("gui.scanpopulation"), ScanAnimalPopulation, elementBounds36)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.0c9abcefa0"), CairoFont.WhiteSmallText(), 430, elementBounds36.FlatCopy(), "tip-population-scan")
					.AddRichtext($"<strong>{value10}</strong> mobs  |  <strong>{populationRows.Count}</strong> regions  |  <strong>{value7}</strong> tracked  |  <strong>{value8}</strong> protected  |  <strong>{value9}</strong> over limit", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 168.0, 740.0, 24.0))
					.AddStaticText(populationMode, CairoFont.WhiteSmallText(), ElementBounds.Fixed(790.0, 168.0, 390.0, 24.0))
					.AddStaticText(SuiteLocalization.Get("gui.mobtotalhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 190.0, 1140.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(24.0, 216.0, 1160.0, 34.0))
					.AddRichtext(SuiteLocalization.Get("gui.mobid"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 222.0, 300.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.region"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(338.0, 222.0, 82.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.matchedrule"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(424.0, 222.0, 205.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.tracked"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(633.0, 222.0, 70.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.mobtotal"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(707.0, 222.0, 82.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.limitlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(793.0, 222.0, 54.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.radiuslabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(851.0, 222.0, 68.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.protected"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(923.0, 222.0, 84.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.statustitle"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(1011.0, 222.0, 140.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(24.0, 253.0, 1160.0, 2.0));
				for (int num27 = 0; num27 < 16; num27++)
				{
					double num28 = 260.0 + (double)num27 * 27.0;
					if ((num27 & 1) == 1)
					{
						guiComposer.AddSuiteInset(ElementBounds.Fixed(24.0, num28 - 1.0, 1160.0, 27.0));
					}
					guiComposer.AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, num28, 300.0, 24.0), $"population-mob-{num27}").AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(338.0, num28, 82.0, 24.0), $"population-region-{num27}").AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(424.0, num28, 205.0, 24.0), $"population-rule-{num27}")
						.AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(633.0, num28, 70.0, 24.0), $"population-tracked-{num27}")
						.AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(707.0, num28, 82.0, 24.0), $"population-mobtotal-{num27}")
						.AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(793.0, num28, 54.0, 24.0), $"population-limit-{num27}")
						.AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(851.0, num28, 68.0, 24.0), $"population-radius-{num27}")
						.AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(923.0, num28, 84.0, 24.0), $"population-protected-{num27}")
						.AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(1011.0, num28, 140.0, 24.0), $"population-status-{num27}");
				}
				guiComposer.AddVerticalScrollbar(OnPopulationScroll, ElementBounds.Fixed(1196.0, 255.0, 18.0, 432.0), "population-scroll");
				num4 = 260.0;
				val = 432.0;
			}
			else if (animalView == "history")
			{
				flag2 = false;
				ElementBounds elementBounds37 = ElementBounds.Fixed(930.0, 130.0, 150.0, 32.0);
				ElementBounds elementBounds38 = ElementBounds.Fixed(842.0, 698.0, 100.0, 30.0);
				ElementBounds elementBounds39 = ElementBounds.Fixed(954.0, 698.0, 100.0, 30.0);
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 618.0)).AddRichtext("<strong>" + pageHeaderText + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 136.0, 650.0, 26.0), "page-header").AddSuiteButton(SuiteLocalization.Get("gui.refreshlogs"), () => SendAnimalLimiterOperation("history"), elementBounds37)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.22891d3a07"), CairoFont.WhiteSmallText(), 360, elementBounds37.FlatCopy(), "tip-animal-refresh-logs")
					.AddRichtext(SuiteLocalization.Get("gui.newestremovals"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 168.0, 280.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(28.0, 190.0, 1020.0, 2.0));
				for (int num29 = 0; num29 < 9; num29++)
				{
					double num30 = 202.0 + (double)num29 * 50.0;
					if ((num29 & 1) == 1)
					{
						guiComposer.AddSuiteInset(ElementBounds.Fixed(28.0, num30 - 2.0, 1018.0, 48.0));
					}
					guiComposer.AddRichtext("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(36.0, num30, 982.0, 46.0), $"removal-row-{num29}");
				}
				guiComposer.AddVerticalScrollbar(OnRemovalLogScroll, ElementBounds.Fixed(1072.0, 195.0, 18.0, 450.0), "removal-scroll").AddRichtext($"<strong>{removalLogLines.Count}</strong> removal record{((removalLogLines.Count == 1) ? "" : "s")}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(36.0, 703.0, 240.0, 24.0)).AddSuiteButton(SuiteLocalization.Get("gui.previous"), ScrollRemovalLogsPrevious, elementBounds38)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.489ab7b8ef"), CairoFont.WhiteSmallText(), 330, elementBounds38.FlatCopy(), "tip-removal-prev")
					.AddSuiteButton(SuiteLocalization.Get("gui.next"), ScrollRemovalLogsNext, elementBounds39)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.acdeede5e3"), CairoFont.WhiteSmallText(), 330, elementBounds39.FlatCopy(), "tip-removal-next");
				num4 = 202.0;
				val = 450.0;
			}
			else if (animalView == "rules")
			{
				flag2 = false;
				ElementBounds elementBounds40 = ElementBounds.Fixed(890.0, 130.0, 160.0, 32.0);
				ElementBounds elementBounds41 = ElementBounds.Fixed(850.0, 698.0, 90.0, 30.0);
				ElementBounds elementBounds42 = ElementBounds.Fixed(954.0, 698.0, 90.0, 30.0);
				int num31 = Math.Max(1, (animalRuleRows.Count + 18 - 1) / 18);
				animalRulePage = Math.Clamp(animalRulePage, 0, num31 - 1);
				int num32 = animalRulePage * 18;
				int num33 = Math.Min(num32 + 18, animalRuleRows.Count);
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 618.0)).AddRichtext("<strong>" + pageHeaderText + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 136.0, 620.0, 26.0), "page-header").AddSuiteButton(SuiteLocalization.Get("gui.refreshrules"), () => SendAnimalLimiterOperation("rules"), elementBounds40)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.774473a941"), CairoFont.WhiteSmallText(), 330, elementBounds40.FlatCopy(), "tip-animal-refresh-rules")
					.AddRichtext("<strong>" + Escape(SuiteLocalization.Get("gui.runtime.rulesexclusions", animalRuleCount, animalExclusionCount)) + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 168.0, 350.0, 24.0))
					.AddStaticText(SuiteLocalization.Get("gui.customradiushelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(400.0, 168.0, 430.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(24.0, 198.0, 1000.0, 34.0))
					.AddRichtext(SuiteLocalization.Get("gui.mobid"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 204.0, 650.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.limitlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(720.0, 204.0, 80.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.radiuslabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(830.0, 204.0, 160.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(24.0, 235.0, 1000.0, 2.0));
				if (animalRuleRows.Count == 0)
				{
					guiComposer.AddStaticText(SuiteLocalization.Get("gui.noanimalrules"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 250.0, 700.0, 24.0));
				}
				else
				{
					for (int num34 = num32; num34 < num33; num34++)
					{
						AnimalRuleDisplayRow animalRuleDisplayRow = animalRuleRows[num34];
						double num35 = 244.0 + (double)(num34 - num32) * 24.0;
						if (((num34 - num32) & 1) == 1)
						{
							guiComposer.AddSuiteInset(ElementBounds.Fixed(24.0, num35 - 2.0, 1000.0, 24.0));
						}
						if (animalRuleDisplayRow.IsExclusion)
						{
							guiComposer.AddRichtext("<font color=\"#ff6666\">" + Escape(animalRuleDisplayRow.Pattern) + "</font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, num35, 660.0, 24.0)).AddRichtext(SuiteLocalization.Get("gui.excludedred"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(720.0, num35, 90.0, 24.0)).AddRichtext("<font color=\"#ff6666\">—</font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(830.0, num35, 165.0, 24.0));
						}
						else
						{
							guiComposer.AddStaticText(animalRuleDisplayRow.Pattern, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, num35, 660.0, 24.0)).AddStaticText(animalRuleDisplayRow.Limit, CairoFont.WhiteSmallText(), ElementBounds.Fixed(720.0, num35, 90.0, 24.0)).AddStaticText(animalRuleDisplayRow.Radius, CairoFont.WhiteSmallText(), ElementBounds.Fixed(830.0, num35, 165.0, 24.0));
						}
					}
				}
				guiComposer.AddStaticText(SuiteLocalization.Get("gui.runtime.pageof", animalRulePage + 1, num31), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 702.0, 160.0, 24.0)).AddSuiteButton(SuiteLocalization.Get("gui.previous"), PreviousAnimalRulePage, elementBounds41).AddHoverText(SuiteLocalization.Get("gui.tip.more.a4681fde39"), CairoFont.WhiteSmallText(), 310, elementBounds41.FlatCopy(), "tip-animal-rules-prev")
					.AddSuiteButton(SuiteLocalization.Get("gui.next"), NextAnimalRulePage, elementBounds42)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.cbc85af9f6"), CairoFont.WhiteSmallText(), 300, elementBounds42.FlatCopy(), "tip-animal-rules-next");
				num4 = 244.0;
				val = 364.0;
			}
			else if (animalView == "settings")
			{
				flag2 = false;
				ElementBounds elementBounds43 = ElementBounds.Fixed(900.0, 622.0, 180.0, 32.0);
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 618.0)).AddRichtext("<strong>" + pageHeaderText + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 136.0, 500.0, 26.0), "page-header").AddStaticText(SuiteLocalization.Get("gui.animalsettingsimmediate"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(560.0, 138.0, 500.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.animallimiterenabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 177.0, 195.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetAnimalBool("enabled", value11);
					}, ElementBounds.Fixed(246.0, 170.0, 60.0, 30.0), "animal-enabled")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.31c5d12121"), CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(246.0, 170.0, 60.0, 30.0), "tip-setting-enabled")
					.AddStaticText(SuiteLocalization.Get("gui.animalfreshdisabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(340.0, 177.0, 560.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.defaultssafety"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 210.0, 260.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(40.0, 232.0, 980.0, 2.0))
					.AddRichtext(SuiteLocalization.Get("gui.defaultlimit"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 248.0, 140.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(188.0, 241.0, 105.0, 30.0), OnAnimalDefaultLimitChanged, CairoFont.WhiteSmallText(), "animal-default-limit")
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveAnimalDefaultLimit, ElementBounds.Fixed(304.0, 241.0, 84.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.e774c4f784"), CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(188.0, 241.0, 105.0, 30.0), "tip-setting-default-limit")
					.AddRichtext(SuiteLocalization.Get("gui.defaultradius"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(560.0, 248.0, 145.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(716.0, 241.0, 105.0, 30.0), OnAnimalRadiusChanged, CairoFont.WhiteSmallText(), "animal-default-radius")
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveAnimalRadius, ElementBounds.Fixed(832.0, 241.0, 84.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.75d21c2041"), CairoFont.WhiteSmallText(), 380, ElementBounds.Fixed(716.0, 241.0, 105.0, 30.0), "tip-setting-default-radius")
					.AddRichtext(SuiteLocalization.Get("gui.playerprotectionradius"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 291.0, 180.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(226.0, 284.0, 105.0, 30.0), OnAnimalProtectionRadiusChanged, CairoFont.WhiteSmallText(), "animal-protection-radius")
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveAnimalProtectionRadius, ElementBounds.Fixed(342.0, 284.0, 84.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.d8d9af6a21"), CairoFont.WhiteSmallText(), 400, ElementBounds.Fixed(226.0, 284.0, 105.0, 30.0), "tip-setting-player-protection")
					.AddRichtext(SuiteLocalization.Get("gui.reconcileseconds"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(560.0, 291.0, 145.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(716.0, 284.0, 105.0, 30.0), OnAnimalReconciliationChanged, CairoFont.WhiteSmallText(), "animal-reconcile-seconds")
					.AddSuiteButton(SuiteLocalization.Get("gui.save"), SaveAnimalReconciliation, ElementBounds.Fixed(832.0, 284.0, 84.0, 30.0))
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.e398a316e6"), CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(716.0, 284.0, 105.0, 30.0), "tip-setting-reconcile")
					.AddRichtext(SuiteLocalization.Get("gui.protectionsbehavior"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 336.0, 300.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(40.0, 358.0, 980.0, 2.0))
					.AddRichtext(SuiteLocalization.Get("gui.limitallentities"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 375.0, 165.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetAnimalBool("limitallentities", value11);
					}, ElementBounds.Fixed(214.0, 368.0, 60.0, 30.0), "animal-limit-all")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.c89bb0294b"), CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(214.0, 368.0, 60.0, 30.0), "tip-setting-limit-all")
					.AddRichtext(SuiteLocalization.Get("gui.protectpets"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(560.0, 375.0, 180.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetAnimalBool("protectpets", value11);
					}, ElementBounds.Fixed(750.0, 368.0, 60.0, 30.0), "animal-protect-pets")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.2c30215595"), CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(750.0, 368.0, 60.0, 30.0), "tip-setting-protect-pets")
					.AddRichtext(SuiteLocalization.Get("gui.protectfarm"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 422.0, 180.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetAnimalBool("protectfarmanimals", value11);
					}, ElementBounds.Fixed(214.0, 415.0, 60.0, 30.0), "animal-protect-farm")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.986427c052"), CairoFont.WhiteSmallText(), 350, ElementBounds.Fixed(214.0, 415.0, 60.0, 30.0), "tip-setting-protect-farm")
					.AddRichtext(SuiteLocalization.Get("gui.protectnamed"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(560.0, 422.0, 155.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetAnimalBool("protectnamedentities", value11);
					}, ElementBounds.Fixed(750.0, 415.0, 60.0, 30.0), "animal-protect-named")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.49d2f18662"), CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(750.0, 415.0, 60.0, 30.0), "tip-setting-protect-named")
					.AddRichtext(SuiteLocalization.Get("gui.keepnearest"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 469.0, 195.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetAnimalBool("keepnearesttoplayers", value11);
					}, ElementBounds.Fixed(244.0, 462.0, 60.0, 30.0), "animal-keep-nearest")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.f1c59bbadf"), CairoFont.WhiteSmallText(), 370, ElementBounds.Fixed(244.0, 462.0, 60.0, 30.0), "tip-setting-keep-nearest")
					.AddRichtext(SuiteLocalization.Get("gui.logging"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 516.0, 180.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(40.0, 538.0, 980.0, 2.0))
					.AddRichtext(SuiteLocalization.Get("gui.logactions"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 555.0, 120.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetAnimalBool("logactions", value11);
					}, ElementBounds.Fixed(170.0, 548.0, 60.0, 30.0), "animal-log-actions")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.97d9ac81c8"), CairoFont.WhiteSmallText(), 340, ElementBounds.Fixed(170.0, 548.0, 60.0, 30.0), "tip-setting-log-actions")
					.AddRichtext(SuiteLocalization.Get("gui.writecsv"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(560.0, 555.0, 145.0, 24.0))
					.AddSwitch((bool value11) =>
					{
						SetAnimalBool("logtocsv", value11);
					}, ElementBounds.Fixed(716.0, 548.0, 60.0, 30.0), "animal-log-csv")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.a49f84c10a"), CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(716.0, 548.0, 60.0, 30.0), "tip-setting-log-csv")
					.AddStaticText(SuiteLocalization.Get("gui.perruleradiushelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 608.0, 800.0, 24.0))
					.AddStaticText(SuiteLocalization.Get("gui.switchessavehelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40.0, 642.0, 800.0, 24.0))
					.AddSuiteButton(SuiteLocalization.Get("gui.refreshsettings"), () => SendAnimalLimiterOperation("settings"), elementBounds43)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.e141999b1b"), CairoFont.WhiteSmallText(), 340, elementBounds43.FlatCopy(), "tip-animal-settings-refresh");
				num4 = 682.0;
				val = 1.0;
			}
			else
			{
				ElementBounds elementBounds44 = ElementBounds.Fixed(38.0, 176.0, 135.0, 34.0);
				ElementBounds elementBounds45 = ElementBounds.Fixed(190.0, 176.0, 165.0, 34.0);
				ElementBounds elementBounds46 = ElementBounds.Fixed(372.0, 176.0, 130.0, 34.0);
				ElementBounds elementBounds47 = ElementBounds.Fixed(519.0, 176.0, 135.0, 34.0);
				ElementBounds elementBounds48 = ElementBounds.Fixed(38.0, 223.0, 165.0, 34.0);
				ElementBounds elementBounds49 = ElementBounds.Fixed(220.0, 223.0, 130.0, 34.0);
				ElementBounds elementBounds50 = ElementBounds.Fixed(194.0, 273.0, 90.0, 30.0);
				guiComposer.AddSuiteInset(ElementBounds.Fixed(16.0, 122.0, fixedWidth, 224.0)).AddRichtext(SuiteLocalization.Get("gui.rulepattern"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 138.0, 95.0, 24.0)).AddTextInput(ElementBounds.Fixed(138.0, 133.0, 390.0, 30.0), (string text10) =>
				{
					animalPattern = text10 ?? "";
				}, CairoFont.WhiteSmallText(), "animal-pattern")
					.AddRichtext(SuiteLocalization.Get("gui.limitlabel"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550.0, 138.0, 42.0, 24.0))
					.AddNumberInput(ElementBounds.Fixed(596.0, 133.0, 90.0, 30.0), OnAnimalLimitChanged, CairoFont.WhiteSmallText(), "animal-limit")
					.AddSuiteButton(SuiteLocalization.Get("gui.setrule"), () => SendAnimalLimiterOperation("set"), elementBounds44)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.55676587cc"), CairoFont.WhiteSmallText(), 390, elementBounds44.FlatCopy(), "tip-animal-set-rule")
					.AddSuiteButton(SuiteLocalization.Get("gui.removerule"), () => SendAnimalLimiterOperation("remove"), elementBounds45)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.b73cac663a"), CairoFont.WhiteSmallText(), 350, elementBounds45.FlatCopy(), "tip-animal-remove-rule")
					.AddSuiteButton(SuiteLocalization.Get("gui.preview"), () => SendAnimalLimiterOperation("preview"), elementBounds46)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.a77a8f75d1"), CairoFont.WhiteSmallText(), 360, elementBounds46.FlatCopy(), "tip-animal-preview")
					.AddSuiteButton(SuiteLocalization.Get("gui.cullnow"), ConfirmCull, elementBounds47)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.dab2caacc6"), CairoFont.WhiteSmallText(), 370, elementBounds47.FlatCopy(), "tip-animal-cull")
					.AddSuiteButton(SuiteLocalization.Get("gui.limittarget"), () => SendAnimalLimiterOperation("target"), elementBounds48)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.2d9cdfee11"), CairoFont.WhiteSmallText(), 370, elementBounds48.FlatCopy(), "tip-animal-target")
					.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => SendAnimalLimiterOperation("status"), elementBounds49)
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.df85d66407"), CairoFont.WhiteSmallText(), 330, elementBounds49.FlatCopy(), "tip-animal-refresh")
					.AddStaticText(SuiteLocalization.Get("gui.limittargethelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(372.0, 230.0, 440.0, 24.0))
					.AddRichtext(SuiteLocalization.Get("gui.ruleradiuschunks"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 279.0, 150.0, 24.0))
					.AddNumberInput(elementBounds50, OnAnimalRuleRadiusChanged, CairoFont.WhiteSmallText(), "animal-rule-radius")
					.AddHoverText(SuiteLocalization.Get("gui.tip.more.beb9e4b5f3"), CairoFont.WhiteSmallText(), 380, elementBounds50.FlatCopy(), "tip-animal-rule-radius")
					.AddStaticText(SuiteLocalization.Get("gui.radiusscalehelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(306.0, 279.0, 500.0, 24.0))
					.AddSuiteInset(ElementBounds.Fixed(16.0, 358.0, fixedWidth, 312.0))
					.AddRichtext("<strong>" + pageHeaderText + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(28.0, 372.0, 980.0, 26.0), "page-header");
				num4 = 406.0;
				val = 314.0;
			}
		}
		val = Math.Min(val, Math.Max(0.0, num2 - 28.0 - num4));
		bool flag3 = flag2 && IsAdminInventoryViewerResult();
		if (flag2)
		{
			if (flag3)
			{
				AddAdminInventoryViewer(guiComposer, 28.0, num3, num4, val);
			}
			else
			{
				AddScrollableResults(guiComposer, 28.0, num3, num4, val);
			}
		}
		guiComposer.Compose();
		SingleComposer = guiComposer;
		if (flag2 & flag3)
		{
			ConfigureAdminInventoryScrollbar();
		}
		else if (flag2)
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
		if (section == "animals")
		{
			if (animalView == "limiter")
			{
				guiComposer.GetNumberInput("animal-limit")?.SetValue(animalLimit.ToString());
				guiComposer.GetTextInput("animal-pattern")?.SetValue(animalPattern);
				guiComposer.GetNumberInput("animal-rule-radius")?.SetValue(animalRuleRadiusChunks.ToString());
			}
			else if (animalView == "settings")
			{
				guiComposer.GetNumberInput("animal-default-limit")?.SetValue(animalDefaultLimit.ToString());
				guiComposer.GetNumberInput("animal-default-radius")?.SetValue(animalRadiusChunks.ToString());
				guiComposer.GetNumberInput("animal-protection-radius")?.SetValue(animalPlayerProtectionRadius.ToString());
				guiComposer.GetNumberInput("animal-reconcile-seconds")?.SetValue(animalReconciliationSeconds.ToString());
			}
			return;
		}
		if (section == "admincommands")
		{
			ApplyAdminCommandControlValues();
			return;
		}
		if (section == "anticheat")
		{
			SingleComposer?.GetTextInput("anticheat-player-search")?.SetValue(antiCheatPlayerSearch);
			SingleComposer?.GetTextInput("anticheat-report-search")?.SetValue(antiCheatReportSearch);
			SingleComposer?.GetTextInput("anticheat-blacklist-mod")?.SetValue(antiCheatBlacklistModId);
			SingleComposer?.GetTextInput("anticheat-blacklist-source")?.SetValue(antiCheatBlacklistSourcePlayer);
			ApplyAntiCheatControlValues();
			ApplyAntiCheatEnforcementControlValues();
			return;
		}
		if (section == "webserver")
		{
			ApplyWebControlValues();
			ApplyWebMapControlValues();
			ApplyDiscordControlValues();
			return;
		}
		if (section == "loginprotection")
		{
			ApplyLoginProtectionControlValues();
			return;
		}
		if (section == "profiler")
		{
			applyingProfilerControls = true;
			try
			{
				guiComposer.GetSwitch("profiler-enabled")?.SetValue(profilerEnabled);
				return;
			}
			finally
			{
				applyingProfilerControls = false;
			}
		}
		if (view == "settings")
		{
			ApplyBlockLoggerControlValues();
		}
		else if (view == "players")
		{
			guiComposer.GetNumberInput("limit")?.SetValue(limit.ToString());
			guiComposer.GetTextInput("player-search")?.SetValue(playerSearch);
		}
		else if (view != "status")
		{
			guiComposer.GetNumberInput("radius")?.SetValue(radius.ToString());
			guiComposer.GetNumberInput("limit")?.SetValue(limit.ToString());
			guiComposer.GetTextInput("search")?.SetValue(search);
		}
	}

	private bool SelectSection(string selected)
	{
		if (selected == section)
		{
			return true;
		}
		if (section == "admincommands")
		{
			CaptureAdminCommandInputs();
		}
		if (selected != "profiler" && profilerLiveUpdates)
		{
			profilerLiveUpdates = false;
			profilerLiveRequestPending = false;
			profilerLiveGeneration++;
		}
		previousWorkspace = section;
		if (selected != "home")
		{
			lastWorkspace = selected;
		}
		section = selected;
		genericScrollValue = 0.0;
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
			if (blockLoggerView == "settings")
			{
				return SelectBlockLoggerSettings();
			}
			if (blockLoggerView == "status")
			{
				return SelectStatus();
			}
			return SelectView(blockLoggerView);
		}
	}

	private bool SelectAntiCheatView(string selected)
	{
		section = "anticheat";
		view = "anticheat";
		bool flag;
		switch (selected)
		{
		case "players":
		case "player":
		case "reports":
		case "blacklist":
		case "enforcement":
		case "settings":
		case "packet":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		antiCheatView = (flag ? selected : "overview");
		genericScrollValue = 0.0;
		string text = antiCheatView;
		if (1 == 0)
		{
		}
		string text2 = text switch
		{
			"players" => "Anti-Cheat players & risk ratings", 
			"player" => "Anti-Cheat player investigation", 
			"reports" => "Persistent Anti-Cheat reports", 
			"blacklist" => "Client mod blacklist", 
			"enforcement" => "Anti-Cheat enforcement rules", 
			"settings" => "Anti-Cheat settings", 
			"packet" => SuiteLocalization.Get("gui.packetprotectiontitle"), 
			_ => SuiteLocalization.Get("gui.runtime.anticheatoverview"), 
		};
		if (1 == 0)
		{
		}
		pageHeaderText = text2;
		string text3 = antiCheatView;
		if (1 == 0)
		{
		}
		text2 = text3 switch
		{
			"player" => "Enter a player name or UID and press Load.", 
			"reports" => "Loading persistent Anti-Cheat reports...", 
			"blacklist" => "Loading client mod blacklist...", 
			"enforcement" => "Loading Anti-Cheat enforcement rules...", 
			"settings" => "Loading Anti-Cheat settings...", 
			"packet" => SuiteLocalization.Get("gui.loadingpacketprotection"), 
			_ => "Loading anti-cheat data...", 
		};
		if (1 == 0)
		{
		}
		string item = text2;
		SetGenericResults(new List<string> { item });
		ComposeDialog();
		if (antiCheatView == "reports")
		{
			return RequestAntiCheat("reports", antiCheatReportSearch);
		}
		if (antiCheatView == "blacklist")
		{
			return RequestAntiCheat("blacklist");
		}
		if (antiCheatView == "enforcement")
		{
			return RequestAntiCheat("enforcement");
		}
		if (antiCheatView == "settings")
		{
			return RequestAntiCheat("settings");
		}
		if (antiCheatView == "packet")
		{
			return RequestAntiCheat("packet");
		}
		if (antiCheatView != "player")
		{
			return RequestAntiCheat(antiCheatView);
		}
		return true;
	}

	private bool SelectAntiCheatPacketView(string selected)
	{
		bool flag;
		switch (selected)
		{
		case "movement":
		case "reach":
		case "mining":
		case "risk":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		antiCheatPacketView = (flag ? selected : "general");
		pageHeaderText = SuiteLocalization.Get("gui.packetprotectiontitle");
		ComposeDialog();
		return true;
	}

	private string BuildPacketTpsStatusText()
	{
		string value = (acPacketTpsReady ? SuiteLocalization.Get("gui.ready") : SuiteLocalization.Get("gui.warmingup"));
		string value2 = (string.IsNullOrWhiteSpace(acPacketTpsBand) ? "Disabled" : acPacketTpsBand);
		return $"<strong>{SuiteLocalization.Get("gui.statusplain")}:</strong> {value}<br><strong>{SuiteLocalization.Get("gui.tpsband")}:</strong> {WebUtility.HtmlEncode(value2)}<br><strong>{SuiteLocalization.Get("gui.targettps")}:</strong> {acPacketTpsTarget:0.##}<br><strong>{SuiteLocalization.Get("gui.estimatedtps")}:</strong> {acPacketTpsEstimated:0.##}<br><strong>{SuiteLocalization.Get("gui.healthpercent")}:</strong> {acPacketTpsHealthPercent:0.#}%";
	}

	private string BuildPacketIngressStatusText()
	{
		bool flag = string.IsNullOrWhiteSpace(acPacketIngressStatus) || acPacketIngressStatus == "Unknown";
		string value;
		if (flag)
		{
			value = "#aab8c7";
		}
		else if (acPacketIngressActive)
		{
			value = "#8fda72";
		}
		else
		{
			value = (acPacketIngressEnabled ? "#ff8080" : "#aab8c7");
		}
		string text = (flag ? SuiteLocalization.Get("gui.workspace.awaitingdata") : ShortDisplay(acPacketIngressStatus, 28));
		return $"<font color=\"{value}\"><strong>{Escape(SuiteLocalization.Get("gui.workspace.ingressstatus", text))}</strong></font>";
	}

	private bool RequestAntiCheat(string operation, string? search = null, string? secondary = null)
	{
		long num = (lastResponseId = ++requestId);
		sendAntiCheat(new AntiCheatGuiRequest
		{
			RequestId = num,
			Operation = operation,
			Search = (search ?? antiCheatPlayerSearch),
			Secondary = (secondary ?? "")
		});
		return true;
	}

	private void SetAntiCheatEnabled(bool value)
	{
		if (!applyingAntiCheatControls)
		{
			antiCheatEnabled = value;
			pageHeaderText = (value ? SuiteLocalization.Get("gui.enablinganticheat") : SuiteLocalization.Get("gui.disablinganticheat"));
			long num = (lastResponseId = ++requestId);
			sendAntiCheat(new AntiCheatGuiRequest
			{
				RequestId = num,
				Operation = "setenabled",
				BoolValue = value
			});
		}
	}

	private void ApplyAntiCheatControlValues()
	{
		if (SingleComposer == null)
		{
			return;
		}
		applyingAntiCheatControls = true;
		try
		{
			SingleComposer.GetSwitch("anticheat-enabled")?.SetValue(antiCheatEnabled);
			if (!(section != "anticheat"))
			{
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
				}
				else if (!(antiCheatView != "settings"))
				{
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
			}
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
		if (!applyingAntiCheatControls)
		{
			switch (key)
			{
			case "master":
				acPacketGuardEnabled = value;
				break;
			case "warnings":
				acPacketWarningsEnabled = value;
				break;
			case "noclip":
				acPacketNoclipEnabled = value;
				break;
			case "diag-context":
				acPacketDiagnosticContext = value;
				break;
			case "diag-suppression":
				acPacketDiagnosticSuppression = value;
				break;
			case "movement":
				acMovementEnabled = value;
				break;
			case "reject-move":
				acRejectUnauthorizedMovement = value;
				break;
			case "exempt-admins":
				acPacketExemptAdmins = value;
				break;
			case "exempt-mounted":
				acPacketExemptMountedPlayers = value;
				break;
			case "tprunes":
				acPacketTPRunesCompatibility = value;
				break;
			case "th3spawn":
				acTh3EssentialsSpawnCompatibility = value;
				break;
			case "reach":
				acReachEnabled = value;
				break;
			case "reach-latency":
				acPacketReachLatencyCompensation = value;
				break;
			case "reach-history":
				acPacketReachPositionHistoryEnabled = value;
				break;
			case "reject-reach":
				acRejectOutOfReach = value;
				break;
			case "mining":
				acMiningEnabled = value;
				break;
			case "reject-bursts":
				acRejectConnectedBursts = value;
				break;
			case "mining-cadence":
				acMiningCadenceEnabled = value;
				break;
			case "reject-cadence":
				acRejectImpossibleCadence = value;
				break;
			case "tps":
				acTpsAwarenessEnabled = value;
				break;
			case "tps-suppress":
				acTpsSuppressCriticalRejects = value;
				break;
			case "family-risk":
				acPerFamilyRiskEnabled = value;
				break;
			case "correlation":
				acIncidentCorrelationEnabled = value;
				break;
			case "history":
				acHistoryEnabled = value;
				break;
			}
		}
	}

	private void SetAntiCheatPacketInt(string key, string value)
	{
		if (!applyingAntiCheatControls && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			switch (key)
			{
			case "warning-cooldown":
				acPacketWarningCooldownSeconds = result;
				break;
			case "accel-sample":
				acPacketAccelerationSampleMinMs = result;
				break;
			case "noclip-samples":
				acPacketNoclipSamples = result;
				break;
			case "diag-history":
				acPacketDiagnosticSpeedHistory = result;
				break;
			case "movement-sample":
				acPacketMovementSampleMs = result;
				break;
			case "movement-confirm-samples":
				acPacketMovementConfirmSamples = result;
				break;
			case "movement-confirm-window":
				acPacketMovementConfirmWindowMs = result;
				break;
			case "correction-grace":
				acPacketCorrectionGraceMs = result;
				break;
			case "mount-grace":
				acPacketMountGraceMs = result;
				break;
			case "engine-teleport-grace":
				acPacketEngineTeleportGraceMs = result;
				break;
			case "tprunes-grace":
				acPacketTPRunesGraceMs = result;
				break;
			case "th3spawn-grace":
				acTh3EssentialsSpawnGraceMs = result;
				break;
			case "reach-history-ms":
				acPacketReachPositionHistoryMs = result;
				break;
			case "reach-rewind-ms":
				acPacketReachMaxRewindMs = result;
				break;
			case "reach-strikes":
				acPacketReachNearThresholdStrikes = result;
				break;
			case "reach-strike-window":
				acPacketReachStrikeWindowMs = result;
				break;
			case "reach-strike-decay":
				acPacketReachStrikeDecayMs = result;
				break;
			case "vein-window":
				acPacketVeinWindowMs = result;
				break;
			case "connected-break-limit":
				acPacketConnectedBreakLimit = result;
				break;
			case "duplicate-target":
				acPacketMiningDuplicateTargetMs = result;
				break;
			case "mining-confirmations":
				acMiningCadenceConfirmations = result;
				break;
			case "cadence-confirm-window":
				acPacketMiningCadenceConfirmationWindowMs = result;
				break;
			case "risk-move-decay":
				acMovementRiskDecaySeconds = result;
				break;
			case "risk-move-review":
				acMovementRiskReviewThreshold = result;
				break;
			case "risk-move-suspicious":
				acMovementRiskSuspiciousThreshold = result;
				break;
			case "risk-reach-decay":
				acReachRiskDecaySeconds = result;
				break;
			case "risk-reach-review":
				acReachRiskReviewThreshold = result;
				break;
			case "risk-reach-suspicious":
				acReachRiskSuspiciousThreshold = result;
				break;
			case "risk-mining-decay":
				acMiningRiskDecaySeconds = result;
				break;
			case "risk-mining-review":
				acMiningRiskReviewThreshold = result;
				break;
			case "risk-mining-suspicious":
				acMiningRiskSuspiciousThreshold = result;
				break;
			case "risk-packet-decay":
				acPacketAbuseRiskDecaySeconds = result;
				break;
			case "risk-packet-review":
				acPacketAbuseRiskReviewThreshold = result;
				break;
			case "risk-packet-suspicious":
				acPacketAbuseRiskSuspiciousThreshold = result;
				break;
			case "risk-client-decay":
				acClientIntegrityRiskDecaySeconds = result;
				break;
			case "risk-client-review":
				acClientIntegrityRiskReviewThreshold = result;
				break;
			case "risk-client-suspicious":
				acClientIntegrityRiskSuspiciousThreshold = result;
				break;
			case "incident-window":
				acIncidentWindowSeconds = result;
				break;
			case "incident-units":
				acIncidentRequiredCategories = result;
				break;
			case "incident-bonus":
				acIncidentMaxRiskBonus = result;
				break;
			case "history-days":
				acHistoryRetentionDays = result;
				break;
			case "history-max":
				acHistoryMaxRecords = Math.Clamp(result, 100, 1000);
				break;
			}
		}
	}

	private void SetAntiCheatPacketDouble(string key, string value)
	{
		if (!applyingAntiCheatControls && (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result)))
		{
			switch (key)
			{
			case "accel-smoothing":
				acPacketAccelerationSmoothing = result;
				break;
			case "noclip-penetration":
				acPacketNoclipPenetration = result;
				break;
			case "noclip-endpoint":
				acPacketNoclipEndpointMovement = result;
				break;
			case "mount-ratio":
				acPacketMountAccelerationRatio = result;
				break;
			case "hard-move":
				acHardMoveDistance = result;
				break;
			case "movement-extreme":
				acPacketMovementExtremeMultiplier = result;
				break;
			case "reach-tolerance":
				acReachTolerance = result;
				break;
			case "reach-max-latency":
				acPacketReachMaxLatencyCompensation = result;
				break;
			case "reach-use-extra":
				acPacketReachUseExtraTolerance = result;
				break;
			case "reach-break-extra":
				acPacketReachBreakExtraTolerance = result;
				break;
			case "reach-hard":
				acReachHardExcess = result;
				break;
			case "cadence-min":
				acPacketMiningCadenceMinExpectedMs = result;
				break;
			case "mining-ratio":
				acMiningCadenceRatio = result;
				break;
			case "tps-degraded":
				acTpsDegradedPercent = result;
				break;
			case "tps-critical":
				acTpsCriticalPercent = result;
				break;
			case "tps-max":
				acTpsMaxMultiplier = result;
				break;
			}
		}
	}

	private bool SaveAntiCheatPacketProtectionSettings()
	{
		pageHeaderText = SuiteLocalization.Get("gui.savingpacketprotection");
		long num = (lastResponseId = ++requestId);
		sendAntiCheat(new AntiCheatGuiRequest
		{
			RequestId = num,
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
		if (!applyingAntiCheatControls)
		{
			switch (key)
			{
			case "requireclient":
				acRequireClient = value;
				break;
			case "runtime-rescan":
				acRuntimeRescanOnChange = value;
				break;
			case "similarity":
				acSimilarityDetection = value;
				break;
			case "packetguard":
				acPacketGuardEnabled = value;
				break;
			case "movement":
				acMovementEnabled = value;
				break;
			case "reject-move":
				acRejectUnauthorizedMovement = value;
				break;
			case "th3spawn":
				acTh3EssentialsSpawnCompatibility = value;
				break;
			case "reach":
				acReachEnabled = value;
				break;
			case "reject-reach":
				acRejectOutOfReach = value;
				break;
			case "mining":
				acMiningEnabled = value;
				break;
			case "reject-bursts":
				acRejectConnectedBursts = value;
				break;
			case "mining-cadence":
				acMiningCadenceEnabled = value;
				break;
			case "reject-cadence":
				acRejectImpossibleCadence = value;
				break;
			case "tps":
				acTpsAwarenessEnabled = value;
				break;
			case "tps-suppress":
				acTpsSuppressCriticalRejects = value;
				break;
			case "correlation":
				acIncidentCorrelationEnabled = value;
				break;
			case "history":
				acHistoryEnabled = value;
				break;
			}
		}
	}

	private void SetAntiCheatSettingInt(string key, string value)
	{
		if (!applyingAntiCheatControls && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			switch (key)
			{
			case "rescan":
				acRescanMinutes = result;
				break;
			case "runtime-cooldown":
				acRuntimeCooldownSeconds = result;
				break;
			case "similarity-review":
				acSimilarityReviewPercent = result;
				break;
			case "similarity-high":
				acSimilarityHighPercent = result;
				break;
			case "th3spawn-grace":
				acTh3EssentialsSpawnGraceMs = result;
				break;
			case "mining-confirmations":
				acMiningCadenceConfirmations = result;
				break;
			case "incident-window":
				acIncidentWindowSeconds = result;
				break;
			case "incident-units":
				acIncidentRequiredCategories = result;
				break;
			case "incident-bonus":
				acIncidentMaxRiskBonus = result;
				break;
			case "history-days":
				acHistoryRetentionDays = result;
				break;
			case "history-max":
				acHistoryMaxRecords = Math.Clamp(result, 100, 1000);
				break;
			}
		}
	}

	private void SetAntiCheatSettingDouble(string key, string value)
	{
		if (!applyingAntiCheatControls && (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result)))
		{
			switch (key)
			{
			case "hard-move":
				acHardMoveDistance = result;
				break;
			case "reach-tolerance":
				acReachTolerance = result;
				break;
			case "reach-hard":
				acReachHardExcess = result;
				break;
			case "mining-ratio":
				acMiningCadenceRatio = result;
				break;
			case "tps-degraded":
				acTpsDegradedPercent = result;
				break;
			case "tps-critical":
				acTpsCriticalPercent = result;
				break;
			case "tps-max":
				acTpsMaxMultiplier = result;
				break;
			}
		}
	}

	private bool SaveAntiCheatSettings()
	{
		pageHeaderText = SuiteLocalization.Get("gui.savinganticheatsettings");
		long num = (lastResponseId = ++requestId);
		sendAntiCheat(new AntiCheatGuiRequest
		{
			RequestId = num,
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
		string selectedId = ((antiCheatEnforcementSelectedIndex >= 0 && antiCheatEnforcementSelectedIndex < antiCheatEnforcementRules.Count) ? antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex].Id : "");
		antiCheatEnforcementRules.Clear();
		if (rules != null)
		{
			foreach (AntiCheatConsequenceRuleGuiPacket item in rules.Take(64))
			{
				antiCheatEnforcementRules.Add(CloneAntiCheatRulePacket(item));
			}
		}
		antiCheatEnforcementSelectedIndex = ((!string.IsNullOrWhiteSpace(selectedId)) ? antiCheatEnforcementRules.FindIndex((AntiCheatConsequenceRuleGuiPacket x) => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase)) : ((antiCheatEnforcementRules.Count <= 0) ? (-1) : 0));
		if (antiCheatEnforcementSelectedIndex < 0 && antiCheatEnforcementRules.Count > 0)
		{
			antiCheatEnforcementSelectedIndex = 0;
		}
		LoadAntiCheatRuleEditorFromSelection();
	}

	private static AntiCheatConsequenceRuleGuiPacket CloneAntiCheatRulePacket(AntiCheatConsequenceRuleGuiPacket rule)
	{
		return new AntiCheatConsequenceRuleGuiPacket
		{
			Id = (rule.Id ?? ""),
			Name = (rule.Name ?? ""),
			Enabled = rule.Enabled,
			TriggerType = (rule.TriggerType ?? "FindingCode"),
			TriggerValue = (rule.TriggerValue ?? ""),
			Subject = (rule.Subject ?? ""),
			Action = (rule.Action ?? "Log"),
			Duration = (rule.Duration ?? "30m"),
			Message = (rule.Message ?? "")
		};
	}

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
		}
		else
		{
			AntiCheatConsequenceRuleGuiPacket antiCheatConsequenceRuleGuiPacket = antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex];
			acRuleName = antiCheatConsequenceRuleGuiPacket.Name ?? "";
			acRuleEnabled = antiCheatConsequenceRuleGuiPacket.Enabled;
			acRuleTriggerType = (string.IsNullOrWhiteSpace(antiCheatConsequenceRuleGuiPacket.TriggerType) ? "FindingCode" : antiCheatConsequenceRuleGuiPacket.TriggerType);
			acRuleTriggerValue = antiCheatConsequenceRuleGuiPacket.TriggerValue ?? "";
			acRuleSubject = antiCheatConsequenceRuleGuiPacket.Subject ?? "";
			acRuleAction = (string.IsNullOrWhiteSpace(antiCheatConsequenceRuleGuiPacket.Action) ? "Log" : antiCheatConsequenceRuleGuiPacket.Action);
			acRuleDuration = antiCheatConsequenceRuleGuiPacket.Duration ?? "30m";
			acRuleMessage = antiCheatConsequenceRuleGuiPacket.Message ?? "";
		}
	}

	private int AntiCheatRuleTriggerIndex()
	{
		string text = acRuleTriggerType;
		if (1 == 0)
		{
		}
		int result = ((text == "Status") ? 1 : ((text == "RiskAtLeast") ? 2 : 0));
		if (1 == 0)
		{
		}
		return result;
	}

	private int AntiCheatRuleActionIndex()
	{
		string text = acRuleAction;
		if (1 == 0)
		{
		}
		int result = text switch
		{
			"Kick" => 1, 
			"TempBan" => 2, 
			"Freeze" => 3, 
			_ => 0, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void OnAntiCheatRuleTriggerChanged(string value, bool selected)
	{
		if (selected && !applyingAntiCheatEnforcementControls)
		{
			acRuleTriggerType = value ?? "FindingCode";
		}
	}

	private void OnAntiCheatRuleActionChanged(string value, bool selected)
	{
		if (selected && !applyingAntiCheatEnforcementControls)
		{
			acRuleAction = value ?? "Log";
		}
	}

	private void CaptureAntiCheatEnforcementControlValues()
	{
		if (SingleComposer != null)
		{
			acRuleName = SingleComposer.GetTextInput("ac-rule-name")?.GetText() ?? acRuleName;
			acRuleTriggerValue = SingleComposer.GetTextInput("ac-rule-trigger-value")?.GetText() ?? acRuleTriggerValue;
			acRuleSubject = SingleComposer.GetTextInput("ac-rule-subject")?.GetText() ?? acRuleSubject;
			acRuleDuration = SingleComposer.GetTextInput("ac-rule-duration")?.GetText() ?? acRuleDuration;
			acRuleMessage = SingleComposer.GetTextInput("ac-rule-message")?.GetText() ?? acRuleMessage;
		}
	}

	private void ApplyAntiCheatEnforcementControlValues()
	{
		if (SingleComposer == null || section != "anticheat" || antiCheatView != "enforcement")
		{
			return;
		}
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
		if (index < 0 || index >= antiCheatEnforcementRules.Count)
		{
			return true;
		}
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
		string id = ((antiCheatEnforcementSelectedIndex >= 0 && antiCheatEnforcementSelectedIndex < antiCheatEnforcementRules.Count) ? antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex].Id : Guid.NewGuid().ToString("N"));
		AntiCheatConsequenceRuleGuiPacket antiCheatConsequenceRuleGuiPacket = new AntiCheatConsequenceRuleGuiPacket
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
		{
			antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex] = antiCheatConsequenceRuleGuiPacket;
		}
		else
		{
			antiCheatEnforcementRules.Add(antiCheatConsequenceRuleGuiPacket);
			antiCheatEnforcementSelectedIndex = antiCheatEnforcementRules.Count - 1;
		}
		pageHeaderText = SuiteLocalization.Get("gui.rulestaged", antiCheatConsequenceRuleGuiPacket.Name);
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
		if (antiCheatEnforcementRules.Count == 0)
		{
			antiCheatEnforcementSelectedIndex = -1;
		}
		else
		{
			antiCheatEnforcementSelectedIndex = Math.Min(antiCheatEnforcementSelectedIndex, antiCheatEnforcementRules.Count - 1);
		}
		LoadAntiCheatRuleEditorFromSelection();
		pageHeaderText = SuiteLocalization.Get("gui.ruleremovedstaged", name);
		RefreshAntiCheatEnforcementResultLines();
		ComposeDialog();
		return true;
	}

	private bool SaveAntiCheatEnforcementRules()
	{
		CaptureAntiCheatEnforcementControlValues();
		if (antiCheatEnforcementSelectedIndex >= 0 && antiCheatEnforcementSelectedIndex < antiCheatEnforcementRules.Count)
		{
			AntiCheatConsequenceRuleGuiPacket antiCheatConsequenceRuleGuiPacket = antiCheatEnforcementRules[antiCheatEnforcementSelectedIndex];
			antiCheatConsequenceRuleGuiPacket.Name = acRuleName.Trim();
			antiCheatConsequenceRuleGuiPacket.Enabled = acRuleEnabled;
			antiCheatConsequenceRuleGuiPacket.TriggerType = acRuleTriggerType;
			antiCheatConsequenceRuleGuiPacket.TriggerValue = acRuleTriggerValue.Trim();
			antiCheatConsequenceRuleGuiPacket.Subject = acRuleSubject.Trim();
			antiCheatConsequenceRuleGuiPacket.Action = acRuleAction;
			antiCheatConsequenceRuleGuiPacket.Duration = acRuleDuration.Trim();
			antiCheatConsequenceRuleGuiPacket.Message = acRuleMessage.Trim();
		}
		else if (!string.IsNullOrWhiteSpace(acRuleName))
		{
			antiCheatEnforcementRules.Add(new AntiCheatConsequenceRuleGuiPacket
			{
				Id = Guid.NewGuid().ToString("N"),
				Name = acRuleName.Trim(),
				Enabled = acRuleEnabled,
				TriggerType = acRuleTriggerType,
				TriggerValue = acRuleTriggerValue.Trim(),
				Subject = acRuleSubject.Trim(),
				Action = acRuleAction,
				Duration = acRuleDuration.Trim(),
				Message = acRuleMessage.Trim()
			});
			antiCheatEnforcementSelectedIndex = antiCheatEnforcementRules.Count - 1;
		}
		pageHeaderText = SuiteLocalization.Get("gui.savingenforcementrules");
		long num = (lastResponseId = ++requestId);
		sendAntiCheat(new AntiCheatGuiRequest
		{
			RequestId = num,
			Operation = "enforcementsave",
			ApplyCurrent = acRuleApplyCurrent,
			ConsequenceRules = antiCheatEnforcementRules.Select(CloneAntiCheatRulePacket).ToList()
		});
		return true;
	}

	private void RefreshAntiCheatEnforcementResultLines()
	{
		List<string> list = new List<string>();
		for (int i = 0; i < antiCheatEnforcementRules.Count; i++)
		{
			AntiCheatConsequenceRuleGuiPacket antiCheatConsequenceRuleGuiPacket = antiCheatEnforcementRules[i];
			list.Add($"RULE\t{i}\t{RuleField(antiCheatConsequenceRuleGuiPacket.Id)}\t{RuleField(antiCheatConsequenceRuleGuiPacket.Name)}\t{(antiCheatConsequenceRuleGuiPacket.Enabled ? 1 : 0)}\t{RuleField(antiCheatConsequenceRuleGuiPacket.TriggerType)}\t{RuleField(antiCheatConsequenceRuleGuiPacket.TriggerValue)}\t{RuleField(antiCheatConsequenceRuleGuiPacket.Subject)}\t{RuleField(antiCheatConsequenceRuleGuiPacket.Action)}\t{RuleField(antiCheatConsequenceRuleGuiPacket.Duration)}\t{RuleField(antiCheatConsequenceRuleGuiPacket.Message)}");
		}
		if (list.Count == 0)
		{
			list.Add("No automatic enforcement rules are configured. Detection and reporting still continue normally.");
		}
		SetGenericResults(list);
	}

	private static string RuleField(string? value)
	{
		return (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
	}

	private static bool TryGetAntiCheatEnforcementRuleIndex(string? raw, out int index)
	{
		index = -1;
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		string[] array = raw.Split('\t');
		return array.Length >= 11 && array[0] == "RULE" && int.TryParse(array[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0;
	}

	private void SetAntiCheatPlayerResults(List<string>? lines)
	{
		antiCheatPlayerSummary.Clear();
		List<string> list = new List<string>();
		if (lines != null)
		{
			foreach (string line in lines)
			{
				string text = (line ?? "").Trim();
				if (text.Length == 0)
				{
					continue;
				}
				if (text.StartsWith("FINDING\t", StringComparison.Ordinal) || text.StartsWith("REPORT\t", StringComparison.Ordinal) || text.StartsWith("MOD\t", StringComparison.Ordinal) || text.StartsWith("Runtime + ", StringComparison.Ordinal) || text.StartsWith("Scanner ", StringComparison.Ordinal))
				{
					list.Add(text);
					continue;
				}
				int num = text.IndexOf(':');
				if (num > 0)
				{
					string key = text.Substring(0, num).Trim();
					string value = text.Substring(num + 1).Trim();
					antiCheatPlayerSummary[key] = value;
				}
				else
				{
					list.Add(text);
				}
			}
		}
		string text2 = AntiCheatSummary("Client DLL SHA-256");
		string text3 = AntiCheatSummary("Client package SHA-256");
		string text4 = AntiCheatSummary("Client MVID");
		string text5 = AntiCheatSummary("Runtime digest");
		if (text2 != "—" || text3 != "—" || text4 != "—" || text5 != "—")
		{
			list.Insert(0, $"FINGERPRINT\t{text2}\t{text3}\t{text4}\t{text5}");
		}
		if (list.Count == 0)
		{
			list.Add("No findings, runtime changes, stored incidents, or scanner warnings are present for this player.");
		}
		SetGenericResults(list);
	}

	private string AntiCheatSummary(string key)
	{
		string value;
		return (antiCheatPlayerSummary.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)) ? value : "—";
	}

	private string BuildAntiCheatIdentityCard()
	{
		string text = AntiCheatSummary("Status");
		return $"<font color=\"#7db6ff\"><strong>IDENTITY</strong></font><br><strong>{Escape(AntiCheatSummary("Player"))}</strong><br><font color=\"{StatusColor(text)}\">{Escape(text)}</font><br><font color=\"#91a0ae\">UID {Escape(AntiCheatSummary("UID"))}<br>Heartbeat {Escape(AntiCheatSummary("Last heartbeat"))}</font>";
	}

	private string BuildAntiCheatRiskCard()
	{
		string text = AntiCheatSummary(SuiteLocalization.Get("gui.riskscore"));
		int result = 0;
		int num = text.IndexOf('/');
		int.TryParse((num > 0) ? text.Substring(0, num) : text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
		return $"<font color=\"#7db6ff\"><strong>RISK & SCAN</strong></font><br><font color=\"{RiskColor(result)}\"><strong>{Escape(text)} • {Escape(AntiCheatSummary("Risk rating"))}</strong></font><br>Findings {Escape(AntiCheatSummary("Findings"))}<br><font color=\"#91a0ae\">Last scan {Escape(AntiCheatSummary("Last scan"))}<br>Last action {Escape(AntiCheatSummary("Last consequence"))}</font>";
	}

	private string BuildAntiCheatClientCard()
	{
		string text = AntiCheatSummary("Client installed");
		string value = (text.Equals("True", StringComparison.OrdinalIgnoreCase) ? "#8fda72" : "#ff7474");
		return $"<font color=\"#7db6ff\"><strong>CLIENT BUILD</strong></font><br>Installed <font color=\"{value}\"><strong>{Escape(text)}</strong></font><br>Version {Escape(AntiCheatSummary("Client version"))}<br><font color=\"#91a0ae\">Game {Escape(AntiCheatSummary("Game version"))}<br>{Escape(AntiCheatSummary("Platform"))}</font>";
	}

	private string BuildAntiCheatRuntimeCard()
	{
		return $"<font color=\"#7db6ff\"><strong>RUNTIME</strong></font><br>Mods / asm {Escape(AntiCheatSummary("Mods / assemblies"))}<br>Generation {Escape(AntiCheatSummary("Runtime generation"))}<br><font color=\"#91a0ae\">{Escape(AntiCheatSummary("Runtime assemblies"))}<br>New {Escape(AntiCheatSummary("Post-baseline assemblies"))}</font>";
	}

	private bool OpenAntiCheatPlayerReport(string playerTarget)
	{
		if (string.IsNullOrWhiteSpace(playerTarget))
		{
			return true;
		}
		antiCheatPlayerSearch = playerTarget.Trim();
		antiCheatView = "player";
		genericScrollValue = 0.0;
		antiCheatPlayerSummary.Clear();
		pageHeaderText = SuiteLocalization.Get("gui.loadingacreport", antiCheatPlayerSearch);
		SetGenericResults(new List<string> { "Loading findings, runtime evidence, and persistent incidents..." });
		ComposeDialog();
		return RequestAntiCheat("player", antiCheatPlayerSearch);
	}

	private bool ResolveAntiCheatBlacklistFingerprint(string modId)
	{
		if (string.IsNullOrWhiteSpace(modId))
		{
			return true;
		}
		antiCheatBlacklistModId = modId.Trim();
		pageHeaderText = SuiteLocalization.Get("gui.resolvingfingerprints", antiCheatBlacklistModId);
		ComposeDialog();
		return RequestAntiCheat("blacklistresolve", antiCheatBlacklistModId);
	}

	private static bool TryGetAntiCheatPlayerActionTarget(string? raw, out string target)
	{
		target = "";
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		string[] array = raw.Split('\t');
		if (array.Length < 10 || array[0] != "PLAYER")
		{
			return false;
		}
		target = ((array.Length >= 11 && !string.IsNullOrWhiteSpace(array[10])) ? array[10] : array[4]);
		return !string.IsNullOrWhiteSpace(target);
	}

	private static bool TryGetAdminPlayerChoice(string? raw, out string playerName)
	{
		playerName = "";
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		string[] array = raw.Split('\t');
		if (array.Length < 3 || array[0] != "ADMINPLAYER")
		{
			return false;
		}
		playerName = array[1];
		return !string.IsNullOrWhiteSpace(playerName);
	}

	private bool SelectAdminCommandPlayer(string playerName)
	{
		CaptureAdminCommandInputs();
		adminCommandTarget = playerName ?? "";
		pageHeaderText = (string.IsNullOrWhiteSpace(adminCommandTarget) ? SuiteLocalization.Get("gui.selectplayer") : SuiteLocalization.Get("gui.selectedplayer", adminCommandTarget));
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
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		string[] array = raw.Split('\t');
		if (array.Length < 9 || array[0] != "INV")
		{
			return false;
		}
		inventoryId = array[1];
		int.TryParse(array[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot);
		bool flag = array[4].Equals("1", StringComparison.OrdinalIgnoreCase) || array[4].Equals("true", StringComparison.OrdinalIgnoreCase);
		if (!flag && int.TryParse(array[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			quantity = Math.Max(1, result);
		}
		itemCode = (flag ? "" : array[7]);
		fingerprint = ((array.Length >= 14) ? array[13] : "");
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
		if (lines == null || string.IsNullOrWhiteSpace(adminCommandInventoryId))
		{
			return;
		}
		foreach (string line in lines)
		{
			if (!TryGetAdminInventorySlot(line, out string inventoryId, out int slot, out int quantity, out string itemCode, out string fingerprint) || !inventoryId.Equals(adminCommandInventoryId, StringComparison.OrdinalIgnoreCase) || slot != adminCommandSlot)
			{
				continue;
			}
			adminCommandQuantity = Math.Max(1, quantity);
			adminCommandItemCode = itemCode;
			adminCommandInventoryFingerprint = fingerprint;
			break;
		}
	}

	private static bool TryGetAdminItemSearchResult(string? raw, out string itemCode, out int maxStack)
	{
		itemCode = "";
		maxStack = 1;
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		string[] array = raw.Split('\t');
		if (array.Length < 5 || array[0] != "ITEM")
		{
			return false;
		}
		itemCode = array[1];
		if (int.TryParse(array[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			maxStack = Math.Max(1, result);
		}
		return !string.IsNullOrWhiteSpace(itemCode);
	}

	private bool SelectAdminItem(string itemCode, int maxStack)
	{
		adminCommandItemCode = itemCode ?? "";
		adminCommandQuantity = Math.Clamp((adminCommandQuantity <= 0) ? 1 : adminCommandQuantity, 1, Math.Max(1, maxStack));
		pageHeaderText = SuiteLocalization.Get("gui.selecteditem", adminCommandItemCode);
		ComposeDialog();
		return true;
	}

	private static bool TryGetAntiCheatBlacklistActionTarget(string? raw, out string modId, out bool alreadyResolved)
	{
		modId = "";
		alreadyResolved = false;
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		string[] array = raw.Split('\t');
		if (array.Length < 8 || array[0] != "BLACKLIST")
		{
			return false;
		}
		modId = array[1];
		alreadyResolved = int.TryParse(array[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0;
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
		pageHeaderText = (string.IsNullOrWhiteSpace(antiCheatReportSearch) ? "Latest persistent Anti-Cheat reports" : ("Persistent reports for " + antiCheatReportSearch.Trim()));
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
		SetGenericResults(new List<string> { "Adding " + antiCheatBlacklistModId.Trim() + " to the blacklist..." });
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
		SetGenericResults(new List<string> { "Removing " + antiCheatBlacklistModId.Trim() + " from the blacklist..." });
		ComposeDialog();
		return RequestAntiCheat("blacklistremove", antiCheatBlacklistModId);
	}

	private bool SelectAdminServerView(string selected)
	{
		CaptureAdminCommandInputs();
		bool flag = ((selected == "restarts" || selected == "chat") ? true : false);
		adminServerView = (flag ? selected : "operations");
		genericScrollValue = 0.0;
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
		bool flag;
		switch (selected)
		{
		case "moderation":
		case "teleport":
		case "inventory":
		case "server":
		case "reports":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		adminCommandView = (flag ? selected : "players");
		genericScrollValue = 0.0;
		pendingAdminConfirmOperation = "";
		pendingAdminConfirmUntilUtc = DateTime.MinValue;
		string text = adminCommandView;
		if (1 == 0)
		{
		}
		string text2 = text switch
		{
			"moderation" => "Moderation command results", 
			"teleport" => "Teleport command results", 
			"inventory" => "Inventory command results", 
			"server" => "Server command results", 
			"reports" => "Report command results", 
			_ => "Player command results", 
		};
		if (1 == 0)
		{
		}
		pageHeaderText = text2;
		SetGenericResults(new List<string> { "ADMINSTATUS\tOK\t" + AdminCommandViewHelp(adminCommandView) });
		ComposeDialog();
		return true;
	}

	private string[] AdminTeleportPlayerValues() => adminPlayerChoices.Values;

	private string[] AdminTeleportPlayerNames() => adminPlayerChoices.Labels;

	private int AdminTeleportPlayerIndex()
	{
		string[] array = AdminTeleportPlayerValues();
		int num = Array.FindIndex(array, (string p) => string.Equals(p, adminCommandTarget, StringComparison.OrdinalIgnoreCase));
		if (num >= 0)
		{
			return num;
		}
		if (array.Length != 0 && !string.IsNullOrWhiteSpace(array[0]))
		{
			adminCommandTarget = array[0];
		}
		return 0;
	}

	private void OnAdminTeleportPlayerChanged(string value, bool selected)
	{
		if (selected && !string.IsNullOrWhiteSpace(value))
		{
			adminCommandTarget = value;
			SingleComposer?.GetTextInput("admincmd-target")?.SetValue(adminCommandTarget);
			pageHeaderText = SuiteLocalization.Get("gui.selectedplayer", adminCommandTarget);
		}
	}

	private bool RefreshAdminTeleportPlayers()
	{
		CaptureAdminCommandInputs();
		adminPlayerChoices = AdminPlayerChoices.Create((capi.World.AllOnlinePlayers ?? Array.Empty<IPlayer>()).Where(p => p != null).Select(p => (p.PlayerUID, p.PlayerName)));
		string[] array = AdminTeleportPlayerValues();
		if (array.Length != 0 && !string.IsNullOrWhiteSpace(array[0]) && !array.Any((string p) => string.Equals(p, adminCommandTarget, StringComparison.OrdinalIgnoreCase)))
		{
			adminCommandTarget = array[0];
		}
		ComposeDialog();
		return true;
	}

	private static string AdminCommandViewHelp(string selected)
	{
		if (1 == 0)
		{
		}
		string result = selected switch
		{
			"moderation" => "Enter a player/UID and optional duration, record ID, or reason, then choose a moderation action.", 
			"teleport" => "Choose a currently online player from the shared picker for Goto, Bring, or Send Back. Refresh updates joins/leaves. My Back is independent.", 
			"inventory" => "Inventory edits are audited. Remove Slot and Restore Snapshot require a second confirmation click.", 
			"server" => "Server lifecycle and cleanup actions are privilege checked and destructive actions require confirmation.", 
			"reports" => "Review and update saved issue reports without typing /nse report commands.", 
			_ => "Use the online-player picker for normal actions. The adjacent name/UID field is retained for offline records and history lookups.", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void ApplyAutoRestartStatusLine(string raw)
	{
		string[] array = raw.Split('\t');
		if (array.Length >= 8)
		{
			adminAutoRestartTimes = array[2];
			adminAutoRestartWarnings = array[3];
			adminAutoRestartReason = array[4];
			adminAutoRestartGameAnnouncements = array[5] == "1" || array[5].Equals("true", StringComparison.OrdinalIgnoreCase);
			adminAutoRestartDiscordAnnouncements = array[6] == "1" || array[6].Equals("true", StringComparison.OrdinalIgnoreCase);
		}
	}

	private void ApplyChatDisplayStatusLine(string raw)
	{
		string[] array = raw.Split('\t');
		if (array.Length >= 7)
		{
			adminChatShowRole = array[1] == "1" || array[1].Equals("true", StringComparison.OrdinalIgnoreCase);
			adminChatBoldName = array[2] == "1" || array[2].Equals("true", StringComparison.OrdinalIgnoreCase);
			adminChatShowTimestamp = array[3] == "1" || array[3].Equals("true", StringComparison.OrdinalIgnoreCase);
			adminChatUseUtc = array[4] == "1" || array[4].Equals("true", StringComparison.OrdinalIgnoreCase);
			adminChatTimestampFormat = array[5];
			if (array.Length >= 10)
			{
				adminChatFormatGeneral = array[7] == "1" || array[7].Equals("true", StringComparison.OrdinalIgnoreCase);
				adminChatFormatOtherGroups = array[8] == "1" || array[8].Equals("true", StringComparison.OrdinalIgnoreCase);
				adminChatAdditionalGroupIds = array[9];
			}
		}
	}

	private void CaptureAdminCommandInputs()
	{
		if (SingleComposer != null)
		{
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
	}

	private void ApplyAdminCommandControlValues()
	{
		if (SingleComposer != null && !(section != "admincommands"))
		{
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
	}

	private void OnAdminQuantityChanged(string value)
	{
		if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			adminCommandQuantity = Math.Clamp(result, 1, 100000);
		}
	}

	private void OnAdminSlotChanged(string value)
	{
		if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			adminCommandSlot = Math.Clamp(result, 0, 10000);
		}
	}

	private void OnAdminRecordIdChanged(string value)
	{
		if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			adminCommandRecordId = Math.Max(0L, result);
		}
	}

	private void OnAdminRadiusChanged(string value)
	{
		if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			adminCommandRadius = Math.Clamp(result, 1, 16);
		}
	}

	private bool RunAdminCommand(string operation, bool dangerous = false)
	{
		CaptureAdminCommandInputs();
		DateTime utcNow = DateTime.UtcNow;
		if (dangerous && (!string.Equals(pendingAdminConfirmOperation, operation, StringComparison.OrdinalIgnoreCase) || utcNow > pendingAdminConfirmUntilUtc))
		{
			pendingAdminConfirmOperation = operation;
			pendingAdminConfirmUntilUtc = utcNow.AddSeconds(5.0);
			pageHeaderText = SuiteLocalization.Get("gui.confirmationrequired");
			SetGenericResults(new List<string>
			{
				"ADMINSTATUS\tERROR\tClick " + AdminOperationName(operation) + " again within 5 seconds to confirm.",
				"ADMINLINE\tNo server-side change has been made yet."
			});
			ComposeDialog();
			return true;
		}
		bool confirmed = dangerous;
		pendingAdminConfirmOperation = "";
		pendingAdminConfirmUntilUtc = DateTime.MinValue;
		pageHeaderText = SuiteLocalization.Get("gui.runningoperation", AdminOperationName(operation));
		SetGenericResults(new List<string> { "ADMINSTATUS\tOK\tSending " + AdminOperationName(operation) + " to the server..." });
		ComposeDialog();
		AdminCommandGuiRequest adminCommandGuiRequest = new AdminCommandGuiRequest
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
			adminCommandGuiRequest.Value = adminCommandDuration.Trim();
			break;
		case "autorestart-save":
			adminCommandGuiRequest.Value = adminAutoRestartTimes.Trim();
			adminCommandGuiRequest.Secondary = adminAutoRestartWarnings.Trim();
			adminCommandGuiRequest.Reason = adminAutoRestartReason.Trim();
			adminCommandGuiRequest.IntValue = (adminAutoRestartGameAnnouncements ? 1 : 0) | (adminAutoRestartDiscordAnnouncements ? 2 : 0);
			break;
		case "chatdisplay-save":
			adminCommandGuiRequest.Value = adminChatTimestampFormat.Trim();
			adminCommandGuiRequest.Secondary = adminChatAdditionalGroupIds.Trim();
			adminCommandGuiRequest.IntValue = (adminChatShowRole ? 1 : 0) | (adminChatShowTimestamp ? 2 : 0) | (adminChatUseUtc ? 4 : 0) | (adminChatBoldName ? 8 : 0) | (adminChatFormatGeneral ? 16 : 0) | (adminChatFormatOtherGroups ? 32 : 0);
			break;
		case "itemsearch":
			adminCommandGuiRequest.Value = adminCommandItemSearch.Trim();
			break;
		case "invgive":
			adminCommandGuiRequest.Value = adminCommandItemCode.Trim();
			adminCommandGuiRequest.IntValue = adminCommandQuantity;
			break;
		case "invgive-selected":
			adminCommandGuiRequest.Value = adminCommandItemCode.Trim();
			adminCommandGuiRequest.Secondary = adminCommandInventoryId.Trim();
			adminCommandGuiRequest.IntValue = adminCommandQuantity;
			adminCommandGuiRequest.IntValue2 = adminCommandSlot;
			adminCommandGuiRequest.ExpectedFingerprint = adminCommandInventoryFingerprint;
			break;
		case "invqty":
			adminCommandGuiRequest.Value = adminCommandInventoryId.Trim();
			adminCommandGuiRequest.IntValue = adminCommandSlot;
			adminCommandGuiRequest.IntValue2 = adminCommandQuantity;
			adminCommandGuiRequest.ExpectedFingerprint = adminCommandInventoryFingerprint;
			break;
		case "invremove":
			adminCommandGuiRequest.Value = adminCommandInventoryId.Trim();
			adminCommandGuiRequest.IntValue = adminCommandSlot;
			adminCommandGuiRequest.ExpectedFingerprint = adminCommandInventoryFingerprint;
			break;
		case "invreplace":
			adminCommandGuiRequest.Value = adminCommandItemCode.Trim();
			adminCommandGuiRequest.Secondary = adminCommandInventoryId.Trim();
			adminCommandGuiRequest.IntValue = adminCommandSlot;
			adminCommandGuiRequest.IntValue2 = adminCommandQuantity;
			adminCommandGuiRequest.ExpectedFingerprint = adminCommandInventoryFingerprint;
			break;
		case "laghere":
			adminCommandGuiRequest.IntValue = adminCommandRadius;
			break;
		case "report-status":
		case "report-assign":
			adminCommandGuiRequest.Value = adminCommandReportValue.Trim();
			break;
		}
		lastResponseId = adminCommandGuiRequest.RequestId;
		sendAdminCommand(adminCommandGuiRequest);
		return true;
	}

	private static string AdminOperationName(string operation)
	{
		if (1 == 0)
		{
		}
		string result = operation switch
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
			_ => operation, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void BuildDiscordIntegrationPage(GuiComposer composer)
	{
		ElementBounds bounds = ElementBounds.Fixed(22.0, 122.0, 112.0, 30.0);
		ElementBounds bounds2 = ElementBounds.Fixed(146.0, 122.0, 148.0, 30.0);
		ElementBounds bounds3 = ElementBounds.Fixed(306.0, 122.0, 102.0, 30.0);
		ElementBounds bounds4 = ElementBounds.Fixed(420.0, 122.0, 102.0, 30.0);
		ElementBounds bounds5 = ElementBounds.Fixed(534.0, 122.0, 110.0, 30.0);
		composer.AddSuiteButton(SuiteLocalization.Get("gui.overview"), () => SelectDiscordView("overview"), bounds, discordView == "overview").AddSuiteButton(SuiteLocalization.Get("gui.discordannouncements"), () => SelectDiscordView("announcements"), bounds2, discordView == "announcements").AddSuiteButton(SuiteLocalization.Get("gui.discordlinking"), () => SelectDiscordView("linking"), bounds3, discordView == "linking")
			.AddSuiteButton(SuiteLocalization.Get("gui.discordrouting"), () => SelectDiscordView("routing"), bounds4, discordView == "routing")
			.AddSuiteButton("MOTD", () => SelectDiscordView("motd"), bounds5, discordView == "motd")
			.AddSuiteButton(SuiteLocalization.Get("gui.refresh"), () => RequestDiscordIntegration((discordView == "motd") ? "motd-get" : "get"), ElementBounds.Fixed(1180.0, 122.0, 110.0, 30.0));
		composer.AddSuiteInset(ElementBounds.Fixed(16.0, 164.0, 1328.0, 576.0));
		if (discordView == "motd")
		{
			BuildDiscordMotdPage(composer, 164.0);
			return;
		}
		if (discordView == "announcements")
		{
			composer.AddRichtext("<font color=\"#7db6ff\"><strong>Discord Announcements</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 176.0, 390.0, 26.0)).AddStaticText("Each event can be enabled independently, routed to its own channel, and formatted separately. Channel ID 0 inherits the main Discord chat channel.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 204.0, 1190.0, 42.0));
			double num = 252.0;
			AddDiscordAnnouncementRow(composer, "playerJoin", "Player joined", discordSettings.PlayerJoin, num);
			num += 66.0;
			AddDiscordAnnouncementRow(composer, "playerLeave", "Player left", discordSettings.PlayerLeave, num);
			num += 66.0;
			AddDiscordAnnouncementRow(composer, "playerDeath", "Player death", discordSettings.PlayerDeath, num);
			num += 66.0;
			AddDiscordAnnouncementRow(composer, "serverStarted", "Server started", discordSettings.ServerStarted, num);
			num += 66.0;
			AddDiscordAnnouncementRow(composer, "serverStopping", "Server stopping", discordSettings.ServerStopping, num);
			num += 66.0;
			AddDiscordAnnouncementRow(composer, "discordLinked", "Discord linked", discordSettings.DiscordLinked, num);
			num += 66.0;
			AddDiscordAnnouncementRow(composer, "discordUnlinked", "Discord unlinked", discordSettings.DiscordUnlinked, num);
			return;
		}
		if (discordView == "linking")
		{
			string text;
			if (discordSettings.LinkAvailable)
			{
				text = (discordSettings.LinkEnabled ? "<font color=\"#8fda72\"><strong>AVAILABLE / ENABLED</strong></font>" : "<font color=\"#ffd166\"><strong>AVAILABLE / DISABLED</strong></font>");
			}
			else
			{
				text = "<font color=\"#ff7070\"><strong>UNAVAILABLE</strong></font>";
			}
			composer.AddRichtext("<font color=\"#7db6ff\"><strong>Discord Account Linking</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 176.0, 390.0, 26.0)).AddRichtext("Account linking: " + text, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 212.0, 560.0, 28.0)).AddStaticText($"Linked accounts: {discordSettings.LinkedAccountCount}     Pending codes: {discordSettings.PendingLinkCodeCount}", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 242.0, 560.0, 26.0))
				.AddStaticText("Enable account linking", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 290.0, 240.0, 24.0))
				.AddSwitch((bool value3) =>
				{
					SetDiscordBool("link.enabled", value3);
				}, ElementBounds.Fixed(286.0, 284.0, 52.0, 26.0), "discord-link-enabled")
				.AddStaticText("Code length", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 336.0, 180.0, 24.0))
				.AddNumberInput(ElementBounds.Fixed(210.0, 330.0, 110.0, 28.0), (string s) =>
				{
					if (int.TryParse(s, out var result))
					{
						discordSettings.LinkCodeLength = Math.Clamp(result, 6, 12);
					}
				}, CairoFont.WhiteSmallText(), "discord-link-code-length")
				.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordInt("link.codelength", discordSettings.LinkCodeLength), ElementBounds.Fixed(334.0, 330.0, 84.0, 28.0))
				.AddStaticText("Code expiry (seconds)", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 378.0, 180.0, 24.0))
				.AddNumberInput(ElementBounds.Fixed(210.0, 372.0, 110.0, 28.0), (string s) =>
				{
					if (int.TryParse(s, out var result))
					{
						discordSettings.LinkCodeExpirySeconds = Math.Clamp(result, 60, 3600);
					}
				}, CairoFont.WhiteSmallText(), "discord-link-expiry")
				.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordInt("link.codeexpiryseconds", discordSettings.LinkCodeExpirySeconds), ElementBounds.Fixed(334.0, 372.0, 84.0, 28.0))
				.AddStaticText("Code cooldown (seconds)", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 420.0, 180.0, 24.0))
				.AddNumberInput(ElementBounds.Fixed(210.0, 414.0, 110.0, 28.0), (string s) =>
				{
					if (int.TryParse(s, out var result))
					{
						discordSettings.LinkCodeCooldownSeconds = Math.Clamp(result, 0, 600);
					}
				}, CairoFont.WhiteSmallText(), "discord-link-cooldown")
				.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordInt("link.codecooldownseconds", discordSettings.LinkCodeCooldownSeconds), ElementBounds.Fixed(334.0, 414.0, 84.0, 28.0))
				.AddStaticText("Allow one Discord account to link multiple VS accounts", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 468.0, 430.0, 24.0))
				.AddSwitch((bool value3) =>
				{
					SetDiscordBool("link.allowmultiplevs", value3);
				}, ElementBounds.Fixed(476.0, 462.0, 52.0, 26.0), "discord-link-multiple")
				.AddStaticText("Log link/unlink changes", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 510.0, 250.0, 24.0))
				.AddSwitch((bool value3) =>
				{
					SetDiscordBool("link.logchanges", value3);
				}, ElementBounds.Fixed(286.0, 504.0, 52.0, 26.0), "discord-link-log")
				.AddStaticText("Enable Discord /link commands", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 552.0, 250.0, 24.0))
				.AddSwitch((bool value3) =>
				{
					SetDiscordBool("commands.linkenabled", value3);
				}, ElementBounds.Fixed(286.0, 546.0, 52.0, 26.0), "discord-link-commands")
				.AddStaticText("Link records are keyed by the permanent Vintage Story PlayerUID. The Discord bot token is never sent to game clients.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 602.0, 900.0, 42.0));
			return;
		}
		if (discordView == "routing")
		{
			composer.AddRichtext("<font color=\"#7db6ff\"><strong>Discord Channels & Routing</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 176.0, 420.0, 26.0)).AddStaticText("Main chat channel ID", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 224.0, 210.0, 24.0)).AddTextInput(ElementBounds.Fixed(246.0, 218.0, 250.0, 28.0), (string text3) =>
			{
				discordSettings.ChatChannelId = text3 ?? "";
			}, CairoFont.WhiteSmallText(), "discord-chat-channel")
				.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("chat.channelid", "discord-chat-channel", discordSettings.ChatChannelId), ElementBounds.Fixed(508.0, 218.0, 84.0, 28.0))
				.AddStaticText("Bold VS role + player label in Discord", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 272.0, 290.0, 24.0))
				.AddSwitch((bool value3) =>
				{
					SetDiscordBool("chat.boldgamesenderlabel", value3);
				}, ElementBounds.Fixed(330.0, 266.0, 52.0, 26.0), "discord-bold-game-sender")
				.AddStaticText("Use VS role color in Discord (embed accent)", CairoFont.WhiteSmallText(), ElementBounds.Fixed(430.0, 272.0, 330.0, 24.0))
				.AddSwitch((bool value3) =>
				{
					SetDiscordBool("chat.usegamerolecolor", value3);
				}, ElementBounds.Fixed(766.0, 266.0, 52.0, 26.0), "discord-game-role-color")
				.AddStaticText("Admin audit announcements", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 320.0, 250.0, 24.0))
				.AddSwitch((bool value3) =>
				{
					SetDiscordBool("adminaudit.enabled", value3);
				}, ElementBounds.Fixed(286.0, 314.0, 52.0, 26.0), "discord-admin-enabled")
				.AddStaticText("Channel ID", CairoFont.WhiteSmallText(), ElementBounds.Fixed(368.0, 320.0, 100.0, 24.0))
				.AddTextInput(ElementBounds.Fixed(468.0, 314.0, 250.0, 28.0), (string text3) =>
				{
					discordSettings.AdminAuditChannelId = text3 ?? "";
				}, CairoFont.WhiteSmallText(), "discord-admin-channel")
				.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("adminaudit.channelid", "discord-admin-channel", discordSettings.AdminAuditChannelId), ElementBounds.Fixed(730.0, 314.0, 84.0, 28.0))
				.AddSuiteButton("Test", () => TestDiscord("admin"), ElementBounds.Fixed(826.0, 314.0, 84.0, 28.0))
				.AddStaticText("Client Integrity / Anti-Cheat", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 368.0, 250.0, 24.0))
				.AddSwitch((bool value3) =>
				{
					SetDiscordBool("anticheat.enabled", value3);
				}, ElementBounds.Fixed(286.0, 362.0, 52.0, 26.0), "discord-ac-enabled")
				.AddStaticText("Channel ID", CairoFont.WhiteSmallText(), ElementBounds.Fixed(368.0, 368.0, 100.0, 24.0))
				.AddTextInput(ElementBounds.Fixed(468.0, 362.0, 250.0, 28.0), (string text3) =>
				{
					discordSettings.AntiCheatChannelId = text3 ?? "";
				}, CairoFont.WhiteSmallText(), "discord-ac-channel")
				.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("anticheat.channelid", "discord-ac-channel", discordSettings.AntiCheatChannelId), ElementBounds.Fixed(730.0, 362.0, 84.0, 28.0))
				.AddSuiteButton("Test", () => TestDiscord("anticheat"), ElementBounds.Fixed(826.0, 362.0, 84.0, 28.0))
				.AddStaticText("Connected guild text channels", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 426.0, 340.0, 24.0));
			string text2 = (string.IsNullOrWhiteSpace(discordSettings.Channels) ? "No channel list is available. Connect the bot and set the Guild ID, then refresh." : string.Join("\n", discordSettings.Channels.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(14).Select((string line) =>
			{
				string[] array = line.Split('\t', 2);
				return (array.Length == 2) ? ("#" + array[1] + "    " + array[0]) : line;
			})));
			composer.AddStaticText(text2, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 458.0, 1180.0, 220.0));
			return;
		}
		string value;
		if (discordSettings.TransportAvailable)
		{
			value = (discordSettings.TransportConnected ? "<font color=\"#8fda72\"><strong>CONNECTED</strong></font>" : "<font color=\"#ffd166\"><strong>NOT CONNECTED</strong></font>");
		}
		else
		{
			value = "<font color=\"#ff7070\"><strong>UNAVAILABLE</strong></font>";
		}
		string value2;
		if (discordSettings.LinkAvailable)
		{
			value2 = (discordSettings.LinkEnabled ? "<font color=\"#8fda72\"><strong>ENABLED</strong></font>" : "<font color=\"#ffd166\"><strong>DISABLED</strong></font>");
		}
		else
		{
			value2 = "<font color=\"#91a0ae\"><strong>UNAVAILABLE</strong></font>";
		}
		composer.AddRichtext("<font color=\"#7db6ff\"><strong>Discord Overview</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 176.0, 440.0, 26.0)).AddRichtext($"Discord bot: {value}<br>Account linking: {value2}<br>Bot account: <strong>{Escape(string.IsNullOrWhiteSpace(discordSettings.BotName) ? "Not connected" : discordSettings.BotName)}</strong><br>Linked accounts: <strong>{discordSettings.LinkedAccountCount}</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 212.0, 560.0, 106.0)).AddRichtext(discordSettings.BotTokenConfigured ? "<font color=\"#8fda72\">Bot token configured on server.</font>" : "<font color=\"#ff7070\">Bot token is not configured.</font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(650.0, 212.0, 540.0, 26.0))
			.AddStaticText("Bot tokens are intentionally excluded from Shift+L and all client packets.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(650.0, 242.0, 600.0, 26.0))
			.AddStaticText("Discord module enabled", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 340.0, 240.0, 24.0))
			.AddSwitch((bool value3) =>
			{
				SetDiscordBool("transport.enabled", value3);
			}, ElementBounds.Fixed(286.0, 334.0, 52.0, 26.0), "discord-transport-enabled")
			.AddStaticText("Chat bridge enabled", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 380.0, 240.0, 24.0))
			.AddSwitch((bool value3) =>
			{
				SetDiscordBool("chat.enabled", value3);
			}, ElementBounds.Fixed(286.0, 374.0, 52.0, 26.0), "discord-chat-enabled")
			.AddStaticText("Game → Discord", CairoFont.WhiteSmallText(), ElementBounds.Fixed(372.0, 380.0, 160.0, 24.0))
			.AddSwitch((bool value3) =>
			{
				SetDiscordBool("chat.gametodiscord", value3);
			}, ElementBounds.Fixed(534.0, 374.0, 52.0, 26.0), "discord-g2d")
			.AddStaticText("Discord → Game", CairoFont.WhiteSmallText(), ElementBounds.Fixed(620.0, 380.0, 160.0, 24.0))
			.AddSwitch((bool value3) =>
			{
				SetDiscordBool("chat.discordtogame", value3);
			}, ElementBounds.Fixed(782.0, 374.0, 52.0, 26.0), "discord-d2g")
			.AddStaticText("Slash commands", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 422.0, 150.0, 24.0))
			.AddSwitch((bool value3) =>
			{
				SetDiscordBool("commands.enabled", value3);
			}, ElementBounds.Fixed(184.0, 416.0, 52.0, 26.0), "discord-commands-enabled")
			.AddStaticText("Player info (/players, /restarttime)", CairoFont.WhiteSmallText(), ElementBounds.Fixed(270.0, 422.0, 260.0, 24.0))
			.AddSwitch((bool value3) =>
			{
				SetDiscordBool("commands.playerinfoenabled", value3);
			}, ElementBounds.Fixed(532.0, 416.0, 52.0, 26.0), "discord-playerinfo-commands")
			.AddStaticText("Staff moderation", CairoFont.WhiteSmallText(), ElementBounds.Fixed(620.0, 422.0, 126.0, 24.0))
			.AddSwitch((bool value3) =>
			{
				SetDiscordBool("commands.staffenabled", value3);
			}, ElementBounds.Fixed(748.0, 416.0, 52.0, 26.0), "discord-staff-commands")
			.AddStaticText("Restart command", CairoFont.WhiteSmallText(), ElementBounds.Fixed(830.0, 422.0, 150.0, 24.0))
			.AddSwitch((bool value3) =>
			{
				SetDiscordBool("commands.restartenabled", value3);
			}, ElementBounds.Fixed(982.0, 416.0, 52.0, 26.0), "discord-restart-commands")
			.AddStaticText("Guild ID", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 474.0, 150.0, 24.0))
			.AddTextInput(ElementBounds.Fixed(184.0, 468.0, 300.0, 28.0), (string text3) =>
			{
				discordSettings.GuildId = text3 ?? "";
			}, CairoFont.WhiteSmallText(), "discord-guild-id")
			.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("transport.guildid", "discord-guild-id", discordSettings.GuildId), ElementBounds.Fixed(496.0, 468.0, 84.0, 28.0))
			.AddStaticText("Invite URL", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 516.0, 150.0, 24.0))
			.AddTextInput(ElementBounds.Fixed(184.0, 510.0, 650.0, 28.0), (string text3) =>
			{
				discordSettings.InviteUrl = text3 ?? "";
			}, CairoFont.WhiteSmallText(), "discord-invite-url")
			.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SendDiscordTextInput("transport.inviteurl", "discord-invite-url", discordSettings.InviteUrl), ElementBounds.Fixed(846.0, 510.0, 84.0, 28.0))
			.AddSuiteButton("Test Main Channel", () => TestDiscord("chat"), ElementBounds.Fixed(34.0, 570.0, 180.0, 30.0))
			.AddStaticText(discordStatusMessage, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 622.0, 1150.0, 54.0));
	}

	private void BuildDiscordMotdPage(GuiComposer composer, double sy)
	{
		string text = (motdAvailable ? ("<font color=\"#8fda72\"><strong>CONNECTED" + (string.IsNullOrWhiteSpace(motdVersion) ? "" : (" · v" + Escape(motdVersion))) + "</strong></font>") : "<font color=\"#ff7070\"><strong>NOT INSTALLED</strong></font>");
		composer.AddRichtext("<font color=\"#7db6ff\"><strong>Community MOTD Configuration</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, sy + 12.0, 420.0, 26.0)).AddRichtext("Companion: " + text, CairoFont.WhiteSmallText(), ElementBounds.Fixed(470.0, sy + 12.0, 360.0, 26.0)).AddSuiteButton("General", () => SelectMotdView("general"), ElementBounds.Fixed(34.0, sy + 46.0, 108.0, 28.0), motdView == "general")
			.AddSuiteButton("Pages & Feeds", () => SelectMotdView("pages"), ElementBounds.Fixed(152.0, sy + 46.0, 132.0, 28.0), motdView == "pages")
			.AddSuiteButton("Home VTML", () => SelectMotdView("home"), ElementBounds.Fixed(294.0, sy + 46.0, 116.0, 28.0), motdView == "home")
			.AddSuiteButton("Rules VTML", () => SelectMotdView("rules"), ElementBounds.Fixed(420.0, sy + 46.0, 116.0, 28.0), motdView == "rules")
			.AddSuiteButton("Reload Disk", () => RequestDiscordIntegration("motd-reload"), ElementBounds.Fixed(900.0, sy + 46.0, 116.0, 28.0))
			.AddSuiteButton("Reset Seen", () => RequestDiscordIntegration("motd-resetseen"), ElementBounds.Fixed(1026.0, sy + 46.0, 108.0, 28.0))
			.AddSuiteButton("Save & Apply", SaveMotdFromShiftL, ElementBounds.Fixed(1144.0, sy + 46.0, 130.0, 28.0))
			.AddStaticText(motdStatusMessage, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, sy + 80.0, 1240.0, 34.0));
		if (!motdAvailable)
		{
			composer.AddStaticText("Install Newfie's Community MOTD on the server to enable this page. Discord and Server Suite continue to work normally when it is absent.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, sy + 132.0, 1160.0, 48.0));
			return;
		}
		if (motdView == "home" || motdView == "rules")
		{
			string key = ((motdView == "home") ? "Content" : "RulesVtml");
			string text2 = ((motdView == "home") ? "Home page VTML" : "Rules page VTML");
			string key2 = ((motdView == "home") ? "motd-home-editor" : "motd-rules-editor");
			composer.AddRichtext("<font color=\"#ffc857\"><strong>" + text2 + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 286.0, 420.0, 26.0)).AddStaticText("Free-form VTML. Changes are staged locally until Save & Apply is pressed.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 316.0, 850.0, 24.0)).AddTextArea(ElementBounds.Fixed(34.0, 348.0, 1240.0, 350.0), (string value) =>
			{
				SetMotdStringLocal(key, value);
			}, CairoFont.SmallTextInput(), key2);
			return;
		}
		if (motdView == "pages")
		{
			composer.AddRichtext("<font color=\"#ffc857\"><strong>Hub Pages</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 286.0, 300.0, 24.0)).AddStaticText("News", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 326.0, 130.0, 22.0)).AddSwitch((bool v) =>
			{
				SetMotdBoolLocal("EnableAnnouncementsTab", v);
			}, ElementBounds.Fixed(176.0, 320.0, 52.0, 26.0), "motd-page-news")
				.AddStaticText("Updates", CairoFont.WhiteSmallText(), ElementBounds.Fixed(262.0, 326.0, 130.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("EnableUpdatesTab", v);
				}, ElementBounds.Fixed(400.0, 320.0, 52.0, 26.0), "motd-page-updates")
				.AddStaticText("Events", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 368.0, 130.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("EnableEventsTab", v);
				}, ElementBounds.Fixed(176.0, 362.0, 52.0, 26.0), "motd-page-events")
				.AddStaticText("Rules", CairoFont.WhiteSmallText(), ElementBounds.Fixed(262.0, 368.0, 130.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("EnableRulesTab", v);
				}, ElementBounds.Fixed(400.0, 362.0, 52.0, 26.0), "motd-page-rules")
				.AddStaticText("Server Info", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 410.0, 130.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("EnableServerTab", v);
				}, ElementBounds.Fixed(176.0, 404.0, 52.0, 26.0), "motd-page-server")
				.AddStaticText("Commands", CairoFont.WhiteSmallText(), ElementBounds.Fixed(262.0, 410.0, 130.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("EnableCommandsTab", v);
				}, ElementBounds.Fixed(400.0, 404.0, 52.0, 26.0), "motd-page-commands")
				.AddRichtext("<font color=\"#7db6ff\"><strong>Discord Feeds</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 460.0, 300.0, 24.0))
				.AddStaticText("Use Discord announcements", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 496.0, 230.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("UseDiscordAnnouncements", v);
				}, ElementBounds.Fixed(278.0, 490.0, 52.0, 26.0), "motd-feed-news")
				.AddStaticText("Use Discord updates", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 538.0, 230.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("UseDiscordUpdates", v);
				}, ElementBounds.Fixed(278.0, 532.0, 52.0, 26.0), "motd-feed-updates")
				.AddStaticText("Use Discord events", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 580.0, 230.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("UseDiscordEvents", v);
				}, ElementBounds.Fixed(278.0, 574.0, 52.0, 26.0), "motd-feed-events")
				.AddStaticText("Merge local + Discord", CairoFont.WhiteSmallText(), ElementBounds.Fixed(356.0, 496.0, 210.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("MergeLocalAndDiscord", v);
				}, ElementBounds.Fixed(574.0, 490.0, 52.0, 26.0), "motd-feed-merge")
				.AddStaticText("Max items per feed", CairoFont.WhiteSmallText(), ElementBounds.Fixed(356.0, 538.0, 170.0, 22.0))
				.AddNumberInput(ElementBounds.Fixed(530.0, 532.0, 96.0, 28.0), (string value) =>
				{
					SetMotdIntLocal("DiscordMaxItemsPerFeed", value, 1, 50, 10);
				}, CairoFont.WhiteSmallText(), "motd-feed-max")
				.AddStaticText("Use Suite status", CairoFont.WhiteSmallText(), ElementBounds.Fixed(356.0, 580.0, 170.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("UseSuiteStatus", v);
				}, ElementBounds.Fixed(530.0, 574.0, 52.0, 26.0), "motd-suite-status")
				.AddRichtext("<font color=\"#ffc857\"><strong>Server Info Categories</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(680.0, 286.0, 330.0, 24.0))
				.AddStaticText("Server", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 326.0, 150.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("ServerInfoShowServer", v);
				}, ElementBounds.Fixed(846.0, 320.0, 52.0, 26.0), "motd-info-server")
				.AddStaticText("World", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930.0, 326.0, 150.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("ServerInfoShowWorld", v);
				}, ElementBounds.Fixed(1092.0, 320.0, 52.0, 26.0), "motd-info-world")
				.AddStaticText("Gameplay", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 368.0, 150.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("ServerInfoShowGameplay", v);
				}, ElementBounds.Fixed(846.0, 362.0, 52.0, 26.0), "motd-info-gameplay")
				.AddStaticText("Prospecting", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930.0, 368.0, 150.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("ServerInfoShowProspecting", v);
				}, ElementBounds.Fixed(1092.0, 362.0, 52.0, 26.0), "motd-info-prospecting")
				.AddStaticText("Temporal", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 410.0, 150.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("ServerInfoShowTemporal", v);
				}, ElementBounds.Fixed(846.0, 404.0, 52.0, 26.0), "motd-info-temporal")
				.AddStaticText("Installed mods", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930.0, 410.0, 150.0, 22.0))
				.AddSwitch((bool v) =>
				{
					SetMotdBoolLocal("ServerInfoShowMods", v);
				}, ElementBounds.Fixed(1092.0, 404.0, 52.0, 26.0), "motd-info-mods")
				.AddStaticText("These controls edit the same Community MOTD server config used by the WebPanel. Nothing is stored in Discord.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 474.0, 540.0, 52.0));
			return;
		}
		composer.AddRichtext("<font color=\"#ffc857\"><strong>General</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 286.0, 280.0, 24.0)).AddStaticText("Title", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 324.0, 120.0, 22.0)).AddTextInput(ElementBounds.Fixed(168.0, 318.0, 430.0, 28.0), (string v) =>
		{
			SetMotdStringLocal("Title", v);
		}, CairoFont.WhiteSmallText(), "motd-title")
			.AddStaticText("Subtitle", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 364.0, 120.0, 22.0))
			.AddTextInput(ElementBounds.Fixed(168.0, 358.0, 430.0, 28.0), (string v) =>
			{
				SetMotdStringLocal("Subtitle", v);
			}, CairoFont.WhiteSmallText(), "motd-subtitle")
			.AddStaticText("Width", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 404.0, 90.0, 22.0))
			.AddNumberInput(ElementBounds.Fixed(168.0, 398.0, 110.0, 28.0), (string v) =>
			{
				SetMotdIntLocal("Width", v, 700, 1600, 980);
			}, CairoFont.WhiteSmallText(), "motd-width")
			.AddStaticText("Height", CairoFont.WhiteSmallText(), ElementBounds.Fixed(310.0, 404.0, 90.0, 22.0))
			.AddNumberInput(ElementBounds.Fixed(398.0, 398.0, 110.0, 28.0), (string v) =>
			{
				SetMotdIntLocal("Height", v, 520, 1000, 720);
			}, CairoFont.WhiteSmallText(), "motd-height")
			.AddStaticText("Version label", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 444.0, 120.0, 22.0))
			.AddTextInput(ElementBounds.Fixed(168.0, 438.0, 250.0, 28.0), (string v) =>
			{
				SetMotdStringLocal("VersionLabel", v);
			}, CairoFont.WhiteSmallText(), "motd-version-label")
			.AddStaticText("Open every join", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 490.0, 180.0, 22.0))
			.AddSwitch((bool v) =>
			{
				SetMotdBoolLocal("OpenOnEveryJoin", v);
			}, ElementBounds.Fixed(224.0, 484.0, 52.0, 26.0), "motd-every-join")
			.AddStaticText("Once per session", CairoFont.WhiteSmallText(), ElementBounds.Fixed(310.0, 490.0, 170.0, 22.0))
			.AddSwitch((bool v) =>
			{
				SetMotdBoolLocal("OnlyOncePerSession", v);
			}, ElementBounds.Fixed(486.0, 484.0, 52.0, 26.0), "motd-once-session")
			.AddStaticText("Once per version", CairoFont.WhiteSmallText(), ElementBounds.Fixed(38.0, 532.0, 180.0, 22.0))
			.AddSwitch((bool v) =>
			{
				SetMotdBoolLocal("OnlyOncePerVersion", v);
			}, ElementBounds.Fixed(224.0, 526.0, 52.0, 26.0), "motd-once-version")
			.AddStaticText("Allow don't show again", CairoFont.WhiteSmallText(), ElementBounds.Fixed(310.0, 532.0, 190.0, 22.0))
			.AddSwitch((bool v) =>
			{
				SetMotdBoolLocal("EnableDontShowAgain", v);
			}, ElementBounds.Fixed(508.0, 526.0, 52.0, 26.0), "motd-dont-show")
			.AddRichtext("<font color=\"#7db6ff\"><strong>Branding & Links</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(680.0, 286.0, 320.0, 24.0))
			.AddStaticText("Enable logo", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 324.0, 150.0, 22.0))
			.AddSwitch((bool v) =>
			{
				SetMotdBoolLocal("EnableLogo", v);
			}, ElementBounds.Fixed(846.0, 318.0, 52.0, 26.0), "motd-logo-enabled")
			.AddStaticText("Logo source", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930.0, 324.0, 130.0, 22.0))
			.AddTextInput(ElementBounds.Fixed(1060.0, 318.0, 180.0, 28.0), (string v) =>
			{
				SetMotdStringLocal("LogoSource", v);
			}, CairoFont.WhiteSmallText(), "motd-logo-source")
			.AddStaticText("Server logo file", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 364.0, 150.0, 22.0))
			.AddTextInput(ElementBounds.Fixed(846.0, 358.0, 394.0, 28.0), (string v) =>
			{
				SetMotdStringLocal("ServerLogoFile", v);
			}, CairoFont.WhiteSmallText(), "motd-logo-file")
			.AddStaticText("Logo width", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 404.0, 120.0, 22.0))
			.AddNumberInput(ElementBounds.Fixed(806.0, 398.0, 100.0, 28.0), (string v) =>
			{
				SetMotdIntLocal("LogoWidth", v, 0, 400, 180);
			}, CairoFont.WhiteSmallText(), "motd-logo-width")
			.AddStaticText("Height", CairoFont.WhiteSmallText(), ElementBounds.Fixed(930.0, 404.0, 80.0, 22.0))
			.AddNumberInput(ElementBounds.Fixed(1010.0, 398.0, 100.0, 28.0), (string v) =>
			{
				SetMotdIntLocal("LogoHeight", v, 0, 400, 0);
			}, CairoFont.WhiteSmallText(), "motd-logo-height")
			.AddStaticText("Preserve aspect", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 444.0, 150.0, 22.0))
			.AddSwitch((bool v) =>
			{
				SetMotdBoolLocal("LogoPreserveAspectRatio", v);
			}, ElementBounds.Fixed(846.0, 438.0, 52.0, 26.0), "motd-logo-aspect")
			.AddStaticText("Show link button", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 490.0, 150.0, 22.0))
			.AddSwitch((bool v) =>
			{
				SetMotdBoolLocal("EnableLinkButton", v);
			}, ElementBounds.Fixed(846.0, 484.0, 52.0, 26.0), "motd-link-enabled")
			.AddTextInput(ElementBounds.Fixed(914.0, 484.0, 148.0, 28.0), (string v) =>
			{
				SetMotdStringLocal("LinkButtonText", v);
			}, CairoFont.WhiteSmallText(), "motd-link-text")
			.AddTextInput(ElementBounds.Fixed(1072.0, 484.0, 168.0, 28.0), (string v) =>
			{
				SetMotdStringLocal("LinkUrl", v);
			}, CairoFont.WhiteSmallText(), "motd-link-url")
			.AddStaticText("Show Web Map", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 532.0, 150.0, 22.0))
			.AddSwitch((bool v) =>
			{
				SetMotdBoolLocal("EnableWebMapButton", v);
			}, ElementBounds.Fixed(846.0, 526.0, 52.0, 26.0), "motd-webmap-enabled")
			.AddTextInput(ElementBounds.Fixed(914.0, 526.0, 148.0, 28.0), (string v) =>
			{
				SetMotdStringLocal("WebMapButtonText", v);
			}, CairoFont.WhiteSmallText(), "motd-webmap-text")
			.AddTextInput(ElementBounds.Fixed(1072.0, 526.0, 168.0, 28.0), (string v) =>
			{
				SetMotdStringLocal("WebMapUrl", v);
			}, CairoFont.WhiteSmallText(), "motd-webmap-url")
			.AddStaticText("Logo source accepts Asset or ServerFile. Server logo paths are relative to NewfiesCommunityMOTD/.", CairoFont.WhiteSmallText(), ElementBounds.Fixed(684.0, 578.0, 550.0, 48.0));
	}

	private bool SelectMotdView(string selected)
	{
		bool flag;
		switch (selected)
		{
		case "pages":
		case "home":
		case "rules":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		motdView = (flag ? selected : "general");
		ComposeDialog();
		ApplyDiscordControlValues();
		return true;
	}

	private string MotdString(string key, string fallback = "")
	{
		return motdConfig[key]?.Value<string>() ?? fallback;
	}

	private bool MotdBool(string key, bool fallback = false)
	{
		return motdConfig[key]?.Value<bool?>() ?? fallback;
	}

	private int MotdInt(string key, int fallback)
	{
		return motdConfig[key]?.Value<int?>() ?? fallback;
	}

	private void SetMotdStringLocal(string key, string? value)
	{
		if (!applyingMotdControls)
		{
			motdConfig[key] = value ?? "";
			if (key == "Content")
			{
				motdConfig["ContentLines"] = new JArray();
			}
		}
	}

	private void SetMotdBoolLocal(string key, bool value)
	{
		if (!applyingMotdControls)
		{
			motdConfig[key] = value;
		}
	}

	private void SetMotdIntLocal(string key, string? value, int min, int max, int fallback)
	{
		if (!applyingMotdControls)
		{
			if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
			{
				result = fallback;
			}
			motdConfig[key] = Math.Clamp(result, min, max);
		}
	}

	private void ApplyMotdControlValues()
	{
		if (SingleComposer == null || discordView != "motd")
		{
			return;
		}
		applyingMotdControls = true;
		try
		{
			if (motdView == "home")
			{
				GuiElementTextArea textArea = SingleComposer.GetTextArea("motd-home-editor");
				if (textArea != null)
				{
					textArea.Autoheight = false;
					textArea.SetValue(MotdString("Content"));
				}
			}
			else if (motdView == "rules")
			{
				GuiElementTextArea textArea2 = SingleComposer.GetTextArea("motd-rules-editor");
				if (textArea2 != null)
				{
					textArea2.Autoheight = false;
					textArea2.SetValue(MotdString("RulesVtml"));
				}
			}
			else if (motdView == "pages")
			{
				SingleComposer.GetSwitch("motd-page-news")?.SetValue(MotdBool("EnableAnnouncementsTab", fallback: true));
				SingleComposer.GetSwitch("motd-page-updates")?.SetValue(MotdBool("EnableUpdatesTab", fallback: true));
				SingleComposer.GetSwitch("motd-page-events")?.SetValue(MotdBool("EnableEventsTab", fallback: true));
				SingleComposer.GetSwitch("motd-page-rules")?.SetValue(MotdBool("EnableRulesTab", fallback: true));
				SingleComposer.GetSwitch("motd-page-server")?.SetValue(MotdBool("EnableServerTab", fallback: true));
				SingleComposer.GetSwitch("motd-page-commands")?.SetValue(MotdBool("EnableCommandsTab", fallback: true));
				SingleComposer.GetSwitch("motd-feed-news")?.SetValue(MotdBool("UseDiscordAnnouncements", fallback: true));
				SingleComposer.GetSwitch("motd-feed-updates")?.SetValue(MotdBool("UseDiscordUpdates", fallback: true));
				SingleComposer.GetSwitch("motd-feed-events")?.SetValue(MotdBool("UseDiscordEvents", fallback: true));
				SingleComposer.GetSwitch("motd-feed-merge")?.SetValue(MotdBool("MergeLocalAndDiscord", fallback: true));
				SingleComposer.GetNumberInput("motd-feed-max")?.SetValue(MotdInt("DiscordMaxItemsPerFeed", 10).ToString(CultureInfo.InvariantCulture));
				SingleComposer.GetSwitch("motd-suite-status")?.SetValue(MotdBool("UseSuiteStatus", fallback: true));
				SingleComposer.GetSwitch("motd-info-server")?.SetValue(MotdBool("ServerInfoShowServer", fallback: true));
				SingleComposer.GetSwitch("motd-info-world")?.SetValue(MotdBool("ServerInfoShowWorld", fallback: true));
				SingleComposer.GetSwitch("motd-info-gameplay")?.SetValue(MotdBool("ServerInfoShowGameplay", fallback: true));
				SingleComposer.GetSwitch("motd-info-prospecting")?.SetValue(MotdBool("ServerInfoShowProspecting", fallback: true));
				SingleComposer.GetSwitch("motd-info-temporal")?.SetValue(MotdBool("ServerInfoShowTemporal", fallback: true));
				SingleComposer.GetSwitch("motd-info-mods")?.SetValue(MotdBool("ServerInfoShowMods", fallback: true));
			}
			else
			{
				SingleComposer.GetTextInput("motd-title")?.SetValue(MotdString("Title", "Message of the Day"));
				SingleComposer.GetTextInput("motd-subtitle")?.SetValue(MotdString("Subtitle"));
				SingleComposer.GetNumberInput("motd-width")?.SetValue(MotdInt("Width", 980).ToString(CultureInfo.InvariantCulture));
				SingleComposer.GetNumberInput("motd-height")?.SetValue(MotdInt("Height", 720).ToString(CultureInfo.InvariantCulture));
				SingleComposer.GetTextInput("motd-version-label")?.SetValue(MotdString("VersionLabel"));
				SingleComposer.GetSwitch("motd-every-join")?.SetValue(MotdBool("OpenOnEveryJoin"));
				SingleComposer.GetSwitch("motd-once-session")?.SetValue(MotdBool("OnlyOncePerSession", fallback: true));
				SingleComposer.GetSwitch("motd-once-version")?.SetValue(MotdBool("OnlyOncePerVersion", fallback: true));
				SingleComposer.GetSwitch("motd-dont-show")?.SetValue(MotdBool("EnableDontShowAgain", fallback: true));
				SingleComposer.GetSwitch("motd-logo-enabled")?.SetValue(MotdBool("EnableLogo"));
				SingleComposer.GetTextInput("motd-logo-source")?.SetValue(MotdString("LogoSource", "ServerFile"));
				SingleComposer.GetTextInput("motd-logo-file")?.SetValue(MotdString("ServerLogoFile", "branding/motd-logo.png"));
				SingleComposer.GetNumberInput("motd-logo-width")?.SetValue(MotdInt("LogoWidth", 180).ToString(CultureInfo.InvariantCulture));
				SingleComposer.GetNumberInput("motd-logo-height")?.SetValue(MotdInt("LogoHeight", 0).ToString(CultureInfo.InvariantCulture));
				SingleComposer.GetSwitch("motd-logo-aspect")?.SetValue(MotdBool("LogoPreserveAspectRatio", fallback: true));
				SingleComposer.GetSwitch("motd-link-enabled")?.SetValue(MotdBool("EnableLinkButton"));
				SingleComposer.GetTextInput("motd-link-text")?.SetValue(MotdString("LinkButtonText", "Discord"));
				SingleComposer.GetTextInput("motd-link-url")?.SetValue(MotdString("LinkUrl"));
				SingleComposer.GetSwitch("motd-webmap-enabled")?.SetValue(MotdBool("EnableWebMapButton"));
				SingleComposer.GetTextInput("motd-webmap-text")?.SetValue(MotdString("WebMapButtonText", "Web Map"));
				SingleComposer.GetTextInput("motd-webmap-url")?.SetValue(MotdString("WebMapUrl"));
			}
		}
		finally
		{
			applyingMotdControls = false;
		}
	}

	private void CaptureMotdControls()
	{
		if (SingleComposer == null || discordView != "motd")
		{
			return;
		}
		if (motdView == "home")
		{
			GuiElementTextArea textArea = SingleComposer.GetTextArea("motd-home-editor");
			if (textArea != null)
			{
				motdConfig["Content"] = textArea.GetText() ?? "";
				motdConfig["ContentLines"] = new JArray();
			}
			return;
		}
		if (motdView == "rules")
		{
			GuiElementTextArea textArea2 = SingleComposer.GetTextArea("motd-rules-editor");
			if (textArea2 != null)
			{
				motdConfig["RulesVtml"] = textArea2.GetText() ?? "";
			}
			return;
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
		int Number(string id, int fallback, int min, int max)
		{
			string s = SingleComposer.GetNumberInput(id)?.GetText() ?? fallback.ToString(CultureInfo.InvariantCulture);
			int result;
			return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? Math.Clamp(result, min, max) : fallback;
		}
		string Text(string id, string fallback)
		{
			return SingleComposer.GetTextInput(id)?.GetText() ?? fallback;
		}
	}

	private bool SaveMotdFromShiftL()
	{
		if (!motdAvailable)
		{
			return true;
		}
		CaptureMotdControls();
		RequestDiscordIntegration("motd-save", "", boolValue: false, 0, motdConfig.ToString(Formatting.None, Array.Empty<JsonConverter>()));
		return true;
	}

	private void AddDiscordAnnouncementRow(GuiComposer composer, string prefix, string label, DiscordAnnouncementGuiPacket route, double y)
	{
		composer.AddStaticText(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, y + 8.0, 150.0, 24.0)).AddSwitch((bool value) =>
		{
			SetDiscordAnnouncementEnabled(prefix, value);
		}, ElementBounds.Fixed(184.0, y + 2.0, 52.0, 26.0), "discord-event-" + prefix + "-enabled").AddStaticText("Channel", CairoFont.WhiteSmallText(), ElementBounds.Fixed(252.0, y + 8.0, 70.0, 24.0))
			.AddTextInput(ElementBounds.Fixed(322.0, y + 2.0, 180.0, 28.0), (string value) =>
			{
				route.ChannelId = value ?? "0";
			}, CairoFont.WhiteSmallText(), "discord-event-" + prefix + "-channel")
			.AddTextInput(ElementBounds.Fixed(516.0, y + 2.0, 660.0, 28.0), (string value) =>
			{
				route.Format = value ?? "";
			}, CairoFont.WhiteSmallText(), "discord-event-" + prefix + "-format")
			.AddSuiteButton(SuiteLocalization.Get("gui.save"), () => SaveDiscordAnnouncement(prefix), ElementBounds.Fixed(1188.0, y + 2.0, 84.0, 28.0));
	}

	private bool SelectDiscordView(string selected)
	{
		bool flag;
		switch (selected)
		{
		case "announcements":
		case "linking":
		case "routing":
		case "motd":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		discordView = (flag ? selected : "overview");
		pageHeaderText = ((discordView == "motd") ? "Discord Integration · Community MOTD" : "Discord Integration");
		ComposeDialog();
		ApplyDiscordControlValues();
		if (discordView == "motd")
		{
			RequestDiscordIntegration("motd-get");
		}
		return true;
	}

	private DiscordAnnouncementGuiPacket GetDiscordRoute(string prefix)
	{
		string text = prefix.ToLowerInvariant();
		if (1 == 0)
		{
		}
		DiscordAnnouncementGuiPacket result = text switch
		{
			"playerjoin" => discordSettings.PlayerJoin, 
			"playerleave" => discordSettings.PlayerLeave, 
			"playerdeath" => discordSettings.PlayerDeath, 
			"serverstarted" => discordSettings.ServerStarted, 
			"serverstopping" => discordSettings.ServerStopping, 
			"discordlinked" => discordSettings.DiscordLinked, 
			"discordunlinked" => discordSettings.DiscordUnlinked, 
			_ => discordSettings.PlayerJoin, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void SetDiscordAnnouncementEnabled(string prefix, bool value)
	{
		if (!applyingDiscordControls)
		{
			GetDiscordRoute(prefix).Enabled = value;
			RequestDiscordIntegration("set", "event." + prefix + ".enabled", value);
		}
	}

	private bool SaveDiscordAnnouncement(string prefix)
	{
		DiscordAnnouncementGuiPacket discordRoute = GetDiscordRoute(prefix);
		discordRoute.ChannelId = SingleComposer?.GetTextInput("discord-event-" + prefix + "-channel")?.GetText() ?? discordRoute.ChannelId;
		discordRoute.Format = SingleComposer?.GetTextInput("discord-event-" + prefix + "-format")?.GetText() ?? discordRoute.Format;
		RequestDiscordIntegration("set", "event." + prefix + ".channelid", boolValue: false, 0, discordRoute.ChannelId);
		RequestDiscordIntegration("set", "event." + prefix + ".format", boolValue: false, 0, discordRoute.Format);
		return true;
	}

	private void SetDiscordBool(string key, bool value)
	{
		if (!applyingDiscordControls)
		{
			RequestDiscordIntegration("set", key, value);
		}
	}

	private bool SendDiscordTextInput(string key, string elementId, string fallback)
	{
		string value = SingleComposer?.GetTextInput(elementId)?.GetText() ?? fallback ?? "";
		return SendDiscordText(key, value);
	}

	private bool SendDiscordText(string key, string value)
	{
		RequestDiscordIntegration("set", key, boolValue: false, 0, value ?? "");
		return true;
	}

	private bool SendDiscordInt(string key, int value)
	{
		RequestDiscordIntegration("set", key, boolValue: false, value);
		return true;
	}

	private bool TestDiscord(string target)
	{
		RequestDiscordIntegration("test", "", boolValue: false, 0, "", target);
		return true;
	}

	private bool RequestDiscordIntegration(string operation, string key = "", bool boolValue = false, int intValue = 0, string stringValue = "", string target = "")
	{
		long num = (lastDiscordResponseId = ++requestId);
		sendDiscordIntegration(new DiscordIntegrationGuiRequest
		{
			RequestId = num,
			Operation = operation,
			SettingKey = key,
			BoolValue = boolValue,
			IntValue = intValue,
			StringValue = (stringValue ?? ""),
			Target = (target ?? "")
		});
		return true;
	}

	private void ApplyDiscordControlValues()
	{
		if (section != "webserver" || webView != "discord" || SingleComposer == null)
		{
			return;
		}
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
				string[] array = new string[7] { "playerJoin", "playerLeave", "playerDeath", "serverStarted", "serverStopping", "discordLinked", "discordUnlinked" };
				foreach (string text in array)
				{
					DiscordAnnouncementGuiPacket discordRoute = GetDiscordRoute(text);
					SingleComposer.GetSwitch("discord-event-" + text + "-enabled")?.SetValue(discordRoute.Enabled);
					SingleComposer.GetTextInput("discord-event-" + text + "-channel")?.SetValue(discordRoute.ChannelId ?? "0");
					SingleComposer.GetTextInput("discord-event-" + text + "-format")?.SetValue(discordRoute.Format ?? "");
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
		genericScrollValue = 0.0;
		bool flag = ((selected == "webmap" || selected == "discord") ? true : false);
		webView = (flag ? selected : "webpanel");
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
		long num = (lastResponseId = ++requestId);
		sendWebDashboard(new WebDashboardRequest
		{
			RequestId = num,
			Operation = operation
		});
		return true;
	}

	private void SetWebBool(string key, bool value)
	{
		if (!applyingWebControls)
		{
			if (key == "enabled")
			{
				webEnabled = value;
			}
			if (key == "securecookies")
			{
				webSecureCookies = value;
			}
			long num = (lastResponseId = ++requestId);
			sendWebDashboard(new WebDashboardRequest
			{
				RequestId = num,
				Operation = "setbool",
				SettingKey = key,
				BoolValue = value
			});
		}
	}

	private bool SaveWebBind()
	{
		return SendWebText("bindaddress", webBindAddress);
	}

	private bool SaveWebPublicUrl()
	{
		return SendWebText("publicurl", webPublicUrl);
	}

	private bool SaveWebUsername()
	{
		return SendWebText("adminusername", webAdminUsername);
	}

	private bool SendWebText(string key, string value)
	{
		long num = (lastResponseId = ++requestId);
		sendWebDashboard(new WebDashboardRequest
		{
			RequestId = num,
			Operation = "settext",
			SettingKey = key,
			StringValue = (value ?? "")
		});
		return true;
	}

	private void OnWebPortChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			webPort = Math.Clamp(result, 1, 65535);
		}
	}

	private bool SaveWebPort()
	{
		return SendWebInt("port", webPort);
	}

	private void OnWebSessionChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			webSessionTimeoutMinutes = Math.Clamp(result, 5, 1440);
		}
	}

	private bool SaveWebSessionTimeout()
	{
		return SendWebInt("sessiontimeoutminutes", webSessionTimeoutMinutes);
	}

	private bool SendWebInt(string key, int value)
	{
		long num = (lastResponseId = ++requestId);
		sendWebDashboard(new WebDashboardRequest
		{
			RequestId = num,
			Operation = "setint",
			SettingKey = key,
			IntValue = value
		});
		return true;
	}

	private void ApplyWebControlValues()
	{
		if (section != "webserver" || webView != "webpanel" || SingleComposer == null)
		{
			return;
		}
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
		webMapBindAddress = (string.IsNullOrWhiteSpace(settings.BindAddress) ? "0.0.0.0" : settings.BindAddress);
		webMapPort = ((settings.Port > 0) ? settings.Port : 9466);
		webMapTitle = (string.IsNullOrWhiteSpace(settings.Title) ? "Newfie's Server Map" : settings.Title);
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
		if (!applyingWebMapControls)
		{
			switch (key)
			{
			case "enabled":
				webMapEnabled = value;
				break;
			case "players":
				webMapShowPlayers = value;
				break;
			case "waypoints":
				webMapShowWaypoints = value;
				break;
			case "activity":
				webMapShowActivity = value;
				break;
			case "badbe":
				webMapShowBadBlockEntities = value;
				break;
			case "exportnext":
				webMapExportTerrainOnStart = value;
				break;
			case "autoexport":
				webMapAutomaticStartupExportEnabled = value;
				break;
			case "incremental":
				webMapUseIncrementalTerrainExport = value;
				break;
			case "lockexport":
				webMapLockServerDuringTerrainExport = value;
				break;
			}
		}
	}

	private void SetWebMapInt(string key, string value)
	{
		if (!applyingWebMapControls && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			switch (key)
			{
			case "port":
				webMapPort = Math.Clamp(result, 1024, 65535);
				break;
			case "major":
				webMapPlayerMajorWaypointLimit = Math.Clamp(result, 0, 100);
				break;
			case "minor":
				webMapPlayerMinorWaypointLimit = Math.Clamp(result, 0, 100);
				break;
			case "maxzoom":
				webMapMaximumZoomOutSpanBlocks = Math.Clamp(result, 2048, 100000);
				break;
			case "defaultspan":
				webMapDefaultViewSpanBlocks = Math.Clamp(result, 512, 100000);
				break;
			case "tilesize":
				webMapTerrainTileSize = Math.Clamp(result, 128, 512);
				break;
			case "activitywindow":
				webMapActivityWindowMinutes = Math.Clamp(result, 5, 1440);
				break;
			case "activitymarkers":
				webMapMaxActivityMarkers = Math.Clamp(result, 25, 200);
				break;
			case "exportdays":
				webMapAutomaticStartupExportIntervalDays = Math.Clamp(result, 1, 365);
				break;
			}
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
		long num = (lastResponseId = ++requestId);
		sendWebDashboard(new WebDashboardRequest
		{
			RequestId = num,
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
		if (section != "webserver" || webView != "webmap" || SingleComposer == null)
		{
			return;
		}
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
		genericScrollValue = 0.0;
		loginView = ((selected == "settings") ? "settings" : "status");
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
		long num = (lastResponseId = ++requestId);
		sendLoginProtection(new LoginProtectionRequest
		{
			RequestId = num,
			Operation = operation
		});
		return true;
	}

	private void SetLoginProtectionBool(string key, bool value)
	{
		if (!applyingLoginControls)
		{
			switch (key)
			{
			case "enabled":
				loginEnabled = value;
				break;
			case "protectonjoin":
				loginProtectOnJoin = value;
				break;
			case "protectonrespawn":
				loginProtectOnRespawn = value;
				break;
			case "announceonjoin":
				loginAnnounceOnJoin = value;
				break;
			case "announceonend":
				loginAnnounceOnEnd = value;
				break;
			case "blockpvp":
				loginBlockPvP = value;
				break;
			case "cancelonattack":
				loginCancelOnAttack = value;
				break;
			case "protectfalldamage":
				loginProtectFallDamage = value;
				break;
			case "extinguishfire":
				loginExtinguishFire = value;
				break;
			case "recoverswimmingonlogin":
				loginRecoverSwimmingOnLogin = value;
				break;
			}
			long num = (lastResponseId = ++requestId);
			sendLoginProtection(new LoginProtectionRequest
			{
				RequestId = num,
				Operation = "setbool",
				SettingKey = key,
				BoolValue = value
			});
		}
	}

	private void OnLoginProtectionSecondsChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			loginProtectionSeconds = Math.Clamp(result, 1, 3600);
		}
	}

	private bool SaveLoginProtectionSeconds()
	{
		loginProtectionSeconds = Math.Clamp(loginProtectionSeconds, 1, 3600);
		long num = (lastResponseId = ++requestId);
		sendLoginProtection(new LoginProtectionRequest
		{
			RequestId = num,
			Operation = "setint",
			SettingKey = "protectionseconds",
			IntValue = loginProtectionSeconds
		});
		return true;
	}

	private void ApplyLoginProtectionControlValues()
	{
		if (section != "loginprotection" || loginView != "settings" || SingleComposer == null)
		{
			return;
		}
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
		if (1 == 0)
		{
		}
		string text = selected switch
		{
			"mods" => "mods", 
			"phases" => "phases", 
			"slowticks" => "slowticks", 
			"hotspots" => "hotspots", 
			"offthread" => "offthread", 
			"lagevents" => "lagevents", 
			_ => "overview", 
		};
		if (1 == 0)
		{
		}
		profilerView = text;
		ComposeDialog();
		return true;
	}

	private static string ProfilerConfidenceMarkup(string? confidence)
	{
		string text = (confidence ?? "Low").Trim();
		string result;
		if (text.Equals("High", StringComparison.OrdinalIgnoreCase))
		{
			result = "<font color=\"#8fda72\"><strong>High</strong></font>";
		}
		else
		{
			result = (text.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? "<font color=\"#ffd166\"><strong>Medium</strong></font>" : "<font color=\"#ffad66\"><strong>Low</strong></font>");
		}
		return result;
	}

	private void SetProfilerEnabled(bool value)
	{
		if (!applyingProfilerControls)
		{
			profilerEnabled = value;
			profilerMessage = (value ? "Enabling profiler..." : "Disabling profiler...");
			long num = (lastResponseId = ++requestId);
			sendProfiler(new ProfilerRequest
			{
				RequestId = num,
				Operation = "setenabled",
				BoolValue = value
			});
		}
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
		capi.Event.RegisterCallback((float _) =>
		{
			if (generation == profilerLiveGeneration && profilerLiveUpdates)
			{
				if (!IsOpened())
				{
					profilerLiveUpdates = false;
					profilerLiveGeneration++;
				}
				else if (section != "profiler")
				{
					profilerLiveUpdates = false;
					profilerLiveGeneration++;
				}
				else
				{
					if (!profilerLiveRequestPending)
					{
						profilerLiveRequestPending = true;
						RequestProfilerSnapshotInternal(showLoading: false, compact: true);
					}
					ScheduleProfilerLiveRefresh(generation);
				}
			}
		}, 2000);
	}

	private bool OpenLocalProfilerWeb()
	{
		string href = (string.IsNullOrWhiteSpace(webLocalUrl) ? $"http://127.0.0.1:{webPort}/" : webLocalUrl);
		capi.Gui.OpenLink(href);
		return true;
	}

	private bool OpenPublicProfilerWeb()
	{
		if (string.IsNullOrWhiteSpace(webPublicUrl))
		{
			profilerMessage = "Public web URL is not configured. Set it in Web Server settings or /serverutils web publicurl.";
			ComposeDialog();
			return true;
		}
		capi.Gui.OpenLink(webPublicUrl);
		return true;
	}

	private bool RequestProfilerSnapshot()
	{
		return RequestProfilerSnapshotInternal(showLoading: true, compact: false);
	}

	private bool RequestProfilerSnapshotInternal(bool showLoading, bool compact)
	{
		if (showLoading)
		{
			profilerMessage = SuiteLocalization.Get("gui.loadingdiagnostics");
		}
		long num = (lastResponseId = ++requestId);
		sendProfiler(new ProfilerRequest
		{
			RequestId = num,
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
		long num = (lastResponseId = ++requestId);
		sendProfiler(new ProfilerRequest
		{
			RequestId = num,
			Operation = "start",
			DurationSeconds = seconds
		});
		capi.Event.RegisterCallback((float _) =>
		{
			if (IsOpened() && section == "profiler")
			{
				RequestProfilerSnapshot();
			}
		}, seconds * 1000 + 750);
		return true;
	}

	private string BuildProfilerStateText()
	{
		string result;
		if (profilerEnabled)
		{
			if (profilerRunning)
			{
				result = $"ENGINE PROFILE RUNNING — about {profilerRemainingSeconds}s remaining";
			}
			else
			{
				result = ((profilerLastProfile.CompletedUnixMilliseconds > 0) ? $"Last profile: {profilerLastProfile.DurationSeconds}s / {profilerLastProfile.TickCount} ticks — avg {profilerLastProfile.AverageTickMs:0.0}ms, P95 {profilerLastProfile.P95TickMs:0.0}, P99 {profilerLastProfile.P99TickMs:0.0}, max {profilerLastProfile.MaxTickMs:0.0}ms" : "No detailed engine profile captured yet.");
			}
		}
		else
		{
			result = "PROFILER DISABLED — enable it above to collect diagnostics.";
		}
		return result;
	}

	private string BuildProfilerCurrentPrimaryText()
	{
		return $"CPU: {profilerCurrent.CpuPercent:0.0}%   Working set: {profilerCurrent.WorkingSetMb:0} MB   Managed heap: {profilerCurrent.ManagedHeapMb:0} MB   Players: {profilerCurrent.Players}   Entities: {profilerCurrent.LoadedEntities:N0}   Chunks: {profilerCurrent.LoadedChunks:N0}";
	}

	private string BuildProfilerCurrentSecondaryText()
	{
		return $"Main-thread delay: {profilerCurrent.MainThreadDelayMs:0.0}ms   BlockLogger queue: {profilerCurrent.BlockLoggerQueue:N0}   Writer: {profilerCurrent.BlockLoggerWriterMs:N0}ms   Dropped: {profilerCurrent.BlockLoggerDropped:N0}   Animal tracked: {profilerCurrent.AnimalTrackedEntities:N0} / {profilerCurrent.AnimalTrackedRegions:N0} regions";
	}

	private void UpdateProfilerLiveElements()
	{
		GuiComposer singleComposer = SingleComposer;
		if (singleComposer != null)
		{
			singleComposer.GetDynamicText("profiler-live-status")?.SetNewTextAsync(SuiteProfilerTextElement.FitUiLine(SuiteLocalization.Get("gui.statusvalue", profilerMessage), 1180.0), autoHeight: false, forceRedraw: true);
			singleComposer.GetDynamicText("profiler-live-state")?.SetNewTextAsync(SuiteProfilerTextElement.FitUiLine(BuildProfilerStateText(), 760.0), autoHeight: false, forceRedraw: true);
			if (!(profilerView != "overview"))
			{
				singleComposer.GetDynamicText("profiler-live-current-primary")?.SetNewTextAsync(BuildProfilerCurrentPrimaryText(), autoHeight: false, forceRedraw: true);
				singleComposer.GetDynamicText("profiler-live-current-secondary")?.SetNewTextAsync(BuildProfilerCurrentSecondaryText(), autoHeight: false, forceRedraw: true);
			}
		}
	}

	private string ProfilerMetricSummary(System.Func<ProfilerSamplePacket, double> selector, string suffix)
	{
		if (profilerSamples.Count == 0)
		{
			return "No rolling samples yet.\n\nInteractive graphs are available in the web dashboard.";
		}
		List<double> list = (from x in profilerSamples.Select(selector)
			orderby x
			select x).ToList();
		List<ProfilerSamplePacket> list2 = profilerSamples;
		double value = selector(list2[list2.Count - 1]);
		double value2 = list.Average();
		double value3 = list[list.Count - 1];
		int index = Math.Clamp((int)Math.Ceiling((double)list.Count * 0.95) - 1, 0, list.Count - 1);
		double value4 = list[index];
		return $"Current   {value:0.0}{suffix}\nAverage   {value2:0.0}{suffix}\nP95       {value4:0.0}{suffix}\nMaximum   {value3:0.0}{suffix}\n" + "Web dashboard: interactive graph";
	}

	private static string ShortProfilerCode(string? value, int maxLength)
	{
		string text = value ?? "";
		if (text.Length <= maxLength)
		{
			return text;
		}
		if (maxLength <= 3)
		{
			return text.Substring(0, maxLength);
		}
		int num = Math.Max(1, maxLength - 3);
		return "..." + text.Substring(text.Length - num);
	}

	private bool SelectAnimalView(string selected)
	{
		section = "animals";
		view = "animals";
		genericScrollValue = 0.0;
		if (1 == 0)
		{
		}
		string text = selected switch
		{
			"history" => "history", 
			"population" => "population", 
			"rules" => "rules", 
			"settings" => "settings", 
			_ => "limiter", 
		};
		if (1 == 0)
		{
		}
		animalView = text;
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
		genericScrollValue = 0.0;
		view = selected;
		blockLoggerView = selected;
		action = "any";
		pageHeaderText = "Ready for " + ViewTitle(selected).ToLowerInvariant() + ".";
		resultText = SuiteLocalization.Get("gui.runtime.noresults");
		SetGenericResults(new List<string> { SuiteLocalization.Get("gui.runtime.noresults") });
		ComposeDialog();
		return true;
	}

	private bool SelectBlockLoggerSettings()
	{
		section = "blocklogger";
		view = "settings";
		blockLoggerView = "settings";
		genericScrollValue = 0.0;
		pageHeaderText = "BlockLogger settings";
		ComposeDialog();
		return RequestBlockLoggerSettings();
	}

	private bool RequestBlockLoggerSettings()
	{
		long num = (lastResponseId = ++requestId);
		sendBlockLoggerSettings(new BlockLoggerSettingsRequest
		{
			RequestId = num,
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
		long num = (lastResponseId = ++requestId);
		sendStatus(new InvestigatorStatusRequest
		{
			RequestId = num
		});
		return true;
	}

	private bool Search()
	{
		if (view == "status")
		{
			return SelectStatus();
		}
		if (view == "settings")
		{
			return RequestBlockLoggerSettings();
		}
		pageHeaderText = SuiteLocalization.Get("gui.searchingview", ViewTitle(view).ToLowerInvariant());
		ShowTransientResult("Searching...");
		long num = (lastResponseId = ++requestId);
		sendQuery(new InvestigatorQueryRequest
		{
			RequestId = num,
			View = view,
			UseSelection = useSelection,
			Radius = radius,
			Limit = limit,
			Search = ((view == "players") ? playerSearch : search),
			Action = action
		});
		return true;
	}

	private bool Refresh()
	{
		bool result;
		if (view == "status")
		{
			result = SelectStatus();
		}
		else
		{
			result = ((view == "animals") ? SendAnimalLimiterOperation("status") : Search());
		}
		return result;
	}

	private bool ConfirmCull()
	{
		if (DateTime.UtcNow > cullConfirmUntilUtc)
		{
			cullConfirmUntilUtc = DateTime.UtcNow.AddSeconds(5.0);
			ShowTransientResult("<font color=\"#ffcc66\"><strong>Cull confirmation:</strong> press Cull Now again within 5 seconds to remove excess entities.</font>");
			return true;
		}
		cullConfirmUntilUtc = DateTime.MinValue;
		return SendAnimalLimiterOperation("cull");
	}

	private bool SendAnimalLimiterOperation(string operation)
	{
		if (1 == 0)
		{
		}
		string text = operation switch
		{
			"history" => "Animal Limiter removal logs", 
			"rules" => "Animal Limiter rules", 
			"settings" => "Animal Limiter settings", 
			_ => "Animal Limiter", 
		};
		if (1 == 0)
		{
		}
		pageHeaderText = text;
		if (1 == 0)
		{
		}
		text = operation switch
		{
			"history" => SuiteLocalization.Get("gui.loadingremovallogs"), 
			"rules" => SuiteLocalization.Get("gui.loadingrules"), 
			"settings" => SuiteLocalization.Get("gui.loadingsettings"), 
			"cull" => "Running animal limiter cull...", 
			"preview" => "Running preview...", 
			_ => "Updating Animal Limiter...", 
		};
		if (1 == 0)
		{
		}
		ShowTransientResult(text);
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
		request.RequestId = (lastResponseId = ++requestId);
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
		if (!applyingAnimalControls)
		{
			switch (key)
			{
			case "enabled":
				animalEnabled = value;
				break;
			case "limitallentities":
				animalLimitAllEntities = value;
				break;
			case "protectpets":
				animalProtectPets = value;
				break;
			case "protectfarmanimals":
				animalProtectFarmAnimals = value;
				break;
			case "protectnamedentities":
				animalProtectNamedEntities = value;
				break;
			case "keepnearesttoplayers":
				animalKeepNearestToPlayers = value;
				break;
			case "logactions":
				animalLogActions = value;
				break;
			case "logtocsv":
				animalLogToCsv = value;
				break;
			}
			SendAnimalRequest(new AnimalLimiterRequest
			{
				Operation = "setbool",
				SettingKey = key,
				BoolValue = value
			});
		}
	}

	private void ApplyBlockLoggerControlValues()
	{
		if (SingleComposer == null || section != "blocklogger" || view != "settings")
		{
			return;
		}
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
		if (!applyingBlockLoggerControls)
		{
			switch (key)
			{
			case "enabled":
				blockLoggerEnabled = value;
				break;
			case "logblockbreak":
				blockLogBreak = value;
				break;
			case "logblockplace":
				blockLogPlace = value;
				break;
			case "logblockuse":
				blockLogUse = value;
				break;
			case "logcontaineraccess":
				blockLogContainerAccess = value;
				break;
			case "logcontaineritemchanges":
				blockLogContainerItems = value;
				break;
			case "logentityinteract":
				blockLogEntityInteract = value;
				break;
			case "logentityspawn":
				blockLogEntitySpawn = value;
				break;
			case "logentitydeath":
				blockLogEntityDeath = value;
				break;
			case "logentitydespawn":
				blockLogEntityDespawn = value;
				break;
			case "aggregateentityspawndespawn":
				blockAggregateSpawnDespawn = value;
				break;
			}
			SendBlockLoggerSettingsRequest(new BlockLoggerSettingsRequest
			{
				Operation = "setbool",
				SettingKey = key,
				BoolValue = value
			});
		}
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
		request.RequestId = (lastResponseId = ++requestId);
		sendBlockLoggerSettings(request);
		return true;
	}

	private bool SaveBlockRetention()
	{
		return SendBlockLoggerInt("retentiondays", blockRetentionDays);
	}

	private bool SaveBlockFlush()
	{
		return SendBlockLoggerInt("flushintervalms", blockFlushIntervalMs);
	}

	private bool SaveBlockQueue()
	{
		return SendBlockLoggerInt("maxqueuedevents", blockMaxQueuedEvents);
	}

	private bool SaveBlockOverflow()
	{
		return SendBlockLoggerInt("emergencyoverflowcapacity", blockEmergencyOverflowCapacity);
	}

	private bool SaveBlockDefaultRadius()
	{
		return SendBlockLoggerInt("defaultradius", blockDefaultRadius);
	}

	private bool SaveBlockEntityRadius()
	{
		return SendBlockLoggerInt("entityradius", blockEntityRadius);
	}

	private bool SaveBlockMaxRadius()
	{
		return SendBlockLoggerInt("maxradius", blockMaxRadius);
	}

	private bool SaveBlockDefaultRows()
	{
		return SendBlockLoggerInt("defaultdisplaycount", blockDefaultRows);
	}

	private bool SaveBlockDefaultEntityRows()
	{
		return SendBlockLoggerInt("defaultentitydisplaycount", blockDefaultEntityRows);
	}

	private bool SaveBlockMaxResults()
	{
		return SendBlockLoggerInt("maxdisplaycount", blockMaxDisplayCount);
	}

	private void OnBlockRetentionChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockRetentionDays = Math.Clamp(result, 0, 3650);
		}
	}

	private void OnBlockFlushChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockFlushIntervalMs = Math.Clamp(result, 100, 60000);
		}
	}

	private void OnBlockQueueChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockMaxQueuedEvents = Math.Clamp(result, 500, 250000);
		}
	}

	private void OnBlockOverflowChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockEmergencyOverflowCapacity = Math.Clamp(result, 1000, 1000000);
		}
	}

	private void OnBlockDefaultRadiusChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockDefaultRadius = Math.Clamp(result, 0, 256);
		}
	}

	private void OnBlockEntityRadiusChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockEntityRadius = Math.Clamp(result, 0, 256);
		}
	}

	private void OnBlockMaxRadiusChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockMaxRadius = Math.Clamp(result, 0, 512);
		}
	}

	private void OnBlockDefaultRowsChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockDefaultRows = Math.Clamp(result, 1, 2000);
		}
	}

	private void OnBlockDefaultEntityRowsChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockDefaultEntityRows = Math.Clamp(result, 1, 2000);
		}
	}

	private void OnBlockMaxResultsChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			blockMaxDisplayCount = Math.Clamp(result, 1, 2000);
		}
	}

	private void ApplyAnimalControlValues()
	{
		if (SingleComposer == null || section != "animals")
		{
			return;
		}
		if (animalView == "limiter")
		{
			SingleComposer.GetNumberInput("animal-rule-radius")?.SetValue(animalRuleRadiusChunks.ToString());
		}
		else if (!(animalView != "settings"))
		{
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
	}

	private void OnAnimalRuleRadiusChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			animalRuleRadiusChunks = Math.Clamp(result, 0, 32);
		}
	}

	private void OnAnimalRadiusChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			animalRadiusChunks = Math.Clamp(result, 0, 32);
		}
	}

	private void OnAnimalDefaultLimitChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			animalDefaultLimit = Math.Clamp(result, 0, 1000000);
		}
	}

	private void OnAnimalProtectionRadiusChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			animalPlayerProtectionRadius = Math.Clamp(result, 0, 1024);
		}
	}

	private void OnAnimalReconciliationChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			animalReconciliationSeconds = Math.Clamp(result, 60, 3600);
		}
	}

	private void OnAnimalLimitChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			animalLimit = Math.Clamp(result, 0, 1000000);
		}
	}

	private void OnTargetChanged(string value, bool selected)
	{
		if (selected)
		{
			useSelection = value == "selection";
		}
	}

	private void OnRadiusChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			radius = Math.Clamp(result, 0, 512);
		}
	}

	private void OnLimitChanged(string value)
	{
		if (int.TryParse(value, out var result))
		{
			limit = Math.Clamp(result, 1, 2000);
		}
	}

	private void OnActionChanged(string value, bool selected)
	{
		if (selected)
		{
			action = value;
		}
	}

	private string[] ActionValues()
	{
		string text = view;
		if (1 == 0)
		{
		}
		string[] result = text switch
		{
			"containers" => new string[4] { "any", "open", "take", "put" }, 
			"entities" => new string[5] { "any", "entityspawn", "entitydeath", "entitydespawn", "entityinteract" }, 
			"players" => new string[4] { "any", "blocks", "containers", "entities" }, 
			_ => new string[4] { "any", "break", "place", "interact" }, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private string[] ActionNames()
	{
		string text = view;
		if (1 == 0)
		{
		}
		string[] result = text switch
		{
			"containers" => new string[4] { "Any", "Open", "Take", "Put" }, 
			"entities" => new string[5]
			{
				"Any",
				SuiteLocalization.Get("gui.spawn"),
				SuiteLocalization.Get("gui.death"),
				SuiteLocalization.Get("gui.despawn"),
				SuiteLocalization.Get("gui.interact")
			}, 
			"players" => new string[4] { "Any", "Blocks", "Containers", "Entities" }, 
			_ => new string[4]
			{
				"Any",
				SuiteLocalization.Get("gui.break"),
				SuiteLocalization.Get("gui.place"),
				SuiteLocalization.Get("gui.use")
			}, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private int PlayerActionIndex()
	{
		string text = action;
		if (1 == 0)
		{
		}
		int result = text switch
		{
			"blocks" => 1, 
			"containers" => 2, 
			"entities" => 3, 
			_ => 0, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private int ActionIndex()
	{
		string[] array = ActionValues();
		for (int i = 0; i < array.Length; i++)
		{
			if (array[i] == action)
			{
				return i;
			}
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
			foreach (string line in lines)
			{
				genericResultLines.Add(string.IsNullOrEmpty(line) ? " " : line);
			}
		}
		genericScrollValue = 0.0;
	}

	private bool IsAdminInventoryViewerResult()
	{
		return section == "admincommands" && adminCommandView == "inventory" && genericResultLines.Any((string line) => line.StartsWith("INV\t", StringComparison.Ordinal));
	}

	private static bool TryGetAdminInventoryVisualSlot(string? raw, out AdminInventoryVisualSlot slot)
	{
		slot = new AdminInventoryVisualSlot();
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		string[] array = raw.Split('\t');
		if (array.Length < 14 || array[0] != "INV")
		{
			return false;
		}
		int.TryParse(array[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result);
		bool flag = array[4].Equals("1", StringComparison.OrdinalIgnoreCase) || array[4].Equals("true", StringComparison.OrdinalIgnoreCase);
		int quantity = 1;
		if (!flag && int.TryParse(array[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2))
		{
			quantity = Math.Max(1, result2);
		}
		slot = new AdminInventoryVisualSlot
		{
			InventoryId = array[1],
			Section = (string.IsNullOrWhiteSpace(array[2]) ? "Other" : array[2]),
			Slot = Math.Max(0, result),
			Empty = flag,
			Quantity = quantity,
			ItemCode = (flag ? "" : array[7]),
			Fingerprint = array[13],
			SerializedStack = ((array.Length >= 15) ? array[14] : "")
		};
		return !string.IsNullOrWhiteSpace(slot.InventoryId);
	}

	private ItemStack? BuildAdminInventoryDisplayStack(AdminInventoryVisualSlot visual)
	{
		if (visual.Empty)
		{
			return null;
		}
		if (!string.IsNullOrWhiteSpace(visual.SerializedStack))
		{
			try
			{
				ItemStack itemStack = new ItemStack(Convert.FromBase64String(visual.SerializedStack));
				if (itemStack.ResolveBlockOrItem(capi.World))
				{
					string text = itemStack.Collectible?.Code?.ToString() ?? "";
					if (string.IsNullOrWhiteSpace(visual.ItemCode) || text.Equals(visual.ItemCode, StringComparison.OrdinalIgnoreCase))
					{
						return itemStack;
					}
				}
			}
			catch
			{
			}
		}
		try
		{
			string domainAndPath = (visual.ItemCode.Contains(':') ? visual.ItemCode : ("game:" + visual.ItemCode));
			AssetLocation assetLocation = new AssetLocation(domainAndPath);
			CollectibleObject collectibleObject = (CollectibleObject)(((object)capi.World.GetItem(assetLocation)) ?? ((object)capi.World.GetBlock(assetLocation)));
			return (collectibleObject == null) ? null : new ItemStack(collectibleObject, Math.Max(1, visual.Quantity));
		}
		catch
		{
			return null;
		}
	}

	private void AddAdminInventoryViewer(GuiComposer composer, double innerX, double innerWidth, double resultsY, double resultsHeight)
	{
		List<AdminInventoryVisualSlot> list = (from raw in genericResultLines
			select TryGetAdminInventoryVisualSlot(raw, out AdminInventoryVisualSlot slot) ? slot : null into adminInventoryVisualSlot
			where adminInventoryVisualSlot != null
			select adminInventoryVisualSlot).Cast<AdminInventoryVisualSlot>().ToList();
		composer.AddSuiteInset(ElementBounds.Fixed(innerX, resultsY, innerWidth, resultsHeight));
		adminInventoryElementBaseY.Clear();
		adminInventoryViewportHeight = Math.Max(80.0, resultsHeight - 34.0);
		if (list.Count == 0)
		{
			composer.AddStaticText(SuiteLocalization.Get("gui.noinventoryslots"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX + 12.0, resultsY + 12.0, 500.0, 24.0));
			adminInventoryContentHeight = adminInventoryViewportHeight;
			return;
		}
		var list2 = (from g in list.GroupBy((AdminInventoryVisualSlot v) => v.InventoryId, StringComparer.OrdinalIgnoreCase)
			select new
			{
				InventoryId = g.Key,
				Section = (g.Select((AdminInventoryVisualSlot v) => v.Section).FirstOrDefault((string x) => !string.IsNullOrWhiteSpace(x)) ?? "Other"),
				Slots = g.OrderBy((AdminInventoryVisualSlot v) => v.Slot).ToList()
			} into g
			orderby AdminInventorySectionOrder(g.Section)
			select g).ThenBy(g => g.InventoryId, StringComparer.OrdinalIgnoreCase).ToList();
		double num = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding;
		double num2 = innerWidth - 52.0;
		double num3 = (num2 - 16.0) / 2.0;
		double[] array = new double[2] { 8.0, 8.0 };
		int num4 = 0;
		composer.BeginClip(ElementBounds.Fixed(innerX + 4.0, resultsY + 4.0, innerWidth - 30.0, adminInventoryViewportHeight));
		foreach (var item in list2)
		{
			int num5 = Math.Max(1, item.Slots.Max((AdminInventoryVisualSlot v) => v.Slot) + 1);
			int num6;
			if (item.Section.Equals("Crafting", StringComparison.OrdinalIgnoreCase))
			{
				num6 = 3;
			}
			else if (item.Section.Equals("Hotbar", StringComparison.OrdinalIgnoreCase))
			{
				num6 = Math.Min(10, num5);
			}
			else
			{
				num6 = (item.Section.Equals("Equipment", StringComparison.OrdinalIgnoreCase) ? Math.Min(5, num5) : Math.Max(1, Math.Min(8, num5)));
			}
			int num7 = (int)Math.Ceiling((double)num5 / (double)num6);
			double num8 = 20.0 + (double)num7 * num + 8.0;
			int num9 = ((!(array[0] <= array[1])) ? 1 : 0);
			double num10 = 10.0 + (double)num9 * (num3 + 16.0);
			double num11 = array[num9];
			InventoryGeneric inventoryGeneric = new InventoryGeneric(num5, "nssinspect-" + num4, capi);
			Dictionary<int, AdminInventoryVisualSlot> bySlot = item.Slots.ToDictionary((AdminInventoryVisualSlot v) => v.Slot);
			foreach (AdminInventoryVisualSlot slot2 in item.Slots)
			{
				if (slot2.Slot >= 0 && slot2.Slot < inventoryGeneric.Count)
				{
					inventoryGeneric[slot2.Slot].Itemstack = BuildAdminInventoryDisplayStack(slot2);
				}
			}
			string key = "admin-inventory-label-" + num4;
			string key2 = "admin-inventory-grid-" + num4;
			string text = item.Section + "  •  " + item.InventoryId;
			composer.AddRichtext("<font color=\"#91a0ae\"><strong>" + Escape(text) + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(num10, num11, num3, 18.0), key);
			ElementBounds bounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, num10, num11 + 20.0, num6, num7);
			ReadonlyAdminInventoryGrid readonlyAdminInventoryGrid = new ReadonlyAdminInventoryGrid(capi, inventoryGeneric, num6, bounds, (int slotIndex) =>
			{
				if (bySlot.TryGetValue(slotIndex, out var value2))
				{
					SelectAdminInventorySlot(value2.InventoryId, value2.Slot, value2.Quantity, value2.ItemCode, value2.Fingerprint);
				}
			});
			if (item.InventoryId.Equals(adminCommandInventoryId, StringComparison.OrdinalIgnoreCase) && adminCommandSlot >= 0 && adminCommandSlot < inventoryGeneric.Count)
			{
				readonlyAdminInventoryGrid.HighlightSlot(adminCommandSlot);
			}
			composer.AddInteractiveElement(readonlyAdminInventoryGrid, key2);
			adminInventoryElementBaseY[key] = num11;
			adminInventoryElementBaseY[key2] = num11 + 20.0;
			array[num9] += num8;
			num4++;
		}
		composer.EndClip();
		adminInventoryContentHeight = Math.Max(adminInventoryViewportHeight, Math.Max(array[0], array[1]) + 4.0);
		composer.AddVerticalScrollbar(OnAdminInventoryScroll, ElementBounds.Fixed(innerX + innerWidth - 20.0, resultsY + 4.0, 18.0, adminInventoryViewportHeight), "admin-inventory-scroll");
		int value = list.Count((AdminInventoryVisualSlot v) => !v.Empty);
		composer.AddRichtext($"<font color=\"#91a0ae\">{list.Count} slots • {value} occupied • scroll for all inventories • click a slot to select it</font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX + 12.0, resultsY + resultsHeight - 25.0, innerWidth - 40.0, 22.0));
	}

	private void ConfigureAdminInventoryScrollbar()
	{
		GuiElementScrollbar guiElementScrollbar = SingleComposer?.GetScrollbar("admin-inventory-scroll");
		if (guiElementScrollbar != null)
		{
			double max = Math.Max(0.0, adminInventoryContentHeight - adminInventoryViewportHeight);
			adminInventoryScrollValue = Math.Clamp(adminInventoryScrollValue, 0.0, max);
			guiElementScrollbar.SetHeights((float)adminInventoryViewportHeight, (float)adminInventoryContentHeight);
			guiElementScrollbar.SetScrollbarPosition((int)Math.Round(adminInventoryScrollValue));
			PositionAdminInventoryElements(adminInventoryScrollValue);
		}
	}

	private void OnAdminInventoryScroll(float value)
	{
		adminInventoryScrollValue = Math.Max(0f, value);
		PositionAdminInventoryElements(adminInventoryScrollValue);
	}

	private void PositionAdminInventoryElements(double scrollValue)
	{
		if (SingleComposer == null)
		{
			return;
		}
		foreach (KeyValuePair<string, double> item in adminInventoryElementBaseY)
		{
			GuiElement element = SingleComposer.GetElement(item.Key);
			if (element != null)
			{
				element.Bounds.fixedY = item.Value - scrollValue;
				element.Bounds.CalcWorldBounds();
			}
		}
	}

	private static int AdminInventorySectionOrder(string section)
	{
		string text = (section ?? "").ToLowerInvariant();
		if (1 == 0)
		{
		}
		int result = text switch
		{
			"hotbar" => 0, 
			"backpack" => 1, 
			"equipment" => 2, 
			"crafting" => 3, 
			"cursor" => 4, 
			_ => 5, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void AddScrollableResults(GuiComposer composer, double innerX, double innerWidth, double resultsY, double resultsHeight)
	{
		genericResultsY = resultsY + 4.0;
		genericResultsHeight = Math.Max(40.0, resultsHeight - 48.0);
		double num = innerWidth - 42.0;
		double currentGenericResultRowHeight = CurrentGenericResultRowHeight;
		composer.BeginClip(ElementBounds.Fixed(innerX, genericResultsY, innerWidth - 24.0, genericResultsHeight));
		if (genericResultLines.Count == 0)
		{
			composer.AddRichtext(SuiteLocalization.Get("gui.literal.7cd1d44b3c"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(6.0, 4.0, num, currentGenericResultRowHeight - 2.0), "result-row-empty");
		}
		else
		{
			for (int i = 0; i < genericResultLines.Count; i++)
			{
				double num2 = 4.0 + (double)i * currentGenericResultRowHeight;
				string raw = genericResultLines[i];
				string target = "";
				string modId = "";
				string inventoryId = "";
				string itemCode = "";
				string fingerprint = "";
				string itemCode2 = "";
				string playerName = "";
				int slot = 0;
				int quantity = 1;
				int maxStack = 1;
				int index = -1;
				bool alreadyResolved = false;
				bool flag = section == "anticheat" && antiCheatView == "players" && TryGetAntiCheatPlayerActionTarget(raw, out target);
				bool flag2 = section == "anticheat" && antiCheatView == "blacklist" && TryGetAntiCheatBlacklistActionTarget(raw, out modId, out alreadyResolved);
				bool flag3 = section == "admincommands" && adminCommandView == "inventory" && TryGetAdminInventorySlot(raw, out inventoryId, out slot, out quantity, out itemCode, out fingerprint);
				bool flag4 = section == "admincommands" && adminCommandView == "inventory" && TryGetAdminItemSearchResult(raw, out itemCode2, out maxStack);
				bool flag5 = section == "admincommands" && TryGetAdminPlayerChoice(raw, out playerName);
				bool flag6 = section == "anticheat" && antiCheatView == "enforcement" && TryGetAntiCheatEnforcementRuleIndex(raw, out index);
				double fixedWidth = ((flag | flag2 | flag3 | flag4 | flag5 | flag6) ? (num - 118.0 - 14.0) : num);
				composer.AddInteractiveElement(new SuiteResultRowElement(capi, ElementBounds.Fixed(2.0, num2 - 2.0, num + 8.0, currentGenericResultRowHeight - 2.0), (i & 1) == 1), $"result-background-{i}");
				composer.AddRichtext(FormatGenericResultLine(raw), CairoFont.WhiteSmallText(), ElementBounds.Fixed(6.0, num2, fixedWidth, currentGenericResultRowHeight - 2.0), $"result-row-{i}");
				if (flag)
				{
					string target2 = target;
					composer.AddSuiteInlineAction("View Report", () => OpenAntiCheatPlayerReport(target2), ElementBounds.Fixed(6.0 + num - 118.0, num2 + 16.0, 118.0, 28.0), $"result-action-{i}");
				}
				else if (flag2)
				{
					string modId2 = modId;
					composer.AddSuiteInlineAction(alreadyResolved ? "Refresh FP" : "Resolve FP", () => ResolveAntiCheatBlacklistFingerprint(modId2), ElementBounds.Fixed(6.0 + num - 118.0, num2 + 18.0, 118.0, 28.0), $"result-action-{i}");
				}
				else if (flag3)
				{
					string inv = inventoryId;
					string code = itemCode;
					string fingerprint2 = fingerprint;
					int slotIndex = slot;
					int qty = quantity;
					composer.AddSuiteInlineAction("Select Slot", () => SelectAdminInventorySlot(inv, slotIndex, qty, code, fingerprint2), ElementBounds.Fixed(6.0 + num - 118.0, num2 + 14.0, 118.0, 28.0), $"result-action-{i}");
				}
				else if (flag4)
				{
					string code2 = itemCode2;
					int maxStack2 = maxStack;
					composer.AddSuiteInlineAction("Use Item", () => SelectAdminItem(code2, maxStack2), ElementBounds.Fixed(6.0 + num - 118.0, num2 + 14.0, 118.0, 28.0), $"result-action-{i}");
				}
				else if (flag5)
				{
					string target3 = playerName;
					composer.AddSuiteInlineAction("Select", () => SelectAdminCommandPlayer(target3), ElementBounds.Fixed(6.0 + num - 118.0, num2 + 14.0, 118.0, 28.0), $"result-action-{i}");
				}
				else if (flag6)
				{
					int ruleIndex = index;
					composer.AddSuiteInlineAction("Edit Rule", () => SelectAntiCheatEnforcementRule(ruleIndex), ElementBounds.Fixed(6.0 + num - 118.0, num2 + 30.0, 118.0, 28.0), $"result-action-{i}");
				}
			}
		}
		composer.EndClip();
		ElementBounds bounds = ElementBounds.Fixed(innerX + innerWidth - 18.0, genericResultsY - 5.0, 18.0, genericResultsHeight);
		double num3 = resultsY + resultsHeight - 36.0;
		ElementBounds elementBounds = ElementBounds.Fixed(innerX + innerWidth - 238.0, num3, 100.0, 30.0);
		ElementBounds elementBounds2 = ElementBounds.Fixed(innerX + innerWidth - 126.0, num3, 100.0, 30.0);
		composer.AddVerticalScrollbar(OnGenericResultsScroll, bounds, "results-scroll").AddDynamicText(SuiteLocalization.Get("gui.workspace.resultcount", genericResultLines.Count), CairoFont.WhiteSmallText(), ElementBounds.Fixed(innerX + 6.0, num3 + 5.0, 520.0, 24.0), "results-page-count").AddSuiteButton(SuiteLocalization.Get("gui.previous"), ScrollGenericResultsPrevious, elementBounds)
			.AddHoverText(SuiteLocalization.Get("gui.workspace.previoushelp"), CairoFont.WhiteSmallText(), 290, elementBounds.FlatCopy(), "tip-results-prev")
			.AddSuiteButton(SuiteLocalization.Get("gui.next"), ScrollGenericResultsNext, elementBounds2)
			.AddHoverText(SuiteLocalization.Get("gui.workspace.nexthelp"), CairoFont.WhiteSmallText(), 300, elementBounds2.FlatCopy(), "tip-results-next");
	}

	private void ConfigureGenericResultsScrollbar()
	{
		GuiElementScrollbar guiElementScrollbar = SingleComposer?.GetScrollbar("results-scroll");
		if (guiElementScrollbar != null)
		{
			double num = Math.Max(genericResultsHeight, (double)genericResultLines.Count * CurrentGenericResultRowHeight + 8.0);
			double max = Math.Max(0.0, num - genericResultsHeight);
			genericScrollValue = Math.Clamp(genericScrollValue, 0.0, max);
			guiElementScrollbar.SetHeights((float)genericResultsHeight, (float)num);
			guiElementScrollbar.SetScrollbarPosition((int)Math.Round(genericScrollValue));
			PositionGenericResultRows(genericScrollValue);
		}
	}

	private void OnGenericResultsScroll(float value)
	{
		genericScrollValue = Math.Max(0f, value);
		PositionGenericResultRows(genericScrollValue);
	}

	private void PositionGenericResultRows(double scrollValue)
	{
		if (SingleComposer == null)
		{
			return;
		}
		for (int i = 0; i < genericResultLines.Count; i++)
		{
			double num = 4.0 + (double)i * CurrentGenericResultRowHeight - scrollValue;
			GuiElement element = SingleComposer.GetElement($"result-background-{i}");
			if (element != null)
			{
				element.Bounds.fixedY = num - 2.0;
				element.Bounds.CalcWorldBounds();
			}
			GuiElementRichtext richtext = SingleComposer.GetRichtext($"result-row-{i}");
			if (richtext != null)
			{
				richtext.Bounds.fixedY = num;
				richtext.Bounds.CalcWorldBounds();
			}
			GuiElement element2 = SingleComposer.GetElement($"result-action-{i}");
			if (element2 != null)
			{
				element2.Bounds.fixedY = num + (double)((section == "admincommands" && (adminCommandView == "inventory" || adminCommandView == "players")) ? 14 : ((antiCheatView == "enforcement") ? 30 : ((antiCheatView == "players") ? 16 : 18)));
				element2.Bounds.CalcWorldBounds();
			}
		}
		GuiElementRichtext richtext2 = SingleComposer.GetRichtext("result-row-empty");
		if (richtext2 != null)
		{
			richtext2.Bounds.fixedY = 4.0 - scrollValue;
			richtext2.Bounds.CalcWorldBounds();
		}
		int num2 = ((genericResultLines.Count != 0) ? Math.Min(genericResultLines.Count, (int)(scrollValue / CurrentGenericResultRowHeight) + 1) : 0);
		int num3 = Math.Min(genericResultLines.Count, (int)Math.Ceiling((scrollValue + genericResultsHeight) / CurrentGenericResultRowHeight));
		SingleComposer.GetDynamicText("results-page-count")?.SetNewText(SuiteLocalization.Get("gui.workspace.resultrange", num2, num3, genericResultLines.Count));
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
		double num = Math.Max(genericResultsHeight, (double)genericResultLines.Count * CurrentGenericResultRowHeight + 8.0);
		double max = Math.Max(0.0, num - genericResultsHeight);
		genericScrollValue = Math.Clamp(value, 0.0, max);
		(SingleComposer?.GetScrollbar("results-scroll"))?.SetScrollbarPosition((int)Math.Round(genericScrollValue));
		PositionGenericResultRows(genericScrollValue);
	}

	public override void OnMouseWheel(MouseWheelEventArgs args)
	{
		base.OnMouseWheel(args);
		if (args.IsHandled)
		{
			return;
		}
		if (section == "admincommands" && adminCommandView == "inventory" && IsAdminInventoryViewerResult())
		{
			double num = ((Math.Abs(args.deltaPrecise) > 0.001f) ? args.deltaPrecise : ((float)args.delta));
			if (Math.Abs(num) > 0.001)
			{
				double max = Math.Max(0.0, adminInventoryContentHeight - adminInventoryViewportHeight);
				adminInventoryScrollValue = Math.Clamp(adminInventoryScrollValue - num * 42.0, 0.0, max);
				(SingleComposer?.GetScrollbar("admin-inventory-scroll"))?.SetScrollbarPosition((int)Math.Round(adminInventoryScrollValue));
				PositionAdminInventoryElements(adminInventoryScrollValue);
				args.SetHandled();
			}
		}
		else if (SingleComposer?.GetScrollbar("results-scroll") != null && genericResultLines.Count > 1)
		{
			double num2 = ((Math.Abs(args.deltaPrecise) > 0.001f) ? args.deltaPrecise : ((float)args.delta));
			if (!(Math.Abs(num2) <= 0.001))
			{
				SetGenericScrollPosition(genericScrollValue - num2 * CurrentGenericResultRowHeight * 2.5);
				args.SetHandled();
			}
		}
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
		foreach (string line in lines)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}
			string[] array = line.Split('\t');
			int result6;
			int result7;
			int result8;
			int result9;
			int result10;
			if (array.Length >= 2 && array[0] == "MODE")
			{
				populationMode = array[1];
			}
			else if (array.Length >= 7 && array[0] == "TOTAL")
			{
				if (int.TryParse(array[2], out var result) && int.TryParse(array[3], out var result2) && int.TryParse(array[4], out var result3) && int.TryParse(array[5], out var result4) && int.TryParse(array[6], out var result5))
				{
					populationMobTotals[array[1]] = new PopulationMobTotal(result, result2, result3, result4, result5);
				}
			}
			else if (array.Length >= 9 && !(array[0] != "ROW") && int.TryParse(array[4], out result6) && int.TryParse(array[5], out result7) && int.TryParse(array[6], out result8) && int.TryParse(array[7], out result9) && int.TryParse(array[8], out result10))
			{
				populationRows.Add(new PopulationDisplayRow(array[1], array[2], array[3], result6, result7, result8, result9, result10));
			}
		}
		populationFirstIndex = 0;
	}

	private void ConfigurePopulationScrollbar()
	{
		GuiElementScrollbar guiElementScrollbar = SingleComposer?.GetScrollbar("population-scroll");
		if (guiElementScrollbar != null)
		{
			int num = Math.Max(16, populationRows.Count);
			int max = Math.Max(0, populationRows.Count - 16);
			populationFirstIndex = Math.Clamp(populationFirstIndex, 0, max);
			guiElementScrollbar.SetHeights(16f, num);
			guiElementScrollbar.SetScrollbarPosition(populationFirstIndex);
		}
	}

	private void OnPopulationScroll(float value)
	{
		int max = Math.Max(0, populationRows.Count - 16);
		int num = Math.Clamp((int)Math.Round(value), 0, max);
		if (num != populationFirstIndex)
		{
			populationFirstIndex = num;
			UpdatePopulationSlots();
		}
	}

	private void UpdatePopulationSlots()
	{
		if (SingleComposer == null)
		{
			return;
		}
		for (int i = 0; i < 16; i++)
		{
			int num = populationFirstIndex + i;
			PopulationDisplayRow populationDisplayRow = ((num < populationRows.Count) ? populationRows[num] : null);
			SetPopulationCell($"population-mob-{i}", (populationDisplayRow == null) ? "" : ("<strong>" + Escape(populationDisplayRow.MobId) + "</strong>"));
			SetPopulationCell($"population-region-{i}", (populationDisplayRow == null) ? "" : Escape(populationDisplayRow.Region));
			SetPopulationCell($"population-rule-{i}", (populationDisplayRow == null) ? "" : Escape(populationDisplayRow.Rule));
			SetPopulationCell($"population-tracked-{i}", (populationDisplayRow == null) ? "" : populationDisplayRow.Tracked.ToString());
			string text = "";
			if (populationDisplayRow != null)
			{
				text = (populationMobTotals.TryGetValue(populationDisplayRow.MobId, out PopulationMobTotal value) ? $"<strong>{value.Tracked}</strong>" : $"<strong>{populationDisplayRow.Tracked}</strong>");
			}
			SetPopulationCell($"population-mobtotal-{i}", text);
			SetPopulationCell($"population-limit-{i}", (populationDisplayRow == null) ? "" : populationDisplayRow.Limit.ToString());
			SetPopulationCell($"population-radius-{i}", (populationDisplayRow == null) ? "" : (populationDisplayRow.Radius + " ch"));
			InvestigatorDialog investigatorDialog = this;
			string key = $"population-protected-{i}";
			string text2;
			if (populationDisplayRow == null)
			{
				text2 = "";
			}
			else
			{
				text2 = ((populationDisplayRow.Protected > 0) ? $"<font color=\"#ffd166\"><strong>{populationDisplayRow.Protected}</strong></font>" : "0");
			}
			investigatorDialog.SetPopulationCell(key, text2);
			if (populationDisplayRow == null)
			{
				SetPopulationCell($"population-status-{i}", "");
				continue;
			}
			int num2 = Math.Max(0, populationDisplayRow.Eligible - populationDisplayRow.Limit);
			string text3 = ((num2 > 0) ? $"<font color=\"#ff6b6b\"><strong>OVER +{num2}</strong></font>" : "<font color=\"#8fda72\"><strong>OK</strong></font>");
			SetPopulationCell($"population-status-{i}", text3);
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
		{
			removalLogLines.Add("No removal records found.");
		}
		else
		{
			removalLogLines.AddRange(lines);
		}
		removalLogFirstIndex = 0;
	}

	private void ConfigureRemovalLogScrollbar()
	{
		GuiElementScrollbar guiElementScrollbar = SingleComposer?.GetScrollbar("removal-scroll");
		if (guiElementScrollbar != null)
		{
			int num = Math.Max(9, removalLogLines.Count);
			int max = Math.Max(0, removalLogLines.Count - 9);
			removalLogFirstIndex = Math.Clamp(removalLogFirstIndex, 0, max);
			guiElementScrollbar.SetHeights(9f, num);
			guiElementScrollbar.SetScrollbarPosition(removalLogFirstIndex);
		}
	}

	private void OnRemovalLogScroll(float value)
	{
		int max = Math.Max(0, removalLogLines.Count - 9);
		int num = Math.Clamp((int)Math.Round(value), 0, max);
		if (num != removalLogFirstIndex)
		{
			removalLogFirstIndex = num;
			UpdateRemovalLogSlots();
		}
	}

	private void UpdateRemovalLogSlots()
	{
		if (SingleComposer == null)
		{
			return;
		}
		for (int i = 0; i < 9; i++)
		{
			GuiElementRichtext richtext = SingleComposer.GetRichtext($"removal-row-{i}");
			if (richtext != null)
			{
				int num = removalLogFirstIndex + i;
				string vtmlCode = ((num < removalLogLines.Count) ? Escape(removalLogLines[num]) : "");
				richtext.SetNewText(vtmlCode, CairoFont.WhiteSmallText());
			}
		}
	}

	private bool ScrollRemovalLogsPrevious()
	{
		SetRemovalLogPosition(removalLogFirstIndex - 9);
		return true;
	}

	private bool ScrollRemovalLogsNext()
	{
		SetRemovalLogPosition(removalLogFirstIndex + 9);
		return true;
	}

	private void SetRemovalLogPosition(int firstIndex)
	{
		int max = Math.Max(0, removalLogLines.Count - 9);
		removalLogFirstIndex = Math.Clamp(firstIndex, 0, max);
		(SingleComposer?.GetScrollbar("removal-scroll"))?.SetScrollbarPosition(removalLogFirstIndex);
		UpdateRemovalLogSlots();
	}

	private void ParseAnimalRuleLines(List<string>? lines)
	{
		animalRuleRows.Clear();
		animalRuleCount = 0;
		animalExclusionCount = 0;
		if (lines == null)
		{
			return;
		}
		bool flag = false;
		bool flag2 = false;
		foreach (string line in lines)
		{
			string text = line?.Trim() ?? "";
			if (text.StartsWith("Rules:", StringComparison.OrdinalIgnoreCase))
			{
				int.TryParse(text.Substring(6).Trim(), out animalRuleCount);
			}
			else if (text.StartsWith("Exclusions:", StringComparison.OrdinalIgnoreCase))
			{
				int.TryParse(text.Substring(11).Trim(), out animalExclusionCount);
			}
			else if (text.Equals("LIMIT RULES", StringComparison.OrdinalIgnoreCase))
			{
				flag = true;
				flag2 = false;
			}
			else if (text.Equals("EXCLUSIONS", StringComparison.OrdinalIgnoreCase))
			{
				flag = false;
				flag2 = true;
			}
			else if (!string.IsNullOrWhiteSpace(text) && !(text == "(none)"))
			{
				if (flag)
				{
					string[] array = text.Split('|');
					string pattern = ((array.Length != 0) ? array[0].Trim() : text);
					string text2 = ((array.Length > 1) ? array[1].Trim() : "");
					string text3 = ((array.Length > 2) ? array[2].Trim() : "");
					animalRuleRows.Add(new AnimalRuleDisplayRow(pattern, text2, text3, IsExclusion: false));
				}
				else if (flag2)
				{
					animalRuleRows.Add(new AnimalRuleDisplayRow(text, "Excluded", "—", IsExclusion: true));
				}
			}
		}
		int num = Math.Max(1, (animalRuleRows.Count + 18 - 1) / 18);
		animalRulePage = Math.Clamp(animalRulePage, 0, num - 1);
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
		int num = Math.Max(1, (animalRuleRows.Count + 18 - 1) / 18);
		if (animalRulePage + 1 < num)
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
				y = 176.0;
				height = 474.0;
			}
			else if (animalView == "rules")
			{
				y = 232.0;
				height = 418.0;
			}
			else if (animalView == "settings")
			{
				y = 560.0;
				height = 92.0;
			}
			else
			{
				y = 406.0;
				height = 244.0;
			}
		}
		else if (view == "status")
		{
			y = 170.0;
			height = 480.0;
		}
		else
		{
			y = 324.0;
			height = 326.0;
		}
	}

	private static string ViewTitle(string value)
	{
		if (1 == 0)
		{
		}
		string result = value switch
		{
			"containers" => "Container history", 
			"entities" => "Entity history", 
			"players" => "Player history", 
			"settings" => "BlockLogger settings", 
			"animals" => "Animal Limiter", 
			_ => "Block history", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private string FormatGenericResultLine(string? raw)
	{
		if (section == "blocklogger" && view == "status")
		{
			return FormatStatusResultLine(raw);
		}
		if (section == "animals" && animalView == "limiter")
		{
			return FormatStatusResultLine(raw);
		}
		if (section == "loginprotection" && loginView == "status")
		{
			return FormatStatusResultLine(raw);
		}
		if (section == "webserver" && webView == "webpanel")
		{
			return FormatStatusResultLine(raw);
		}
		if (section == "anticheat")
		{
			return FormatAntiCheatResultLine(raw);
		}
		if (section == "admincommands")
		{
			return FormatAdminCommandResultLine(raw);
		}
		return FormatAuditResultLine(raw);
	}

	private static string FormatAdminCommandResultLine(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return "";
		}
		string text = raw.Trim();
		string[] array = text.Split('\t');
		if (array.Length >= 9 && array[0] == "INV")
		{
			string text2 = array[1];
			string text3 = array[2];
			string text4 = array[3];
			if (array[4] == "1" || array[4].Equals("true", StringComparison.OrdinalIgnoreCase))
			{
				return $"<font color=\"#7db6ff\"><strong>{Escape(text3)}</strong></font>  <strong>{Escape(text2)}[{Escape(text4)}]</strong>  <font color=\"#91a0ae\">EMPTY SLOT</font><br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────</font>";
			}
			string text5 = array[5];
			string text6 = array[6];
			string text7 = array[7];
			string text8 = array[8];
			string text9 = "";
			if (array.Length >= 11 && int.TryParse(array[9], out var result) && int.TryParse(array[10], out var result2) && result2 > 0)
			{
				text9 += $" • durability {result}/{result2}";
			}
			if (array.Length >= 12 && float.TryParse(array[11], NumberStyles.Float, CultureInfo.InvariantCulture, out var result3) && Math.Abs(result3) > 0.05f)
			{
				text9 += $" • {result3:0.#}°C";
			}
			return $"<font color=\"#7db6ff\"><strong>{Escape(text3)}</strong></font>  <strong>{Escape(text2)}[{Escape(text4)}]</strong>  {Escape(text5)}x <strong>{Escape(text8)}</strong><br><font color=\"#91a0ae\">{Escape(text7)} • max {Escape(text6)}{Escape(text9)}</font>";
		}
		if (array.Length >= 5 && array[0] == "ITEM")
		{
			return $"<font color=\"#7db6ff\"><strong>{Escape(array[2])}</strong></font><br><font color=\"#91a0ae\">{Escape(array[1])} • {Escape(array[3])} • max stack {Escape(array[4])}</font><br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────</font>";
		}
		if (array.Length >= 7 && array[0] == "ADMINPLAYER")
		{
			return $"<font color=\"#7db6ff\"><strong>{Escape(array[2])}</strong></font> <font color=\"#77d18b\">{Escape(SuiteLocalization.Get("gui.runtime.online"))}</font><br><font color=\"#91a0ae\">{Escape(array[3])} ms • {Escape(array[4])} • {Escape(array[5])} • HP {Escape(array[6])}</font>";
		}
		if (array.Length >= 3 && array[0] == "ADMINSTATUS")
		{
			bool flag = string.Equals(array[1], "OK", StringComparison.OrdinalIgnoreCase);
			string value = (flag ? "#77d18b" : "#ff7b7b");
			return $"<font color=\"{value}\"><strong>{Escape(SuiteLocalization.Get(flag ? "gui.runtime.success" : "gui.runtime.attention"))}</strong></font>  {Escape(array[2])}";
		}
		if (array.Length >= 2 && array[0] == "ADMINLINE")
		{
			return "<font color=\"#b8c6d4\">" + Escape(array[1]) + "</font>";
		}
		return Escape(text);
	}

	private static string FormatAntiCheatResultLine(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return "";
		}
		string text = raw.Trim();
		string[] array = text.Split('\t');
		if (array.Length >= 10 && array[0] == "PLAYER")
		{
			int.TryParse(array[1], out var result);
			string text2 = array[2];
			string text3 = array[3];
			string text4 = array[4];
			string text5 = array[5];
			string text6 = array[6];
			string text7 = array[7];
			string text8 = array[8];
			string text9 = array[9];
			string value = RiskColor(result);
			string value2 = StatusColor(text3);
			return $"<font color=\"{value}\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.risk"))} {result}/100 • {Escape(text2)}</strong></font>   <strong>{Escape(text4)}</strong> <font color=\"{value2}\">[{Escape(text3)}]</font><br><font color=\"#91a0ae\">{Escape(SuiteLocalization.Get("gui.runtime.findings"))} {Escape(text6)} • {Escape(SuiteLocalization.Get("gui.runtime.runtime"))} +{Escape(text7)} • {Escape(SuiteLocalization.Get("gui.runtime.client"))} {Escape(text5)} • {Escape(SuiteLocalization.Get("gui.runtime.mods"))} {Escape(text8)} • {Escape(SuiteLocalization.Get("gui.runtime.assemblies"))} {Escape(text9)}</font><br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────</font>";
		}
		if (array.Length >= 13 && array[0] == "REPORT")
		{
			int.TryParse(array[3], out var result2);
			int.TryParse(array[4], out var result3);
			int.TryParse(array[7], out var result4);
			int.TryParse(array[8], out var result5);
			string value3 = RiskColor(result3);
			string value4 = ConfidenceColor(array[6]);
			string value5 = SeverityColor(result5);
			string text10 = ShortDisplay(array[11], 220);
			string text11 = (string.IsNullOrWhiteSpace(array[12]) ? SuiteLocalization.Get("gui.runtime.noactionrecommendation") : ShortDisplay(array[12], 180));
			return $"<font color=\"#7db6ff\"><strong>{Escape(array[2])}</strong></font>  <font color=\"#91a0ae\">{Escape(array[1])}</font><br><strong>{Escape(SuiteLocalization.Get("gui.runtime.risklabel"))}:</strong> {result2} → <font color=\"{value3}\"><strong>{result3}/100 • {Escape(array[5])}</strong></font><br><strong>{Escape(SuiteLocalization.Get("gui.runtime.detector"))}:</strong> <font color=\"{value5}\"><strong>{Escape(array[9])} / {Escape(array[10])}</strong></font>   <strong>{Escape(SuiteLocalization.Get("gui.runtime.confidence"))}:</strong> <font color=\"{value4}\"><strong>{Escape(array[6])} {result4}%</strong></font><br><strong>{Escape(SuiteLocalization.Get("gui.runtime.incident"))}:</strong> {Escape(text10)}<br><font color=\"#91a0ae\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.recommended"))}:</strong> {Escape(text11)}</font><br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
		}
		if (array.Length >= 11 && array[0] == "RULE")
		{
			bool flag = array[4] == "1" || array[4].Equals("true", StringComparison.OrdinalIgnoreCase);
			string value6 = (flag ? "#8fda72" : "#91a0ae");
			string text12 = (string.IsNullOrWhiteSpace(array[7]) ? SuiteLocalization.Get("gui.runtime.anysubject") : array[7]);
			string text13 = (string.IsNullOrWhiteSpace(array[9]) ? "n/a" : array[9]);
			string text14 = (string.IsNullOrWhiteSpace(array[10]) ? SuiteLocalization.Get("gui.runtime.defaultacmessage") : ShortDisplay(array[10], 150));
			return $"<font color=\"{value6}\"><strong>{Escape(SuiteLocalization.Get(flag ? "gui.runtime.enabledcaps" : "gui.runtime.disabledcaps"))}</strong></font>  <font color=\"#7db6ff\"><strong>{Escape(array[3])}</strong></font><br><strong>{Escape(SuiteLocalization.Get("gui.runtime.when"))}:</strong> {Escape(array[5])} = {Escape(array[6])}   <strong>{Escape(SuiteLocalization.Get("gui.runtime.subject"))}:</strong> {Escape(text12)}<br><strong>{Escape(SuiteLocalization.Get("gui.runtime.action"))}:</strong> {Escape(array[8])}   <strong>{Escape(SuiteLocalization.Get("gui.runtime.duration"))}:</strong> {Escape(text13)}<br><font color=\"#91a0ae\">{Escape(SuiteLocalization.Get("gui.runtime.message"))}: {Escape(text14)}</font><br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
		}
		if (array.Length >= 8 && array[0] == "BLACKLIST")
		{
			string value7 = ((array.Length >= 9 && !string.IsNullOrWhiteSpace(array[8])) ? (" • " + Escape(SuiteLocalization.Get("gui.runtime.latest")) + " " + Escape(array[8])) : "");
			string value8 = ((array.Length >= 10 && !string.IsNullOrWhiteSpace(array[9]) && !array[9].Equals("never", StringComparison.OrdinalIgnoreCase)) ? (" • " + Escape(SuiteLocalization.Get("gui.runtime.refreshed")) + " " + Escape(array[9])) : "");
			return $"<font color=\"#ffad66\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.blacklisted"))} • {Escape(array[1])}</strong></font>   <font color=\"#b8c5cf\">{Escape(array[2])}</font><br><font color=\"#91a0ae\">{Escape(SuiteLocalization.Get("gui.runtime.packagecontent"))} {Escape(array[3])} / {Escape(array[4])} • {Escape(SuiteLocalization.Get("gui.runtime.assemblyfp"))} {Escape(array[5])} • {Escape(SuiteLocalization.Get("gui.runtime.manual"))} {Escape(array[6])} • {Escape(SuiteLocalization.Get("gui.runtime.releases"))} {Escape(array[7])}{value7}{value8}</font><br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────</font>";
		}
		if (array.Length >= 5 && array[0] == "FINGERPRINT")
		{
			return $"<font color=\"#7db6ff\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.clientruntimefps"))}</strong></font><br><font color=\"#91a0ae\">DLL {Escape(array[1])} • package {Escape(array[2])}<br>MVID {Escape(array[3])} • runtime {Escape(array[4])}</font><br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
		}
		if (array.Length >= 5 && array[0] == "MOD")
		{
			return $"<font color=\"#7db6ff\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.clientmod"))}</strong></font>  <strong>{Escape(array[1])}</strong> {Escape(array[2])}<br><font color=\"#91a0ae\">{Escape(array[3])} • {Escape(array[4])}</font><br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
		}
		if (array.Length >= 9 && array[0] == "FINDING")
		{
			int.TryParse(array[1], out var result6);
			int.TryParse(array[3], out var result7);
			string value9 = ConfidenceColor(array[2]);
			string value10 = SeverityColor(result6);
			return $"<font color=\"{value10}\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.findingcaps"))} • {Escape(array[4])} / {Escape(array[5])}</strong></font>   <font color=\"{value9}\"><strong>{Escape(array[2])} {result7}%</strong></font><br>{Escape(array[6])}<br><font color=\"#91a0ae\">{Escape(SuiteLocalization.Get("gui.runtime.subject"))}: {Escape(array[7])}" + (string.IsNullOrWhiteSpace(array[8]) ? "" : (" • " + Escape(SuiteLocalization.Get("gui.runtime.recommended")) + ": " + Escape(array[8]))) + "</font><br><font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
		}
		if (text.StartsWith("Runtime + ", StringComparison.Ordinal))
		{
			return $"<font color=\"#c69cff\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.runtimechange"))}</strong></font><br>{Escape(text.Substring(10))}<br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
		}
		if (text.StartsWith("Scanner ", StringComparison.Ordinal))
		{
			return $"<font color=\"#7db6ff\"><strong>{Escape(SuiteLocalization.Get("gui.runtime.scannermetric"))}</strong></font><br>{Escape(text.Substring(8))}<br>" + "<font color=\"#263746\">────────────────────────────────────────────────────────────────────────</font>";
		}
		int num = text.IndexOf(':');
		if (num > 0)
		{
			string text15 = text.Substring(0, num).Trim();
			string text16 = text.Substring(num + 1).Trim();
			string value11;
			if (text15.Equals("Error", StringComparison.OrdinalIgnoreCase))
			{
				value11 = "#ff7474";
			}
			else if (text15.Equals("Risk rating", StringComparison.OrdinalIgnoreCase))
			{
				value11 = RiskRatingColor(text16);
			}
			else if (text15.Equals("Status", StringComparison.OrdinalIgnoreCase))
			{
				value11 = StatusColor(text16);
			}
			else if (text15.Contains("Blacklisted", StringComparison.OrdinalIgnoreCase))
			{
				value11 = "#ffad66";
			}
			else
			{
				value11 = (text15.Contains("Persistent reports", StringComparison.OrdinalIgnoreCase) ? "#7db6ff" : "#edf3f8");
			}
			string text17;
			if (text15.Equals("Error", StringComparison.OrdinalIgnoreCase))
			{
				text17 = SuiteLocalization.Get("gui.runtime.error");
			}
			else if (text15.Equals("Risk rating", StringComparison.OrdinalIgnoreCase))
			{
				text17 = SuiteLocalization.Get("gui.runtime.riskrating");
			}
			else if (text15.Equals("Status", StringComparison.OrdinalIgnoreCase))
			{
				text17 = SuiteLocalization.Get("gui.runtime.status");
			}
			else
			{
				text17 = (text15.Equals("Persistent reports", StringComparison.OrdinalIgnoreCase) ? SuiteLocalization.Get("gui.runtime.persistentreports") : text15);
			}
			return $"<strong>{Escape(text17)}:</strong> <font color=\"{value11}\">{Escape(text16)}</font>";
		}
		return Escape(text);
	}

	private static string ShortDisplay(string? value, int maxChars)
	{
		string text = (value ?? "").Trim();
		if (text.Length <= maxChars)
		{
			return text;
		}
		return text.Substring(0, Math.Max(1, maxChars - 1)).TrimEnd() + "…";
	}

	private static string RiskColor(int risk)
	{
		string result;
		if (risk >= 75)
		{
			result = "#ff7474";
		}
		else if (risk >= 50)
		{
			result = "#ffad66";
		}
		else if (risk >= 25)
		{
			result = "#ffd166";
		}
		else
		{
			result = ((risk >= 10) ? "#c69cff" : "#8fda72");
		}
		return result;
	}

	private static string RiskRatingColor(string rating)
	{
		string text = rating.Trim().ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result = text switch
		{
			"critical" => "#ff7474", 
			"high" => "#ffad66", 
			"elevated" => "#ffd166", 
			"guarded" => "#c69cff", 
			_ => "#8fda72", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string StatusColor(string status)
	{
		string text = status.Trim().ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result;
		switch (text)
		{
		case "suspicious":
		case "failed":
		case "protocolmismatch":
			result = "#ff7474";
			break;
		case "review":
		case "timedout":
			result = "#ffd166";
			break;
		case "clean":
			result = "#8fda72";
			break;
		default:
			result = "#7db6ff";
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static string ConfidenceColor(string confidence)
	{
		string text = confidence.Trim().ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result = text switch
		{
			"critical" => "#ff7474", 
			"high" => "#ffad66", 
			"medium" => "#ffd166", 
			"low" => "#c69cff", 
			_ => "#91a0ae", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string SeverityColor(int severity)
	{
		string result;
		if (severity >= 4)
		{
			result = "#ff7474";
		}
		else if (severity >= 3)
		{
			result = "#ffad66";
		}
		else if (severity >= 2)
		{
			result = "#ffd166";
		}
		else
		{
			result = ((severity >= 1) ? "#c69cff" : "#91a0ae");
		}
		return result;
	}

	private static string FormatStatusResultLine(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return "";
		}
		string text = raw.Trim();
		int num = text.IndexOf(':');
		if (num > 0)
		{
			string text2 = Escape(text.Substring(0, num));
			string text3 = Escape(text.Substring(num + 1).TrimStart());
			return "<strong>" + text2 + ":</strong> " + text3;
		}
		return Escape(text);
	}

	private static string FormatAuditResultLine(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return "";
		}
		string[] array = raw.Split(new string[1] { " | " }, StringSplitOptions.None);
		if (array.Length < 5)
		{
			return Escape(raw);
		}
		string value = Escape(array[0]);
		string value2 = Escape(array[1]);
		string value3 = Escape(array[2]);
		string value4 = Escape(array[3]);
		string value5 = ActionColor(array[2]);
		string value6 = $"<font color=\"{value5}\">{value3}</font>";
		string text = "";
		for (int i = 4; i < array.Length; i++)
		{
			text = text + " | " + Escape(array[i]);
		}
		return $"{value} | <strong>{value2}</strong> | {value6} | <strong>{value4}</strong>{text}";
	}

	private static string ActionColor(string? detail)
	{
		string text = detail?.Trim().ToLowerInvariant() ?? "";
		if (text.StartsWith("broke "))
		{
			return "#ff7070";
		}
		if (text.StartsWith("placed "))
		{
			return "#8fda72";
		}
		if (text.StartsWith("used "))
		{
			return "#ffd166";
		}
		if (text.StartsWith("opened "))
		{
			return "#72c7ff";
		}
		if (text.StartsWith("took "))
		{
			return "#ffad66";
		}
		if (text.StartsWith("put "))
		{
			return "#66d9c2";
		}
		if (text.StartsWith("interacted with "))
		{
			return "#c69cff";
		}
		if (text.StartsWith("spawned "))
		{
			return "#78dcca";
		}
		if (text.EndsWith(" died", StringComparison.Ordinal))
		{
			return "#ff6b6b";
		}
		if (text.StartsWith("despawned "))
		{
			return "#aeb8d4";
		}
		return "#e8e0cc";
	}

	private static string FormatLines(List<string>? lines)
	{
		if (lines == null || lines.Count == 0)
		{
			return "No records found.";
		}
		string[] array = new string[lines.Count];
		for (int i = 0; i < lines.Count; i++)
		{
			array[i] = Escape(lines[i]);
		}
		return string.Join("<br>", array);
	}

	private static string ErrorText(string? text)
	{
		return "<font color=\"#ff8080\">" + Escape(text) + "</font>";
	}

	private static string Escape(string? text)
	{
		return WebUtility.HtmlEncode(text ?? "");
	}

	private void RememberWorkspaceNavigation()
	{
		if (section != lastComposedWorkspace)
		{
			previousWorkspace = lastComposedWorkspace;
			lastComposedWorkspace = section;
		}
		if (section != "home" && Array.IndexOf(SuiteSections, section) >= 0)
		{
			lastWorkspace = section;
		}
	}

	private static string SectionLabel(string value)
	{
		if (1 == 0)
		{
		}
		string key = value switch
		{
			"blocklogger" => "gui.blocklogger", 
			"animals" => "gui.animallimiter", 
			"profiler" => "gui.profiler", 
			"loginprotection" => "gui.loginprotection", 
			"webserver" => "gui.integrations", 
			"anticheat" => "gui.anticheat", 
			"admincommands" => "gui.playeradmin", 
			_ => "gui.workspace.home", 
		};
		if (1 == 0)
		{
		}
		return SuiteLocalization.Get(key);
	}

	private static string SectionHelp(string value)
	{
		if (1 == 0)
		{
		}
		string key = value switch
		{
			"blocklogger" => "gui.workspace.help.blocklogger", 
			"animals" => "gui.workspace.help.animals", 
			"profiler" => "gui.workspace.help.profiler", 
			"loginprotection" => "gui.workspace.help.loginprotection", 
			"webserver" => "gui.workspace.help.webserver", 
			"anticheat" => "gui.workspace.help.anticheat", 
			"admincommands" => "gui.workspace.help.admincommands", 
			_ => "gui.workspace.homehelp", 
		};
		if (1 == 0)
		{
		}
		return SuiteLocalization.Get(key);
	}

	private void BuildSuiteNavigation(GuiComposer composer, double width)
	{
		string[] names = Array.ConvertAll(SuiteSections, SectionLabel);
		composer.AddRichtext("<strong>" + SuiteLocalization.Get("gui.title") + "</strong>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(24.0, 12.0, width - 360.0, 26.0)).AddSuiteButton(SuiteLocalization.Get("gui.workspace.back"), () => SelectSection(previousWorkspace), ElementBounds.Fixed(width - 288.0, 9.0, 78.0, 28.0)).AddSuiteButton(SuiteLocalization.Get("gui.workspace.copyview"), () =>
		{
			capi.Input.ClipboardText = $"{SectionLabel(section)}\n{pageHeaderText}\n{resultText}";
			return true;
		}, ElementBounds.Fixed(width - 202.0, 9.0, 94.0, 28.0))
			.AddSuiteButton(SuiteLocalization.Get("gui.close"), () =>
			{
				TryClose();
				return true;
			}, ElementBounds.Fixed(width - 98.0, 9.0, 74.0, 28.0))
			.AddHoverText(SuiteLocalization.Get("gui.tip.close"), CairoFont.WhiteSmallText(), 280, ElementBounds.Fixed(width - 98.0, 9.0, 74.0, 28.0), "tip-close-investigator")
			.AddDropDown(SuiteSections, names, Math.Max(0, Array.IndexOf(SuiteSections, section)), (string value, bool selected) =>
			{
				if (selected)
				{
					SelectSection(value);
				}
			}, ElementBounds.Fixed(24.0, 43.0, 258.0, 30.0), CairoFont.WhiteSmallText(), "suite-workspace-picker")
			.AddHoverText(SuiteLocalization.Get("gui.workspace.switchhelp"), CairoFont.WhiteSmallText(), 350, ElementBounds.Fixed(24.0, 43.0, 258.0, 30.0), "tip-suite-workspace-picker")
			.AddStaticText(SectionHelp(section), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.67, 0.74, 0.82, 1.0 }), ElementBounds.Fixed(300.0, 44.0, width - 430.0, 30.0))
			.AddSuiteButton(SuiteLocalization.Get("gui.workspace.home"), () => SelectSection("home"), ElementBounds.Fixed(width - 110.0, 43.0, 86.0, 30.0), section == "home");
	}

	private void BuildSuiteHome(GuiComposer composer)
	{
		composer.AddRichtext("<font color=\"#7db6ff\"><strong>" + SuiteLocalization.Get("gui.workspace.welcome") + "</strong></font>", CairoFont.WhiteMediumText(), ElementBounds.Fixed(34.0, 132.0, 760.0, 34.0)).AddSuiteButton(SuiteLocalization.Get("gui.workspace.resume", SectionLabel(lastWorkspace)), () => SelectSection(lastWorkspace), ElementBounds.Fixed(814.0, 128.0, 210.0, 34.0)).AddStaticText(SuiteLocalization.Get("gui.workspace.starthelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 171.0, 980.0, 44.0));
		for (int num = 1; num < SuiteSections.Length; num++)
		{
			string target = SuiteSections[num];
			int num2 = num - 1;
			double num3 = 28 + num2 % 2 * 506;
			double num4 = 228 + num2 / 2 * 108;
			composer.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(num3, num4, 494.0, 96.0))).AddSuiteButton(SectionLabel(target), () => SelectSection(target), ElementBounds.Fixed(num3 + 16.0, num4 + 12.0, 226.0, 28.0)).AddStaticText(SectionHelp(target), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.71, 0.77, 0.84, 1.0 }), ElementBounds.Fixed(num3 + 16.0, num4 + 48.0, 462.0, 40.0));
		}
		composer.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(534.0, 552.0, 494.0, 96.0))).AddStaticText(SuiteLocalization.Get("gui.workspace.shortcuts"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(550.0, 563.0, 440.0, 24.0)).AddSuiteButton(SuiteLocalization.Get("gui.workspace.investigate"), () => SelectAntiCheatView("players"), ElementBounds.Fixed(550.0, 601.0, 218.0, 28.0))
			.AddSuiteButton(SuiteLocalization.Get("gui.workspace.audit"), () => SelectView("blocks"), ElementBounds.Fixed(778.0, 601.0, 232.0, 28.0))
			.AddStaticText(SuiteLocalization.Get("gui.workspace.permissions"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.64, 0.71, 0.78, 1.0 }), ElementBounds.Fixed(34.0, 684.0, 970.0, 38.0));
	}

	private bool SelectBlockSettingsView(string selected)
	{
		bool flag = ((selected == "storage" || selected == "queries") ? true : false);
		blockSettingsView = (flag ? selected : "events");
		ComposeDialog();
		return true;
	}

	private void BuildBlockLoggerSettings(GuiComposer composer)
	{
		composer.AddRichtext("<font color=\"#7db6ff\"><strong>" + SuiteLocalization.Get("gui.blsettings2") + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 132.0, 600.0, 26.0), "page-header").AddSuiteButton(SuiteLocalization.Get("gui.refresh"), RequestBlockLoggerSettings, ElementBounds.Fixed(912.0, 128.0, 116.0, 30.0)).AddStaticText(SuiteLocalization.Get("gui.blsettingssave"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 170.0, 980.0, 26.0))
			.AddSuiteButton(SuiteLocalization.Get("gui.workspace.events"), () => SelectBlockSettingsView("events"), ElementBounds.Fixed(34.0, 210.0, 168.0, 30.0), blockSettingsView == "events")
			.AddSuiteButton(SuiteLocalization.Get("gui.workspace.storage"), () => SelectBlockSettingsView("storage"), ElementBounds.Fixed(214.0, 210.0, 168.0, 30.0), blockSettingsView == "storage")
			.AddSuiteButton(SuiteLocalization.Get("gui.workspace.queries"), () => SelectBlockSettingsView("queries"), ElementBounds.Fixed(394.0, 210.0, 168.0, 30.0), blockSettingsView == "queries")
			.AddRichtext(SuiteLocalization.Get("gui.blenabled"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(738.0, 215.0, 200.0, 24.0))
			.AddSwitch((bool value) =>
			{
				SetBlockLoggerBool("enabled", value);
			}, ElementBounds.Fixed(956.0, 210.0, 60.0, 30.0), "bl-enabled")
			.AddHoverText(SuiteLocalization.Get("gui.tip.more.4361c09efd"), CairoFont.WhiteSmallText(), 390, ElementBounds.Fixed(956.0, 210.0, 60.0, 30.0), "tip-bl-enabled");
		if (blockSettingsView == "events")
		{
			AddSettingsCard(composer, "gui.blockevents", 28.0, 264.0, 492.0, 222.0);
			AddSettingsCard(composer, "gui.containersheading", 536.0, 264.0, 492.0, 222.0);
			AddBlockToggle(composer, "gui.break", "logblockbreak", "bl-break", 44.0, 318.0);
			AddBlockToggle(composer, "gui.place", "logblockplace", "bl-place", 44.0, 362.0);
			AddBlockToggle(composer, "gui.use", "logblockuse", "bl-use", 44.0, 406.0);
			AddBlockToggle(composer, "gui.access", "logcontaineraccess", "bl-container-access", 552.0, 318.0);
			AddBlockToggle(composer, "gui.itemchanges", "logcontaineritemchanges", "bl-container-items", 552.0, 362.0);
			AddSettingsCard(composer, "gui.entityevents", 28.0, 502.0, 1000.0, 164.0);
			AddBlockToggle(composer, "gui.interact", "logentityinteract", "bl-entity-interact", 44.0, 548.0, 264.0);
			AddBlockToggle(composer, "gui.spawn", "logentityspawn", "bl-entity-spawn", 44.0, 592.0, 264.0);
			AddBlockToggle(composer, "gui.death", "logentitydeath", "bl-entity-death", 374.0, 548.0, 264.0);
			AddBlockToggle(composer, "gui.despawn", "logentitydespawn", "bl-entity-despawn", 374.0, 592.0, 264.0);
			AddBlockToggle(composer, "gui.aggregate", "aggregateentityspawndespawn", "bl-aggregate", 708.0, 548.0, 264.0);
		}
		else if (blockSettingsView == "storage")
		{
			AddSettingsCard(composer, "gui.workspace.retention", 28.0, 264.0, 1000.0, 118.0);
			AddBlockNumber(composer, "gui.retentiondays", "bl-retention", OnBlockRetentionChanged, SaveBlockRetention, 44.0, 318.0, "gui.tip.more.50ed193127");
			AddSettingsCard(composer, "gui.workspace.writer", 28.0, 398.0, 1000.0, 222.0);
			AddBlockNumber(composer, "gui.flushinterval", "bl-flush", OnBlockFlushChanged, SaveBlockFlush, 44.0, 450.0, "gui.tip.more.052803c007");
			AddBlockNumber(composer, "gui.maxqueue", "bl-queue", OnBlockQueueChanged, SaveBlockQueue, 552.0, 450.0, "gui.tip.more.e84f3495d3");
			AddBlockNumber(composer, "gui.emergencyoverflow", "bl-overflow", OnBlockOverflowChanged, SaveBlockOverflow, 44.0, 502.0, "gui.tip.more.f02bb317d5");
		}
		else
		{
			AddSettingsCard(composer, "gui.workspace.radius", 28.0, 264.0, 1000.0, 170.0);
			AddBlockNumber(composer, "gui.defaultblockradius", "bl-default-radius", OnBlockDefaultRadiusChanged, SaveBlockDefaultRadius, 44.0, 316.0);
			AddBlockNumber(composer, "gui.defaultentityradius", "bl-entity-radius", OnBlockEntityRadiusChanged, SaveBlockEntityRadius, 552.0, 316.0);
			AddBlockNumber(composer, "gui.maximumradius", "bl-max-radius", OnBlockMaxRadiusChanged, SaveBlockMaxRadius, 44.0, 368.0);
			AddSettingsCard(composer, "gui.workspace.rows", 28.0, 450.0, 1000.0, 170.0);
			AddBlockNumber(composer, "gui.defaultrows", "bl-default-rows", OnBlockDefaultRowsChanged, SaveBlockDefaultRows, 44.0, 502.0);
			AddBlockNumber(composer, "gui.defaultentityrows", "bl-default-entity-rows", OnBlockDefaultEntityRowsChanged, SaveBlockDefaultEntityRows, 552.0, 502.0);
			AddBlockNumber(composer, "gui.maxresults", "bl-max-results", OnBlockMaxResultsChanged, SaveBlockMaxResults, 44.0, 554.0, "gui.tip.more.89f406f6e0");
		}
		composer.AddStaticText(SuiteLocalization.Get("gui.blockloggerlowlevelnote"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.67, 0.74, 0.82, 1.0 }), ElementBounds.Fixed(34.0, 690.0, 980.0, 40.0));
	}

	private void AddSettingsCard(GuiComposer composer, string titleKey, double x, double y, double width, double height)
	{
		composer.AddStaticElement(new SuiteSettingsCardElement(capi, ElementBounds.Fixed(x, y, width, height))).AddRichtext(SuiteLocalization.Get(titleKey), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.49, 0.714, 1.0, 1.0 }), ElementBounds.Fixed(x + 16.0, y + 15.0, width - 32.0, 26.0));
	}

	private void BuildAntiCheatClientSettings(GuiComposer composer)
	{
		composer.AddRichtext("<font color=\"#7db6ff\"><strong>" + pageHeaderText + "</strong></font>", CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 132.0, 690.0, 26.0), "page-header").AddSuiteButton(SuiteLocalization.Get("gui.literal.0022a3e10c"), SaveAntiCheatSettings, ElementBounds.Fixed(770.0, 128.0, 136.0, 30.0)).AddHoverText(SuiteLocalization.Get("gui.saveallsettings"), CairoFont.WhiteSmallText(), 380, ElementBounds.Fixed(770.0, 128.0, 136.0, 30.0), "tip-anticheat-settings-save")
			.AddSuiteButton(SuiteLocalization.Get("gui.reload"), () => RequestAntiCheat("settings"), ElementBounds.Fixed(918.0, 128.0, 110.0, 30.0))
			.AddRichtext(SuiteLocalization.Get("gui.settingsstagedhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(34.0, 174.0, 980.0, 40.0));
		AddSettingsCard(composer, "gui.clientscanner", 28.0, 234.0, 1000.0, 238.0);
		composer.AddRichtext(SuiteLocalization.Get("gui.requireclient"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 288.0, 332.0, 28.0)).AddSwitch((bool v) =>
		{
			SetAntiCheatSettingBool("requireclient", v);
		}, ElementBounds.Fixed(452.0, 282.0, 52.0, 26.0), "ac-set-require-client").AddRichtext(SuiteLocalization.Get("gui.periodicrescan"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550.0, 288.0, 318.0, 28.0))
			.AddNumberInput(ElementBounds.Fixed(912.0, 282.0, 96.0, 28.0), (string v) =>
			{
				SetAntiCheatSettingInt("rescan", v);
			}, CairoFont.WhiteSmallText(), "ac-set-rescan")
			.AddRichtext(SuiteLocalization.Get("gui.rescanruntime"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 332.0, 332.0, 28.0))
			.AddSwitch((bool v) =>
			{
				SetAntiCheatSettingBool("runtime-rescan", v);
			}, ElementBounds.Fixed(452.0, 326.0, 52.0, 26.0), "ac-set-runtime-rescan")
			.AddRichtext(SuiteLocalization.Get("gui.rescancooldown"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550.0, 332.0, 318.0, 28.0))
			.AddNumberInput(ElementBounds.Fixed(912.0, 326.0, 96.0, 28.0), (string v) =>
			{
				SetAntiCheatSettingInt("runtime-cooldown", v);
			}, CairoFont.WhiteSmallText(), "ac-set-runtime-cooldown")
			.AddRichtext(SuiteLocalization.Get("gui.similarity"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 376.0, 332.0, 28.0))
			.AddSwitch((bool v) =>
			{
				SetAntiCheatSettingBool("similarity", v);
			}, ElementBounds.Fixed(452.0, 370.0, 52.0, 26.0), "ac-set-similarity")
			.AddRichtext(SuiteLocalization.Get("gui.similaritythresholds"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550.0, 376.0, 300.0, 28.0))
			.AddRichtext(SuiteLocalization.Get("gui.reviewplain"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(550.0, 419.0, 84.0, 26.0))
			.AddNumberInput(ElementBounds.Fixed(640.0, 413.0, 96.0, 28.0), (string v) =>
			{
				SetAntiCheatSettingInt("similarity-review", v);
			}, CairoFont.WhiteSmallText(), "ac-set-sim-review")
			.AddRichtext(SuiteLocalization.Get("gui.literal.b1a5954a48"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(818.0, 419.0, 84.0, 26.0))
			.AddNumberInput(ElementBounds.Fixed(912.0, 413.0, 96.0, 28.0), (string v) =>
			{
				SetAntiCheatSettingInt("similarity-high", v);
			}, CairoFont.WhiteSmallText(), "ac-set-sim-high");
		AddSettingsCard(composer, "gui.packetprotectiontitle", 28.0, 492.0, 1000.0, 172.0);
		composer.AddRichtext(SuiteLocalization.Get("gui.packetsettingsmovedhelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 548.0, 716.0, 66.0)).AddSuiteButton(SuiteLocalization.Get("gui.packetprotection"), () => SelectAntiCheatView("packet"), ElementBounds.Fixed(790.0, 544.0, 218.0, 32.0)).AddRichtext(SuiteLocalization.Get("gui.packetsettingssavehelp"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(48.0, 614.0, 944.0, 40.0))
			.AddRichtext(SuiteLocalization.Get("gui.clientsettingssavehelp"), CairoFont.WhiteSmallText().WithColor(new double[4] { 0.67, 0.74, 0.82, 1.0 }), ElementBounds.Fixed(34.0, 692.0, 980.0, 38.0));
	}

	private void AddBlockToggle(GuiComposer composer, string labelKey, string setting, string id, double x, double y, double width = 460.0)
	{
		composer.AddRichtext(SuiteLocalization.Get(labelKey), CairoFont.WhiteSmallText(), ElementBounds.Fixed(x, y + 5.0, width - 74.0, 28.0)).AddSwitch((bool value) =>
		{
			SetBlockLoggerBool(setting, value);
		}, ElementBounds.Fixed(x + width - 60.0, y, 60.0, 30.0), id);
	}

	private void AddBlockNumber(GuiComposer composer, string labelKey, string id, Action<string> onChange, ActionConsumable onSave, double x, double y, string? helpKey = null)
	{
		composer.AddRichtext(SuiteLocalization.Get(labelKey), CairoFont.WhiteSmallText(), ElementBounds.Fixed(x, y + 5.0, 260.0, 34.0)).AddNumberInput(ElementBounds.Fixed(x + 266.0, y, 96.0, 30.0), onChange, CairoFont.WhiteSmallText(), id).AddSuiteButton(SuiteLocalization.Get("gui.save"), onSave, ElementBounds.Fixed(x + 374.0, y, 86.0, 30.0));
		if (helpKey != null)
		{
			composer.AddHoverText(SuiteLocalization.Get(helpKey), CairoFont.WhiteSmallText(), 410, ElementBounds.Fixed(x + 266.0, y, 96.0, 30.0), "tip-" + id);
		}
	}
}
