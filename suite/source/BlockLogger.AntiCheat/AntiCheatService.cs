using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Admin;
using BlockLogger.Services;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;
using NewfiesAntiCheat.Shared.Packets;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatService : IDisposable
{
	private sealed class ProtectedAssetManifest
	{
		public string Digest { get; init; } = "";

		public int Count { get; init; }

		public List<ProtectedAssetChallengeDto> Details { get; init; } = new List<ProtectedAssetChallengeDto>();

		public List<string> Patterns { get; init; } = new List<string>();
	}

	private sealed class EvidenceTransferState
	{
		public string RequestId { get; set; } = "";

		public string PlayerUid { get; set; } = "";

		public string PlayerName { get; set; } = "";

		public string ModId { get; set; } = "";

		public string ModVersion { get; set; } = "";

		public string ExpectedFileName { get; set; } = "";

		public string ExpectedPackageSha256 { get; set; } = "";

		public string RequestedBy { get; set; } = "";

		public string DeclineAction { get; set; } = "TempBan";

		public int DeclineTempBanHours { get; set; } = 24;

		public long RequestedUtc { get; set; }

		public long ConsentRespondedUtc { get; set; }

		public bool ConsentGranted { get; set; }

		public long StartedUtc { get; set; }

		public string OriginalFileName { get; set; } = "";

		public long TotalBytes { get; set; }

		public string ReportedSha256 { get; set; } = "";

		public bool GeneratedArchive { get; set; }

		public int TotalChunks { get; set; }

		public int NextChunk { get; set; }

		public long ReceivedBytes { get; set; }

		public string TempPath { get; set; } = "";

		public string FinalPath { get; set; } = "";

		public EvidenceFileWriter? Writer { get; set; }

		public bool Finalizing { get; set; }
	}

	private const string ConfigFile = "NewfiesAntiCheat.json";

	private const string ServerSuiteModId = "newfiesserveressentialssuite";

	private readonly ICoreServerAPI api;

	private readonly Func<AdminService?> adminProvider;

	private readonly AntiCheatSessionManager sessions = new AntiCheatSessionManager();

	private readonly AntiCheatIncidentCorrelator incidentCorrelator = new AntiCheatIncidentCorrelator();

	private AntiCheatConfig config = new AntiCheatConfig();

	private AntiCheatEvaluator evaluator;

	private IServerNetworkChannel? channel;

	private PacketGuardService? packetGuard;

	private OreShieldService? oreShield;

	private AntiCheatHistoryStore? historyStore;

	private long watchdogId;

	private long correlationExpiryTickId;

	private bool started;

	private bool runtimeAvailable;

	private string runtimeAvailabilityReason = "Not started.";

	private string lastActivationError = "";

	private bool eventHooksRegistered;

	private HashSet<string> serverLoadedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly object canonicalModLock = new object();

	private IReadOnlyDictionary<string, AntiCheatCanonicalModFingerprint> serverCanonicalMods = new Dictionary<string, AntiCheatCanonicalModFingerprint>(StringComparer.OrdinalIgnoreCase);

	private AntiCheatClientCompanionBaseline? serverClientIntegrityBaseline;

	private string serverClientIntegrityBaselineStatus = "Not captured.";

	private Assembly? serverClientIntegrityRuntimeAssembly;

	private List<int> serverSelfAttestationMethodTokens = new List<int>();

	private List<int> serverSelfAttestationTypeTokens = new List<int>();

	private string serverSelfAttestationCatalogStatus = "Not captured.";

	private readonly object coreAssemblyLock = new object();

	private IReadOnlyDictionary<string, AntiCheatCoreAssemblyFingerprint> serverCoreAssemblies = new Dictionary<string, AntiCheatCoreAssemblyFingerprint>(StringComparer.OrdinalIgnoreCase);

	private readonly object protectedAssetManifestLock = new object();

	private ProtectedAssetManifest? protectedAssetManifest;

	private string protectedAssetManifestKey = "";

	private readonly Dictionary<string, long> packetGuardPlayerWarningUtc = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private const int MaxConcurrentEvidenceTransfers = 2;

	private const int EvidenceRetentionDays = 15;

	private const int EvidenceRetentionCleanupIntervalMs = 3600000;

	private const string ModDbVerificationCacheFile = "NewfiesAntiCheatModDbVerificationCache.json";

	private const string ApprovedClientModStoreFile = "NewfiesAntiCheatApprovedClientMods.json";

	private const int MaxConcurrentModDbVerifications = 2;

	private const long ModDbResolvedCacheMs = 604800000L;

	private const long ModDbNegativeCacheMs = 43200000L;

	private readonly object modDbVerificationLock = new object();

	private readonly Dictionary<string, AntiCheatModDbVerificationCacheEntry> modDbVerificationCache = new Dictionary<string, AntiCheatModDbVerificationCacheEntry>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, long> modDbVerificationCacheWeights = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private long modDbVerificationCacheEstimatedBytes;

	private readonly HashSet<string> modDbVerificationPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private bool modDbVerificationDeferred;

	private readonly SemaphoreSlim modDbVerificationSlots = new SemaphoreSlim(2, 2);

	private readonly CancellationTokenSource modDbVerificationCts = new CancellationTokenSource();

	private int modDbVerificationCacheDirtyCount;

	private long modDbVerificationCacheLastSavedUtc;

	private HashSet<string> serverModDbBaselineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private long serverModDbBaselineLastQueuedUtc;

	private readonly object approvedClientModLock = new object();

	private readonly List<AntiCheatApprovedClientModFingerprint> approvedClientModFingerprints = new List<AntiCheatApprovedClientModFingerprint>();

	private readonly object evidenceLock = new object();

	private readonly Dictionary<string, EvidenceTransferState> evidenceTransfers = new Dictionary<string, EvidenceTransferState>(StringComparer.OrdinalIgnoreCase);

	private readonly List<AntiCheatEvidencePackageWebSnapshot> evidenceRecords = new List<AntiCheatEvidencePackageWebSnapshot>();

	private long evidenceRetentionCleanupTickId;

	private const int MaxConnectionDiagnostics = 256;

	private readonly object connectionDiagnosticLock = new object();

	private readonly List<AntiCheatConnectionDiagnosticWebSnapshot> connectionDiagnostics = new List<AntiCheatConnectionDiagnosticWebSnapshot>();

	private static readonly string[] RiskFamilyOrder = new string[5] { "Movement", "Reach", "Mining", "PacketAbuse", "ClientIntegrity" };

	public AntiCheatConfig Config => config;

	public AntiCheatService(ICoreServerAPI api, Func<AdminService?> adminProvider)
	{
		this.api = api;
		this.adminProvider = adminProvider;
		evaluator = new AntiCheatEvaluator(config);
	}

	public object GetRetentionDiagnostics()
	{
		lock (modDbVerificationLock)
		{
			return new
			{
				sessions = sessions.Count,
				modDbEntries = modDbVerificationCache.Count,
				modDbEstimatedBytes = modDbVerificationCacheEstimatedBytes,
				modDbByteBudget = 67108864L,
				pendingVerifications = modDbVerificationPending.Count,
				cleanup = "Bounded cache eviction, expiring diagnostics and disconnected session cleanup; estimated bytes are not whole-heap attribution."
			};
		}
	}

	public OreShieldWebSnapshot GetOreShieldSnapshot()
	{
		return oreShield?.GetSnapshot() ?? BuildOreShieldFallbackSnapshot("OreShield inactive.");
	}

	public AntiCheatPlayerWebSnapshot? GetInvestigationSnapshot(string uid)
	{
		if (!sessions.TryGet(uid, out AntiCheatSession session))
		{
			return null;
		}
		AntiCheatPlayerWebSnapshot antiCheatPlayerWebSnapshot = ToWebPlayer(session, includeConnectionDiagnostics: false, includeDetails: false, includeMods: false, includeEvidence: true);
		antiCheatPlayerWebSnapshot.Findings = antiCheatPlayerWebSnapshot.Findings.OrderByDescending((AntiCheatFindingWebSnapshot f) => f.ObservedUtc).Take(32).ToList();
		return antiCheatPlayerWebSnapshot;
	}

	private object BuildDecisionSnapshot(AntiCheatSession session)
	{
		long now = NowMs();
		bool active = config.Enabled && runtimeAvailable;
		int num = session.ConfirmedSpeed.Count(now);
		int num2 = session.ConfirmedSpeed.Bonus(now, config.PacketGuardConfirmedSpeedRepeatRisk);
		var rules = config.ConsequenceRules.Where((AntiCheatConsequenceRule r) => r.Enabled).Select((AntiCheatConsequenceRule r) =>
		{
			(bool, string, ScanFindingDto) tuple = MatchConsequence(r, session);
			bool flag = tuple.Item3?.Scanner == "ModDbPolicy" && r.Action != "Log" && !config.ModDbAutomaticConsequencesEnabled;
			string name = r.Name;
			string action = r.Action;
			bool item = tuple.Item1;
			string state = ((!active) ? "Inactive: anti-cheat is disabled or its runtime is unavailable" : ((!tuple.Item1) ? "Not matched" : (flag ? "Suppressed: external Mod DB findings are advisory" : (session.AppliedConsequenceKeys.Contains(r.Id + ":" + tuple.Item2) ? "Already processed for this evidence" : "Matched; consequence processing determines the action"))));
			return new
			{
				Name = name,
				Action = action,
				matched = item,
				state = state
			};
		}).Take(64)
			.ToArray();
		bool enabled = active;
		bool packetGuardKickRepeatedConfirmedSpeed = config.PacketGuardKickRepeatedConfirmedSpeed;
		int confirmedSpeedReports = num;
		int repeatRiskBonus = num2;
		bool repeatedSpeedEligible = active && config.PacketGuardKickRepeatedConfirmedSpeed && num >= 3 && num2 >= 50;
		string speedExplanation;
		if (active)
		{
			if (config.PacketGuardKickRepeatedConfirmedSpeed)
			{
				if (num < 3)
				{
					speedExplanation = "Waiting for three Critical, server-confirmed unmounted speed observations in one minute.";
				}
				else
				{
					speedExplanation = ((num2 < 50) ? "Repeated-speed risk bonus is below 50; inspect the configured repeat risk." : "Repeated-speed conditions are met; inspect the latest consequence.");
				}
			}
			else
			{
				speedExplanation = "Repeated-speed kicking is disabled.";
			}
		}
		else
		{
			speedExplanation = "Anti-cheat enforcement is inactive. Check whether the module is enabled and its runtime is available.";
		}
		return new
		{
			enabled = enabled,
			repeatedSpeedEnabled = packetGuardKickRepeatedConfirmedSpeed,
			confirmedSpeedReports = confirmedSpeedReports,
			requiredReports = 3,
			repeatWindowSeconds = 60,
			repeatRiskBonus = repeatRiskBonus,
			requiredBonus = 50,
			repeatedSpeedEligible = repeatedSpeedEligible,
			speedExplanation = speedExplanation,
			rules = rules,
			note = "Mounted and review-only observations do not count toward the dedicated unmounted repeated-speed kick. Risk alone does not guarantee a configured disruptive consequence."
		};
	}

	public void Start()
	{
		if (started)
		{
			return;
		}
		started = true;
		LoadConfig();
		evaluator = new AntiCheatEvaluator(config);
		CaptureServerLoadedModIds();
		CaptureServerCoreAssemblyBaseline();
		CaptureServerClientIntegrityBaseline();
		StartCanonicalModFingerprintBuild();
		RegisterCommands();
		historyStore = new AntiCheatHistoryStore(api, config);
		historyStore.Start();
		LoadEvidenceMetadata();
		CleanupExpiredEvidencePackages(NowMs(), logSummary: true);
		if (evidenceRetentionCleanupTickId == 0)
		{
			evidenceRetentionCleanupTickId = api.Event.RegisterGameTickListener((float _) =>
			{
				CleanupExpiredEvidencePackages(NowMs(), logSummary: true);
			}, 3600000, 3600000);
		}
		LoadModDbVerificationCache();
		LoadApprovedClientModFingerprints();
		if (!config.Enabled)
		{
			runtimeAvailable = false;
			runtimeAvailabilityReason = "Disabled by configuration.";
			lastActivationError = "";
			api.Logger.Notification("[ServerEssentials/AntiCheat] Anti-Cheat add-on is disabled by {0}. It can be enabled from the web dashboard or Shift+L without restarting the Suite.", "NewfiesAntiCheat.json");
			return;
		}
		(bool, bool, string) serverCompanionStatus = GetServerCompanionStatus();
		if (!serverCompanionStatus.Item1)
		{
			runtimeAvailable = false;
			runtimeAvailabilityReason = "Required server companion 'newfiesanticheatclient' is not installed.";
			lastActivationError = "";
			api.Logger.Notification("[ServerEssentials/AntiCheat] Anti-Cheat is enabled in configuration, but the server companion '{0}' is not installed. The Suite will continue without live Anti-Cheat protection.", "newfiesanticheatclient");
		}
		else if (!serverCompanionStatus.Item2)
		{
			runtimeAvailable = false;
			runtimeAvailabilityReason = "Server companion version metadata is unavailable; the required client version cannot be determined safely.";
			lastActivationError = "";
			api.Logger.Warning("[ServerEssentials/AntiCheat] Anti-Cheat companion is installed, but its semantic version could not be read. Live Anti-Cheat protection was not started because the client-version baseline is unknown.");
		}
		else
		{
			QueueServerInstalledModDbBaseline();
			if (!ActivateRuntime(out string error))
			{
				api.Logger.Error("[ServerEssentials/AntiCheat] Anti-Cheat could not be activated. The rest of Server Essentials will continue. {0}", error);
			}
		}
	}

	private bool SupportsOreShieldClientSync(IServerPlayer player)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return false;
		}
		if (!sessions.TryGet(player.PlayerUID, out AntiCheatSession session) || !session.ClientInstalled)
		{
			return false;
		}
		if (session.ProtocolVersion != 13)
		{
			return false;
		}
		return (session.SupportedScanners & 0x10000) != 0;
	}

	private bool ActivateRuntime(out string error)
	{
		error = "";
		try
		{
			if (channel == null)
			{
				channel = api.Network.GetChannel("newfiesanticheat");
				if (channel == null)
				{
					channel = api.Network.RegisterChannel("newfiesanticheat").RegisterMessageType<ClientHelloPacket>().RegisterMessageType<ServerChallengePacket>()
						.RegisterMessageType<ClientScanReportPacket>()
						.RegisterMessageType<ServerAckPacket>()
						.RegisterMessageType<HeartbeatPacket>()
						.RegisterMessageType<ServerModEvidenceRequestPacket>()
						.RegisterMessageType<ClientModEvidenceConsentPacket>()
						.RegisterMessageType<ServerModEvidenceConsentAckPacket>()
						.RegisterMessageType<ClientModEvidenceStartPacket>()
						.RegisterMessageType<ClientModEvidenceChunkPacket>()
						.RegisterMessageType<ClientModEvidenceCompletePacket>();
				}
				channel.SetMessageHandler<ClientHelloPacket>(OnClientHello).SetMessageHandler<ClientScanReportPacket>(OnScanReport).SetMessageHandler<HeartbeatPacket>(OnHeartbeat)
					.SetMessageHandler<ClientModEvidenceConsentPacket>(OnEvidenceConsent)
					.SetMessageHandler<ClientModEvidenceStartPacket>(OnEvidenceStart)
					.SetMessageHandler<ClientModEvidenceChunkPacket>(OnEvidenceChunk)
					.SetMessageHandler<ClientModEvidenceCompletePacket>(OnEvidenceComplete);
			}
			if (!eventHooksRegistered)
			{
				api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
				api.Event.PlayerDisconnect += OnPlayerDisconnect;
				api.Event.CanPlaceOrBreakBlock += OnStartupGateCanPlaceOrBreakBlock;
				api.Event.CanUseBlock += OnStartupGateCanUseBlock;
				api.Event.OnPlayerInteractEntity += OnStartupGateInteractEntity;
				eventHooksRegistered = true;
			}
			if (watchdogId == 0)
			{
				watchdogId = api.Event.RegisterGameTickListener((float _) =>
				{
					Watchdog();
				}, 10000, 10000);
			}
			if (correlationExpiryTickId == 0)
			{
				correlationExpiryTickId = api.Event.RegisterGameTickListener((float _) =>
				{
					RefreshIncidentExpiries();
				}, 1000, 1000);
			}
			runtimeAvailable = true;
			runtimeAvailabilityReason = "Active on network channel 'newfiesanticheat'.";
			lastActivationError = "";
			LogServerCompanionStatus();
			if (packetGuard == null)
			{
				packetGuard = new PacketGuardService(api, config, ReportServerBehaviorFinding);
			}
			packetGuard.Start();
			if (oreShield == null)
			{
				oreShield = new OreShieldService(api, config, SupportsOreShieldClientSync);
			}
			bool flag = oreShield.Start(out string message);
			if (config.OreShieldEnabled && !flag)
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat] Ore Shield did not activate; other Anti-Cheat layers remain active. {0}", message);
			}
			foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
			{
				if (sessions.TryGet(item.PlayerUID, out AntiCheatSession session) && session.ClientInstalled && session.ProtocolVersion == 13)
				{
					session.ConnectedUtc = NowMs();
					session.ScanRetryCount = 0;
					SendChallenge(item, session);
				}
				else
				{
					OnPlayerNowPlaying(item);
				}
			}
			api.Logger.Notification("[ServerEssentials/AntiCheat] Anti-Cheat enabled: protocol {0} on channel '{1}', PacketGuard={2}, Ore Shield={3}, RequireClient={4}.", 13, "newfiesanticheat", config.PacketGuardEnabled, config.OreShieldEnabled, config.RequireClient);
			return true;
		}
		catch (Exception ex)
		{
			DeactivateRuntime();
			error = $"Channel '{"newfiesanticheat"}' activation failed: {ex.GetType().Name}: {ex.Message}";
			lastActivationError = error;
			runtimeAvailabilityReason = error;
			return false;
		}
	}

	private void DeactivateRuntime()
	{
		if (eventHooksRegistered)
		{
			api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
			api.Event.PlayerDisconnect -= OnPlayerDisconnect;
			api.Event.CanPlaceOrBreakBlock -= OnStartupGateCanPlaceOrBreakBlock;
			api.Event.CanUseBlock -= OnStartupGateCanUseBlock;
			api.Event.OnPlayerInteractEntity -= OnStartupGateInteractEntity;
			eventHooksRegistered = false;
		}
		if (watchdogId != 0)
		{
			api.Event.UnregisterGameTickListener(watchdogId);
			watchdogId = 0L;
		}
		if (correlationExpiryTickId != 0)
		{
			api.Event.UnregisterGameTickListener(correlationExpiryTickId);
			correlationExpiryTickId = 0L;
		}
		AbortAllEvidenceTransfers("Anti-Cheat runtime stopped during evidence transfer.");
		oreShield?.Dispose();
		oreShield = null;
		packetGuard?.Dispose();
		packetGuard = null;
		runtimeAvailable = false;
		PacketGuardTrustedState.ClearAll();
		incidentCorrelator.ClearAll();
	}

	private (bool Installed, bool VersionMatches, string Version) GetServerCompanionStatus()
	{
		try
		{
			Mod mod = api.ModLoader.Mods.FirstOrDefault((Mod x) => x?.Info != null && string.Equals(x.Info.ModID, "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase));
			string text = (mod?.Info?.Version ?? "").Trim();
			bool flag = mod != null;
			bool item = flag && !string.IsNullOrWhiteSpace(text);
			return (Installed: flag, VersionMatches: item, Version: text);
		}
		catch
		{
			return (Installed: false, VersionMatches: false, Version: "");
		}
	}

	private string GetRequiredClientVersion()
	{
		(bool, bool, string) serverCompanionStatus = GetServerCompanionStatus();
		if (serverCompanionStatus.Item1 && !string.IsNullOrWhiteSpace(serverCompanionStatus.Item3))
		{
			return serverCompanionStatus.Item3.Trim();
		}
		return "1.1.28";
	}

	private void CaptureServerClientIntegrityBaseline()
	{
		serverClientIntegrityBaseline = null;
		serverClientIntegrityRuntimeAssembly = null;
		serverSelfAttestationMethodTokens = new List<int>();
		serverSelfAttestationTypeTokens = new List<int>();
		serverSelfAttestationCatalogStatus = "Unavailable.";
		serverClientIntegrityBaselineStatus = "Client Integrity companion baseline unavailable.";
		try
		{
			Mod mod = api.ModLoader.Mods.FirstOrDefault((Mod x) => x?.Info != null && string.Equals(x.Info.ModID, "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase));
			if (mod == null)
			{
				serverClientIntegrityBaselineStatus = "Client Integrity companion is not installed on the server.";
				return;
			}
			string text = (mod.SourcePath ?? mod.FileName ?? "").Trim();
			if (text.Length == 0)
			{
				serverClientIntegrityBaselineStatus = "The server-installed Client Integrity companion has no readable source path.";
				return;
			}
			string value = AntiCheatLocalModHasher.HashPackage(text);
			string value2 = AntiCheatLocalModHasher.HashNormalizedContent(text);
			(long, int, long, List<AntiCheatCanonicalDllFingerprint>) packageStats = AntiCheatLocalModHasher.GetPackageStats(text);
			AntiCheatCanonicalDllFingerprint antiCheatCanonicalDllFingerprint = packageStats.Item4.FirstOrDefault((AntiCheatCanonicalDllFingerprint x) => string.Equals(x.FileName, "NewfiesAntiCheatClient.dll", StringComparison.OrdinalIgnoreCase)) ?? packageStats.Item4.FirstOrDefault((AntiCheatCanonicalDllFingerprint x) => x.FileName.Contains("NewfiesAntiCheatClient", StringComparison.OrdinalIgnoreCase)) ?? ((packageStats.Item4.Count == 1) ? packageStats.Item4[0] : null);
			Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly a) => string.Equals(a.GetName().Name, "NewfiesAntiCheatClient", StringComparison.OrdinalIgnoreCase));
			string text2 = "";
			string text3 = "";
			string text4 = "";
			long num = 0L;
			try
			{
				text2 = assembly?.Location ?? "";
				text3 = assembly?.ManifestModule.ModuleVersionId.ToString("D") ?? "";
				text4 = Path.GetFileName(text2);
				if (text2.Length > 0 && File.Exists(text2))
				{
					num = new FileInfo(text2).Length;
				}
			}
			catch
			{
			}
			string value3 = AntiCheatLocalModHasher.HashFileSha256(text2);
			string text5 = (IsSha256(antiCheatCanonicalDllFingerprint?.Sha256) ? NormalizeSha256(antiCheatCanonicalDllFingerprint.Sha256) : NormalizeSha256(value3));
			long num2 = ((antiCheatCanonicalDllFingerprint != null && antiCheatCanonicalDllFingerprint.FileSizeBytes > 0) ? antiCheatCanonicalDllFingerprint.FileSizeBytes : num);
			string text6;
			if (string.IsNullOrWhiteSpace(antiCheatCanonicalDllFingerprint?.FileName))
			{
				text6 = ((text4.Length > 0) ? text4 : "NewfiesAntiCheatClient.dll");
			}
			else
			{
				text6 = antiCheatCanonicalDllFingerprint.FileName;
			}
			if (!IsSha256(text5) || string.IsNullOrWhiteSpace(text3) || !IsSha256(value2))
			{
				serverClientIntegrityBaselineStatus = $"Server companion baseline incomplete (assemblySha={(IsSha256(text5) ? "ok" : "missing")}, mvid={((text3.Length > 0) ? "ok" : "missing")}, contentSha={(IsSha256(value2) ? "ok" : "missing")}).";
				api.Logger.Error("[ServerEssentials/AntiCheat] {0} Exact Client Integrity self-baseline admission will fail closed until the server companion is installed as a normal readable code mod.", serverClientIntegrityBaselineStatus);
				return;
			}
			SignedClientBuildManifestStatus signedClientBuildManifestStatus = SignedClientBuildManifestVerifier.Verify(text, config.ClientBuildManifestPublicKeyBase64, "newfiesanticheatclient", (mod.Info.Version ?? "").Trim(), 13, text6, text5, num2, text3);
			if (config.RequireSignedClientBuildManifest && (!signedClientBuildManifestStatus.Present || !signedClientBuildManifestStatus.ClaimsMatchBaseline || !signedClientBuildManifestStatus.SignatureVerified))
			{
				serverClientIntegrityBaselineStatus = "Signed Client Integrity manifest required but invalid: " + signedClientBuildManifestStatus.Message;
				api.Logger.Error("[ServerEssentials/AntiCheat] {0}", serverClientIntegrityBaselineStatus);
				return;
			}
			if (signedClientBuildManifestStatus.Present && !signedClientBuildManifestStatus.ClaimsMatchBaseline)
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat] {0}", signedClientBuildManifestStatus.Message);
			}
			serverClientIntegrityRuntimeAssembly = assembly;
			if (assembly != null)
			{
				serverSelfAttestationMethodTokens = SelfAttestationEngine.GetCandidateMethodTokens(assembly);
				serverSelfAttestationTypeTokens = SelfAttestationEngine.GetCandidateTypeTokens(assembly);
				serverSelfAttestationCatalogStatus = $"methods={serverSelfAttestationMethodTokens.Count};types={serverSelfAttestationTypeTokens.Count}";
			}
			else
			{
				serverSelfAttestationCatalogStatus = "The server Client Integrity assembly is not loaded for randomized attestation.";
			}
			serverClientIntegrityBaseline = new AntiCheatClientCompanionBaseline
			{
				ModId = "newfiesanticheatclient",
				Version = (mod.Info.Version ?? "").Trim(),
				SourceFileName = Path.GetFileName(text),
				PackageSha256 = NormalizeSha256(value),
				ContentSha256 = NormalizeSha256(value2),
				PackageBytes = packageStats.Item1,
				DllCount = packageStats.Item2,
				DllBytes = packageStats.Item3,
				AssemblyName = "NewfiesAntiCheatClient",
				AssemblyFileName = text6,
				AssemblySha256 = text5,
				AssemblyFileSizeBytes = num2,
				ModuleVersionId = text3,
				BuildManifestSha256 = signedClientBuildManifestStatus.ManifestSha256,
				BuildManifestSignatureSha256 = signedClientBuildManifestStatus.SignatureSha256,
				BuildId = signedClientBuildManifestStatus.BuildId,
				BuildManifestSignatureVerified = signedClientBuildManifestStatus.SignatureVerified
			};
			serverClientIntegrityBaselineStatus = $"Ready: v{serverClientIntegrityBaseline.Version}, DLL {text5.Substring(0, 12)}…, content {serverClientIntegrityBaseline.ContentSha256.Substring(0, 12)}…, attestation {serverSelfAttestationCatalogStatus}.";
			api.Logger.Notification("[ServerEssentials/AntiCheat] Client Integrity anti-bypass baseline ready: version={0}, dllSha256={1}, contentSha256={2}, mvid={3}, attestation={4}, signedManifest={5}.", serverClientIntegrityBaseline.Version, serverClientIntegrityBaseline.AssemblySha256, serverClientIntegrityBaseline.ContentSha256, serverClientIntegrityBaseline.ModuleVersionId, serverSelfAttestationCatalogStatus, serverClientIntegrityBaseline.BuildManifestSignatureVerified ? "verified" : (signedClientBuildManifestStatus.Present ? "present-unverified" : "absent"));
		}
		catch (Exception ex)
		{
			serverClientIntegrityBaseline = null;
			serverClientIntegrityBaselineStatus = "Client Integrity companion baseline error: " + ex.Message;
			api.Logger.Error("[ServerEssentials/AntiCheat] Could not capture authoritative Client Integrity baseline: {0}", ex.Message);
		}
	}

	private (bool Allowed, string Code, string Detail) ValidateClientIntegrityBaseline(IReadOnlyList<ClientModDto> mods, IReadOnlyList<ClientAssemblyDto> assemblies, ClientIntegrityDto? integrity)
	{
		AntiCheatClientCompanionBaseline baseline = serverClientIntegrityBaseline;
		if (baseline == null)
		{
			return (Allowed: false, Code: "CLIENT_BASELINE_UNAVAILABLE", Detail: serverClientIntegrityBaselineStatus);
		}
		if (integrity == null)
		{
			return (Allowed: false, Code: "CLIENT_SELF_INTEGRITY_MISSING", Detail: "The required Client Integrity self-fingerprint payload is missing.");
		}
		string selfId = "newfiesanticheatclient";
		List<ClientModDto> list = (mods ?? Array.Empty<ClientModDto>()).Where((ClientModDto m) => string.Equals((m.ModId ?? "").Trim(), selfId, StringComparison.OrdinalIgnoreCase)).ToList();
		if (list.Count != 1)
		{
			return (Allowed: false, Code: "CLIENT_SELF_MOD_INVENTORY_INVALID", Detail: $"Expected exactly one ModLoader record for {selfId}; received {list.Count}.");
		}
		ClientModDto clientModDto = list[0];
		if (!clientModDto.IsCodeMod)
		{
			return (Allowed: false, Code: "CLIENT_SELF_MOD_NOT_CODE", Detail: "Client Integrity is not reported as a code mod.");
		}
		if (!string.Equals((clientModDto.Version ?? "").Trim(), baseline.Version, StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_SELF_VERSION_MISMATCH", Detail: $"Client Integrity mod inventory reports v{clientModDto.Version}; server baseline is v{baseline.Version}.");
		}
		if (!string.Equals((integrity.AssemblyName ?? "").Trim(), baseline.AssemblyName, StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_IDENTITY_MISMATCH", Detail: $"Expected assembly {baseline.AssemblyName}; received {integrity.AssemblyName}.");
		}
		if (!IsSha256(integrity.AssemblySha256) || !IsSha256(integrity.ContentSha256))
		{
			return (Allowed: false, Code: "CLIENT_SELF_FINGERPRINT_INCOMPLETE", Detail: "Client Integrity did not provide valid DLL and normalized-content SHA-256 values.");
		}
		if (string.IsNullOrWhiteSpace(integrity.ModuleVersionId))
		{
			return (Allowed: false, Code: "CLIENT_SELF_MVID_MISSING", Detail: "Client Integrity did not provide a module version identifier.");
		}
		if (!string.Equals(NormalizeSha256(integrity.AssemblySha256), baseline.AssemblySha256, StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_DLL_HASH_MISMATCH", Detail: $"Loaded Client Integrity DLL SHA-256 does not match the server-installed baseline (detected {ShortHash(integrity.AssemblySha256)}, expected {ShortHash(baseline.AssemblySha256)}).");
		}
		if (!string.Equals((integrity.ModuleVersionId ?? "").Trim(), baseline.ModuleVersionId, StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_DLL_MVID_MISMATCH", Detail: "Loaded Client Integrity module identity does not match the server-installed DLL.");
		}
		if (baseline.AssemblyFileSizeBytes > 0 && integrity.AssemblyFileSizeBytes != baseline.AssemblyFileSizeBytes)
		{
			return (Allowed: false, Code: "CLIENT_DLL_SIZE_MISMATCH", Detail: $"Loaded Client Integrity DLL size is {integrity.AssemblyFileSizeBytes} bytes; expected {baseline.AssemblyFileSizeBytes}.");
		}
		if (!string.Equals(NormalizeSha256(integrity.ContentSha256), baseline.ContentSha256, StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_PACKAGE_CONTENT_MISMATCH", Detail: $"Client Integrity normalized package content does not match the server-installed companion (detected {ShortHash(integrity.ContentSha256)}, expected {ShortHash(baseline.ContentSha256)}).");
		}
		if (!IsSha256(clientModDto.ContentSha256))
		{
			return (Allowed: false, Code: "CLIENT_SELF_MOD_FINGERPRINT_UNAVAILABLE", Detail: "The Client Integrity ModLoader inventory could not provide a valid normalized-content fingerprint.");
		}
		if (!string.Equals(NormalizeSha256(clientModDto.ContentSha256), baseline.ContentSha256, StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_MOD_INVENTORY_CONTENT_MISMATCH", Detail: "The Client Integrity ModLoader inventory fingerprint does not match the server-installed companion.");
		}
		if (!string.Equals(NormalizeSha256(clientModDto.ContentSha256), NormalizeSha256(integrity.ContentSha256), StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_SELF_CROSSCHECK_FAILED", Detail: "Client Integrity self-fingerprint and ModLoader inventory content fingerprints disagree.");
		}
		if (IsSha256(clientModDto.Sha256) && IsSha256(integrity.PackageSha256) && !string.Equals(NormalizeSha256(clientModDto.Sha256), NormalizeSha256(integrity.PackageSha256), StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_SELF_PACKAGE_CROSSCHECK_FAILED", Detail: "Client Integrity self-fingerprint and ModLoader inventory package hashes disagree.");
		}
		if (clientModDto.PackageBytes > 0 && integrity.PackageBytes > 0 && clientModDto.PackageBytes != integrity.PackageBytes)
		{
			return (Allowed: false, Code: "CLIENT_SELF_PACKAGE_SIZE_CROSSCHECK_FAILED", Detail: "Client Integrity self-fingerprint and ModLoader inventory package sizes disagree.");
		}
		if (clientModDto.DllCount >= 0 && integrity.DllCount >= 0 && clientModDto.DllCount != integrity.DllCount)
		{
			return (Allowed: false, Code: "CLIENT_SELF_DLL_COUNT_CROSSCHECK_FAILED", Detail: "Client Integrity self-fingerprint and ModLoader inventory DLL counts disagree.");
		}
		if (clientModDto.DllBytes > 0 && integrity.DllBytes > 0 && clientModDto.DllBytes != integrity.DllBytes)
		{
			return (Allowed: false, Code: "CLIENT_SELF_DLL_BYTES_CROSSCHECK_FAILED", Detail: "Client Integrity self-fingerprint and ModLoader inventory DLL byte totals disagree.");
		}
		if (IsSha256(baseline.BuildManifestSha256))
		{
			if (!IsSha256(integrity.BuildManifestSha256))
			{
				return (Allowed: false, Code: "CLIENT_BUILD_MANIFEST_UNAVAILABLE", Detail: "The Client Integrity build manifest could not be read for this measurement.");
			}
			if (!string.Equals(NormalizeSha256(integrity.BuildManifestSha256), baseline.BuildManifestSha256, StringComparison.OrdinalIgnoreCase))
			{
				return (Allowed: false, Code: "CLIENT_BUILD_MANIFEST_MISMATCH", Detail: "Client Integrity build-manifest digest does not match the server-installed companion.");
			}
			if (string.IsNullOrWhiteSpace(integrity.BuildId))
			{
				return (Allowed: false, Code: "CLIENT_BUILD_ID_UNAVAILABLE", Detail: "The Client Integrity signed-build ID could not be read for this measurement.");
			}
			if (!string.Equals((integrity.BuildId ?? "").Trim(), baseline.BuildId, StringComparison.Ordinal))
			{
				return (Allowed: false, Code: "CLIENT_BUILD_ID_MISMATCH", Detail: $"Client Integrity build ID '{integrity.BuildId}' does not match server build '{baseline.BuildId}'.");
			}
			if (IsSha256(baseline.BuildManifestSignatureSha256))
			{
				if (!IsSha256(integrity.BuildManifestSignatureSha256))
				{
					return (Allowed: false, Code: "CLIENT_BUILD_SIGNATURE_UNAVAILABLE", Detail: "The Client Integrity build-signature file could not be read for this measurement.");
				}
				if (!string.Equals(NormalizeSha256(integrity.BuildManifestSignatureSha256), baseline.BuildManifestSignatureSha256, StringComparison.OrdinalIgnoreCase))
				{
					return (Allowed: false, Code: "CLIENT_BUILD_SIGNATURE_FILE_MISMATCH", Detail: "Client Integrity build-signature file does not match the server-installed companion.");
				}
			}
		}
		List<ClientAssemblyDto> list2 = (assemblies ?? Array.Empty<ClientAssemblyDto>()).Where((ClientAssemblyDto a) => string.Equals((a.AssemblyName ?? "").Trim(), baseline.AssemblyName, StringComparison.OrdinalIgnoreCase)).ToList();
		if (list2.Count == 0)
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_SCAN_UNAVAILABLE", Detail: "The independent assembly scanner did not return the loaded " + baseline.AssemblyName + " assembly for this measurement.");
		}
		if (list2.Count != 1)
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_DUPLICATE", Detail: $"Expected exactly one loaded {baseline.AssemblyName} assembly; received {list2.Count}.");
		}
		ClientAssemblyDto clientAssemblyDto = list2[0];
		if (!(clientAssemblyDto.AssociatedModIds ?? new List<string>()).Any((string x) => string.Equals((x ?? "").Trim(), selfId, StringComparison.OrdinalIgnoreCase)))
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_OWNER_MISMATCH", Detail: "The loaded Client Integrity assembly is not associated with the Client Integrity ModLoader record.");
		}
		if (!IsSha256(clientAssemblyDto.Sha256))
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_SCAN_FINGERPRINT_UNAVAILABLE", Detail: "The independent loaded-assembly scanner could not produce a valid Client Integrity DLL fingerprint.");
		}
		if (!string.Equals(NormalizeSha256(clientAssemblyDto.Sha256), baseline.AssemblySha256, StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_SCAN_HASH_MISMATCH", Detail: "Independent loaded-assembly scanning does not match the server-installed Client Integrity DLL hash.");
		}
		if (string.IsNullOrWhiteSpace(clientAssemblyDto.ModuleVersionId))
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_SCAN_MVID_UNAVAILABLE", Detail: "The independent loaded-assembly scanner could not produce the Client Integrity module identity.");
		}
		if (!string.Equals((clientAssemblyDto.ModuleVersionId ?? "").Trim(), baseline.ModuleVersionId, StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_SCAN_MVID_MISMATCH", Detail: "Independent loaded-assembly scanning does not match the server-installed Client Integrity module identity.");
		}
		if (baseline.AssemblyFileSizeBytes > 0 && clientAssemblyDto.FileSizeBytes <= 0)
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_SCAN_SIZE_UNAVAILABLE", Detail: "The independent loaded-assembly scanner could not read the Client Integrity DLL size.");
		}
		if (baseline.AssemblyFileSizeBytes > 0 && clientAssemblyDto.FileSizeBytes != baseline.AssemblyFileSizeBytes)
		{
			return (Allowed: false, Code: "CLIENT_SELF_ASSEMBLY_SCAN_SIZE_MISMATCH", Detail: "Independent loaded-assembly scanning does not match the server-installed Client Integrity DLL size.");
		}
		if (!string.Equals(NormalizeSha256(clientAssemblyDto.Sha256), NormalizeSha256(integrity.AssemblySha256), StringComparison.OrdinalIgnoreCase) || !string.Equals((clientAssemblyDto.ModuleVersionId ?? "").Trim(), (integrity.ModuleVersionId ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
		{
			return (Allowed: false, Code: "CLIENT_SELF_INDEPENDENT_CROSSCHECK_FAILED", Detail: "The independent assembly scanner and Client Integrity self-fingerprint disagree.");
		}
		return (Allowed: true, Code: "", Detail: "Exact server companion baseline matched.");
	}

	private static string ShortHash(string? value)
	{
		string text = NormalizeSha256(value);
		return (text.Length == 64) ? (text.Substring(0, 12) + "…") : "missing";
	}

	private static bool IsOperationalClientIntegrityAdmissionFailure(string code)
	{
		switch (code)
		{
		case "CLIENT_SELF_INTEGRITY_MISSING":
		case "CLIENT_SELF_FINGERPRINT_INCOMPLETE":
		case "CLIENT_SELF_MVID_MISSING":
		case "CLIENT_SELF_MOD_FINGERPRINT_UNAVAILABLE":
		case "CLIENT_BUILD_MANIFEST_UNAVAILABLE":
		case "CLIENT_BUILD_ID_UNAVAILABLE":
		case "CLIENT_BUILD_SIGNATURE_UNAVAILABLE":
		case "CLIENT_SELF_ASSEMBLY_SCAN_UNAVAILABLE":
		case "CLIENT_SELF_ASSEMBLY_SCAN_FINGERPRINT_UNAVAILABLE":
		case "CLIENT_SELF_ASSEMBLY_SCAN_MVID_UNAVAILABLE":
		case "CLIENT_SELF_ASSEMBLY_SCAN_SIZE_UNAVAILABLE":
			return true;
		default:
			return false;
		}
	}

	private void LogServerCompanionStatus()
	{
		(bool, bool, string) serverCompanionStatus = GetServerCompanionStatus();
		if (!serverCompanionStatus.Item1)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Server AntiCheat add-on '{0}' is not installed. Live Anti-Cheat will remain unavailable until the Newfies Client Integrity companion is installed on the server.", "newfiesanticheatclient");
		}
		else if (!serverCompanionStatus.Item2)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Server companion '{0}' is installed, but its semantic version metadata is unavailable. The Suite cannot derive the client-version requirement safely.", "newfiesanticheatclient");
		}
		else
		{
			api.Logger.Notification("[ServerEssentials/AntiCheat] Server companion detected: {0} v{1}. When version matching is required, clients must report this same server-installed version.", "newfiesanticheatclient", serverCompanionStatus.Item3);
		}
	}

	private void CaptureServerLoadedModIds()
	{
		try
		{
			serverLoadedModIds = (from x in api.ModLoader.Mods
				where x?.Info != null && !string.IsNullOrWhiteSpace(x.Info.ModID)
				select (x.Info.ModID ?? "").Trim() into x
				where x.Length > 0
				select x).ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception ex)
		{
			serverLoadedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			api.Logger.Warning("[ServerEssentials/AntiCheat] Could not capture the server mod ID set for client-only filtering: {0}", ex.Message);
		}
	}

	private void StartCanonicalModFingerprintBuild()
	{
		if (!config.CanonicalServerModIntegrityEnabled)
		{
			lock (canonicalModLock)
			{
				serverCanonicalMods = new Dictionary<string, AntiCheatCanonicalModFingerprint>(StringComparer.OrdinalIgnoreCase);
				return;
			}
		}
		List<(string ModId, string Version, string SourcePath)> inputs;
		try
		{
			inputs = (from x in api.ModLoader.Mods
				where x?.Info != null && !string.IsNullOrWhiteSpace(x.Info.ModID)
				where x.Info.RequiredOnClient || !string.Equals(x.Info.Side.ToString(), "Server", StringComparison.OrdinalIgnoreCase)
				select ((x.Info.ModID ?? "").Trim(), (x.Info.Version ?? "").Trim(), (x.SourcePath ?? x.FileName ?? "").Trim()) into x
				where x.Item1.Length > 0 && x.Item3.Length > 0 && !IsProtectedCoreModId(x.Item1) && !string.Equals(x.Item1, "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase)
				select x).DistinctBy(((string, string, string) x) => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Could not snapshot server mod paths for canonical integrity: {0}", ex.Message);
			return;
		}
		Task.Run(() =>
		{
			Dictionary<string, AntiCheatCanonicalModFingerprint> dictionary = new Dictionary<string, AntiCheatCanonicalModFingerprint>(StringComparer.OrdinalIgnoreCase);
			foreach (var item in inputs)
			{
				string text = AntiCheatLocalModHasher.HashPackage(item.SourcePath);
				string text2 = AntiCheatLocalModHasher.HashNormalizedContent(item.SourcePath);
				(long, int, long, List<AntiCheatCanonicalDllFingerprint>) packageStats = AntiCheatLocalModHasher.GetPackageStats(item.SourcePath);
				if (text.Length != 0 || text2.Length != 0)
				{
					dictionary[item.ModId] = new AntiCheatCanonicalModFingerprint
					{
						ModId = item.ModId,
						Version = item.Version,
						PackageSha256 = text,
						ContentSha256 = text2,
						SourceFileName = Path.GetFileName(item.SourcePath),
						PackageBytes = packageStats.Item1,
						DllCount = packageStats.Item2,
						DllBytes = packageStats.Item3,
						Assemblies = packageStats.Item4
					};
				}
			}
			lock (canonicalModLock)
			{
				serverCanonicalMods = dictionary;
			}
			api.Logger.Debug("[ServerEssentials/AntiCheat] Protected server-mod fingerprint baseline contains {0} mod(s).", dictionary.Count);
		});
	}

	private IReadOnlyDictionary<string, AntiCheatCanonicalModFingerprint> GetCanonicalModSnapshot()
	{
		lock (canonicalModLock)
		{
			return serverCanonicalMods;
		}
	}

	private bool TryGetServerModBaseline(ClientModDto mod, out AntiCheatCanonicalModFingerprint expected)
	{
		expected = null;
		if (!config.CanonicalServerModIntegrityEnabled || mod == null || string.IsNullOrWhiteSpace(mod.ModId))
		{
			return false;
		}
		IReadOnlyDictionary<string, AntiCheatCanonicalModFingerprint> canonicalModSnapshot = GetCanonicalModSnapshot();
		if (!canonicalModSnapshot.TryGetValue((mod.ModId ?? "").Trim(), out var value) || value == null)
		{
			return false;
		}
		expected = value;
		return true;
	}

	private static string CompareExactHash(string? client, string? expected)
	{
		string text = NormalizeSha256(client);
		string text2 = NormalizeSha256(expected);
		if (text.Length != 64 || text2.Length != 64)
		{
			return "Unavailable";
		}
		return string.Equals(text, text2, StringComparison.OrdinalIgnoreCase) ? "Match" : "Mismatch";
	}

	private static List<ClientAssemblyDto> AssembliesForMod(IReadOnlyList<ClientAssemblyDto>? assemblies, string? modId)
	{
		string id = NormalizeModId(modId);
		return (assemblies ?? Array.Empty<ClientAssemblyDto>()).Where((ClientAssemblyDto a) => (a.AssociatedModIds ?? new List<string>()).Any((string x) => string.Equals(NormalizeModId(x), id, StringComparison.OrdinalIgnoreCase))).ToList();
	}

	private static string CompareClientAssembliesToServerBaseline(IReadOnlyList<ClientAssemblyDto>? assemblies, string? modId, AntiCheatCanonicalModFingerprint expected)
	{
		List<AntiCheatCanonicalDllFingerprint> list = (expected.Assemblies ?? new List<AntiCheatCanonicalDllFingerprint>()).Where((AntiCheatCanonicalDllFingerprint x) => x != null && IsSha256(x.Sha256) && !string.IsNullOrWhiteSpace(x.FileName)).ToList();
		if (list.Count == 0)
		{
			return "NotApplicable";
		}
		List<ClientAssemblyDto> list2 = AssembliesForMod(assemblies, modId);
		if (list2.Count == 0)
		{
			return "Unavailable";
		}
		bool flag = false;
		bool flag2 = true;
		foreach (AntiCheatCanonicalDllFingerprint dll in list)
		{
			ClientAssemblyDto clientAssemblyDto = list2.FirstOrDefault((ClientAssemblyDto a) => string.Equals(a.FileName, dll.FileName, StringComparison.OrdinalIgnoreCase));
			if (clientAssemblyDto == null || !IsSha256(clientAssemblyDto.Sha256))
			{
				flag2 = false;
				continue;
			}
			int num = (from x in (clientAssemblyDto.AssociatedModIds ?? new List<string>()).Select(NormalizeModId)
				where x.Length > 0
				select x).Distinct(StringComparer.OrdinalIgnoreCase).Count();
			if (num != 1)
			{
				flag2 = false;
				continue;
			}
			flag = true;
			if (string.Equals(NormalizeSha256(clientAssemblyDto.Sha256), NormalizeSha256(dll.Sha256), StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			return "Mismatch";
		}
		return (flag & flag2) ? "Match" : "Unavailable";
	}

	private static string ComputeServerBaselineVerdict(ClientModDto mod, IReadOnlyList<ClientAssemblyDto>? assemblies, AntiCheatCanonicalModFingerprint expected, out string versionComparison, out string packageComparison, out string contentComparison, out string dllComparison, out string assemblyComparison)
	{
		versionComparison = (string.Equals((mod.Version ?? "").Trim(), (expected.Version ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ? "Match" : "Mismatch");
		packageComparison = CompareExactHash(mod.Sha256, expected.PackageSha256);
		contentComparison = CompareExactHash(mod.ContentSha256, expected.ContentSha256);
		bool flag = mod.PackageBytes > 0 || mod.DllCount > 0 || mod.DllBytes > 0 || expected.DllCount == 0;
		dllComparison = "";
		ref string reference = ref dllComparison;
		string text;
		if (flag)
		{
			text = ((mod.DllCount == expected.DllCount && mod.DllBytes == expected.DllBytes) ? "Match" : "Mismatch");
		}
		else
		{
			text = "Unavailable";
		}
		reference = text;
		assemblyComparison = CompareClientAssembliesToServerBaseline(assemblies, mod.ModId, expected);
		if (versionComparison == "Mismatch")
		{
			return "Mismatch";
		}
		if (contentComparison == "Match")
		{
			return "Match";
		}
		if (contentComparison == "Mismatch")
		{
			return "Mismatch";
		}
		if (packageComparison == "Match")
		{
			return "Match";
		}
		if (assemblyComparison == "Match")
		{
			return "Match";
		}
		if (assemblyComparison == "Mismatch")
		{
			return "Mismatch";
		}
		if (packageComparison == "Mismatch" || dllComparison == "Mismatch")
		{
			return "Diagnostic";
		}
		return "Unavailable";
	}

	private void InvalidateProtectedAssetManifest()
	{
		lock (protectedAssetManifestLock)
		{
			protectedAssetManifest = null;
			protectedAssetManifestKey = "";
		}
	}

	private ProtectedAssetManifest GetProtectedAssetManifest()
	{
		if (!config.RuntimeSurfaceInspectionEnabled || !config.ProtectedAssetInspectionEnabled)
		{
			return new ProtectedAssetManifest();
		}
		List<string> list = (from x in config.ProtectedAssetPatterns ?? new List<string>()
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
		if (list.Count == 0)
		{
			return new ProtectedAssetManifest();
		}
		string b = string.Join("|", list) + "#" + config.ProtectedAssetDetailedEntries;
		lock (protectedAssetManifestLock)
		{
			if (this.protectedAssetManifest != null && string.Equals(protectedAssetManifestKey, b, StringComparison.Ordinal))
			{
				return this.protectedAssetManifest;
			}
		}
		List<(string, string)> list2 = new List<(string, string)>();
		try
		{
			foreach (KeyValuePair<AssetLocation, IAsset> item in api.Assets.AllAssets.OrderBy((KeyValuePair<AssetLocation, IAsset> x) => x.Key.ToString(), StringComparer.OrdinalIgnoreCase))
			{
				string text = item.Key.ToString();
				if (!AssetMatchesAnyPattern(text, list))
				{
					continue;
				}
				byte[] array;
				try
				{
					array = item.Value?.Data ?? Array.Empty<byte>();
				}
				catch
				{
					array = Array.Empty<byte>();
				}
				if (array.Length != 0)
				{
					list2.Add((text, Convert.ToHexString(SHA256.HashData(array)).ToLowerInvariant()));
					if (list2.Count >= 4096)
					{
						break;
					}
				}
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Protected-asset baseline could not be built: {0}", ex.Message);
			return new ProtectedAssetManifest
			{
				Patterns = list
			};
		}
		int count = Math.Clamp(config.ProtectedAssetDetailedEntries, 0, 128);
		ProtectedAssetManifest protectedAssetManifest = ((list2.Count == 0) ? new ProtectedAssetManifest
		{
			Digest = "",
			Count = 0,
			Patterns = list,
			Details = new List<ProtectedAssetChallengeDto>()
		} : new ProtectedAssetManifest
		{
			Digest = ComputeProtectedAssetDigest(list2),
			Count = list2.Count,
			Patterns = list,
			Details = list2.Take(count).Select(((string Location, string Sha256) x) =>
			{
				ProtectedAssetChallengeDto protectedAssetChallengeDto = new ProtectedAssetChallengeDto();
				(protectedAssetChallengeDto.Location, protectedAssetChallengeDto.Sha256) = x;
				return protectedAssetChallengeDto;
			}).ToList()
		});
		if (protectedAssetManifest.Count == 0)
		{
			api.Logger.Debug("[ServerEssentials/AntiCheat] Protected-asset baseline is unavailable on this server runtime; protected-asset comparison will be skipped instead of producing false mismatches.");
		}
		lock (protectedAssetManifestLock)
		{
			this.protectedAssetManifest = protectedAssetManifest;
			protectedAssetManifestKey = b;
		}
		return protectedAssetManifest;
	}

	private static bool AssetMatchesAnyPattern(string location, IReadOnlyList<string> patterns)
	{
		string text = (location ?? "").Trim().ToLowerInvariant();
		foreach (string pattern in patterns)
		{
			string text2 = (pattern ?? "").Trim().ToLowerInvariant();
			if (text2.Length == 0)
			{
				continue;
			}
			if (text2.StartsWith("contains:", StringComparison.Ordinal))
			{
				string text3 = text2.Substring(9);
				if (text3.Length <= 0 || !text.Contains(text3, StringComparison.Ordinal))
				{
					continue;
				}
				return true;
			}
			if (text2.EndsWith('*'))
			{
				string text4 = text2;
				text2 = text4.Substring(0, text4.Length - 1);
			}
			if (text2.Length <= 0 || !text.StartsWith(text2, StringComparison.Ordinal))
			{
				continue;
			}
			return true;
		}
		return false;
	}

	private static string ComputeProtectedAssetDigest(IEnumerable<(string Location, string Sha256)> assets)
	{
		string s = string.Join("\n", from x in assets.OrderBy(((string Location, string Sha256) x) => x.Location, StringComparer.OrdinalIgnoreCase)
			select (x.Location ?? "").ToLowerInvariant() + "|" + x.Sha256);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
	}

	private List<string> GetSuspiciousRuntimeTerms()
	{
		if (!config.RuntimeSurfaceInspectionEnabled)
		{
			return new List<string>();
		}
		if (!config.SuspiciousHotkeyInspectionEnabled && !config.SuspiciousClientCommandInspectionEnabled)
		{
			return new List<string>();
		}
		return (from x in config.SuspiciousRuntimeTerms ?? new List<string>()
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim() into x
			where x.Length >= 3
			select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
	}

	private List<string> GetBlacklistedRuntimeTerms()
	{
		if (!config.RuntimeSurfaceInspectionEnabled)
		{
			return new List<string>();
		}
		HashSet<string> activeIds = (from x in config.ForbiddenModIds ?? new List<string>()
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
		List<string> list = new List<string>(activeIds);
		foreach (AntiCheatForbiddenFingerprint item in (config.ForbiddenFingerprints ?? new List<AntiCheatForbiddenFingerprint>()).Where((AntiCheatForbiddenFingerprint x) => x != null && activeIds.Contains((x.ModId ?? "").Trim())))
		{
			list.Add(item.ModId ?? "");
			list.AddRange(item.AssemblyNames ?? new List<string>());
			list.AddRange(from a in (item.Releases ?? new List<AntiCheatReleaseFingerprint>()).SelectMany((AntiCheatReleaseFingerprint r) => r.Assemblies ?? new List<AntiCheatReleaseAssemblyFingerprint>())
				select a.AssemblyName);
			list.AddRange((item.ModSystemTypeNames ?? new List<string>()).Select((string typeName) =>
			{
				string text = typeName ?? "";
				int num = text.IndexOf('.');
				return (num > 0) ? text.Substring(0, num) : text;
			}));
		}
		return (from x in list
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim() into x
			where x.Length >= 3
			select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
	}

	private void LoadConfig()
	{
		bool flag = false;
		AntiCheatConfig antiCheatConfig = ModConfigRecovery.LoadOrNull<AntiCheatConfig>(api, "NewfiesAntiCheat.json", "ServerEssentials/AntiCheat");
		if (antiCheatConfig == null)
		{
			antiCheatConfig = new AntiCheatConfig();
			flag = true;
		}
		config = antiCheatConfig;
		if (flag)
		{
			config.Enabled = GetServerCompanionStatus().Installed;
			config.SeedDefaultConsequenceRulesIfEmpty();
			config.SeedDefaultFingerprintsIfEmpty();
		}
		config.Clamp();
		SuiteConfigStorage.Store(api, config, "NewfiesAntiCheat.json");
	}

	private void CaptureServerCoreAssemblyBaseline()
	{
		if (!config.StartupCoreAssemblyInspectionEnabled)
		{
			lock (coreAssemblyLock)
			{
				serverCoreAssemblies = new Dictionary<string, AntiCheatCoreAssemblyFingerprint>(StringComparer.OrdinalIgnoreCase);
				return;
			}
		}
		HashSet<string> hashSet = new HashSet<string>(config.StartupCoreAssemblyNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, AntiCheatCoreAssemblyFingerprint> dictionary = new Dictionary<string, AntiCheatCoreAssemblyFingerprint>(StringComparer.OrdinalIgnoreCase);
		foreach (Assembly item in from a in AppDomain.CurrentDomain.GetAssemblies()
			where !a.IsDynamic
			select a)
		{
			string text;
			try
			{
				text = item.GetName().Name ?? "";
			}
			catch
			{
				continue;
			}
			if (text.Length == 0 || !hashSet.Contains(text))
			{
				continue;
			}
			string text2;
			try
			{
				text2 = item.Location ?? "";
			}
			catch
			{
				text2 = "";
			}
			if (text2.Length == 0 || !File.Exists(text2))
			{
				continue;
			}
			string text3 = ComputeFileSha256(text2);
			if (text3.Length == 64)
			{
				string version = "";
				string moduleVersionId = "";
				try
				{
					version = item.GetName().Version?.ToString() ?? "";
				}
				catch
				{
				}
				try
				{
					moduleVersionId = item.ManifestModule.ModuleVersionId.ToString("D");
				}
				catch
				{
				}
				dictionary[text] = new AntiCheatCoreAssemblyFingerprint
				{
					AssemblyName = text,
					Version = version,
					Sha256 = text3,
					ModuleVersionId = moduleVersionId
				};
			}
		}
		lock (coreAssemblyLock)
		{
			serverCoreAssemblies = dictionary;
		}
		api.Logger.Notification("[ServerEssentials/AntiCheat] Startup core baseline contains {0} shared assembly fingerprint(s): {1}.", dictionary.Count, string.Join(", ", dictionary.Keys.OrderBy((string x) => x)));
	}

	private IReadOnlyDictionary<string, AntiCheatCoreAssemblyFingerprint> GetCoreAssemblySnapshot()
	{
		lock (coreAssemblyLock)
		{
			return serverCoreAssemblies;
		}
	}

	private static string ComputeFileSha256(string path)
	{
		try
		{
			using FileStream inputStream = File.OpenRead(path);
			using SHA256 sHA = SHA256.Create();
			return Convert.ToHexString(sHA.ComputeHash(inputStream)).ToLowerInvariant();
		}
		catch
		{
			return "";
		}
	}

	private void RecordConnectionDiagnostic(AntiCheatSession session, string code, string stage, string action, string detail)
	{
		if (session == null)
		{
			return;
		}
		AntiCheatConnectionDiagnosticWebSnapshot item = new AntiCheatConnectionDiagnosticWebSnapshot
		{
			Utc = NowMs(),
			PlayerUid = session.PlayerUid,
			PlayerName = session.PlayerName,
			Code = (code ?? ""),
			Stage = (stage ?? ""),
			Action = (action ?? ""),
			Detail = (detail ?? ""),
			Status = session.Status.ToString(),
			ClientInstalled = session.ClientInstalled,
			ClientVersion = session.ClientVersion,
			ProtocolVersion = session.ProtocolVersion,
			LastHelloUtc = session.LastHelloUtc,
			LastHeartbeatUtc = session.LastHeartbeatUtc,
			StartupGateStartedUtc = session.StartupGateStartedUtc,
			ChallengeId = session.ChallengeId
		};
		lock (connectionDiagnosticLock)
		{
			connectionDiagnostics.Insert(0, item);
			if (connectionDiagnostics.Count > 256)
			{
				connectionDiagnostics.RemoveRange(256, connectionDiagnostics.Count - 256);
			}
		}
	}

	private List<AntiCheatConnectionDiagnosticWebSnapshot> GetConnectionDiagnosticsSnapshot()
	{
		lock (connectionDiagnosticLock)
		{
			return (from x in connectionDiagnostics.Take(64)
				select new AntiCheatConnectionDiagnosticWebSnapshot
				{
					Utc = x.Utc,
					PlayerUid = x.PlayerUid,
					PlayerName = x.PlayerName,
					Code = x.Code,
					Stage = x.Stage,
					Action = x.Action,
					Detail = x.Detail,
					Status = x.Status,
					ClientInstalled = x.ClientInstalled,
					ClientVersion = x.ClientVersion,
					ProtocolVersion = x.ProtocolVersion,
					LastHelloUtc = x.LastHelloUtc,
					LastHeartbeatUtc = x.LastHeartbeatUtc,
					StartupGateStartedUtc = x.StartupGateStartedUtc,
					ChallengeId = x.ChallengeId
				}).ToList();
		}
	}

	private AntiCheatModDbHealthWebSnapshot BuildModDbHealthSnapshot()
	{
		long now = NowMs();
		List<AntiCheatModDbVerificationCacheEntry> list;
		HashSet<string> pendingKeys;
		HashSet<string> baselineKeys;
		long serverBaselineLastQueuedUtc;
		lock (modDbVerificationLock)
		{
			list = modDbVerificationCache.Values.ToList();
			pendingKeys = modDbVerificationPending.ToHashSet(StringComparer.OrdinalIgnoreCase);
			baselineKeys = serverModDbBaselineKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
			serverBaselineLastQueuedUtc = serverModDbBaselineLastQueuedUtc;
		}
		List<AntiCheatModDbVerificationCacheEntry> source = list.Where((AntiCheatModDbVerificationCacheEntry x) => baselineKeys.Contains(ModDbVerificationKey(x.ModId, x.Version))).ToList();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		IReadOnlyDictionary<string, AntiCheatCanonicalModFingerprint> canonicalModSnapshot = GetCanonicalModSnapshot();
		foreach (AntiCheatCanonicalModFingerprint value in canonicalModSnapshot.Values)
		{
			string baselineKey = ModDbVerificationKey(value.ModId, value.Version);
			if (!baselineKeys.Contains(baselineKey))
			{
				continue;
			}
			AntiCheatModDbVerificationCacheEntry antiCheatModDbVerificationCacheEntry = source.FirstOrDefault((AntiCheatModDbVerificationCacheEntry x) => string.Equals(ModDbVerificationKey(x.ModId, x.Version), baselineKey, StringComparison.OrdinalIgnoreCase));
			if (antiCheatModDbVerificationCacheEntry == null || !IsFreshModDbVerification(antiCheatModDbVerificationCacheEntry, now) || !string.Equals(antiCheatModDbVerificationCacheEntry.Status, "Resolved", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			List<AntiCheatModDbReleaseFingerprintResolution> modDbReleaseCandidates = GetModDbReleaseCandidates(antiCheatModDbVerificationCacheEntry);
			if (modDbReleaseCandidates.Count != 0)
			{
				string text = CompareHashAny(value.ContentSha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.ContentSha256));
				string text2 = CompareHashAny(value.PackageSha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.PackageSha256));
				if (text == "Match" || text2 == "Match")
				{
					num++;
				}
				else if (text == "Mismatch")
				{
					num2++;
				}
				else if (text2 == "Mismatch")
				{
					num3++;
				}
			}
		}
		return new AntiCheatModDbHealthWebSnapshot
		{
			Enabled = config.ClientModDbVerificationEnabled,
			CacheEntries = list.Count,
			Pending = pendingKeys.Count,
			Resolved = list.Count((AntiCheatModDbVerificationCacheEntry x) => string.Equals(x.Status, "Resolved", StringComparison.OrdinalIgnoreCase)),
			NotFound = list.Count((AntiCheatModDbVerificationCacheEntry x) => string.Equals(x.Status, "NotFound", StringComparison.OrdinalIgnoreCase)),
			Unavailable = list.Count((AntiCheatModDbVerificationCacheEntry x) => string.Equals(x.Status, "Unavailable", StringComparison.OrdinalIgnoreCase)),
			Fresh = list.Count((AntiCheatModDbVerificationCacheEntry x) => IsFreshModDbVerification(x, now)),
			Stale = list.Count((AntiCheatModDbVerificationCacheEntry x) => !IsFreshModDbVerification(x, now)),
			LastCheckedUtc = ((list.Count == 0) ? 0 : list.Max((AntiCheatModDbVerificationCacheEntry x) => x.CheckedUtc)),
			LastResolvedUtc = (from x in list
				where string.Equals(x.Status, "Resolved", StringComparison.OrdinalIgnoreCase)
				select x.CheckedUtc).DefaultIfEmpty(0L).Max(),
			LastUnavailableUtc = (from x in list
				where string.Equals(x.Status, "Unavailable", StringComparison.OrdinalIgnoreCase)
				select x.CheckedUtc).DefaultIfEmpty(0L).Max(),
			MaxConcurrentRequests = 2,
			ServerBaselineTotal = baselineKeys.Count,
			ServerBaselineFresh = source.Count((AntiCheatModDbVerificationCacheEntry x) => IsFreshModDbVerification(x, now)),
			ServerBaselinePending = baselineKeys.Count((string x) => pendingKeys.Contains(x)),
			ServerBaselineResolved = source.Count((AntiCheatModDbVerificationCacheEntry x) => string.Equals(x.Status, "Resolved", StringComparison.OrdinalIgnoreCase)),
			ServerBaselineNotFound = source.Count((AntiCheatModDbVerificationCacheEntry x) => string.Equals(x.Status, "NotFound", StringComparison.OrdinalIgnoreCase)),
			ServerBaselineUnavailable = source.Count((AntiCheatModDbVerificationCacheEntry x) => string.Equals(x.Status, "Unavailable", StringComparison.OrdinalIgnoreCase)),
			ServerBaselineOfficialMatches = num,
			ServerBaselineCustomBuilds = num2,
			ServerBaselineDiagnosticDifferences = num3,
			ServerBaselineLastQueuedUtc = serverBaselineLastQueuedUtc
		};
	}

	private void BeginStartupGate(AntiCheatSession session, long now)
	{
		if (!config.StartupIntegrityGateEnabled || !config.RequireClient)
		{
			session.StartupGatePending = false;
			session.StartupGateVerdict = "Disabled";
			return;
		}
		session.StartupGatePending = true;
		session.StartupIntegrityVerified = false;
		session.StartupGateStartedUtc = now;
		session.StartupGateCompletedUtc = 0L;
		session.StartupGateVerdict = "Pending";
		session.StartupGateBlockedActions = 0;
		session.StartupGateLastNoticeUtc = 0L;
	}

	private void CompleteStartupGate(AntiCheatSession session, AntiCheatPlayerStatus scanStatus, long now)
	{
		if (!config.StartupIntegrityGateEnabled)
		{
			session.StartupGatePending = false;
			session.StartupIntegrityVerified = true;
			session.StartupGateCompletedUtc = now;
			session.StartupGateVerdict = "Disabled";
		}
		else
		{
			session.StartupIntegrityVerified = true;
			session.StartupGateCompletedUtc = now;
			session.StartupGateVerdict = scanStatus.ToString();
			bool startupIntegrityGateRequireClean = config.StartupIntegrityGateRequireClean;
			session.StartupGatePending = startupIntegrityGateRequireClean && scanStatus != AntiCheatPlayerStatus.Clean;
		}
	}

	private bool IsStartupGateBlocking(IServerPlayer? player, string action)
	{
		if (!config.StartupIntegrityGateEnabled || !config.StartupIntegrityGateBlockWorldInteractions || player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return false;
		}
		if (!sessions.TryGet(player.PlayerUID, out AntiCheatSession session) || !session.StartupGatePending)
		{
			return false;
		}
		session.StartupGateBlockedActions++;
		long num = NowMs();
		long num2 = (long)Math.Max(2, config.StartupIntegrityGateNoticeCooldownSeconds) * 1000L;
		if (num - session.StartupGateLastNoticeUtc >= num2)
		{
			session.StartupGateLastNoticeUtc = num;
			try
			{
				player.SendMessage(GlobalConstants.GeneralChatGroup, "Client integrity check in progress. World interactions unlock after the startup scan completes.", EnumChatType.Notification);
			}
			catch
			{
			}
		}
		return true;
	}

	private bool OnStartupGateCanPlaceOrBreakBlock(IServerPlayer player, BlockSelection blockSel, out string claimant)
	{
		claimant = "";
		return !IsStartupGateBlocking(player, "place-break");
	}

	private bool OnStartupGateCanUseBlock(IServerPlayer player, BlockSelection blockSel)
	{
		return !IsStartupGateBlocking(player, "use-block");
	}

	private void OnStartupGateInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
	{
		if (byPlayer is IServerPlayer player && IsStartupGateBlocking(player, "interact-entity"))
		{
			handling = EnumHandling.PreventDefault;
		}
	}

	private void OnPlayerNowPlaying(IServerPlayer player)
	{
		AntiCheatSession orCreate = sessions.GetOrCreate(player.PlayerUID, player.PlayerName);
		long num = NowMs();
		bool flag = orCreate.ClientInstalled && orCreate.LastHelloUtc > 0;
		orCreate.ConnectedUtc = num;
		orCreate.ClientMissingSinceUtc = 0L;
		orCreate.RequiredClientDisconnectScheduled = false;
		if (!flag)
		{
			BeginStartupGate(orCreate, num);
		}
		if (flag)
		{
			api.Logger.Debug("[ServerEssentials/AntiCheat] Preserving early client hello for {0} ({1}) across PlayerNowPlaying.", player.PlayerName, player.PlayerUID);
			PublishPlayer(orCreate);
			return;
		}
		orCreate.ClientInstalled = false;
		orCreate.ClientVersion = "";
		orCreate.ClientGameVersion = "";
		orCreate.ClientPlatform = "";
		orCreate.SupportedScanners = 0;
		orCreate.RequestedScanners = 0;
		orCreate.StartupIntegrityVerified = false;
		orCreate.StartupGateCompletedUtc = 0L;
		orCreate.StartupGateVerdict = (config.StartupIntegrityGateEnabled ? "Pending" : "Disabled");
		orCreate.StartupGateBlockedActions = 0;
		orCreate.StartupGateLastNoticeUtc = 0L;
		orCreate.Status = AntiCheatPlayerStatus.AwaitingClient;
		orCreate.RiskScore = 0;
		orCreate.LastHelloUtc = 0L;
		orCreate.LastHeartbeatUtc = 0L;
		orCreate.LastScanUtc = 0L;
		orCreate.NextScanUtc = 0L;
		orCreate.Findings.Clear();
		orCreate.LastMods.Clear();
		orCreate.LastAssemblies.Clear();
		orCreate.LastIntegrity = null;
		orCreate.LastRuntime = null;
		orCreate.LastHarmony = null;
		orCreate.LastScannerMetrics.Clear();
		orCreate.LastRuntimeDigest = "";
		orCreate.LastHarmonyPatchDigest = "";
		orCreate.LastSelfIntegrityDigest = "";
		orCreate.LastHeartbeatSelfIntegrityDigest = "";
		orCreate.LastHeartbeatRuntimeDigest = "";
		orCreate.LastHeartbeatRuntimeGeneration = 0;
		orCreate.LastHeartbeatAssemblyCount = 0;
		orCreate.LastHeartbeatDynamicAssemblyCount = 0;
		orCreate.LastHeartbeatHarmonyPatchDigest = "";
		orCreate.LastHeartbeatHarmonyPatchCount = 0;
		orCreate.LastHeartbeatSuspiciousHarmonyOwnerCount = 0;
		orCreate.LastHeartbeatSequence = 0L;
		orCreate.HeartbeatBindingRejectCount = 0;
		orCreate.LastHeartbeatBindingRejectUtc = 0L;
		orCreate.NextRuntimeChangeChallengeUtc = 0L;
		orCreate.ScanRetryCount = 0;
		orCreate.AppliedConsequenceKeys.Clear();
		orCreate.LastConsequence = "";
		orCreate.LastConsequenceUtc = 0L;
		orCreate.AdminSuiteVersionWarningKey = "";
		PublishPlayer(orCreate);
	}

	private static string ModDbVerificationKey(string? modId, string? version)
	{
		return NormalizeModId(modId) + "@" + (version ?? "").Trim().ToLowerInvariant();
	}

	private static bool ShouldVerifyModWithModDb(ClientModDto mod)
	{
		if (mod == null)
		{
			return false;
		}
		string text = NormalizeModId(mod.ModId);
		if (text.Length == 0 || string.IsNullOrWhiteSpace(mod.Version))
		{
			return false;
		}
		if (IsProtectedCoreModId(text))
		{
			return false;
		}
		if (string.Equals(text, "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return true;
	}

	private void LoadApprovedClientModFingerprints()
	{
		try
		{
			AntiCheatApprovedClientModStore antiCheatApprovedClientModStore = SuiteConfigStorage.Load<AntiCheatApprovedClientModStore>(api, "NewfiesAntiCheatApprovedClientMods.json") ?? new AntiCheatApprovedClientModStore();
			lock (approvedClientModLock)
			{
				approvedClientModFingerprints.Clear();
				foreach (AntiCheatApprovedClientModFingerprint item in antiCheatApprovedClientModStore.Entries ?? new List<AntiCheatApprovedClientModFingerprint>())
				{
					if (item != null)
					{
						string text = NormalizeModId(item.ModId);
						string text2 = (item.Version ?? "").Trim();
						string text3 = NormalizeSha256(item.ContentSha256);
						if (text.Length != 0 && text2.Length != 0 && text3.Length == 64)
						{
							item.Id = (string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id.Trim());
							item.ModId = text;
							item.Version = text2;
							item.ContentSha256 = text3;
							item.PackageSha256 = NormalizeSha256(item.PackageSha256);
							item.Label = (item.Label ?? "").Trim();
							item.Notes = (item.Notes ?? "").Trim();
							item.ApprovedBy = (item.ApprovedBy ?? "").Trim();
							approvedClientModFingerprints.Add(item);
						}
					}
				}
			}
			api.Logger.Notification("[ServerEssentials/AntiCheat] Loaded {0} approved custom client-mod fingerprint(s).", approvedClientModFingerprints.Count);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Could not load approved client-mod fingerprints: {0}", ex.Message);
		}
	}

	private bool SaveApprovedClientModFingerprints(out string error)
	{
		error = "";
		try
		{
			List<AntiCheatApprovedClientModFingerprint> entries;
			lock (approvedClientModLock)
			{
				entries = approvedClientModFingerprints.OrderBy((AntiCheatApprovedClientModFingerprint x) => x.ModId, StringComparer.OrdinalIgnoreCase).ThenBy((AntiCheatApprovedClientModFingerprint x) => x.Version, StringComparer.OrdinalIgnoreCase).ThenBy((AntiCheatApprovedClientModFingerprint x) => x.Label, StringComparer.OrdinalIgnoreCase)
					.ToList();
			}
			SuiteConfigStorage.Store(api, new AntiCheatApprovedClientModStore
			{
				Entries = entries
			}, "NewfiesAntiCheatApprovedClientMods.json");
			return true;
		}
		catch (Exception ex)
		{
			error = "Could not save approved client-mod fingerprints: " + ex.Message;
			return false;
		}
	}

	private AntiCheatApprovedClientModFingerprint? FindApprovedClientModFingerprint(ClientModDto mod)
	{
		if (mod == null)
		{
			return null;
		}
		string id = NormalizeModId(mod.ModId);
		string version = (mod.Version ?? "").Trim();
		string content = NormalizeSha256(mod.ContentSha256);
		if (id.Length == 0 || version.Length == 0 || content.Length != 64)
		{
			return null;
		}
		lock (approvedClientModLock)
		{
			return approvedClientModFingerprints.FirstOrDefault((AntiCheatApprovedClientModFingerprint x) => string.Equals(x.ModId, id, StringComparison.OrdinalIgnoreCase) && VersionEquivalentForApproval(x.Version, version) && string.Equals(NormalizeSha256(x.ContentSha256), content, StringComparison.OrdinalIgnoreCase));
		}
	}

	private static bool VersionEquivalentForApproval(string? left, string? right)
	{
		return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
		static string Normalize(string? value)
		{
			string text = (value ?? "").Trim();
			if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase) && text.Length > 1 && char.IsDigit(text[1]))
			{
				text = text.Substring(1);
			}
			return text.Trim();
		}
	}

	public (bool Success, string Message, AntiCheatClientModApprovalRequest? Approval) InspectClientModPackageForApproval(string filePath, string originalFileName, string label, string notes)
	{
		if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
		{
			return (Success: false, Message: "Uploaded mod ZIP could not be found.", Approval: null);
		}
		if (!Path.GetExtension(originalFileName ?? filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
		{
			return (Success: false, Message: "Only Vintage Story mod ZIP packages can be fingerprinted from the WebPanel.", Approval: null);
		}
		try
		{
			string text = "";
			string text2 = "";
			string text3 = "";
			using (FileStream stream = File.OpenRead(filePath))
			{
				using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
				List<ZipArchiveEntry> list = zipArchive.Entries.Where((ZipArchiveEntry e) => !string.IsNullOrEmpty(e.Name) && string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), "modinfo.json", StringComparison.OrdinalIgnoreCase)).ToList();
				if (list.Count != 1)
				{
					return (Success: false, Message: (list.Count == 0) ? "The ZIP does not contain a root modinfo.json." : "The ZIP contains more than one root modinfo.json.", Approval: null);
				}
				ZipArchiveEntry zipArchiveEntry = list[0];
				if (zipArchiveEntry.Length < 2 || zipArchiveEntry.Length > 1048576)
				{
					return (Success: false, Message: "The uploaded modinfo.json is invalid or unexpectedly large.", Approval: null);
				}
				using Stream utf8Json = zipArchiveEntry.Open();
				using JsonDocument jsonDocument = JsonDocument.Parse(utf8Json);
				if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object)
				{
					return (Success: false, Message: "The uploaded modinfo.json must contain a JSON object.", Approval: null);
				}
				text = NormalizeModId(ReadString(jsonDocument.RootElement, "modid"));
				text2 = ReadString(jsonDocument.RootElement, "version");
				text3 = ReadString(jsonDocument.RootElement, "name");
			}
			if (text.Length == 0)
			{
				return (Success: false, Message: "The uploaded modinfo.json does not contain a valid ModID.", Approval: null);
			}
			if (text2.Length == 0 || text2.Length > 128)
			{
				return (Success: false, Message: "The uploaded modinfo.json does not contain a valid exact version.", Approval: null);
			}
			if (IsProtectedCoreModId(text) || string.Equals(text, "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase))
			{
				return (Success: false, Message: "Core game modules and Client Integrity cannot be approved through the custom fingerprint uploader.", Approval: null);
			}
			string text4 = AntiCheatLocalModHasher.HashFileSha256(filePath);
			string text5 = AntiCheatLocalModHasher.HashNormalizedContent(filePath);
			(long, int, long, List<AntiCheatCanonicalDllFingerprint>) packageStats = AntiCheatLocalModHasher.GetPackageStats(filePath);
			if (text4.Length != 64 || text5.Length != 64)
			{
				return (Success: false, Message: "The uploaded ZIP could not be hashed safely. The package may be malformed or exceed Anti-Cheat hashing limits.", Approval: null);
			}
			string text6 = (label ?? "").Trim();
			if (text6.Length == 0)
			{
				text6 = (string.IsNullOrWhiteSpace(text3) ? (text + " " + text2 + " upload") : (text3 + " " + text2));
			}
			AntiCheatClientModApprovalRequest item = new AntiCheatClientModApprovalRequest
			{
				Action = "approve",
				ModId = text,
				Version = text2,
				ContentSha256 = text5,
				PackageSha256 = text4,
				Label = text6,
				Notes = (notes ?? "").Trim()
			};
			return (Success: true, Message: $"Validated {text} {text2}: {packageStats.Item2} DLL(s), {packageStats.Item1:N0} package bytes. Ready for exact fingerprint approval.", Approval: item);
		}
		catch (InvalidDataException ex)
		{
			return (Success: false, Message: "The uploaded file is not a readable ZIP package: " + ex.Message, Approval: null);
		}
		catch (JsonException ex2)
		{
			return (Success: false, Message: "The uploaded modinfo.json is not valid JSON: " + ex2.Message, Approval: null);
		}
		catch (Exception ex3)
		{
			return (Success: false, Message: "Could not inspect the uploaded mod ZIP: " + ex3.Message, Approval: null);
		}
		static string ReadString(JsonElement root, string name)
		{
			foreach (JsonProperty item2 in root.EnumerateObject())
			{
				if (item2.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
				{
					return (item2.Value.ValueKind == JsonValueKind.String) ? (item2.Value.GetString() ?? "").Trim() : item2.Value.ToString().Trim();
				}
			}
			return "";
		}
	}

	public (bool Success, string Message) UpdateApprovedClientModFingerprint(AntiCheatClientModApprovalRequest request, string actor)
	{
		if (request == null)
		{
			return (Success: false, Message: "Approval request is required.");
		}
		string text = (request.Action ?? "approve").Trim().ToLowerInvariant();
		if ((text == "remove" || text == "delete") ? true : false)
		{
			string entryId = (request.Id ?? "").Trim();
			if (entryId.Length == 0)
			{
				return (Success: false, Message: "Approval ID is required for removal.");
			}
			AntiCheatApprovedClientModFingerprint antiCheatApprovedClientModFingerprint = null;
			lock (approvedClientModLock)
			{
				antiCheatApprovedClientModFingerprint = approvedClientModFingerprints.FirstOrDefault((AntiCheatApprovedClientModFingerprint x) => string.Equals(x.Id, entryId, StringComparison.OrdinalIgnoreCase));
				if (antiCheatApprovedClientModFingerprint != null)
				{
					approvedClientModFingerprints.Remove(antiCheatApprovedClientModFingerprint);
				}
			}
			if (antiCheatApprovedClientModFingerprint == null)
			{
				return (Success: false, Message: "Approved fingerprint was not found.");
			}
			if (!SaveApprovedClientModFingerprints(out string error))
			{
				lock (approvedClientModLock)
				{
					approvedClientModFingerprints.Add(antiCheatApprovedClientModFingerprint);
				}
				return (Success: false, Message: error);
			}
			RefreshAllSessionsAfterApprovalChange();
			return (Success: true, Message: $"Removed approved custom fingerprint for {antiCheatApprovedClientModFingerprint.ModId} {antiCheatApprovedClientModFingerprint.Version}.");
		}
		if (text != "approve" && text != "save")
		{
			return (Success: false, Message: "Unknown approval action.");
		}
		string modId = NormalizeModId(request.ModId);
		string version = (request.Version ?? "").Trim();
		string content = NormalizeSha256(request.ContentSha256);
		string packageSha = NormalizeSha256(request.PackageSha256);
		if (modId.Length == 0)
		{
			return (Success: false, Message: "A valid ModID is required.");
		}
		if (IsProtectedCoreModId(modId) || string.Equals(modId, "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase))
		{
			return (Success: false, Message: "Core game modules and Client Integrity cannot be added to the custom-fingerprint approval store.");
		}
		if (version.Length == 0 || version.Length > 128)
		{
			return (Success: false, Message: "The exact mod version is required and must be 128 characters or fewer.");
		}
		if (content.Length != 64)
		{
			return (Success: false, Message: "A normalized content SHA-256 is required. Package hash alone is not sufficient for a custom approval.");
		}
		long num = NowMs();
		lock (approvedClientModLock)
		{
			AntiCheatApprovedClientModFingerprint antiCheatApprovedClientModFingerprint2 = approvedClientModFingerprints.FirstOrDefault((AntiCheatApprovedClientModFingerprint x) => string.Equals(x.ModId, modId, StringComparison.OrdinalIgnoreCase) && VersionEquivalentForApproval(x.Version, version) && string.Equals(NormalizeSha256(x.ContentSha256), content, StringComparison.OrdinalIgnoreCase)) ?? new AntiCheatApprovedClientModFingerprint
			{
				Id = Guid.NewGuid().ToString("N"),
				ModId = modId,
				Version = version,
				ContentSha256 = content,
				CreatedUtc = num
			};
			bool flag = !approvedClientModFingerprints.Contains(antiCheatApprovedClientModFingerprint2);
			antiCheatApprovedClientModFingerprint2.PackageSha256 = packageSha;
			string text2 = (request.Label ?? "").Trim();
			string text3 = (request.Notes ?? "").Trim();
			string text4 = ((text2.Length == 0) ? (modId + " " + version + " custom") : text2);
			antiCheatApprovedClientModFingerprint2.Label = ((text4.Length > 160) ? text4.Substring(0, 160) : text4);
			antiCheatApprovedClientModFingerprint2.Notes = ((text3.Length > 1200) ? text3.Substring(0, 1200) : text3);
			antiCheatApprovedClientModFingerprint2.ApprovedBy = (actor ?? "").Trim();
			antiCheatApprovedClientModFingerprint2.UpdatedUtc = num;
			if (antiCheatApprovedClientModFingerprint2.CreatedUtc <= 0)
			{
				antiCheatApprovedClientModFingerprint2.CreatedUtc = num;
			}
			if (flag)
			{
				approvedClientModFingerprints.Add(antiCheatApprovedClientModFingerprint2);
			}
		}
		if (!SaveApprovedClientModFingerprints(out string error2))
		{
			LoadApprovedClientModFingerprints();
			return (Success: false, Message: error2);
		}
		RefreshAllSessionsAfterApprovalChange();
		return (Success: true, Message: $"Approved exact custom fingerprint for {modId} {version}. Only this normalized content SHA-256 is trusted.");
	}

	private void RefreshAllSessionsAfterApprovalChange()
	{
		foreach (AntiCheatSession item in sessions.Snapshot())
		{
			if (item.LastScanUtc > 0)
			{
				RefreshSessionAfterModDbVerification(item.PlayerUid);
			}
		}
	}

	private List<AntiCheatApprovedClientModFingerprintWebSnapshot> GetApprovedClientModFingerprintSnapshot()
	{
		lock (approvedClientModLock)
		{
			return (from x in approvedClientModFingerprints.OrderBy((AntiCheatApprovedClientModFingerprint x) => x.ModId, StringComparer.OrdinalIgnoreCase).ThenBy((AntiCheatApprovedClientModFingerprint x) => x.Version, StringComparer.OrdinalIgnoreCase)
				select new AntiCheatApprovedClientModFingerprintWebSnapshot
				{
					Id = x.Id,
					ModId = x.ModId,
					Version = x.Version,
					ContentSha256 = x.ContentSha256,
					PackageSha256 = x.PackageSha256,
					Label = x.Label,
					Notes = x.Notes,
					ApprovedBy = x.ApprovedBy,
					CreatedUtc = x.CreatedUtc,
					UpdatedUtc = x.UpdatedUtc
				}).ToList();
		}
	}

	private void LoadModDbVerificationCache()
	{
		try
		{
			AntiCheatModDbVerificationCacheStore antiCheatModDbVerificationCacheStore = SuiteConfigStorage.Load<AntiCheatModDbVerificationCacheStore>(api, "NewfiesAntiCheatModDbVerificationCache.json");
			if (antiCheatModDbVerificationCacheStore?.Entries == null)
			{
				return;
			}
			bool flag = antiCheatModDbVerificationCacheStore.SchemaVersion < 2;
			bool flag2 = antiCheatModDbVerificationCacheStore.SchemaVersion < 3;
			long cutoff = NowMs() - 2592000000u;
			int num = 0;
			lock (modDbVerificationLock)
			{
				modDbVerificationCache.Clear();
				modDbVerificationCacheWeights.Clear();
				modDbVerificationCacheEstimatedBytes = 0L;
				foreach (AntiCheatModDbVerificationCacheEntry item in from x in antiCheatModDbVerificationCacheStore.Entries
					where x != null && x.CheckedUtc >= cutoff
					orderby x.CheckedUtc
					select x)
				{
					AntiCheatModDbVerificationCacheEntry antiCheatModDbVerificationCacheEntry = item;
					if (antiCheatModDbVerificationCacheEntry.Releases == null)
					{
						List<AntiCheatModDbReleaseFingerprintResolution> list = (antiCheatModDbVerificationCacheEntry.Releases = new List<AntiCheatModDbReleaseFingerprintResolution>());
					}
					if (item.Releases.Count == 0 && item.Release != null)
					{
						item.Releases.Add(item.Release);
					}
					if (item.Release == null && item.Releases.Count > 0)
					{
						item.Release = item.Releases[0];
					}
					if ((flag | flag2) && string.Equals(item.Status, "Resolved", StringComparison.OrdinalIgnoreCase))
					{
						item.CheckedUtc = 0L;
						num++;
					}
					string text = ModDbVerificationKey(item.ModId, item.Version);
					if (!text.StartsWith("@", StringComparison.Ordinal) && text.Length > 2)
					{
						StoreModDbVerificationEntryLocked(text, item);
					}
				}
			}
			api.Logger.Debug("[ServerEssentials/AntiCheat] Loaded {0} cached exact Mod DB verification result(s).", modDbVerificationCache.Count);
			if (num > 0)
			{
				api.Logger.Notification("[ServerEssentials/AntiCheat] Marked {0} legacy ModDB cache result(s) stale so exact-version fingerprints and file manifests can be rebuilt safely.", num);
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Could not load Mod DB verification cache: {0}", ex.Message);
		}
	}

	private void SaveModDbVerificationCache(bool force = false)
	{
		long num = NowMs();
		lock (modDbVerificationLock)
		{
			if (!force && modDbVerificationCacheDirtyCount < 10 && num - modDbVerificationCacheLastSavedUtc < 5000)
			{
				return;
			}
		}
		try
		{
			List<AntiCheatModDbVerificationCacheEntry> entries;
			lock (modDbVerificationLock)
			{
				entries = modDbVerificationCache.Values.OrderByDescending((AntiCheatModDbVerificationCacheEntry x) => x.CheckedUtc).Take(1024).ToList();
			}
			SuiteConfigStorage.Store(api, new AntiCheatModDbVerificationCacheStore
			{
				Entries = entries
			}, "NewfiesAntiCheatModDbVerificationCache.json");
			lock (modDbVerificationLock)
			{
				modDbVerificationCacheDirtyCount = 0;
				modDbVerificationCacheLastSavedUtc = num;
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Could not save Mod DB verification cache: {0}", ex.Message);
		}
	}

	private void StoreModDbVerificationEntryLocked(string key, AntiCheatModDbVerificationCacheEntry entry)
	{
		long num = ModDbCacheMemoryBudget.Estimate(entry);
		if (num > 67108864)
		{
			return;
		}
		if (modDbVerificationCacheWeights.Remove(key, out var value))
		{
			modDbVerificationCacheEstimatedBytes -= value;
		}
		modDbVerificationCache.Remove(key);
		while (modDbVerificationCache.Count >= 1024 || modDbVerificationCacheEstimatedBytes + num > 67108864)
		{
			KeyValuePair<string, AntiCheatModDbVerificationCacheEntry> keyValuePair = (from row in modDbVerificationCache
				orderby serverModDbBaselineKeys.Contains(row.Key), row.Value.CheckedUtc
				select row).First();
			modDbVerificationCache.Remove(keyValuePair.Key);
			if (modDbVerificationCacheWeights.Remove(keyValuePair.Key, out var value2))
			{
				modDbVerificationCacheEstimatedBytes -= value2;
			}
		}
		modDbVerificationCache[key] = entry;
		modDbVerificationCacheWeights[key] = num;
		modDbVerificationCacheEstimatedBytes += num;
	}

	private AntiCheatModDbVerificationCacheEntry? GetModDbVerification(string? modId, string? version)
	{
		string key = ModDbVerificationKey(modId, version);
		lock (modDbVerificationLock)
		{
			AntiCheatModDbVerificationCacheEntry value;
			return modDbVerificationCache.TryGetValue(key, out value) ? value : null;
		}
	}

	private static bool IsFreshModDbVerification(AntiCheatModDbVerificationCacheEntry entry, long now)
	{
		long num = (string.Equals(entry.Status, "Resolved", StringComparison.OrdinalIgnoreCase) ? 604800000 : 43200000);
		return entry.CheckedUtc > 0 && now - entry.CheckedUtc <= num;
	}

	private void QueueServerInstalledModDbBaseline()
	{
		if (!config.Enabled || !config.ClientModDbVerificationEnabled)
		{
			lock (modDbVerificationLock)
			{
				serverModDbBaselineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				serverModDbBaselineLastQueuedUtc = 0L;
				return;
			}
		}
		List<ClientModDto> list;
		try
		{
			list = (from x in (from x in api.ModLoader.Mods
					where x?.Info != null && !string.IsNullOrWhiteSpace(x.Info.ModID) && !string.IsNullOrWhiteSpace(x.Info.Version)
					where x.Info.RequiredOnClient || !string.Equals(x.Info.Side.ToString(), "Server", StringComparison.OrdinalIgnoreCase)
					select new ClientModDto
					{
						ModId = (x.Info.ModID ?? "").Trim(),
						Version = (x.Info.Version ?? "").Trim(),
						ModType = x.Info.Type.ToString(),
						FileName = (x.FileName ?? "").Trim(),
						IsCodeMod = (x.Info.Type == EnumModType.Code),
						IsEnabled = true,
						SourceType = "ServerInstalled"
					}).Where(ShouldVerifyModWithModDb).GroupBy((ClientModDto x) => ModDbVerificationKey(x.ModId, x.Version), StringComparer.OrdinalIgnoreCase)
				select x.First()).Take(256).ToList();
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Could not snapshot installed server mods for ModDB startup baseline warming: {0}", ex.Message);
			return;
		}
		long now = NowMs();
		lock (modDbVerificationLock)
		{
			serverModDbBaselineKeys = list.Select((ClientModDto x) => ModDbVerificationKey(x.ModId, x.Version)).ToHashSet(StringComparer.OrdinalIgnoreCase);
			serverModDbBaselineLastQueuedUtc = now;
		}
		int val = QueueModDbVerification("__server_moddb_baseline__", "", list);
		int num;
		int val2;
		lock (modDbVerificationLock)
		{
			num = serverModDbBaselineKeys.Count((string key) => modDbVerificationCache.TryGetValue(key, out AntiCheatModDbVerificationCacheEntry value) && IsFreshModDbVerification(value, now));
			val2 = serverModDbBaselineKeys.Count((string key) => modDbVerificationPending.Contains(key));
		}
		api.Logger.Notification("[ServerEssentials/AntiCheat] ModDB startup baseline warming: {0} installed third-party release(s), {1} already fresh, {2} queued/in progress. Resolution is bounded to {3} concurrent request(s).", list.Count, num, Math.Max(val, val2), 2);
	}

	private List<AntiCheatConnectionDiagnosticWebSnapshot> GetConnectionDiagnosticsForPlayer(string playerUid, string playerName, int limit = 32)
	{
		lock (connectionDiagnosticLock)
		{
			return (from x in connectionDiagnostics.Where((AntiCheatConnectionDiagnosticWebSnapshot x) => (!string.IsNullOrWhiteSpace(playerUid) && string.Equals(x.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)) || (string.IsNullOrWhiteSpace(playerUid) && !string.IsNullOrWhiteSpace(playerName) && string.Equals(x.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))).Take(Math.Clamp(limit, 1, 64))
				select new AntiCheatConnectionDiagnosticWebSnapshot
				{
					Utc = x.Utc,
					PlayerUid = x.PlayerUid,
					PlayerName = x.PlayerName,
					Code = x.Code,
					Stage = x.Stage,
					Action = x.Action,
					Detail = x.Detail,
					Status = x.Status,
					ClientInstalled = x.ClientInstalled,
					ClientVersion = x.ClientVersion,
					ProtocolVersion = x.ProtocolVersion,
					LastHelloUtc = x.LastHelloUtc,
					LastHeartbeatUtc = x.LastHeartbeatUtc,
					StartupGateStartedUtc = x.StartupGateStartedUtc,
					ChallengeId = x.ChallengeId
				}).ToList();
		}
	}

	private int QueueModDbVerification(string playerUid, string targetGameVersion, IReadOnlyList<ClientModDto> mods, bool forceRefresh = false)
	{
		if (!config.ClientModDbVerificationEnabled && !forceRefresh)
		{
			return 0;
		}
		int num = 0;
		long now = NowMs();
		foreach (ClientModDto item in (from x in (mods ?? Array.Empty<ClientModDto>()).Where(ShouldVerifyModWithModDb).GroupBy((ClientModDto x) => ModDbVerificationKey(x.ModId, x.Version), StringComparer.OrdinalIgnoreCase)
			select x.First()).Take(256))
		{
			string key = ModDbVerificationKey(item.ModId, item.Version);
			bool flag;
			lock (modDbVerificationLock)
			{
				if ((forceRefresh || !modDbVerificationCache.TryGetValue(key, out AntiCheatModDbVerificationCacheEntry value) || !IsFreshModDbVerification(value, now)) && !modDbVerificationCts.IsCancellationRequested && !modDbVerificationPending.Contains(key))
				{
					if (modDbVerificationPending.Count < 512)
					{
						flag = modDbVerificationPending.Add(key);
						goto IL_01bb;
					}
					modDbVerificationDeferred = true;
				}
			}
			continue;
			IL_01bb:
			if (!flag)
			{
				continue;
			}
			num++;
			string modId = NormalizeModId(item.ModId);
			string version = (item.Version ?? "").Trim();
			Task.Run(async () =>
			{
				try
				{
					bool slotTaken = false;
					AntiCheatModDbVerificationCacheEntry entry;
					try
					{
						await modDbVerificationSlots.WaitAsync(modDbVerificationCts.Token).ConfigureAwait(continueOnCapturedContext: false);
						slotTaken = true;
						AntiCheatModDbFingerprintResolution result = await AntiCheatModDbFingerprintResolver.ResolveExactVersionAsync(modId, version, targetGameVersion ?? "", modDbVerificationCts.Token).ConfigureAwait(continueOnCapturedContext: false);
						List<AntiCheatModDbReleaseFingerprintResolution> releases = (result.Success ? (result.Releases ?? new List<AntiCheatModDbReleaseFingerprintResolution>()).Where((AntiCheatModDbReleaseFingerprintResolution x) => x != null).Take(8).ToList() : new List<AntiCheatModDbReleaseFingerprintResolution>());
						AntiCheatModDbReleaseFingerprintResolution release = releases.FirstOrDefault();
						string status = ((result.Success && release != null) ? "Resolved" : (result.Message.Contains("does not expose an exact", StringComparison.OrdinalIgnoreCase) ? "NotFound" : "Unavailable"));
						entry = new AntiCheatModDbVerificationCacheEntry
						{
							ModId = modId,
							Version = version,
							Status = status,
							Message = result.Message,
							CheckedUtc = NowMs(),
							Release = release,
							Releases = releases
						};
					}
					catch (OperationCanceledException)
					{
						return;
					}
					catch (Exception ex2)
					{
						Exception ex3 = ex2;
						entry = new AntiCheatModDbVerificationCacheEntry
						{
							ModId = modId,
							Version = version,
							Status = "Unavailable",
							Message = ex3.GetType().Name + ": " + ex3.Message,
							CheckedUtc = NowMs()
						};
					}
					finally
					{
						if (slotTaken)
						{
							modDbVerificationSlots.Release();
						}
					}
					lock (modDbVerificationLock)
					{
						if (modDbVerificationCts.IsCancellationRequested)
						{
							return;
						}
						StoreModDbVerificationEntryLocked(key, entry);
						modDbVerificationCacheDirtyCount++;
					}
					try
					{
						api.Event.EnqueueMainThreadTask(() =>
						{
							if (started)
							{
								SaveModDbVerificationCache();
								RefreshSessionsAfterModDbVerification(modId, version);
							}
						}, "nses-anticheat-moddb-verification");
					}
					catch
					{
					}
				}
				finally
				{
					lock (modDbVerificationLock)
					{
						modDbVerificationPending.Remove(key);
					}
				}
			});
		}
		return num;
	}

	private void RefillDeferredModDbVerifications()
	{
		string[] source;
		lock (modDbVerificationLock)
		{
			if (!modDbVerificationDeferred || modDbVerificationCts.IsCancellationRequested || modDbVerificationPending.Count >= 512)
			{
				return;
			}
			modDbVerificationDeferred = false;
			source = serverModDbBaselineKeys.ToArray();
		}
		ClientModDto[] mods = source.Select((string key) =>
		{
			int num = key.IndexOf('@');
			return new ClientModDto
			{
				ModId = key.Substring(0, num),
				Version = key.Substring(num + 1)
			};
		}).ToArray();
		QueueModDbVerification("__server_moddb_baseline__", "", mods);
		foreach (AntiCheatSession item in sessions.Snapshot())
		{
			if (item.LastScanUtc > 0)
			{
				QueueModDbVerification(item.PlayerUid, item.ClientGameVersion, item.LastMods);
			}
		}
	}

	private void RefreshSessionsAfterModDbVerification(string modId, string version)
	{
		string key = ModDbVerificationKey(modId, version);
		foreach (AntiCheatSession item in sessions.Snapshot())
		{
			if (item.LastScanUtc > 0 && (item.LastMods ?? new List<ClientModDto>()).Any((ClientModDto m) => string.Equals(ModDbVerificationKey(m.ModId, m.Version), key, StringComparison.OrdinalIgnoreCase)))
			{
				RefreshSessionAfterModDbVerification(item.PlayerUid);
			}
		}
	}

	public (bool Success, string Message, int UniqueTargets, int Queued, int FreshCached, int AlreadyPending) ResolveAllClientModDbFingerprints(bool forceRefresh = false)
	{
		HashSet<string> onlineUids = (from p in api.World.AllOnlinePlayers.OfType<IServerPlayer>()
			select p.PlayerUID).ToHashSet(StringComparer.OrdinalIgnoreCase);
		List<AntiCheatSession> source = (from s in sessions.Snapshot()
			where onlineUids.Contains(s.PlayerUid) && s.LastScanUtc > 0
			select s).ToList();
		var list = (from g in source.SelectMany((AntiCheatSession s) => from m in (s.LastMods ?? new List<ClientModDto>()).Where(ShouldVerifyModWithModDb)
				select new
				{
					Session = s,
					Mod = m,
					Key = ModDbVerificationKey(m.ModId, m.Version)
				}).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
			select g.First()).Take(2048).ToList();
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		long now = NowMs();
		foreach (var item2 in list)
		{
			lock (modDbVerificationLock)
			{
				if (modDbVerificationPending.Contains(item2.Key))
				{
					num2++;
					continue;
				}
				if (!forceRefresh && modDbVerificationCache.TryGetValue(item2.Key, out AntiCheatModDbVerificationCacheEntry value) && IsFreshModDbVerification(value, now))
				{
					num++;
					continue;
				}
			}
			num3 += QueueModDbVerification(item2.Session.PlayerUid, item2.Session.ClientGameVersion, new ClientModDto[1] { item2.Mod }, forceRefresh);
		}
		string item = (forceRefresh ? $"Queued {num3} of {list.Count} unique client mod release fingerprint(s) for refresh." : $"Queued {num3} unresolved/stale fingerprint(s); {num} already fresh and {num2} already in progress.");
		return (Success: true, Message: item, UniqueTargets: list.Count, Queued: num3, FreshCached: num, AlreadyPending: num2);
	}

	private void RefreshSessionAfterModDbVerification(string playerUid)
	{
		if (!sessions.TryGet(playerUid, out AntiCheatSession session) || session.LastScanUtc <= 0)
		{
			return;
		}
		List<ScanFindingDto> list = session.Findings.Where((ScanFindingDto f) => !IsServerEvaluationScanner(f.Scanner)).ToList();
		list.AddRange(BuildModDbVerificationFindings(session.LastMods, session.LastAssemblies));
		(AntiCheatPlayerStatus, int, List<ScanFindingDto>) tuple = evaluator.Evaluate(session.LastMods, list, session.LastAssemblies, session.LastIntegrity, session.LastHarmony, GetCanonicalModSnapshot(), GetCoreAssemblySnapshot());
		session.Findings = tuple.Item3;
		session.CorrelationRiskBonus = 0;
		long num = NowMs();
		RefreshObservedFindingTimestamps(session, tuple.Item3, num, refreshClientEvidence: false);
		UpdateSessionFamilyRisk(session, num, preserveOperationalStatus: true);
		ApplyIncidentCorrelation(session, num);
		IServerPlayer serverPlayer = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerUID.Equals(playerUid, StringComparison.OrdinalIgnoreCase));
		(List<string>, List<string>) tuple2 = EvaluateStrictClientModDbAdmission(session.LastMods, session.LastAssemblies);
		session.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && (string.Equals(f.Code, "CLIENT_MOD_DB_PENDING", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Code, "CLIENT_MOD_DB_REJECTED", StringComparison.OrdinalIgnoreCase)));
		if (config.StrictClientModDbEnforcementEnabled && tuple2.Item1.Count > 0)
		{
			SetRuntimeFinding(session, "CLIENT_MOD_DB_REJECTED", AntiCheatSeverity.Info, "Strict client-mod database policy rejected one or more reported mods. This is an admission-policy failure, not by itself a cheat finding.", StrictModDbSubject(tuple2.Item1));
			session.Status = AntiCheatPlayerStatus.Failed;
			session.StartupGatePending = false;
			session.StartupIntegrityVerified = false;
			session.StartupGateCompletedUtc = num;
			session.StartupGateVerdict = "ModDatabaseRejected";
			if (serverPlayer != null)
			{
				EnforceStrictModDatabaseDisconnect(serverPlayer, session, tuple2.Item1);
			}
		}
		else if (config.StrictClientModDbEnforcementEnabled && tuple2.Item2.Count > 0)
		{
			SetRuntimeFinding(session, "CLIENT_MOD_DB_PENDING", AntiCheatSeverity.Info, "Strict client-mod database verification is still resolving exact fingerprints. Admission remains pending until every reported mod has a trusted exact match.", StrictModDbSubject(tuple2.Item2));
			session.StartupGatePending = true;
			session.StartupIntegrityVerified = false;
			session.StartupGateVerdict = "ModDatabasePending";
		}
		else if (session.StartupGatePending && string.Equals(session.StartupGateVerdict, "ModDatabasePending", StringComparison.OrdinalIgnoreCase))
		{
			CompleteStartupGate(session, tuple.Item1, num);
			RecordConnectionDiagnostic(session, "CLIENT_MOD_DB_APPROVED", "Startup mod database", "Accepted", "Every reported third-party client mod now has an exact trusted database fingerprint.");
		}
		if (serverPlayer != null && !session.RequiredClientDisconnectScheduled)
		{
			ApplyConsequences(serverPlayer, session, "moddb-verification");
		}
		PublishPlayer(session);
	}

	private static List<AntiCheatModDbReleaseFingerprintResolution> GetModDbReleaseCandidates(AntiCheatModDbVerificationCacheEntry? cached)
	{
		if (cached == null)
		{
			return new List<AntiCheatModDbReleaseFingerprintResolution>();
		}
		List<AntiCheatModDbReleaseFingerprintResolution> list = (cached.Releases ?? new List<AntiCheatModDbReleaseFingerprintResolution>()).Where((AntiCheatModDbReleaseFingerprintResolution x) => x != null).ToList();
		if (list.Count == 0 && cached.Release != null)
		{
			list.Add(cached.Release);
		}
		return list;
	}

	private static string CompareHashAny(string? client, IEnumerable<string?> officials)
	{
		string clientHash = NormalizeSha256(client);
		if (clientHash.Length != 64)
		{
			return "Unavailable";
		}
		List<string> list = (from x in (officials ?? Array.Empty<string>()).Select(NormalizeSha256)
			where x.Length == 64
			select x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			return "Unavailable";
		}
		return list.Any((string x) => string.Equals(clientHash, x, StringComparison.OrdinalIgnoreCase)) ? "Match" : "Mismatch";
	}

	private static bool DllLayoutMatches(ClientModDto mod, AntiCheatModDbReleaseFingerprintResolution release)
	{
		return mod.DllCount == release.DllCount && mod.DllBytes == release.DllBytes;
	}

	private static int OfficialReleaseMatchScore(ClientModDto mod, IReadOnlyList<ClientAssemblyDto> assemblies, AntiCheatModDbReleaseFingerprintResolution release)
	{
		int num = 0;
		if (IsSha256(mod.ContentSha256) && IsSha256(release.ContentSha256) && string.Equals(NormalizeSha256(mod.ContentSha256), NormalizeSha256(release.ContentSha256), StringComparison.OrdinalIgnoreCase))
		{
			num += 10000;
		}
		if (IsSha256(mod.Sha256) && IsSha256(release.PackageSha256) && string.Equals(NormalizeSha256(mod.Sha256), NormalizeSha256(release.PackageSha256), StringComparison.OrdinalIgnoreCase))
		{
			num += 8000;
		}
		if ((mod.PackageBytes > 0 || mod.DllCount > 0 || mod.DllBytes > 0 || release.DllCount == 0) && DllLayoutMatches(mod, release))
		{
			num += 250;
		}
		string id = NormalizeModId(mod.ModId);
		List<ClientAssemblyDto> source = (assemblies ?? Array.Empty<ClientAssemblyDto>()).Where((ClientAssemblyDto a) => (a.AssociatedModIds ?? new List<string>()).Any((string x) => string.Equals(NormalizeModId(x), id, StringComparison.OrdinalIgnoreCase))).ToList();
		foreach (AntiCheatModDbAssemblyFingerprintResolution official in release.Assemblies ?? new List<AntiCheatModDbAssemblyFingerprintResolution>())
		{
			ClientAssemblyDto clientAssemblyDto = source.FirstOrDefault((ClientAssemblyDto a) => string.Equals(a.AssemblyName, official.AssemblyName, StringComparison.OrdinalIgnoreCase));
			if (clientAssemblyDto != null && IsSha256(clientAssemblyDto.Sha256) && IsSha256(official.Sha256) && string.Equals(NormalizeSha256(clientAssemblyDto.Sha256), NormalizeSha256(official.Sha256), StringComparison.OrdinalIgnoreCase))
			{
				num += 100;
			}
		}
		return num;
	}

	private (List<string> Rejected, List<string> Pending) EvaluateStrictClientModDbAdmission(IReadOnlyList<ClientModDto>? mods, IReadOnlyList<ClientAssemblyDto>? assemblies)
	{
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		if (!config.StrictClientModDbEnforcementEnabled)
		{
			return (Rejected: list, Pending: list2);
		}
		if (!config.ClientModDbVerificationEnabled)
		{
			list.Add("Official ModDB verification is disabled while strict client-mod database enforcement is enabled.");
			return (Rejected: list, Pending: list2);
		}
		long now = NowMs();
		foreach (ClientModDto item in (mods ?? Array.Empty<ClientModDto>()).Where((ClientModDto x) => x != null))
		{
			string text = NormalizeModId(item.ModId);
			string text2 = (item.Version ?? "").Trim();
			if (text.Length == 0)
			{
				list.Add("reported mod has an invalid or empty ModID");
			}
			else
			{
				if (IsProtectedCoreModId(text) || string.Equals(text, "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (text2.Length == 0)
				{
					list.Add(text + ": reported mod version is empty");
					continue;
				}
				string text3 = (text + " " + text2).Trim();
				if ((TryGetServerModBaseline(item, out AntiCheatCanonicalModFingerprint expected) && ComputeServerBaselineVerdict(item, assemblies, expected, out string _, out string _, out string _, out string _, out string _) == "Match") || FindApprovedClientModFingerprint(item) != null)
				{
					continue;
				}
				AntiCheatModDbVerificationCacheEntry modDbVerification = GetModDbVerification(item.ModId, item.Version);
				if (modDbVerification == null || !IsFreshModDbVerification(modDbVerification, now))
				{
					list2.Add(text3 + ": waiting for a fresh ModDB fingerprint");
					continue;
				}
				List<AntiCheatModDbReleaseFingerprintResolution> modDbReleaseCandidates = GetModDbReleaseCandidates(modDbVerification);
				if (!string.Equals(modDbVerification.Status, "Resolved", StringComparison.OrdinalIgnoreCase) || modDbReleaseCandidates.Count == 0)
				{
					string text4 = (string.IsNullOrWhiteSpace(modDbVerification.Status) ? "Unverified" : modDbVerification.Status.Trim());
					list.Add(text3 + ": ModDB status " + text4 + "; no exact trusted release fingerprint is available");
					continue;
				}
				string text5 = CompareHashAny(item.ContentSha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.ContentSha256));
				string text6 = CompareHashAny(item.Sha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.PackageSha256));
				if (!(text5 == "Match") && (!(text5 == "Unavailable") || !(text6 == "Match")))
				{
					string text7;
					if (text5 == "Mismatch")
					{
						text7 = "normalized content SHA-256 does not match any official same-version release";
					}
					else
					{
						text7 = ((text6 == "Mismatch") ? "package SHA-256 does not match any official same-version release and normalized content was unavailable" : "client did not provide an exact comparable content/package SHA-256");
					}
					list.Add(text3 + ": " + text7);
				}
			}
		}
		return (Rejected: list, Pending: list2);
	}

	private static string StrictModDbSubject(IEnumerable<string> rows)
	{
		List<string> list = (rows ?? Array.Empty<string>()).Where((string x) => !string.IsNullOrWhiteSpace(x)).Take(6).ToList();
		return (list.Count == 0) ? "" : string.Join(" | ", list);
	}

	private List<ScanFindingDto> BuildModDbVerificationFindings(IReadOnlyList<ClientModDto>? mods, IReadOnlyList<ClientAssemblyDto>? assemblies)
	{
		List<ScanFindingDto> list = new List<ScanFindingDto>();
		if (!config.ClientModDbVerificationEnabled)
		{
			return list;
		}
		foreach (ClientModDto mod in (mods ?? Array.Empty<ClientModDto>()).Where(ShouldVerifyModWithModDb))
		{
			if ((TryGetServerModBaseline(mod, out AntiCheatCanonicalModFingerprint expected) && ComputeServerBaselineVerdict(mod, assemblies, expected, out string _, out string _, out string _, out string _, out string _) == "Match") || FindApprovedClientModFingerprint(mod) != null)
			{
				continue;
			}
			AntiCheatModDbVerificationCacheEntry modDbVerification = GetModDbVerification(mod.ModId, mod.Version);
			List<AntiCheatModDbReleaseFingerprintResolution> modDbReleaseCandidates = GetModDbReleaseCandidates(modDbVerification);
			if (modDbVerification == null || !IsFreshModDbVerification(modDbVerification, NowMs()) || !string.Equals(modDbVerification.Status, "Resolved", StringComparison.OrdinalIgnoreCase) || modDbReleaseCandidates.Count == 0)
			{
				continue;
			}
			string id = NormalizeModId(mod.ModId);
			string text = CompareHashAny(mod.ContentSha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.ContentSha256));
			string text2 = CompareHashAny(mod.Sha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.PackageSha256));
			bool flag = mod.PackageBytes > 0 || mod.DllCount > 0 || mod.DllBytes > 0 || modDbReleaseCandidates.All((AntiCheatModDbReleaseFingerprintResolution x) => x.DllCount == 0);
			bool flag2 = flag && modDbReleaseCandidates.Any((AntiCheatModDbReleaseFingerprintResolution x) => DllLayoutMatches(mod, x));
			if (text == "Mismatch")
			{
				list.Add(new ScanFindingDto
				{
					Scanner = "ModDbPolicy",
					Code = "MODDB_CONTENT_MISMATCH",
					Severity = 2,
					Summary = "The client's normalized mod content does not match any official Mod DB upload for the same mod ID and version.",
					Subject = id,
					Evidence = $"version={mod.Version};officialCandidates={modDbReleaseCandidates.Count};clientContent={NormalizeSha256(mod.ContentSha256)};clientPackageBytes={mod.PackageBytes}"
				});
			}
			else if (text == "Unavailable" && text2 == "Mismatch")
			{
				list.Add(new ScanFindingDto
				{
					Scanner = "ModDbPolicy",
					Code = "MODDB_PACKAGE_MISMATCH",
					Severity = 0,
					Summary = "The client package hash does not match any official Mod DB upload for the same version, but normalized content could not be compared.",
					Subject = id,
					Evidence = $"version={mod.Version};officialCandidates={modDbReleaseCandidates.Count};clientPackage={NormalizeSha256(mod.Sha256)}"
				});
			}
			if (flag && !flag2)
			{
				list.Add(new ScanFindingDto
				{
					Scanner = "ModDbPolicy",
					Code = "MODDB_DLL_SET_MISMATCH",
					Severity = 0,
					Summary = "The client's DLL count/bytes do not match any official Mod DB upload for the same version. This is advisory until an exact hash comparison also disagrees.",
					Subject = id,
					Evidence = $"version={mod.Version};officialCandidates={modDbReleaseCandidates.Count};clientDllCount={mod.DllCount};clientDllBytes={mod.DllBytes}"
				});
			}
			List<ClientAssemblyDto> list2 = (assemblies ?? Array.Empty<ClientAssemblyDto>()).Where((ClientAssemblyDto a) => (a.AssociatedModIds ?? new List<string>()).Any((string x) => string.Equals(NormalizeModId(x), id, StringComparison.OrdinalIgnoreCase))).ToList();
			foreach (ClientAssemblyDto clientAssembly in list2)
			{
				int num = (from x in (clientAssembly.AssociatedModIds ?? new List<string>()).Select(NormalizeModId)
					where x.Length > 0
					select x).Distinct(StringComparer.OrdinalIgnoreCase).Count();
				if (num != 1 || !IsSha256(clientAssembly.Sha256))
				{
					continue;
				}
				List<AntiCheatModDbAssemblyFingerprintResolution> list3 = (from x in modDbReleaseCandidates.SelectMany((AntiCheatModDbReleaseFingerprintResolution x) => x.Assemblies ?? new List<AntiCheatModDbAssemblyFingerprintResolution>())
					where string.Equals(x.AssemblyName, clientAssembly.AssemblyName, StringComparison.OrdinalIgnoreCase) && IsSha256(x.Sha256)
					select x).ToList();
				if (list3.Count == 0)
				{
					continue;
				}
				string clientHash = NormalizeSha256(clientAssembly.Sha256);
				if (!list3.Any((AntiCheatModDbAssemblyFingerprintResolution x) => string.Equals(clientHash, NormalizeSha256(x.Sha256), StringComparison.OrdinalIgnoreCase)))
				{
					AntiCheatModDbAssemblyFingerprintResolution antiCheatModDbAssemblyFingerprintResolution = list3.OrderByDescending((AntiCheatModDbAssemblyFingerprintResolution x) => ((clientAssembly.SimilarityMinHash?.Count ?? 0) >= 64 && (x.SimilarityMinHash?.Count ?? 0) >= 64) ? StructuralSimilarity.SimilarityPercent(clientAssembly.SimilarityMinHash, x.SimilarityMinHash) : 0).First();
					bool flag3 = clientAssembly.FileSizeBytes > 0 && antiCheatModDbAssemblyFingerprintResolution.FileSizeBytes > 0 && clientAssembly.FileSizeBytes != antiCheatModDbAssemblyFingerprintResolution.FileSizeBytes;
					bool flag4 = IsSha256(clientAssembly.BehaviorSha256) && IsSha256(antiCheatModDbAssemblyFingerprintResolution.BehaviorSha256) && !string.Equals(NormalizeSha256(clientAssembly.BehaviorSha256), NormalizeSha256(antiCheatModDbAssemblyFingerprintResolution.BehaviorSha256), StringComparison.OrdinalIgnoreCase);
					int value = (((clientAssembly.SimilarityMinHash?.Count ?? 0) >= 64 && (antiCheatModDbAssemblyFingerprintResolution.SimilarityMinHash?.Count ?? 0) >= 64) ? StructuralSimilarity.SimilarityPercent(clientAssembly.SimilarityMinHash, antiCheatModDbAssemblyFingerprintResolution.SimilarityMinHash) : 0);
					list.Add(new ScanFindingDto
					{
						Scanner = "ModDbPolicy",
						Code = "MODDB_ASSEMBLY_MISMATCH",
						Severity = 2,
						Summary = "A confidently associated client DLL SHA-256 does not match any DLL with the same assembly name in the official Mod DB upload(s) for this exact version.",
						Subject = id,
						Evidence = $"version={mod.Version};assembly={clientAssembly.AssemblyName};officialCandidates={modDbReleaseCandidates.Count};officialDllCandidates={list3.Count};hashComparable=true;hashMismatch=true;sizeMismatch={flag3.ToString().ToLowerInvariant()};behaviorMismatch={flag4.ToString().ToLowerInvariant()};structureSimilarity={value};clientBytes={clientAssembly.FileSizeBytes};officialBytes={antiCheatModDbAssemblyFingerprintResolution.FileSizeBytes};associationOwners={num}"
					});
				}
			}
		}
		return list;
	}

	private AntiCheatModWebSnapshot ToWebModSnapshot(ClientModDto mod, IReadOnlyList<ClientAssemblyDto>? assemblies)
	{
		AntiCheatModWebSnapshot antiCheatModWebSnapshot = new AntiCheatModWebSnapshot
		{
			ModId = mod.ModId,
			Version = mod.Version,
			ModType = mod.ModType,
			FileName = mod.FileName,
			Sha256 = mod.Sha256,
			ContentSha256 = mod.ContentSha256,
			PackageBytes = mod.PackageBytes,
			DllCount = mod.DllCount,
			DllBytes = mod.DllBytes,
			IsCodeMod = mod.IsCodeMod,
			SourceType = mod.SourceType,
			LoadedOnServer = serverLoadedModIds.Contains((mod.ModId ?? "").Trim())
		};
		AntiCheatCanonicalModFingerprint antiCheatCanonicalModFingerprint = null;
		if (TryGetServerModBaseline(mod, out AntiCheatCanonicalModFingerprint expected))
		{
			antiCheatCanonicalModFingerprint = expected;
			antiCheatModWebSnapshot.ServerBaselineAvailable = true;
			antiCheatModWebSnapshot.ServerBaselineVersion = expected.Version;
			antiCheatModWebSnapshot.ServerBaselineFileName = expected.SourceFileName;
			antiCheatModWebSnapshot.ServerBaselinePackageSha256 = expected.PackageSha256;
			antiCheatModWebSnapshot.ServerBaselineContentSha256 = expected.ContentSha256;
			antiCheatModWebSnapshot.ServerBaselinePackageBytes = expected.PackageBytes;
			antiCheatModWebSnapshot.ServerBaselineDllCount = expected.DllCount;
			antiCheatModWebSnapshot.ServerBaselineDllBytes = expected.DllBytes;
			antiCheatModWebSnapshot.ServerBaselineVerdict = ComputeServerBaselineVerdict(mod, assemblies, expected, out string versionComparison, out string packageComparison, out string contentComparison, out string dllComparison, out string assemblyComparison);
			antiCheatModWebSnapshot.ServerVersionComparison = versionComparison;
			antiCheatModWebSnapshot.ServerPackageComparison = packageComparison;
			antiCheatModWebSnapshot.ServerContentComparison = contentComparison;
			antiCheatModWebSnapshot.ServerDllLayoutComparison = dllComparison;
			antiCheatModWebSnapshot.ServerAssemblyComparison = assemblyComparison;
			AntiCheatModWebSnapshot antiCheatModWebSnapshot2 = antiCheatModWebSnapshot;
			string serverBaselineVerdict = antiCheatModWebSnapshot.ServerBaselineVerdict;
			if (1 == 0)
			{
			}
			string serverBaselineMessage = serverBaselineVerdict switch
			{
				"Match" => "Client matches the exact mod baseline loaded by this server. Server baseline takes precedence over public ModDB packaging for this shared mod.", 
				"Mismatch" => "Client differs from the exact version/content/DLL baseline loaded by this server.", 
				"Diagnostic" => "Client has packaging or DLL-layout differences from the server copy, but no authoritative normalized-content or exact DLL hash mismatch is available.", 
				_ => "The server has this mod loaded, but there is not enough comparable fingerprint data for a definitive server-baseline verdict.", 
			};
			if (1 == 0)
			{
			}
			antiCheatModWebSnapshot2.ServerBaselineMessage = serverBaselineMessage;
		}
		AntiCheatApprovedClientModFingerprint antiCheatApprovedClientModFingerprint = FindApprovedClientModFingerprint(mod);
		if (antiCheatApprovedClientModFingerprint != null)
		{
			antiCheatModWebSnapshot.ApprovedCustomFingerprint = true;
			antiCheatModWebSnapshot.ApprovedCustomFingerprintId = antiCheatApprovedClientModFingerprint.Id;
			antiCheatModWebSnapshot.ApprovedCustomLabel = antiCheatApprovedClientModFingerprint.Label;
			antiCheatModWebSnapshot.ApprovedCustomNotes = antiCheatApprovedClientModFingerprint.Notes;
		}
		if (!config.ClientModDbVerificationEnabled)
		{
			antiCheatModWebSnapshot.ModDbStatus = "Disabled";
			antiCheatModWebSnapshot.ModDbMessage = "Official ModDB verification is disabled by server configuration. Local client mod inventory, server-baseline and blacklist checks remain active.";
			return antiCheatModWebSnapshot;
		}
		AntiCheatModDbVerificationCacheEntry modDbVerification = GetModDbVerification(mod.ModId, mod.Version);
		string item = ModDbVerificationKey(mod.ModId, mod.Version);
		bool flag;
		lock (modDbVerificationLock)
		{
			flag = modDbVerificationPending.Contains(item);
		}
		if (modDbVerification == null)
		{
			antiCheatModWebSnapshot.ModDbStatus = (flag ? "Pending" : "NotChecked");
			antiCheatModWebSnapshot.ModDbMessage = (flag ? "Exact official Mod DB release verification is in progress." : "Waiting for Mod DB verification.");
			return antiCheatModWebSnapshot;
		}
		bool flag2 = IsFreshModDbVerification(modDbVerification, NowMs());
		AntiCheatModWebSnapshot antiCheatModWebSnapshot3 = antiCheatModWebSnapshot;
		string modDbStatus;
		if (flag)
		{
			modDbStatus = "Refreshing";
		}
		else
		{
			modDbStatus = ((!flag2) ? "Stale" : modDbVerification.Status);
		}
		antiCheatModWebSnapshot3.ModDbStatus = modDbStatus;
		AntiCheatModWebSnapshot antiCheatModWebSnapshot4 = antiCheatModWebSnapshot;
		string modDbMessage;
		if (flag2)
		{
			modDbMessage = modDbVerification.Message;
		}
		else
		{
			modDbMessage = (flag ? ("Refreshing stale ModDB fingerprints before any comparison can be treated as resolved. " + modDbVerification.Message) : ("Cached ModDB fingerprint is stale and will be refreshed before it can produce comparison findings. " + modDbVerification.Message));
		}
		antiCheatModWebSnapshot4.ModDbMessage = modDbMessage;
		antiCheatModWebSnapshot.ModDbCheckedUtc = modDbVerification.CheckedUtc;
		List<AntiCheatModDbReleaseFingerprintResolution> modDbReleaseCandidates = GetModDbReleaseCandidates(modDbVerification);
		antiCheatModWebSnapshot.OfficialCandidateCount = modDbReleaseCandidates.Count;
		if (!flag2 || modDbReleaseCandidates.Count == 0)
		{
			return antiCheatModWebSnapshot;
		}
		AntiCheatModDbReleaseFingerprintResolution antiCheatModDbReleaseFingerprintResolution = modDbReleaseCandidates.OrderByDescending((AntiCheatModDbReleaseFingerprintResolution x) => OfficialReleaseMatchScore(mod, assemblies ?? Array.Empty<ClientAssemblyDto>(), x)).First();
		antiCheatModWebSnapshot.OfficialVersion = antiCheatModDbReleaseFingerprintResolution.Version;
		antiCheatModWebSnapshot.OfficialFileName = antiCheatModDbReleaseFingerprintResolution.FileName;
		antiCheatModWebSnapshot.OfficialPackageSha256 = antiCheatModDbReleaseFingerprintResolution.PackageSha256;
		antiCheatModWebSnapshot.OfficialContentSha256 = antiCheatModDbReleaseFingerprintResolution.ContentSha256;
		antiCheatModWebSnapshot.OfficialPackageBytes = antiCheatModDbReleaseFingerprintResolution.PackageBytes;
		antiCheatModWebSnapshot.OfficialDllCount = antiCheatModDbReleaseFingerprintResolution.DllCount;
		antiCheatModWebSnapshot.OfficialDllBytes = antiCheatModDbReleaseFingerprintResolution.DllBytes;
		antiCheatModWebSnapshot.PackageComparison = CompareHashAny(mod.Sha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.PackageSha256));
		antiCheatModWebSnapshot.ContentComparison = CompareHashAny(mod.ContentSha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.ContentSha256));
		bool flag3 = mod.PackageBytes > 0 || mod.DllCount > 0 || mod.DllBytes > 0 || modDbReleaseCandidates.All((AntiCheatModDbReleaseFingerprintResolution x) => x.DllCount == 0);
		AntiCheatModWebSnapshot antiCheatModWebSnapshot5 = antiCheatModWebSnapshot;
		string dllLayoutComparison;
		if (flag3)
		{
			dllLayoutComparison = (modDbReleaseCandidates.Any((AntiCheatModDbReleaseFingerprintResolution x) => DllLayoutMatches(mod, x)) ? "Match" : "Mismatch");
		}
		else
		{
			dllLayoutComparison = "Unavailable";
		}
		antiCheatModWebSnapshot5.DllLayoutComparison = dllLayoutComparison;
		string modId = NormalizeModId(mod.ModId);
		List<ClientAssemblyDto> list = AssembliesForMod(assemblies, modId);
		if (!modDbReleaseCandidates.Any((AntiCheatModDbReleaseFingerprintResolution x) => (x.Assemblies?.Count ?? 0) > 0))
		{
			antiCheatModWebSnapshot.AssemblyComparison = "NotApplicable";
		}
		else if (list.Count == 0)
		{
			antiCheatModWebSnapshot.AssemblyComparison = "Unavailable";
		}
		else
		{
			bool flag4 = false;
			bool flag5 = false;
			foreach (AntiCheatModDbReleaseFingerprintResolution item2 in modDbReleaseCandidates)
			{
				List<AntiCheatModDbAssemblyFingerprintResolution> list2 = item2.Assemblies ?? new List<AntiCheatModDbAssemblyFingerprintResolution>();
				if (list2.Count == 0)
				{
					continue;
				}
				bool flag6 = true;
				bool flag7 = true;
				foreach (AntiCheatModDbAssemblyFingerprintResolution official in list2)
				{
					ClientAssemblyDto clientAssemblyDto = list.FirstOrDefault((ClientAssemblyDto a) => string.Equals(a.AssemblyName, official.AssemblyName, StringComparison.OrdinalIgnoreCase));
					if (clientAssemblyDto == null)
					{
						flag6 = false;
						flag7 = false;
						break;
					}
					int num = (from x in (clientAssemblyDto.AssociatedModIds ?? new List<string>()).Select(NormalizeModId)
						where x.Length > 0
						select x).Distinct(StringComparer.OrdinalIgnoreCase).Count();
					if (num != 1 || !IsSha256(clientAssemblyDto.Sha256) || !IsSha256(official.Sha256))
					{
						flag6 = false;
						flag7 = false;
						break;
					}
					if (!string.Equals(NormalizeSha256(clientAssemblyDto.Sha256), NormalizeSha256(official.Sha256), StringComparison.OrdinalIgnoreCase))
					{
						flag7 = false;
					}
				}
				if (flag6)
				{
					flag4 = true;
					if (flag7)
					{
						flag5 = true;
						break;
					}
				}
			}
			AntiCheatModWebSnapshot antiCheatModWebSnapshot6 = antiCheatModWebSnapshot;
			string assemblyComparison2;
			if (flag5)
			{
				assemblyComparison2 = "Match";
			}
			else
			{
				assemblyComparison2 = (flag4 ? "Mismatch" : "Unavailable");
			}
			antiCheatModWebSnapshot6.AssemblyComparison = assemblyComparison2;
		}
		AntiCheatModWebSnapshot antiCheatModWebSnapshot7 = antiCheatModWebSnapshot;
		string comparisonBasis;
		if (antiCheatModWebSnapshot.ContentComparison == "Match")
		{
			comparisonBasis = "Normalized content SHA-256";
		}
		else if (antiCheatModWebSnapshot.PackageComparison == "Match")
		{
			comparisonBasis = "Package SHA-256";
		}
		else if (antiCheatModWebSnapshot.AssemblyComparison == "Match")
		{
			comparisonBasis = "Assembly SHA-256";
		}
		else
		{
			comparisonBasis = ((antiCheatModWebSnapshot.DllLayoutComparison == "Match") ? "DLL layout (supporting)" : "No authoritative match");
		}
		antiCheatModWebSnapshot7.ComparisonBasis = comparisonBasis;
		antiCheatModWebSnapshot.ComparedAgainstOfficial = flag2 && modDbReleaseCandidates.Count > 0;
		if (antiCheatCanonicalModFingerprint != null)
		{
			if (!string.Equals((antiCheatCanonicalModFingerprint.Version ?? "").Trim(), (mod.Version ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
			{
				antiCheatModWebSnapshot.ServerOfficialComparison = "NotApplicable";
			}
			else
			{
				string text = CompareHashAny(antiCheatCanonicalModFingerprint.ContentSha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.ContentSha256));
				string text2 = CompareHashAny(antiCheatCanonicalModFingerprint.PackageSha256, modDbReleaseCandidates.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.PackageSha256));
				AntiCheatModWebSnapshot antiCheatModWebSnapshot8 = antiCheatModWebSnapshot;
				string serverOfficialComparison;
				if (text == "Match" || text2 == "Match")
				{
					serverOfficialComparison = "Match";
				}
				else if (text == "Mismatch")
				{
					serverOfficialComparison = "Mismatch";
				}
				else
				{
					serverOfficialComparison = ((text2 == "Mismatch") ? "Diagnostic" : "Unavailable");
				}
				antiCheatModWebSnapshot8.ServerOfficialComparison = serverOfficialComparison;
				if (antiCheatModWebSnapshot.ServerBaselineVerdict == "Match" && antiCheatModWebSnapshot.ServerOfficialComparison == "Mismatch")
				{
					antiCheatModWebSnapshot.ModDbMessage = "Client matches the exact server-loaded build. The server baseline itself differs from every official ModDB upload for this version, so the public ModDB difference is treated as server-customization context rather than a player mismatch. " + antiCheatModWebSnapshot.ModDbMessage;
				}
			}
		}
		return antiCheatModWebSnapshot;
	}

	private (int Count, long Bytes) GetEvidenceStats()
	{
		lock (evidenceLock)
		{
			return (Count: evidenceRecords.Count, Bytes: evidenceRecords.Sum((AntiCheatEvidencePackageWebSnapshot x) => Math.Max(0L, x.Bytes)));
		}
	}

	private List<AntiCheatEvidencePackageWebSnapshot> GetEvidenceForPlayer(string playerUid)
	{
		lock (evidenceLock)
		{
			return (from x in (from x in evidenceRecords
					where string.Equals(x.OwnerPlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)
					orderby x.CollectedUtc descending
					select x).Take(50)
				select new AntiCheatEvidencePackageWebSnapshot
				{
					FileName = x.FileName,
					ModId = x.ModId,
					Version = x.Version,
					OriginalFileName = x.OriginalFileName,
					OwnerPlayerUid = x.OwnerPlayerUid,
					OwnerPlayerName = x.OwnerPlayerName,
					CollectedUtc = x.CollectedUtc,
					AuthorizationUtc = x.AuthorizationUtc,
					AuthorizationMethod = x.AuthorizationMethod,
					DeclinePolicyAction = x.DeclinePolicyAction,
					DeclinePolicyHours = x.DeclinePolicyHours,
					RequestedBy = x.RequestedBy,
					TransferRequestId = x.TransferRequestId,
					Bytes = x.Bytes,
					Sha256 = x.Sha256,
					ExpectedPackageSha256 = x.ExpectedPackageSha256,
					GeneratedArchive = x.GeneratedArchive,
					OfficialDiffStatus = x.OfficialDiffStatus,
					OfficialDiffSummary = x.OfficialDiffSummary,
					OfficialDiffFileName = x.OfficialDiffFileName,
					OfficialDiffCandidateCount = x.OfficialDiffCandidateCount,
					OfficialDiffMatchedFiles = x.OfficialDiffMatchedFiles,
					OfficialDiffAddedFiles = x.OfficialDiffAddedFiles,
					OfficialDiffRemovedFiles = x.OfficialDiffRemovedFiles,
					OfficialDiffModifiedFiles = x.OfficialDiffModifiedFiles,
					OfficialDiffManifestComplete = x.OfficialDiffManifestComplete,
					OfficialDiffAnalyzedUtc = x.OfficialDiffAnalyzedUtc,
					OfficialDiffAddedPaths = (x.OfficialDiffAddedPaths ?? new List<string>()).ToList(),
					OfficialDiffRemovedPaths = (x.OfficialDiffRemovedPaths ?? new List<string>()).ToList(),
					OfficialDiffModifiedPaths = (x.OfficialDiffModifiedPaths ?? new List<string>()).ToList()
				}).ToList();
		}
	}

	public (bool Success, string Message) SaveEvidenceCollectionSettings(AntiCheatEvidenceSettingsSaveRequest request)
	{
		if (request == null)
		{
			return (Success: false, Message: "Evidence collection settings are required.");
		}
		bool suspectPackageCollectionEnabled = config.SuspectPackageCollectionEnabled;
		int suspectPackageCollectionMaxMb = config.SuspectPackageCollectionMaxMb;
		int suspectPackageCollectionTimeoutSeconds = config.SuspectPackageCollectionTimeoutSeconds;
		int suspectPackageUploadKbps = config.SuspectPackageUploadKbps;
		string suspectPackageDeclineAction = config.SuspectPackageDeclineAction;
		int suspectPackageDeclineTempBanHours = config.SuspectPackageDeclineTempBanHours;
		string text = (request.DeclineAction ?? "TempBan").Trim();
		if (!text.Equals("None", StringComparison.OrdinalIgnoreCase) && !text.Equals("Disconnect", StringComparison.OrdinalIgnoreCase) && !text.Equals("TempBan", StringComparison.OrdinalIgnoreCase))
		{
			return (Success: false, Message: "Decline action must be None, Disconnect, or TempBan.");
		}
		config.SuspectPackageCollectionEnabled = request.Enabled;
		config.SuspectPackageCollectionMaxMb = Math.Clamp(request.MaxMb, 8, 256);
		config.SuspectPackageCollectionTimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 30, 300);
		config.SuspectPackageUploadKbps = Math.Clamp(request.UploadKbps, 128, 4096);
		AntiCheatConfig antiCheatConfig = config;
		string suspectPackageDeclineAction2;
		if (text.Equals("None", StringComparison.OrdinalIgnoreCase))
		{
			suspectPackageDeclineAction2 = "None";
		}
		else
		{
			suspectPackageDeclineAction2 = (text.Equals("Disconnect", StringComparison.OrdinalIgnoreCase) ? "Disconnect" : "TempBan");
		}
		antiCheatConfig.SuspectPackageDeclineAction = suspectPackageDeclineAction2;
		config.SuspectPackageDeclineTempBanHours = Math.Clamp(request.DeclineTempBanHours, 1, 720);
		if (!TrySaveConfig(out string error))
		{
			config.SuspectPackageCollectionEnabled = suspectPackageCollectionEnabled;
			config.SuspectPackageCollectionMaxMb = suspectPackageCollectionMaxMb;
			config.SuspectPackageCollectionTimeoutSeconds = suspectPackageCollectionTimeoutSeconds;
			config.SuspectPackageUploadKbps = suspectPackageUploadKbps;
			config.SuspectPackageDeclineAction = suspectPackageDeclineAction;
			config.SuspectPackageDeclineTempBanHours = suspectPackageDeclineTempBanHours;
			return (Success: false, Message: error);
		}
		string value = ((config.SuspectPackageDeclineAction == "TempBan") ? $"TempBan {config.SuspectPackageDeclineTempBanHours}h" : config.SuspectPackageDeclineAction);
		return (Success: true, Message: $"Suspicious package collection {(config.SuspectPackageCollectionEnabled ? "enabled" : "disabled")}; max {config.SuspectPackageCollectionMaxMb} MB; upload cap {config.SuspectPackageUploadKbps} KB/s; decline policy {value}.");
	}

	public (bool Success, string Message) RequestSuspiciousModPackage(string uidOrName, string modId, string requestedBy)
	{
		if (!config.SuspectPackageCollectionEnabled)
		{
			return (Success: false, Message: "Suspicious package collection is disabled in Anti-Cheat settings.");
		}
		if (!config.Enabled || !runtimeAvailable || channel == null)
		{
			return (Success: false, Message: "Anti-cheat is disabled or unavailable.");
		}
		if (string.IsNullOrWhiteSpace(uidOrName) || string.IsNullOrWhiteSpace(modId))
		{
			return (Success: false, Message: "Player and mod ID are required.");
		}
		IServerPlayer player = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerUID.Equals(uidOrName, StringComparison.OrdinalIgnoreCase) || p.PlayerName.Equals(uidOrName, StringComparison.OrdinalIgnoreCase));
		if (player == null)
		{
			return (Success: false, Message: "Player is not online.");
		}
		if (!sessions.TryGet(player.PlayerUID, out AntiCheatSession session) || !session.ClientInstalled)
		{
			return (Success: false, Message: "Client Integrity has not completed its handshake for this player.");
		}
		if (session.ProtocolVersion != 13)
		{
			return (Success: false, Message: "Player Client Integrity protocol does not match the server.");
		}
		ClientModDto mod = session.LastMods.FirstOrDefault((ClientModDto m) => string.Equals((m.ModId ?? "").Trim(), modId.Trim(), StringComparison.OrdinalIgnoreCase));
		if (mod == null)
		{
			return (Success: false, Message: "That mod is not present in the player's most recent accepted mod inventory.");
		}
		List<AntiCheatEvidenceCandidateWebSnapshot> source = BuildEvidenceCandidates(session);
		AntiCheatEvidenceCandidateWebSnapshot antiCheatEvidenceCandidateWebSnapshot = source.FirstOrDefault((AntiCheatEvidenceCandidateWebSnapshot c) => string.Equals(c.ModId, mod.ModId, StringComparison.OrdinalIgnoreCase));
		if (antiCheatEvidenceCandidateWebSnapshot == null)
		{
			return (Success: false, Message: "That mod is not currently associated with suspicious Anti-Cheat evidence for this player.");
		}
		long num = (long)config.SuspectPackageCollectionMaxMb * 1024L * 1024;
		if (mod.PackageBytes > num && mod.PackageBytes > 0)
		{
			return (Success: false, Message: $"Reported package size is {mod.PackageBytes} bytes, above the configured {config.SuspectPackageCollectionMaxMb} MB evidence limit.");
		}
		lock (evidenceLock)
		{
			if (evidenceTransfers.Values.Any((EvidenceTransferState x) => string.Equals(x.PlayerUid, player.PlayerUID, StringComparison.OrdinalIgnoreCase)))
			{
				return (Success: false, Message: "An evidence package transfer is already in progress for this player.");
			}
			if (evidenceTransfers.Count >= 2)
			{
				return (Success: false, Message: $"The server is already collecting {2} suspicious packages. Try again after one finishes.");
			}
			string text = Guid.NewGuid().ToString("N");
			evidenceTransfers[text] = new EvidenceTransferState
			{
				RequestId = text,
				PlayerUid = player.PlayerUID,
				PlayerName = player.PlayerName,
				ModId = (mod.ModId ?? modId),
				ModVersion = (mod.Version ?? ""),
				ExpectedFileName = (mod.FileName ?? ""),
				ExpectedPackageSha256 = NormalizeSha256(mod.Sha256),
				RequestedBy = (string.IsNullOrWhiteSpace(requestedBy) ? "WebPanel administrator" : requestedBy.Trim()),
				DeclineAction = config.SuspectPackageDeclineAction,
				DeclineTempBanHours = config.SuspectPackageDeclineTempBanHours,
				RequestedUtc = NowMs()
			};
			channel.SendPacket(new ServerModEvidenceRequestPacket
			{
				ProtocolVersion = 13,
				RequestId = text,
				ModId = (mod.ModId ?? modId),
				ExpectedFileName = (mod.FileName ?? ""),
				ExpectedPackageSha256 = NormalizeSha256(mod.Sha256),
				MaxBytes = num,
				MaxUploadKbps = config.SuspectPackageUploadKbps,
				ModVersion = (mod.Version ?? ""),
				DeclineTempBanHours = config.SuspectPackageDeclineTempBanHours,
				DeclineAction = config.SuspectPackageDeclineAction,
				RequestedBy = (string.IsNullOrWhiteSpace(requestedBy) ? "WebPanel administrator" : requestedBy.Trim()),
				ReviewReason = (antiCheatEvidenceCandidateWebSnapshot.Reason ?? "Suspicious Anti-Cheat evidence")
			}, player);
		}
		string value;
		if (config.SuspectPackageDeclineAction == "TempBan")
		{
			value = $"{config.SuspectPackageDeclineTempBanHours}-hour temporary ban";
		}
		else
		{
			value = ((config.SuspectPackageDeclineAction == "Disconnect") ? "disconnect" : "no automatic punishment");
		}
		return (Success: true, Message: $"Manual Collect ZIP request sent to {player.PlayerName} for '{mod.ModId}'. The popup requires explicit player approval before any package bytes are prepared or transferred. Decline policy: {value}.");
	}

	private void OnEvidenceConsent(IServerPlayer player, ClientModEvidenceConsentPacket packet)
	{
		if (player == null || packet == null || packet.ProtocolVersion != 13)
		{
			return;
		}
		EvidenceTransferState evidenceTransferState = null;
		EvidenceTransferState evidenceTransferState2 = null;
		lock (evidenceLock)
		{
			if (!evidenceTransfers.TryGetValue(packet.RequestId ?? "", out EvidenceTransferState value) || !string.Equals(value.PlayerUid, player.PlayerUID, StringComparison.OrdinalIgnoreCase) || value.ConsentRespondedUtc > 0)
			{
				return;
			}
			value.ConsentRespondedUtc = NowMs();
			value.ConsentGranted = packet.Accepted;
			AdminService adminService = adminProvider();
			if (packet.Accepted)
			{
				adminService?.Audit("anticheat", "mod-transfer-authorized", value.PlayerUid, value.PlayerName, $"Player authorized transfer of loaded mod package {value.ModId} {value.ModVersion}. Request={value.RequestId}", "anticheat");
				evidenceTransferState = value;
			}
			else
			{
				adminService?.Audit("anticheat", "mod-transfer-declined", value.PlayerUid, value.PlayerName, $"Player declined transfer of loaded mod package {value.ModId} {value.ModVersion}. Request={value.RequestId}; requestedBy={value.RequestedBy}; policy={value.DeclineAction}{((value.DeclineAction == "TempBan") ? $" {value.DeclineTempBanHours}h" : "")}", "anticheat");
				evidenceTransferState2 = value;
				AbortEvidenceTransferLocked(value, "Player declined the mod package transfer request.");
			}
		}
		if (evidenceTransferState != null)
		{
			channel?.SendPacket(new ServerModEvidenceConsentAckPacket
			{
				ProtocolVersion = 13,
				RequestId = evidenceTransferState.RequestId,
				Authorized = true,
				Message = "Player authorization recorded; this single requested mod package may now be transferred."
			}, player);
			api.Logger.Notification("[ServerEssentials/AntiCheat] Player {0} explicitly authorized mod package transfer. Mod={1} Request={2}", evidenceTransferState.PlayerName, evidenceTransferState.ModId, evidenceTransferState.RequestId);
		}
		else
		{
			if (evidenceTransferState2 == null)
			{
				return;
			}
			AdminService adminService2 = adminProvider();
			string reason = "Declined the server's Client Integrity mod package transfer request for '" + evidenceTransferState2.ModId + "'.";
			string text = evidenceTransferState2.DeclineAction ?? "TempBan";
			if (text.Equals("None", StringComparison.OrdinalIgnoreCase))
			{
				api.Logger.Notification("[ServerEssentials/AntiCheat] Mod transfer declined by {0}; configured decline policy is None, so no automatic punishment was applied.", evidenceTransferState2.PlayerName);
				return;
			}
			if (text.Equals("Disconnect", StringComparison.OrdinalIgnoreCase))
			{
				if (adminService2 != null)
				{
					string text2 = adminService2.Kick("anticheat", evidenceTransferState2.PlayerUid, reason, "anticheat-consent");
					api.Logger.Warning("[ServerEssentials/AntiCheat] Mod transfer declined by {0}; applied disconnect policy. Result: {1}", evidenceTransferState2.PlayerName, text2);
					return;
				}
				try
				{
					player.Disconnect("Mod package transfer declined. Server policy requires disconnection.");
					return;
				}
				catch
				{
					return;
				}
			}
			int num = Math.Clamp(evidenceTransferState2.DeclineTempBanHours, 1, 720);
			if (adminService2 == null)
			{
				api.Logger.Error("[ServerEssentials/AntiCheat] Player {0} declined a mod package transfer but AdminService was unavailable; disconnecting without a persisted temp ban.", evidenceTransferState2.PlayerName);
				try
				{
					player.Disconnect($"Mod package transfer declined. Server policy requires a {num}-hour temporary ban, but the ban service is currently unavailable.");
					return;
				}
				catch
				{
					return;
				}
			}
			string text3 = adminService2.TempBan("anticheat", evidenceTransferState2.PlayerUid, $"{num}h", reason, "anticheat-consent");
			api.Logger.Warning("[ServerEssentials/AntiCheat] Mod transfer declined by {0}; applied {1}-hour temporary-ban policy. Result: {2}", evidenceTransferState2.PlayerName, num, text3);
		}
	}

	private void OnEvidenceStart(IServerPlayer player, ClientModEvidenceStartPacket packet)
	{
		if (player == null || packet == null || packet.ProtocolVersion != 13)
		{
			return;
		}
		lock (evidenceLock)
		{
			if (!evidenceTransfers.TryGetValue(packet.RequestId ?? "", out EvidenceTransferState value) || !string.Equals(value.PlayerUid, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			try
			{
				if (value.Writer != null)
				{
					throw new InvalidOperationException("Evidence transfer already started.");
				}
				if (!value.ConsentGranted || value.ConsentRespondedUtc <= 0)
				{
					throw new InvalidOperationException("Player authorization has not been granted for this transfer request.");
				}
				long num = (long)config.SuspectPackageCollectionMaxMb * 1024L * 1024;
				if (packet.TotalBytes <= 0 || packet.TotalBytes > num)
				{
					throw new InvalidOperationException($"Client package size {packet.TotalBytes} is outside the configured evidence limit.");
				}
				if (!string.Equals(packet.ModId ?? "", value.ModId, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidOperationException("Client evidence mod ID did not match the requested mod.");
				}
				if (packet.TotalChunks <= 0 || packet.TotalChunks > 10000 || packet.ChunkSize <= 0 || packet.ChunkSize > 65536)
				{
					throw new InvalidOperationException("Client evidence chunk metadata is invalid.");
				}
				string evidenceRoot = GetEvidenceRoot();
				string value2 = SanitizeEvidenceFilePart(value.PlayerName, 40, "player");
				string text = SanitizeEvidenceFilePart(value.ModId, 48, "mod");
				string value3 = SanitizeEvidenceFilePart(Path.GetFileNameWithoutExtension(packet.OriginalFileName ?? value.ExpectedFileName), 64, text);
				string value4 = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
				string text2 = $"{value3}__{text}__{value4}__{value.RequestId.Substring(0, 8)}__owner-{value2}";
				string text3 = UniqueEvidencePath(Path.Combine(evidenceRoot, text2 + ".zip"));
				string text4 = text3 + ".part";
				value.OriginalFileName = Path.GetFileName(packet.OriginalFileName ?? value.ExpectedFileName);
				value.TotalBytes = packet.TotalBytes;
				value.ReportedSha256 = NormalizeSha256(packet.Sha256);
				value.GeneratedArchive = packet.GeneratedArchive;
				value.TotalChunks = packet.TotalChunks;
				value.FinalPath = text3;
				value.TempPath = text4;
				value.Writer = new EvidenceFileWriter(text4);
				value.StartedUtc = NowMs();
			}
			catch (Exception ex)
			{
				AbortEvidenceTransferLocked(value, "Evidence transfer start rejected: " + ex.Message);
			}
		}
	}

	private void OnEvidenceChunk(IServerPlayer player, ClientModEvidenceChunkPacket packet)
	{
		if (player == null || packet == null || packet.ProtocolVersion != 13)
		{
			return;
		}
		lock (evidenceLock)
		{
			if (!evidenceTransfers.TryGetValue(packet.RequestId ?? "", out EvidenceTransferState value) || !string.Equals(value.PlayerUid, player.PlayerUID, StringComparison.OrdinalIgnoreCase) || value.Writer == null || value.Finalizing)
			{
				return;
			}
			try
			{
				if (packet.ChunkIndex != value.NextChunk)
				{
					throw new InvalidOperationException($"Out-of-order evidence chunk {packet.ChunkIndex}; expected {value.NextChunk}.");
				}
				byte[] array = packet.Data ?? Array.Empty<byte>();
				if (array.Length == 0 || array.Length > 65536)
				{
					throw new InvalidOperationException("Evidence chunk length is invalid.");
				}
				if (value.ReceivedBytes + array.Length > value.TotalBytes)
				{
					throw new InvalidOperationException("Evidence upload exceeded its declared byte length.");
				}
				if (value.NextChunk >= value.TotalChunks)
				{
					throw new InvalidOperationException("Evidence upload exceeded its declared chunk count.");
				}
				if (!value.Writer.TryWrite(array))
				{
					throw new InvalidOperationException("Evidence disk queue is full or failed; retry the transfer later.");
				}
				value.ReceivedBytes += array.Length;
				value.NextChunk++;
			}
			catch (Exception ex)
			{
				AbortEvidenceTransferLocked(value, "Evidence transfer failed: " + ex.Message);
			}
		}
	}

	private void OnEvidenceComplete(IServerPlayer player, ClientModEvidenceCompletePacket packet)
	{
		if (player == null || packet == null || packet.ProtocolVersion != 13)
		{
			return;
		}
		lock (evidenceLock)
		{
			if (!evidenceTransfers.TryGetValue(packet.RequestId ?? "", out EvidenceTransferState state) || !string.Equals(state.PlayerUid, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			if (!packet.Success)
			{
				AbortEvidenceTransferLocked(state, "Client could not provide evidence package: " + (packet.Message ?? "unknown error"));
				return;
			}
			try
			{
				if (state.Writer == null)
				{
					throw new InvalidOperationException("Evidence transfer did not start.");
				}
				if (state.ReceivedBytes != state.TotalBytes || packet.TotalBytes != state.TotalBytes)
				{
					throw new InvalidOperationException($"Evidence byte count mismatch ({state.ReceivedBytes}/{state.TotalBytes}).");
				}
				if (state.NextChunk != state.TotalChunks)
				{
					throw new InvalidOperationException($"Evidence chunk count mismatch ({state.NextChunk}/{state.TotalChunks}).");
				}
				if (!state.Finalizing)
				{
					state.Finalizing = true;
					string completionHash = NormalizeSha256(packet.Sha256);
					Task.Run(() => FinalizeEvidenceAsync(state, completionHash));
				}
			}
			catch (Exception ex)
			{
				AbortEvidenceTransferLocked(state, "Evidence finalization failed: " + ex.Message);
			}
		}
	}

	private async Task FinalizeEvidenceAsync(EvidenceTransferState state, string completionHash)
	{
		try
		{
			string sha = await state.Writer.CompleteAsync().ConfigureAwait(continueOnCapturedContext: false);
			if (state.Writer.IsAborted)
			{
				return;
			}
			if (state.ReportedSha256.Length == 64 && !string.Equals(sha, state.ReportedSha256, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Evidence SHA-256 did not match the client's start metadata.");
			}
			if (completionHash.Length == 64 && !string.Equals(sha, completionHash, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Evidence SHA-256 did not match the client's completion metadata.");
			}
			File.Move(state.TempPath, state.FinalPath, overwrite: false);
			AntiCheatEvidencePackageWebSnapshot record = new AntiCheatEvidencePackageWebSnapshot
			{
				FileName = Path.GetFileName(state.FinalPath),
				ModId = state.ModId,
				Version = state.ModVersion,
				OriginalFileName = state.OriginalFileName,
				OwnerPlayerUid = state.PlayerUid,
				OwnerPlayerName = state.PlayerName,
				CollectedUtc = NowMs(),
				AuthorizationUtc = state.ConsentRespondedUtc,
				AuthorizationMethod = "InGamePopup",
				DeclinePolicyAction = state.DeclineAction,
				DeclinePolicyHours = state.DeclineTempBanHours,
				RequestedBy = state.RequestedBy,
				TransferRequestId = state.RequestId,
				Bytes = state.TotalBytes,
				Sha256 = sha,
				ExpectedPackageSha256 = NormalizeSha256(state.ExpectedPackageSha256),
				GeneratedArchive = state.GeneratedArchive
			};
			WriteEvidenceMetadata(state.FinalPath, record);
			api.Event.EnqueueMainThreadTask(() =>
			{
				lock (evidenceLock)
				{
					if (!evidenceTransfers.TryGetValue(state.RequestId, out EvidenceTransferState value) || value != state)
					{
						return;
					}
					evidenceRecords.Add(record);
					evidenceTransfers.Remove(state.RequestId);
				}
				api.Logger.Warning("[ServerEssentials/AntiCheat] Collected suspicious mod package {0} from {1} ({2}), {3} bytes, SHA-256 {4}.", record.ModId, record.OwnerPlayerName, record.OwnerPlayerUid, record.Bytes, record.Sha256);
			}, "anticheat-evidence-finalized");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			api.Event.EnqueueMainThreadTask(() =>
			{
				lock (evidenceLock)
				{
					if (evidenceTransfers.TryGetValue(state.RequestId, out EvidenceTransferState value) && value == state)
					{
						AbortEvidenceTransferLocked(state, "Evidence finalization failed: " + ex3.Message);
					}
				}
			}, "anticheat-evidence-failed");
		}
	}

	private string GetEvidenceRoot()
	{
		string text = Path.Combine(SuiteConfigStorage.GetConfigRoot(api), "SuspectedHacks");
		Directory.CreateDirectory(text);
		return text;
	}

	private void LoadEvidenceMetadata()
	{
		lock (evidenceLock)
		{
			evidenceRecords.Clear();
			try
			{
				string evidenceRoot = GetEvidenceRoot();
				foreach (string item in Directory.EnumerateFiles(evidenceRoot, "*.zip.json", SearchOption.TopDirectoryOnly).Take(5000))
				{
					try
					{
						string text = item;
						string path = text.Substring(0, text.Length - 5);
						if (!File.Exists(path))
						{
							continue;
						}
						AntiCheatEvidencePackageWebSnapshot antiCheatEvidencePackageWebSnapshot = JsonSerializer.Deserialize<AntiCheatEvidencePackageWebSnapshot>(File.ReadAllText(item), new JsonSerializerOptions
						{
							PropertyNameCaseInsensitive = true
						});
						if (antiCheatEvidencePackageWebSnapshot != null && !string.IsNullOrWhiteSpace(antiCheatEvidencePackageWebSnapshot.FileName))
						{
							AntiCheatEvidencePackageWebSnapshot antiCheatEvidencePackageWebSnapshot2 = antiCheatEvidencePackageWebSnapshot;
							if (antiCheatEvidencePackageWebSnapshot2.OfficialDiffAddedPaths == null)
							{
								List<string> list = (antiCheatEvidencePackageWebSnapshot2.OfficialDiffAddedPaths = new List<string>());
							}
							antiCheatEvidencePackageWebSnapshot2 = antiCheatEvidencePackageWebSnapshot;
							if (antiCheatEvidencePackageWebSnapshot2.OfficialDiffRemovedPaths == null)
							{
								List<string> list = (antiCheatEvidencePackageWebSnapshot2.OfficialDiffRemovedPaths = new List<string>());
							}
							antiCheatEvidencePackageWebSnapshot2 = antiCheatEvidencePackageWebSnapshot;
							if (antiCheatEvidencePackageWebSnapshot2.OfficialDiffModifiedPaths == null)
							{
								List<string> list = (antiCheatEvidencePackageWebSnapshot2.OfficialDiffModifiedPaths = new List<string>());
							}
							evidenceRecords.Add(antiCheatEvidencePackageWebSnapshot);
						}
					}
					catch
					{
					}
				}
			}
			catch (Exception ex)
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat] Could not enumerate SuspectedHacks metadata: {0}", ex.Message);
			}
		}
	}

	private void CleanupExpiredEvidencePackages(long now, bool logSummary)
	{
		int num = 0;
		long num2 = 0L;
		try
		{
			string evidenceRoot = GetEvidenceRoot();
			string b = Path.GetFullPath(evidenceRoot).TrimEnd(new char[2]
			{
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar
			});
			long num3 = now - (long)TimeSpan.FromDays(15).TotalMilliseconds;
			foreach (string item in Directory.EnumerateFiles(evidenceRoot, "*.zip", SearchOption.TopDirectoryOnly).Take(10000))
			{
				try
				{
					string fullPath = Path.GetFullPath(item);
					string a = Path.GetDirectoryName(fullPath)?.TrimEnd(new char[2]
					{
						Path.DirectorySeparatorChar,
						Path.AltDirectorySeparatorChar
					});
					if (!string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
					{
						continue;
					}
					long num4 = 0L;
					string path = fullPath + ".json";
					if (File.Exists(path))
					{
						try
						{
							num4 = JsonSerializer.Deserialize<AntiCheatEvidencePackageWebSnapshot>(File.ReadAllText(path), new JsonSerializerOptions
							{
								PropertyNameCaseInsensitive = true
							})?.CollectedUtc ?? 0;
						}
						catch
						{
						}
					}
					if (num4 <= 0)
					{
						num4 = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)).ToUnixTimeMilliseconds();
					}
					if (num4 <= 0 || num4 > num3)
					{
						continue;
					}
					long val = 0L;
					try
					{
						val = new FileInfo(fullPath).Length;
					}
					catch
					{
					}
					File.Delete(fullPath);
					num++;
					num2 += Math.Max(0L, val);
					string deletedName = Path.GetFileName(fullPath);
					lock (evidenceLock)
					{
						evidenceRecords.RemoveAll((AntiCheatEvidencePackageWebSnapshot x) => string.Equals(x.FileName, deletedName, StringComparison.OrdinalIgnoreCase));
					}
				}
				catch (Exception ex)
				{
					api.Logger.Warning("[ServerEssentials/AntiCheat] Could not delete expired suspicious package '{0}': {1}", Path.GetFileName(item), ex.Message);
				}
			}
			if (logSummary && num > 0)
			{
				api.Logger.Notification("[ServerEssentials/AntiCheat] Evidence retention cleanup deleted {0} suspicious mod ZIP(s) older than {1} days ({2} bytes). Authorization/provenance JSON sidecars were retained.", num, 15, num2);
			}
		}
		catch (Exception ex2)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Evidence retention cleanup failed: {0}", ex2.Message);
		}
	}

	private void WriteEvidenceMetadata(string zipPath, AntiCheatEvidencePackageWebSnapshot record)
	{
		try
		{
			File.WriteAllText(zipPath + ".json", JsonSerializer.Serialize(record, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Evidence package saved but metadata sidecar failed: {0}", ex.Message);
		}
	}

	public (bool Success, string Message) AnalyzeEvidencePackageAgainstModDb(string fileName)
	{
		string safeName = Path.GetFileName(fileName ?? "");
		if (safeName.Length == 0 || !safeName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			return (Success: false, Message: "A saved evidence ZIP filename is required.");
		}
		AntiCheatEvidencePackageWebSnapshot antiCheatEvidencePackageWebSnapshot;
		lock (evidenceLock)
		{
			antiCheatEvidencePackageWebSnapshot = evidenceRecords.FirstOrDefault((AntiCheatEvidencePackageWebSnapshot x) => string.Equals(x.FileName, safeName, StringComparison.OrdinalIgnoreCase));
		}
		if (antiCheatEvidencePackageWebSnapshot == null)
		{
			return (Success: false, Message: "Evidence package metadata was not found.");
		}
		if (string.IsNullOrWhiteSpace(antiCheatEvidencePackageWebSnapshot.Version))
		{
			return (Success: false, Message: "This older evidence record has no mod version metadata. Collect the package again before exact-version comparison.");
		}
		AntiCheatModDbVerificationCacheEntry modDbVerification = GetModDbVerification(antiCheatEvidencePackageWebSnapshot.ModId, antiCheatEvidencePackageWebSnapshot.Version);
		List<AntiCheatModDbReleaseFingerprintResolution> modDbReleaseCandidates = GetModDbReleaseCandidates(modDbVerification);
		if (modDbVerification == null || !IsFreshModDbVerification(modDbVerification, NowMs()) || !string.Equals(modDbVerification.Status, "Resolved", StringComparison.OrdinalIgnoreCase) || modDbReleaseCandidates.Count == 0)
		{
			return (Success: false, Message: "Fresh exact-version ModDB fingerprints are required first. Use Resolve All DB Fingerprints (force refresh if needed), then compare again.");
		}
		List<AntiCheatModDbReleaseFingerprintResolution> list = modDbReleaseCandidates.Where((AntiCheatModDbReleaseFingerprintResolution x) => x.ManifestComplete && (x.Files?.Count ?? 0) > 0).ToList();
		if (list.Count == 0)
		{
			return (Success: false, Message: "The cached official fingerprints do not contain complete file manifests. Force-refresh the ModDB fingerprint, then compare again.");
		}
		string zipPath = Path.Combine(GetEvidenceRoot(), safeName);
		AntiCheatModDbPackageManifestResolution antiCheatModDbPackageManifestResolution;
		try
		{
			antiCheatModDbPackageManifestResolution = AntiCheatModDbFingerprintResolver.FingerprintLocalPackageManifest(zipPath);
		}
		catch (Exception ex)
		{
			return (Success: false, Message: "Could not fingerprint the saved evidence ZIP: " + ex.Message);
		}
		if (!antiCheatModDbPackageManifestResolution.ManifestComplete)
		{
			return (Success: false, Message: "The evidence package contains too many files or duplicate archive paths for a complete safe manifest comparison.");
		}
		AntiCheatModDbReleaseFingerprintResolution antiCheatModDbReleaseFingerprintResolution = null;
		(int, List<string>, List<string>, List<string>) tuple = (0, new List<string>(), new List<string>(), new List<string>());
		int num = int.MaxValue;
		foreach (AntiCheatModDbReleaseFingerprintResolution item5 in list)
		{
			(int, List<string>, List<string>, List<string>) tuple2 = Diff(antiCheatModDbPackageManifestResolution, item5);
			int num2 = tuple2.Item2.Count + tuple2.Item3.Count + tuple2.Item4.Count;
			if (antiCheatModDbReleaseFingerprintResolution == null || num2 < num || (num2 == num && tuple2.Item1 > tuple.Item1))
			{
				antiCheatModDbReleaseFingerprintResolution = item5;
				tuple = tuple2;
				num = num2;
			}
		}
		if (antiCheatModDbReleaseFingerprintResolution == null)
		{
			return (Success: false, Message: "No complete official manifest was available for comparison.");
		}
		string officialDiffStatus = ((num == 0) ? "OfficialMatch" : "Differences");
		string text = ((num == 0) ? ("Captured package content matches official ModDB upload '" + antiCheatModDbReleaseFingerprintResolution.FileName + "'.") : $"Best official match '{antiCheatModDbReleaseFingerprintResolution.FileName}': {tuple.Item4.Count} modified, {tuple.Item2.Count} added, {tuple.Item3.Count} removed file(s).");
		lock (evidenceLock)
		{
			AntiCheatEvidencePackageWebSnapshot antiCheatEvidencePackageWebSnapshot2 = evidenceRecords.FirstOrDefault((AntiCheatEvidencePackageWebSnapshot x) => string.Equals(x.FileName, safeName, StringComparison.OrdinalIgnoreCase));
			if (antiCheatEvidencePackageWebSnapshot2 == null)
			{
				return (Success: false, Message: "Evidence package disappeared during comparison.");
			}
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffStatus = officialDiffStatus;
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffSummary = text;
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffFileName = antiCheatModDbReleaseFingerprintResolution.FileName;
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffCandidateCount = list.Count;
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffMatchedFiles = tuple.Item1;
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffAddedFiles = tuple.Item2.Count;
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffRemovedFiles = tuple.Item3.Count;
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffModifiedFiles = tuple.Item4.Count;
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffManifestComplete = antiCheatModDbPackageManifestResolution.ManifestComplete && antiCheatModDbReleaseFingerprintResolution.ManifestComplete;
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffAnalyzedUtc = NowMs();
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffAddedPaths = tuple.Item2.Take(40).ToList();
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffRemovedPaths = tuple.Item3.Take(40).ToList();
			antiCheatEvidencePackageWebSnapshot2.OfficialDiffModifiedPaths = tuple.Item4.Take(40).ToList();
			WriteEvidenceMetadata(zipPath, antiCheatEvidencePackageWebSnapshot2);
		}
		return (Success: true, Message: text);
		static (int Matched, List<string> Added, List<string> Removed, List<string> Modified) Diff(AntiCheatModDbPackageManifestResolution client, AntiCheatModDbReleaseFingerprintResolution official)
		{
			Dictionary<string, AntiCheatModDbFileFingerprintResolution> c = (client.Files ?? new List<AntiCheatModDbFileFingerprintResolution>()).ToDictionary((AntiCheatModDbFileFingerprintResolution x) => x.Path, (AntiCheatModDbFileFingerprintResolution x) => x, StringComparer.Ordinal);
			Dictionary<string, AntiCheatModDbFileFingerprintResolution> o = (official.Files ?? new List<AntiCheatModDbFileFingerprintResolution>()).ToDictionary((AntiCheatModDbFileFingerprintResolution x) => x.Path, (AntiCheatModDbFileFingerprintResolution x) => x, StringComparer.Ordinal);
			List<string> item = c.Keys.Where((string x) => !o.ContainsKey(x)).OrderBy((string x) => x, StringComparer.Ordinal).ToList();
			List<string> item2 = o.Keys.Where((string x) => !c.ContainsKey(x)).OrderBy((string x) => x, StringComparer.Ordinal).ToList();
			List<string> item3 = (from x in c.Keys.Where(o.ContainsKey)
				where !string.Equals(c[x].Sha256, o[x].Sha256, StringComparison.OrdinalIgnoreCase)
				select x).OrderBy((string x) => x, StringComparer.Ordinal).ToList();
			int item4 = c.Keys.Count((string x) => o.TryGetValue(x, out var value) && string.Equals(c[x].Sha256, value.Sha256, StringComparison.OrdinalIgnoreCase));
			return (Matched: item4, Added: item, Removed: item2, Modified: item3);
		}
	}

	private List<AntiCheatEvidenceCandidateWebSnapshot> BuildEvidenceCandidates(AntiCheatSession session)
	{
		List<AntiCheatEvidenceCandidateWebSnapshot> list = new List<AntiCheatEvidenceCandidateWebSnapshot>();
		List<AntiCheatEvidencePackageWebSnapshot> source;
		lock (evidenceLock)
		{
			source = evidenceRecords.Where((AntiCheatEvidencePackageWebSnapshot x) => string.Equals(x.OwnerPlayerUid, session.PlayerUid, StringComparison.OrdinalIgnoreCase)).ToList();
		}
		foreach (ClientModDto mod in session.LastMods.Where((ClientModDto m) => m != null && !string.IsNullOrWhiteSpace(m.ModId)))
		{
			string id = (mod.ModId ?? "").Trim();
			bool flag = string.Equals(id, "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase);
			bool flag2 = flag;
			if (!flag2)
			{
				bool flag3;
				switch (id)
				{
				case "game":
				case "survival":
				case "creative":
					flag3 = true;
					break;
				default:
					flag3 = false;
					break;
				}
				flag2 = flag3;
			}
			if (flag2)
			{
				continue;
			}
			List<ScanFindingDto> list2 = session.Findings.Where((ScanFindingDto f) => IsEvidenceCaptureFinding(f) && FindingReferencesMod(f, mod)).ToList();
			bool flag4 = config.ForbiddenModIds.Any((string x) => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
			if (!flag4 && list2.Count == 0)
			{
				continue;
			}
			AntiCheatEvidencePackageWebSnapshot antiCheatEvidencePackageWebSnapshot = (from x in source
				where string.Equals(x.ModId, id, StringComparison.OrdinalIgnoreCase)
				orderby x.CollectedUtc descending
				select x).FirstOrDefault();
			int severity = ((list2.Count == 0) ? 4 : list2.Max((ScanFindingDto x) => x.Severity));
			string reason = (flag4 ? "Blacklisted mod ID" : string.Join(", ", list2.Select((ScanFindingDto x) => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Take(4)));
			bool transferInProgress;
			lock (evidenceLock)
			{
				transferInProgress = evidenceTransfers.Values.Any((EvidenceTransferState x) => string.Equals(x.PlayerUid, session.PlayerUid, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ModId, id, StringComparison.OrdinalIgnoreCase));
			}
			list.Add(new AntiCheatEvidenceCandidateWebSnapshot
			{
				ModId = id,
				Version = (mod.Version ?? ""),
				FileName = (mod.FileName ?? ""),
				PackageBytes = Math.Max(0L, mod.PackageBytes),
				DllCount = Math.Max(0, mod.DllCount),
				DllBytes = Math.Max(0L, mod.DllBytes),
				Reason = reason,
				Severity = severity,
				AlreadyCollected = (antiCheatEvidencePackageWebSnapshot != null),
				TransferInProgress = transferInProgress,
				LastCollectedFile = (antiCheatEvidencePackageWebSnapshot?.FileName ?? "")
			});
		}
		return list.OrderByDescending((AntiCheatEvidenceCandidateWebSnapshot x) => x.Severity).ThenBy((AntiCheatEvidenceCandidateWebSnapshot x) => x.ModId, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool FindingReferencesMod(ScanFindingDto finding, ClientModDto mod)
	{
		string text = (mod.ModId ?? "").Trim();
		string text2 = (mod.FileName ?? "").Trim();
		string a = finding.Subject ?? "";
		string text3 = finding.Evidence ?? "";
		if (text.Length > 0 && (string.Equals(a, text, StringComparison.OrdinalIgnoreCase) || text3.Contains("modId=" + text, StringComparison.OrdinalIgnoreCase) || text3.Contains("blacklisted-id=" + text, StringComparison.OrdinalIgnoreCase) || text3.Contains(text, StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}
		return text2.Length > 0 && (string.Equals(a, text2, StringComparison.OrdinalIgnoreCase) || text3.Contains(text2, StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsEvidenceCaptureFinding(ScanFindingDto finding)
	{
		if (finding == null || finding.Severity <= 0)
		{
			return false;
		}
		string text = (finding.Code ?? "").Trim();
		if (text.Length == 0)
		{
			return false;
		}
		if (text.Equals("HOTKEY_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("CLIENT_COMMAND_BLACKLIST_MATCH", StringComparison.OrdinalIgnoreCase) || text.Equals("HOTKEY_SUSPICIOUS_NAME", StringComparison.OrdinalIgnoreCase) || text.Equals("CLIENT_COMMAND_SUSPICIOUS", StringComparison.OrdinalIgnoreCase) || text.Equals("RUNTIME_BLACKLIST_ALIAS_IGNORED", StringComparison.OrdinalIgnoreCase) || text.Equals("PROTECTED_ASSET_BASELINE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return true;
	}

	private void CleanupEvidenceTransfers(long now)
	{
		lock (evidenceLock)
		{
			long val = (long)config.SuspectPackageCollectionTimeoutSeconds * 1000L;
			long num = (long)Math.Max(128, config.SuspectPackageUploadKbps) * 1024L;
			foreach (EvidenceTransferState item in evidenceTransfers.Values.ToList())
			{
				long num2 = ((item.TotalBytes > 0) ? ((long)Math.Ceiling((double)item.TotalBytes * 1000.0 / (double)num)) : 0);
				long num3 = Math.Max(val, Math.Min(900000L, num2 * 2 + 30000));
				if (now - ((item.StartedUtc > 0) ? item.StartedUtc : item.RequestedUtc) > num3)
				{
					AbortEvidenceTransferLocked(item, "Evidence transfer timed out.");
				}
			}
		}
	}

	private void AbortEvidenceForPlayer(string playerUid)
	{
		lock (evidenceLock)
		{
			foreach (EvidenceTransferState item in evidenceTransfers.Values.Where((EvidenceTransferState x) => string.Equals(x.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)).ToList())
			{
				AbortEvidenceTransferLocked(item, "Player disconnected during evidence transfer.");
			}
		}
	}

	private void AbortAllEvidenceTransfers(string reason)
	{
		lock (evidenceLock)
		{
			foreach (EvidenceTransferState item in evidenceTransfers.Values.ToList())
			{
				AbortEvidenceTransferLocked(item, reason);
			}
		}
	}

	private void AbortEvidenceTransferLocked(EvidenceTransferState state, string reason)
	{
		state.Writer?.Abort();
		evidenceTransfers.Remove(state.RequestId);
		api.Logger.Warning("[ServerEssentials/AntiCheat] {0} Player={1} Mod={2} Request={3}", reason, state.PlayerName, state.ModId, state.RequestId);
	}

	private static string HashEvidenceFile(string path)
	{
		using FileStream inputStream = File.OpenRead(path);
		using SHA256 sHA = SHA256.Create();
		return Convert.ToHexString(sHA.ComputeHash(inputStream)).ToLowerInvariant();
	}

	private static string SanitizeEvidenceFilePart(string? value, int max, string fallback)
	{
		string text = (string.IsNullOrWhiteSpace(value) ? fallback : value.Trim());
		StringBuilder stringBuilder = new StringBuilder(text.Length);
		string text2 = text;
		foreach (char c in text2)
		{
			bool flag = char.IsLetterOrDigit(c);
			bool flag2 = flag;
			if (!flag2)
			{
				bool flag3 = ((c == '-' || c == '.' || c == '_') ? true : false);
				flag2 = flag3;
			}
			if (flag2)
			{
				stringBuilder.Append(c);
			}
			else if (char.IsWhiteSpace(c))
			{
				stringBuilder.Append('-');
			}
		}
		string text3 = stringBuilder.ToString().Trim(new char[3] { '.', '-', '_' });
		if (text3.Length == 0)
		{
			text3 = fallback;
		}
		return (text3.Length <= max) ? text3 : text3.Substring(0, max);
	}

	private static string UniqueEvidencePath(string path)
	{
		if (!File.Exists(path) && !File.Exists(path + ".part"))
		{
			return path;
		}
		string path2 = Path.GetDirectoryName(path) ?? ".";
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
		string extension = Path.GetExtension(path);
		int num = fileNameWithoutExtension.LastIndexOf("__owner-", StringComparison.OrdinalIgnoreCase);
		string text = ((num >= 0) ? fileNameWithoutExtension.Substring(0, num) : fileNameWithoutExtension);
		string text2 = ((num >= 0) ? fileNameWithoutExtension.Substring(num) : "");
		for (int i = 2; i < 10000; i++)
		{
			string text3 = Path.Combine(path2, text + "-" + i + text2 + extension);
			if (!File.Exists(text3) && !File.Exists(text3 + ".part"))
			{
				return text3;
			}
		}
		return Path.Combine(path2, text + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + text2 + extension);
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		incidentCorrelator.Clear(player?.PlayerUID);
		if (player != null)
		{
			AbortEvidenceForPlayer(player.PlayerUID);
			packetGuardPlayerWarningUtc.Remove(player.PlayerUID);
			GhostVisibilityCompatibilityRegistry.Remove(player.PlayerUID);
			sessions.Remove(player.PlayerUID);
		}
	}

	private bool DropFloodPacket(AntiCheatSession session, string packetType, long now)
	{
		if (session.NetworkPacketWindowStartUtc <= 0 || now - session.NetworkPacketWindowStartUtc >= 2000)
		{
			session.NetworkPacketWindowStartUtc = now;
			session.NetworkPacketWindowCount = 1;
			return false;
		}
		session.NetworkPacketWindowCount++;
		if (session.NetworkPacketWindowCount <= 12)
		{
			return false;
		}
		session.NetworkPacketsDropped++;
		session.LastNetworkFloodUtc = now;
		if (session.NetworkPacketsDropped == 1 || session.NetworkPacketsDropped % 100 == 0)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Dropped excess {0} traffic from {1} ({2}). Total dropped={3}.", packetType, session.PlayerName, session.PlayerUid, session.NetworkPacketsDropped);
		}
		return true;
	}

	private void OnClientHello(IServerPlayer player, ClientHelloPacket hello)
	{
		if (!config.Enabled || !runtimeAvailable || channel == null || player == null || hello == null || (hello.ClientVersion?.Length ?? 0) > 128 || (hello.GameVersion?.Length ?? 0) > 128 || (hello.Platform?.Length ?? 0) > 512)
		{
			return;
		}
		AntiCheatSession orCreate = sessions.GetOrCreate(player.PlayerUID, player.PlayerName);
		long num = NowMs();
		if (DropFloodPacket(orCreate, "hello", num))
		{
			return;
		}
		bool flag = orCreate.ClientInstalled && orCreate.Status == AntiCheatPlayerStatus.Scanning && !orCreate.ChallengeConsumed && !string.IsNullOrWhiteSpace(orCreate.ChallengeId) && orCreate.ChallengeExpiresUtc > num;
		bool flag2 = orCreate.ClientMissingSinceUtc > 0 || orCreate.Status == AntiCheatPlayerStatus.TimedOut;
		orCreate.ClientInstalled = true;
		orCreate.ClientVersion = hello.ClientVersion ?? "";
		orCreate.ClientGameVersion = hello.GameVersion ?? "";
		orCreate.ClientPlatform = hello.Platform ?? "";
		orCreate.SupportedScanners = hello.SupportedScanners;
		orCreate.ProtocolVersion = hello.ProtocolVersion;
		GhostVisibilityCompatibilityRegistry.Remove(player.PlayerUID);
		orCreate.LastHelloUtc = num;
		orCreate.LastHeartbeatUtc = orCreate.LastHelloUtc;
		RecordConnectionDiagnostic(orCreate, flag2 ? "HELLO_RECOVERED" : "HELLO_RECEIVED", "Handshake", flag2 ? "Recovered" : "Received", $"Client hello received with protocol {hello.ProtocolVersion} and release label '{orCreate.ClientVersion}'.");
		orCreate.ClientMissingSinceUtc = 0L;
		orCreate.RequiredClientDisconnectScheduled = false;
		if (config.StartupIntegrityGateEnabled && !orCreate.StartupIntegrityVerified && orCreate.StartupGateStartedUtc <= 0)
		{
			BeginStartupGate(orCreate, num);
		}
		orCreate.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, "CLIENT_MISSING", StringComparison.OrdinalIgnoreCase));
		if (hello.ProtocolVersion != 13)
		{
			orCreate.Status = AntiCheatPlayerStatus.ProtocolMismatch;
			orCreate.RiskScore = 0;
			RecordConnectionDiagnostic(orCreate, "PROTOCOL_MISMATCH", "Handshake", "Rejected", $"Client protocol {hello.ProtocolVersion}; server requires {13}.");
			SetRuntimeFinding(orCreate, "PROTOCOL_MISMATCH", AntiCheatSeverity.Info, "The anti-cheat client protocol does not match the server. This is a compatibility/coverage issue, not cheat evidence.", hello.ProtocolVersion.ToString());
			ApplyConsequences(player, orCreate, "protocol-mismatch");
			PublishPlayer(orCreate);
			channel.SendPacket(new ServerAckPacket
			{
				Accepted = false,
				State = "ProtocolMismatch"
			}, player);
			if (orCreate.StartupGatePending && config.StartupIntegrityGateDisconnectOnFailure && config.RequireClient)
			{
				EnforceStartupGateDisconnect(player, orCreate, "Client Integrity protocol mismatch.");
			}
			return;
		}
		string requiredClientVersion = GetRequiredClientVersion();
		bool flag3 = string.Equals(orCreate.ClientVersion?.Trim(), requiredClientVersion, StringComparison.OrdinalIgnoreCase);
		if (!string.IsNullOrWhiteSpace(orCreate.ClientVersion) && !flag3)
		{
			SetRuntimeFinding(orCreate, "CLIENT_VERSION_DIFFERENT", AntiCheatSeverity.Info, "Client Integrity reports a different release than the companion installed on the server. This is a compatibility/rollout difference, not cheat evidence.", $"detected={orCreate.ClientVersion};serverInstalled={requiredClientVersion};protocol={hello.ProtocolVersion}");
		}
		else
		{
			orCreate.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, "CLIENT_VERSION_DIFFERENT", StringComparison.OrdinalIgnoreCase));
		}
		if (config.RequireCurrentClientRelease && !flag3)
		{
			orCreate.Status = AntiCheatPlayerStatus.Failed;
			orCreate.RiskScore = 0;
			RecordConnectionDiagnostic(orCreate, "SERVER_CLIENT_VERSION_REQUIRED", "Handshake", "Rejected", $"Server-installed Client Integrity is v{requiredClientVersion}; detected client release '{orCreate.ClientVersion}'.");
			SetRuntimeFinding(orCreate, "SERVER_CLIENT_VERSION_REQUIRED", AntiCheatSeverity.Info, "The server requires clients to match the Client Integrity version installed on the server. This is a compatibility requirement, not cheat evidence.", "detected=" + orCreate.ClientVersion + ";required=" + requiredClientVersion);
			PublishPlayer(orCreate);
			channel.SendPacket(new ServerAckPacket
			{
				Accepted = false,
				State = "ServerClientVersionRequired"
			}, player);
			if (config.RequireClient)
			{
				EnforceClientVersionMismatchDisconnect(player, orCreate, orCreate.ClientVersion);
			}
			return;
		}
		ClientScannerFlags clientScannerFlags = ClientScannerFlags.Core | ClientScannerFlags.ServerBaselineSelfIntegrity;
		if (config.RandomizedSelfAttestationEnabled)
		{
			clientScannerFlags |= ClientScannerFlags.RandomizedSelfAttestation;
		}
		if (config.RequireSignedClientBuildManifest)
		{
			clientScannerFlags |= ClientScannerFlags.SignedBuildManifest;
		}
		if (((uint)hello.SupportedScanners & (uint)clientScannerFlags) != (uint)clientScannerFlags)
		{
			orCreate.Status = AntiCheatPlayerStatus.Failed;
			orCreate.RiskScore = 0;
			SetRuntimeFinding(orCreate, "SCANNER_CAPABILITY_MISSING", AntiCheatSeverity.Info, "The client does not support all server-required integrity scanners. Update the companion mod; this is not cheat evidence by itself.", hello.SupportedScanners.ToString());
			ApplyConsequences(player, orCreate, "scanner-capability");
			PublishPlayer(orCreate);
			channel.SendPacket(new ServerAckPacket
			{
				Accepted = false,
				State = "ScannerCapabilityMissing"
			}, player);
			if (orCreate.StartupGatePending && config.StartupIntegrityGateDisconnectOnFailure && config.RequireClient)
			{
				EnforceStartupGateDisconnect(player, orCreate, "Required startup integrity scanner capability is missing.");
			}
			return;
		}
		if (config.RequireHardenedClientCapabilities && (hello.SupportedScanners & 0x600) != 1536)
		{
			orCreate.Status = AntiCheatPlayerStatus.Failed;
			orCreate.RiskScore = 0;
			RecordConnectionDiagnostic(orCreate, "HARDENED_CAPABILITIES_REQUIRED", "Handshake", "Rejected", "The server requires the hardened report-hash and bound-heartbeat capabilities, but this client did not advertise both.");
			SetRuntimeFinding(orCreate, "CLIENT_HARDENING_REQUIRED", AntiCheatSeverity.Info, "The server requires the refreshed Client Integrity transport hardening capabilities. This is a compatibility requirement, not cheat evidence.", orCreate.ClientVersion);
			PublishPlayer(orCreate);
			channel.SendPacket(new ServerAckPacket
			{
				Accepted = false,
				State = "HardenedCapabilitiesRequired"
			}, player);
			if (orCreate.StartupGatePending && config.StartupIntegrityGateDisconnectOnFailure && config.RequireClient)
			{
				EnforceStartupGateDisconnect(player, orCreate, "This server requires the hardened Client Integrity transport capabilities.");
			}
			return;
		}
		GhostVisibilityCompatibilityRegistry.Update(player.PlayerUID, hello.SupportedScanners);
		channel.SendPacket(new ServerAckPacket
		{
			Accepted = true,
			State = "HelloAccepted"
		}, player);
		RecordConnectionDiagnostic(orCreate, "HELLO_ACK_SENT", "Handshake", "Accepted", flag ? "Server acknowledged a duplicate Client Integrity hello while the existing scan challenge remained active; no overlapping challenge was issued." : "Server acknowledged Client Integrity hello; scan challenge follows.");
		if (flag)
		{
			api.Logger.Debug("[ServerEssentials/AntiCheat] Duplicate hello from {0} ({1}) acknowledged without replacing active challenge {2}.", player.PlayerName, player.PlayerUID, orCreate.ChallengeId);
		}
		else
		{
			orCreate.ScanRetryCount = 0;
			SendChallenge(player, orCreate);
		}
	}

	private List<SelfAttestationProbeDto> BuildSelfAttestationChallenge(AntiCheatSession session)
	{
		session.SelfAttestationProbes = new List<SelfAttestationProbeDto>();
		session.ExpectedSelfAttestationResults = new List<SelfAttestationResultDto>();
		session.ExpectedSelfAttestationDigest = "";
		if (!config.RandomizedSelfAttestationEnabled)
		{
			return new List<SelfAttestationProbeDto>();
		}
		Assembly assembly = serverClientIntegrityRuntimeAssembly;
		if (assembly == null || serverSelfAttestationMethodTokens.Count < 2 || serverSelfAttestationTypeTokens.Count < 1)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Randomized Client Integrity attestation is enabled but the trusted probe catalog is unavailable ({0}). This challenge will rely on the exact DLL/content baseline only.", serverSelfAttestationCatalogStatus);
			return new List<SelfAttestationProbeDto>();
		}
		List<SelfAttestationProbeDto> list = new List<SelfAttestationProbeDto>();
		foreach (int item in SelectRandomTokens(serverSelfAttestationMethodTokens, config.SelfAttestationMethodProbeCount))
		{
			list.Add(new SelfAttestationProbeDto
			{
				ProbeId = Guid.NewGuid().ToString("N"),
				Kind = 1,
				MetadataToken = item
			});
		}
		foreach (int item2 in SelectRandomTokens(serverSelfAttestationTypeTokens, config.SelfAttestationTypeProbeCount))
		{
			list.Add(new SelfAttestationProbeDto
			{
				ProbeId = Guid.NewGuid().ToString("N"),
				Kind = 2,
				MetadataToken = item2
			});
		}
		list.Add(new SelfAttestationProbeDto
		{
			ProbeId = Guid.NewGuid().ToString("N"),
			Kind = 3,
			MetadataToken = 0
		});
		List<SelfAttestationResultDto> list2 = SelfAttestationEngine.Evaluate(assembly, list, session.ChallengeId, session.ChallengeNonce);
		if (list2.Count != list.Count || list2.Any((SelfAttestationResultDto x) => !x.Success || !IsSha256(x.Digest)))
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Server-side randomized attestation probe generation failed. No client will be penalized for this challenge; exact baseline checks remain active.");
			return new List<SelfAttestationProbeDto>();
		}
		session.SelfAttestationProbes = list.Select(CloneSelfProbe).ToList();
		session.ExpectedSelfAttestationResults = list2.Select(CloneSelfResult).ToList();
		session.ExpectedSelfAttestationDigest = SelfAttestationEngine.ComputeAggregateDigest(list2);
		return list;
	}

	private static List<int> SelectRandomTokens(IReadOnlyList<int> source, int requested)
	{
		if (source == null || source.Count == 0 || requested <= 0)
		{
			return new List<int>();
		}
		int num = Math.Min(requested, source.Count);
		List<int> list = source.ToList();
		List<int> list2 = new List<int>(num);
		for (int i = 0; i < num; i++)
		{
			int @int = RandomNumberGenerator.GetInt32(list.Count);
			list2.Add(list[@int]);
			list.RemoveAt(@int);
		}
		return list2;
	}

	private static SelfAttestationProbeDto CloneSelfProbe(SelfAttestationProbeDto probe)
	{
		return new SelfAttestationProbeDto
		{
			ProbeId = (probe?.ProbeId ?? ""),
			Kind = (probe?.Kind ?? 0),
			MetadataToken = (probe?.MetadataToken ?? 0)
		};
	}

	private static SelfAttestationResultDto CloneSelfResult(SelfAttestationResultDto result)
	{
		return new SelfAttestationResultDto
		{
			ProbeId = (result?.ProbeId ?? ""),
			Kind = (result?.Kind ?? 0),
			MetadataToken = (result?.MetadataToken ?? 0),
			Digest = (result?.Digest ?? ""),
			Success = (result?.Success ?? false),
			ErrorCode = (result?.ErrorCode ?? "")
		};
	}

	private (bool Allowed, string Reason) ValidateRandomizedSelfAttestation(AntiCheatSession session, ClientScanReportPacket report)
	{
		List<SelfAttestationProbeDto> list = session.SelfAttestationProbes ?? new List<SelfAttestationProbeDto>();
		List<SelfAttestationResultDto> list2 = session.ExpectedSelfAttestationResults ?? new List<SelfAttestationResultDto>();
		if (!config.RandomizedSelfAttestationEnabled || list.Count == 0 || list2.Count == 0)
		{
			return (Allowed: true, Reason: "NotRequested");
		}
		List<SelfAttestationResultDto> list3 = report.SelfAttestationResults ?? new List<SelfAttestationResultDto>();
		if (list3.Count != list.Count)
		{
			return (Allowed: false, Reason: $"ProbeCountMismatch expected={list.Count} received={list3.Count}");
		}
		if (!IsSha256(report.SelfAttestationDigest))
		{
			return (Allowed: false, Reason: "AggregateDigestMissingOrInvalid");
		}
		Dictionary<string, List<SelfAttestationResultDto>> dictionary = list3.GroupBy((SelfAttestationResultDto x) => x.ProbeId ?? "", StringComparer.Ordinal).ToDictionary((IGrouping<string, SelfAttestationResultDto> g) => g.Key, (IGrouping<string, SelfAttestationResultDto> g) => g.ToList(), StringComparer.Ordinal);
		foreach (SelfAttestationProbeDto probe in list)
		{
			if (!dictionary.TryGetValue(probe.ProbeId ?? "", out var value) || value.Count != 1)
			{
				return (Allowed: false, Reason: "ProbeIdentityMismatch");
			}
			SelfAttestationResultDto selfAttestationResultDto = value[0];
			if (!SelfAttestationEngine.SameProbe(probe, selfAttestationResultDto))
			{
				return (Allowed: false, Reason: "ProbeShapeMismatch");
			}
			if (!selfAttestationResultDto.Success || !IsSha256(selfAttestationResultDto.Digest))
			{
				return (Allowed: false, Reason: "ProbeReadFailed:" + selfAttestationResultDto.ErrorCode);
			}
			SelfAttestationResultDto selfAttestationResultDto2 = list2.FirstOrDefault((SelfAttestationResultDto x) => string.Equals(x.ProbeId, probe.ProbeId, StringComparison.Ordinal));
			if (selfAttestationResultDto2 == null || !FixedTimeHexEquals(selfAttestationResultDto2.Digest, selfAttestationResultDto.Digest))
			{
				return (Allowed: false, Reason: "ProbeDigestMismatch");
			}
		}
		string expected = SelfAttestationEngine.ComputeAggregateDigest(list3);
		if (!FixedTimeHexEquals(expected, report.SelfAttestationDigest))
		{
			return (Allowed: false, Reason: "AggregateDigestSelfMismatch");
		}
		if (!FixedTimeHexEquals(session.ExpectedSelfAttestationDigest, report.SelfAttestationDigest))
		{
			return (Allowed: false, Reason: "AggregateDigestBaselineMismatch");
		}
		return (Allowed: true, Reason: "Matched");
	}

	private void SendChallenge(IServerPlayer player, AntiCheatSession session)
	{
		if (config.Enabled && runtimeAvailable && channel != null)
		{
			session.ChallengeId = Guid.NewGuid().ToString("N");
			session.ChallengeNonce = RandomNumberGenerator.GetBytes(32);
			session.ChallengeIssuedUtc = NowMs();
			int num = (session.StartupGatePending ? Math.Max(config.ChallengeTimeoutSeconds, config.StartupChallengeTimeoutSeconds) : config.ChallengeTimeoutSeconds);
			session.ChallengeExpiresUtc = NowMs() + (long)num * 1000L;
			session.ChallengeConsumed = false;
			List<SelfAttestationProbeDto> list = BuildSelfAttestationChallenge(session);
			session.RequestedScanners = 8447;
			if (list.Count > 0)
			{
				session.RequestedScanners |= 16384;
			}
			session.Status = AntiCheatPlayerStatus.Scanning;
			PublishPlayer(session);
			ProtectedAssetManifest protectedAssetManifest = GetProtectedAssetManifest();
			channel.SendPacket(new ServerChallengePacket
			{
				ProtocolVersion = 13,
				ChallengeId = session.ChallengeId,
				Nonce = session.ChallengeNonce,
				ServerTimestampUtc = NowMs(),
				RequestedScanners = session.RequestedScanners,
				TimeoutSeconds = num,
				SuspiciousRuntimeTerms = GetSuspiciousRuntimeTerms(),
				BlacklistedRuntimeTerms = GetBlacklistedRuntimeTerms(),
				ProtectedAssetPatterns = protectedAssetManifest.Patterns,
				ProtectedAssetDigest = protectedAssetManifest.Digest,
				ProtectedAssetCount = protectedAssetManifest.Count,
				ProtectedAssets = protectedAssetManifest.Details,
				ScanHotkeys = (config.RuntimeSurfaceInspectionEnabled && config.SuspiciousHotkeyInspectionEnabled),
				ScanClientCommands = (config.RuntimeSurfaceInspectionEnabled && config.SuspiciousClientCommandInspectionEnabled),
				ScanProtectedAssets = (config.RuntimeSurfaceInspectionEnabled && config.ProtectedAssetInspectionEnabled && protectedAssetManifest.Count > 0 && !string.IsNullOrWhiteSpace(protectedAssetManifest.Digest)),
				SelfAttestationProbes = list,
				BuildManifestDigest = (serverClientIntegrityBaseline?.BuildManifestSha256 ?? "")
			}, player);
		}
	}

	private void OnScanReport(IServerPlayer player, ClientScanReportPacket report)
	{
		if (!config.Enabled || !runtimeAvailable || channel == null || player == null || report == null)
		{
			return;
		}
		AntiCheatSession orCreate = sessions.GetOrCreate(player.PlayerUID, player.PlayerName);
		long num = NowMs();
		if (DropFloodPacket(orCreate, "scan-report", num))
		{
			return;
		}
		string text = ValidateReport(orCreate, report);
		if (text.Length > 0)
		{
			if (text == "ChallengeAlreadyUsed" && string.Equals(orCreate.ChallengeId, report.ChallengeId, StringComparison.Ordinal))
			{
				channel.SendPacket(new ServerAckPacket
				{
					ChallengeId = (report.ChallengeId ?? ""),
					Accepted = false,
					State = "DuplicateReportIgnored"
				}, player);
				api.Logger.Debug("[ServerEssentials/AntiCheat] Ignored duplicate consumed scan report from {0} ({1}); challenge={2}.", player.PlayerName, player.PlayerUID, report.ChallengeId ?? "");
				RecordConnectionDiagnostic(orCreate, "DUPLICATE_SCAN_REPORT", "Scan transport", "Ignored", "A second copy of an already-consumed challenge report was rejected without adding cheat risk or starting another scan.");
				return;
			}
			channel.SendPacket(new ServerAckPacket
			{
				ChallengeId = (report.ChallengeId ?? ""),
				Accepted = false,
				State = text
			}, player);
			if (IsTransientScanReject(orCreate, report, text))
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat] Transient scan rejection for {0} ({1}): {2}. The player is not receiving cheat risk for this transport/timing failure.", player.PlayerName, player.PlayerUID, text);
				SetRuntimeFinding(orCreate, "SCAN_RETRY", AntiCheatSeverity.Info, "A scan response arrived stale/incomplete and was retried without adding cheat risk.", text);
				bool flag;
				switch (text)
				{
				case "IntegrityPayloadMissing":
				case "IntegrityHashFormatInvalid":
				case "IntegrityMetricsInvalid":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				int num2 = ((!flag) ? 1 : 2);
				if (orCreate.ScanRetryCount < num2)
				{
					orCreate.ScanRetryCount++;
					SendChallenge(player, orCreate);
					return;
				}
				switch (text)
				{
				case "IntegrityPayloadMissing":
				case "IntegrityHashFormatInvalid":
				case "IntegrityMetricsInvalid":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				if (flag)
				{
					orCreate.Status = AntiCheatPlayerStatus.Failed;
					orCreate.StartupGatePending = false;
					orCreate.StartupIntegrityVerified = false;
					orCreate.StartupGateCompletedUtc = num;
					orCreate.StartupGateVerdict = "IntegrityMeasurementUnavailable";
					RecordConnectionDiagnostic(orCreate, "CLIENT_INTEGRITY_MEASUREMENT_UNAVAILABLE", "Client self-integrity", "Kick", $"Client Integrity could not produce a valid self-measurement after {num2 + 1} attempts: {text}.");
					PublishPlayer(orCreate);
					EnforceClientIntegrityBaselineDisconnect(player, orCreate, $"Client Integrity could not be reliably measured after {num2 + 1} independent attempts ({text}).");
				}
				else
				{
					PublishPlayer(orCreate);
				}
			}
			else if (text == "ServerClientVersionRequired")
			{
				string requiredClientVersion = GetRequiredClientVersion();
				orCreate.Status = AntiCheatPlayerStatus.Failed;
				orCreate.RiskScore = 0;
				RecordConnectionDiagnostic(orCreate, "SERVER_CLIENT_VERSION_REQUIRED", "Startup scan", "Rejected", $"The scan reports Client Integrity '{report.ClientVersion}', but the server-installed companion is v{requiredClientVersion}.");
				SetRuntimeFinding(orCreate, "SERVER_CLIENT_VERSION_REQUIRED", AntiCheatSeverity.Info, "The server requires clients to match the Client Integrity version installed on the server. This is a compatibility requirement, not cheat evidence.", "detected=" + report.ClientVersion + ";required=" + requiredClientVersion);
				PublishPlayer(orCreate);
				if (config.RequireClient)
				{
					EnforceClientVersionMismatchDisconnect(player, orCreate, report.ClientVersion);
				}
			}
			else if (text == "HardenedCapabilityRequired")
			{
				orCreate.Status = AntiCheatPlayerStatus.Failed;
				orCreate.RiskScore = 0;
				RecordConnectionDiagnostic(orCreate, "HARDENED_CAPABILITIES_REQUIRED", "Startup scan", "Rejected", "The scan did not prove the hardened Client Integrity capability set required by server policy.");
				SetRuntimeFinding(orCreate, "CLIENT_HARDENING_REQUIRED", AntiCheatSeverity.Info, "The server requires report-hash v2 and challenge-bound heartbeats. This is a client compatibility requirement, not cheat evidence.", orCreate.ClientVersion);
				PublishPlayer(orCreate);
				if (orCreate.StartupGatePending && config.StartupIntegrityGateDisconnectOnFailure && config.RequireClient)
				{
					EnforceStartupGateDisconnect(player, orCreate, "This server requires the hardened Client Integrity transport capabilities.");
				}
			}
			else
			{
				orCreate.Status = AntiCheatPlayerStatus.Failed;
				bool flag;
				switch (text)
				{
				case "NonceMismatch":
				case "ReportHashMismatch":
				case "ReportHashDowngrade":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				int num3 = (flag ? 25 : 10);
				SetRuntimeFinding(orCreate, "INVALID_REPORT", (num3 >= 25) ? AntiCheatSeverity.High : AntiCheatSeverity.Moderate, "The client anti-cheat report failed server validation.", text);
				MarkFindingObserved(orCreate, "ServerRuntime", "INVALID_REPORT", text, NowMs());
				UpdateSessionFamilyRisk(orCreate, NowMs(), preserveOperationalStatus: true);
				api.Logger.Warning("[ServerEssentials/AntiCheat] Scan validation failed for {0} ({1}): {2}.", player.PlayerName, player.PlayerUID, text);
				ApplyConsequences(player, orCreate, "invalid-report");
				PublishPlayer(orCreate);
				if (orCreate.StartupGatePending && config.StartupIntegrityGateDisconnectOnFailure && config.RequireClient)
				{
					EnforceStartupGateDisconnect(player, orCreate, "Startup integrity report validation failed: " + text);
				}
			}
			return;
		}
		orCreate.ChallengeConsumed = true;
		orCreate.ScanRetryCount = 0;
		int riskScore = orCreate.RiskScore;
		long lastConsequenceUtc = orCreate.LastConsequenceUtc;
		List<ClientModDto> list = report.Mods ?? new List<ClientModDto>();
		ClientScannerFlags clientScannerFlags = (ClientScannerFlags)(report.CompletedScanners & 0x3FF00);
		orCreate.SupportedScanners |= (int)clientScannerFlags;
		if (report.ReportHashVersion >= 2)
		{
			orCreate.SupportedScanners |= 512;
		}
		List<ScanFindingDto> second = orCreate.Findings.Where((ScanFindingDto f) => string.Equals(f.Scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase)).ToList();
		List<ScanFindingDto> list2 = (report.Findings ?? new List<ScanFindingDto>()).Concat(second).ToList();
		List<ClientAssemblyDto> list3 = report.Assemblies ?? new List<ClientAssemblyDto>();
		ClientIntegrityDto integrity = report.Integrity;
		ClientRuntimeDto runtime = report.Runtime;
		ClientHarmonyDto harmony = report.Harmony;
		List<ClientScannerMetricDto> lastScannerMetrics = report.ScannerMetrics ?? new List<ClientScannerMetricDto>();
		(bool, string, string) tuple = ValidateClientIntegrityBaseline(list, list3, integrity);
		if (!tuple.Item1 && IsOperationalClientIntegrityAdmissionFailure(tuple.Item2))
		{
			orCreate.SelfIntegrityMeasurementFailureCount++;
			if (orCreate.SelfIntegrityMeasurementFailureCount < 3)
			{
				SetRuntimeFinding(orCreate, "CLIENT_SELF_MEASUREMENT_RETRY", AntiCheatSeverity.Info, "Client Integrity could not complete one self-measurement. A fresh challenge is being used to confirm the measurement before any disconnect.", tuple.Item2 + ": " + tuple.Item3);
				RecordConnectionDiagnostic(orCreate, "CLIENT_SELF_MEASUREMENT_RETRY", "Client self-integrity", "Confirm", $"Self-integrity measurement attempt {orCreate.SelfIntegrityMeasurementFailureCount}/{3} was unavailable: {tuple.Item2}. No kick was applied for this single measurement failure.");
				channel.SendPacket(new ServerAckPacket
				{
					ChallengeId = report.ChallengeId,
					Accepted = false,
					State = "SelfIntegrityConfirming"
				}, player);
				SendChallenge(player, orCreate);
			}
			else
			{
				orCreate.Status = AntiCheatPlayerStatus.Failed;
				orCreate.StartupGatePending = false;
				orCreate.StartupIntegrityVerified = false;
				orCreate.StartupGateCompletedUtc = num;
				orCreate.StartupGateVerdict = "ClientIntegrityMeasurementUnavailable";
				SetRuntimeFinding(orCreate, "CLIENT_SELF_MEASUREMENT_UNAVAILABLE", AntiCheatSeverity.Info, "Client Integrity could not produce a complete self-measurement after three independent attempts.", tuple.Item2 + ": " + tuple.Item3);
				RecordConnectionDiagnostic(orCreate, "CLIENT_SELF_MEASUREMENT_UNAVAILABLE", "Client self-integrity", "Kick", "The required self-integrity measurement remained unavailable after three fresh challenges; this is treated as verification unavailable, not as direct cheat evidence.");
				PublishPlayer(orCreate);
				channel.SendPacket(new ServerAckPacket
				{
					ChallengeId = report.ChallengeId,
					Accepted = false,
					State = "ClientIntegrityMeasurementUnavailable"
				}, player);
				EnforceClientIntegrityBaselineDisconnect(player, orCreate, "Client Integrity could not be reliably measured after three independent attempts (" + tuple.Item2 + ").");
			}
			return;
		}
		if (!tuple.Item1)
		{
			orCreate.SelfIntegrityMeasurementFailureCount = 0;
			orCreate.LastMods = list;
			orCreate.LastAssemblies = list3;
			orCreate.LastIntegrity = integrity;
			orCreate.LastRuntime = runtime;
			orCreate.LastHarmony = harmony;
			orCreate.LastScannerMetrics = lastScannerMetrics;
			orCreate.Status = AntiCheatPlayerStatus.Failed;
			orCreate.RiskScore = 0;
			orCreate.StartupGatePending = false;
			orCreate.StartupIntegrityVerified = false;
			orCreate.StartupGateCompletedUtc = num;
			orCreate.StartupGateVerdict = "ClientIntegrityBaselineRejected";
			SetRuntimeFinding(orCreate, tuple.Item2, AntiCheatSeverity.Critical, "Client Integrity failed the server's exact companion baseline verification.", tuple.Item3);
			RecordConnectionDiagnostic(orCreate, tuple.Item2, "Client self-integrity", "Kick", tuple.Item3);
			PublishPlayer(orCreate);
			channel.SendPacket(new ServerAckPacket
			{
				ChallengeId = report.ChallengeId,
				Accepted = false,
				State = "ClientIntegrityBaselineRejected"
			}, player);
			EnforceClientIntegrityBaselineDisconnect(player, orCreate, tuple.Item3);
			return;
		}
		orCreate.SelfIntegrityMeasurementFailureCount = 0;
		orCreate.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, "CLIENT_SELF_MEASUREMENT_RETRY", StringComparison.OrdinalIgnoreCase));
		(bool, string) tuple2 = ValidateRandomizedSelfAttestation(orCreate, report);
		if (!tuple2.Item1)
		{
			orCreate.SelfAttestationFailureCount++;
			orCreate.LastSelfAttestationFailureUtc = num;
			orCreate.LastSelfAttestationFailureReason = tuple2.Item2;
			int num4 = Math.Max(2, config.SelfAttestationFailuresBeforeKick);
			if (orCreate.SelfAttestationFailureCount < num4)
			{
				orCreate.SelfAttestationConfirmationPending = true;
				SetRuntimeFinding(orCreate, "CLIENT_RANDOM_ATTESTATION_RETRY", AntiCheatSeverity.Info, "A randomized Client Integrity measurement did not validate. A fresh independent challenge is being used to confirm the result before any disconnect.", tuple2.Item2);
				RecordConnectionDiagnostic(orCreate, "CLIENT_RANDOM_ATTESTATION_RETRY", "Client self-integrity", "Confirm", $"Randomized attestation attempt {orCreate.SelfAttestationFailureCount}/{num4} failed: {tuple2.Item2}. A fresh probe set was issued; no kick was applied for this single failure.");
				PublishPlayer(orCreate);
				channel.SendPacket(new ServerAckPacket
				{
					ChallengeId = report.ChallengeId,
					Accepted = false,
					State = "SelfAttestationConfirming"
				}, player);
				orCreate.ScanRetryCount = 0;
				SendChallenge(player, orCreate);
			}
			else
			{
				orCreate.SelfAttestationConfirmationPending = false;
				orCreate.Status = AntiCheatPlayerStatus.Failed;
				orCreate.StartupGatePending = false;
				orCreate.StartupIntegrityVerified = false;
				orCreate.StartupGateCompletedUtc = num;
				orCreate.StartupGateVerdict = "RandomizedSelfAttestationRejected";
				SetRuntimeFinding(orCreate, "CLIENT_RANDOM_ATTESTATION_FAILED", AntiCheatSeverity.Critical, "Client Integrity failed repeated independent server-selected IL/metadata measurements.", tuple2.Item2);
				RecordConnectionDiagnostic(orCreate, "CLIENT_RANDOM_ATTESTATION_FAILED", "Client self-integrity", "Kick", $"Repeated randomized attestation failure ({orCreate.SelfAttestationFailureCount}/{num4}): {tuple2.Item2}.");
				PublishPlayer(orCreate);
				channel.SendPacket(new ServerAckPacket
				{
					ChallengeId = report.ChallengeId,
					Accepted = false,
					State = "RandomizedSelfAttestationRejected"
				}, player);
				EnforceClientIntegrityBaselineDisconnect(player, orCreate, "Repeated randomized Client Integrity attestation failure: " + tuple2.Item2);
			}
			return;
		}
		orCreate.SelfAttestationFailureCount = 0;
		orCreate.LastSelfAttestationFailureReason = "";
		orCreate.SelfAttestationConfirmationPending = false;
		orCreate.LastAcceptedSelfAttestationDigest = report.SelfAttestationDigest ?? "";
		orCreate.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, "CLIENT_RANDOM_ATTESTATION_RETRY", StringComparison.OrdinalIgnoreCase));
		(AntiCheatPlayerStatus, int, List<ScanFindingDto>) tuple3 = evaluator.Evaluate(list, list2, list3, integrity, harmony, GetCanonicalModSnapshot(), GetCoreAssemblySnapshot());
		list2.AddRange(BuildModDbVerificationFindings(list, list3));
		(AntiCheatPlayerStatus, int, List<ScanFindingDto>) tuple4 = evaluator.Evaluate(list, list2, list3, integrity, harmony, GetCanonicalModSnapshot(), GetCoreAssemblySnapshot());
		orCreate.LastMods = list;
		if (list.Any((ClientModDto m) => m.PackageBytes > 0 || m.DllCount > 0 || m.DllBytes > 0 || IsSha256(m.ContentSha256)))
		{
			orCreate.SupportedScanners |= 256;
		}
		WarnAdminIfLocalSuiteVersionMismatch(player, list, orCreate);
		orCreate.LastAssemblies = list3;
		orCreate.LastIntegrity = integrity;
		orCreate.LastRuntime = runtime;
		orCreate.LastHarmony = harmony;
		orCreate.LastScannerMetrics = lastScannerMetrics;
		QueueModDbVerification(orCreate.PlayerUid, orCreate.ClientGameVersion, list);
		orCreate.LastRuntimeDigest = runtime?.AssemblySetDigest ?? "";
		orCreate.LastHarmonyPatchDigest = harmony?.PatchSetDigest ?? "";
		orCreate.LastSelfIntegrityDigest = ComputeIntegrityDigest(integrity);
		orCreate.LastHeartbeatSelfIntegrityDigest = orCreate.LastSelfIntegrityDigest;
		orCreate.LastHeartbeatRuntimeDigest = orCreate.LastRuntimeDigest;
		orCreate.LastHeartbeatRuntimeGeneration = runtime?.RuntimeGeneration ?? 0;
		orCreate.LastHeartbeatAssemblyCount = runtime?.LoadedAssemblyCount ?? 0;
		orCreate.LastHeartbeatDynamicAssemblyCount = runtime?.DynamicAssemblyCount ?? 0;
		orCreate.LastHeartbeatHarmonyPatchDigest = harmony?.PatchSetDigest ?? "";
		orCreate.LastHeartbeatHarmonyPatchCount = harmony?.PatchCount ?? 0;
		orCreate.LastHeartbeatSuspiciousHarmonyOwnerCount = harmony?.SuspiciousOwnerCount ?? 0;
		orCreate.Findings = tuple4.Item3;
		orCreate.CorrelationRiskBonus = 0;
		long nowUtc = (orCreate.LastScanUtc = NowMs());
		RefreshObservedFindingTimestamps(orCreate, tuple4.Item3, nowUtc, refreshClientEvidence: true);
		UpdateSessionFamilyRisk(orCreate, nowUtc);
		foreach (ScanFindingDto item in tuple4.Item3.Where((ScanFindingDto f) => !string.Equals(f.Scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase) && (config.ModDbAutomaticConsequencesEnabled || !string.Equals(f.Scanner, "ModDbPolicy", StringComparison.OrdinalIgnoreCase))))
		{
			incidentCorrelator.Observe(player.PlayerUID, item, nowUtc);
		}
		ApplyIncidentCorrelation(orCreate, nowUtc);
		orCreate.ClientVersion = (string.IsNullOrWhiteSpace(report.ClientVersion) ? orCreate.ClientVersion : report.ClientVersion);
		orCreate.LastHeartbeatUtc = Math.Max(orCreate.LastHeartbeatUtc, orCreate.LastScanUtc);
		orCreate.NextScanUtc = orCreate.LastScanUtc + (long)config.RescanMinutes * 60000L;
		bool startupGatePending = orCreate.StartupGatePending;
		(List<string>, List<string>) tuple5 = EvaluateStrictClientModDbAdmission(list, list3);
		orCreate.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && (string.Equals(f.Code, "CLIENT_MOD_DB_PENDING", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Code, "CLIENT_MOD_DB_REJECTED", StringComparison.OrdinalIgnoreCase)));
		if (config.StrictClientModDbEnforcementEnabled && tuple5.Item1.Count > 0)
		{
			SetRuntimeFinding(orCreate, "CLIENT_MOD_DB_REJECTED", AntiCheatSeverity.Info, "Strict client-mod database policy rejected one or more reported mods. This is an admission-policy failure, not by itself a cheat finding.", StrictModDbSubject(tuple5.Item1));
			orCreate.Status = AntiCheatPlayerStatus.Failed;
			orCreate.StartupGatePending = false;
			orCreate.StartupIntegrityVerified = false;
			orCreate.StartupGateCompletedUtc = orCreate.LastScanUtc;
			orCreate.StartupGateVerdict = "ModDatabaseRejected";
			RecordConnectionDiagnostic(orCreate, "CLIENT_MOD_DB_REJECTED", startupGatePending ? "Startup mod database" : "Runtime mod database", "Kick", StrictModDbSubject(tuple5.Item1));
			PublishPlayer(orCreate);
			channel.SendPacket(new ServerAckPacket
			{
				ChallengeId = report.ChallengeId,
				Accepted = false,
				State = "ClientModDatabaseRejected",
				NextScanUtc = orCreate.NextScanUtc
			}, player);
			EnforceStrictModDatabaseDisconnect(player, orCreate, tuple5.Item1);
			return;
		}
		if (config.StrictClientModDbEnforcementEnabled && tuple5.Item2.Count > 0)
		{
			SetRuntimeFinding(orCreate, "CLIENT_MOD_DB_PENDING", AntiCheatSeverity.Info, "Strict client-mod database verification is still resolving exact fingerprints. Admission remains pending until every reported mod has a trusted exact match.", StrictModDbSubject(tuple5.Item2));
			orCreate.StartupGatePending = true;
			orCreate.StartupIntegrityVerified = false;
			orCreate.StartupGateVerdict = "ModDatabasePending";
			RecordConnectionDiagnostic(orCreate, "CLIENT_MOD_DB_PENDING", startupGatePending ? "Startup mod database" : "Runtime mod database", "Pending", StrictModDbSubject(tuple5.Item2));
		}
		else
		{
			CompleteStartupGate(orCreate, tuple3.Item1, orCreate.LastScanUtc);
		}
		RecordConnectionDiagnostic(orCreate, startupGatePending ? "STARTUP_SCAN_ACCEPTED" : "RUNTIME_SCAN_ACCEPTED", startupGatePending ? "Startup scan" : "Runtime scan", "Accepted", $"Deep scan accepted with status {tuple3.Item1}; {orCreate.LastMods.Count} reported mods and {orCreate.LastAssemblies.Count} assemblies." + ((tuple5.Item2.Count > 0) ? $" Strict ModDB admission pending for {tuple5.Item2.Count} mod(s)." : ""));
		PublishPlayer(orCreate);
		channel.SendPacket(new ServerAckPacket
		{
			ChallengeId = report.ChallengeId,
			Accepted = true,
			State = orCreate.Status.ToString(),
			NextScanUtc = orCreate.NextScanUtc
		}, player);
		ApplyConsequences(player, orCreate, "scan");
		string consequence = ((orCreate.LastConsequenceUtc > lastConsequenceUtc) ? orCreate.LastConsequence : "");
		foreach (ScanFindingDto item2 in (from f in orCreate.Findings
			where !string.Equals(f.Scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase)
			where f.Severity >= 2
			select f).Take(64))
		{
			historyStore?.RecordScanFinding(player, item2, riskScore, orCreate.RiskScore, orCreate.Status.ToString(), consequence);
		}
		PublishPlayer(orCreate);
	}

	private void WarnAdminIfLocalSuiteVersionMismatch(IServerPlayer player, IReadOnlyList<ClientModDto> mods, AntiCheatSession session)
	{
		if (player == null || session == null || !IsSuiteAdmin(player))
		{
			return;
		}
		string serverSuiteVersion = GetServerSuiteVersion();
		if (string.IsNullOrWhiteSpace(serverSuiteVersion))
		{
			return;
		}
		ClientModDto clientModDto = (mods ?? Array.Empty<ClientModDto>()).FirstOrDefault((ClientModDto m) => string.Equals(m?.ModId, "newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase));
		string text = (clientModDto?.Version ?? "").Trim();
		bool flag = clientModDto == null || string.IsNullOrWhiteSpace(text);
		if (!flag && string.Equals(text, serverSuiteVersion, StringComparison.OrdinalIgnoreCase))
		{
			session.AdminSuiteVersionWarningKey = "";
			return;
		}
		string text2 = (flag ? ("missing->" + serverSuiteVersion) : (text + "->" + serverSuiteVersion));
		if (string.Equals(session.AdminSuiteVersionWarningKey, text2, StringComparison.Ordinal))
		{
			return;
		}
		session.AdminSuiteVersionWarningKey = text2;
		string message = (flag ? $"[Server Essentials] Admin notice: Newfies Server Essentials Suite was not detected in your local Mods folder. This server is running v{serverSuiteVersion}. Install/update the matching v{serverSuiteVersion} suite locally so Shift+L and admin client features stay compatible." : $"[Server Essentials] Admin notice: your local Newfies Server Essentials Suite is v{text}, but this server is v{serverSuiteVersion}. Update the suite in your local Mods folder to v{serverSuiteVersion}.");
		try
		{
			player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
			api.Logger.Notification("[ServerEssentials] Warned admin {0} ({1}) about local Suite version {2}; server is {3}.", player.PlayerName, player.PlayerUID, flag ? "missing" : text, serverSuiteVersion);
		}
		catch
		{
		}
	}

	private string GetServerSuiteVersion()
	{
		try
		{
			return (api.ModLoader.Mods.FirstOrDefault((Mod m) => m?.Info != null && string.Equals(m.Info.ModID, "newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase))?.Info?.Version ?? "").Trim();
		}
		catch
		{
			return "";
		}
	}

	private static bool IsSuiteAdmin(IServerPlayer player)
	{
		try
		{
			return player.HasPrivilege(Privilege.root) || player.HasPrivilege(Privilege.controlserver);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsTransientScanReject(AntiCheatSession session, ClientScanReportPacket report, string reject)
	{
		bool flag;
		switch (reject)
		{
		case "ChallengeExpired":
		case "RequiredScannerMissing":
		case "RuntimePayloadMissing":
		case "HarmonyPayloadMissing":
		case "IntegrityPayloadMissing":
		case "IntegrityHashFormatInvalid":
		case "IntegrityMetricsInvalid":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return true;
		}
		if (reject == "ChallengeMismatch" && session.Status == AntiCheatPlayerStatus.Scanning && session.ChallengeExpiresUtc > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && !string.Equals(session.ChallengeId, report.ChallengeId, StringComparison.Ordinal))
		{
			return true;
		}
		return false;
	}

	private string ValidateReport(AntiCheatSession session, ClientScanReportPacket report)
	{
		if (!ClientReportBounds.IsValid(report))
		{
			return "ReportShapeInvalid";
		}
		if (report.ProtocolVersion != 13)
		{
			return "ProtocolMismatch";
		}
		if (session.ChallengeConsumed)
		{
			return "ChallengeAlreadyUsed";
		}
		if (session.ChallengeExpiresUtc <= 0 || NowMs() > session.ChallengeExpiresUtc)
		{
			return "ChallengeExpired";
		}
		if (!string.Equals(session.ChallengeId, report.ChallengeId, StringComparison.Ordinal))
		{
			return "ChallengeMismatch";
		}
		if (!CryptographicOperations.FixedTimeEquals(session.ChallengeNonce, report.Nonce ?? Array.Empty<byte>()))
		{
			return "NonceMismatch";
		}
		ClientScannerFlags requestedScanners = (ClientScannerFlags)session.RequestedScanners;
		if (((uint)report.CompletedScanners & (uint)requestedScanners) != (uint)requestedScanners)
		{
			return "RequiredScannerMissing";
		}
		if ((report.Mods?.Count ?? 0) > 512 || (report.Assemblies?.Count ?? 0) > 512 || (report.Findings?.Count ?? 0) > 256 || (report.ScannerMetrics?.Count ?? 0) > 16 || (report.SelfAttestationResults?.Count ?? 0) > 32)
		{
			return "ReportTooLarge";
		}
		if ((requestedScanners & ClientScannerFlags.Runtime) != 0 && report.Runtime == null)
		{
			return "RuntimePayloadMissing";
		}
		if ((requestedScanners & ClientScannerFlags.Harmony) != 0 && report.Harmony == null)
		{
			return "HarmonyPayloadMissing";
		}
		if ((report.Runtime?.NewAssemblies?.Count).GetValueOrDefault() > 64)
		{
			return "RuntimePayloadTooLarge";
		}
		if ((report.Harmony?.Patches?.Count).GetValueOrDefault() > 512)
		{
			return "HarmonyPayloadTooLarge";
		}
		if ((report.Assemblies ?? new List<ClientAssemblyDto>()).Any((ClientAssemblyDto a) =>
		{
			bool flag4 = (a.SimilarityMinHash?.Count ?? 0) > 64;
			bool flag5 = flag4;
			if (!flag5)
			{
				int structureTokenCount = a.StructureTokenCount;
				bool flag6 = ((structureTokenCount < 0 || structureTokenCount > 4096) ? true : false);
				flag5 = flag6;
			}
			bool flag7 = flag5 || (a.BehaviorMinHash?.Count ?? 0) > 64;
			bool flag8 = flag7;
			if (!flag8)
			{
				int structureTokenCount = a.BehaviorTokenCount;
				bool flag6 = ((structureTokenCount < 0 || structureTokenCount > 4096) ? true : false);
				flag8 = flag6;
			}
			return flag8;
		}))
		{
			return "SimilaritySignatureInvalid";
		}
		if (report.Runtime != null && (report.Runtime.RuntimeGeneration < 0 || report.Runtime.LoadedAssemblyCount < 0 || report.Runtime.DynamicAssemblyCount < 0 || report.Runtime.BaselineAssemblyCount < 0))
		{
			return "RuntimePayloadInvalid";
		}
		if (report.Runtime != null && (report.Runtime.DynamicAssemblyCount > report.Runtime.LoadedAssemblyCount || report.Runtime.UnassociatedNewAssemblyCount < 0 || report.Runtime.UnassociatedNewAssemblyCount > (report.Runtime.NewAssemblies?.Count ?? 0)))
		{
			return "RuntimeCountsInvalid";
		}
		if (report.Runtime != null && !IsSha256(report.Runtime.AssemblySetDigest))
		{
			return "RuntimeDigestInvalid";
		}
		if (report.Harmony != null && !IsSha256(report.Harmony.PatchSetDigest))
		{
			return "HarmonyDigestInvalid";
		}
		if (report.Harmony != null && (report.Harmony.PatchCount < 0 || report.Harmony.PatchCount != (report.Harmony.Patches?.Count ?? 0) || report.Harmony.SuspiciousOwnerCount < 0))
		{
			return "HarmonyCountsInvalid";
		}
		if ((report.Mods ?? new List<ClientModDto>()).Any((ClientModDto m) => !IsSha256OrEmpty(m.Sha256) || !IsSha256OrEmpty(m.ContentSha256)))
		{
			return "ModHashFormatInvalid";
		}
		if ((report.Assemblies ?? new List<ClientAssemblyDto>()).Any((ClientAssemblyDto a) => !IsSha256OrEmpty(a.Sha256) || !IsSha256OrEmpty(a.StructureSha256) || !IsSha256OrEmpty(a.BehaviorSha256)))
		{
			return "AssemblyHashFormatInvalid";
		}
		if ((report.Runtime?.NewAssemblies ?? new List<ClientRuntimeAssemblyDto>()).Any((ClientRuntimeAssemblyDto a) => !IsSha256OrEmpty(a.Sha256)))
		{
			return "RuntimeAssemblyHashFormatInvalid";
		}
		if (report.Integrity == null)
		{
			return "IntegrityPayloadMissing";
		}
		if (!IsSha256(report.Integrity.AssemblySha256) || !IsSha256OrEmpty(report.Integrity.PackageSha256) || !IsSha256(report.Integrity.ContentSha256) || !IsSha256OrEmpty(report.Integrity.BuildManifestSha256) || !IsSha256OrEmpty(report.Integrity.BuildManifestSignatureSha256))
		{
			return "IntegrityHashFormatInvalid";
		}
		if (report.Integrity.AssemblyFileSizeBytes <= 0 || report.Integrity.PackageBytes < 0 || report.Integrity.DllCount < 0 || report.Integrity.DllBytes < 0)
		{
			return "IntegrityMetricsInvalid";
		}
		if ((report.ScannerMetrics ?? new List<ClientScannerMetricDto>()).Any((ClientScannerMetricDto m) => m.DurationMs < 0 || m.ModCount < 0 || m.AssemblyCount < 0 || m.FindingCount < 0 || m.CacheHits < 0 || m.CacheMisses < 0))
		{
			return "ScannerMetricInvalid";
		}
		if (report.ScanStartedUtc <= 0 || report.ScanCompletedUtc < report.ScanStartedUtc)
		{
			return "ScanTimingInvalid";
		}
		ClientScannerFlags completedScanners = (ClientScannerFlags)report.CompletedScanners;
		string requiredClientVersion = GetRequiredClientVersion();
		bool flag = string.Equals(report.ClientVersion?.Trim(), requiredClientVersion, StringComparison.OrdinalIgnoreCase);
		if (config.RequireCurrentClientRelease && !flag)
		{
			return "ServerClientVersionRequired";
		}
		bool flag2 = (session.SupportedScanners & 0x200) != 0;
		bool flag3 = (completedScanners & ClientScannerFlags.HardenedProtocol9) == ClientScannerFlags.HardenedProtocol9 && report.ReportHashVersion >= 2;
		if (config.RequireHardenedClientCapabilities && !flag3)
		{
			return "HardenedCapabilityRequired";
		}
		if (flag2 && report.ReportHashVersion < 2)
		{
			return "ReportHashDowngrade";
		}
		int reportHashVersion = report.ReportHashVersion;
		if ((reportHashVersion < 0 || reportHashVersion > 2) ? true : false)
		{
			return "ReportHashVersionInvalid";
		}
		string expected = ((report.ReportHashVersion >= 2) ? ReportHasher.ComputeV2(report.ProtocolVersion, report.ClientVersion ?? "", report.ScanStartedUtc, report.ScanCompletedUtc, report.CompletedScanners, report.ChallengeId, report.Nonce, report.Mods ?? new List<ClientModDto>(), report.Findings ?? new List<ScanFindingDto>(), report.Assemblies ?? new List<ClientAssemblyDto>(), report.Integrity, report.Runtime, report.ScannerMetrics ?? new List<ClientScannerMetricDto>(), report.Harmony, report.SelfAttestationResults ?? new List<SelfAttestationResultDto>()) : ReportHasher.Compute(report.ChallengeId, report.Nonce, report.Mods ?? new List<ClientModDto>(), report.Findings ?? new List<ScanFindingDto>(), report.Assemblies ?? new List<ClientAssemblyDto>(), report.Integrity, report.Runtime, report.ScannerMetrics ?? new List<ClientScannerMetricDto>(), report.Harmony, report.SelfAttestationResults ?? new List<SelfAttestationResultDto>()));
		if (!FixedTimeHexEquals(expected, report.ReportHash))
		{
			return "ReportHashMismatch";
		}
		return "";
	}

	private static bool FixedTimeHexEquals(string? expected, string? actual)
	{
		string text = (expected ?? "").Trim();
		string text2 = (actual ?? "").Trim();
		if (text.Length != 64 || text2.Length != 64 || !text.All(Uri.IsHexDigit) || !text2.All(Uri.IsHexDigit))
		{
			return false;
		}
		try
		{
			return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(text), Convert.FromHexString(text2));
		}
		catch
		{
			return false;
		}
	}

	private bool ValidateBoundHeartbeat(AntiCheatSession session, HeartbeatPacket heartbeat, out string reject)
	{
		reject = "";
		if ((session.SupportedScanners & 0x400) == 0)
		{
			return true;
		}
		if (heartbeat.BindingVersion != 1)
		{
			reject = "BindingVersion";
			return false;
		}
		if (session.ChallengeNonce == null || session.ChallengeNonce.Length == 0 || string.IsNullOrWhiteSpace(session.ChallengeId))
		{
			reject = "NoActiveChallenge";
			return false;
		}
		if (!string.Equals(session.ChallengeId, heartbeat.ChallengeId, StringComparison.Ordinal))
		{
			reject = "ChallengeMismatch";
			return false;
		}
		if (heartbeat.Sequence <= 0 || heartbeat.Sequence <= session.LastHeartbeatSequence)
		{
			reject = "ReplayOrOutOfOrder";
			return false;
		}
		string expected = HeartbeatHasher.Compute(heartbeat, session.ChallengeNonce);
		if (!FixedTimeHexEquals(expected, heartbeat.BindingHash))
		{
			reject = "BindingHashMismatch";
			return false;
		}
		return true;
	}

	private void RecordHeartbeatBindingReject(AntiCheatSession session, string reject, long now)
	{
		session.HeartbeatBindingRejectCount++;
		session.LastHeartbeatBindingRejectUtc = now;
		if (session.HeartbeatBindingRejectCount == 1 || session.HeartbeatBindingRejectCount % 20 == 0)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Rejected bound heartbeat from {0} ({1}): {2}. Count={3}.", session.PlayerName, session.PlayerUid, reject, session.HeartbeatBindingRejectCount);
		}
		RecordConnectionDiagnostic(session, "HEARTBEAT_BINDING_REJECTED", "Heartbeat", "Ignored", "Bound heartbeat validation failed: " + reject + ". This heartbeat was not used to refresh liveness.");
	}

	private void OnHeartbeat(IServerPlayer player, HeartbeatPacket heartbeat)
	{
		if (!config.Enabled || !runtimeAvailable || channel == null || player == null || heartbeat == null || (heartbeat.ClientVersion?.Length ?? 0) > 128 || (heartbeat.ChallengeId?.Length ?? 0) > 128 || (heartbeat.BindingHash?.Length ?? 0) > 128 || (heartbeat.RuntimeDigest?.Length ?? 0) > 128 || (heartbeat.SelfIntegrityDigest?.Length ?? 0) > 128 || (heartbeat.HarmonyPatchDigest?.Length ?? 0) > 128 || (heartbeat.SelfAttestationDigest?.Length ?? 0) > 128 || heartbeat.ProtocolVersion != 13)
		{
			return;
		}
		long num = NowMs();
		if (sessions.TryGet(player.PlayerUID, out AntiCheatSession session) && DropFloodPacket(session, "heartbeat", num))
		{
			return;
		}
		if (!sessions.TryGet(player.PlayerUID, out AntiCheatSession session2))
		{
			session2 = sessions.GetOrCreate(player.PlayerUID, player.PlayerName);
			session2.NetworkPacketWindowStartUtc = num;
			session2.NetworkPacketWindowCount = 1;
			session2.ClientInstalled = true;
			session2.ClientVersion = heartbeat.ClientVersion ?? "";
			session2.ProtocolVersion = heartbeat.ProtocolVersion;
			session2.ConnectedUtc = num;
			session2.LastHelloUtc = num;
			session2.LastHeartbeatUtc = num;
			session2.SupportedScanners = 255;
			session2.ClientMissingSinceUtc = 0L;
			session2.RequiredClientDisconnectScheduled = false;
			session2.ScanRetryCount = 0;
			SendChallenge(player, session2);
			return;
		}
		if (!session2.ClientInstalled)
		{
			session2.ClientInstalled = true;
			session2.ClientVersion = heartbeat.ClientVersion ?? session2.ClientVersion;
			session2.ProtocolVersion = heartbeat.ProtocolVersion;
			session2.LastHelloUtc = num;
			session2.LastHeartbeatUtc = num;
			session2.SupportedScanners = 255;
			session2.ClientMissingSinceUtc = 0L;
			session2.RequiredClientDisconnectScheduled = false;
			session2.ScanRetryCount = 0;
			session2.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, "CLIENT_MISSING", StringComparison.OrdinalIgnoreCase));
			api.Logger.Debug("[ServerEssentials/AntiCheat] Recovered client presence from heartbeat for {0} ({1}).", player.PlayerName, player.PlayerUID);
			SendChallenge(player, session2);
			return;
		}
		if (!ValidateBoundHeartbeat(session2, heartbeat, out string reject))
		{
			RecordHeartbeatBindingReject(session2, reject, num);
			PublishPlayer(session2);
			return;
		}
		if ((session2.SupportedScanners & 0x400) != 0)
		{
			session2.LastHeartbeatSequence = heartbeat.Sequence;
		}
		bool flag = session2.Status == AntiCheatPlayerStatus.TimedOut && session2.Findings.Any((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, "HEARTBEAT_TIMEOUT", StringComparison.OrdinalIgnoreCase));
		if (flag)
		{
			session2.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, "HEARTBEAT_TIMEOUT", StringComparison.OrdinalIgnoreCase));
			session2.Status = AntiCheatPlayerStatus.Unknown;
			RebuildSessionRiskFromCurrentEvidence(session2);
			RecordConnectionDiagnostic(session2, "HEARTBEAT_RECOVERED", "Heartbeat", "Recovered", "A valid challenge-bound heartbeat resumed after a timeout; stale coverage status was cleared.");
		}
		int lastHeartbeatRuntimeGeneration = session2.LastHeartbeatRuntimeGeneration;
		string lastHeartbeatRuntimeDigest = session2.LastHeartbeatRuntimeDigest;
		string lastHeartbeatHarmonyPatchDigest = session2.LastHeartbeatHarmonyPatchDigest;
		bool flag2 = session2.LastHeartbeatUtc > 0;
		session2.LastHeartbeatUtc = num;
		if (!string.IsNullOrWhiteSpace(heartbeat.ClientVersion))
		{
			session2.ClientVersion = heartbeat.ClientVersion;
		}
		session2.LastHeartbeatRuntimeGeneration = Math.Max(0, heartbeat.RuntimeGeneration);
		session2.LastHeartbeatAssemblyCount = Math.Max(0, heartbeat.LoadedAssemblyCount);
		session2.LastHeartbeatDynamicAssemblyCount = Math.Max(0, heartbeat.DynamicAssemblyCount);
		string text = (heartbeat.RuntimeDigest ?? "").Trim().ToLowerInvariant();
		string text2 = (heartbeat.SelfIntegrityDigest ?? "").Trim().ToLowerInvariant();
		string text3 = (heartbeat.HarmonyPatchDigest ?? "").Trim().ToLowerInvariant();
		string text4 = (heartbeat.SelfAttestationDigest ?? "").Trim().ToLowerInvariant();
		session2.LastHeartbeatRuntimeDigest = text;
		session2.LastHeartbeatSelfIntegrityDigest = text2;
		session2.LastHeartbeatSelfAttestationDigest = text4;
		session2.LastHeartbeatHarmonyPatchDigest = text3;
		session2.LastHeartbeatHarmonyPatchCount = Math.Max(0, heartbeat.HarmonyPatchCount);
		session2.LastHeartbeatSuspiciousHarmonyOwnerCount = Math.Max(0, heartbeat.SuspiciousHarmonyOwnerCount);
		bool flag3 = flag;
		bool flag4 = flag2 && heartbeat.RuntimeGeneration < lastHeartbeatRuntimeGeneration;
		if (flag4)
		{
			SetRuntimeFinding(session2, "RUNTIME_GENERATION_REGRESSED", AntiCheatSeverity.Moderate, "The client runtime generation counter moved backwards between heartbeats.", $"{lastHeartbeatRuntimeGeneration}->{heartbeat.RuntimeGeneration}");
			flag3 = true;
		}
		bool flag5 = flag2 && IsSha256(lastHeartbeatRuntimeDigest) && IsSha256(text) && !string.Equals(lastHeartbeatRuntimeDigest, text, StringComparison.OrdinalIgnoreCase) && heartbeat.RuntimeGeneration <= lastHeartbeatRuntimeGeneration;
		if (flag5)
		{
			SetRuntimeFinding(session2, "RUNTIME_DIGEST_CHANGED_WITHOUT_GENERATION", AntiCheatSeverity.Low, "The runtime assembly-set digest changed without a corresponding generation increase. This can be legitimate with unloadable runtime contexts but requires review.", text.Substring(0, 16));
			flag3 = true;
		}
		bool flag6 = text.Length > 0 && !IsSha256(text);
		bool flag7 = text2.Length > 0 && !IsSha256(text2);
		bool flag8 = text3.Length > 0 && !IsSha256(text3);
		bool flag9 = text4.Length > 0 && !IsSha256(text4);
		bool flag10 = session2.ChallengeConsumed && session2.SelfAttestationProbes.Count > 0 && heartbeat.SelfAttestationProbeCount != session2.SelfAttestationProbes.Count;
		bool flag11 = session2.ChallengeConsumed && IsSha256(session2.ExpectedSelfAttestationDigest) && IsSha256(text4) && !string.Equals(session2.ExpectedSelfAttestationDigest, text4, StringComparison.OrdinalIgnoreCase);
		if (flag6)
		{
			SetRuntimeFinding(session2, "HEARTBEAT_RUNTIME_DIGEST_INVALID", AntiCheatSeverity.Moderate, "The heartbeat runtime digest was not a valid SHA-256 value.", text.Substring(0, Math.Min(16, text.Length)));
			flag3 = true;
		}
		if (flag7)
		{
			SetRuntimeFinding(session2, "HEARTBEAT_SELF_DIGEST_INVALID", AntiCheatSeverity.Moderate, "The heartbeat Client Integrity self-digest was not a valid SHA-256 value.", text2.Substring(0, Math.Min(16, text2.Length)));
			flag3 = true;
		}
		if (flag8)
		{
			SetRuntimeFinding(session2, "HEARTBEAT_HARMONY_DIGEST_INVALID", AntiCheatSeverity.Moderate, "The heartbeat Harmony patch-set digest was not a valid SHA-256 value.", text3.Substring(0, Math.Min(16, text3.Length)));
			flag3 = true;
		}
		if (flag9 | flag10 | flag11)
		{
			string subject;
			if (flag9)
			{
				subject = "invalid digest format";
			}
			else
			{
				subject = (flag10 ? $"probe count {heartbeat.SelfAttestationProbeCount}/{session2.SelfAttestationProbes.Count}" : "digest differs from the last accepted server-selected probe set");
			}
			SetRuntimeFinding(session2, "HEARTBEAT_SELF_ATTESTATION_CHANGED", AntiCheatSeverity.Moderate, "The periodic Client Integrity randomized self-check no longer matches the accepted deep attestation. A fresh deep challenge is required before any enforcement.", subject);
			flag3 = true;
		}
		bool flag12 = IsSha256(session2.LastRuntimeDigest) && IsSha256(text) && !string.Equals(session2.LastRuntimeDigest, text, StringComparison.OrdinalIgnoreCase);
		bool flag13 = IsSha256(session2.LastSelfIntegrityDigest) && IsSha256(text2) && !string.Equals(session2.LastSelfIntegrityDigest, text2, StringComparison.OrdinalIgnoreCase);
		bool flag14 = IsSha256(session2.LastHarmonyPatchDigest) && IsSha256(text3) && !string.Equals(session2.LastHarmonyPatchDigest, text3, StringComparison.OrdinalIgnoreCase);
		bool flag15 = flag2 && IsSha256(lastHeartbeatHarmonyPatchDigest) && IsSha256(text3) && !string.Equals(lastHeartbeatHarmonyPatchDigest, text3, StringComparison.OrdinalIgnoreCase) && heartbeat.RuntimeGeneration <= lastHeartbeatRuntimeGeneration;
		if (flag15)
		{
			SetRuntimeFinding(session2, "RUNTIME_PATCHSET_CHANGED_WITHOUT_GENERATION", AntiCheatSeverity.Low, "The Harmony patch set changed without a corresponding runtime-generation increase. A fresh scan is required to identify the new patch owner.", text3.Substring(0, 16));
			flag3 = true;
		}
		if (config.RuntimeRescanOnChange && session2.LastScanUtc > 0 && session2.Status != AntiCheatPlayerStatus.Scanning && (flag12 | flag13 | flag14 | flag6 | flag7 | flag8 | flag9 | flag10 | flag11 | flag4 | flag5 | flag15) && num >= session2.NextRuntimeChangeChallengeUtc)
		{
			session2.NextRuntimeChangeChallengeUtc = num + (long)config.RuntimeChangeRescanCooldownSeconds * 1000L;
			if (flag12 | flag13 | flag14 | flag9 | flag10 | flag11)
			{
				bool flag16 = flag9 | flag10 | flag11;
				string code;
				if (flag16)
				{
					code = "CLIENT_SELF_ATTESTATION_CHANGED";
				}
				else if (flag13)
				{
					code = "CLIENT_SELF_DIGEST_CHANGED";
				}
				else
				{
					code = (flag14 ? "RUNTIME_PATCHSET_CHANGED" : "RUNTIME_DIGEST_CHANGED");
				}
				string summary;
				if (flag16)
				{
					summary = "The periodic randomized Client Integrity measurement changed after the last accepted deep scan. A fresh independent challenge was requested; the heartbeat alone does not cause a kick.";
				}
				else if (flag13)
				{
					summary = "The Client Integrity binary/package digest changed after the last accepted deep scan. A fresh scan was requested automatically.";
				}
				else
				{
					summary = (flag14 ? "The runtime Harmony patch set changed after the last accepted deep scan. A fresh scan was requested automatically." : "The client runtime assembly set changed after the last accepted deep scan. A fresh scan was requested automatically.");
				}
				string subject2;
				if (flag16)
				{
					subject2 = ((text4.Length > 0) ? text4.Substring(0, Math.Min(16, text4.Length)) : "missing");
				}
				else if (flag13)
				{
					subject2 = text2.Substring(0, Math.Min(16, text2.Length));
				}
				else
				{
					subject2 = (flag14 ? text3.Substring(0, Math.Min(16, text3.Length)) : text.Substring(0, Math.Min(16, text.Length)));
				}
				SetRuntimeFinding(session2, code, (!(flag13 | flag16)) ? AntiCheatSeverity.Low : AntiCheatSeverity.Moderate, summary, subject2);
			}
			PublishPlayer(session2);
			session2.ScanRetryCount = 0;
			SendChallenge(player, session2);
		}
		else if (flag3)
		{
			PublishPlayer(session2);
		}
	}

	private void Watchdog()
	{
		if (!config.Enabled || !runtimeAvailable || channel == null)
		{
			return;
		}
		RefillDeferredModDbVerifications();
		long num = NowMs();
		CleanupEvidenceTransfers(num);
		long num2 = (long)config.ClientHelloGraceSeconds * 1000L;
		long num3 = (long)config.ClientHelloDisconnectDelaySeconds * 1000L;
		foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
		{
			AntiCheatSession orCreate = sessions.GetOrCreate(item.PlayerUID, item.PlayerName);
			bool flag = false;
			bool flag2 = false;
			bool flag3 = false;
			if (!orCreate.ClientInstalled && config.RequireClient && orCreate.ConnectedUtc > 0 && num - orCreate.ConnectedUtc > num2)
			{
				if (orCreate.Status != AntiCheatPlayerStatus.TimedOut)
				{
					orCreate.Status = AntiCheatPlayerStatus.TimedOut;
					orCreate.RiskScore = 0;
					orCreate.ClientMissingSinceUtc = num;
					orCreate.RequiredClientDisconnectScheduled = false;
					SetRuntimeFinding(orCreate, "CLIENT_MISSING", AntiCheatSeverity.Info, "The required anti-cheat client has not completed its hello handshake yet. A recovery window is active before disconnect so slow joins are not treated as missing clients.", "newfiesanticheatclient");
					api.Logger.Warning("[ServerEssentials/AntiCheat] Client hello grace expired for {0} ({1}); waiting an additional {2}s recovery window before required-client disconnect.", item.PlayerName, item.PlayerUID, config.ClientHelloDisconnectDelaySeconds);
					RecordConnectionDiagnostic(orCreate, "HELLO_GRACE_EXPIRED", "Handshake", "Recovery window", $"No client hello after {config.ClientHelloGraceSeconds}s; allowing another {config.ClientHelloDisconnectDelaySeconds}s before disconnect.");
					flag = true;
					flag2 = true;
				}
				else
				{
					if (orCreate.ClientMissingSinceUtc <= 0)
					{
						orCreate.ClientMissingSinceUtc = num;
						flag = true;
						flag2 = true;
					}
					flag3 = orCreate.ClientMissingSinceUtc > 0 && num - orCreate.ClientMissingSinceUtc >= num3 && !orCreate.RequiredClientDisconnectScheduled;
				}
			}
			else if (orCreate.ClientInstalled && orCreate.Status == AntiCheatPlayerStatus.Scanning && orCreate.ChallengeExpiresUtc > 0 && num > orCreate.ChallengeExpiresUtc && !orCreate.ChallengeConsumed)
			{
				long num4 = (long)Math.Max(30, config.HeartbeatTimeoutSeconds) * 1000L;
				bool flag4 = orCreate.LastHeartbeatUtc > 0 && num - orCreate.LastHeartbeatUtc <= num4;
				long num5 = ((orCreate.StartupGateStartedUtc > 0) ? (num - orCreate.StartupGateStartedUtc) : long.MaxValue);
				if ((orCreate.StartupGatePending & flag4) && num5 >= 0 && num5 < (long)config.StartupChallengeHardLimitSeconds * 1000L)
				{
					long num6 = orCreate.StartupGateStartedUtc + (long)config.StartupChallengeHardLimitSeconds * 1000L;
					long num7 = Math.Min((long)config.StartupChallengeHeartbeatExtensionSeconds * 1000L, Math.Max(1000L, num6 - num));
					orCreate.ChallengeExpiresUtc = num + num7;
					SetRuntimeFinding(orCreate, "STARTUP_SCAN_EXTENDED", AntiCheatSeverity.Info, "Startup integrity scan is still running and the client heartbeat is healthy, so the existing challenge deadline was extended without retrying or adding cheat risk.", $"extensionSeconds={Math.Max(1L, num7 / 1000)};startupAgeSeconds={Math.Max(0L, num5 / 1000)}");
					api.Logger.Notification("[ServerEssentials/AntiCheat] Startup scan for {0} ({1}) is still alive; extended challenge {2} by {3}s instead of issuing an overlapping retry.", item.PlayerName, item.PlayerUID, orCreate.ChallengeId, Math.Max(1L, num7 / 1000));
					RecordConnectionDiagnostic(orCreate, "STARTUP_SCAN_EXTENDED", "Startup scan", "Extended", $"Heartbeat remained healthy; extended the active challenge by {Math.Max(1L, num7 / 1000)}s at {Math.Max(0L, num5 / 1000)}s startup age.");
					PublishPlayer(orCreate);
					continue;
				}
				bool flag5 = orCreate.StartupGatePending && orCreate.StartupGateStartedUtc > 0 && num5 >= (long)config.StartupChallengeHardLimitSeconds * 1000L;
				if (!flag5 && orCreate.ScanRetryCount < 1)
				{
					orCreate.ScanRetryCount++;
					SetRuntimeFinding(orCreate, "SCAN_RETRY", AntiCheatSeverity.Info, "The scan challenge timed out without a healthy startup heartbeat and was retried automatically without adding cheat risk.", $"attempt={orCreate.ScanRetryCount + 1}");
					api.Logger.Warning("[ServerEssentials/AntiCheat] Scan timeout for {0} ({1}); retrying once with a fresh challenge.", item.PlayerName, item.PlayerUID);
					RecordConnectionDiagnostic(orCreate, "SCAN_RETRY", orCreate.StartupGatePending ? "Startup scan" : "Runtime scan", "Retry", "Challenge timed out without a fresh heartbeat; issuing one bounded retry without cheat risk.");
					SendChallenge(item, orCreate);
					continue;
				}
				orCreate.Status = AntiCheatPlayerStatus.TimedOut;
				SetRuntimeFinding(orCreate, "SCAN_TIMEOUT", AntiCheatSeverity.Info, "The client did not return the requested scan after the bounded recovery window. This is a scan availability failure, not cheat evidence by itself.", orCreate.ChallengeId);
				api.Logger.Warning("[ServerEssentials/AntiCheat] Scan failed after bounded recovery for {0} ({1}); challenge={2}.", item.PlayerName, item.PlayerUID, orCreate.ChallengeId);
				RecordConnectionDiagnostic(orCreate, "SCAN_TIMEOUT", orCreate.StartupGatePending ? "Startup scan" : "Runtime scan", orCreate.StartupGatePending ? "Disconnect eligible" : "Coverage stale", flag5 ? $"Startup scan reached the configured {config.StartupChallengeHardLimitSeconds}s hard limit." : "Scan did not complete after the bounded retry path.");
				if (orCreate.StartupGatePending && config.StartupIntegrityGateDisconnectOnFailure && config.RequireClient)
				{
					EnforceStartupGateDisconnect(item, orCreate, "Startup integrity scan did not complete inside the bounded recovery window.");
				}
				flag = true;
			}
			else if (orCreate.ClientInstalled && orCreate.LastHeartbeatUtc > 0 && num - orCreate.LastHeartbeatUtc > (long)config.HeartbeatTimeoutSeconds * 1000L)
			{
				if (orCreate.Status != AntiCheatPlayerStatus.TimedOut)
				{
					orCreate.Status = AntiCheatPlayerStatus.TimedOut;
					SetRuntimeFinding(orCreate, "HEARTBEAT_TIMEOUT", AntiCheatSeverity.Info, "The anti-cheat client heartbeat timed out. Coverage is stale; no additional cheat risk was assigned.", orCreate.ClientVersion);
					api.Logger.Warning("[ServerEssentials/AntiCheat] Heartbeat timed out for {0} ({1}); client={2}.", item.PlayerName, item.PlayerUID, orCreate.ClientVersion);
					RecordConnectionDiagnostic(orCreate, "HEARTBEAT_TIMEOUT", "Heartbeat", "Coverage stale", $"No heartbeat inside the configured {config.HeartbeatTimeoutSeconds}s window.");
					flag = true;
				}
			}
			else if (orCreate.ClientInstalled && orCreate.NextScanUtc > 0 && num >= orCreate.NextScanUtc && orCreate.Status != AntiCheatPlayerStatus.Scanning)
			{
				orCreate.ScanRetryCount = 0;
				SendChallenge(item, orCreate);
				continue;
			}
			if (flag3)
			{
				orCreate.RequiredClientDisconnectScheduled = true;
				if (!ApplyConsequences(item, orCreate, "required-client-timeout"))
				{
					EnforceRequiredClientDisconnect(item, orCreate);
				}
				flag = true;
			}
			else if (flag && !flag2)
			{
				ApplyConsequences(item, orCreate, "watchdog");
			}
			if (flag)
			{
				PublishPlayer(orCreate);
			}
		}
	}

	private void RegisterCommands()
	{
		try
		{
			if (api.ChatCommands.Get("anticheat") != null)
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat] Root /anticheat already exists; the Suite will not replace it. Web anti-cheat controls remain available when networking is healthy.");
			}
			else
			{
				api.RegisterCommand("anticheat", "Newfie's Anti-Cheat status and rescans (integrated into Server Essentials Suite)", "status [player] | rescan <player>", OnAntiCheatCommand, "controlserver");
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Could not inspect/register /anticheat: {0}: {1}", ex.GetType().Name, ex.Message);
		}
	}

	private void OnAntiCheatCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = (args.PopWord() ?? "status").ToLowerInvariant();
		bool flag;
		switch (text)
		{
		case "status":
		{
			string name = args.PopWord() ?? "";
			if (!string.IsNullOrWhiteSpace(name))
			{
				AntiCheatSession antiCheatSession = sessions.Snapshot().FirstOrDefault((AntiCheatSession s) => s.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase));
				SendCommandReply(player, groupId, (antiCheatSession == null) ? "No anti-cheat session found for that player." : Describe(antiCheatSession));
				return;
			}
			IReadOnlyList<AntiCheatSession> readOnlyList = sessions.Snapshot();
			int value = readOnlyList.Count((AntiCheatSession s) => s.Status == AntiCheatPlayerStatus.Clean);
			int value2 = readOnlyList.Count((AntiCheatSession s) => s.Status == AntiCheatPlayerStatus.Review);
			int value3 = readOnlyList.Count((AntiCheatSession s) => s.Status == AntiCheatPlayerStatus.Suspicious);
			int value4 = readOnlyList.Count((AntiCheatSession s) => !s.ClientInstalled || s.Status == AntiCheatPlayerStatus.TimedOut);
			SendCommandReply(player, groupId, $"Anti-Cheat: {readOnlyList.Count} tracked | clean {value} | review {value2} | suspicious {value3} | missing/timed out {value4}");
			return;
		}
		case "oreshield":
		case "oreguard":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			OreShieldWebSnapshot oreShieldWebSnapshot = oreShield?.GetSnapshot() ?? BuildOreShieldFallbackSnapshot(config.OreShieldEnabled ? "Not active." : "Disabled by configuration.");
			SendCommandReply(player, groupId, $"Ore Shield: {(oreShieldWebSnapshot.Active ? "ACTIVE" : (oreShieldWebSnapshot.Enabled ? "UNAVAILABLE" : "DISABLED"))} | ores {oreShieldWebSnapshot.ProtectedOreVariants} | modded {(oreShieldWebSnapshot.ProtectModdedOres ? "on" : "off")} | unknown-neighbor HIDE | strict-match {(oreShieldWebSnapshot.StrictOreMatching ? "on" : "off")} | LOS {oreShieldWebSnapshot.LosDiscoveries} discoveries/{oreShieldWebSnapshot.LosRayChecks} checks | vein-reveal {(oreShieldWebSnapshot.RevealWholeVeinOnMine ? "on" : "off")} {oreShieldWebSnapshot.VeinDiscoveries} discoveries/{oreShieldWebSnapshot.VeinBlocksRevealed} blocks | cache {(oreShieldWebSnapshot.ChunkCacheEnabled ? "on" : "off")} {oreShieldWebSnapshot.CacheEntries} entries/{(double)oreShieldWebSnapshot.CacheBytes / 1048576.0:F1} MiB | hits {oreShieldWebSnapshot.CacheHits}/{oreShieldWebSnapshot.CacheMisses} | chunks masked {oreShieldWebSnapshot.ChunksMasked} | hidden ore {oreShieldWebSnapshot.HiddenRealOreBlocks} | visual decoys off | {oreShieldWebSnapshot.Status}");
		}
		else if (text == "rescan")
		{
			string uidOrName = args.PopWord() ?? "";
			string message = RequestRescan(uidOrName);
			SendCommandReply(player, groupId, message);
		}
		else
		{
			SendCommandReply(player, groupId, "Usage: /anticheat status [player] | /anticheat oreshield | /anticheat rescan <player>");
		}
	}

	public string RequestRescan(string uidOrName)
	{
		if (!config.Enabled || !runtimeAvailable || channel == null)
		{
			return "Anti-cheat is disabled or unavailable.";
		}
		if (string.IsNullOrWhiteSpace(uidOrName))
		{
			return "A player name or UID is required.";
		}
		IServerPlayer serverPlayer = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerName.Equals(uidOrName, StringComparison.OrdinalIgnoreCase) || p.PlayerUID.Equals(uidOrName, StringComparison.OrdinalIgnoreCase));
		if (serverPlayer == null)
		{
			return "Player is not online.";
		}
		AntiCheatSession orCreate = sessions.GetOrCreate(serverPlayer.PlayerUID, serverPlayer.PlayerName);
		if (!orCreate.ClientInstalled)
		{
			return "Anti-cheat client has not handshaken yet.";
		}
		if (orCreate.ProtocolVersion != 13)
		{
			return "Player client protocol does not match the server.";
		}
		if (orCreate.Status == AntiCheatPlayerStatus.Scanning && !orCreate.ChallengeConsumed && orCreate.ChallengeExpiresUtc > NowMs())
		{
			return "An anti-cheat scan is already in progress for " + serverPlayer.PlayerName + ".";
		}
		orCreate.ScanRetryCount = 0;
		SendChallenge(serverPlayer, orCreate);
		return "Anti-cheat rescan requested for " + serverPlayer.PlayerName + ".";
	}

	public AntiCheatNavStatusWebSnapshot GetNavStatusSnapshot()
	{
		IReadOnlyList<AntiCheatSession> source = sessions.Snapshot();
		HashSet<string> onlineUids = (from p in api.World.AllOnlinePlayers.OfType<IServerPlayer>()
			select p.PlayerUID).ToHashSet(StringComparer.OrdinalIgnoreCase);
		List<AntiCheatSession> source2 = source.Where((AntiCheatSession x) => onlineUids.Contains(x.PlayerUid)).ToList();
		return new AntiCheatNavStatusWebSnapshot
		{
			Enabled = config.Enabled,
			Available = runtimeAvailable,
			Suspicious = source2.Count((AntiCheatSession x) => x.Status == AntiCheatPlayerStatus.Suspicious),
			Review = source2.Count((AntiCheatSession x) => x.Status == AntiCheatPlayerStatus.Review),
			MissingOrTimedOut = source2.Count((AntiCheatSession x) =>
			{
				bool flag = !x.ClientInstalled;
				bool flag2 = flag;
				if (!flag2)
				{
					AntiCheatPlayerStatus status = x.Status;
					bool flag3 = (uint)(status - 6) <= 2u;
					flag2 = flag3;
				}
				return flag2;
			}),
			PacketGuard = new AntiCheatNavPacketGuardWebSnapshot
			{
				Enabled = config.PacketGuardEnabled
			}
		};
	}

	public AntiCheatWebSnapshot GetSnapshot()
	{
		(bool, bool, string) serverCompanionStatus = GetServerCompanionStatus();
		IReadOnlyList<AntiCheatSession> source = sessions.Snapshot();
		HashSet<string> onlineUids = (from p in api.World.AllOnlinePlayers.OfType<IServerPlayer>()
			select p.PlayerUID).ToHashSet(StringComparer.OrdinalIgnoreCase);
		List<AntiCheatSession> list = source.Where((AntiCheatSession x) => onlineUids.Contains(x.PlayerUid)).ToList();
		(int, long) evidenceStats = GetEvidenceStats();
		AntiCheatWebSnapshot antiCheatWebSnapshot = new AntiCheatWebSnapshot();
		antiCheatWebSnapshot.Enabled = config.Enabled;
		antiCheatWebSnapshot.Available = runtimeAvailable;
		antiCheatWebSnapshot.AvailabilityReason = runtimeAvailabilityReason;
		antiCheatWebSnapshot.LastActivationError = lastActivationError;
		antiCheatWebSnapshot.NetworkChannelName = "newfiesanticheat";
		antiCheatWebSnapshot.NetworkPacketsDropped = list.Sum((AntiCheatSession x) => x.NetworkPacketsDropped);
		antiCheatWebSnapshot.RequireClient = config.RequireClient;
		antiCheatWebSnapshot.ModDbAutomaticConsequencesEnabled = config.ModDbAutomaticConsequencesEnabled;
		antiCheatWebSnapshot.ClientModDbVerificationEnabled = config.ClientModDbVerificationEnabled;
		antiCheatWebSnapshot.StrictClientModDbEnforcementEnabled = config.StrictClientModDbEnforcementEnabled;
		antiCheatWebSnapshot.StartupIntegrityGateEnabled = config.StartupIntegrityGateEnabled;
		antiCheatWebSnapshot.StartupIntegrityGateBlockWorldInteractions = config.StartupIntegrityGateBlockWorldInteractions;
		antiCheatWebSnapshot.StartupIntegrityGateRequireClean = config.StartupIntegrityGateRequireClean;
		antiCheatWebSnapshot.StartupIntegrityGateDisconnectOnFailure = config.StartupIntegrityGateDisconnectOnFailure;
		antiCheatWebSnapshot.StartupIntegrityGateNoticeCooldownSeconds = config.StartupIntegrityGateNoticeCooldownSeconds;
		antiCheatWebSnapshot.StartupCoreAssemblyInspectionEnabled = config.StartupCoreAssemblyInspectionEnabled;
		antiCheatWebSnapshot.StartupCoreAssemblyCount = GetCoreAssemblySnapshot().Count;
		antiCheatWebSnapshot.ServerCompanionInstalled = serverCompanionStatus.Item1;
		antiCheatWebSnapshot.ServerCompanionVersionMatches = serverCompanionStatus.Item2;
		antiCheatWebSnapshot.ServerCompanionVersion = serverCompanionStatus.Item3;
		antiCheatWebSnapshot.ServerCompanionModId = "newfiesanticheatclient";
		antiCheatWebSnapshot.ExpectedClientVersion = serverCompanionStatus.Item3;
		antiCheatWebSnapshot.ProtocolVersion = 13;
		antiCheatWebSnapshot.ChallengeTimeoutSeconds = config.ChallengeTimeoutSeconds;
		antiCheatWebSnapshot.StartupChallengeTimeoutSeconds = config.StartupChallengeTimeoutSeconds;
		antiCheatWebSnapshot.StartupChallengeHeartbeatExtensionSeconds = config.StartupChallengeHeartbeatExtensionSeconds;
		antiCheatWebSnapshot.StartupChallengeHardLimitSeconds = config.StartupChallengeHardLimitSeconds;
		antiCheatWebSnapshot.ClientHelloGraceSeconds = config.ClientHelloGraceSeconds;
		antiCheatWebSnapshot.ClientHelloDisconnectDelaySeconds = config.ClientHelloDisconnectDelaySeconds;
		antiCheatWebSnapshot.HeartbeatTimeoutSeconds = config.HeartbeatTimeoutSeconds;
		antiCheatWebSnapshot.RescanMinutes = config.RescanMinutes;
		antiCheatWebSnapshot.RuntimeRescanOnChange = config.RuntimeRescanOnChange;
		antiCheatWebSnapshot.RuntimeChangeRescanCooldownSeconds = config.RuntimeChangeRescanCooldownSeconds;
		antiCheatWebSnapshot.FingerprintReleaseDepth = config.FingerprintReleaseDepth;
		antiCheatWebSnapshot.ForbiddenModIds = (from x in config.ForbiddenModIds
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
		antiCheatWebSnapshot.ActiveBlacklistProfileId = config.ActiveBlacklistProfileId ?? "";
		antiCheatWebSnapshot.BlacklistProfiles = (from x in (config.BlacklistProfiles ?? new List<AntiCheatBlacklistProfile>()).OrderBy((AntiCheatBlacklistProfile x) => x.Name, StringComparer.OrdinalIgnoreCase)
			select new AntiCheatBlacklistProfileWebSnapshot
			{
				Id = x.Id,
				Name = x.Name,
				ModCount = (x.ModIds ?? new List<string>()).Count,
				FingerprintCount = (x.Fingerprints ?? new List<AntiCheatForbiddenFingerprint>()).Count,
				ModIds = (x.ModIds ?? new List<string>()).ToList(),
				CreatedUtc = x.CreatedUtc,
				UpdatedUtc = x.UpdatedUtc
			}).ToList();
		antiCheatWebSnapshot.RequiredClientModIds = (from x in config.RequiredClientModIds
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
		antiCheatWebSnapshot.RequireApprovedClientBuild = config.RequireApprovedClientBuild;
		antiCheatWebSnapshot.RequireHardenedClientCapabilities = config.RequireHardenedClientCapabilities;
		antiCheatWebSnapshot.RequireCurrentClientRelease = config.RequireCurrentClientRelease;
		antiCheatWebSnapshot.ApprovedClientBuildHashes = config.ApprovedClientBuildHashes.ToList();
		antiCheatWebSnapshot.SimilarityDetectionEnabled = config.SimilarityDetectionEnabled;
		antiCheatWebSnapshot.SimilarityReviewThresholdPercent = config.SimilarityReviewThresholdPercent;
		antiCheatWebSnapshot.SimilarityHighThresholdPercent = config.SimilarityHighThresholdPercent;
		antiCheatWebSnapshot.RuntimeSurfaceInspectionEnabled = config.RuntimeSurfaceInspectionEnabled;
		antiCheatWebSnapshot.SuspiciousHotkeyInspectionEnabled = config.SuspiciousHotkeyInspectionEnabled;
		antiCheatWebSnapshot.SuspiciousClientCommandInspectionEnabled = config.SuspiciousClientCommandInspectionEnabled;
		antiCheatWebSnapshot.CanonicalServerModIntegrityEnabled = config.CanonicalServerModIntegrityEnabled;
		antiCheatWebSnapshot.SuspectPackageCollectionEnabled = config.SuspectPackageCollectionEnabled;
		antiCheatWebSnapshot.SuspectPackageCollectionMaxMb = config.SuspectPackageCollectionMaxMb;
		antiCheatWebSnapshot.SuspectPackageCollectionTimeoutSeconds = config.SuspectPackageCollectionTimeoutSeconds;
		antiCheatWebSnapshot.SuspectPackageUploadKbps = config.SuspectPackageUploadKbps;
		antiCheatWebSnapshot.SuspectPackageDeclineAction = config.SuspectPackageDeclineAction;
		antiCheatWebSnapshot.SuspectPackageDeclineTempBanHours = config.SuspectPackageDeclineTempBanHours;
		antiCheatWebSnapshot.SuspectPackageRetentionDays = 15;
		antiCheatWebSnapshot.SuspectPackageFolder = "NewfiesServerEssentialsSuite/SuspectedHacks";
		antiCheatWebSnapshot.SuspectPackageStoredCount = evidenceStats.Item1;
		antiCheatWebSnapshot.SuspectPackageStoredBytes = evidenceStats.Item2;
		antiCheatWebSnapshot.ProtectedAssetInspectionEnabled = config.ProtectedAssetInspectionEnabled;
		antiCheatWebSnapshot.SuspiciousRuntimeTerms = (config.SuspiciousRuntimeTerms ?? new List<string>()).ToList();
		antiCheatWebSnapshot.BlacklistedRuntimeTermCount = GetBlacklistedRuntimeTerms().Count;
		antiCheatWebSnapshot.ProtectedAssetPatterns = (config.ProtectedAssetPatterns ?? new List<string>()).ToList();
		antiCheatWebSnapshot.CanonicalServerModCount = GetCanonicalModSnapshot().Count;
		antiCheatWebSnapshot.ForbiddenFingerprints = config.ForbiddenFingerprints.Select((AntiCheatForbiddenFingerprint fp) => new AntiCheatForbiddenFingerprintWebSnapshot
		{
			ModId = fp.ModId,
			HasPackageHash = !string.IsNullOrWhiteSpace(fp.PackageSha256),
			HasContentHash = !string.IsNullOrWhiteSpace(fp.ContentSha256),
			PackageSha256 = fp.PackageSha256,
			ContentSha256 = fp.ContentSha256,
			Source = fp.Source,
			ResolvedVersion = fp.ResolvedVersion,
			ResolvedFileName = fp.ResolvedFileName,
			ResolvedUtc = fp.ResolvedUtc,
			AssemblyNameCount = fp.AssemblyNames.Count,
			AssemblyHashCount = fp.AssemblySha256s.Count,
			AssemblyStructureHashCount = fp.AssemblyStructureSha256s.Count,
			AssemblyBehaviorHashCount = fp.AssemblyBehaviorSha256s.Count,
			ReleaseCount = (fp.Releases ?? new List<AntiCheatReleaseFingerprint>()).Count,
			ResolvedPackageBytes = ((fp.Releases ?? new List<AntiCheatReleaseFingerprint>()).FirstOrDefault((AntiCheatReleaseFingerprint r) => string.Equals(r.Version, fp.ResolvedVersion, StringComparison.OrdinalIgnoreCase))?.PackageBytes ?? (fp.Releases ?? new List<AntiCheatReleaseFingerprint>()).FirstOrDefault()?.PackageBytes ?? 0),
			ResolvedDllCount = ((fp.Releases ?? new List<AntiCheatReleaseFingerprint>()).FirstOrDefault((AntiCheatReleaseFingerprint r) => string.Equals(r.Version, fp.ResolvedVersion, StringComparison.OrdinalIgnoreCase))?.DllCount ?? (fp.Releases ?? new List<AntiCheatReleaseFingerprint>()).FirstOrDefault()?.DllCount ?? 0),
			ResolvedDllBytes = ((fp.Releases ?? new List<AntiCheatReleaseFingerprint>()).FirstOrDefault((AntiCheatReleaseFingerprint r) => string.Equals(r.Version, fp.ResolvedVersion, StringComparison.OrdinalIgnoreCase))?.DllBytes ?? (fp.Releases ?? new List<AntiCheatReleaseFingerprint>()).FirstOrDefault()?.DllBytes ?? 0),
			ResolvedVersions = (from x in fp.Releases ?? new List<AntiCheatReleaseFingerprint>()
				select x.Version into x
				where !string.IsNullOrWhiteSpace(x)
				select x).ToList(),
			SimilarityAssemblyCount = (fp.Releases ?? new List<AntiCheatReleaseFingerprint>()).SelectMany((AntiCheatReleaseFingerprint x) => x.Assemblies ?? new List<AntiCheatReleaseAssemblyFingerprint>()).Count((AntiCheatReleaseAssemblyFingerprint x) => x.StructureTokenCount >= 20 && (x.SimilarityMinHash?.Count ?? 0) >= 64),
			ManualEntryCount = (fp.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).Count,
			ManualEntries = (fp.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).Select((AntiCheatManualFingerprintEntry entry) => new AntiCheatManualFingerprintWebSnapshot
			{
				Id = entry.Id,
				Label = entry.Label,
				Version = entry.Version,
				FileName = entry.FileName,
				Notes = entry.Notes,
				PackageSha256 = entry.PackageSha256,
				ContentSha256 = entry.ContentSha256,
				AssemblySha256s = (entry.AssemblySha256s ?? new List<string>()).ToList(),
				AssemblyMvids = (entry.AssemblyMvids ?? new List<string>()).ToList(),
				AssemblyStructureSha256s = (entry.AssemblyStructureSha256s ?? new List<string>()).ToList(),
				AssemblyBehaviorSha256s = (entry.AssemblyBehaviorSha256s ?? new List<string>()).ToList(),
				CreatedUtc = entry.CreatedUtc,
				UpdatedUtc = entry.UpdatedUtc
			}).OrderBy((AntiCheatManualFingerprintWebSnapshot entry) => entry.Label, StringComparer.OrdinalIgnoreCase).ThenBy((AntiCheatManualFingerprintWebSnapshot entry) => entry.Id, StringComparer.OrdinalIgnoreCase)
				.ToList()
		}).ToList();
		antiCheatWebSnapshot.ConsequenceRules = config.ConsequenceRules.Select(ToWebRule).ToList();
		antiCheatWebSnapshot.PacketGuard = BuildPacketGuardSnapshot();
		antiCheatWebSnapshot.OreShield = oreShield?.GetSnapshot() ?? BuildOreShieldFallbackSnapshot((!config.OreShieldEnabled) ? "Disabled by configuration." : (runtimeAvailable ? "Not active." : "Anti-Cheat runtime is unavailable."));
		antiCheatWebSnapshot.History = historyStore?.GetSummary() ?? new AntiCheatHistorySummaryWebSnapshot
		{
			Enabled = config.HistoryEnabled,
			RetentionDays = config.HistoryRetentionDays,
			MaxRecords = config.HistoryMaxRecords
		};
		antiCheatWebSnapshot.OnlinePlayers = onlineUids.Count;
		antiCheatWebSnapshot.TrackedPlayers = list.Count;
		antiCheatWebSnapshot.ClientsConnected = list.Count((AntiCheatSession x) => x.ClientInstalled);
		antiCheatWebSnapshot.Clean = list.Count((AntiCheatSession x) => x.Status == AntiCheatPlayerStatus.Clean);
		antiCheatWebSnapshot.Review = list.Count((AntiCheatSession x) => x.Status == AntiCheatPlayerStatus.Review);
		antiCheatWebSnapshot.Suspicious = list.Count((AntiCheatSession x) => x.Status == AntiCheatPlayerStatus.Suspicious);
		antiCheatWebSnapshot.AwaitingOrScanning = list.Count((AntiCheatSession x) =>
		{
			AntiCheatPlayerStatus status = x.Status;
			return (uint)(status - 1) <= 1u;
		});
		antiCheatWebSnapshot.MissingOrTimedOut = list.Count((AntiCheatSession x) =>
		{
			bool flag = !x.ClientInstalled;
			bool flag2 = flag;
			if (!flag2)
			{
				AntiCheatPlayerStatus status = x.Status;
				bool flag3 = (uint)(status - 6) <= 2u;
				flag2 = flag3;
			}
			return flag2;
		});
		antiCheatWebSnapshot.ModDbHealth = BuildModDbHealthSnapshot();
		antiCheatWebSnapshot.ApprovedClientModFingerprints = GetApprovedClientModFingerprintSnapshot();
		antiCheatWebSnapshot.RecentConnectionDiagnostics = GetConnectionDiagnosticsSnapshot();
		antiCheatWebSnapshot.Players = (from x in list
			select ToWebPlayer(x, includeConnectionDiagnostics: false, includeDetails: false) into x
			orderby x.RiskScore descending, x.PlayerName
			select x).ToList();
		return antiCheatWebSnapshot;
	}

	private AntiCheatPacketGuardWebSnapshot BuildPacketGuardSnapshot()
	{
		bool tPRunesDetected = false;
		bool th3EssentialsDetected = false;
		try
		{
			tPRunesDetected = api.ModLoader.IsModEnabled("tprunes");
		}
		catch
		{
		}
		try
		{
			th3EssentialsDetected = api.ModLoader.IsModEnabled("th3essentials");
		}
		catch
		{
		}
		PacketGuardTpsSnapshot packetGuardTpsSnapshot = packetGuard?.GetTpsSnapshot() ?? BuildInactiveTpsSnapshot();
		return new AntiCheatPacketGuardWebSnapshot
		{
			ConfirmedSpeedRepeatRisk = config.PacketGuardConfirmedSpeedRepeatRisk,
			KickRepeatedConfirmedSpeed = config.PacketGuardKickRepeatedConfirmedSpeed,
			MovementModeLimitsEnabled = config.PacketGuardMovementModeLimitsEnabled,
			MaxHorizontalSpeed = config.PacketGuardMaxHorizontalSpeed,
			GroundMaxHorizontalSpeed = config.PacketGuardGroundMaxHorizontalSpeed,
			SwimmingMaxHorizontalSpeed = config.PacketGuardSwimmingMaxHorizontalSpeed,
			AirborneMaxHorizontalSpeed = config.PacketGuardAirborneMaxHorizontalSpeed,
			MountProfilesEnabled = config.PacketGuardMountProfilesEnabled,
			MountProfileEnforcementEnabled = config.PacketGuardMountProfileEnforcementEnabled,
			UnknownMountEnforcementEnabled = config.PacketGuardUnknownMountEnforcementEnabled,
			MountProfileConfirmSamples = config.PacketGuardMountProfileConfirmSamples,
			MountProfileConfirmWindowMs = config.PacketGuardMountProfileConfirmWindowMs,
			MountRiderCorrelationEnabled = config.PacketGuardMountRiderCorrelationEnabled,
			DraconisMaxHorizontalSpeed = config.PacketGuardDraconisMaxHorizontalSpeed,
			DraconisMaxVerticalSpeed = config.PacketGuardDraconisMaxVerticalSpeed,
			DraconisMaxAcceleration = config.PacketGuardDraconisMaxAcceleration,
			DraconisMaxRiderDistance = config.PacketGuardDraconisMaxRiderDistance,
			DraconisMaxRiderRelativeSpeed = config.PacketGuardDraconisMaxRiderRelativeSpeed,
			ShipwrightMaxHorizontalSpeed = config.PacketGuardShipwrightMaxHorizontalSpeed,
			ShipwrightMaxVerticalSpeed = config.PacketGuardShipwrightMaxVerticalSpeed,
			ShipwrightMaxAcceleration = config.PacketGuardShipwrightMaxAcceleration,
			ShipwrightMaxRiderDistance = config.PacketGuardShipwrightMaxRiderDistance,
			ShipwrightMaxRiderRelativeSpeed = config.PacketGuardShipwrightMaxRiderRelativeSpeed,
			UnknownMountMaxHorizontalSpeed = config.PacketGuardUnknownMountMaxHorizontalSpeed,
			UnknownMountMaxVerticalSpeed = config.PacketGuardUnknownMountMaxVerticalSpeed,
			UnknownMountMaxAcceleration = config.PacketGuardUnknownMountMaxAcceleration,
			UnknownMountMaxRiderDistance = config.PacketGuardUnknownMountMaxRiderDistance,
			UnknownMountMaxRiderRelativeSpeed = config.PacketGuardUnknownMountMaxRiderRelativeSpeed,
			Enabled = config.PacketGuardEnabled,
			IngressEnabled = config.PacketGuardIngressEnabled,
			IngressActive = (packetGuard?.IsIngressActive ?? false),
			IngressStatus = (packetGuard?.IngressStatus ?? "PacketGuard not running"),
			IngressMaxPacketKiB = config.PacketGuardIngressMaxPacketKiB,
			MaxViewDistanceBlocks = config.PacketGuardMaxViewDistanceBlocks,
			IngressPacketsPerSecond = config.PacketGuardIngressPacketsPerSecond,
			IngressPacketBurst = config.PacketGuardIngressPacketBurst,
			BlockPacketsPerSecond = config.PacketGuardBlockPacketsPerSecond,
			BlockPacketBurst = config.PacketGuardBlockPacketBurst,
			SettingsPacketsPerSecond = config.PacketGuardSettingsPacketsPerSecond,
			SettingsPacketBurst = config.PacketGuardSettingsPacketBurst,
			PlayerWarningEnabled = config.PacketGuardPlayerWarningEnabled,
			PlayerWarningCooldownSeconds = config.PacketGuardPlayerWarningCooldownSeconds,
			AccelerationSampleMinMs = config.PacketGuardAccelerationSampleMinMs,
			AccelerationSmoothingFactor = config.PacketGuardAccelerationSmoothingFactor,
			NoclipEnabled = config.PacketGuardNoclipEnabled,
			NoclipRequiredCollisionSamples = config.PacketGuardNoclipRequiredCollisionSamples,
			NoclipMinPenetrationDepth = config.PacketGuardNoclipMinPenetrationDepth,
			NoclipEndpointMinNetMovement = config.PacketGuardNoclipEndpointMinNetMovement,
			MountAccelerationMinSpeedRatio = config.PacketGuardMountAccelerationMinSpeedRatio,
			DiagnosticContextEnabled = config.PacketGuardDiagnosticContextEnabled,
			DiagnosticSuppressionEnabled = config.PacketGuardDiagnosticSuppressionEnabled,
			DiagnosticSpeedHistorySamples = config.PacketGuardDiagnosticSpeedHistorySamples,
			MovementEnabled = config.PacketGuardMovementEnabled,
			MovementSampleMs = config.PacketGuardMovementSampleMs,
			HardMoveDistance = config.PacketGuardHardMoveDistance,
			MovementConfirmSamples = config.PacketGuardMovementConfirmSamples,
			MovementConfirmWindowMs = config.PacketGuardMovementConfirmWindowMs,
			MovementExtremeMultiplier = config.PacketGuardMovementExtremeMultiplier,
			CorrectionGraceMs = config.PacketGuardCorrectionGraceMs,
			RejectUnauthorizedMovement = config.PacketGuardRejectUnauthorizedMovement,
			ExemptAdmins = config.PacketGuardExemptAdmins,
			ExemptMountedPlayers = config.PacketGuardExemptMountedPlayers,
			MountGraceMs = config.PacketGuardMountGraceMs,
			EngineTeleportGraceMs = config.PacketGuardEngineTeleportGraceMs,
			TPRunesCompatibility = config.PacketGuardTPRunesCompatibility,
			TPRunesGraceMs = config.PacketGuardTPRunesGraceMs,
			TPRunesDetected = tPRunesDetected,
			Th3EssentialsSpawnCompatibility = config.PacketGuardTh3EssentialsSpawnCompatibility,
			Th3EssentialsSpawnGraceMs = config.PacketGuardTh3EssentialsSpawnGraceMs,
			Th3EssentialsDetected = th3EssentialsDetected,
			ReachEnabled = config.PacketGuardReachEnabled,
			ReachTolerance = config.PacketGuardReachTolerance,
			ReachLatencyCompensation = config.PacketGuardReachLatencyCompensation,
			ReachMaxLatencyCompensation = config.PacketGuardReachMaxLatencyCompensation,
			ReachPositionHistoryEnabled = config.PacketGuardReachPositionHistoryEnabled,
			ReachPositionHistoryMs = config.PacketGuardReachPositionHistoryMs,
			ReachMaxRewindMs = config.PacketGuardReachMaxRewindMs,
			ReachUseExtraTolerance = config.PacketGuardReachUseExtraTolerance,
			ReachBreakExtraTolerance = config.PacketGuardReachBreakExtraTolerance,
			ReachNearThresholdStrikes = config.PacketGuardReachNearThresholdStrikes,
			ReachStrikeWindowMs = config.PacketGuardReachStrikeWindowMs,
			ReachStrikeDecayMs = config.PacketGuardReachStrikeDecayMs,
			ReachHardExcess = config.PacketGuardReachHardExcess,
			RejectOutOfReachActions = config.PacketGuardRejectOutOfReachActions,
			MiningEnabled = config.PacketGuardMiningEnabled,
			VeinWindowMs = config.PacketGuardVeinWindowMs,
			ConnectedBreakLimit = config.PacketGuardConnectedBreakLimit,
			MiningDuplicateTargetMs = config.PacketGuardMiningDuplicateTargetMs,
			RejectConnectedBreakBursts = config.PacketGuardRejectConnectedBreakBursts,
			MiningCadenceValidationEnabled = config.PacketGuardMiningCadenceValidationEnabled,
			MiningCadenceMinExpectedMs = config.PacketGuardMiningCadenceMinExpectedMs,
			MiningCadenceSuspiciousRatio = config.PacketGuardMiningCadenceSuspiciousRatio,
			MiningCadenceConfirmations = config.PacketGuardMiningCadenceConfirmations,
			MiningCadenceConfirmationWindowMs = config.PacketGuardMiningCadenceConfirmationWindowMs,
			RejectImpossibleMiningCadence = config.PacketGuardRejectImpossibleMiningCadence,
			TpsAwarenessEnabled = config.PacketGuardTpsAwarenessEnabled,
			TpsDegradedPercent = config.PacketGuardTpsDegradedPercent,
			TpsCriticalPercent = config.PacketGuardTpsCriticalPercent,
			TpsMaxTimingMultiplier = config.PacketGuardTpsMaxTimingMultiplier,
			TpsSuppressTimingRejectsWhenCritical = config.PacketGuardTpsSuppressTimingRejectsWhenCritical,
			TpsReady = packetGuardTpsSnapshot.Ready,
			TpsTarget = packetGuardTpsSnapshot.TargetTps,
			TpsEstimated = packetGuardTpsSnapshot.EstimatedTps,
			TpsHealthPercent = packetGuardTpsSnapshot.HealthPercent,
			TpsTimingMultiplier = packetGuardTpsSnapshot.TimingMultiplier,
			TpsBand = packetGuardTpsSnapshot.Band,
			TpsSampleCount = packetGuardTpsSnapshot.SampleCount,
			PerFamilyRiskEnabled = config.PerFamilyRiskEnabled,
			MovementRiskDecaySeconds = config.MovementRiskDecaySeconds,
			MovementRiskReviewThreshold = config.MovementRiskReviewThreshold,
			MovementRiskSuspiciousThreshold = config.MovementRiskSuspiciousThreshold,
			ReachRiskDecaySeconds = config.ReachRiskDecaySeconds,
			ReachRiskReviewThreshold = config.ReachRiskReviewThreshold,
			ReachRiskSuspiciousThreshold = config.ReachRiskSuspiciousThreshold,
			MiningRiskDecaySeconds = config.MiningRiskDecaySeconds,
			MiningRiskReviewThreshold = config.MiningRiskReviewThreshold,
			MiningRiskSuspiciousThreshold = config.MiningRiskSuspiciousThreshold,
			PacketAbuseRiskDecaySeconds = config.PacketAbuseRiskDecaySeconds,
			PacketAbuseRiskReviewThreshold = config.PacketAbuseRiskReviewThreshold,
			PacketAbuseRiskSuspiciousThreshold = config.PacketAbuseRiskSuspiciousThreshold,
			ClientIntegrityRiskDecaySeconds = config.ClientIntegrityRiskDecaySeconds,
			ClientIntegrityRiskReviewThreshold = config.ClientIntegrityRiskReviewThreshold,
			ClientIntegrityRiskSuspiciousThreshold = config.ClientIntegrityRiskSuspiciousThreshold,
			IncidentCorrelationEnabled = config.IncidentCorrelationEnabled,
			IncidentCorrelationWindowSeconds = config.IncidentCorrelationWindowSeconds,
			IncidentCorrelationRequiredCategories = config.IncidentCorrelationRequiredCategories,
			IncidentCorrelationMaxRiskBonus = config.IncidentCorrelationMaxRiskBonus,
			TrustedTeleportSources = (packetGuard?.GetTrustedTeleportSources().Select((PacketGuardService.TrustedTeleportSourceSnapshot x) => new AntiCheatTrustedTeleportSourceWebSnapshot
			{
				Code = x.Code,
				Name = x.Name,
				Status = x.Status,
				TrustScope = x.TrustScope,
				Verification = x.Verification,
				ObservedCount = x.ObservedCount,
				LastSeenUtc = x.LastSeenUtc,
				LastPlayerName = x.LastPlayerName,
				LastReason = x.LastReason,
				RecentEvents = x.RecentEvents.Select((PacketGuardService.TrustedTeleportEventSnapshot e) => new AntiCheatTrustedTeleportEventWebSnapshot
				{
					Utc = e.Utc,
					PlayerName = e.PlayerName,
					PlayerUid = e.PlayerUid,
					FromX = e.FromX,
					FromY = e.FromY,
					FromZ = e.FromZ,
					FromDimension = e.FromDimension,
					ToX = e.ToX,
					ToY = e.ToY,
					ToZ = e.ToZ,
					ToDimension = e.ToDimension,
					Reason = e.Reason,
					Verification = e.Verification
				}).ToList()
			}).ToList() ?? new List<AntiCheatTrustedTeleportSourceWebSnapshot>()),
			PacketSuppressions = (packetGuard?.GetPacketSuppressions().Select((PacketGuardService.PacketSuppressionSnapshot x) => new AntiCheatPacketSuppressionWebSnapshot
			{
				Code = x.Code,
				Name = x.Name,
				Category = x.Category,
				Description = x.Description,
				Count = x.Count,
				LastSeenUtc = x.LastSeenUtc,
				LastPlayerName = x.LastPlayerName,
				LastDetail = x.LastDetail
			}).ToList() ?? new List<AntiCheatPacketSuppressionWebSnapshot>())
		};
	}

	private PacketGuardTpsSnapshot BuildInactiveTpsSnapshot()
	{
		double num = 50.0;
		try
		{
			double num2 = api.Server.Config.TickTime;
			if (double.IsFinite(num2) && num2 >= 5.0 && num2 <= 1000.0)
			{
				num = num2;
			}
		}
		catch
		{
		}
		double num3 = 1000.0 / Math.Max(1.0, num);
		return new PacketGuardTpsSnapshot(config.PacketGuardTpsAwarenessEnabled, ready: false, num3, num3, 100.0, 1.0, config.PacketGuardTpsAwarenessEnabled ? "Inactive" : "Disabled", 0, num, suppressTimingRejections: false);
	}

	public AntiCheatHistoryWebSnapshot GetHistorySnapshot(string playerFilter = "", int limit = 250, string findingFilter = "")
	{
		return historyStore?.GetSnapshot(playerFilter, limit, findingFilter) ?? new AntiCheatHistoryWebSnapshot
		{
			Enabled = config.HistoryEnabled,
			RetentionDays = config.HistoryRetentionDays,
			MaxRecords = config.HistoryMaxRecords,
			PlayerFilter = (playerFilter ?? "").Trim(),
			FindingFilter = (findingFilter ?? "").Trim()
		};
	}

	public (bool Success, string Message, int Cleared) ClearHistory()
	{
		if (historyStore == null)
		{
			return (Success: false, Message: "Anti-cheat history store is unavailable.", Cleared: 0);
		}
		int num = historyStore.ClearAll();
		return (Success: true, Message: $"Cleared {num} persistent anti-cheat report{((num == 1) ? "" : "s")}.", Cleared: num);
	}

	public (bool Success, string Message) SaveModuleSettings(bool enabled)
	{
		if (enabled)
		{
			(bool, bool, string) serverCompanionStatus = GetServerCompanionStatus();
			if (!serverCompanionStatus.Item1)
			{
				return (Success: false, Message: "Cannot enable Anti-Cheat: server add-on 'newfiesanticheatclient' is not installed.");
			}
			if (!serverCompanionStatus.Item2)
			{
				return (Success: false, Message: "Cannot enable Anti-Cheat: the installed server companion does not expose a usable semantic version, so the required client version cannot be derived safely.");
			}
		}
		if (enabled == config.Enabled)
		{
			return (Success: true, Message: "Anti-Cheat is already " + (enabled ? "enabled" : "disabled") + ".");
		}
		bool enabled2 = config.Enabled;
		config.Enabled = enabled;
		if (!TrySaveConfig(out string error))
		{
			config.Enabled = enabled2;
			return (Success: false, Message: error);
		}
		if (!enabled)
		{
			DeactivateRuntime();
			QueueServerInstalledModDbBaseline();
			runtimeAvailabilityReason = "Disabled by configuration.";
			lastActivationError = "";
			api.Logger.Notification("[ServerEssentials/AntiCheat] Anti-Cheat disabled from an administration UI. Client scanning, repack detection, PacketGuard, Ore Shield and automatic consequences are now inactive.");
			return (Success: true, Message: "Anti-Cheat disabled. The rest of Server Essentials remains active; stored reports remain available for review.");
		}
		evaluator = new AntiCheatEvaluator(config);
		CaptureServerLoadedModIds();
		StartCanonicalModFingerprintBuild();
		QueueServerInstalledModDbBaseline();
		InvalidateProtectedAssetManifest();
		if (!ActivateRuntime(out string error2))
		{
			config.Enabled = false;
			TrySaveConfig(out string _);
			return (Success: false, Message: "Anti-Cheat could not be enabled and was returned to the disabled state. " + error2);
		}
		return (Success: true, Message: "Anti-Cheat enabled. Client integrity, repack detection, PacketGuard, Ore Shield and configured consequences are active.");
	}

	private OreShieldWebSnapshot BuildOreShieldFallbackSnapshot(string status)
	{
		return new OreShieldWebSnapshot
		{
			Enabled = config.OreShieldEnabled,
			Active = false,
			Status = status,
			ProtectModdedOres = config.OreShieldProtectModdedOres,
			RevealOnUnknownNeighbor = config.OreShieldRevealOnUnknownNeighbor,
			StrictOreMatching = config.OreShieldStrictOreMatching,
			RevealRadiusBlocks = config.OreShieldRevealRadiusBlocks,
			RevealWholeVeinOnMine = config.OreShieldRevealWholeVeinOnMine,
			MaxVeinRevealBlocks = config.OreShieldMaxVeinRevealBlocks,
			DiscoveryRangeBlocks = config.OreShieldDiscoveryRangeBlocks,
			VisibilityScanIntervalMs = config.OreShieldVisibilityScanIntervalMs,
			MaxLosChecksPerPass = config.OreShieldMaxLosChecksPerPass,
			ViewConeDegrees = config.OreShieldViewConeDegrees,
			IdleRescanMs = config.OreShieldIdleRescanMs,
			LeakAuditEnabled = config.OreShieldLeakAuditEnabled,
			ChunkCacheEnabled = config.OreShieldChunkCacheEnabled,
			ChunkCacheMiB = config.OreShieldChunkCacheMiB,
			MaxCachedChunks = config.OreShieldMaxCachedChunks,
			AdditionalOrePatterns = new List<string>(config.OreShieldAdditionalOrePatterns ?? new List<string>()),
			ExcludedBlockPatterns = new List<string>(config.OreShieldExcludedBlockPatterns ?? new List<string>())
		};
	}

	private void RestartOreShieldRuntime()
	{
		OreShieldRuntimeState restoredRuntimeState = null;
		try
		{
			restoredRuntimeState = oreShield?.ExportRuntimeState();
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Ore Shield runtime state could not be preserved across reload: {0}", ex.Message);
		}
		oreShield?.Dispose();
		oreShield = null;
		if (runtimeAvailable && config.Enabled)
		{
			oreShield = new OreShieldService(api, config, SupportsOreShieldClientSync, restoredRuntimeState);
			bool flag = oreShield.Start(out string message);
			if (config.OreShieldEnabled && !flag)
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat] Ore Shield configuration reload did not activate; other Anti-Cheat layers remain active. {0}", message);
			}
		}
	}

	public string GetFullConfigJson()
	{
		return JsonSerializer.Serialize(config, new JsonSerializerOptions
		{
			WriteIndented = true
		});
	}

	public (bool Success, string Message) SaveFullConfigJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return (Success: false, Message: "Configuration JSON is required.");
		}
		AntiCheatConfig antiCheatConfig;
		try
		{
			antiCheatConfig = JsonSerializer.Deserialize<AntiCheatConfig>(json, new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			});
		}
		catch (Exception ex)
		{
			return (Success: false, Message: "Invalid Anti-Cheat configuration JSON: " + ex.Message);
		}
		if (antiCheatConfig == null)
		{
			return (Success: false, Message: "Invalid Anti-Cheat configuration JSON.");
		}
		antiCheatConfig.Enabled = config.Enabled;
		antiCheatConfig.SchemaVersion = config.SchemaVersion;
		antiCheatConfig.Clamp();
		string fullConfigJson = GetFullConfigJson();
		PropertyInfo[] properties = typeof(AntiCheatConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public);
		PropertyInfo[] array = properties;
		foreach (PropertyInfo propertyInfo in array)
		{
			if (propertyInfo.CanRead && propertyInfo.CanWrite && !(propertyInfo.Name == "Enabled") && !(propertyInfo.Name == "SchemaVersion"))
			{
				propertyInfo.SetValue(config, propertyInfo.GetValue(antiCheatConfig));
			}
		}
		config.Clamp();
		if (!TrySaveConfig(out string error))
		{
			try
			{
				AntiCheatConfig antiCheatConfig2 = JsonSerializer.Deserialize<AntiCheatConfig>(fullConfigJson);
				if (antiCheatConfig2 != null)
				{
					PropertyInfo[] array2 = properties;
					foreach (PropertyInfo propertyInfo2 in array2)
					{
						if (propertyInfo2.CanRead && propertyInfo2.CanWrite && !(propertyInfo2.Name == "Enabled") && !(propertyInfo2.Name == "SchemaVersion"))
						{
							propertyInfo2.SetValue(config, propertyInfo2.GetValue(antiCheatConfig2));
						}
					}
					config.Clamp();
				}
			}
			catch
			{
			}
			return (Success: false, Message: error);
		}
		CaptureServerCoreAssemblyBaseline();
		StartCanonicalModFingerprintBuild();
		InvalidateProtectedAssetManifest();
		QueueServerInstalledModDbBaseline();
		RestartOreShieldRuntime();
		ReevaluateCurrentSessions();
		return (Success: true, Message: "Full Anti-Cheat configuration saved. Ore Shield was reloaded immediately; module enable/disable and schema version remain lifecycle-managed separately.");
	}

	public (bool Success, string Message) SaveOreShieldSettings(AntiCheatOreShieldSaveRequest request)
	{
		if (request == null)
		{
			return (Success: false, Message: "Ore Shield settings are required.");
		}
		bool oldEnabled = config.OreShieldEnabled;
		bool oldProtectModded = config.OreShieldProtectModdedOres;
		bool oldRevealUnknown = config.OreShieldRevealOnUnknownNeighbor;
		bool oldStrict = config.OreShieldStrictOreMatching;
		int oldRevealRadius = config.OreShieldRevealRadiusBlocks;
		bool oldRevealWholeVein = config.OreShieldRevealWholeVeinOnMine;
		int oldMaxVeinRevealBlocks = config.OreShieldMaxVeinRevealBlocks;
		int oldDiscoveryRange = config.OreShieldDiscoveryRangeBlocks;
		int oldVisibilityInterval = config.OreShieldVisibilityScanIntervalMs;
		int oldMaxLosChecks = config.OreShieldMaxLosChecksPerPass;
		int oldViewCone = config.OreShieldViewConeDegrees;
		int oldIdleRescan = config.OreShieldIdleRescanMs;
		bool oldLeakAudit = config.OreShieldLeakAuditEnabled;
		bool oldCacheEnabled = config.OreShieldChunkCacheEnabled;
		int oldCacheMiB = config.OreShieldChunkCacheMiB;
		int oldMaxCached = config.OreShieldMaxCachedChunks;
		List<string> oldAdditional = new List<string>(config.OreShieldAdditionalOrePatterns ?? new List<string>());
		List<string> oldExcluded = new List<string>(config.OreShieldExcludedBlockPatterns ?? new List<string>());
		config.OreShieldEnabled = request.Enabled;
		config.OreShieldProtectModdedOres = request.ProtectModdedOres;
		config.OreShieldRevealOnUnknownNeighbor = false;
		config.OreShieldStrictOreMatching = request.StrictOreMatching;
		config.OreShieldRevealRadiusBlocks = 0;
		config.OreShieldRevealWholeVeinOnMine = request.RevealWholeVeinOnMine;
		config.OreShieldMaxVeinRevealBlocks = request.MaxVeinRevealBlocks;
		config.OreShieldDiscoveryRangeBlocks = request.DiscoveryRangeBlocks;
		config.OreShieldVisibilityScanIntervalMs = request.VisibilityScanIntervalMs;
		config.OreShieldMaxLosChecksPerPass = request.MaxLosChecksPerPass;
		config.OreShieldViewConeDegrees = request.ViewConeDegrees;
		config.OreShieldIdleRescanMs = request.IdleRescanMs;
		config.OreShieldLeakAuditEnabled = request.LeakAuditEnabled;
		config.OreShieldChunkCacheEnabled = request.ChunkCacheEnabled;
		config.OreShieldChunkCacheMiB = request.ChunkCacheMiB;
		config.OreShieldMaxCachedChunks = request.MaxCachedChunks;
		config.OreShieldAdditionalOrePatterns = request.AdditionalOrePatterns ?? new List<string>();
		config.OreShieldExcludedBlockPatterns = request.ExcludedBlockPatterns ?? new List<string>();
		config.Clamp();
		if (!TrySaveConfig(out string error))
		{
			Restore();
			return (Success: false, Message: error);
		}
		RestartOreShieldRuntime();
		OreShieldWebSnapshot oreShieldWebSnapshot = oreShield?.GetSnapshot() ?? new OreShieldWebSnapshot
		{
			Enabled = config.OreShieldEnabled,
			Active = false,
			Status = (config.OreShieldEnabled ? "Not active." : "Disabled by configuration.")
		};
		string value;
		if (oreShieldWebSnapshot.Active)
		{
			value = "active";
		}
		else
		{
			value = (oreShieldWebSnapshot.Enabled ? "configured but unavailable" : "disabled");
		}
		return (Success: true, Message: $"Ore Shield settings saved and runtime reloaded: {value}. Protected ore variants: {oreShieldWebSnapshot.ProtectedOreVariants}.");
		void Restore()
		{
			config.OreShieldEnabled = oldEnabled;
			config.OreShieldProtectModdedOres = oldProtectModded;
			config.OreShieldRevealOnUnknownNeighbor = oldRevealUnknown;
			config.OreShieldStrictOreMatching = oldStrict;
			config.OreShieldRevealRadiusBlocks = oldRevealRadius;
			config.OreShieldRevealWholeVeinOnMine = oldRevealWholeVein;
			config.OreShieldMaxVeinRevealBlocks = oldMaxVeinRevealBlocks;
			config.OreShieldDiscoveryRangeBlocks = oldDiscoveryRange;
			config.OreShieldVisibilityScanIntervalMs = oldVisibilityInterval;
			config.OreShieldMaxLosChecksPerPass = oldMaxLosChecks;
			config.OreShieldViewConeDegrees = oldViewCone;
			config.OreShieldIdleRescanMs = oldIdleRescan;
			config.OreShieldLeakAuditEnabled = oldLeakAudit;
			config.OreShieldChunkCacheEnabled = oldCacheEnabled;
			config.OreShieldChunkCacheMiB = oldCacheMiB;
			config.OreShieldMaxCachedChunks = oldMaxCached;
			config.OreShieldAdditionalOrePatterns = oldAdditional;
			config.OreShieldExcludedBlockPatterns = oldExcluded;
			config.Clamp();
		}
	}

	public (bool Success, string Message) SaveClientScanSettings(AntiCheatClientScanSettingsSaveRequest request)
	{
		if (request == null)
		{
			return (Success: false, Message: "Client scan settings are required.");
		}
		AntiCheatClientScanSettingsSaveRequest request2 = new AntiCheatClientScanSettingsSaveRequest
		{
			RequireClient = config.RequireClient,
			ClientModDbVerificationEnabled = config.ClientModDbVerificationEnabled,
			StrictClientModDbEnforcementEnabled = config.StrictClientModDbEnforcementEnabled,
			RequireHardenedClientCapabilities = config.RequireHardenedClientCapabilities,
			RequireCurrentClientRelease = config.RequireCurrentClientRelease,
			StartupIntegrityGateEnabled = config.StartupIntegrityGateEnabled,
			StartupIntegrityGateBlockWorldInteractions = config.StartupIntegrityGateBlockWorldInteractions,
			StartupIntegrityGateRequireClean = config.StartupIntegrityGateRequireClean,
			StartupIntegrityGateDisconnectOnFailure = config.StartupIntegrityGateDisconnectOnFailure,
			StartupIntegrityGateNoticeCooldownSeconds = config.StartupIntegrityGateNoticeCooldownSeconds,
			ChallengeTimeoutSeconds = config.ChallengeTimeoutSeconds,
			StartupChallengeTimeoutSeconds = config.StartupChallengeTimeoutSeconds,
			StartupChallengeHeartbeatExtensionSeconds = config.StartupChallengeHeartbeatExtensionSeconds,
			StartupChallengeHardLimitSeconds = config.StartupChallengeHardLimitSeconds,
			ClientHelloGraceSeconds = config.ClientHelloGraceSeconds,
			ClientHelloDisconnectDelaySeconds = config.ClientHelloDisconnectDelaySeconds,
			HeartbeatTimeoutSeconds = config.HeartbeatTimeoutSeconds,
			RescanMinutes = config.RescanMinutes,
			RuntimeRescanOnChange = config.RuntimeRescanOnChange,
			RuntimeChangeRescanCooldownSeconds = config.RuntimeChangeRescanCooldownSeconds
		};
		bool clientModDbVerificationEnabled = config.ClientModDbVerificationEnabled;
		bool strictClientModDbEnforcementEnabled = config.StrictClientModDbEnforcementEnabled;
		ApplyClientScanSettings(request);
		if (!TrySaveConfig(out string error))
		{
			ApplyClientScanSettings(request2);
			return (Success: false, Message: error);
		}
		ReevaluateCurrentSessions();
		if (clientModDbVerificationEnabled != config.ClientModDbVerificationEnabled)
		{
			QueueServerInstalledModDbBaseline();
		}
		if ((!clientModDbVerificationEnabled && config.ClientModDbVerificationEnabled) || (!strictClientModDbEnforcementEnabled && config.StrictClientModDbEnforcementEnabled))
		{
			ResolveAllClientModDbFingerprints();
		}
		string requiredClientVersion = GetRequiredClientVersion();
		return (Success: true, Message: $"Client scan settings saved. Startup gate {(config.StartupIntegrityGateEnabled ? "ON" : "OFF")}, interaction quarantine {(config.StartupIntegrityGateBlockWorldInteractions ? "ON" : "OFF")}, startup scan window {config.StartupChallengeTimeoutSeconds}s, heartbeat hard limit {config.StartupChallengeHardLimitSeconds}s, ModDB verification {(config.ClientModDbVerificationEnabled ? "ON" : "OFF")}, strict mod DB enforcement {(config.StrictClientModDbEnforcementEnabled ? "ON" : "OFF")}, hardened transport requirement {(config.RequireHardenedClientCapabilities ? "ON" : "OFF")}, server-version match v{requiredClientVersion} {(config.RequireCurrentClientRelease ? "ON" : "OFF")}.");
	}

	private void ApplyClientScanSettings(AntiCheatClientScanSettingsSaveRequest request)
	{
		config.RequireClient = request.RequireClient;
		config.ClientModDbVerificationEnabled = request.ClientModDbVerificationEnabled;
		config.StrictClientModDbEnforcementEnabled = request.StrictClientModDbEnforcementEnabled;
		config.RequireHardenedClientCapabilities = request.RequireHardenedClientCapabilities;
		config.RequireCurrentClientRelease = request.RequireCurrentClientRelease;
		config.StartupIntegrityGateEnabled = request.StartupIntegrityGateEnabled;
		config.StartupIntegrityGateBlockWorldInteractions = request.StartupIntegrityGateBlockWorldInteractions;
		config.StartupIntegrityGateRequireClean = request.StartupIntegrityGateRequireClean;
		config.StartupIntegrityGateDisconnectOnFailure = request.StartupIntegrityGateDisconnectOnFailure;
		config.StartupIntegrityGateNoticeCooldownSeconds = request.StartupIntegrityGateNoticeCooldownSeconds;
		config.ChallengeTimeoutSeconds = request.ChallengeTimeoutSeconds;
		config.StartupChallengeTimeoutSeconds = request.StartupChallengeTimeoutSeconds;
		config.StartupChallengeHeartbeatExtensionSeconds = request.StartupChallengeHeartbeatExtensionSeconds;
		config.StartupChallengeHardLimitSeconds = request.StartupChallengeHardLimitSeconds;
		config.ClientHelloGraceSeconds = request.ClientHelloGraceSeconds;
		config.ClientHelloDisconnectDelaySeconds = request.ClientHelloDisconnectDelaySeconds;
		config.HeartbeatTimeoutSeconds = request.HeartbeatTimeoutSeconds;
		config.RescanMinutes = request.RescanMinutes;
		config.RuntimeRescanOnChange = request.RuntimeRescanOnChange;
		config.RuntimeChangeRescanCooldownSeconds = request.RuntimeChangeRescanCooldownSeconds;
		config.Clamp();
	}

	public (bool Success, string Message) SaveGuiSettings(AntiCheatGuiSettingsSaveRequest request)
	{
		if (request == null)
		{
			return (Success: false, Message: "Anti-Cheat settings are required.");
		}
		AntiCheatGuiSettingsSaveRequest request2 = new AntiCheatGuiSettingsSaveRequest
		{
			RequireClient = config.RequireClient,
			RescanMinutes = config.RescanMinutes,
			RuntimeRescanOnChange = config.RuntimeRescanOnChange,
			RuntimeChangeRescanCooldownSeconds = config.RuntimeChangeRescanCooldownSeconds,
			SimilarityDetectionEnabled = config.SimilarityDetectionEnabled,
			SimilarityReviewThresholdPercent = config.SimilarityReviewThresholdPercent,
			SimilarityHighThresholdPercent = config.SimilarityHighThresholdPercent,
			RuntimeSurfaceInspectionEnabled = config.RuntimeSurfaceInspectionEnabled,
			SuspiciousHotkeyInspectionEnabled = config.SuspiciousHotkeyInspectionEnabled,
			SuspiciousClientCommandInspectionEnabled = config.SuspiciousClientCommandInspectionEnabled,
			CanonicalServerModIntegrityEnabled = config.CanonicalServerModIntegrityEnabled,
			ProtectedAssetInspectionEnabled = config.ProtectedAssetInspectionEnabled
		};
		ApplyGuiSettings(request);
		if (!TrySaveConfig(out string error))
		{
			ApplyGuiSettings(request2);
			return (Success: false, Message: error);
		}
		ReevaluateCurrentSessions();
		return (Success: true, Message: "Client scanner settings saved. Packet Protection settings were left unchanged.");
	}

	private void ApplyGuiSettings(AntiCheatGuiSettingsSaveRequest request)
	{
		config.RequireClient = request.RequireClient;
		config.RescanMinutes = request.RescanMinutes;
		config.RuntimeRescanOnChange = request.RuntimeRescanOnChange;
		config.RuntimeChangeRescanCooldownSeconds = request.RuntimeChangeRescanCooldownSeconds;
		bool flag = config.CanonicalServerModIntegrityEnabled != request.CanonicalServerModIntegrityEnabled;
		bool flag2 = config.ProtectedAssetInspectionEnabled != request.ProtectedAssetInspectionEnabled || config.RuntimeSurfaceInspectionEnabled != request.RuntimeSurfaceInspectionEnabled;
		config.SimilarityDetectionEnabled = request.SimilarityDetectionEnabled;
		config.SimilarityReviewThresholdPercent = request.SimilarityReviewThresholdPercent;
		config.SimilarityHighThresholdPercent = request.SimilarityHighThresholdPercent;
		config.RuntimeSurfaceInspectionEnabled = request.RuntimeSurfaceInspectionEnabled;
		config.SuspiciousHotkeyInspectionEnabled = request.SuspiciousHotkeyInspectionEnabled;
		config.SuspiciousClientCommandInspectionEnabled = request.SuspiciousClientCommandInspectionEnabled;
		config.CanonicalServerModIntegrityEnabled = request.CanonicalServerModIntegrityEnabled;
		config.ProtectedAssetInspectionEnabled = request.ProtectedAssetInspectionEnabled;
		config.Clamp();
		if (flag)
		{
			StartCanonicalModFingerprintBuild();
		}
		if (flag2)
		{
			InvalidateProtectedAssetManifest();
		}
		CaptureServerCoreAssemblyBaseline();
	}

	public (bool Success, string Message) SaveGuiPacketProtectionSettings(AntiCheatPacketProtectionGuiSaveRequest request)
	{
		if (request == null)
		{
			return (Success: false, Message: "Packet protection settings are required.");
		}
		AntiCheatPacketGuardSaveRequest request2 = new AntiCheatPacketGuardSaveRequest
		{
			Enabled = request.PacketGuardEnabled,
			PlayerWarningEnabled = request.PlayerWarningEnabled,
			PlayerWarningCooldownSeconds = request.PlayerWarningCooldownSeconds,
			AccelerationSampleMinMs = request.AccelerationSampleMinMs,
			AccelerationSmoothingFactor = request.AccelerationSmoothingFactor,
			NoclipEnabled = request.NoclipEnabled,
			NoclipRequiredCollisionSamples = request.NoclipRequiredCollisionSamples,
			NoclipMinPenetrationDepth = request.NoclipMinPenetrationDepth,
			NoclipEndpointMinNetMovement = request.NoclipEndpointMinNetMovement,
			MountAccelerationMinSpeedRatio = request.MountAccelerationMinSpeedRatio,
			DiagnosticContextEnabled = request.DiagnosticContextEnabled,
			DiagnosticSuppressionEnabled = request.DiagnosticSuppressionEnabled,
			DiagnosticSpeedHistorySamples = request.DiagnosticSpeedHistorySamples,
			MovementEnabled = request.MovementEnabled,
			MovementSampleMs = request.MovementSampleMs,
			HardMoveDistance = request.HardMoveDistance,
			MovementConfirmSamples = request.MovementConfirmSamples,
			MovementConfirmWindowMs = request.MovementConfirmWindowMs,
			MovementExtremeMultiplier = request.MovementExtremeMultiplier,
			CorrectionGraceMs = request.CorrectionGraceMs,
			RejectUnauthorizedMovement = request.RejectUnauthorizedMovement,
			ExemptAdmins = request.ExemptAdmins,
			ExemptMountedPlayers = request.ExemptMountedPlayers,
			MountGraceMs = request.MountGraceMs,
			EngineTeleportGraceMs = request.EngineTeleportGraceMs,
			TPRunesCompatibility = request.TPRunesCompatibility,
			TPRunesGraceMs = request.TPRunesGraceMs,
			Th3EssentialsSpawnCompatibility = request.Th3EssentialsSpawnCompatibility,
			Th3EssentialsSpawnGraceMs = request.Th3EssentialsSpawnGraceMs,
			ReachEnabled = request.ReachEnabled,
			ReachTolerance = request.ReachTolerance,
			ReachLatencyCompensation = request.ReachLatencyCompensation,
			ReachMaxLatencyCompensation = request.ReachMaxLatencyCompensation,
			ReachPositionHistoryEnabled = request.ReachPositionHistoryEnabled,
			ReachPositionHistoryMs = request.ReachPositionHistoryMs,
			ReachMaxRewindMs = request.ReachMaxRewindMs,
			ReachUseExtraTolerance = request.ReachUseExtraTolerance,
			ReachBreakExtraTolerance = request.ReachBreakExtraTolerance,
			ReachNearThresholdStrikes = request.ReachNearThresholdStrikes,
			ReachStrikeWindowMs = request.ReachStrikeWindowMs,
			ReachStrikeDecayMs = request.ReachStrikeDecayMs,
			ReachHardExcess = request.ReachHardExcess,
			RejectOutOfReachActions = request.RejectOutOfReachActions,
			MiningEnabled = request.MiningEnabled,
			VeinWindowMs = request.VeinWindowMs,
			ConnectedBreakLimit = request.ConnectedBreakLimit,
			MiningDuplicateTargetMs = request.MiningDuplicateTargetMs,
			RejectConnectedBreakBursts = request.RejectConnectedBreakBursts,
			MiningCadenceValidationEnabled = request.MiningCadenceValidationEnabled,
			MiningCadenceMinExpectedMs = request.MiningCadenceMinExpectedMs,
			MiningCadenceSuspiciousRatio = request.MiningCadenceSuspiciousRatio,
			MiningCadenceConfirmations = request.MiningCadenceConfirmations,
			MiningCadenceConfirmationWindowMs = request.MiningCadenceConfirmationWindowMs,
			RejectImpossibleMiningCadence = request.RejectImpossibleMiningCadence,
			TpsAwarenessEnabled = request.TpsAwarenessEnabled,
			TpsDegradedPercent = request.TpsDegradedPercent,
			TpsCriticalPercent = request.TpsCriticalPercent,
			TpsMaxTimingMultiplier = request.TpsMaxTimingMultiplier,
			TpsSuppressTimingRejectsWhenCritical = request.TpsSuppressTimingRejectsWhenCritical,
			PerFamilyRiskEnabled = request.PerFamilyRiskEnabled,
			MovementRiskDecaySeconds = request.MovementRiskDecaySeconds,
			MovementRiskReviewThreshold = request.MovementRiskReviewThreshold,
			MovementRiskSuspiciousThreshold = request.MovementRiskSuspiciousThreshold,
			ReachRiskDecaySeconds = request.ReachRiskDecaySeconds,
			ReachRiskReviewThreshold = request.ReachRiskReviewThreshold,
			ReachRiskSuspiciousThreshold = request.ReachRiskSuspiciousThreshold,
			MiningRiskDecaySeconds = request.MiningRiskDecaySeconds,
			MiningRiskReviewThreshold = request.MiningRiskReviewThreshold,
			MiningRiskSuspiciousThreshold = request.MiningRiskSuspiciousThreshold,
			PacketAbuseRiskDecaySeconds = request.PacketAbuseRiskDecaySeconds,
			PacketAbuseRiskReviewThreshold = request.PacketAbuseRiskReviewThreshold,
			PacketAbuseRiskSuspiciousThreshold = request.PacketAbuseRiskSuspiciousThreshold,
			ClientIntegrityRiskDecaySeconds = request.ClientIntegrityRiskDecaySeconds,
			ClientIntegrityRiskReviewThreshold = request.ClientIntegrityRiskReviewThreshold,
			ClientIntegrityRiskSuspiciousThreshold = request.ClientIntegrityRiskSuspiciousThreshold,
			IncidentCorrelationEnabled = request.IncidentCorrelationEnabled,
			IncidentCorrelationWindowSeconds = request.IncidentCorrelationWindowSeconds,
			IncidentCorrelationRequiredCategories = request.IncidentCorrelationRequiredCategories,
			IncidentCorrelationMaxRiskBonus = request.IncidentCorrelationMaxRiskBonus,
			HistoryEnabled = request.HistoryEnabled,
			HistoryRetentionDays = request.HistoryRetentionDays,
			HistoryMaxRecords = request.HistoryMaxRecords
		};
		return SavePacketGuardSettings(request2);
	}

	public (bool Success, string Message) SavePacketGuardSettings(AntiCheatPacketGuardSaveRequest request)
	{
		if (request == null)
		{
			return (Success: false, Message: "PacketGuard settings are required.");
		}
		if (request.MaxHorizontalSpeed.HasValue && !double.IsFinite(request.MaxHorizontalSpeed.Value))
		{
			return (Success: false, Message: "MaxHorizontalSpeed must be a finite number.");
		}
		if (request.GroundMaxHorizontalSpeed.HasValue && !double.IsFinite(request.GroundMaxHorizontalSpeed.Value))
		{
			return (Success: false, Message: "GroundMaxHorizontalSpeed must be a finite number.");
		}
		if (request.SwimmingMaxHorizontalSpeed.HasValue && !double.IsFinite(request.SwimmingMaxHorizontalSpeed.Value))
		{
			return (Success: false, Message: "SwimmingMaxHorizontalSpeed must be a finite number.");
		}
		if (request.AirborneMaxHorizontalSpeed.HasValue && !double.IsFinite(request.AirborneMaxHorizontalSpeed.Value))
		{
			return (Success: false, Message: "AirborneMaxHorizontalSpeed must be a finite number.");
		}
		if (request.DraconisMaxHorizontalSpeed.HasValue && !double.IsFinite(request.DraconisMaxHorizontalSpeed.Value))
		{
			return (Success: false, Message: "DraconisMaxHorizontalSpeed must be a finite number.");
		}
		if (request.DraconisMaxVerticalSpeed.HasValue && !double.IsFinite(request.DraconisMaxVerticalSpeed.Value))
		{
			return (Success: false, Message: "DraconisMaxVerticalSpeed must be a finite number.");
		}
		if (request.DraconisMaxAcceleration.HasValue && !double.IsFinite(request.DraconisMaxAcceleration.Value))
		{
			return (Success: false, Message: "DraconisMaxAcceleration must be a finite number.");
		}
		if (request.DraconisMaxRiderDistance.HasValue && !double.IsFinite(request.DraconisMaxRiderDistance.Value))
		{
			return (Success: false, Message: "DraconisMaxRiderDistance must be a finite number.");
		}
		if (request.DraconisMaxRiderRelativeSpeed.HasValue && !double.IsFinite(request.DraconisMaxRiderRelativeSpeed.Value))
		{
			return (Success: false, Message: "DraconisMaxRiderRelativeSpeed must be a finite number.");
		}
		if (request.ShipwrightMaxHorizontalSpeed.HasValue && !double.IsFinite(request.ShipwrightMaxHorizontalSpeed.Value))
		{
			return (Success: false, Message: "ShipwrightMaxHorizontalSpeed must be a finite number.");
		}
		if (request.ShipwrightMaxVerticalSpeed.HasValue && !double.IsFinite(request.ShipwrightMaxVerticalSpeed.Value))
		{
			return (Success: false, Message: "ShipwrightMaxVerticalSpeed must be a finite number.");
		}
		if (request.ShipwrightMaxAcceleration.HasValue && !double.IsFinite(request.ShipwrightMaxAcceleration.Value))
		{
			return (Success: false, Message: "ShipwrightMaxAcceleration must be a finite number.");
		}
		if (request.ShipwrightMaxRiderDistance.HasValue && !double.IsFinite(request.ShipwrightMaxRiderDistance.Value))
		{
			return (Success: false, Message: "ShipwrightMaxRiderDistance must be a finite number.");
		}
		if (request.ShipwrightMaxRiderRelativeSpeed.HasValue && !double.IsFinite(request.ShipwrightMaxRiderRelativeSpeed.Value))
		{
			return (Success: false, Message: "ShipwrightMaxRiderRelativeSpeed must be a finite number.");
		}
		if (request.UnknownMountMaxHorizontalSpeed.HasValue && !double.IsFinite(request.UnknownMountMaxHorizontalSpeed.Value))
		{
			return (Success: false, Message: "UnknownMountMaxHorizontalSpeed must be a finite number.");
		}
		if (request.UnknownMountMaxVerticalSpeed.HasValue && !double.IsFinite(request.UnknownMountMaxVerticalSpeed.Value))
		{
			return (Success: false, Message: "UnknownMountMaxVerticalSpeed must be a finite number.");
		}
		if (request.UnknownMountMaxAcceleration.HasValue && !double.IsFinite(request.UnknownMountMaxAcceleration.Value))
		{
			return (Success: false, Message: "UnknownMountMaxAcceleration must be a finite number.");
		}
		if (request.UnknownMountMaxRiderDistance.HasValue && !double.IsFinite(request.UnknownMountMaxRiderDistance.Value))
		{
			return (Success: false, Message: "UnknownMountMaxRiderDistance must be a finite number.");
		}
		if (request.UnknownMountMaxRiderRelativeSpeed.HasValue && !double.IsFinite(request.UnknownMountMaxRiderRelativeSpeed.Value))
		{
			return (Success: false, Message: "UnknownMountMaxRiderRelativeSpeed must be a finite number.");
		}
		AntiCheatPacketGuardSaveRequest request2 = new AntiCheatPacketGuardSaveRequest
		{
			ConfirmedSpeedRepeatRisk = config.PacketGuardConfirmedSpeedRepeatRisk,
			KickRepeatedConfirmedSpeed = config.PacketGuardKickRepeatedConfirmedSpeed,
			MovementModeLimitsEnabled = config.PacketGuardMovementModeLimitsEnabled,
			MaxHorizontalSpeed = config.PacketGuardMaxHorizontalSpeed,
			GroundMaxHorizontalSpeed = config.PacketGuardGroundMaxHorizontalSpeed,
			SwimmingMaxHorizontalSpeed = config.PacketGuardSwimmingMaxHorizontalSpeed,
			AirborneMaxHorizontalSpeed = config.PacketGuardAirborneMaxHorizontalSpeed,
			MountProfilesEnabled = config.PacketGuardMountProfilesEnabled,
			MountProfileEnforcementEnabled = config.PacketGuardMountProfileEnforcementEnabled,
			UnknownMountEnforcementEnabled = config.PacketGuardUnknownMountEnforcementEnabled,
			MountProfileConfirmSamples = config.PacketGuardMountProfileConfirmSamples,
			MountProfileConfirmWindowMs = config.PacketGuardMountProfileConfirmWindowMs,
			MountRiderCorrelationEnabled = config.PacketGuardMountRiderCorrelationEnabled,
			DraconisMaxHorizontalSpeed = config.PacketGuardDraconisMaxHorizontalSpeed,
			DraconisMaxVerticalSpeed = config.PacketGuardDraconisMaxVerticalSpeed,
			DraconisMaxAcceleration = config.PacketGuardDraconisMaxAcceleration,
			DraconisMaxRiderDistance = config.PacketGuardDraconisMaxRiderDistance,
			DraconisMaxRiderRelativeSpeed = config.PacketGuardDraconisMaxRiderRelativeSpeed,
			ShipwrightMaxHorizontalSpeed = config.PacketGuardShipwrightMaxHorizontalSpeed,
			ShipwrightMaxVerticalSpeed = config.PacketGuardShipwrightMaxVerticalSpeed,
			ShipwrightMaxAcceleration = config.PacketGuardShipwrightMaxAcceleration,
			ShipwrightMaxRiderDistance = config.PacketGuardShipwrightMaxRiderDistance,
			ShipwrightMaxRiderRelativeSpeed = config.PacketGuardShipwrightMaxRiderRelativeSpeed,
			UnknownMountMaxHorizontalSpeed = config.PacketGuardUnknownMountMaxHorizontalSpeed,
			UnknownMountMaxVerticalSpeed = config.PacketGuardUnknownMountMaxVerticalSpeed,
			UnknownMountMaxAcceleration = config.PacketGuardUnknownMountMaxAcceleration,
			UnknownMountMaxRiderDistance = config.PacketGuardUnknownMountMaxRiderDistance,
			UnknownMountMaxRiderRelativeSpeed = config.PacketGuardUnknownMountMaxRiderRelativeSpeed,
			Enabled = config.PacketGuardEnabled,
			IngressEnabled = config.PacketGuardIngressEnabled,
			PlayerWarningEnabled = config.PacketGuardPlayerWarningEnabled,
			PlayerWarningCooldownSeconds = config.PacketGuardPlayerWarningCooldownSeconds,
			AccelerationSampleMinMs = config.PacketGuardAccelerationSampleMinMs,
			AccelerationSmoothingFactor = config.PacketGuardAccelerationSmoothingFactor,
			NoclipEnabled = config.PacketGuardNoclipEnabled,
			NoclipRequiredCollisionSamples = config.PacketGuardNoclipRequiredCollisionSamples,
			NoclipMinPenetrationDepth = config.PacketGuardNoclipMinPenetrationDepth,
			NoclipEndpointMinNetMovement = config.PacketGuardNoclipEndpointMinNetMovement,
			MountAccelerationMinSpeedRatio = config.PacketGuardMountAccelerationMinSpeedRatio,
			DiagnosticContextEnabled = config.PacketGuardDiagnosticContextEnabled,
			DiagnosticSuppressionEnabled = config.PacketGuardDiagnosticSuppressionEnabled,
			DiagnosticSpeedHistorySamples = config.PacketGuardDiagnosticSpeedHistorySamples,
			MovementEnabled = config.PacketGuardMovementEnabled,
			MovementSampleMs = config.PacketGuardMovementSampleMs,
			HardMoveDistance = config.PacketGuardHardMoveDistance,
			MovementConfirmSamples = config.PacketGuardMovementConfirmSamples,
			MovementConfirmWindowMs = config.PacketGuardMovementConfirmWindowMs,
			MovementExtremeMultiplier = config.PacketGuardMovementExtremeMultiplier,
			CorrectionGraceMs = config.PacketGuardCorrectionGraceMs,
			RejectUnauthorizedMovement = config.PacketGuardRejectUnauthorizedMovement,
			ExemptAdmins = config.PacketGuardExemptAdmins,
			ExemptMountedPlayers = config.PacketGuardExemptMountedPlayers,
			MountGraceMs = config.PacketGuardMountGraceMs,
			EngineTeleportGraceMs = config.PacketGuardEngineTeleportGraceMs,
			TPRunesCompatibility = config.PacketGuardTPRunesCompatibility,
			TPRunesGraceMs = config.PacketGuardTPRunesGraceMs,
			Th3EssentialsSpawnCompatibility = config.PacketGuardTh3EssentialsSpawnCompatibility,
			Th3EssentialsSpawnGraceMs = config.PacketGuardTh3EssentialsSpawnGraceMs,
			ReachEnabled = config.PacketGuardReachEnabled,
			ReachTolerance = config.PacketGuardReachTolerance,
			ReachLatencyCompensation = config.PacketGuardReachLatencyCompensation,
			ReachMaxLatencyCompensation = config.PacketGuardReachMaxLatencyCompensation,
			ReachPositionHistoryEnabled = config.PacketGuardReachPositionHistoryEnabled,
			ReachPositionHistoryMs = config.PacketGuardReachPositionHistoryMs,
			ReachMaxRewindMs = config.PacketGuardReachMaxRewindMs,
			ReachUseExtraTolerance = config.PacketGuardReachUseExtraTolerance,
			ReachBreakExtraTolerance = config.PacketGuardReachBreakExtraTolerance,
			ReachNearThresholdStrikes = config.PacketGuardReachNearThresholdStrikes,
			ReachStrikeWindowMs = config.PacketGuardReachStrikeWindowMs,
			ReachStrikeDecayMs = config.PacketGuardReachStrikeDecayMs,
			ReachHardExcess = config.PacketGuardReachHardExcess,
			RejectOutOfReachActions = config.PacketGuardRejectOutOfReachActions,
			MiningEnabled = config.PacketGuardMiningEnabled,
			VeinWindowMs = config.PacketGuardVeinWindowMs,
			ConnectedBreakLimit = config.PacketGuardConnectedBreakLimit,
			MiningDuplicateTargetMs = config.PacketGuardMiningDuplicateTargetMs,
			RejectConnectedBreakBursts = config.PacketGuardRejectConnectedBreakBursts,
			MiningCadenceValidationEnabled = config.PacketGuardMiningCadenceValidationEnabled,
			MiningCadenceMinExpectedMs = config.PacketGuardMiningCadenceMinExpectedMs,
			MiningCadenceSuspiciousRatio = config.PacketGuardMiningCadenceSuspiciousRatio,
			MiningCadenceConfirmations = config.PacketGuardMiningCadenceConfirmations,
			MiningCadenceConfirmationWindowMs = config.PacketGuardMiningCadenceConfirmationWindowMs,
			RejectImpossibleMiningCadence = config.PacketGuardRejectImpossibleMiningCadence,
			TpsAwarenessEnabled = config.PacketGuardTpsAwarenessEnabled,
			TpsDegradedPercent = config.PacketGuardTpsDegradedPercent,
			TpsCriticalPercent = config.PacketGuardTpsCriticalPercent,
			TpsMaxTimingMultiplier = config.PacketGuardTpsMaxTimingMultiplier,
			TpsSuppressTimingRejectsWhenCritical = config.PacketGuardTpsSuppressTimingRejectsWhenCritical,
			PerFamilyRiskEnabled = config.PerFamilyRiskEnabled,
			MovementRiskDecaySeconds = config.MovementRiskDecaySeconds,
			MovementRiskReviewThreshold = config.MovementRiskReviewThreshold,
			MovementRiskSuspiciousThreshold = config.MovementRiskSuspiciousThreshold,
			ReachRiskDecaySeconds = config.ReachRiskDecaySeconds,
			ReachRiskReviewThreshold = config.ReachRiskReviewThreshold,
			ReachRiskSuspiciousThreshold = config.ReachRiskSuspiciousThreshold,
			MiningRiskDecaySeconds = config.MiningRiskDecaySeconds,
			MiningRiskReviewThreshold = config.MiningRiskReviewThreshold,
			MiningRiskSuspiciousThreshold = config.MiningRiskSuspiciousThreshold,
			PacketAbuseRiskDecaySeconds = config.PacketAbuseRiskDecaySeconds,
			PacketAbuseRiskReviewThreshold = config.PacketAbuseRiskReviewThreshold,
			PacketAbuseRiskSuspiciousThreshold = config.PacketAbuseRiskSuspiciousThreshold,
			ClientIntegrityRiskDecaySeconds = config.ClientIntegrityRiskDecaySeconds,
			ClientIntegrityRiskReviewThreshold = config.ClientIntegrityRiskReviewThreshold,
			ClientIntegrityRiskSuspiciousThreshold = config.ClientIntegrityRiskSuspiciousThreshold,
			IncidentCorrelationEnabled = config.IncidentCorrelationEnabled,
			IncidentCorrelationWindowSeconds = config.IncidentCorrelationWindowSeconds,
			IncidentCorrelationRequiredCategories = config.IncidentCorrelationRequiredCategories,
			IncidentCorrelationMaxRiskBonus = config.IncidentCorrelationMaxRiskBonus,
			HistoryEnabled = config.HistoryEnabled,
			HistoryRetentionDays = config.HistoryRetentionDays,
			HistoryMaxRecords = config.HistoryMaxRecords
		};
		ApplyPacketGuardRequest(request);
		if (!TrySaveConfig(out string error))
		{
			ApplyPacketGuardRequest(request2);
			return (Success: false, Message: error);
		}
		packetGuard?.Dispose();
		packetGuard = null;
		if (config.Enabled && config.PacketGuardEnabled)
		{
			packetGuard = new PacketGuardService(api, config, ReportServerBehaviorFinding);
			packetGuard.Start();
		}
		historyStore?.ApplyRetentionNow();
		return (Success: true, Message: $"Packet protection saved. Ingress {(config.PacketGuardIngressEnabled ? "on" : "off")}, player chat warnings {(config.PacketGuardPlayerWarningEnabled ? "on" : "off")}, movement {(config.PacketGuardMovementEnabled ? "on" : "off")}, block collision/noclip {(config.PacketGuardNoclipEnabled ? "on" : "off")}, reach {(config.PacketGuardReachEnabled ? "on" : "off")}, mining {(config.PacketGuardMiningEnabled ? "on" : "off")}; persistent history {(config.HistoryEnabled ? "on" : "off")}.");
	}

	private void ApplyPacketGuardRequest(AntiCheatPacketGuardSaveRequest request)
	{
		if (request.IngressEnabled.HasValue)
		{
			config.PacketGuardIngressEnabled = request.IngressEnabled.Value;
		}
		if (request.ConfirmedSpeedRepeatRisk.HasValue)
		{
			config.PacketGuardConfirmedSpeedRepeatRisk = request.ConfirmedSpeedRepeatRisk.Value;
		}
		if (request.KickRepeatedConfirmedSpeed.HasValue)
		{
			config.PacketGuardKickRepeatedConfirmedSpeed = request.KickRepeatedConfirmedSpeed.Value;
		}
		if (request.MovementModeLimitsEnabled.HasValue)
		{
			config.PacketGuardMovementModeLimitsEnabled = request.MovementModeLimitsEnabled.Value;
		}
		if (request.MaxHorizontalSpeed.HasValue)
		{
			config.PacketGuardMaxHorizontalSpeed = request.MaxHorizontalSpeed.Value;
		}
		if (request.GroundMaxHorizontalSpeed.HasValue)
		{
			config.PacketGuardGroundMaxHorizontalSpeed = request.GroundMaxHorizontalSpeed.Value;
		}
		if (request.SwimmingMaxHorizontalSpeed.HasValue)
		{
			config.PacketGuardSwimmingMaxHorizontalSpeed = request.SwimmingMaxHorizontalSpeed.Value;
		}
		if (request.AirborneMaxHorizontalSpeed.HasValue)
		{
			config.PacketGuardAirborneMaxHorizontalSpeed = request.AirborneMaxHorizontalSpeed.Value;
		}
		if (request.MountProfilesEnabled.HasValue)
		{
			config.PacketGuardMountProfilesEnabled = request.MountProfilesEnabled.Value;
		}
		if (request.MountProfileEnforcementEnabled.HasValue)
		{
			config.PacketGuardMountProfileEnforcementEnabled = request.MountProfileEnforcementEnabled.Value;
		}
		if (request.UnknownMountEnforcementEnabled.HasValue)
		{
			config.PacketGuardUnknownMountEnforcementEnabled = request.UnknownMountEnforcementEnabled.Value;
		}
		if (request.MountProfileConfirmSamples.HasValue)
		{
			config.PacketGuardMountProfileConfirmSamples = request.MountProfileConfirmSamples.Value;
		}
		if (request.MountProfileConfirmWindowMs.HasValue)
		{
			config.PacketGuardMountProfileConfirmWindowMs = request.MountProfileConfirmWindowMs.Value;
		}
		if (request.MountRiderCorrelationEnabled.HasValue)
		{
			config.PacketGuardMountRiderCorrelationEnabled = request.MountRiderCorrelationEnabled.Value;
		}
		if (request.DraconisMaxHorizontalSpeed.HasValue)
		{
			config.PacketGuardDraconisMaxHorizontalSpeed = request.DraconisMaxHorizontalSpeed.Value;
		}
		if (request.DraconisMaxVerticalSpeed.HasValue)
		{
			config.PacketGuardDraconisMaxVerticalSpeed = request.DraconisMaxVerticalSpeed.Value;
		}
		if (request.DraconisMaxAcceleration.HasValue)
		{
			config.PacketGuardDraconisMaxAcceleration = request.DraconisMaxAcceleration.Value;
		}
		if (request.DraconisMaxRiderDistance.HasValue)
		{
			config.PacketGuardDraconisMaxRiderDistance = request.DraconisMaxRiderDistance.Value;
		}
		if (request.DraconisMaxRiderRelativeSpeed.HasValue)
		{
			config.PacketGuardDraconisMaxRiderRelativeSpeed = request.DraconisMaxRiderRelativeSpeed.Value;
		}
		if (request.ShipwrightMaxHorizontalSpeed.HasValue)
		{
			config.PacketGuardShipwrightMaxHorizontalSpeed = request.ShipwrightMaxHorizontalSpeed.Value;
		}
		if (request.ShipwrightMaxVerticalSpeed.HasValue)
		{
			config.PacketGuardShipwrightMaxVerticalSpeed = request.ShipwrightMaxVerticalSpeed.Value;
		}
		if (request.ShipwrightMaxAcceleration.HasValue)
		{
			config.PacketGuardShipwrightMaxAcceleration = request.ShipwrightMaxAcceleration.Value;
		}
		if (request.ShipwrightMaxRiderDistance.HasValue)
		{
			config.PacketGuardShipwrightMaxRiderDistance = request.ShipwrightMaxRiderDistance.Value;
		}
		if (request.ShipwrightMaxRiderRelativeSpeed.HasValue)
		{
			config.PacketGuardShipwrightMaxRiderRelativeSpeed = request.ShipwrightMaxRiderRelativeSpeed.Value;
		}
		if (request.UnknownMountMaxHorizontalSpeed.HasValue)
		{
			config.PacketGuardUnknownMountMaxHorizontalSpeed = request.UnknownMountMaxHorizontalSpeed.Value;
		}
		if (request.UnknownMountMaxVerticalSpeed.HasValue)
		{
			config.PacketGuardUnknownMountMaxVerticalSpeed = request.UnknownMountMaxVerticalSpeed.Value;
		}
		if (request.UnknownMountMaxAcceleration.HasValue)
		{
			config.PacketGuardUnknownMountMaxAcceleration = request.UnknownMountMaxAcceleration.Value;
		}
		if (request.UnknownMountMaxRiderDistance.HasValue)
		{
			config.PacketGuardUnknownMountMaxRiderDistance = request.UnknownMountMaxRiderDistance.Value;
		}
		if (request.UnknownMountMaxRiderRelativeSpeed.HasValue)
		{
			config.PacketGuardUnknownMountMaxRiderRelativeSpeed = request.UnknownMountMaxRiderRelativeSpeed.Value;
		}
		config.PacketGuardEnabled = request.Enabled;
		config.PacketGuardPlayerWarningEnabled = request.PlayerWarningEnabled;
		config.PacketGuardPlayerWarningCooldownSeconds = request.PlayerWarningCooldownSeconds;
		config.PacketGuardAccelerationSampleMinMs = request.AccelerationSampleMinMs;
		config.PacketGuardAccelerationSmoothingFactor = request.AccelerationSmoothingFactor;
		config.PacketGuardNoclipEnabled = request.NoclipEnabled;
		config.PacketGuardNoclipRequiredCollisionSamples = request.NoclipRequiredCollisionSamples;
		config.PacketGuardNoclipMinPenetrationDepth = request.NoclipMinPenetrationDepth;
		config.PacketGuardNoclipEndpointMinNetMovement = request.NoclipEndpointMinNetMovement;
		config.PacketGuardMountAccelerationMinSpeedRatio = request.MountAccelerationMinSpeedRatio;
		config.PacketGuardDiagnosticContextEnabled = request.DiagnosticContextEnabled;
		config.PacketGuardDiagnosticSuppressionEnabled = request.DiagnosticSuppressionEnabled;
		config.PacketGuardDiagnosticSpeedHistorySamples = request.DiagnosticSpeedHistorySamples;
		config.PacketGuardMovementEnabled = request.MovementEnabled;
		config.PacketGuardMovementSampleMs = request.MovementSampleMs;
		config.PacketGuardHardMoveDistance = request.HardMoveDistance;
		config.PacketGuardMovementConfirmSamples = request.MovementConfirmSamples;
		config.PacketGuardMovementConfirmWindowMs = request.MovementConfirmWindowMs;
		config.PacketGuardMovementExtremeMultiplier = request.MovementExtremeMultiplier;
		config.PacketGuardCorrectionGraceMs = request.CorrectionGraceMs;
		config.PacketGuardRejectUnauthorizedMovement = request.RejectUnauthorizedMovement;
		config.PacketGuardExemptAdmins = request.ExemptAdmins;
		config.PacketGuardExemptMountedPlayers = request.ExemptMountedPlayers;
		config.PacketGuardMountGraceMs = request.MountGraceMs;
		config.PacketGuardEngineTeleportGraceMs = request.EngineTeleportGraceMs;
		config.PacketGuardTPRunesCompatibility = request.TPRunesCompatibility;
		config.PacketGuardTPRunesGraceMs = request.TPRunesGraceMs;
		config.PacketGuardTh3EssentialsSpawnCompatibility = request.Th3EssentialsSpawnCompatibility;
		config.PacketGuardTh3EssentialsSpawnGraceMs = request.Th3EssentialsSpawnGraceMs;
		config.PacketGuardReachEnabled = request.ReachEnabled;
		config.PacketGuardReachTolerance = request.ReachTolerance;
		config.PacketGuardReachLatencyCompensation = request.ReachLatencyCompensation;
		config.PacketGuardReachMaxLatencyCompensation = request.ReachMaxLatencyCompensation;
		config.PacketGuardReachPositionHistoryEnabled = request.ReachPositionHistoryEnabled;
		config.PacketGuardReachPositionHistoryMs = request.ReachPositionHistoryMs;
		config.PacketGuardReachMaxRewindMs = request.ReachMaxRewindMs;
		config.PacketGuardReachUseExtraTolerance = request.ReachUseExtraTolerance;
		config.PacketGuardReachBreakExtraTolerance = request.ReachBreakExtraTolerance;
		config.PacketGuardReachNearThresholdStrikes = request.ReachNearThresholdStrikes;
		config.PacketGuardReachStrikeWindowMs = request.ReachStrikeWindowMs;
		config.PacketGuardReachStrikeDecayMs = request.ReachStrikeDecayMs;
		config.PacketGuardReachHardExcess = request.ReachHardExcess;
		config.PacketGuardRejectOutOfReachActions = request.RejectOutOfReachActions;
		config.PacketGuardMiningEnabled = request.MiningEnabled;
		config.PacketGuardVeinWindowMs = request.VeinWindowMs;
		config.PacketGuardConnectedBreakLimit = request.ConnectedBreakLimit;
		config.PacketGuardMiningDuplicateTargetMs = request.MiningDuplicateTargetMs;
		config.PacketGuardRejectConnectedBreakBursts = request.RejectConnectedBreakBursts;
		config.PacketGuardMiningCadenceValidationEnabled = request.MiningCadenceValidationEnabled;
		config.PacketGuardMiningCadenceMinExpectedMs = request.MiningCadenceMinExpectedMs;
		config.PacketGuardMiningCadenceSuspiciousRatio = request.MiningCadenceSuspiciousRatio;
		config.PacketGuardMiningCadenceConfirmations = request.MiningCadenceConfirmations;
		config.PacketGuardMiningCadenceConfirmationWindowMs = request.MiningCadenceConfirmationWindowMs;
		config.PacketGuardRejectImpossibleMiningCadence = request.RejectImpossibleMiningCadence;
		config.PacketGuardTpsAwarenessEnabled = request.TpsAwarenessEnabled;
		config.PacketGuardTpsDegradedPercent = request.TpsDegradedPercent;
		config.PacketGuardTpsCriticalPercent = request.TpsCriticalPercent;
		config.PacketGuardTpsMaxTimingMultiplier = request.TpsMaxTimingMultiplier;
		config.PacketGuardTpsSuppressTimingRejectsWhenCritical = request.TpsSuppressTimingRejectsWhenCritical;
		config.PerFamilyRiskEnabled = request.PerFamilyRiskEnabled;
		config.MovementRiskDecaySeconds = request.MovementRiskDecaySeconds;
		config.MovementRiskReviewThreshold = request.MovementRiskReviewThreshold;
		config.MovementRiskSuspiciousThreshold = request.MovementRiskSuspiciousThreshold;
		config.ReachRiskDecaySeconds = request.ReachRiskDecaySeconds;
		config.ReachRiskReviewThreshold = request.ReachRiskReviewThreshold;
		config.ReachRiskSuspiciousThreshold = request.ReachRiskSuspiciousThreshold;
		config.MiningRiskDecaySeconds = request.MiningRiskDecaySeconds;
		config.MiningRiskReviewThreshold = request.MiningRiskReviewThreshold;
		config.MiningRiskSuspiciousThreshold = request.MiningRiskSuspiciousThreshold;
		config.PacketAbuseRiskDecaySeconds = request.PacketAbuseRiskDecaySeconds;
		config.PacketAbuseRiskReviewThreshold = request.PacketAbuseRiskReviewThreshold;
		config.PacketAbuseRiskSuspiciousThreshold = request.PacketAbuseRiskSuspiciousThreshold;
		config.ClientIntegrityRiskDecaySeconds = request.ClientIntegrityRiskDecaySeconds;
		config.ClientIntegrityRiskReviewThreshold = request.ClientIntegrityRiskReviewThreshold;
		config.ClientIntegrityRiskSuspiciousThreshold = request.ClientIntegrityRiskSuspiciousThreshold;
		config.IncidentCorrelationEnabled = request.IncidentCorrelationEnabled;
		config.IncidentCorrelationWindowSeconds = request.IncidentCorrelationWindowSeconds;
		config.IncidentCorrelationRequiredCategories = request.IncidentCorrelationRequiredCategories;
		config.IncidentCorrelationMaxRiskBonus = request.IncidentCorrelationMaxRiskBonus;
		config.HistoryEnabled = request.HistoryEnabled;
		config.HistoryRetentionDays = request.HistoryRetentionDays;
		config.HistoryMaxRecords = request.HistoryMaxRecords;
		config.Clamp();
	}

	public (bool Success, string Message) UpdateForbiddenMod(string action, string modId, string sourcePlayer = "")
	{
		string normalized = NormalizeModId(modId);
		if (normalized.Length == 0)
		{
			return (Success: false, Message: "A valid mod ID is required. Use letters, numbers, dash, underscore, or period only.");
		}
		if (config.RequiredClientModIds.Any((string x) => string.Equals(x?.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
		{
			return (Success: false, Message: normalized + " is a required client mod and cannot also be blacklisted.");
		}
		if (IsProtectedCoreModId(normalized))
		{
			return (Success: false, Message: normalized + " is a protected Vintage Story core mod ID and cannot be blacklisted from the panel.");
		}
		action = (action ?? "").Trim().ToLowerInvariant();
		if (action == "add")
		{
			bool flag = config.ForbiddenModIds.Any((string x) => string.Equals(x?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
			List<AntiCheatForbiddenFingerprint> collection = config.ForbiddenFingerprints.Where((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
			if (!flag)
			{
				config.ForbiddenModIds.Add(normalized);
			}
			CaptureForbiddenFingerprint(normalized, sourcePlayer);
			if (!TrySaveConfig(out string error))
			{
				if (!flag)
				{
					config.ForbiddenModIds.RemoveAll((string x) => string.Equals(x?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
				}
				config.ForbiddenFingerprints.RemoveAll((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase));
				config.ForbiddenFingerprints.AddRange(collection);
				return (Success: false, Message: error);
			}
			ReevaluateCurrentSessions();
			return (Success: true, Message: flag ? (normalized + " was already blacklisted; its captured fingerprint was refreshed when player evidence was supplied.") : ("Blacklisted client mod " + normalized + ". Current accepted scans were re-evaluated immediately."));
		}
		if (action == "remove")
		{
			List<string> list = config.ForbiddenModIds.Where((string x) => string.Equals(x?.Trim(), normalized, StringComparison.OrdinalIgnoreCase)).ToList();
			if (list.Count == 0)
			{
				return (Success: true, Message: normalized + " is not currently blacklisted.");
			}
			config.ForbiddenModIds.RemoveAll((string x) => string.Equals(x?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
			List<AntiCheatForbiddenFingerprint> collection2 = config.ForbiddenFingerprints.Where((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
			config.ForbiddenFingerprints.RemoveAll((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase));
			if (!TrySaveConfig(out string error2))
			{
				config.ForbiddenModIds.AddRange(list);
				config.ForbiddenFingerprints.AddRange(collection2);
				return (Success: false, Message: error2);
			}
			ReevaluateCurrentSessions();
			return (Success: true, Message: "Removed " + normalized + " from the client mod blacklist. Current accepted scans were re-evaluated immediately.");
		}
		return (Success: false, Message: "Unknown blacklist action. Use add or remove.");
	}

	public (bool Success, string Message, string ProfileId) UpdateBlacklistProfile(string action, string profileId, string name)
	{
		action = (action ?? "").Trim().ToLowerInvariant();
		profileId = (profileId ?? "").Trim();
		name = (name ?? "").Trim();
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		AntiCheatConfig antiCheatConfig = config;
		List<AntiCheatBlacklistProfile> list = antiCheatConfig.BlacklistProfiles ?? (antiCheatConfig.BlacklistProfiles = new List<AntiCheatBlacklistProfile>());
		bool flag;
		switch (action)
		{
		case "load-default":
		{
			AntiCheatBlacklistProfile antiCheatBlacklistProfile2 = AntiCheatDefaultBlacklistProfileData.CreateProfile();
			config.ForbiddenModIds = antiCheatBlacklistProfile2.ModIds.ToList();
			config.ForbiddenFingerprints = CloneForbiddenFingerprints(antiCheatBlacklistProfile2.Fingerprints);
			config.ActiveBlacklistProfileId = "newfies-default-unfair-mods";
			config.Clamp();
			if (!TrySaveConfig(out string error2))
			{
				return (Success: false, Message: error2, ProfileId: "");
			}
			ReevaluateCurrentSessions();
			return (Success: true, Message: $"Loaded Newfie's exact supplied unfair-mod baseline ({config.ForbiddenModIds.Count} mod IDs, {config.ForbiddenFingerprints.Count} fingerprint sets). All supplied hashes and release signatures were restored into the active editable blacklist.", ProfileId: config.ActiveBlacklistProfileId);
		}
		case "load":
		{
			AntiCheatBlacklistProfile antiCheatBlacklistProfile = list.FirstOrDefault((AntiCheatBlacklistProfile x) => string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase));
			if (antiCheatBlacklistProfile == null)
			{
				return (Success: false, Message: "Blacklist profile not found.", ProfileId: "");
			}
			config.ForbiddenModIds = (antiCheatBlacklistProfile.ModIds ?? new List<string>()).ToList();
			config.ForbiddenFingerprints = CloneForbiddenFingerprints(antiCheatBlacklistProfile.Fingerprints ?? new List<AntiCheatForbiddenFingerprint>());
			config.ActiveBlacklistProfileId = antiCheatBlacklistProfile.Id;
			config.Clamp();
			if (!TrySaveConfig(out string error))
			{
				return (Success: false, Message: error, ProfileId: "");
			}
			ReevaluateCurrentSessions();
			return (Success: true, Message: $"Loaded blacklist profile '{antiCheatBlacklistProfile.Name}' with {config.ForbiddenModIds.Count} mod IDs and {config.ForbiddenFingerprints.Count} fingerprint sets. The active list is editable; changes are not written back to the saved profile until you save it.", ProfileId: antiCheatBlacklistProfile.Id);
		}
		case "save":
		case "save-as":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			AntiCheatBlacklistProfile antiCheatBlacklistProfile3 = null;
			if (action == "save" && profileId.Length > 0)
			{
				antiCheatBlacklistProfile3 = list.FirstOrDefault((AntiCheatBlacklistProfile x) => string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase));
			}
			if (antiCheatBlacklistProfile3 == null)
			{
				if (name.Length == 0)
				{
					return (Success: false, Message: "Enter a profile name first.", ProfileId: "");
				}
				if (list.Count >= 32)
				{
					return (Success: false, Message: "The maximum of 32 blacklist profiles has been reached.", ProfileId: "");
				}
				antiCheatBlacklistProfile3 = new AntiCheatBlacklistProfile
				{
					Id = Guid.NewGuid().ToString("N"),
					Name = name,
					CreatedUtc = num
				};
				list.Add(antiCheatBlacklistProfile3);
			}
			else if (name.Length > 0)
			{
				antiCheatBlacklistProfile3.Name = name;
			}
			antiCheatBlacklistProfile3.ModIds = config.ForbiddenModIds.ToList();
			HashSet<string> activeIds = new HashSet<string>(antiCheatBlacklistProfile3.ModIds, StringComparer.OrdinalIgnoreCase);
			antiCheatBlacklistProfile3.Fingerprints = CloneForbiddenFingerprints((config.ForbiddenFingerprints ?? new List<AntiCheatForbiddenFingerprint>()).Where((AntiCheatForbiddenFingerprint x) => x != null && activeIds.Contains(x.ModId ?? "")));
			antiCheatBlacklistProfile3.UpdatedUtc = num;
			if (antiCheatBlacklistProfile3.CreatedUtc <= 0)
			{
				antiCheatBlacklistProfile3.CreatedUtc = num;
			}
			config.ActiveBlacklistProfileId = antiCheatBlacklistProfile3.Id;
			config.Clamp();
			if (!TrySaveConfig(out string error3))
			{
				return (Success: false, Message: error3, ProfileId: "");
			}
			return (Success: true, Message: $"Saved the current editable blacklist as profile '{antiCheatBlacklistProfile3.Name}' ({antiCheatBlacklistProfile3.ModIds.Count} mod IDs, {antiCheatBlacklistProfile3.Fingerprints.Count} fingerprint sets).", ProfileId: antiCheatBlacklistProfile3.Id);
		}
		if (action == "delete")
		{
			AntiCheatBlacklistProfile antiCheatBlacklistProfile4 = list.FirstOrDefault((AntiCheatBlacklistProfile x) => string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase));
			if (antiCheatBlacklistProfile4 == null)
			{
				return (Success: false, Message: "Blacklist profile not found.", ProfileId: "");
			}
			list.Remove(antiCheatBlacklistProfile4);
			if (string.Equals(config.ActiveBlacklistProfileId, antiCheatBlacklistProfile4.Id, StringComparison.OrdinalIgnoreCase))
			{
				config.ActiveBlacklistProfileId = "";
			}
			config.Clamp();
			if (!TrySaveConfig(out string error4))
			{
				return (Success: false, Message: error4, ProfileId: "");
			}
			return (Success: true, Message: "Deleted blacklist profile '" + antiCheatBlacklistProfile4.Name + "'. The current active blacklist was left unchanged.", ProfileId: "");
		}
		return (Success: false, Message: "Unknown blacklist profile action.", ProfileId: "");
	}

	private static List<AntiCheatForbiddenFingerprint> CloneForbiddenFingerprints(IEnumerable<AntiCheatForbiddenFingerprint> source)
	{
		string json = JsonSerializer.Serialize(source?.Where((AntiCheatForbiddenFingerprint x) => x != null).ToList() ?? new List<AntiCheatForbiddenFingerprint>());
		return JsonSerializer.Deserialize<List<AntiCheatForbiddenFingerprint>>(json) ?? new List<AntiCheatForbiddenFingerprint>();
	}

	private void CaptureForbiddenFingerprint(string modId, string sourcePlayer)
	{
		if (string.IsNullOrWhiteSpace(sourcePlayer))
		{
			return;
		}
		AntiCheatSession antiCheatSession = sessions.Snapshot().FirstOrDefault((AntiCheatSession x) => x.PlayerUid.Equals(sourcePlayer, StringComparison.OrdinalIgnoreCase) || x.PlayerName.Equals(sourcePlayer, StringComparison.OrdinalIgnoreCase));
		if (antiCheatSession == null)
		{
			return;
		}
		ClientModDto clientModDto = antiCheatSession.LastMods.FirstOrDefault((ClientModDto x) => string.Equals(x.ModId, modId, StringComparison.OrdinalIgnoreCase));
		if (clientModDto == null)
		{
			return;
		}
		List<ClientAssemblyDto> source = antiCheatSession.LastAssemblies.Where((ClientAssemblyDto a) => (a.AssociatedModIds ?? new List<string>()).Any((string id) => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase))).ToList();
		AntiCheatForbiddenFingerprint antiCheatForbiddenFingerprint = config.ForbiddenFingerprints.FirstOrDefault((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, modId, StringComparison.OrdinalIgnoreCase));
		AntiCheatForbiddenFingerprint antiCheatForbiddenFingerprint2 = new AntiCheatForbiddenFingerprint();
		antiCheatForbiddenFingerprint2.ModId = modId;
		antiCheatForbiddenFingerprint2.Source = (((antiCheatForbiddenFingerprint?.ManualEntries?.Count).GetValueOrDefault() > 0) ? "Manual+LiveScan" : "LiveScan");
		antiCheatForbiddenFingerprint2.ResolvedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		antiCheatForbiddenFingerprint2.PackageSha256 = clientModDto.Sha256 ?? "";
		antiCheatForbiddenFingerprint2.ContentSha256 = clientModDto.ContentSha256 ?? "";
		antiCheatForbiddenFingerprint2.AssemblyNames = (from a in source
			select a.AssemblyName into x
			where !string.IsNullOrWhiteSpace(x)
			select x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		antiCheatForbiddenFingerprint2.AssemblySha256s = source.Select((ClientAssemblyDto a) => a.Sha256).Where(IsSha256).Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		antiCheatForbiddenFingerprint2.AssemblyMvids = (from a in source
			select a.ModuleVersionId into x
			where Guid.TryParse(x, out var _)
			select x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		antiCheatForbiddenFingerprint2.AssemblyStructureSha256s = source.Select((ClientAssemblyDto a) => a.StructureSha256).Where(IsSha256).Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		antiCheatForbiddenFingerprint2.AssemblyBehaviorSha256s = source.Select((ClientAssemblyDto a) => a.BehaviorSha256).Where(IsSha256).Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		antiCheatForbiddenFingerprint2.ModSystemTypeNames = (from x in source.SelectMany((ClientAssemblyDto a) => a.ModSystemTypeNames ?? new List<string>())
			where !string.IsNullOrWhiteSpace(x)
			select x).Distinct(StringComparer.Ordinal).ToList();
		antiCheatForbiddenFingerprint2.ManualEntries = (antiCheatForbiddenFingerprint?.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).ToList();
		antiCheatForbiddenFingerprint2.Releases = new List<AntiCheatReleaseFingerprint>
		{
			new AntiCheatReleaseFingerprint
			{
				Version = (clientModDto.Version ?? ""),
				FileName = (clientModDto.FileName ?? ""),
				PackageSha256 = (clientModDto.Sha256 ?? ""),
				ContentSha256 = (clientModDto.ContentSha256 ?? ""),
				PackageBytes = Math.Max(0L, clientModDto.PackageBytes),
				DllCount = Math.Max(0, clientModDto.DllCount),
				DllBytes = Math.Max(0L, clientModDto.DllBytes),
				ResolvedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Assemblies = source.Select((ClientAssemblyDto a) => new AntiCheatReleaseAssemblyFingerprint
				{
					AssemblyName = (a.AssemblyName ?? ""),
					Sha256 = (a.Sha256 ?? ""),
					FileSizeBytes = Math.Max(0L, a.FileSizeBytes),
					StructureTokenCount = a.StructureTokenCount,
					SimilarityMinHash = (a.SimilarityMinHash ?? new List<ulong>()).Take(64).ToList(),
					BehaviorSha256 = (a.BehaviorSha256 ?? ""),
					BehaviorTokenCount = a.BehaviorTokenCount,
					BehaviorMinHash = (a.BehaviorMinHash ?? new List<ulong>()).Take(64).ToList()
				}).ToList()
			}
		};
		AntiCheatForbiddenFingerprint item = antiCheatForbiddenFingerprint2;
		config.ForbiddenFingerprints.RemoveAll((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, modId, StringComparison.OrdinalIgnoreCase));
		config.ForbiddenFingerprints.Add(item);
	}

	public (bool Success, string Message) ApplyResolvedForbiddenFingerprint(AntiCheatModDbFingerprintResolution resolution)
	{
		if (resolution == null || !resolution.Success)
		{
			return (Success: false, Message: resolution?.Message ?? "The Mod DB fingerprint resolution failed.");
		}
		string normalized = NormalizeModId(resolution.ModId);
		if (normalized.Length == 0)
		{
			return (Success: false, Message: "The resolved Mod DB package did not provide a valid mod ID.");
		}
		if (!config.ForbiddenModIds.Any((string x) => string.Equals(x?.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
		{
			return (Success: false, Message: normalized + " must be on the blacklist before its Mod DB fingerprint can be stored.");
		}
		List<AntiCheatForbiddenFingerprint> list = config.ForbiddenFingerprints.Where((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
		AntiCheatForbiddenFingerprint antiCheatForbiddenFingerprint = list.FirstOrDefault();
		long resolvedNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		List<AntiCheatReleaseFingerprint> list2 = (from r in (resolution.Releases ?? new List<AntiCheatModDbReleaseFingerprintResolution>()).Take(20)
			select new AntiCheatReleaseFingerprint
			{
				Version = (r.Version ?? ""),
				FileName = (r.FileName ?? ""),
				PackageSha256 = NormalizeSha256(r.PackageSha256),
				ContentSha256 = NormalizeSha256(r.ContentSha256),
				PackageBytes = Math.Max(0L, r.PackageBytes),
				DllCount = Math.Max(0, r.DllCount),
				DllBytes = Math.Max(0L, r.DllBytes),
				ResolvedUtc = resolvedNow,
				Assemblies = (from a in (r.Assemblies ?? new List<AntiCheatModDbAssemblyFingerprintResolution>()).Take(32)
					select new AntiCheatReleaseAssemblyFingerprint
					{
						AssemblyName = (a.AssemblyName ?? ""),
						Sha256 = NormalizeSha256(a.Sha256),
						FileSizeBytes = Math.Max(0L, a.FileSizeBytes),
						StructureTokenCount = Math.Clamp(a.StructureTokenCount, 0, 4096),
						SimilarityMinHash = (a.SimilarityMinHash ?? new List<ulong>()).Take(64).ToList(),
						BehaviorSha256 = NormalizeSha256(a.BehaviorSha256),
						BehaviorTokenCount = Math.Clamp(a.BehaviorTokenCount, 0, 4096),
						BehaviorMinHash = (a.BehaviorMinHash ?? new List<ulong>()).Take(64).ToList()
					}).ToList()
			}).ToList();
		AntiCheatForbiddenFingerprint antiCheatForbiddenFingerprint2 = new AntiCheatForbiddenFingerprint();
		antiCheatForbiddenFingerprint2.ModId = normalized;
		antiCheatForbiddenFingerprint2.Source = (((antiCheatForbiddenFingerprint?.ManualEntries?.Count).GetValueOrDefault() > 0) ? "Manual+ModDB" : "ModDB");
		antiCheatForbiddenFingerprint2.ResolvedVersion = resolution.Version ?? "";
		antiCheatForbiddenFingerprint2.ResolvedFileName = resolution.FileName ?? "";
		antiCheatForbiddenFingerprint2.ResolvedUtc = resolvedNow;
		antiCheatForbiddenFingerprint2.PackageSha256 = NormalizeSha256(resolution.PackageSha256);
		antiCheatForbiddenFingerprint2.ContentSha256 = NormalizeSha256(resolution.ContentSha256);
		antiCheatForbiddenFingerprint2.AssemblyNames = (from x in (antiCheatForbiddenFingerprint?.AssemblyNames ?? new List<string>()).Concat(resolution.AssemblyNames ?? new List<string>())
			where !string.IsNullOrWhiteSpace(x)
			select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
		antiCheatForbiddenFingerprint2.AssemblySha256s = (from x in (antiCheatForbiddenFingerprint?.AssemblySha256s ?? new List<string>()).Concat(resolution.AssemblySha256s ?? new List<string>()).Select(NormalizeSha256)
			where x.Length == 64
			select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
		antiCheatForbiddenFingerprint2.AssemblyMvids = (antiCheatForbiddenFingerprint?.AssemblyMvids ?? new List<string>()).ToList();
		antiCheatForbiddenFingerprint2.AssemblyStructureSha256s = (antiCheatForbiddenFingerprint?.AssemblyStructureSha256s ?? new List<string>()).ToList();
		antiCheatForbiddenFingerprint2.AssemblyBehaviorSha256s = (antiCheatForbiddenFingerprint?.AssemblyBehaviorSha256s ?? new List<string>()).ToList();
		antiCheatForbiddenFingerprint2.ModSystemTypeNames = (antiCheatForbiddenFingerprint?.ModSystemTypeNames ?? new List<string>()).ToList();
		antiCheatForbiddenFingerprint2.Releases = list2;
		antiCheatForbiddenFingerprint2.ManualEntries = (antiCheatForbiddenFingerprint?.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).ToList();
		AntiCheatForbiddenFingerprint antiCheatForbiddenFingerprint3 = antiCheatForbiddenFingerprint2;
		if (antiCheatForbiddenFingerprint3.PackageSha256.Length != 64 && antiCheatForbiddenFingerprint3.ContentSha256.Length != 64 && antiCheatForbiddenFingerprint3.AssemblySha256s.Count == 0)
		{
			return (Success: false, Message: "The Mod DB release for " + normalized + " did not yield a usable fingerprint.");
		}
		config.ForbiddenFingerprints.RemoveAll((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase));
		config.ForbiddenFingerprints.Add(antiCheatForbiddenFingerprint3);
		if (!TrySaveConfig(out string error))
		{
			config.ForbiddenFingerprints.RemoveAll((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase));
			config.ForbiddenFingerprints.AddRange(list);
			return (Success: false, Message: error);
		}
		evaluator = new AntiCheatEvaluator(config);
		ReevaluateCurrentSessions();
		return (Success: true, Message: $"Stored {list2.Count} Mod DB release fingerprint{((list2.Count == 1) ? "" : "s")} for {normalized} ({string.Join(", ", from x in list2
			select x.Version into x
			where !string.IsNullOrWhiteSpace(x)
			select x)}). Current accepted scans were re-evaluated immediately.");
	}

	public (bool Success, string Message) UpdateManualForbiddenFingerprint(AntiCheatManualFingerprintSaveRequest request)
	{
		if (request == null)
		{
			request = new AntiCheatManualFingerprintSaveRequest();
		}
		string normalized = NormalizeModId(request.ModId);
		if (normalized.Length == 0)
		{
			return (Success: false, Message: "A valid blacklisted mod ID is required.");
		}
		if (!config.ForbiddenModIds.Any((string x) => string.Equals(x?.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
		{
			return (Success: false, Message: normalized + " must be on the blacklist before manual fingerprints can be stored.");
		}
		List<AntiCheatForbiddenFingerprint> list = config.ForbiddenFingerprints.Where((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
		AntiCheatForbiddenFingerprint antiCheatForbiddenFingerprint = list.FirstOrDefault();
		AntiCheatForbiddenFingerprint antiCheatForbiddenFingerprint2 = new AntiCheatForbiddenFingerprint
		{
			ModId = normalized,
			Source = (antiCheatForbiddenFingerprint?.Source ?? ""),
			ResolvedVersion = (antiCheatForbiddenFingerprint?.ResolvedVersion ?? ""),
			ResolvedFileName = (antiCheatForbiddenFingerprint?.ResolvedFileName ?? ""),
			ResolvedUtc = (antiCheatForbiddenFingerprint?.ResolvedUtc ?? 0),
			PackageSha256 = (antiCheatForbiddenFingerprint?.PackageSha256 ?? ""),
			ContentSha256 = (antiCheatForbiddenFingerprint?.ContentSha256 ?? ""),
			AssemblyNames = (antiCheatForbiddenFingerprint?.AssemblyNames ?? new List<string>()).ToList(),
			AssemblySha256s = (antiCheatForbiddenFingerprint?.AssemblySha256s ?? new List<string>()).ToList(),
			AssemblyMvids = (antiCheatForbiddenFingerprint?.AssemblyMvids ?? new List<string>()).ToList(),
			AssemblyStructureSha256s = (antiCheatForbiddenFingerprint?.AssemblyStructureSha256s ?? new List<string>()).ToList(),
			AssemblyBehaviorSha256s = (antiCheatForbiddenFingerprint?.AssemblyBehaviorSha256s ?? new List<string>()).ToList(),
			ModSystemTypeNames = (antiCheatForbiddenFingerprint?.ModSystemTypeNames ?? new List<string>()).ToList(),
			Releases = (antiCheatForbiddenFingerprint?.Releases ?? new List<AntiCheatReleaseFingerprint>()).ToList(),
			ManualEntries = (antiCheatForbiddenFingerprint?.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).Select((AntiCheatManualFingerprintEntry x) => new AntiCheatManualFingerprintEntry
			{
				Id = x.Id,
				Label = x.Label,
				Version = x.Version,
				FileName = x.FileName,
				Notes = x.Notes,
				PackageSha256 = x.PackageSha256,
				ContentSha256 = x.ContentSha256,
				AssemblySha256s = (x.AssemblySha256s ?? new List<string>()).ToList(),
				AssemblyMvids = (x.AssemblyMvids ?? new List<string>()).ToList(),
				AssemblyStructureSha256s = (x.AssemblyStructureSha256s ?? new List<string>()).ToList(),
				AssemblyBehaviorSha256s = (x.AssemblyBehaviorSha256s ?? new List<string>()).ToList(),
				CreatedUtc = x.CreatedUtc,
				UpdatedUtc = x.UpdatedUtc
			}).ToList()
		};
		string text = (request.Action ?? "save").Trim().ToLowerInvariant();
		if (text == "delete")
		{
			string entryId = (request.EntryId ?? "").Trim();
			if (entryId.Length == 0)
			{
				return (Success: false, Message: "Manual fingerprint entry ID is required.");
			}
			if (antiCheatForbiddenFingerprint2.ManualEntries.RemoveAll((AntiCheatManualFingerprintEntry x) => string.Equals(x.Id, entryId, StringComparison.OrdinalIgnoreCase)) == 0)
			{
				return (Success: false, Message: "That manual fingerprint entry no longer exists.");
			}
		}
		else
		{
			if (!(text == "save"))
			{
				return (Success: false, Message: "Unknown manual fingerprint action. Use save or delete.");
			}
			string text2 = NormalizeSha256(request.PackageSha256);
			string text3 = NormalizeSha256(request.ContentSha256);
			if (!string.IsNullOrWhiteSpace(request.PackageSha256) && text2.Length != 64)
			{
				return (Success: false, Message: "Package SHA-256 must be exactly 64 hexadecimal characters.");
			}
			if (!string.IsNullOrWhiteSpace(request.ContentSha256) && text3.Length != 64)
			{
				return (Success: false, Message: "Normalized content SHA-256 must be exactly 64 hexadecimal characters.");
			}
			List<string> source = (from x in request.AssemblySha256s ?? new List<string>()
				where !string.IsNullOrWhiteSpace(x)
				select x.Trim()).ToList();
			List<string> source2 = (from x in request.AssemblyMvids ?? new List<string>()
				where !string.IsNullOrWhiteSpace(x)
				select x.Trim()).ToList();
			List<string> source3 = (from x in request.AssemblyStructureSha256s ?? new List<string>()
				where !string.IsNullOrWhiteSpace(x)
				select x.Trim()).ToList();
			List<string> source4 = (from x in request.AssemblyBehaviorSha256s ?? new List<string>()
				where !string.IsNullOrWhiteSpace(x)
				select x.Trim()).ToList();
			if (source.Any((string x) => NormalizeSha256(x).Length != 64))
			{
				return (Success: false, Message: "Every assembly SHA-256 must be exactly 64 hexadecimal characters.");
			}
			if (source2.Any((string x) => !Guid.TryParse(x, out var _)))
			{
				return (Success: false, Message: "Every assembly MVID must be a valid GUID.");
			}
			if (source3.Any((string x) => NormalizeSha256(x).Length != 64))
			{
				return (Success: false, Message: "Every assembly structure SHA-256 must be exactly 64 hexadecimal characters.");
			}
			if (source4.Any((string x) => NormalizeSha256(x).Length != 64))
			{
				return (Success: false, Message: "Every assembly IL behavior SHA-256 must be exactly 64 hexadecimal characters.");
			}
			List<string> list2 = source.Select(NormalizeSha256).Distinct(StringComparer.OrdinalIgnoreCase).Take(32)
				.ToList();
			List<string> list3 = source2.Select((string x) => Guid.Parse(x).ToString("D")).Distinct(StringComparer.OrdinalIgnoreCase).Take(32)
				.ToList();
			List<string> list4 = source3.Select(NormalizeSha256).Distinct(StringComparer.OrdinalIgnoreCase).Take(32)
				.ToList();
			List<string> list5 = source4.Select(NormalizeSha256).Distinct(StringComparer.OrdinalIgnoreCase).Take(32)
				.ToList();
			if (text2.Length != 64 && text3.Length != 64 && list2.Count == 0 && list3.Count == 0 && list4.Count == 0 && list5.Count == 0)
			{
				return (Success: false, Message: "Enter at least one valid package/content/assembly SHA-256, assembly MVID, structure SHA-256, or IL behavior SHA-256.");
			}
			string entryId2 = (request.EntryId ?? "").Trim();
			AntiCheatManualFingerprintEntry antiCheatManualFingerprintEntry = antiCheatForbiddenFingerprint2.ManualEntries.FirstOrDefault((AntiCheatManualFingerprintEntry x) => string.Equals(x.Id, entryId2, StringComparison.OrdinalIgnoreCase));
			long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			if (antiCheatManualFingerprintEntry == null)
			{
				antiCheatManualFingerprintEntry = new AntiCheatManualFingerprintEntry
				{
					Id = Guid.NewGuid().ToString("N"),
					CreatedUtc = num
				};
				antiCheatForbiddenFingerprint2.ManualEntries.Add(antiCheatManualFingerprintEntry);
			}
			antiCheatManualFingerprintEntry.Label = (string.IsNullOrWhiteSpace(request.Label) ? "Manual fingerprint" : request.Label.Trim());
			antiCheatManualFingerprintEntry.Version = (request.Version ?? "").Trim();
			antiCheatManualFingerprintEntry.FileName = (request.FileName ?? "").Trim();
			antiCheatManualFingerprintEntry.Notes = (request.Notes ?? "").Trim();
			antiCheatManualFingerprintEntry.PackageSha256 = text2;
			antiCheatManualFingerprintEntry.ContentSha256 = text3;
			antiCheatManualFingerprintEntry.AssemblySha256s = list2;
			antiCheatManualFingerprintEntry.AssemblyMvids = list3;
			antiCheatManualFingerprintEntry.AssemblyStructureSha256s = list4;
			antiCheatManualFingerprintEntry.AssemblyBehaviorSha256s = list5;
			antiCheatManualFingerprintEntry.UpdatedUtc = num;
			if (antiCheatManualFingerprintEntry.CreatedUtc <= 0)
			{
				antiCheatManualFingerprintEntry.CreatedUtc = num;
			}
		}
		config.ForbiddenFingerprints.RemoveAll((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase));
		config.ForbiddenFingerprints.Add(antiCheatForbiddenFingerprint2);
		int num2 = antiCheatForbiddenFingerprint2.ManualEntries?.Count ?? 0;
		int num3 = antiCheatForbiddenFingerprint2.Releases?.Count ?? 0;
		bool flag = IsSha256(antiCheatForbiddenFingerprint2.PackageSha256) || IsSha256(antiCheatForbiddenFingerprint2.ContentSha256) || antiCheatForbiddenFingerprint2.AssemblySha256s.Count > 0 || antiCheatForbiddenFingerprint2.AssemblyMvids.Count > 0 || antiCheatForbiddenFingerprint2.AssemblyStructureSha256s.Count > 0 || antiCheatForbiddenFingerprint2.AssemblyBehaviorSha256s.Count > 0 || antiCheatForbiddenFingerprint2.ModSystemTypeNames.Count > 0;
		bool flag2 = (antiCheatForbiddenFingerprint?.Source ?? "").Contains("LiveScan", StringComparison.OrdinalIgnoreCase);
		if (num2 > 0 && num3 > 0)
		{
			antiCheatForbiddenFingerprint2.Source = "Manual+ModDB";
		}
		else if ((num2 > 0) & flag & flag2)
		{
			antiCheatForbiddenFingerprint2.Source = "Manual+LiveScan";
		}
		else if (num2 > 0)
		{
			antiCheatForbiddenFingerprint2.Source = "Manual";
		}
		else if (num3 > 0)
		{
			antiCheatForbiddenFingerprint2.Source = "ModDB";
		}
		else if (flag)
		{
			antiCheatForbiddenFingerprint2.Source = "LiveScan";
		}
		else
		{
			antiCheatForbiddenFingerprint2.Source = "";
		}
		antiCheatForbiddenFingerprint2.ResolvedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (!TrySaveConfig(out string error))
		{
			config.ForbiddenFingerprints.RemoveAll((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase));
			config.ForbiddenFingerprints.AddRange(list);
			return (Success: false, Message: error);
		}
		evaluator = new AntiCheatEvaluator(config);
		ReevaluateCurrentSessions();
		int valueOrDefault = (config.ForbiddenFingerprints.FirstOrDefault((AntiCheatForbiddenFingerprint x) => string.Equals(x.ModId, normalized, StringComparison.OrdinalIgnoreCase))?.ManualEntries?.Count).GetValueOrDefault();
		return (Success: true, Message: (text == "delete") ? $"Removed the manual fingerprint from {normalized}. {valueOrDefault} manual entr{((valueOrDefault == 1) ? "y remains" : "ies remain")}. Current accepted scans were re-evaluated immediately." : $"Saved manual blacklist fingerprint for {normalized}. {valueOrDefault} manual entr{((valueOrDefault == 1) ? "y is" : "ies are")} stored. Current accepted scans were re-evaluated immediately.");
	}

	public int GetFingerprintReleaseDepth()
	{
		return Math.Clamp(config.FingerprintReleaseDepth, 1, 20);
	}

	public (bool Success, string Message) UpdateFingerprintReleaseDepth(int releaseDepth)
	{
		int num = Math.Clamp(releaseDepth, 1, 20);
		int fingerprintReleaseDepth = config.FingerprintReleaseDepth;
		config.FingerprintReleaseDepth = num;
		if (!TrySaveConfig(out string error))
		{
			config.FingerprintReleaseDepth = fingerprintReleaseDepth;
			return (Success: false, Message: error);
		}
		return (Success: true, Message: $"Blacklist fingerprint version depth set to {num} release{((num == 1) ? "" : "s")} per mod.");
	}

	public (bool Success, string Message) UpdateClientIntegrityPolicy(string action, string hash = "", bool? requireApproved = null)
	{
		action = (action ?? "").Trim().ToLowerInvariant();
		string normalizedHash = NormalizeSha256(hash);
		bool requireApprovedClientBuild = config.RequireApprovedClientBuild;
		List<string> approvedClientBuildHashes = config.ApprovedClientBuildHashes.ToList();
		switch (action)
		{
		case "add":
			if (normalizedHash.Length != 64)
			{
				return (Success: false, Message: "A valid 64-character SHA-256 hash is required.");
			}
			if (!config.ApprovedClientBuildHashes.Contains(normalizedHash, StringComparer.OrdinalIgnoreCase))
			{
				config.ApprovedClientBuildHashes.Add(normalizedHash);
			}
			break;
		case "remove":
			if (normalizedHash.Length != 64)
			{
				return (Success: false, Message: "A valid 64-character SHA-256 hash is required.");
			}
			config.ApprovedClientBuildHashes.RemoveAll((string x) => string.Equals(x, normalizedHash, StringComparison.OrdinalIgnoreCase));
			break;
		case "require":
			if (!requireApproved.HasValue)
			{
				return (Success: false, Message: "requireApproved is required.");
			}
			if (requireApproved.Value && config.ApprovedClientBuildHashes.Count == 0)
			{
				return (Success: false, Message: "Add at least one trusted client build hash before enabling approved-build enforcement.");
			}
			config.RequireApprovedClientBuild = requireApproved.Value;
			break;
		default:
			return (Success: false, Message: "Unknown integrity-policy action.");
		}
		if (!TrySaveConfig(out string error))
		{
			config.RequireApprovedClientBuild = requireApprovedClientBuild;
			config.ApprovedClientBuildHashes = approvedClientBuildHashes;
			return (Success: false, Message: error);
		}
		evaluator = new AntiCheatEvaluator(config);
		ReevaluateCurrentSessions();
		return (Success: true, Message: $"Client build integrity policy updated. Approved hashes: {config.ApprovedClientBuildHashes.Count}; enforcement: {(config.RequireApprovedClientBuild ? "on" : "off")}.");
	}

	private static bool IsSha256(string? value)
	{
		return NormalizeSha256(value).Length == 64;
	}

	private static string NormalizeSha256(string? value)
	{
		string text = (value ?? "").Trim().ToLowerInvariant();
		return (text.Length == 64 && text.All(Uri.IsHexDigit)) ? text : "";
	}

	private bool TrySaveConfig(out string error)
	{
		error = "";
		try
		{
			config.SchemaVersion = 62;
			config.Clamp();
			config.ForbiddenModIds = (from x in config.ForbiddenModIds
				where !string.IsNullOrWhiteSpace(x)
				select x.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
			SuiteConfigStorage.Store(api, config, "NewfiesAntiCheat.json");
			return true;
		}
		catch (Exception ex)
		{
			error = "Could not save NewfiesAntiCheat.json: " + ex.Message;
			api.Logger.Error("[ServerEssentials/AntiCheat] Failed to save anti-cheat policy: {0}: {1}", ex.GetType().Name, ex.Message);
			return false;
		}
	}

	private void ReevaluateCurrentSessions()
	{
		foreach (AntiCheatSession session in sessions.Snapshot())
		{
			if (session.LastScanUtc <= 0 || session.LastMods.Count == 0)
			{
				continue;
			}
			List<ScanFindingDto> clientFindings = session.Findings.Where((ScanFindingDto f) => !IsServerEvaluationScanner(f.Scanner)).ToList();
			(AntiCheatPlayerStatus, int, List<ScanFindingDto>) tuple = evaluator.Evaluate(session.LastMods, clientFindings, session.LastAssemblies, session.LastIntegrity, session.LastHarmony, GetCanonicalModSnapshot(), GetCoreAssemblySnapshot());
			session.Findings = tuple.Item3;
			session.CorrelationRiskBonus = 0;
			long nowUtc = NowMs();
			RefreshObservedFindingTimestamps(session, tuple.Item3, nowUtc, refreshClientEvidence: false);
			UpdateSessionFamilyRisk(session, nowUtc);
			ApplyIncidentCorrelation(session, nowUtc);
			IServerPlayer serverPlayer = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerUID.Equals(session.PlayerUid, StringComparison.OrdinalIgnoreCase));
			if (serverPlayer != null)
			{
				(List<string>, List<string>) tuple2 = EvaluateStrictClientModDbAdmission(session.LastMods, session.LastAssemblies);
				if (config.StrictClientModDbEnforcementEnabled && tuple2.Item1.Count > 0)
				{
					EnforceStrictModDatabaseDisconnect(serverPlayer, session, tuple2.Item1);
				}
				else
				{
					ApplyConsequences(serverPlayer, session, "policy-reevaluation");
				}
			}
			PublishPlayer(session);
		}
	}

	public (bool Success, string Message) SaveConsequenceRules(IReadOnlyList<AntiCheatConsequenceRule>? rules, bool applyCurrent)
	{
		if (rules == null)
		{
			return (Success: false, Message: "Consequence rules are required.");
		}
		if (rules.Count > 64)
		{
			return (Success: false, Message: "A maximum of 64 anti-cheat consequence rules is supported.");
		}
		List<AntiCheatConsequenceRule> list = rules.Select(CloneRule).ToList();
		foreach (AntiCheatConsequenceRule item in list)
		{
			string text = ValidateConsequenceRule(item);
			if (text.Length > 0)
			{
				return (Success: false, Message: item.Name + ": " + text);
			}
		}
		List<AntiCheatConsequenceRule> consequenceRules = config.ConsequenceRules;
		config.ConsequenceRules = list;
		config.Clamp();
		if (!TrySaveConfig(out string error))
		{
			config.ConsequenceRules = consequenceRules;
			return (Success: false, Message: error);
		}
		if (applyCurrent)
		{
			foreach (AntiCheatSession item2 in sessions.Snapshot())
			{
				item2.AppliedConsequenceKeys.Clear();
			}
			ApplyConsequencesToCurrentSessions("policy-save");
		}
		return (Success: true, Message: applyCurrent ? $"Saved {config.ConsequenceRules.Count} consequence rule(s) and applied them to current matching sessions." : $"Saved {config.ConsequenceRules.Count} consequence rule(s). They will apply on the next matching scan/status change.");
	}

	private void ApplyConsequencesToCurrentSessions(string source)
	{
		foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
		{
			if (sessions.TryGet(item.PlayerUID, out AntiCheatSession session))
			{
				ApplyConsequences(item, session, source);
			}
		}
	}

	private bool ApplyConsequences(IServerPlayer player, AntiCheatSession session, string source)
	{
		if (!config.Enabled || config.ConsequenceRules.Count == 0)
		{
			return false;
		}
		bool result = false;
		foreach (AntiCheatConsequenceRule item2 in config.ConsequenceRules.Where((AntiCheatConsequenceRule x) => x.Enabled))
		{
			(bool, string, ScanFindingDto) tuple = MatchConsequence(item2, session);
			if (!tuple.Item1)
			{
				continue;
			}
			string item = item2.Id + ":" + tuple.Item2;
			if (session.AppliedConsequenceKeys.Contains(item))
			{
				continue;
			}
			string action = item2.Action;
			if (tuple.Item3 != null && string.Equals(tuple.Item3.Scanner, "ModDbPolicy", StringComparison.OrdinalIgnoreCase) && action != "Log" && !config.ModDbAutomaticConsequencesEnabled)
			{
				session.AppliedConsequenceKeys.Add(item);
				api.Logger.Warning("[ServerEssentials/AntiCheat] Suppressed disruptive consequence '{0}' for {1}: ModDB verification is advisory by default. Review the Client Mods comparison and enable ModDbAutomaticConsequencesEnabled only if you intentionally want external ModDB findings to punish players.", item2.Name, player.PlayerName);
				continue;
			}
			string text = ExpandConsequenceMessage(item2.Message, player, session, tuple.Item3, item2.Duration);
			AdminService adminService = adminProvider();
			string text2;
			if (action == "Log")
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat] Consequence log for {0}: rule '{1}' matched ({2}). {3}", player.PlayerName, item2.Name, tuple.Item2, text);
				adminService?.Audit("anticheat", "anticheat-log", player.PlayerUID, player.PlayerName, item2.Name + ": " + text, "anticheat");
				text2 = "logged";
			}
			else if (adminService != null)
			{
				text2 = action switch
				{
					"Kick" => adminService.Kick("anticheat", player.PlayerUID, text, "anticheat"), 
					"TempBan" => adminService.TempBan("anticheat", player.PlayerUID, item2.Duration, text, "anticheat"), 
					"Freeze" => adminService.Freeze("anticheat", player.PlayerUID, item2.Duration, text, "anticheat"), 
					_ => "failed: unsupported action", 
				};
			}
			else
			{
				api.Logger.Error("[ServerEssentials/AntiCheat] Could not apply {0} to {1}: AdminService is unavailable.", action, player.PlayerName);
				text2 = "failed: AdminService unavailable";
			}
			session.AppliedConsequenceKeys.Add(item);
			session.LastConsequence = $"{item2.Name}: {action} — {text2}";
			session.LastConsequenceUtc = NowMs();
			api.Logger.Notification("[ServerEssentials/AntiCheat] Consequence rule '{0}' -> {1} for {2} ({3}; source={4}). Result: {5}", item2.Name, action, player.PlayerName, tuple.Item2, source, text2);
			if ((!(action == "Kick") && !(action == "TempBan")) || 1 == 0)
			{
				continue;
			}
			result = adminService != null && !text2.StartsWith("failed", StringComparison.OrdinalIgnoreCase) && !text2.Contains("must be online", StringComparison.OrdinalIgnoreCase);
			break;
		}
		return result;
	}

	private void EnforceClientVersionMismatchDisconnect(IServerPlayer player, AntiCheatSession session, string? reportedVersion)
	{
		if (player == null || session.RequiredClientDisconnectScheduled)
		{
			return;
		}
		session.RequiredClientDisconnectScheduled = true;
		string requiredClientVersion = GetRequiredClientVersion();
		string text = (string.IsNullOrWhiteSpace(reportedVersion) ? "unknown" : reportedVersion.Trim());
		string text2 = $"Client Integrity version mismatch. This server requires v{requiredClientVersion} because that is the Client Integrity version installed on the server; your client reports v{text}. This is a compatibility requirement, not a cheat finding. Install Client Integrity v{requiredClientVersion} and reconnect.";
		AdminService adminService = adminProvider();
		string text3;
		if (adminService != null)
		{
			text3 = adminService.Kick("anticheat", player.PlayerUID, text2, "anticheat-client-version");
		}
		else
		{
			try
			{
				player.SendIngameError("newfies-anticheat-version", text2);
			}
			catch
			{
			}
			try
			{
				if (!PlayerDisconnectCompat.DisconnectWithReason(player, "Client Integrity version mismatch.", text2, out string error))
				{
					api.Logger.Warning("[ServerEssentials/AntiCheat] Client-version disconnect compatibility path unavailable for {0}: {1}. Falling back to API disconnect.", player.PlayerUID, error);
					player.Disconnect(text2);
				}
				text3 = "fallback disconnect completed";
			}
			catch (Exception ex)
			{
				text3 = "fallback disconnect failed: " + ex.Message;
			}
		}
		session.LastConsequence = "Client version requirement: Kick — " + text3;
		session.LastConsequenceUtc = NowMs();
		RecordConnectionDiagnostic(session, "SERVER_CLIENT_VERSION_DISCONNECT", "Compatibility", "Kick", $"Required v{requiredClientVersion}; detected v{text}. Result: {text3}");
		api.Logger.Warning("[ServerEssentials/AntiCheat] Client version mismatch rejected {0} ({1}). Required v{2}; detected v{3}. Result: {4}", player.PlayerName, player.PlayerUID, requiredClientVersion, text, text3);
	}

	private void EnforceClientIntegrityBaselineDisconnect(IServerPlayer player, AntiCheatSession session, string reason)
	{
		if (player == null || session.RequiredClientDisconnectScheduled)
		{
			return;
		}
		session.RequiredClientDisconnectScheduled = true;
		string text = (string.IsNullOrWhiteSpace(reason) ? "The Client Integrity DLL/package did not match the server-installed baseline." : reason.Trim());
		string requiredClientVersion = GetRequiredClientVersion();
		string text2 = "Client Integrity verification failed. This server requires the exact Client Integrity v" + requiredClientVersion + " build installed on the server. " + text + " Reinstall the unmodified Client Integrity package supplied/approved for this server and reconnect.";
		AdminService adminService = adminProvider();
		string text3;
		if (adminService != null)
		{
			text3 = adminService.Kick("anticheat", player.PlayerUID, text2, "anticheat-client-integrity-baseline");
		}
		else
		{
			try
			{
				player.SendIngameError("newfies-anticheat-integrity-baseline", text2);
			}
			catch
			{
			}
			try
			{
				if (!PlayerDisconnectCompat.DisconnectWithReason(player, "Client Integrity verification failed.", text2, out string error))
				{
					api.Logger.Warning("[ServerEssentials/AntiCheat] Client Integrity baseline disconnect compatibility path unavailable for {0}: {1}. Falling back to API disconnect.", player.PlayerUID, error);
					player.Disconnect(text2);
				}
				text3 = "fallback disconnect completed";
			}
			catch (Exception ex)
			{
				text3 = "fallback disconnect failed: " + ex.Message;
			}
		}
		session.LastConsequence = "Client Integrity exact baseline: Kick — " + text3;
		session.LastConsequenceUtc = NowMs();
		RecordConnectionDiagnostic(session, "CLIENT_INTEGRITY_BASELINE_DISCONNECT", "Client self-integrity", "Kick", text + " Result: " + text3);
		api.Logger.Warning("[ServerEssentials/AntiCheat] Client Integrity exact-baseline policy rejected {0} ({1}). {2} Result: {3}", player.PlayerName, player.PlayerUID, text, text3);
	}

	private void EnforceStrictModDatabaseDisconnect(IServerPlayer player, AntiCheatSession session, IReadOnlyList<string> failures)
	{
		if (player == null || session.RequiredClientDisconnectScheduled)
		{
			return;
		}
		session.RequiredClientDisconnectScheduled = true;
		string text = StrictModDbSubject(failures);
		if (string.IsNullOrWhiteSpace(text))
		{
			text = "One or more client mods did not have an exact trusted database fingerprint.";
		}
		string text2 = "Client mod verification failed. Every third-party client mod on this server must exactly match the server baseline, an administrator-approved custom fingerprint, or an official Vintage Story ModDB release. " + text + " Remove/update the unmatched mod or ask an administrator to approve the exact custom fingerprint, then reconnect.";
		AdminService adminService = adminProvider();
		string text3;
		if (adminService != null)
		{
			text3 = adminService.Kick("anticheat", player.PlayerUID, text2, "anticheat-client-mod-db");
		}
		else
		{
			try
			{
				player.SendIngameError("newfies-anticheat-client-mod-db", text2);
			}
			catch
			{
			}
			try
			{
				if (!PlayerDisconnectCompat.DisconnectWithReason(player, "Client mod database verification failed.", text2, out string error))
				{
					api.Logger.Warning("[ServerEssentials/AntiCheat] Strict client-mod DB disconnect compatibility path unavailable for {0}: {1}. Falling back to API disconnect.", player.PlayerUID, error);
					player.Disconnect(text2);
				}
				text3 = "fallback disconnect completed";
			}
			catch (Exception ex)
			{
				text3 = "fallback disconnect failed: " + ex.Message;
			}
		}
		session.LastConsequence = "Strict client-mod database policy: Kick — " + text3;
		session.LastConsequenceUtc = NowMs();
		RecordConnectionDiagnostic(session, "CLIENT_MOD_DB_DISCONNECT", "Client mod database", "Kick", text + " Result: " + text3);
		api.Logger.Warning("[ServerEssentials/AntiCheat] Strict client-mod database policy rejected {0} ({1}). {2}. Result: {3}", player.PlayerName, player.PlayerUID, text, text3);
	}

	private void EnforceStartupGateDisconnect(IServerPlayer player, AntiCheatSession session, string reason)
	{
		if (player == null || session.RequiredClientDisconnectScheduled)
		{
			return;
		}
		session.RequiredClientDisconnectScheduled = true;
		string text = (string.IsNullOrWhiteSpace(reason) ? "Startup Client Integrity verification did not complete." : reason.Trim());
		string text2 = (string.IsNullOrWhiteSpace(session.ClientVersion) ? "unknown" : session.ClientVersion.Trim());
		string requiredClientVersion = GetRequiredClientVersion();
		bool flag = session.ProtocolVersion == 13;
		bool flag2 = string.Equals(text2, requiredClientVersion, StringComparison.OrdinalIgnoreCase);
		string text3;
		if (flag)
		{
			text3 = ((config.RequireCurrentClientRelease && !flag2) ? $"Client version mismatch: detected v{text2}, while the server-installed Client Integrity companion requires v{requiredClientVersion}." : $"Protocol {session.ProtocolVersion} is compatible and the server-installed Client Integrity release is v{requiredClientVersion}.");
		}
		else
		{
			text3 = $"Protocol mismatch: detected {session.ProtocolVersion}, required {13}.";
		}
		string text4 = ((config.RequireCurrentClientRelease && !flag2) ? ("Install Client Integrity v" + requiredClientVersion + " and reconnect.") : "Reconnect and retry. If the scan keeps failing, check client startup load, disk/CPU pressure, and connection stability.");
		string text5 = $"Client Integrity startup verification failed: {text} Detected client v{text2}, protocol {session.ProtocolVersion}. " + text3 + " " + text4;
		AdminService adminService = adminProvider();
		string text6;
		if (adminService != null)
		{
			text6 = adminService.Kick("anticheat", player.PlayerUID, text5, "anticheat-startup-gate");
		}
		else
		{
			try
			{
				player.SendIngameError("newfies-anticheat-startup", text5);
			}
			catch
			{
			}
			try
			{
				if (!PlayerDisconnectCompat.DisconnectWithReason(player, "Client Integrity startup verification failed.", text5, out string error))
				{
					api.Logger.Warning("[ServerEssentials/AntiCheat] Startup-gate disconnect compatibility path unavailable for {0}: {1}. Falling back to API disconnect.", player.PlayerUID, error);
					player.Disconnect(text5);
				}
				text6 = "fallback disconnect completed";
			}
			catch (Exception ex)
			{
				text6 = "fallback disconnect failed: " + ex.Message;
			}
		}
		session.LastConsequence = "Startup integrity gate: Kick — " + text6;
		session.LastConsequenceUtc = NowMs();
		RecordConnectionDiagnostic(session, "STARTUP_GATE_DISCONNECT", "Startup scan", "Kick", text + " Result: " + text6);
		api.Logger.Warning("[ServerEssentials/AntiCheat] Startup integrity gate rejected {0} ({1}). Reason: {2}. Result: {3}", player.PlayerName, player.PlayerUID, text, text6);
	}

	private void EnforceRequiredClientDisconnect(IServerPlayer player, AntiCheatSession session)
	{
		if (session.ClientInstalled || session.LastHelloUtc > 0)
		{
			session.RequiredClientDisconnectScheduled = false;
			RecordConnectionDiagnostic(session, "REQUIRED_CLIENT_DISCONNECT_CANCELLED", "Handshake", "Recovered", "Client hello completed on the disconnect boundary; required-client kick was cancelled.");
			return;
		}
		int value = config.ClientHelloGraceSeconds + config.ClientHelloDisconnectDelaySeconds;
		string requiredClientVersion = GetRequiredClientVersion();
		string message = $"Client Integrity handshake was not received within the configured {value}s join window ({config.ClientHelloGraceSeconds}s hello grace + {config.ClientHelloDisconnectDelaySeconds}s recovery). This is a client-presence/timeout failure, not proof that the installed version is wrong. This server expects Anti-Cheat protocol {13}; the Client Integrity companion installed on the server is v{requiredClientVersion}. " + "If Client Integrity is already installed and enabled, reconnect and check for slow startup, disk/CPU load, or connection loss before reinstalling it.";
		AdminService adminService = adminProvider();
		string text;
		if (adminService != null)
		{
			text = adminService.Kick("anticheat", player.PlayerUID, message, "anticheat-required-client");
		}
		else
		{
			try
			{
				player.SendIngameError("newfies-anticheat-required", message);
			}
			catch
			{
			}
			string uid = player.PlayerUID;
			api.Event.RegisterCallback((float _) =>
			{
				try
				{
					if (!sessions.TryGet(uid, out AntiCheatSession session2) || !session2.ClientInstalled)
					{
						IServerPlayer serverPlayer = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerUID.Equals(uid, StringComparison.OrdinalIgnoreCase));
						if (serverPlayer != null && !PlayerDisconnectCompat.DisconnectWithReason(serverPlayer, "Client Integrity requirement not met.", message, out string error))
						{
							api.Logger.Warning("[ServerEssentials/AntiCheat] Correct player-facing disconnect path unavailable for {0}: {1}. Falling back to API disconnect after sending the UI reason.", uid, error);
							serverPlayer.Disconnect(message);
						}
					}
				}
				catch (Exception ex)
				{
					api.Logger.Warning("[ServerEssentials/AntiCheat] Required-client disconnect failed for {0}: {1}", uid, ex.Message);
				}
			}, 2200);
			text = "fallback disconnect scheduled";
		}
		session.LastConsequence = "Required anti-cheat client: Kick — " + text;
		session.LastConsequenceUtc = NowMs();
		RecordConnectionDiagnostic(session, "REQUIRED_CLIENT_DISCONNECT", "Handshake", "Kick", $"No client hello completed inside the total {value}s join window. {text}");
		api.Logger.Warning("[ServerEssentials/AntiCheat] Required client missing for {0}; player-facing disconnect scheduled. Result: {1}", player.PlayerName, text);
	}

	private static (bool Matched, string Fingerprint, ScanFindingDto? Finding) MatchConsequence(AntiCheatConsequenceRule rule, AntiCheatSession session)
	{
		if (rule.TriggerType == "Status")
		{
			bool item = string.Equals(session.Status.ToString(), rule.TriggerValue, StringComparison.OrdinalIgnoreCase);
			return (Matched: item, Fingerprint: "status:" + session.Status, Finding: null);
		}
		if (rule.TriggerType == "RiskAtLeast")
		{
			bool flag = int.TryParse(rule.TriggerValue, out var result);
			result = Math.Clamp(result, 0, 100);
			bool item2 = flag && session.RiskScore >= result;
			return (Matched: item2, Fingerprint: $"risk:{result}", Finding: null);
		}
		if (rule.TriggerType == "FamilyRiskAtLeast")
		{
			string[] array = (rule.TriggerValue ?? "").Split(':', 2);
			int result2 = 0;
			bool flag2 = array.Length == 2 && int.TryParse(array[1], out result2);
			result2 = (flag2 ? Math.Clamp(result2, 0, 100) : 0);
			string family = ((array.Length != 0) ? array[0].Trim() : "");
			bool flag3 = RiskFamilyOrder.Any((string x) => string.Equals(x, family, StringComparison.OrdinalIgnoreCase));
			int num = ((flag3 && session.FamilyRiskScores.TryGetValue(family, out var value)) ? value : 0);
			bool item3 = (flag2 & flag3) && num >= result2;
			return (Matched: item3, Fingerprint: $"family-risk:{family}:{result2}", Finding: null);
		}
		ScanFindingDto scanFindingDto = session.Findings.FirstOrDefault((ScanFindingDto f) => !AntiCheatObservationPolicy.IsReviewOnly(f.Code) && string.Equals(f.Code, rule.TriggerValue, StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(rule.Subject) || string.Equals(f.Subject, rule.Subject, StringComparison.OrdinalIgnoreCase)));
		return (scanFindingDto == null) ? (Matched: false, Fingerprint: "finding:" + rule.TriggerValue, Finding: null) : (Matched: true, Fingerprint: "finding:" + scanFindingDto.Code + ":" + scanFindingDto.Subject, Finding: scanFindingDto);
	}

	private static string ExpandConsequenceMessage(string template, IServerPlayer player, AntiCheatSession session, ScanFindingDto? finding, string duration)
	{
		string text = (string.IsNullOrWhiteSpace(template) ? "Anti-cheat policy matched." : template.Trim());
		return text.Replace("{player}", player.PlayerName, StringComparison.OrdinalIgnoreCase).Replace("{status}", session.Status.ToString(), StringComparison.OrdinalIgnoreCase).Replace("{risk}", session.RiskScore.ToString(), StringComparison.OrdinalIgnoreCase)
			.Replace("{code}", finding?.Code ?? "", StringComparison.OrdinalIgnoreCase)
			.Replace("{subject}", finding?.Subject ?? "", StringComparison.OrdinalIgnoreCase)
			.Replace("{duration}", duration ?? "", StringComparison.OrdinalIgnoreCase);
	}

	private static AntiCheatConsequenceRule CloneRule(AntiCheatConsequenceRule rule)
	{
		return new AntiCheatConsequenceRule
		{
			Id = rule.Id,
			Name = rule.Name,
			Enabled = rule.Enabled,
			TriggerType = rule.TriggerType,
			TriggerValue = rule.TriggerValue,
			Subject = rule.Subject,
			Action = rule.Action,
			Duration = rule.Duration,
			Message = rule.Message
		};
	}

	private static AntiCheatConsequenceRuleWebSnapshot ToWebRule(AntiCheatConsequenceRule rule)
	{
		return new AntiCheatConsequenceRuleWebSnapshot
		{
			Id = rule.Id,
			Name = rule.Name,
			Enabled = rule.Enabled,
			TriggerType = rule.TriggerType,
			TriggerValue = rule.TriggerValue,
			Subject = rule.Subject,
			Action = rule.Action,
			Duration = rule.Duration,
			Message = rule.Message
		};
	}

	private static string ValidateConsequenceRule(AntiCheatConsequenceRule rule)
	{
		if (string.IsNullOrWhiteSpace(rule.Name))
		{
			return "A rule name is required.";
		}
		bool flag;
		switch (rule.TriggerType)
		{
		case "FindingCode":
		case "Status":
		case "RiskAtLeast":
		case "FamilyRiskAtLeast":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			return "Trigger type must be FindingCode, Status, RiskAtLeast, or FamilyRiskAtLeast.";
		}
		switch (rule.Action)
		{
		case "Log":
		case "Kick":
		case "TempBan":
		case "Freeze":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			return "Action must be Log, Kick, TempBan, or Freeze.";
		}
		if (rule.TriggerType == "RiskAtLeast" && (!int.TryParse(rule.TriggerValue, out var result) || result < 0 || result > 100))
		{
			return "Risk threshold must be from 0 to 100.";
		}
		if (rule.TriggerType == "FamilyRiskAtLeast")
		{
			string[] parts = (rule.TriggerValue ?? "").Split(':', 2);
			if (parts.Length != 2 || !RiskFamilyOrder.Any((string x) => string.Equals(x, parts[0].Trim(), StringComparison.OrdinalIgnoreCase)) || !int.TryParse(parts[1], out var result2) || result2 < 0 || result2 > 100)
			{
				return "Family risk trigger must use Movement:50, Reach:50, Mining:50, PacketAbuse:50, or ClientIntegrity:50 format.";
			}
		}
		if (rule.TriggerType == "Status" && !Enum.TryParse<AntiCheatPlayerStatus>(rule.TriggerValue, ignoreCase: true, out var _))
		{
			return "Status trigger is not recognized.";
		}
		if (rule.TriggerType == "FindingCode" && string.IsNullOrWhiteSpace(rule.TriggerValue))
		{
			return "Finding code is required.";
		}
		string action = rule.Action;
		flag = ((action == "TempBan" || action == "Freeze") ? true : false);
		if (flag && ((!AdminService.TryParseDuration(rule.Duration, out var milliseconds, out var permanent) | permanent) || milliseconds < 1000))
		{
			return "Timed ban/freeze duration must be a finite value such as 30s, 10m, 6h, 3d, or 2w.";
		}
		if ((rule.Message ?? "").Length > 240)
		{
			return "Message is too long (maximum 240 characters).";
		}
		return "";
	}

	private static bool IsServerEvaluationScanner(string? scanner)
	{
		return string.Equals(scanner, "ServerPolicy", StringComparison.OrdinalIgnoreCase) || string.Equals(scanner, "FingerprintPolicy", StringComparison.OrdinalIgnoreCase) || string.Equals(scanner, "SimilarityPolicy", StringComparison.OrdinalIgnoreCase) || string.Equals(scanner, "BehaviorPolicy", StringComparison.OrdinalIgnoreCase) || string.Equals(scanner, "CorrelationPolicy", StringComparison.OrdinalIgnoreCase) || string.Equals(scanner, "AssemblyCorrelation", StringComparison.OrdinalIgnoreCase) || string.Equals(scanner, "ServerIntegrity", StringComparison.OrdinalIgnoreCase) || string.Equals(scanner, "ModDbPolicy", StringComparison.OrdinalIgnoreCase);
	}

	private void SendPacketGuardPlayerWarning(IServerPlayer player, string code)
	{
		if (!config.PacketGuardPlayerWarningEnabled || player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || AntiCheatObservationPolicy.IsReviewOnly(code) || string.IsNullOrWhiteSpace(code) || !code.StartsWith("PACKET_", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "PACKET_MINING_CADENCE_UNCERTAIN", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "PACKET_UNVERIFIED_TELEPORT_PATTERN", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		long num = NowMs();
		int num2 = Math.Max(3000, config.PacketGuardPlayerWarningCooldownSeconds * 1000);
		if (packetGuardPlayerWarningUtc.TryGetValue(player.PlayerUID, out var value) && num - value < num2)
		{
			return;
		}
		packetGuardPlayerWarningUtc[player.PlayerUID] = num;
		string text = (code ?? "").ToUpperInvariant();
		if (1 == 0)
		{
		}
		string text2 = text switch
		{
			"PACKET_NOCLIP" => "Unusual collision movement", 
			"PACKET_SPEED_HACK" => "Unusual movement speed", 
			"PACKET_ACCELERATION_HACK" => "Unusual movement acceleration", 
			"PACKET_UNAUTHORIZED_MOVEMENT" => "Unusual movement", 
			"PACKET_MOUNT_MOVEMENT_HACK" => "Unusual mounted movement", 
			"PACKET_BLOCK_REACH" => "Unusual interaction reach", 
			"PACKET_CONNECTED_BREAK_BURST" => "Unusual block breaking", 
			"PACKET_IMPOSSIBLE_MINING_CADENCE" => "Unusual mining timing", 
			"PACKET_MINING_CADENCE_UNCERTAIN" => "Unusual mining timing", 
			_ => "Unusual player activity", 
		};
		if (1 == 0)
		{
		}
		string text3 = text2;
		try
		{
			player.SendMessage(GlobalConstants.GeneralChatGroup, "[PacketGuard] " + text3 + " detected. If you believe this is a false detection, please report it to server staff.", EnumChatType.Notification);
		}
		catch
		{
		}
	}

	private void ReportServerBehaviorFinding(IServerPlayer player, string code, AntiCheatSeverity severity, string summary, string subject, string evidence)
	{
		if (config.Enabled && runtimeAvailable && player != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			bool flag = AntiCheatObservationPolicy.IsReviewOnly(code);
			SendPacketGuardPlayerWarning(player, code);
			AntiCheatSession orCreate = sessions.GetOrCreate(player.PlayerUID, player.PlayerName);
			long num = NowMs();
			bool flag2 = ConfirmedSpeedRisk.Eligible(code, (int)severity, evidence);
			PruneExpiredPacketBehaviorFindings(orCreate, num);
			RebuildSessionRiskFromCurrentEvidence(orCreate);
			int riskScore = orCreate.RiskScore;
			long lastConsequenceUtc = orCreate.LastConsequenceUtc;
			if (flag2)
			{
				int value = orCreate.ConfirmedSpeed.Observe(num);
				evidence += $";confirmedSpeedReports={value};repeatWindowMs=60000;repeatRiskBonus={orCreate.ConfirmedSpeed.Bonus(num, config.PacketGuardConfirmedSpeedRepeatRisk)}";
			}
			orCreate.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
			ScanFindingDto scanFindingDto = new ScanFindingDto
			{
				Scanner = "ServerBehavior",
				Code = code,
				Severity = (int)severity,
				Summary = (summary ?? ""),
				Subject = (subject ?? ""),
				Evidence = (evidence ?? "server-behavior")
			};
			orCreate.Findings.Add(scanFindingDto);
			orCreate.ServerBehaviorFindingUtc[code] = num;
			MarkFindingObserved(orCreate, scanFindingDto.Scanner, scanFindingDto.Code, scanFindingDto.Subject, num);
			if (!flag)
			{
				incidentCorrelator.Observe(player.PlayerUID, scanFindingDto, num);
			}
			RebuildSessionRiskFromCurrentEvidence(orCreate);
			orCreate.CorrelationRiskBonus = 0;
			ApplyIncidentCorrelation(orCreate, num);
			bool flag3 = !flag && ApplyConsequences(player, orCreate, "server-behavior");
			if (flag2 && !flag3 && config.PacketGuardKickRepeatedConfirmedSpeed && orCreate.ConfirmedSpeed.Count(num) >= 3 && orCreate.ConfirmedSpeed.Bonus(num, config.PacketGuardConfirmedSpeedRepeatRisk) >= 50)
			{
				player.Disconnect("Repeated server-confirmed impossible movement speed.");
				orCreate.LastConsequence = "Kick: repeated confirmed speed";
				orCreate.LastConsequenceUtc = num;
				api.Logger.Warning("[PacketGuard] Kicked {0}: three Critical unmounted speed reports in one minute; risk={1}.", player.PlayerName, orCreate.RiskScore);
			}
			string consequence = ((orCreate.LastConsequenceUtc > lastConsequenceUtc) ? orCreate.LastConsequence : "");
			historyStore?.Record(player, code, (int)severity, summary ?? "", subject ?? "", evidence ?? "server-behavior", riskScore, orCreate.RiskScore, orCreate.Status.ToString(), consequence);
			PublishPlayer(orCreate);
		}
	}

	private void PruneExpiredPacketBehaviorFindings(AntiCheatSession session, long nowUtc)
	{
		HashSet<string> expired = (from x in session.ServerBehaviorFindingUtc.Where((KeyValuePair<string, long> x) =>
			{
				string riskFamilyForCode = GetRiskFamilyForCode(x.Key);
				long num = (config.PerFamilyRiskEnabled ? Math.Max(10000L, (long)GetFamilyDecaySeconds(riskFamilyForCode) * 1000L) : Math.Max(10000L, (long)config.IncidentCorrelationWindowSeconds * 1000L));
				return x.Value > 0 && nowUtc - x.Value > num;
			})
			select x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (expired.Count == 0)
		{
			return;
		}
		session.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(f.Code) && f.Code.StartsWith("PACKET_", StringComparison.OrdinalIgnoreCase) && expired.Contains(f.Code));
		foreach (string item in expired)
		{
			session.ServerBehaviorFindingUtc.Remove(item);
		}
	}

	private void RebuildSessionRiskFromCurrentEvidence(AntiCheatSession session)
	{
		if (session.LastScanUtc > 0)
		{
			List<ScanFindingDto> clientFindings = session.Findings.Where((ScanFindingDto f) => !IsServerEvaluationScanner(f.Scanner)).ToList();
			(AntiCheatPlayerStatus, int, List<ScanFindingDto>) tuple = evaluator.Evaluate(session.LastMods, clientFindings, session.LastAssemblies, session.LastIntegrity, session.LastHarmony, GetCanonicalModSnapshot(), GetCoreAssemblySnapshot());
			session.Findings = tuple.Item3;
			if (!config.PerFamilyRiskEnabled)
			{
				session.Status = tuple.Item1;
				session.RiskScore = Math.Min(100, tuple.Item2 + session.ConfirmedSpeed.Bonus(NowMs(), config.PacketGuardConfirmedSpeedRepeatRisk));
				if (session.RiskScore >= 50)
				{
					session.Status = AntiCheatPlayerStatus.Suspicious;
				}
			}
			else
			{
				RefreshObservedFindingTimestamps(session, tuple.Item3, NowMs(), refreshClientEvidence: false);
				UpdateSessionFamilyRisk(session, NowMs(), preserveOperationalStatus: true);
			}
		}
		else if (config.PerFamilyRiskEnabled)
		{
			UpdateSessionFamilyRisk(session, NowMs(), preserveOperationalStatus: true);
		}
		else
		{
			int num = AntiCheatEvaluator.ComputeRiskScore(session.Findings.Where((ScanFindingDto f) => string.Equals(f.Scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase)));
			if (session.Findings.Any((ScanFindingDto f) => f.Code == "PACKET_SPEED_HACK" && f.Severity == 4))
			{
				num = Math.Min(100, num + session.ConfirmedSpeed.Bonus(NowMs(), config.PacketGuardConfirmedSpeedRepeatRisk));
			}
			session.RiskScore = num;
			AntiCheatSession antiCheatSession = session;
			AntiCheatPlayerStatus status;
			if (session.RiskScore >= 50)
			{
				status = AntiCheatPlayerStatus.Suspicious;
			}
			else
			{
				status = ((session.RiskScore >= 10) ? AntiCheatPlayerStatus.Review : AntiCheatPlayerStatus.Clean);
			}
			antiCheatSession.Status = status;
		}
	}

	private bool ApplyIncidentCorrelation(AntiCheatSession session, long nowUtc)
	{
		int riskScore = session.RiskScore;
		AntiCheatPlayerStatus status = session.Status;
		int num = Math.Max(0, session.CorrelationRiskBonus);
		string a = session.Findings.FirstOrDefault((ScanFindingDto f) => string.Equals(f.Scanner, "CorrelationPolicy", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, "CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase))?.Evidence ?? "";
		session.CorrelationRiskBonus = 0;
		session.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "CorrelationPolicy", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, "CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase));
		CorrelationResult correlationResult = incidentCorrelator.Evaluate(session.PlayerUid, nowUtc, config);
		if (correlationResult.Active)
		{
			session.Findings.Add(new ScanFindingDto
			{
				Scanner = "CorrelationPolicy",
				Code = "CROSS_DETECTOR_INCIDENT",
				Severity = (int)correlationResult.Severity,
				Summary = "Independent anti-cheat evidence units corroborated each other inside the configured incident window.",
				Subject = session.PlayerUid,
				Evidence = correlationResult.Evidence
			});
			session.CorrelationRiskBonus = correlationResult.RiskBonus;
		}
		bool flag = !config.PerFamilyRiskEnabled;
		bool flag2 = flag;
		if (flag2)
		{
			AntiCheatPlayerStatus status2 = session.Status;
			bool flag3 = (uint)(status2 - 3) <= 2u;
			flag2 = flag3;
		}
		if (flag2)
		{
			AntiCheatSession antiCheatSession = session;
			AntiCheatPlayerStatus status3;
			if (session.RiskScore >= 50)
			{
				status3 = AntiCheatPlayerStatus.Suspicious;
			}
			else
			{
				status3 = ((session.RiskScore >= 10) ? AntiCheatPlayerStatus.Review : AntiCheatPlayerStatus.Clean);
			}
			antiCheatSession.Status = status3;
		}
		string b = (correlationResult.Active ? correlationResult.Evidence : "");
		return riskScore != session.RiskScore || status != session.Status || num != session.CorrelationRiskBonus || !string.Equals(a, b, StringComparison.Ordinal);
	}

	private void RefreshIncidentExpiries()
	{
		if (!runtimeAvailable)
		{
			return;
		}
		long nowUtc = NowMs();
		foreach (AntiCheatSession session in sessions.Snapshot())
		{
			int riskScore = session.RiskScore;
			AntiCheatPlayerStatus status = session.Status;
			Dictionary<string, int> dictionary = new Dictionary<string, int>(session.FamilyRiskScores, StringComparer.OrdinalIgnoreCase);
			PruneExpiredPacketBehaviorFindings(session, nowUtc);
			if (config.PerFamilyRiskEnabled)
			{
				UpdateSessionFamilyRisk(session, nowUtc, preserveOperationalStatus: true);
			}
			bool flag = ApplyIncidentCorrelation(session, nowUtc);
			bool flag2 = riskScore != session.RiskScore || status != session.Status || dictionary.Count != session.FamilyRiskScores.Count || dictionary.Any((KeyValuePair<string, int> x) => !session.FamilyRiskScores.TryGetValue(x.Key, out var value) || value != x.Value);
			if (flag | flag2)
			{
				PublishPlayer(session);
			}
		}
	}

	private static string FindingObservationKey(string? scanner, string? code, string? subject)
	{
		return $"{(scanner ?? "").Trim()}|{(code ?? "").Trim()}|{(subject ?? "").Trim()}";
	}

	private static void MarkFindingObserved(AntiCheatSession session, string? scanner, string? code, string? subject, long nowUtc)
	{
		if (session != null && nowUtc > 0)
		{
			session.FindingObservedUtc[FindingObservationKey(scanner, code, subject)] = nowUtc;
		}
	}

	private void RefreshObservedFindingTimestamps(AntiCheatSession session, IEnumerable<ScanFindingDto> findings, long nowUtc, bool refreshClientEvidence)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ScanFindingDto item in findings ?? Array.Empty<ScanFindingDto>())
		{
			string text = FindingObservationKey(item.Scanner, item.Code, item.Subject);
			hashSet.Add(text);
			if (string.Equals(item.Scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase))
			{
				if (session.ServerBehaviorFindingUtc.TryGetValue(item.Code ?? "", out var value) && value > 0)
				{
					session.FindingObservedUtc[text] = value;
				}
			}
			else if (refreshClientEvidence || !session.FindingObservedUtc.ContainsKey(text))
			{
				session.FindingObservedUtc[text] = (refreshClientEvidence ? nowUtc : Math.Max(1L, session.LastScanUtc));
			}
		}
		long maxAge = Math.Max(60000L, (long)GetMaximumFamilyDecaySeconds() * 2000L);
		foreach (string item2 in (from x in session.FindingObservedUtc
			where nowUtc - x.Value > maxAge
			select x.Key).ToList())
		{
			session.FindingObservedUtc.Remove(item2);
		}
	}

	private void UpdateSessionFamilyRisk(AntiCheatSession session, long nowUtc, bool preserveOperationalStatus = false)
	{
		if (!config.PerFamilyRiskEnabled)
		{
			return;
		}
		Dictionary<string, Dictionary<string, int>> dictionary = RiskFamilyOrder.ToDictionary((string x) => x, (string _) => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, long> dictionary2 = RiskFamilyOrder.ToDictionary((string x) => x, (string _) => 0L, StringComparer.OrdinalIgnoreCase);
		foreach (ScanFindingDto item in session.Findings ?? new List<ScanFindingDto>())
		{
			int num = AntiCheatEvaluator.RiskPoints(item);
			if (item.Code == "PACKET_SPEED_HACK" && item.Severity == 4)
			{
				num = Math.Min(100, num + session.ConfirmedSpeed.Bonus(nowUtc, config.PacketGuardConfirmedSpeedRepeatRisk));
			}
			if (num <= 0)
			{
				continue;
			}
			string riskFamily = GetRiskFamily(item);
			if (!dictionary.ContainsKey(riskFamily))
			{
				continue;
			}
			string key = FindingObservationKey(item.Scanner, item.Code, item.Subject);
			long num2 = (session.FindingObservedUtc.TryGetValue(key, out var value) ? value : 0);
			if (num2 <= 0 && string.Equals(item.Scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase) && session.ServerBehaviorFindingUtc.TryGetValue(item.Code ?? "", out var value2))
			{
				num2 = value2;
			}
			if (num2 <= 0)
			{
				num2 = ((session.LastScanUtc > 0) ? session.LastScanUtc : nowUtc);
			}
			long num3 = Math.Max(1000L, (long)GetFamilyDecaySeconds(riskFamily) * 1000L);
			long num4 = Math.Max(0L, nowUtc - num2);
			if (num4 >= num3)
			{
				continue;
			}
			double num5 = 1.0 - (double)num4 / (double)num3;
			int num6 = Math.Clamp((int)Math.Ceiling((double)num * num5), 0, num);
			if (num6 > 0)
			{
				string key2 = item.Code ?? "";
				if (dictionary[riskFamily].TryGetValue(key2, out var value3))
				{
					dictionary[riskFamily][key2] = Math.Max(value3, num6);
				}
				else
				{
					dictionary[riskFamily][key2] = num6;
				}
				dictionary2[riskFamily] = Math.Max(dictionary2[riskFamily], num2);
			}
		}
		session.FamilyRiskScores.Clear();
		session.FamilyRiskLastEvidenceUtc.Clear();
		AntiCheatPlayerStatus antiCheatPlayerStatus = AntiCheatPlayerStatus.Clean;
		int num7 = 0;
		string[] riskFamilyOrder = RiskFamilyOrder;
		foreach (string text in riskFamilyOrder)
		{
			int num9 = ((!text.Equals("Movement", StringComparison.OrdinalIgnoreCase)) ? Math.Clamp(dictionary[text].Values.Sum(), 0, 100) : dictionary[text].Values.DefaultIfEmpty(0).Max());
			session.FamilyRiskScores[text] = num9;
			session.FamilyRiskLastEvidenceUtc[text] = dictionary2[text];
			num7 = Math.Max(num7, num9);
			(int, int) familyThresholds = GetFamilyThresholds(text);
			if (num9 >= familyThresholds.Item2)
			{
				antiCheatPlayerStatus = AntiCheatPlayerStatus.Suspicious;
			}
			else if (num9 >= familyThresholds.Item1 && antiCheatPlayerStatus != AntiCheatPlayerStatus.Suspicious)
			{
				antiCheatPlayerStatus = AntiCheatPlayerStatus.Review;
			}
		}
		session.RiskScore = num7;
		bool flag = !preserveOperationalStatus;
		bool flag2 = flag;
		if (!flag2)
		{
			AntiCheatPlayerStatus status = session.Status;
			bool flag3 = (((uint)status <= 1u || (uint)(status - 3) <= 2u) ? true : false);
			flag2 = flag3;
		}
		if (flag2)
		{
			session.Status = antiCheatPlayerStatus;
		}
	}

	private string GetRiskFamily(ScanFindingDto finding)
	{
		string text = finding.Code ?? "";
		if (AntiCheatEvaluator.IsPacketGuardMovementRiskFamily(text))
		{
			return "Movement";
		}
		if (text.Equals("PACKET_BLOCK_REACH", StringComparison.OrdinalIgnoreCase))
		{
			return "Reach";
		}
		if (text.Equals("PACKET_CONNECTED_BREAK_BURST", StringComparison.OrdinalIgnoreCase) || text.Equals("PACKET_IMPOSSIBLE_MINING_CADENCE", StringComparison.OrdinalIgnoreCase) || text.Equals("PACKET_MINING_CADENCE_UNCERTAIN", StringComparison.OrdinalIgnoreCase))
		{
			return "Mining";
		}
		if (text.Equals("INVALID_REPORT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("PACKET_", StringComparison.OrdinalIgnoreCase) || text.Contains("REPORT_REPLAY", StringComparison.OrdinalIgnoreCase) || text.Contains("NONCE", StringComparison.OrdinalIgnoreCase) || text.Contains("MALFORMED_PACKET", StringComparison.OrdinalIgnoreCase) || text.Contains("PACKET_FLOOD", StringComparison.OrdinalIgnoreCase))
		{
			return "PacketAbuse";
		}
		return "ClientIntegrity";
	}

	private static string GetRiskFamilyForCode(string? code)
	{
		if (AntiCheatEvaluator.IsPacketGuardMovementRiskFamily(code))
		{
			return "Movement";
		}
		if (string.Equals(code, "PACKET_BLOCK_REACH", StringComparison.OrdinalIgnoreCase))
		{
			return "Reach";
		}
		if (string.Equals(code, "PACKET_CONNECTED_BREAK_BURST", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "PACKET_IMPOSSIBLE_MINING_CADENCE", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "PACKET_MINING_CADENCE_UNCERTAIN", StringComparison.OrdinalIgnoreCase))
		{
			return "Mining";
		}
		if (string.Equals(code, "INVALID_REPORT", StringComparison.OrdinalIgnoreCase) || (code != null && code.StartsWith("PACKET_", StringComparison.OrdinalIgnoreCase)) || (code != null && code.Contains("REPORT_REPLAY", StringComparison.OrdinalIgnoreCase)) || (code != null && code.Contains("NONCE", StringComparison.OrdinalIgnoreCase)) || (code != null && code.Contains("MALFORMED_PACKET", StringComparison.OrdinalIgnoreCase)) || (code != null && code.Contains("PACKET_FLOOD", StringComparison.OrdinalIgnoreCase)))
		{
			return "PacketAbuse";
		}
		return "ClientIntegrity";
	}

	private int GetFamilyDecaySeconds(string family)
	{
		if (1 == 0)
		{
		}
		int result = family switch
		{
			"Movement" => config.MovementRiskDecaySeconds, 
			"Reach" => config.ReachRiskDecaySeconds, 
			"Mining" => config.MiningRiskDecaySeconds, 
			"PacketAbuse" => config.PacketAbuseRiskDecaySeconds, 
			_ => config.ClientIntegrityRiskDecaySeconds, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private int GetMaximumFamilyDecaySeconds()
	{
		return Math.Max(config.ClientIntegrityRiskDecaySeconds, Math.Max(config.PacketAbuseRiskDecaySeconds, Math.Max(config.MiningRiskDecaySeconds, Math.Max(config.ReachRiskDecaySeconds, config.MovementRiskDecaySeconds))));
	}

	private (int Review, int Suspicious) GetFamilyThresholds(string family)
	{
		if (1 == 0)
		{
		}
		(int, int) result = family switch
		{
			"Movement" => (config.MovementRiskReviewThreshold, config.MovementRiskSuspiciousThreshold), 
			"Reach" => (config.ReachRiskReviewThreshold, config.ReachRiskSuspiciousThreshold), 
			"Mining" => (config.MiningRiskReviewThreshold, config.MiningRiskSuspiciousThreshold), 
			"PacketAbuse" => (config.PacketAbuseRiskReviewThreshold, config.PacketAbuseRiskSuspiciousThreshold), 
			_ => (config.ClientIntegrityRiskReviewThreshold, config.ClientIntegrityRiskSuspiciousThreshold), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private string GetFamilyStatus(string family, int score)
	{
		(int, int) familyThresholds = GetFamilyThresholds(family);
		string result;
		if (score >= familyThresholds.Item2)
		{
			result = "Suspicious";
		}
		else
		{
			result = ((score >= familyThresholds.Item1) ? "Review" : "Clean");
		}
		return result;
	}

	private void SetRuntimeFinding(AntiCheatSession session, string code, AntiCheatSeverity severity, string summary, string? subject)
	{
		session.Findings.RemoveAll((ScanFindingDto f) => string.Equals(f.Scanner, "ServerRuntime", StringComparison.OrdinalIgnoreCase) && string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
		ScanFindingDto scanFindingDto = new ScanFindingDto
		{
			Scanner = "ServerRuntime",
			Code = code,
			Severity = (int)severity,
			Summary = summary,
			Subject = (subject ?? ""),
			Evidence = "server-runtime"
		};
		session.Findings.Add(scanFindingDto);
		MarkFindingObserved(session, scanFindingDto.Scanner, scanFindingDto.Code, scanFindingDto.Subject, NowMs());
	}

	private static bool IsProtectedCoreModId(string modId)
	{
		switch (modId)
		{
		case "game":
		case "survival":
		case "creative":
			return true;
		default:
			return false;
		}
	}

	private static string NormalizeModId(string? modId)
	{
		string text = (modId ?? "").Trim().ToLowerInvariant();
		int length = text.Length;
		if ((length < 1 || length > 64) ? true : false)
		{
			return "";
		}
		string text2 = text;
		foreach (char c in text2)
		{
			if ((c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '-' && c != '_' && c != '.')
			{
				return "";
			}
		}
		return text;
	}

	public AntiCheatPlayerWebSnapshot? GetPlayerSnapshot(string uidOrName)
	{
		if (string.IsNullOrWhiteSpace(uidOrName))
		{
			return null;
		}
		AntiCheatSession antiCheatSession = sessions.Snapshot().FirstOrDefault((AntiCheatSession x) => x.PlayerUid.Equals(uidOrName, StringComparison.OrdinalIgnoreCase) || x.PlayerName.Equals(uidOrName, StringComparison.OrdinalIgnoreCase));
		return (antiCheatSession == null) ? null : ToWebPlayer(antiCheatSession, includeConnectionDiagnostics: true);
	}

	public List<AntiCheatPlayerWebSnapshot> GetClientModsSnapshot()
	{
		HashSet<string> online = (from p in api.World.AllOnlinePlayers.OfType<IServerPlayer>()
			select p.PlayerUID).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return (from s in sessions.Snapshot()
			where online.Contains(s.PlayerUid)
			select ToWebPlayer(s, includeConnectionDiagnostics: false, includeDetails: false, includeMods: true)).ToList();
	}

	private AntiCheatPlayerWebSnapshot ToWebPlayer(AntiCheatSession s, bool includeConnectionDiagnostics = false, bool includeDetails = true, bool includeMods = false, bool includeEvidence = false)
	{
		List<ClientModDto> source = (from m in s.LastMods
			where !string.IsNullOrWhiteSpace(m.ModId)
			where !IsProtectedCoreModId(NormalizeModId(m.ModId))
			where !string.Equals(m.ModId.Trim(), "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase)
			select m).ToList();
		List<ClientModDto> list = source.Where((ClientModDto m) => !serverLoadedModIds.Contains(m.ModId.Trim())).ToList();
		AntiCheatPlayerWebSnapshot antiCheatPlayerWebSnapshot = new AntiCheatPlayerWebSnapshot
		{
			PlayerUid = s.PlayerUid,
			PlayerName = s.PlayerName,
			Decision = ((includeDetails | includeEvidence) ? BuildDecisionSnapshot(s) : null),
			Status = s.Status.ToString(),
			RiskScore = s.RiskScore,
			StartupGatePending = s.StartupGatePending,
			StartupIntegrityVerified = s.StartupIntegrityVerified,
			StartupGateStartedUtc = s.StartupGateStartedUtc,
			StartupGateCompletedUtc = s.StartupGateCompletedUtc,
			StartupGateVerdict = s.StartupGateVerdict,
			StartupGateBlockedActions = s.StartupGateBlockedActions,
			RiskFamilies = RiskFamilyOrder.Select((string family) =>
			{
				int score = (s.FamilyRiskScores.TryGetValue(family, out var value) ? value : 0);
				(int, int) familyThresholds = GetFamilyThresholds(family);
				long value2;
				return new AntiCheatRiskFamilyWebSnapshot
				{
					Family = family,
					Score = score,
					Status = GetFamilyStatus(family, score),
					ReviewThreshold = familyThresholds.Item1,
					SuspiciousThreshold = familyThresholds.Item2,
					DecaySeconds = GetFamilyDecaySeconds(family),
					LastEvidenceUtc = (s.FamilyRiskLastEvidenceUtc.TryGetValue(family, out value2) ? value2 : 0)
				};
			}).ToList(),
			ClientInstalled = s.ClientInstalled,
			ClientVersion = s.ClientVersion,
			PackageMetricsSupported = ((s.SupportedScanners & 0x100) != 0),
			ReportHashV2Supported = ((s.SupportedScanners & 0x200) != 0),
			BoundHeartbeatSupported = ((s.SupportedScanners & 0x400) != 0),
			HardenedCapabilitiesSupported = ((s.SupportedScanners & 0x600) == 1536),
			ChallengeQueueingSupported = ((s.SupportedScanners & 0x800) != 0),
			CurrentClientReleaseSupported = string.Equals(s.ClientVersion?.Trim(), GetRequiredClientVersion(), StringComparison.OrdinalIgnoreCase),
			GameVersion = s.ClientGameVersion,
			Platform = s.ClientPlatform,
			ModCount = list.Count,
			ReportedModCount = s.LastMods.Count,
			AssemblyCount = s.LastAssemblies.Count,
			ClientAssemblySha256 = (s.LastIntegrity?.AssemblySha256 ?? ""),
			ClientPackageSha256 = (s.LastIntegrity?.PackageSha256 ?? ""),
			ClientAssemblyMvid = (s.LastIntegrity?.ModuleVersionId ?? ""),
			RuntimeGeneration = (s.LastRuntime?.RuntimeGeneration ?? s.LastHeartbeatRuntimeGeneration),
			RuntimeDigest = (s.LastRuntime?.AssemblySetDigest ?? s.LastRuntimeDigest),
			RuntimeBaselineAssemblyCount = (s.LastRuntime?.BaselineAssemblyCount ?? 0),
			RuntimeLoadedAssemblyCount = (s.LastRuntime?.LoadedAssemblyCount ?? s.LastHeartbeatAssemblyCount),
			RuntimeDynamicAssemblyCount = (s.LastRuntime?.DynamicAssemblyCount ?? s.LastHeartbeatDynamicAssemblyCount),
			RuntimeNewAssemblyCount = (s.LastRuntime?.NewAssemblies?.Count).GetValueOrDefault(),
			RuntimeUnassociatedNewAssemblyCount = (s.LastRuntime?.UnassociatedNewAssemblyCount ?? 0),
			HarmonyPatchCount = (s.LastHarmony?.PatchCount ?? s.LastHeartbeatHarmonyPatchCount),
			SuspiciousHarmonyOwnerCount = (s.LastHarmony?.SuspiciousOwnerCount ?? s.LastHeartbeatSuspiciousHarmonyOwnerCount),
			HarmonyPatchDigest = (s.LastHarmony?.PatchSetDigest ?? s.LastHeartbeatHarmonyPatchDigest),
			FindingCount = s.Findings.Count,
			LastScanUtc = s.LastScanUtc,
			LastHeartbeatUtc = s.LastHeartbeatUtc,
			NextScanUtc = s.NextScanUtc,
			NetworkPacketsDropped = s.NetworkPacketsDropped,
			LastConsequence = s.LastConsequence,
			LastConsequenceUtc = s.LastConsequenceUtc,
			Findings = s.Findings.Select((ScanFindingDto f) =>
			{
				AntiCheatFindingInsight antiCheatFindingInsight = AntiCheatFindingIntelligence.Analyze(f, s.Findings);
				string evidenceDomain = AntiCheatFindingIntelligence.GetEvidenceDomain(f.Code, f.Scanner, antiCheatFindingInsight.Category);
				string key = FindingObservationKey(f.Scanner, f.Code, f.Subject);
				long observedUtc = (s.FindingObservedUtc.TryGetValue(key, out var value) ? value : ((s.LastScanUtc > 0) ? s.LastScanUtc : 0));
				bool serverAuthoritative = string.Equals(evidenceDomain, "ServerBehavior", StringComparison.OrdinalIgnoreCase) || string.Equals(f.Scanner, "ServerBehavior", StringComparison.OrdinalIgnoreCase);
				return new AntiCheatFindingWebSnapshot
				{
					Scanner = f.Scanner,
					Code = f.Code,
					Severity = f.Severity,
					Summary = f.Summary,
					Subject = f.Subject,
					Evidence = f.Evidence,
					Category = antiCheatFindingInsight.Category,
					EvidenceDomain = evidenceDomain,
					ServerAuthoritative = serverAuthoritative,
					RiskPoints = AntiCheatEvaluator.RiskPoints(f),
					ObservedUtc = observedUtc,
					Confidence = antiCheatFindingInsight.Confidence,
					ConfidenceScore = antiCheatFindingInsight.ConfidenceScore,
					CorroboratingSignals = antiCheatFindingInsight.CorroboratingSignals,
					Why = antiCheatFindingInsight.Why,
					RecommendedAction = antiCheatFindingInsight.RecommendedAction
				};
			}).ToList(),
			Mods = ((includeDetails | includeMods) ? source.Select((ClientModDto m) => ToWebModSnapshot(m, s.LastAssemblies)).OrderBy((AntiCheatModWebSnapshot m) => m.ModId, StringComparer.OrdinalIgnoreCase).ToList() : new List<AntiCheatModWebSnapshot>()),
			Assemblies = (includeDetails ? s.LastAssemblies.Select((ClientAssemblyDto a) => new AntiCheatAssemblyWebSnapshot
			{
				AssemblyName = a.AssemblyName,
				Version = a.Version,
				FileName = a.FileName,
				Sha256 = a.Sha256,
				FileSizeBytes = a.FileSizeBytes,
				ModuleVersionId = a.ModuleVersionId,
				AssociatedModIds = (a.AssociatedModIds ?? new List<string>()),
				StructureSha256 = a.StructureSha256,
				ModSystemTypeNames = (a.ModSystemTypeNames ?? new List<string>()),
				StructureTokenCount = a.StructureTokenCount,
				BehaviorSha256 = a.BehaviorSha256,
				BehaviorTokenCount = a.BehaviorTokenCount
			}).OrderBy((AntiCheatAssemblyWebSnapshot a) => a.AssemblyName, StringComparer.OrdinalIgnoreCase).ToList() : new List<AntiCheatAssemblyWebSnapshot>()),
			RuntimeNewAssemblies = (includeDetails ? (s.LastRuntime?.NewAssemblies ?? new List<ClientRuntimeAssemblyDto>()).Select((ClientRuntimeAssemblyDto a) => new AntiCheatRuntimeAssemblyWebSnapshot
			{
				AssemblyName = a.AssemblyName,
				Version = a.Version,
				FileName = a.FileName,
				ModuleVersionId = a.ModuleVersionId,
				Sha256 = a.Sha256,
				IsDynamic = a.IsDynamic,
				LoadContextName = a.LoadContextName,
				AssociatedWithLoadedMod = a.AssociatedWithLoadedMod
			}).OrderBy((AntiCheatRuntimeAssemblyWebSnapshot a) => a.AssemblyName, StringComparer.OrdinalIgnoreCase).ToList() : new List<AntiCheatRuntimeAssemblyWebSnapshot>()),
			ScannerMetrics = (includeDetails ? s.LastScannerMetrics.Select((ClientScannerMetricDto m) => new AntiCheatScannerMetricWebSnapshot
			{
				Scanner = m.Scanner,
				DurationMs = m.DurationMs,
				Success = m.Success,
				ModCount = m.ModCount,
				AssemblyCount = m.AssemblyCount,
				FindingCount = m.FindingCount,
				CacheHits = m.CacheHits,
				CacheMisses = m.CacheMisses
			}).OrderBy((AntiCheatScannerMetricWebSnapshot m) => m.Scanner, StringComparer.OrdinalIgnoreCase).ToList() : new List<AntiCheatScannerMetricWebSnapshot>()),
			RecentConnectionDiagnostics = (includeConnectionDiagnostics ? GetConnectionDiagnosticsForPlayer(s.PlayerUid, s.PlayerName) : new List<AntiCheatConnectionDiagnosticWebSnapshot>()),
			EvidenceCandidates = (includeDetails ? BuildEvidenceCandidates(s) : new List<AntiCheatEvidenceCandidateWebSnapshot>()),
			CollectedEvidence = (includeDetails ? GetEvidenceForPlayer(s.PlayerUid) : new List<AntiCheatEvidencePackageWebSnapshot>())
		};
		antiCheatPlayerWebSnapshot.StrongFindingCount = antiCheatPlayerWebSnapshot.Findings.Count((AntiCheatFindingWebSnapshot f) => f.ConfidenceScore >= 75 || f.Severity >= 3);
		antiCheatPlayerWebSnapshot.HighConfidenceFindingCount = antiCheatPlayerWebSnapshot.Findings.Count((AntiCheatFindingWebSnapshot f) => f.ConfidenceScore >= 75);
		antiCheatPlayerWebSnapshot.ServerAuthoritativeFindingCount = antiCheatPlayerWebSnapshot.Findings.Count((AntiCheatFindingWebSnapshot f) => f.ServerAuthoritative);
		if (!includeDetails && !includeEvidence)
		{
			antiCheatPlayerWebSnapshot.Findings = (from f in antiCheatPlayerWebSnapshot.Findings
				orderby f.ServerAuthoritative descending, f.ConfidenceScore descending, f.RiskPoints descending
				select f).Take(12).ToList();
			foreach (AntiCheatFindingWebSnapshot finding in antiCheatPlayerWebSnapshot.Findings)
			{
				string text = (finding.RecommendedAction = "");
				string evidence = (finding.Why = text);
				finding.Evidence = evidence;
				string summary = finding.Summary;
				if (summary != null && summary.Length > 256)
				{
					finding.Summary = finding.Summary.Substring(0, 256) + "…";
				}
				string subject = finding.Subject;
				if (subject != null && subject.Length > 256)
				{
					finding.Subject = finding.Subject.Substring(0, 256) + "…";
				}
			}
		}
		return antiCheatPlayerWebSnapshot;
	}

	private static string ComputeIntegrityDigest(ClientIntegrityDto? integrity)
	{
		if (integrity == null)
		{
			return "";
		}
		try
		{
			using SHA256 sHA = SHA256.Create();
			string s = integrity.AssemblySha256 + "|" + integrity.PackageSha256 + "|" + integrity.ContentSha256 + "|" + integrity.ModuleVersionId + "|" + integrity.AssemblyFileSizeBytes + "|" + integrity.PackageBytes + "|" + integrity.DllCount + "|" + integrity.DllBytes + "|" + integrity.BuildManifestSha256 + "|" + integrity.BuildManifestSignatureSha256 + "|" + integrity.BuildId;
			return Convert.ToHexString(sHA.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
		}
		catch
		{
			return "";
		}
	}

	private void PublishPlayer(AntiCheatSession session)
	{
		TreeAttribute treeAttribute = new TreeAttribute();
		treeAttribute.SetString("playerUid", session.PlayerUid);
		treeAttribute.SetString("playerName", session.PlayerName);
		treeAttribute.SetString("status", session.Status.ToString());
		treeAttribute.SetInt("riskScore", session.RiskScore);
		treeAttribute.SetInt("findingCount", session.Findings.Count);
		treeAttribute.SetString("clientVersion", session.ClientVersion ?? "");
		treeAttribute.SetLong("lastScanUtc", session.LastScanUtc);
		treeAttribute.SetLong("lastHeartbeatUtc", session.LastHeartbeatUtc);
		treeAttribute.SetBool("clientInstalled", session.ClientInstalled);
		api.Event.PushEvent("newfiesanticheat:player-updated", treeAttribute);
		if (session.Status == AntiCheatPlayerStatus.Suspicious)
		{
			api.Event.PushEvent("newfiesanticheat:alert", treeAttribute);
		}
	}

	private static void SendCommandReply(IServerPlayer player, int groupId, string message)
	{
		player.SendMessage(groupId, message, EnumChatType.CommandSuccess);
	}

	private static string Describe(AntiCheatSession s)
	{
		return $"{s.PlayerName}: {s.Status}, risk {s.RiskScore}, client {(s.ClientInstalled ? s.ClientVersion : "missing")}, findings {s.Findings.Count}, mods {s.LastMods.Count}";
	}

	private static bool IsSha256OrEmpty(string? value)
	{
		return string.IsNullOrWhiteSpace(value) || IsSha256(value);
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}

	public void Dispose()
	{
		if (started)
		{
			started = false;
			SaveModDbVerificationCache(force: true);
			modDbVerificationCts.Cancel();
			lock (modDbVerificationLock)
			{
				modDbVerificationCache.Clear();
				modDbVerificationCacheWeights.Clear();
				modDbVerificationCacheEstimatedBytes = 0L;
			}
			if (evidenceRetentionCleanupTickId != 0)
			{
				api.Event.UnregisterGameTickListener(evidenceRetentionCleanupTickId);
				evidenceRetentionCleanupTickId = 0L;
			}
			DeactivateRuntime();
			historyStore?.Dispose();
			historyStore = null;
			sessions.Clear();
			channel = null;
		}
	}
}
