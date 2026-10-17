using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BlockLogger.Admin;
using BlockLogger.Afk;
using BlockLogger.Analytics;
using BlockLogger.AnimalLimiter;
using BlockLogger.Announcements;
using BlockLogger.AntiCheat;
using BlockLogger.Compatibility;
using BlockLogger.Config;
using BlockLogger.Integrations;
using BlockLogger.LoginProtection;
using BlockLogger.ModUpdates;
using BlockLogger.Models;
using BlockLogger.PlayerCommands;
using BlockLogger.PlayerList;
using BlockLogger.Reports;
using BlockLogger.Restarts;
using BlockLogger.Roles;
using BlockLogger.ServerHealth;
using BlockLogger.Services;
using BlockLogger.WebConsole;
using BlockLogger.WebMap;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.WebDashboard;

public sealed class WebDashboardService : IDisposable
{
	private sealed class AntiCheatApprovalUploadState
	{
		public object Gate { get; } = new object();

		public string UploadId { get; init; } = "";

		public string TempPath { get; init; } = "";

		public string FileName { get; init; } = "";

		public string Label { get; init; } = "";

		public string Notes { get; init; } = "";

		public string Actor { get; init; } = "";

		public long ExpectedBytes { get; init; }

		public long ReceivedBytes { get; set; }

		public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
	}

	private sealed record WebConsoleCommandResult(bool Success, string Status, string Message);

	private sealed class CompatibilityIgnoreStore
	{
		public int SchemaVersion { get; set; } = 1;

		public List<CompatibilityIgnoreRecord> Ignores { get; set; } = new List<CompatibilityIgnoreRecord>();
	}

	private sealed class CompatibilityIgnoreRecord
	{
		public string Fingerprint { get; set; } = "";

		public string Reason { get; set; } = "";

		public string IgnoredBy { get; set; } = "";

		public long IgnoredUnixMilliseconds { get; set; }
	}

	private sealed class TungstenOptimizationRecommendation
	{
		public string Code { get; set; } = "";

		public int Score { get; set; }

		public string Level { get; set; } = "Not indicated";

		public string Reason { get; set; } = "";
	}

	private sealed class FeatureBreakerState
	{
		public string Key { get; set; } = "";

		public string Name { get; set; } = "";

		public int ConsecutiveFailures { get; set; }

		public long TotalFailures { get; set; }

		public long OpenUntilUnixMilliseconds { get; set; }

		public string LastError { get; set; } = "";

		public long LastFailureUnixMilliseconds { get; set; }

		public string Safety { get; set; } = "Safe";
	}

	private sealed class FeatureOverheadState
	{
		public string Key { get; set; } = "";

		public string Name { get; set; } = "";

		public long Calls { get; set; }

		public double LastMilliseconds { get; set; }

		public double TotalMilliseconds { get; set; }

		public double MaxMilliseconds { get; set; }
	}

	private sealed class WebEndpointMetricsState
	{
		public string Method { get; set; } = "";

		public string Path { get; set; } = "";

		public long Requests { get; set; }

		public long Errors { get; set; }

		public long ResponseBytes { get; set; }

		public double TotalMilliseconds { get; set; }

		public double MaxMilliseconds { get; set; }

		public double QueueWaitTotalMilliseconds { get; set; }

		public double MainThreadWaitTotalMilliseconds { get; set; }

		public double MainThreadWorkTotalMilliseconds { get; set; }

		public Queue<double> RecentDurations { get; } = new Queue<double>();
	}

	private sealed class WebRequestMetricContext
	{
		public object Sync { get; } = new object();

		public long UnixMilliseconds { get; set; }

		public string Method { get; set; } = "";

		public string Path { get; set; } = "";

		public long StartedTimestamp { get; set; }

		public double QueueWaitMilliseconds { get; set; }

		public int StatusCode { get; set; }

		public bool ResponseRejected { get; set; }

		public RouteOperationLease? RouteLease { get; set; }

		public long ResponseBytes { get; set; }

		public double MainThreadWaitMilliseconds { get; set; }

		public double MainThreadWorkMilliseconds { get; set; }
	}

	private sealed record QueuedWebClient(TcpClient Client, long AcceptedTimestamp);

	private sealed class RouteOperationLease : IDisposable
	{
		private WebDashboardService? owner;

		private readonly string operationKey;

		private readonly string displayName;

		private readonly bool releaseHeavy;

		private readonly long startedTimestamp;

		private int references = 1;

		public void Retain()
		{
			Interlocked.Increment(ref references);
		}

		public RouteOperationLease(WebDashboardService owner, string operationKey, string displayName, bool releaseHeavy)
		{
			this.owner = owner;
			this.operationKey = operationKey;
			this.displayName = displayName;
			this.releaseHeavy = releaseHeavy;
			startedTimestamp = Stopwatch.GetTimestamp();
		}

		public void Dispose()
		{
			if (Interlocked.Decrement(ref references) == 0)
			{
				Interlocked.Exchange(ref owner, null)?.FinishRouteOperation(operationKey, displayName, releaseHeavy, startedTimestamp);
			}
		}
	}

	private sealed record PerformanceSnapshotRouteResult(bool Success, string Message, PerformanceSnapshotRecord? Snapshot);

	private sealed record ConfigRestoreRouteResult(bool Success, bool NotFound, string Message);

	private sealed record LoginProtectionConfigUpdateResult(bool Success, string Message);

	private sealed record OptimizationExperimentPreparation(bool Success, string Message, OptimizationExperiment? Experiment);

	private sealed record OptimizationExperimentEvaluationResult(bool Success, string Message, OptimizationExperiment? Experiment);

	private sealed record TungstenOptimizationInfo(string Code, string Name, string Description, string Category);

	private sealed record SynergyOptimizationInfo(string Code, string Name, string Description, string Category, string Scope, string Caution);

	private sealed record TungstenCommandResult(bool Success, string Status, string Message, string Command);

	private sealed record WebIdentity(string Username, string Role, bool IsOwner);

	private sealed class WebSession
	{
		public string Token { get; init; } = "";

		public string CsrfToken { get; init; } = "";

		public string RemoteIp { get; init; } = "";

		public string Username { get; init; } = "";

		public string Role { get; init; } = "Administrator";

		public bool IsOwner { get; init; }

		public DateTime CreatedUtc { get; init; }

		public DateTime ExpiresUtc { get; set; }
	}

	private sealed class FailedLoginState
	{
		public int Failures;

		public DateTime LastFailureUtc;

		public DateTime LockedUntilUtc;
	}

	private sealed record WebTarget(int X, int Y, int Z);

	private sealed record WebActionResult(bool Success, string Message);

	private const string ConfigFile = "NewfiesWebServer.json";

	private const string AdminAccountsFile = "NewfiesWebAdminAccounts.json";

	private const string OptimizationExperimentsFile = "NewfiesOptimizationExperiments.json";

	private const string PerformanceCenterFile = "NewfiesPerformanceCenter.json";

	private const string OperationsCenterFile = "NewfiesOperationsCenter.json";

	private const string CompatibilityIgnoresFile = "NewfiesCompatibilityIgnores.json";

	public const string BuildId = "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1";

	public const string TargetGameVersion = "1.22.7";

	private const int PersistentSchemaVersion = 2;

	private const int BlockLoggerSchemaVersion = 3;

	public const int CompatibilityIgnoreSchemaVersion = 1;

	private const int MaxWebAdminAccounts = 50;

	private const long MaxAntiCheatApprovalUploadBytes = 100663296L;

	private const int MaxAntiCheatApprovalChunkBytes = 73728;

	private static readonly TimeSpan AntiCheatApprovalUploadLifetime = TimeSpan.FromMinutes(15L);

	private const double PlayerLoadLifecycleDiscontinuityMs = 30000.0;

	private const long PlayerLoadFreshSampleWindowMs = 10000L;

	private static readonly TungstenOptimizationInfo[] TungstenOptimizations = new TungstenOptimizationInfo[27]
	{
		new TungstenOptimizationInfo("entitylistreuse", "Entity List Reuse", "Entity spawning", "Lists"),
		new TungstenOptimizationInfo("blocklistreuse", "Block List Reuse", "Block operations", "Lists"),
		new TungstenOptimizationInfo("getdropslistreuse", "Get Drops List Reuse", "Block breaking", "Lists"),
		new TungstenOptimizationInfo("eventmanagerlistreuse", "Event Manager List Reuse", "Event system", "Lists"),
		new TungstenOptimizationInfo("chunkloadingoptimization", "Chunk Loading", "Chunk loading", "Chunks"),
		new TungstenOptimizationInfo("chunkunloadingoptimization", "Chunk Unloading", "Chunk unloading", "Chunks"),
		new TungstenOptimizationInfo("entitysimulationoptimization", "Entity Simulation", "Entity ticking", "Entities"),
		new TungstenOptimizationInfo("cookingcontaineroptimization", "Cooking Container", "Cooking containers", "Containers"),
		new TungstenOptimizationInfo("containeroptimization", "Container", "Inventory/container work", "Containers"),
		new TungstenOptimizationInfo("gridrecipeoptimization", "Grid Recipe", "Crafting recipe matching", "Recipes"),
		new TungstenOptimizationInfo("propickreadingoptimization", "Propick Reading", "Prospecting-pick readings", "Gameplay"),
		new TungstenOptimizationInfo("sendplayerentitydeathsoptimization", "Send Player Entity Deaths", "Player-death broadcasts", "Network"),
		new TungstenOptimizationInfo("physicsmanagerlistoptimization", "Physics Manager List", "Physics tick list reuse", "Physics"),
		new TungstenOptimizationInfo("physicsmanagermethodlistoptimization", "Physics Manager Method List", "Physics method list reuse", "Physics"),
		new TungstenOptimizationInfo("servermainlinqoptimization", "ServerMain LINQ", "Player/server queries", "Server"),
		new TungstenOptimizationInfo("placeholderoptimization", "Placeholder", "Registry placeholder processing", "Registry"),
		new TungstenOptimizationInfo("wildcardfastmatchoptimization", "Wildcard Fast Match", "Wildcard matching", "Registry"),
		new TungstenOptimizationInfo("getentitiesaroundoptimization", "Get Entities Around", "Entity queries", "Entities"),
		new TungstenOptimizationInfo("entitydespawnpacketoptimization", "Entity Despawn Packet", "Despawn packet handling", "Network"),
		new TungstenOptimizationInfo("recipebaselinqoptimization", "RecipeBase LINQ", "Recipe LINQ paths", "Recipes"),
		new TungstenOptimizationInfo("broadcastlinqoptimization", "Broadcast LINQ", "Server broadcast paths", "Network"),
		new TungstenOptimizationInfo("bulkentityattributespacketoptimization", "Bulk Entity Attributes Packet", "Entity attribute packets", "Network"),
		new TungstenOptimizationInfo("classregistryfrozenoptimization", "ClassRegistry Frozen", "Registry compaction/frozen lookup", "Registry"),
		new TungstenOptimizationInfo("getplayersaroundoptimization", "Get Players Around", "Nearby-player queries", "Server"),
		new TungstenOptimizationInfo("genterrazeroallocoptimization", "GenTerra Zero Alloc", "Worldgen column-noise allocations", "Worldgen"),
		new TungstenOptimizationInfo("genterrabitarrayoptimization", "GenTerra BitArray", "Worldgen BitArray work", "Worldgen"),
		new TungstenOptimizationInfo("genterranativenoiseoptimization", "GenTerra Native Noise", "Worldgen native-noise path", "Worldgen")
	};

	private static readonly SynergyOptimizationInfo[] SynergyOptimizations = new SynergyOptimizationInfo[15]
	{
		new SynergyOptimizationInfo("activationrange", "Entity Activation Range", "Skips most distant non-player entity ticks while preserving safety/whitelisted behaviors.", "Entities", "Server", "Can affect modded entities that require continuous far-away ticking; disable this optimization for those workloads."),
		new SynergyOptimizationInfo("collisionfastpath", "Entity Collision Fast-Path", "Skips terrain collision resolution for stationary entities when Synergy's safety conditions are met.", "Physics", "Server", "Best fit for entity-heavy areas such as animal pens; players are excluded by Synergy."),
		new SynergyOptimizationInfo("networkflush", "Network Flush Consolidation", "Batches small TCP sends and flushes them at tick boundaries to reduce syscall overhead.", "Network", "Server", "Most useful with several concurrent players; large/compressed packets still use Synergy's immediate path."),
		new SynergyOptimizationInfo("inventoryscan", "Inventory Dirty Scan", "Skips the full dirty-slot inventory scan when nothing has changed.", "Inventories", "Server", "Usually a small gain; becomes more relevant on high-player-count servers."),
		new SynergyOptimizationInfo("pathfindingpool", "Pathfinding Node Pooling", "Pools A* path nodes to reduce pathfinding allocations and GC pressure.", "Pathfinding", "Server", "Allocation optimization; useful when pathfinding/GC evidence is present."),
		new SynergyOptimizationInfo("pathfindingthrottle", "Pathfinding Distance Throttle", "Reduces pathfinding request frequency as entities get farther from players.", "Pathfinding", "Server", "Distant entities may react slightly later by design."),
		new SynergyOptimizationInfo("repulsethrottle", "Repulse Agents Throttle", "Reduces expensive entity repulsion checks for distant entities.", "Entities", "Server", "Especially useful for dense animal/entity groups because repulsion work can scale poorly."),
		new SynergyOptimizationInfo("aithrottle", "AI Brain Tick Throttle", "Reduces AI task evaluation frequency by distance while preserving running/survival-critical tasks.", "AI", "Server", "Use profiler evidence when possible; Synergy exempts urgent/combat/survival cases."),
		new SynergyOptimizationInfo("hysteresis", "Tracking Hysteresis", "Keeps entities tracked inside a buffer around the normal tracking boundary to reduce pop-in/out.", "Fluidity", "Server", "Fluidity feature rather than a direct TPS optimization."),
		new SynergyOptimizationInfo("sendfrequency", "Distance Send Frequency", "Reduces position update frequency for distant entities to lower bandwidth.", "Network", "Server", "If distant animals look choppy, this is the first Synergy option to test OFF."),
		new SynergyOptimizationInfo("attributedelta", "Attribute Sync No-Op Skip", "Avoids unnecessary full attribute-tree resync work for no-op attribute removals.", "Network", "Server", "Bandwidth/serialization optimization; benefit depends on mod/entity behavior."),
		new SynergyOptimizationInfo("spawnpriority", "Spawn Priority Ordering", "Prioritizes nearer tracked entities after login/teleport for better perceived loading.", "Fluidity", "Server", "Primarily improves perceived loading order, not server TPS."),
		new SynergyOptimizationInfo("entityloadbudget", "Entity Load Budgeting", "Budgets client entity creation across ticks after login/teleport to avoid frame spikes.", "Client Fluidity", "Client", "Requires Synergy on the client to provide the client-side benefit."),
		new SynergyOptimizationInfo("deltaencoding", "Delta Position Encoding", "Uses compact delta-encoded entity position packets for Synergy-capable clients.", "Network", "Server + Client", "Bandwidth savings require Synergy on both server and client; vanilla clients keep the vanilla packet path."),
		new SynergyOptimizationInfo("blocktickpool", "Block Tick Pooling", "Pools BlockPos instances in random block ticking to reduce allocation pressure.", "World / GC", "Server", "Most valuable when block ticking contributes measurable allocation/GC pressure.")
	};

	private static readonly WebRoutePolicyInfo[] WebRoutePolicies = new WebRoutePolicyInfo[201]
	{
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/dashboard",
			Access = "Authenticated read",
			Risk = "Read-only",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/web/status",
			Access = "Authenticated read",
			Risk = "Read-only",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/customization",
			Access = "Authenticated read",
			Risk = "Reads WebPanel branding and presentation preferences",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/customization",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes WebPanel branding and presentation preferences",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/community-motd",
			Access = "Authenticated read",
			Risk = "Reads Community MOTD companion status and configuration",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/community-motd",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes Community MOTD configuration or resets MOTD seen-state",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/playercommands",
			Access = "Authenticated read",
			Risk = "Player command availability, provider ownership, and settings read",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/playercommands/settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Enables/disables player command subsystems and changes cooldown/message policy",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/playercommands/command",
			Access = "Administrator/Owner + CSRF",
			Risk = "Enables or disables one player-facing Suite command",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/roles",
			Access = "Administrator/Owner + CSRF",
			CooldownSeconds = 1,
			Risk = "Changes game roles and shared Suite policies",
			Safety = "High"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/roles/batch",
			Access = "Administrator/Owner + CSRF",
			CooldownSeconds = 1,
			Risk = "Preview or assign up to 50 player roles",
			Safety = "High"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/roles/preview",
			Access = "Administrator/Owner + CSRF",
			Risk = "Read-only role draft preview",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/roles/player",
			Access = "Administrator/Owner + CSRF",
			CooldownSeconds = 1,
			Risk = "Assigns online or offline player roles",
			Safety = "High"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/playercommands/permissions",
			Access = "Authenticated read",
			Risk = "Configured roles, online-player roles, and direct privilege overrides",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/playercommands/permissions",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes an online player role or permanent direct privilege override",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/warps",
			Access = "Authenticated read",
			Risk = "Warp destinations and cost policy read",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/warps/settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes default warp policy",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/warps/upsert",
			Access = "Administrator/Owner + CSRF",
			Risk = "Creates or changes a teleport destination, access policy, and optional item cost",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/warps/delete",
			Access = "Administrator/Owner + CSRF",
			Risk = "Deletes a configured warp",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/warps/reload",
			Access = "Administrator/Owner + CSRF",
			Risk = "Reloads warp configuration from disk",
			Safety = "Moderate",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/discord",
			Access = "Authenticated read",
			Risk = "Discord integration/link status and configuration read (bot token excluded)",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/discord/settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes Discord integration, announcement routing, or account-linking settings",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/discord/test",
			Access = "Administrator/Owner + CSRF",
			Risk = "Queues a Discord test announcement",
			Safety = "Moderate",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/web/accounts",
			Access = "Owner only",
			Risk = "Panel account metadata read",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/web/accounts/create",
			Access = "Owner + CSRF",
			Risk = "Creates a panel administrator account",
			Safety = "Admin / High impact",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/web/accounts/action",
			Access = "Owner + CSRF",
			Risk = "Changes/disables/deletes panel administrator accounts",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/live",
			Access = "Authenticated read",
			Risk = "Cached/lightweight live state",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/features",
			Access = "Authenticated read",
			Risk = "Master feature-gate configuration read",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/features",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes which suite subsystems start after restart",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/playerlist/config",
			Access = "Authenticated read",
			Risk = "Public Tab player-list configuration and diagnostics read",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/playerlist/config",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes public Tab player-list appearance, layout, reserved-slot access, and debug settings",
			Safety = "Low",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/compatibility",
			Access = "Authenticated read",
			Risk = "Loaded-mod compatibility inspection",
			Safety = "Safe",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/compatibility/deep",
			Access = "Authenticated read",
			Risk = "Harmony patch-overlap inspection",
			Safety = "Moderate",
			CooldownSeconds = 20,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/compatibility/ignore",
			Access = "Authenticated + CSRF",
			Risk = "Suppresses a compatibility notification",
			Safety = "Safe",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/compatibility/unignore",
			Access = "Authenticated + CSRF",
			Risk = "Restores a compatibility notification",
			Safety = "Safe",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/log-intelligence",
			Access = "Authenticated read",
			Risk = "Cached log diagnosis/preflight state",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/log-intelligence/block-entity",
			Access = "Authenticated read",
			Risk = "Live inspection of one tracked block/block entity",
			Safety = "Safe",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/log-intelligence/block-entity/action",
			Access = "Administrator/Owner + CSRF",
			Risk = "Can stop/resume tracking, clear tracker entries, teleport an online player, remove a block entity, or replace a block",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/log-intelligence/issues/clear",
			Access = "Administrator/Owner + CSRF",
			Risk = "Clears retained grouped issue signatures without deleting server logs",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/player-analytics",
			Access = "Authenticated read",
			Risk = "Read-only player-count history",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/server-status",
			Access = "Authenticated read",
			Risk = "Read-only server status and player analytics",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/anticheat/status",
			Access = "Authenticated read",
			Risk = "Lightweight Anti-Cheat navigation health counters only",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/anticheat",
			Access = "Authenticated read",
			Risk = "Read-only anti-cheat client/session status",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/anticheat/client-mods",
			Access = "Authenticated read",
			Risk = "On-demand client mod comparison inventories",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/anticheat/player",
			Access = "Authenticated read",
			Risk = "Sensitive anti-cheat client mod/finding detail",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/anticheat/history",
			Access = "Authenticated read",
			Risk = "Persistent anti-cheat incident history including evidence and positions",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/history/clear",
			Access = "Administrator/Owner + CSRF",
			Risk = "Permanently deletes all persistent anti-cheat incident reports",
			Safety = "High",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Enables or disables live Anti-Cheat enforcement, including client scans, PacketGuard and automatic consequences; installed companion dependency remains required on clients",
			Safety = "High",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/client-settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes Client Integrity handshake/startup scan timeouts, startup quarantine gate, rescan cadence, hardened transport requirement, and official ModDB verification of client mods",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/anticheat/full-config",
			Access = "Authenticated read",
			Risk = "Reads the complete Anti-Cheat configuration for advanced administration",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/full-config",
			Access = "Administrator/Owner + CSRF",
			Risk = "Replaces the complete editable Anti-Cheat configuration; module lifecycle and schema version remain protected",
			Safety = "High",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/packetguard",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes server-authoritative packet/movement/reach/mining protection and false-positive exemptions",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/oreshield",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes Ore Shield concealment, host/ore matching and chunk-cache policy",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/rescan",
			Access = "Administrator/Owner + CSRF",
			Risk = "Requests an immediate client anti-cheat rescan",
			Safety = "Moderate",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/evidence/request",
			Access = "Administrator/Owner + CSRF",
			Risk = "Requests player authorization to transfer the exact loaded suspicious mod package; accepted packages are stored under SuspectedHacks and explicit decline triggers the configured server policy",
			Safety = "High",
			CooldownSeconds = 3
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/evidence/analyze",
			Access = "Administrator/Owner + CSRF",
			Risk = "Hashes a captured suspicious mod package and compares its bounded file manifest with cached official same-version ModDB candidates",
			Safety = "Moderate",
			CooldownSeconds = 3
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/evidence/settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes suspicious package evidence collection limits",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/blacklist",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes forbidden mod IDs/fingerprints and re-evaluates current sessions",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/moddb/approval",
			Access = "Administrator/Owner + CSRF",
			Risk = "Adds or removes an exact ModID/version/normalized-content fingerprint exception for intentional private or patched client mods",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/moddb/approval/upload",
			Access = "Administrator/Owner + CSRF",
			Risk = "Receives a bounded mod ZIP in chunks, inspects root modinfo.json, hashes package/content without executing it, and can approve that exact client-mod fingerprint",
			Safety = "High"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/moddb/resolve-all",
			Access = "Administrator/Owner + CSRF",
			Risk = "Queues exact official ModDB fingerprint resolution for all unique third-party client mod ID/version pairs currently reported by online clients",
			Safety = "Moderate",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/blacklist/profile",
			Access = "Administrator/Owner + CSRF",
			Risk = "Loads, saves, or deletes named blacklist profiles and can replace the active blacklist",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/blacklist/resolve",
			Access = "Administrator/Owner + CSRF",
			Risk = "Downloads an official Mod DB release in memory and stores blacklist package/content fingerprints",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/blacklist/fingerprint-settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes how many recent Mod DB releases are fingerprinted per blacklisted mod",
			Safety = "Medium",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/blacklist/manual",
			Access = "Administrator/Owner + CSRF",
			Risk = "Adds, edits, or removes operator-supplied exact blacklist fingerprints and re-evaluates current sessions",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/integrity-policy",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes approved Client Integrity build hashes and build-lock enforcement",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/anticheat/consequences",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes automatic anti-cheat moderation consequences; can kick, temporarily ban, or freeze matching players",
			Safety = "High",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/restarts",
			Access = "Authenticated read",
			Risk = "Automated restart schedule/status read",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/restarts/settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes automatic restart schedule and warning behavior",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/restarts/action",
			Access = "Administrator/Owner + CSRF",
			Risk = "Can skip/reload/toggle automatic restarts or schedule/cancel a one-off restart",
			Safety = "High",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/chat-display",
			Access = "Authenticated read",
			Risk = "Chat presentation settings read",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/chat-display/settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes player chat presentation and shared role name colors",
			Safety = "Low",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/chatguard",
			Access = "Authenticated read",
			Risk = "Optional standalone ChatGuard moderation status and audit summary",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/chatguard/settings",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes standalone ChatGuard moderation rules and enforcement mode",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/chatguard/action",
			Access = "Administrator/Owner + CSRF",
			Risk = "Can warn, temporarily/permanently mute, unmute, or clear ChatGuard warnings through the standalone module",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/chatguard/test",
			Access = "Administrator/Owner + CSRF",
			Risk = "Dry-runs a message through ChatGuard rules without changing moderation state or counters",
			Safety = "Safe",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/announcements",
			Access = "Authenticated read",
			Risk = "Announcement schedule/configuration read",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/announcements/save",
			Access = "Authenticated + CSRF",
			Risk = "Announcement schedule/configuration change",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/announcements/action",
			Access = "Authenticated + CSRF",
			Risk = "Immediate announcement/reset action",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/console",
			Access = "Authenticated read",
			Risk = "Server log/console mirror",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/console/input",
			Access = "Authenticated + CSRF",
			Risk = "Enables web console command input for 15 minutes",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/console/command",
			Access = "Authenticated + CSRF",
			Risk = "Executes a Vintage Story server command or broadcasts a server message",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/webmap",
			Access = "Authenticated read",
			Risk = "Native map host status plus cached map-layer data",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/leaderboard",
			Access = "Authenticated read",
			Risk = "Cached community progression leaderboard including offline XSkills progress",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/webmap/terrain/status",
			Access = "Authenticated read",
			Risk = "Lightweight terrain export progress/status only",
			Safety = "Low"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/webmap/config",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes native map listener, public layers, and one-shot next-start terrain export/lock configuration",
			Safety = "Admin",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/webmap/export-schedule",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes only startup terrain export scheduling/mode settings while preserving the Web Map listener and public-layer configuration",
			Safety = "Admin",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/webmap/terrain/update",
			Access = "Administrator/Owner + CSRF",
			Risk = "Explicitly updates only changed/new/missing live terrain tiles when the current cache is compatible",
			Safety = "Admin",
			CooldownSeconds = 3
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/webmap/terrain/force",
			Access = "Administrator/Owner + CSRF",
			Risk = "Explicitly starts a live terrain export; may temporarily increase server disk/CPU load",
			Safety = "Admin",
			CooldownSeconds = 5
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/webmap/waypoint/save",
			Access = "Administrator/Owner + CSRF",
			Risk = "Creates or edits a persistent public Web Map waypoint",
			Safety = "Admin",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/webmap/waypoint/delete",
			Access = "Administrator/Owner + CSRF",
			Risk = "Deletes a persistent public Web Map waypoint",
			Safety = "Admin",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/mod-updates",
			Access = "Authenticated read",
			Risk = "Cached Mod DB update status",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/mod-updates/check",
			Access = "Authenticated + CSRF",
			Risk = "Outbound official Mod DB update check",
			Safety = "Moderate",
			CooldownSeconds = 30,
			ExclusiveHeavyOperation = false
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/mod-updates/apply",
			Access = "Administrator/Owner + CSRF",
			Risk = "Downloads, validates, backs up and replaces a loaded ZIP mod package on disk; restart required",
			Safety = "High",
			CooldownSeconds = 10,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/reports",
			Access = "Authenticated read",
			Risk = "Sensitive issue report queue",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/reports/detail",
			Access = "Authenticated read",
			Risk = "Paged private report history",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/reports/action",
			Access = "Authenticated + CSRF",
			Risk = "Moderation workflow change",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/log-intelligence/scan",
			Access = "Authenticated + CSRF",
			Risk = "Manual current/archive log scan",
			Safety = "Moderate",
			CooldownSeconds = 20,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/log-intelligence/preflight",
			Access = "Authenticated + CSRF",
			Risk = "Loaded-mod/config/command preflight inspection",
			Safety = "Safe",
			CooldownSeconds = 5
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/collection-audit",
			Access = "Authenticated read",
			Risk = "Read-only collection-cost audit",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/loginprotection/config",
			Access = "Authenticated read",
			Risk = "Configuration read",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/loginprotection/config",
			Access = "Authenticated + CSRF",
			Risk = "Configuration-changing",
			Safety = "Moderate",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/admin/status",
			Access = "Authenticated read",
			Risk = "Sensitive read",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/admin/players",
			Access = "Authenticated read",
			Risk = "Sensitive read",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/admin/database-storage",
			Access = "Authenticated read",
			Risk = "Cached database disk-usage viewer",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/admin/database-storage/analyze",
			Access = "Administrator/Owner + CSRF",
			Risk = "Manual SQLite page-usage analysis",
			Safety = "Moderate",
			CooldownSeconds = 15,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/admin/player",
			Access = "Authenticated read",
			Risk = "Sensitive read",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/admin/inventory",
			Access = "Authenticated read",
			Risk = "Live or read-only saved player inventory, snapshots and inventory-only audit history",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/admin/items",
			Access = "Authenticated read",
			Risk = "Item catalog search",
			Safety = "Low"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/admin/inventory/action",
			Access = "Authenticated + CSRF",
			Risk = "Inventory mutation",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/admin/inventory/compare",
			Access = "Authenticated read",
			Risk = "Inventory snapshot comparison",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/admin/inventory/snapshot",
			Access = "Authenticated + CSRF",
			Risk = "Inventory snapshot/restore",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/admin/action",
			Access = "Authenticated + CSRF",
			Risk = "Player administration",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/admin/maintenance",
			Access = "Authenticated + CSRF",
			Risk = "Server access control",
			Safety = "Admin / High impact",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/admin/shutdown",
			Access = "Authenticated + CSRF",
			Risk = "Server shutdown",
			Safety = "Admin / High impact",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/admin/cleanup",
			Access = "Authenticated + CSRF",
			Risk = "Entity cleanup",
			Safety = "Admin / High impact",
			CooldownSeconds = 5,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/health",
			Access = "Authenticated read",
			Risk = "Read-only",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/health/database-integrity",
			Access = "Authenticated + CSRF",
			Risk = "Diagnostic action",
			Safety = "Moderate",
			CooldownSeconds = 60,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/health/safe-mode",
			Access = "Authenticated + CSRF",
			Risk = "Configuration-changing",
			Safety = "Moderate",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/health/circuit-reset",
			Access = "Authenticated + CSRF",
			Risk = "Diagnostic recovery",
			Safety = "Moderate",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/operations/overview",
			Access = "Authenticated read",
			Risk = "Read-only",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/operations/diagnostics",
			Access = "Authenticated read",
			Risk = "Read-only counters",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/admin/investigation",
			Access = "Authenticated read",
			Risk = "Sensitive player investigation",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/operations/memory",
			Access = "Authenticated read",
			Risk = "Read-only",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/operations/memory-snapshot",
			Access = "Authenticated + CSRF",
			Risk = "Captures or manages lightweight memory investigation snapshots",
			Safety = "Safe",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/operations/audit",
			Access = "Authenticated read",
			Risk = "Sensitive read",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/operations/alert-status",
			Access = "Authenticated + CSRF",
			Risk = "Workflow change",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/operations/clear",
			Access = "Administrator/Owner + CSRF",
			Risk = "Clears reviewed Action Center records",
			Safety = "Safe",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/operations/action-status",
			Access = "Authenticated + CSRF",
			Risk = "Workflow change",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/operations/performance-snapshot",
			Access = "Authenticated + CSRF",
			Risk = "Diagnostic action",
			Safety = "Safe",
			CooldownSeconds = 10
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/operations/config-snapshot",
			Access = "Authenticated + CSRF",
			Risk = "Configuration backup",
			Safety = "Moderate",
			CooldownSeconds = 5
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/operations/config-restore",
			Access = "Authenticated + CSRF",
			Risk = "Configuration-changing",
			Safety = "Admin / High impact",
			CooldownSeconds = 30,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/performance/entity-scan",
			Access = "Authenticated + CSRF",
			Risk = "Heavy diagnostic action",
			Safety = "Moderate",
			CooldownSeconds = 30,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/conflicts",
			Access = "Authenticated read",
			Risk = "Heavy diagnostic action",
			Safety = "Moderate",
			CooldownSeconds = 20,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/bundle",
			Access = "Authenticated read",
			Risk = "Heavy export",
			Safety = "Moderate",
			CooldownSeconds = 30,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/animals/scan",
			Access = "Authenticated + CSRF",
			Risk = "Bounded incremental diagnostic",
			Safety = "Moderate",
			CooldownSeconds = 15,
			ExclusiveHeavyOperation = false
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/animals/scan-status",
			Access = "Authenticated read",
			Risk = "Lightweight progress poll",
			Safety = "Safe",
			CooldownSeconds = 0,
			ExclusiveHeavyOperation = false
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/tick-debug",
			Access = "Authenticated read",
			Risk = "Read-only native tick diagnostics",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/tick-debug/enabled",
			Access = "Administrator/Owner + CSRF",
			Risk = "Enables or disables Tick Debugger actions",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/tick-debug/slow-start",
			Access = "Authenticated + CSRF",
			Risk = "Native slow-tick logging",
			Safety = "Moderate",
			CooldownSeconds = 10,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/tick-debug/slow-stop",
			Access = "Authenticated + CSRF",
			Risk = "Stops native slow-tick logging",
			Safety = "Safe",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/tick-debug/action",
			Access = "Authenticated + CSRF",
			Risk = "Native diagnostic command",
			Safety = "Moderate",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/tick-debug/incident",
			Access = "Authenticated + CSRF",
			Risk = "Combined native slow-tick and engine profile capture",
			Safety = "Admin / High impact",
			CooldownSeconds = 30,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/profiler/start",
			Access = "Authenticated + CSRF",
			Risk = "Engine profiling",
			Safety = "Admin / High impact",
			CooldownSeconds = 30,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/profiler/enabled",
			Access = "Authenticated + CSRF",
			Risk = "Diagnostic configuration",
			Safety = "Safe",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/profiler/clear",
			Access = "Authenticated + CSRF",
			Risk = "Clears retained profiler result lists",
			Safety = "Safe",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/performance/lag-history/clear",
			Access = "Authenticated + CSRF",
			Risk = "Clears retained lag diagnostic history",
			Safety = "Safe",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/tick-debug/clear",
			Access = "Authenticated + CSRF",
			Risk = "Clears retained Tick Debugger output",
			Safety = "Safe",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/optimizations/tungsten/test",
			Access = "Authenticated + CSRF",
			Risk = "External optimizer change",
			Safety = "External / High impact",
			CooldownSeconds = 10,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/optimizations/tungsten/toggle",
			Access = "Authenticated + CSRF",
			Risk = "External optimizer change",
			Safety = "External / High impact",
			CooldownSeconds = 3
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/optimizations/tungsten/all",
			Access = "Authenticated + CSRF",
			Risk = "External optimizer change",
			Safety = "External / High impact",
			CooldownSeconds = 10,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/optimizations/tungsten/action",
			Access = "Authenticated + CSRF",
			Risk = "External optimizer command",
			Safety = "External",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/optimizations/synergy/test",
			Access = "Authenticated + CSRF",
			Risk = "External optimizer change",
			Safety = "External / High impact",
			CooldownSeconds = 10,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/optimizations/synergy/toggle",
			Access = "Authenticated + CSRF",
			Risk = "External optimizer change",
			Safety = "External / High impact",
			CooldownSeconds = 3
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/optimizations/synergy/all",
			Access = "Authenticated + CSRF",
			Risk = "External optimizer change",
			Safety = "External / High impact",
			CooldownSeconds = 10,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/optimizations/synergy/action",
			Access = "Authenticated + CSRF",
			Risk = "External optimizer command",
			Safety = "External",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/animals/status",
			Access = "Authenticated read",
			Risk = "Animal Limiter runtime status",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/animals/rules",
			Access = "Authenticated read",
			Risk = "Animal Limiter rule configuration read",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/animals/rules/set",
			Access = "Authenticated + CSRF",
			Risk = "Creates or updates an Animal Limiter rule",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/animals/rules/remove",
			Access = "Authenticated + CSRF",
			Risk = "Removes an Animal Limiter rule",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/animals/exclusions/add",
			Access = "Authenticated + CSRF",
			Risk = "Adds an Animal Limiter exclusion",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/animals/exclusions/remove",
			Access = "Authenticated + CSRF",
			Risk = "Removes an Animal Limiter exclusion",
			Safety = "Admin / High impact",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/animals/recommendations/start",
			Access = "Authenticated + CSRF",
			Risk = "Manual loaded-entity recommendation scan",
			Safety = "Moderate",
			CooldownSeconds = 10,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/animals/recommendations",
			Access = "Authenticated read",
			Risk = "Animal Limiter recommendation results",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/blocklogger/nearby",
			Access = "Authenticated read",
			Risk = "Sensitive nearby audit lookup",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/blocklogger/player",
			Access = "Authenticated read",
			Risk = "Sensitive player audit lookup",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/blocklogger/maintenance",
			Access = "Authenticated read",
			Risk = "BlockLogger storage policy/status",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/blocklogger/maintenance/config",
			Access = "Administrator/Owner + CSRF",
			Risk = "Changes BlockLogger retention and backup policy",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/blocklogger/purge",
			Access = "Administrator/Owner + CSRF",
			Risk = "Deletes selected historical BlockLogger database rows and may compact SQLite",
			Safety = "High",
			CooldownSeconds = 10,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/health/admin-database-backup",
			Access = "Authenticated + CSRF",
			Risk = "Admin database backup",
			Safety = "Moderate",
			CooldownSeconds = 15
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/health/admin-database-integrity",
			Access = "Authenticated + CSRF",
			Risk = "Admin database integrity check",
			Safety = "Moderate",
			CooldownSeconds = 30
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/health/database-backup",
			Access = "Authenticated + CSRF",
			Risk = "BlockLogger database backup",
			Safety = "Moderate",
			CooldownSeconds = 15
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/loginprotection/status",
			Access = "Authenticated read",
			Risk = "Login Protection runtime status",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/operations/alert-remove",
			Access = "Authenticated + CSRF",
			Risk = "Removes an operations alert",
			Safety = "Safe",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/optimizations/experiment/evaluate",
			Access = "Authenticated + CSRF",
			Risk = "Optimization experiment evaluation",
			Safety = "Moderate",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/optimizations/synergy",
			Access = "Authenticated read",
			Risk = "External optimizer status",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/optimizations/tungsten",
			Access = "Authenticated read",
			Risk = "External optimizer status",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/block-activity",
			Access = "Authenticated read",
			Risk = "Read-only BlockLogger activity aggregation",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/entity-growth",
			Access = "Authenticated read",
			Risk = "Read-only entity growth analysis",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/entity-scan",
			Access = "Authenticated read",
			Risk = "Cached entity scan result",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/environment",
			Access = "Authenticated read",
			Risk = "Read-only environment history",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/incident",
			Access = "Authenticated read",
			Risk = "Read-only incident detail",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/overview",
			Access = "Authenticated read",
			Risk = "Read-only performance overview",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/page-snapshot",
			Access = "Authenticated read",
			Risk = "Cached/coalesced Performance Center page snapshot",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/providers",
			Access = "Authenticated read",
			Risk = "Read-only provider status",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/performance/timeline",
			Access = "Authenticated read",
			Risk = "Read-only performance timeline",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/profiler",
			Access = "Authenticated read",
			Risk = "Read-only profiler state",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/server/mods",
			Access = "Authenticated read",
			Risk = "Loaded mod inventory and profiler attribution",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/server/mods/detail",
			Access = "Authenticated read",
			Risk = "Loaded mod profiler detail",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/custom-patches",
			Access = "Authenticated read",
			Risk = "Reads constrained custom Harmony patch definitions and runtime telemetry",
			Safety = "Moderate"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/custom-patches/save",
			Access = "Owner + CSRF",
			Risk = "Creates or edits a declarative runtime Harmony patch",
			Safety = "High",
			CooldownSeconds = 2,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/custom-patches/action",
			Access = "Owner + CSRF",
			Risk = "Enables, disables, deletes, or reapplies a custom runtime Harmony patch",
			Safety = "High",
			CooldownSeconds = 2,
			ExclusiveHeavyOperation = true
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/custom-patches/generate",
			Access = "Administrator/Owner + CSRF",
			Risk = "Generates a disabled declarative patch template; does not apply code",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/content-patches",
			Access = "Authenticated read",
			Risk = "Reads generated JSON/recipe patch definitions and restart state",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/content-patches/assets",
			Access = "Authenticated read",
			Risk = "Searches loaded recipe, item and block asset names",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/content-patches/asset",
			Access = "Authenticated read",
			Risk = "Inspects one loaded content asset",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "GET",
			Path = "/api/content-patches/export",
			Access = "Authenticated read",
			Risk = "Downloads the managed enabled content patch pack",
			Safety = "Safe"
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/content-patches/build",
			Access = "Owner + CSRF",
			Risk = "Validates and stages a generated content change for next restart",
			Safety = "High",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/content-patches/generate",
			Access = "Administrator/Owner + CSRF",
			Risk = "Generates a disabled JSON/recipe patch template",
			Safety = "Moderate",
			CooldownSeconds = 1
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/content-patches/save",
			Access = "Owner + CSRF",
			Risk = "Writes a managed Vintage Story content-patch mod for next restart",
			Safety = "High",
			CooldownSeconds = 2
		},
		new WebRoutePolicyInfo
		{
			Method = "POST",
			Path = "/api/content-patches/action",
			Access = "Owner + CSRF",
			Risk = "Enables, disables, or deletes generated content patches for next restart",
			Safety = "High",
			CooldownSeconds = 2
		}
	};

	private const int MaxWebEndpointMetricKeys = 128;

	private const int MaxWebSessions = 128;

	private const int MaxFailedLoginSources = 2048;

	private readonly ICoreServerAPI api;

	private readonly BlockLoggerModule module;

	private readonly object stateLock = new object();

	private readonly object authLock = new object();

	private readonly object webAccountLock = new object();

	private readonly object operationsLock = new object();

	private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	private static readonly byte[] DashboardHtmlBytes = Encoding.UTF8.GetBytes(WebDashboardPage.Html);

	private static readonly byte[] DashboardHtmlGzipBytes = CompressDashboardHtml();

	private static readonly byte[] BusyResponseBytes = Encoding.UTF8.GetBytes("Server busy - try again shortly.");

	private static readonly byte[] BusyHttpResponsePacket = Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 32\r\nConnection: close\r\nCache-Control: no-store\r\nRetry-After: 1\r\n\r\nServer busy - try again shortly.");

	private readonly SemaphoreSlim heavyOperationGate = new SemaphoreSlim(1, 1);

	private readonly object hardeningLock = new object();

	private readonly Dictionary<string, FeatureBreakerState> featureBreakers = new Dictionary<string, FeatureBreakerState>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, FeatureOverheadState> featureOverhead = new Dictionary<string, FeatureOverheadState>(StringComparer.OrdinalIgnoreCase);

	private readonly object webMetricsLock = new object();

	private readonly Dictionary<string, WebEndpointMetricsState> webEndpointMetrics = new Dictionary<string, WebEndpointMetricsState>(StringComparer.OrdinalIgnoreCase);

	private readonly Queue<WebRequestDiagnostic> recentWebRequests = new Queue<WebRequestDiagnostic>();

	private long webTotalRequests;

	private long webErrorResponses;

	private long webUnauthorizedResponses;

	private long webRateLimitedResponses;

	private long webTotalResponseBytes;

	private int webActiveRequests;

	private int webActiveWorkers;

	private int webQueuedRequests;

	private int webPeakQueuedRequests;

	private long webRejectedBusyRequests;

	private long webSnapshotCacheHits;

	private long webSnapshotCacheMisses;

	private long webSnapshotRefreshes;

	private static readonly AsyncLocal<WebRequestMetricContext?> requestMetricContext = new AsyncLocal<WebRequestMetricContext>();

	private readonly Dictionary<string, long> operationLastStarted = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private string activeHeavyOperation = "";

	private ServerUtilitiesSelfTest lastSelfTest = new ServerUtilitiesSelfTest();

	private LogIntelligenceService? logIntelligence;

	private readonly object tickDebugLock = new object();

	private bool nativeSlowTickCaptureRunning;

	private bool nativeSlowTickIncidentCapture;

	private int nativeSlowTickCaptureGeneration;

	private int nativeSlowTickThresholdMs = 100;

	private int nativeSlowTickDurationSeconds = 30;

	private long nativeSlowTickStartedUnixMilliseconds;

	private long nativeSlowTickEndsUnixMilliseconds;

	private string nativeSlowTickLogPath = "";

	private long nativeSlowTickLogOffset;

	private string nativeSlowTickLastExcerpt = "";

	private bool nativeSlowTickWasEnabled;

	private int nativeSlowTickPreviousThreshold;

	private string nativeTickDebugLastAction = "";

	private string nativeTickDebugLastOutput = "";

	private long nativeTickDebugLastActionUnixMilliseconds;

	private string nativeTickDebugLastIncidentSummary = "";

	private long tickDebugNetworkSampleUnixMilliseconds;

	private long tickDebugNetworkSentBytes;

	private long tickDebugNetworkReceivedBytes;

	private double tickDebugNetworkSendBytesPerSecond;

	private double tickDebugNetworkReceiveBytesPerSecond;

	private readonly Dictionary<string, WebSession> sessions = new Dictionary<string, WebSession>(StringComparer.Ordinal);

	private readonly Dictionary<string, FailedLoginState> failedLogins = new Dictionary<string, FailedLoginState>(StringComparer.OrdinalIgnoreCase);

	private WebDashboardConfig config = new WebDashboardConfig();

	private SuiteCustomizationConfig suiteCustomization = new SuiteCustomizationConfig();

	private readonly SemaphoreSlim databaseStorageAnalysisGate = new SemaphoreSlim(1, 1);

	private WebAdminAccountStore webAdminAccounts = new WebAdminAccountStore();

	private OptimizationExperimentStore optimizationExperiments = new OptimizationExperimentStore();

	private PerformanceEntityScanSnapshot? lastPerformanceEntityScan;

	private PerformanceCenterStore performanceCenterStore = new PerformanceCenterStore();

	private OperationsCenterStore operationsCenterStore = new OperationsCenterStore();

	private CompatibilityIgnoreStore compatibilityIgnores = new CompatibilityIgnoreStore();

	private long performanceMaintenanceListenerId = -1L;

	private long lastPerformanceMaintenanceUnixMilliseconds;

	private long lastOperationsMaintenanceUnixMilliseconds;

	private long lastPlayerLoadSampleUnixMilliseconds;

	private readonly CoalescedSnapshotCache snapshotCache = new CoalescedSnapshotCache();

	private readonly ConcurrentDictionary<string, AntiCheatApprovalUploadState> antiCheatApprovalUploads = new ConcurrentDictionary<string, AntiCheatApprovalUploadState>(StringComparer.Ordinal);

	private readonly object databaseStorageViewerLock = new object();

	private DatabaseStorageViewerSnapshot? cachedDatabaseStorageViewer;

	private List<PerformanceModIdentity>? environmentModIdentityCache;

	private string environmentModFingerprintCache = "";

	private readonly string runtimeId = Guid.NewGuid().ToString("N");

	private TcpListener? listener;

	private CancellationTokenSource? listenerCts;

	private Task? listenerTask;

	private Channel<QueuedWebClient>? webRequestQueue;

	private List<Task> webWorkerTasks = new List<Task>();

	private string lastError = "";

	private DateTime startedUtc;

	private readonly string localAnonymousCsrfToken = RandomToken(24);

	private bool disposed;

	public WebDashboardConfig Config => config;

	public bool PasswordConfigured => !string.IsNullOrWhiteSpace(config.PasswordSalt) && !string.IsNullOrWhiteSpace(config.PasswordHash);

	public bool LocalAnonymousMode => config.Enabled && IsLoopbackBind(config.BindAddress) && !PasswordConfigured;

	public bool IsRunning
	{
		get
		{
			lock (stateLock)
			{
				return listener != null;
			}
		}
	}

	public int ActiveSessionCount
	{
		get
		{
			lock (authLock)
			{
				PruneSessionsUnsafe();
				return sessions.Count;
			}
		}
	}

	public int PanelAccountCount
	{
		get
		{
			lock (webAccountLock)
			{
				return webAdminAccounts.Accounts?.Count ?? 0;
			}
		}
	}

	public string LastError
	{
		get
		{
			lock (stateLock)
			{
				return lastError;
			}
		}
	}

	public string ListenAddress => $"http://{DisplayBindAddress(config.BindAddress)}:{config.Port}/";

	public string LocalDashboardUrl => $"http://127.0.0.1:{config.Port}/";

	public string PublicDashboardUrl => string.IsNullOrWhiteSpace(config.PublicUrl) ? "" : config.PublicUrl;

	private object BuildRetentionDiagnostics()
	{
		LogStorageService storage = module.Storage;
		return new
		{
			schemaVersion = 1,
			buildId = "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1",
			capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			memory = CaptureMemoryInspectorSnapshot("Diagnostic export"),
			webPanel = snapshotCache.GetDiagnostics(),
			logger = ((storage == null) ? null : new
			{
				pendingEvents = storage.PendingEvents,
				pendingSnapshots = storage.PendingSnapshots,
				pendingEntityAggregates = storage.PendingEntityAggregates,
				capacity = storage.QueueCapacity,
				droppedEvents = storage.DroppedEvents,
				cleanup = "The worker drains bounded queues; shutdown completes waiting queries."
			}),
			admin = module.Admin?.GetRetentionDiagnostics(),
			antiCheat = module.AntiCheat?.GetRetentionDiagnostics(),
			oreShield = module.AntiCheat?.GetOreShieldSnapshot(),
			note = "Counts and sampled payload bytes describe Suite-owned structures. This export contains no tokens, inventory contents or player names. It does not attribute the entire process heap."
		};
	}

	private object? BuildPlayerInvestigation(string player)
	{
		AdminService admin = module.Admin;
		AdminPlayerSummary adminPlayerSummary = admin?.GetPlayer(player);
		if (admin == null || adminPlayerSummary == null)
		{
			return null;
		}
		AntiCheatPlayerWebSnapshot antiCheatPlayerWebSnapshot = module.AntiCheat?.GetInvestigationSnapshot(adminPlayerSummary.PlayerUid);
		return new
		{
			capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			player = adminPlayerSummary,
			antiCheat = ((antiCheatPlayerWebSnapshot == null) ? null : new
			{
				Status = antiCheatPlayerWebSnapshot.Status,
				RiskScore = antiCheatPlayerWebSnapshot.RiskScore,
				RiskFamilies = antiCheatPlayerWebSnapshot.RiskFamilies,
				LastConsequence = antiCheatPlayerWebSnapshot.LastConsequence,
				LastConsequenceUtc = antiCheatPlayerWebSnapshot.LastConsequenceUtc,
				findings = antiCheatPlayerWebSnapshot.Findings.OrderByDescending((AntiCheatFindingWebSnapshot f) => f.ObservedUtc).Take(32).ToArray(),
				Decision = antiCheatPlayerWebSnapshot.Decision
			}),
			riskHistory = module.AntiCheat?.GetHistorySnapshot(adminPlayerSummary.PlayerUid, 32).Incidents,
			warnings = admin.GetWarnings(adminPlayerSummary.PlayerUid).Take(32).ToArray(),
			notes = admin.GetNotes(adminPlayerSummary.PlayerUid).Take(32).ToArray(),
			history = admin.GetHistory(adminPlayerSummary.PlayerUid, 64),
			inventorySnapshots = admin.GetInventorySnapshots(adminPlayerSummary.PlayerUid),
			role = new RoleManagementService(api, () => module.PlayerCommands?.Config, () => module.PlayerList).PreviewPlayer(adminPlayerSummary.PlayerUid, ""),
			detailNote = "Inventory contents and block history load only when requested in their existing tabs."
		};
	}

	private static byte[] CompressDashboardHtml()
	{
		using MemoryStream memoryStream = new MemoryStream();
		using (GZipStream gZipStream = new GZipStream(memoryStream, CompressionLevel.Fastest, leaveOpen: true))
		{
			gZipStream.Write(DashboardHtmlBytes);
		}
		return memoryStream.ToArray();
	}

	internal static bool AcceptsDashboardGzip(string? acceptEncoding)
	{
		bool result = false;
		string[] array = (acceptEncoding ?? "").Split(',');
		foreach (string text in array)
		{
			string[] array2 = text.Split(';');
			string text2 = array2[0].Trim();
			double result2 = 1.0;
			foreach (string item in array2.Skip(1))
			{
				string[] array3 = item.Split('=', 2);
				if (array3.Length == 2 && array3[0].Trim().Equals("q", StringComparison.OrdinalIgnoreCase) && (!double.TryParse(array3[1].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result2) || result2 < 0.0 || result2 > 1.0))
				{
					result2 = 0.0;
				}
			}
			if (text2.Equals("gzip", StringComparison.OrdinalIgnoreCase))
			{
				return result2 > 0.0;
			}
			if (text2 == "*")
			{
				result = result2 > 0.0;
			}
		}
		return result;
	}

	public WebDashboardService(ICoreServerAPI api, BlockLoggerModule module)
	{
		this.api = api;
		this.module = module;
	}

	public BadBlockEntitySnapshot? GetBadBlockEntitySnapshotForMap()
	{
		try
		{
			return logIntelligence?.GetBadBlockEntitySnapshot();
		}
		catch
		{
			return null;
		}
	}

	public void Start()
	{
		LoadConfig();
		LoadSuiteCustomization();
		LoadWebAdminAccounts();
		LoadOptimizationExperiments();
		LoadCompatibilityIgnores();
		if (module.Features.PerformanceCenter)
		{
			LoadPerformanceCenterStore();
		}
		if (module.Features.OperationsCenter)
		{
			LoadOperationsCenterStore();
		}
		if (module.Features.LogIntelligence)
		{
			try
			{
				logIntelligence = new LogIntelligenceService(api, () => module.ServerHealth?.GetSnapshot(120, 300));
				logIntelligence.Start(config.BindAddress, config.Port, config.Enabled);
			}
			catch (Exception ex)
			{
				api.Logger.Warning("[LogIntelligence] startup preflight/log collector disabled after initialization failure: {0}: {1}", ex.GetType().Name, ex.Message);
				try
				{
					logIntelligence?.Dispose();
				}
				catch
				{
				}
				logIntelligence = null;
			}
		}
		else
		{
			api.Logger.Notification("[LogIntelligence] disabled by {0}; no log scan listener or state writer registered.", "NewfiesServerEssentialsFeatures.json");
		}
		if (config.SafeDiagnosticsMode && module.Features.PerformanceDiagnostics)
		{
			module.Config.ProfilerEnabled = true;
			module.SaveRuntimeConfig();
		}
		module.ServerHealth?.SetAutomaticIncidentProfilingEnabled(!config.SafeDiagnosticsMode);
		RegisterCommands();
		if (module.Features.OperationsCenter)
		{
			api.Event.GameWorldSave += OnGameWorldSave;
		}
		if (performanceMaintenanceListenerId < 0 && (module.Features.PerformanceCenter || module.Features.OperationsCenter))
		{
			performanceMaintenanceListenerId = api.Event.RegisterGameTickListener((float _) =>
			{
				if (module.Features.PerformanceCenter)
				{
					RunProtectedFeature("performance-maintenance", "Performance Center Maintenance", "Safe", MaintainPerformanceCenter);
				}
				if (module.Features.OperationsCenter)
				{
					RunProtectedFeature("operations-maintenance", "Operations Center Maintenance", "Safe", MaintainOperationsCenter);
				}
			}, (Exception ex2) =>
			{
				api.Logger.Warning("[WebDashboard] performance/operations maintenance callback failed: {0}: {1}", ex2.GetType().Name, ex2.Message);
			}, 60000);
		}
		if (config.Enabled)
		{
			RestartListener();
		}
		else
		{
			api.Logger.Notification("[WebDashboard] disabled by saved config. Fresh installs default to local loopback access.");
		}
		api.Event.RegisterCallback((float _) =>
		{
			RunStartupSelfTestSafe();
		}, 250);
	}

	private void RunStartupSelfTestSafe()
	{
		if (disposed)
		{
			return;
		}
		try
		{
			lastSelfTest = BuildServerUtilitiesHealth();
			api.Logger.Notification("[ServerEssentials] Startup self-test: {0}. Build={1}", lastSelfTest.Overall, "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1");
			foreach (SelfTestCheck item in lastSelfTest.Checks.Where((SelfTestCheck x) => x.Status != "OK"))
			{
				api.Logger.Warning("[ServerEssentials] Self-test {0}: {1} - {2}", item.Status, item.Name, item.Detail);
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials] deferred startup self-test failed: {0}: {1}", ex.GetType().Name, ex.Message);
		}
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		disposed = true;
		StopListener();
		api.Event.GameWorldSave -= OnGameWorldSave;
		logIntelligence?.Dispose();
		logIntelligence = null;
		if (performanceMaintenanceListenerId >= 0)
		{
			api.Event.UnregisterGameTickListener(performanceMaintenanceListenerId);
			performanceMaintenanceListenerId = -1L;
		}
		try
		{
			if (module.Features.PerformanceCenter)
			{
				MaintainPerformanceCenter();
				SavePerformanceCenterStore();
			}
			if (module.Features.OperationsCenter)
			{
				MaintainOperationsCenter();
				SaveOperationsCenterStore();
			}
		}
		catch
		{
		}
		string[] array = antiCheatApprovalUploads.Keys.ToArray();
		foreach (string uploadId in array)
		{
			RemoveAntiCheatApprovalUpload(uploadId);
		}
		lock (authLock)
		{
			sessions.Clear();
			failedLogins.Clear();
		}
	}

	public List<string> GetGuiStatusLines()
	{
		return new List<string>
		{
			"Build: newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1 (VS target 1.22.7)",
			$"Enabled: {config.Enabled}",
			$"Running: {IsRunning}",
			"Bind address: " + config.BindAddress,
			$"Port: {config.Port}",
			"Listen URL: " + ListenAddress,
			"Local GUI URL: " + LocalDashboardUrl,
			"Public GUI URL: " + (string.IsNullOrWhiteSpace(PublicDashboardUrl) ? "(not configured)" : PublicDashboardUrl),
			"Admin username: " + config.AdminUsername,
			$"Password configured: {PasswordConfigured}",
			"Authentication mode: " + (LocalAnonymousMode ? "LOCAL LOOPBACK / NO LOGIN" : (PasswordConfigured ? "PASSWORD LOGIN" : "PASSWORD REQUIRED FOR REMOTE BIND")),
			$"Additional panel accounts: {PanelAccountCount}",
			$"Active web sessions: {ActiveSessionCount}",
			$"Web workers: {config.WebWorkerCount}",
			$"Web request queue: {Math.Max(0, Volatile.Read(in webQueuedRequests))}/{config.WebRequestQueueCapacity} (max wait {config.WebQueueMaxWaitMs}ms)",
			$"Session timeout: {config.SessionTimeoutMinutes} minutes",
			$"Secure cookies: {config.SecureCookies}",
			"Allowed networks: " + ((config.AllowedNetworks.Count == 0) ? "any" : string.Join(", ", config.AllowedNetworks)),
			"Last error: " + (string.IsNullOrWhiteSpace(LastError) ? "(none)" : LastError),
			"",
			LocalAnonymousMode ? "Fresh local mode is reachable only from this machine. Set a password before changing to a non-loopback bind." : "Direct HTTP is plaintext. For Internet exposure, place this listener behind HTTPS (Caddy/nginx/Cloudflare Tunnel or equivalent)."
		};
	}

	public void ReloadConfig()
	{
		LoadConfig();
		LoadSuiteCustomization();
		LoadWebAdminAccounts();
		LogoutAllSessions();
		RestartListener();
	}

	public string SetEnabled(bool value)
	{
		config.Enabled = value;
		SaveConfig();
		if (value)
		{
			if (!PasswordConfigured && !IsLoopbackBind(config.BindAddress))
			{
				StopListener();
				return "Web server enabled in config, but a non-loopback bind requires an admin password.";
			}
			RestartListener();
			string result;
			if (IsRunning)
			{
				result = (LocalAnonymousMode ? ("Local web server enabled at " + ListenAddress + " without login (loopback only).") : ("Web server enabled at " + ListenAddress));
			}
			else
			{
				result = "Web server could not start: " + LastError;
			}
			return result;
		}
		StopListener();
		return "Web server disabled.";
	}

	public string SetBindAddress(string? value)
	{
		value = (value ?? "").Trim();
		if (!TryParseBindAddress(value, out IPAddress address))
		{
			return "Invalid bind address. Use a specific IP, 0.0.0.0, ::, localhost, or loopback.";
		}
		if (!IPAddress.IsLoopback(address) && !PasswordConfigured)
		{
			return "Remote/non-loopback binding requires an admin password. Set the web password first.";
		}
		config.BindAddress = value;
		config.Clamp();
		SaveConfig();
		if (config.Enabled)
		{
			RestartListener();
		}
		return (IsRunning || !config.Enabled) ? ("Bind address set to " + config.BindAddress + ".") : ("Bind saved, but listener failed: " + LastError);
	}

	public string SetPort(int value)
	{
		config.Port = Math.Clamp(value, 1, 65535);
		SaveConfig();
		if (config.Enabled)
		{
			RestartListener();
		}
		return (IsRunning || !config.Enabled) ? $"Web port set to {config.Port}." : ("Port saved, but listener failed: " + LastError);
	}

	public string SetPublicUrl(string? value)
	{
		value = (value ?? "").Trim();
		if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
		{
			value = "";
		}
		if (!string.IsNullOrEmpty(value))
		{
			if (!Uri.TryCreate(value, UriKind.Absolute, out Uri result) || (result.Scheme != Uri.UriSchemeHttp && result.Scheme != Uri.UriSchemeHttps))
			{
				return "Public URL must be an absolute http:// or https:// URL, or 'off'.";
			}
			if (value.Length > 512)
			{
				return "Public URL is too long.";
			}
			if (!value.EndsWith("/", StringComparison.Ordinal))
			{
				value += "/";
			}
		}
		config.PublicUrl = value;
		SaveConfig();
		return string.IsNullOrWhiteSpace(config.PublicUrl) ? "Public web URL cleared." : ("Public web URL set to " + config.PublicUrl);
	}

	public string SetUsername(string? value)
	{
		value = (value ?? "").Trim();
		if (string.IsNullOrWhiteSpace(value))
		{
			return "Username cannot be empty.";
		}
		if (value.Length > 64)
		{
			value = value.Substring(0, 64);
		}
		lock (webAccountLock)
		{
			if (FindPanelAccount(value) != null)
			{
				return "That username is already used by an additional panel account.";
			}
		}
		config.AdminUsername = value;
		SaveConfig();
		LogoutAllSessions();
		return "Web admin username set to " + config.AdminUsername + ". Existing sessions were invalidated.";
	}

	public string SetSessionTimeout(int minutes)
	{
		config.SessionTimeoutMinutes = Math.Clamp(minutes, 5, 1440);
		SaveConfig();
		return $"Web session timeout set to {config.SessionTimeoutMinutes} minutes.";
	}

	public string SetSecureCookies(bool value)
	{
		config.SecureCookies = value;
		SaveConfig();
		LogoutAllSessions();
		return value ? "Secure web cookies enabled. Existing sessions were invalidated." : "Secure web cookies disabled. Existing sessions were invalidated.";
	}

	public string SetPassword(string? password)
	{
		if (password == null)
		{
			password = "";
		}
		if (password.Length < 10)
		{
			return "Password must be at least 10 characters.";
		}
		if (password.Length > 256)
		{
			return "Password is too long.";
		}
		SetPasswordHash(password);
		SaveConfig();
		LogoutAllSessions();
		if (config.Enabled)
		{
			RestartListener();
		}
		return "Web admin password updated. Existing sessions were invalidated.";
	}

	public void LogoutAllSessions()
	{
		lock (authLock)
		{
			sessions.Clear();
		}
	}

	private T MeasureFeature<T>(string key, string name, Func<T> action)
	{
		long timestamp = Stopwatch.GetTimestamp();
		try
		{
			return action();
		}
		finally
		{
			RecordFeatureOverhead(key, name, Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
		}
	}

	private void MeasureFeature(string key, string name, Action action)
	{
		long timestamp = Stopwatch.GetTimestamp();
		try
		{
			action();
		}
		finally
		{
			RecordFeatureOverhead(key, name, Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
		}
	}

	private void RecordFeatureOverhead(string key, string name, double milliseconds)
	{
		lock (hardeningLock)
		{
			if (!featureOverhead.TryGetValue(key, out FeatureOverheadState value))
			{
				value = new FeatureOverheadState
				{
					Key = key,
					Name = name
				};
				featureOverhead[key] = value;
			}
			value.Calls++;
			value.LastMilliseconds = milliseconds;
			value.TotalMilliseconds += milliseconds;
			value.MaxMilliseconds = Math.Max(value.MaxMilliseconds, milliseconds);
		}
	}

	private void RunProtectedFeature(string key, string name, string safety, Action action)
	{
		if (IsFeatureCircuitOpen(key, out string _))
		{
			return;
		}
		try
		{
			MeasureFeature(key, name, action);
			RecordFeatureSuccess(key, name, safety);
		}
		catch (Exception ex)
		{
			RecordFeatureFailure(key, ex, name, safety);
			api.Logger.Warning("[ServerEssentials] Nonessential feature {0} failed safely: {1}: {2}", name, ex.GetType().Name, ex.Message);
		}
	}

	private bool TryRunProtectedFeature<T>(string key, string name, string safety, Func<T> action, out T? value, out string error) where T : class
	{
		value = null;
		if (IsFeatureCircuitOpen(key, out error))
		{
			return false;
		}
		try
		{
			value = MeasureFeature(key, name, action);
			RecordFeatureSuccess(key, name, safety);
			error = "";
			return true;
		}
		catch (Exception ex)
		{
			RecordFeatureFailure(key, ex, name, safety);
			error = ex.GetType().Name + ": " + ex.Message;
			return false;
		}
	}

	private void RecordFeatureSuccess(string key, string name, string safety = "Safe")
	{
		lock (hardeningLock)
		{
			if (!featureBreakers.TryGetValue(key, out FeatureBreakerState value))
			{
				value = new FeatureBreakerState
				{
					Key = key,
					Name = name,
					Safety = safety
				};
				featureBreakers[key] = value;
			}
			value.Name = name;
			value.Safety = safety;
			value.ConsecutiveFailures = 0;
			if (value.OpenUntilUnixMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
			{
				value.OpenUntilUnixMilliseconds = 0L;
			}
		}
	}

	private void RecordFeatureFailure(string key, Exception ex, string name, string safety = "Moderate")
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		lock (hardeningLock)
		{
			if (!featureBreakers.TryGetValue(key, out FeatureBreakerState value))
			{
				value = new FeatureBreakerState
				{
					Key = key,
					Name = name,
					Safety = safety
				};
				featureBreakers[key] = value;
			}
			value.Name = name;
			value.Safety = safety;
			value.ConsecutiveFailures++;
			value.TotalFailures++;
			value.LastFailureUnixMilliseconds = num;
			value.LastError = ex.GetType().Name + ": " + ex.Message;
			if (value.ConsecutiveFailures >= 3)
			{
				value.OpenUntilUnixMilliseconds = num + 300000;
				api.Logger.Warning("[ServerEssentials] Circuit breaker OPEN for {0} for 5 minutes after {1} consecutive failures. Core BlockLogger continues running.", name, value.ConsecutiveFailures);
			}
		}
	}

	private bool IsFeatureCircuitOpen(string key, out string message)
	{
		lock (hardeningLock)
		{
			if (!featureBreakers.TryGetValue(key, out FeatureBreakerState value))
			{
				message = "";
				return false;
			}
			long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			if (value.OpenUntilUnixMilliseconds <= num)
			{
				value.OpenUntilUnixMilliseconds = 0L;
				if (value.ConsecutiveFailures >= 3)
				{
					value.ConsecutiveFailures = 0;
				}
				message = "";
				return false;
			}
			message = $"{value.Name} circuit breaker is open until {DateTimeOffset.FromUnixTimeMilliseconds(value.OpenUntilUnixMilliseconds):u}. Last error: {value.LastError}";
			return true;
		}
	}

	private List<FeatureCircuitBreakerStatus> SnapshotCircuitBreakers()
	{
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		lock (hardeningLock)
		{
			return (from x in featureBreakers.Values.OrderBy((FeatureBreakerState x) => x.Name, StringComparer.OrdinalIgnoreCase)
				select new FeatureCircuitBreakerStatus
				{
					Key = x.Key,
					Name = x.Name,
					ConsecutiveFailures = x.ConsecutiveFailures,
					TotalFailures = x.TotalFailures,
					Open = (x.OpenUntilUnixMilliseconds > now),
					OpenUntilUnixMilliseconds = x.OpenUntilUnixMilliseconds,
					LastError = x.LastError,
					LastFailureUnixMilliseconds = x.LastFailureUnixMilliseconds,
					Safety = x.Safety
				}).ToList();
		}
	}

	private List<FeatureOverheadSnapshot> SnapshotOverhead()
	{
		lock (hardeningLock)
		{
			return (from x in featureOverhead.Values
				orderby x.MaxMilliseconds descending
				select new FeatureOverheadSnapshot
				{
					Key = x.Key,
					Name = x.Name,
					Calls = x.Calls,
					LastMilliseconds = x.LastMilliseconds,
					AverageMilliseconds = ((x.Calls <= 0) ? 0.0 : (x.TotalMilliseconds / (double)x.Calls)),
					MaxMilliseconds = x.MaxMilliseconds
				}).ToList();
		}
	}

	private WebRoutePolicyInfo? FindWebRoutePolicy(string method, string path)
	{
		return WebRoutePolicies.FirstOrDefault((WebRoutePolicyInfo x) => string.Equals(x.Method, method, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
	}

	private bool SafeModeBlocksRoute(WebRequest request, WebRoutePolicyInfo? policy)
	{
		if (!config.SafeDiagnosticsMode)
		{
			return false;
		}
		string path = request.Path;
		if (path == "/api/profiler/enabled" && !GetJsonBool(request.Body, "enabled", fallback: false))
		{
			return true;
		}
		switch (path)
		{
		default:
			if (!(path == "/api/optimizations/synergy/all"))
			{
				if (path == "/api/tick-debug/action")
				{
					string text = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
					if (text.StartsWith("dump-", StringComparison.Ordinal))
					{
						return true;
					}
				}
				if (path == "/api/optimizations/tungsten/action" || path == "/api/optimizations/synergy/action")
				{
					bool flag;
					switch (GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant())
					{
					case "status":
					case "stats":
					case "health":
					case "manifest":
					case "diag-dump":
						flag = true;
						break;
					default:
						flag = false;
						break;
					}
					return !flag;
				}
				return false;
			}
			goto case "/api/profiler/start";
		case "/api/profiler/start":
		case "/api/tick-debug/slow-start":
		case "/api/tick-debug/incident":
		case "/api/optimizations/tungsten/test":
		case "/api/optimizations/tungsten/toggle":
		case "/api/optimizations/tungsten/all":
		case "/api/optimizations/synergy/test":
		case "/api/optimizations/synergy/toggle":
			return true;
		}
	}

	private bool TryGetMasterDisabledFeature(string path, out string featureName)
	{
		featureName = "";
		if (!module.Features.BlockLogger && path.StartsWith("/api/blocklogger", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "BlockLogger";
		}
		else if (!module.Features.Administration && path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Administration";
		}
		else if (!module.Features.AntiCheat && path.StartsWith("/api/anticheat", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Anti-Cheat";
		}
		else if (!module.Features.AnimalLimiter && path.StartsWith("/api/animals", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Animal Limiter";
		}
		else if (!module.Features.LoginProtection && path.StartsWith("/api/loginprotection", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Login Protection";
		}
		else if (!module.Features.Announcements && path.StartsWith("/api/announcements", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Announcements";
		}
		else if (!module.Features.Reports && path.StartsWith("/api/reports", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Reports";
		}
		else if (!module.Features.PlayerAnalytics && path.StartsWith("/api/player-analytics", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Player Analytics";
		}
		else if (!module.Features.WebConsole && path.StartsWith("/api/console", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Web Console";
		}
		else if (!module.Features.WebMap && path.StartsWith("/api/webmap", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Web Map";
		}
		else if (!module.Features.ModUpdateChecker && path.StartsWith("/api/mod-updates", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Mod Update Checker";
		}
		else if (!module.Features.LogIntelligence && path.StartsWith("/api/log-intelligence", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Log Intelligence";
		}
		else if (!module.Features.OperationsCenter && path.StartsWith("/api/operations", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Operations Center";
		}
		else if (!module.Features.PerformanceCenter && path.StartsWith("/api/performance", StringComparison.OrdinalIgnoreCase))
		{
			featureName = "Performance Center";
		}
		else if (!module.Features.PerformanceDiagnostics && (path.StartsWith("/api/profiler", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/tick-debug", StringComparison.OrdinalIgnoreCase)))
		{
			featureName = "Performance Diagnostics";
		}
		return featureName.Length > 0;
	}

	private bool TryPrepareRouteOperation(WebRequest request, out RouteOperationLease? lease, out string error)
	{
		lease = null;
		error = "";
		WebRoutePolicyInfo webRoutePolicyInfo = FindWebRoutePolicy(request.Method, request.Path);
		if (SafeModeBlocksRoute(request, webRoutePolicyInfo))
		{
			error = "Safe Diagnostics Mode is enabled. Manual engine/native tick captures and external optimizer changes are locked.";
			return false;
		}
		if (webRoutePolicyInfo == null)
		{
			return true;
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string text = webRoutePolicyInfo.Method + ":" + webRoutePolicyInfo.Path;
		lock (hardeningLock)
		{
			if (webRoutePolicyInfo.CooldownSeconds > 0 && operationLastStarted.TryGetValue(text, out var value))
			{
				long num2 = (long)webRoutePolicyInfo.CooldownSeconds * 1000L - (num - value);
				if (num2 > 0)
				{
					error = $"This action is rate-limited. Try again in {Math.Max(1, (int)Math.Ceiling((double)num2 / 1000.0))} second(s).";
					return false;
				}
			}
		}
		if (webRoutePolicyInfo.ExclusiveHeavyOperation)
		{
			if (!heavyOperationGate.Wait(0))
			{
				lock (hardeningLock)
				{
					error = (string.IsNullOrWhiteSpace(activeHeavyOperation) ? "Another heavy performance operation is already running." : ("Another heavy performance operation is already running: " + activeHeavyOperation + "."));
				}
				return false;
			}
			lock (hardeningLock)
			{
				activeHeavyOperation = webRoutePolicyInfo.Path;
			}
		}
		lock (hardeningLock)
		{
			operationLastStarted[text] = num;
		}
		lease = new RouteOperationLease(this, text, webRoutePolicyInfo.Path, webRoutePolicyInfo.ExclusiveHeavyOperation);
		return true;
	}

	private void FinishRouteOperation(string operationKey, string displayName, bool releaseHeavy, long startedTimestamp)
	{
		RecordFeatureOverhead("route:" + operationKey, "Web " + displayName, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
		if (!releaseHeavy)
		{
			return;
		}
		lock (hardeningLock)
		{
			activeHeavyOperation = "";
		}
		try
		{
			heavyOperationGate.Release();
		}
		catch
		{
		}
	}

	private T LoadModConfigSafe<T>(string filename, Func<T> createDefault, string featureKey) where T : class
	{
		ConfigMigrationService.MigrationProbe probe = ConfigMigrationService.Prepare(api, filename, featureKey);
		try
		{
			T val = SuiteConfigStorage.Load<T>(api, filename);
			if (val != null)
			{
				ConfigMigrationService.ApplyIfNeeded(api, val, probe);
				return val;
			}
			return createDefault();
		}
		catch (ConfigMigrationException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			string text = BackupCorruptModConfig(filename);
			api.Logger.Error("[ServerEssentials] Could not load {0}: {1}: {2}. Corrupt/invalid file backup: {3}", filename, ex2.GetType().Name, ex2.Message, text);
			RecordFeatureFailure(featureKey, ex2, "Safe config recovery");
			return createDefault();
		}
	}

	private string BackupCorruptModConfig(string filename)
	{
		try
		{
			string text = SuiteConfigStorage.GetConfigPath(api, filename);
			if (!File.Exists(text))
			{
				text = SuiteConfigStorage.FindLegacyRootFile(api, filename) ?? "";
			}
			if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
			{
				return "(source file not found; fresh defaults created)";
			}
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
			string extension = Path.GetExtension(filename);
			string text2 = Path.Combine(SuiteConfigStorage.GetBackupRoot(api), $"{fileNameWithoutExtension}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}");
			File.Move(text, text2, overwrite: false);
			return text2;
		}
		catch (Exception ex)
		{
			return $"(backup failed: {ex.GetType().Name}: {ex.Message})";
		}
	}

	private void LoadConfig()
	{
		WebDashboardConfig webDashboardConfig = LoadModConfigSafe("NewfiesWebServer.json", () => new WebDashboardConfig(), "web-config");
		webDashboardConfig.Clamp();
		config = webDashboardConfig;
		if (!string.IsNullOrEmpty(config.Password))
		{
			SetPasswordHash(config.Password);
			config.Password = "";
			api.Logger.Notification("[WebDashboard] consumed the one-time Password field from {0} and replaced it with a PBKDF2 hash.", "NewfiesWebServer.json");
		}
		SaveConfig();
	}

	private void SaveConfig()
	{
		config.Clamp();
		SuiteConfigStorage.Store(api, config, "NewfiesWebServer.json");
	}

	private void LoadSuiteCustomization()
	{
		suiteCustomization = SuiteCustomizationConfig.Load(api);
	}

	private void SaveSuiteCustomization()
	{
		suiteCustomization.Clamp();
		SuiteConfigStorage.Store(api, suiteCustomization, "NewfiesSuiteCustomization.json");
	}

	private void LoadCompatibilityIgnores()
	{
		CompatibilityIgnoreStore compatibilityIgnoreStore = LoadModConfigSafe("NewfiesCompatibilityIgnores.json", () => new CompatibilityIgnoreStore(), "compatibility-ignores");
		CompatibilityIgnoreStore compatibilityIgnoreStore2 = compatibilityIgnoreStore;
		if (compatibilityIgnoreStore2.Ignores == null)
		{
			List<CompatibilityIgnoreRecord> list = (compatibilityIgnoreStore2.Ignores = new List<CompatibilityIgnoreRecord>());
		}
		compatibilityIgnores = compatibilityIgnoreStore;
		SaveCompatibilityIgnores();
	}

	private void SaveCompatibilityIgnores()
	{
		CompatibilityIgnoreStore compatibilityIgnoreStore = compatibilityIgnores;
		if (compatibilityIgnoreStore.Ignores == null)
		{
			List<CompatibilityIgnoreRecord> list = (compatibilityIgnoreStore.Ignores = new List<CompatibilityIgnoreRecord>());
		}
		SuiteConfigStorage.Store(api, compatibilityIgnores, "NewfiesCompatibilityIgnores.json");
	}

	private static string CompatibilityFingerprint(string category, string title, string detail)
	{
		string s = $"{category.Trim()}\n{title.Trim()}\n{detail.Trim()}";
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
	}

	private void LoadWebAdminAccounts()
	{
		WebAdminAccountStore webAdminAccountStore = LoadModConfigSafe("NewfiesWebAdminAccounts.json", () => new WebAdminAccountStore(), "web-admin-accounts");
		WebAdminAccountStore webAdminAccountStore2 = webAdminAccountStore;
		if (webAdminAccountStore2.Accounts == null)
		{
			List<WebAdminAccountRecord> list = (webAdminAccountStore2.Accounts = new List<WebAdminAccountRecord>());
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<WebAdminAccountRecord> list3 = new List<WebAdminAccountRecord>();
		foreach (WebAdminAccountRecord account in webAdminAccountStore.Accounts)
		{
			if (account == null)
			{
				continue;
			}
			account.Username = (account.Username ?? "").Trim();
			if (IsValidPanelUsername(account.Username) && !string.Equals(account.Username, config.AdminUsername, StringComparison.OrdinalIgnoreCase) && hashSet.Add(account.Username))
			{
				account.Id = (string.IsNullOrWhiteSpace(account.Id) ? Guid.NewGuid().ToString("N") : account.Id.Trim());
				account.Role = NormalizePanelRole(account.Role);
				account.PasswordIterations = Math.Clamp(account.PasswordIterations, 100000, 1000000);
				if (account.CreatedUnixMilliseconds <= 0)
				{
					account.CreatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				}
				list3.Add(account);
				if (list3.Count >= 50)
				{
					break;
				}
			}
		}
		webAdminAccountStore.Accounts = list3;
		webAdminAccounts = webAdminAccountStore;
		SaveWebAdminAccounts();
	}

	private void SaveWebAdminAccounts()
	{
		WebAdminAccountStore webAdminAccountStore = webAdminAccounts;
		if (webAdminAccountStore.Accounts == null)
		{
			List<WebAdminAccountRecord> list = (webAdminAccountStore.Accounts = new List<WebAdminAccountRecord>());
		}
		if (webAdminAccounts.Accounts.Count > 50)
		{
			webAdminAccounts.Accounts = webAdminAccounts.Accounts.Take(50).ToList();
		}
		SuiteConfigStorage.Store(api, webAdminAccounts, "NewfiesWebAdminAccounts.json");
	}

	private static bool IsValidPanelUsername(string? username)
	{
		username = (username ?? "").Trim();
		if (username.Length < 3 || username.Length > 64)
		{
			return false;
		}
		string text = username;
		foreach (char c in text)
		{
			bool flag = char.IsLetterOrDigit(c);
			bool flag2 = flag;
			if (!flag2)
			{
				bool flag3 = ((c == '-' || c == '.' || c == '_') ? true : false);
				flag2 = flag3;
			}
			if (!flag2)
			{
				return false;
			}
		}
		return true;
	}

	private static string NormalizePanelRole(string? role)
	{
		return (string.Equals(role, "ReadOnly", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "Read Only", StringComparison.OrdinalIgnoreCase)) ? "ReadOnly" : "Administrator";
	}

	private static void SetAccountPasswordHash(WebAdminAccountRecord account, string password)
	{
		byte[] bytes = RandomNumberGenerator.GetBytes(32);
		byte[] inArray = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), bytes, account.PasswordIterations, HashAlgorithmName.SHA256, 32);
		account.PasswordSalt = Convert.ToBase64String(bytes);
		account.PasswordHash = Convert.ToBase64String(inArray);
		account.LastPasswordChangedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}

	private static bool VerifyAccountPassword(WebAdminAccountRecord account, string password)
	{
		try
		{
			byte[] salt = Convert.FromBase64String(account.PasswordSalt);
			byte[] array = Convert.FromBase64String(account.PasswordHash);
			byte[] array2 = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, account.PasswordIterations, HashAlgorithmName.SHA256, array.Length);
			return CryptographicOperations.FixedTimeEquals(array, array2);
		}
		catch
		{
			return false;
		}
	}

	private WebAdminAccountRecord? FindPanelAccount(string? username)
	{
		return webAdminAccounts.Accounts.FirstOrDefault((WebAdminAccountRecord x) => string.Equals(x.Username, (username ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
	}

	private void LogoutSessionsForUsername(string username)
	{
		lock (authLock)
		{
			foreach (string item in (from x in sessions
				where string.Equals(x.Value.Username, username, StringComparison.OrdinalIgnoreCase)
				select x.Key).ToList())
			{
				sessions.Remove(item);
			}
		}
	}

	private object BuildWebAdminAccountsPayload()
	{
		lock (webAccountLock)
		{
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			var list = (from x in webAdminAccounts.Accounts.OrderBy((WebAdminAccountRecord x) => x.Username, StringComparer.OrdinalIgnoreCase)
				select new
				{
					Id = x.Id,
					Username = x.Username,
					Role = x.Role,
					Enabled = x.Enabled,
					CreatedUnixMilliseconds = x.CreatedUnixMilliseconds,
					LastLoginUnixMilliseconds = x.LastLoginUnixMilliseconds,
					LastLoginRemote = x.LastLoginRemote,
					LastPasswordChangedUnixMilliseconds = x.LastPasswordChangedUnixMilliseconds,
					locked = (x.LockedUntilUnixMilliseconds > now),
					LockedUntilUnixMilliseconds = x.LockedUntilUnixMilliseconds
				}).ToList();
			return new
			{
				owner = new
				{
					username = config.AdminUsername,
					role = "Owner",
					enabled = true,
					passwordConfigured = PasswordConfigured
				},
				accounts = list,
				count = list.Count,
				maximum = 50
			};
		}
	}

	private void LoadOptimizationExperiments()
	{
		optimizationExperiments = LoadModConfigSafe("NewfiesOptimizationExperiments.json", () => new OptimizationExperimentStore(), "optimization-store");
		optimizationExperiments.SchemaVersion = 2;
		OptimizationExperimentStore optimizationExperimentStore = optimizationExperiments;
		if (optimizationExperimentStore.History == null)
		{
			List<OptimizationExperiment> list = (optimizationExperimentStore.History = new List<OptimizationExperiment>());
		}
		if (optimizationExperiments.History.Count > config.MaxOptimizationExperiments)
		{
			optimizationExperiments.History = optimizationExperiments.History.OrderByDescending((OptimizationExperiment x) => x.CreatedUnixMilliseconds).Take(config.MaxOptimizationExperiments).ToList();
		}
		SaveOptimizationExperiments();
	}

	private void SaveOptimizationExperiments()
	{
		OptimizationExperimentStore optimizationExperimentStore = optimizationExperiments;
		if (optimizationExperimentStore.History == null)
		{
			List<OptimizationExperiment> list = (optimizationExperimentStore.History = new List<OptimizationExperiment>());
		}
		if (optimizationExperiments.History.Count > config.MaxOptimizationExperiments)
		{
			optimizationExperiments.History = optimizationExperiments.History.OrderByDescending((OptimizationExperiment x) => x.CreatedUnixMilliseconds).Take(config.MaxOptimizationExperiments).ToList();
		}
		SuiteConfigStorage.Store(api, optimizationExperiments, "NewfiesOptimizationExperiments.json");
	}

	private void LoadPerformanceCenterStore()
	{
		this.performanceCenterStore = LoadModConfigSafe("NewfiesPerformanceCenter.json", () => new PerformanceCenterStore(), "performance-store");
		this.performanceCenterStore.SchemaVersion = 2;
		PerformanceCenterStore performanceCenterStore = this.performanceCenterStore;
		if (performanceCenterStore.Incidents == null)
		{
			List<PerformanceIncidentArchive> list = (performanceCenterStore.Incidents = new List<PerformanceIncidentArchive>());
		}
		performanceCenterStore = this.performanceCenterStore;
		if (performanceCenterStore.EntityScans == null)
		{
			List<PerformanceEntityScanSnapshot> list3 = (performanceCenterStore.EntityScans = new List<PerformanceEntityScanSnapshot>());
		}
		performanceCenterStore = this.performanceCenterStore;
		if (performanceCenterStore.EnvironmentHistory == null)
		{
			List<PerformanceEnvironmentChange> list5 = (performanceCenterStore.EnvironmentHistory = new List<PerformanceEnvironmentChange>());
		}
		lastPerformanceEntityScan = this.performanceCenterStore.EntityScans.OrderByDescending((PerformanceEntityScanSnapshot x) => x.UnixMilliseconds).FirstOrDefault();
		TrimPerformanceCenterStore();
		SavePerformanceCenterStore();
	}

	private void SavePerformanceCenterStore()
	{
		if (module.Features.PerformanceCenter)
		{
			TrimPerformanceCenterStore();
			SuiteConfigStorage.Store(api, performanceCenterStore, "NewfiesPerformanceCenter.json");
		}
	}

	private void TrimPerformanceCenterStore()
	{
		performanceCenterStore.Incidents = (performanceCenterStore.Incidents ?? new List<PerformanceIncidentArchive>()).OrderByDescending((PerformanceIncidentArchive x) => x.StartUnixMilliseconds).Take(config.MaxArchivedIncidents).ToList();
		performanceCenterStore.EntityScans = (performanceCenterStore.EntityScans ?? new List<PerformanceEntityScanSnapshot>()).OrderByDescending((PerformanceEntityScanSnapshot x) => x.UnixMilliseconds).Take(config.MaxEntityScanHistory).ToList();
		performanceCenterStore.EnvironmentHistory = (performanceCenterStore.EnvironmentHistory ?? new List<PerformanceEnvironmentChange>()).OrderByDescending((PerformanceEnvironmentChange x) => x.DetectedUnixMilliseconds).Take(config.MaxEnvironmentChanges).ToList();
	}

	private void LoadOperationsCenterStore()
	{
		this.operationsCenterStore = LoadModConfigSafe("NewfiesOperationsCenter.json", () => new OperationsCenterStore(), "operations-store");
		this.operationsCenterStore.SchemaVersion = 2;
		OperationsCenterStore operationsCenterStore = this.operationsCenterStore;
		if (operationsCenterStore.Alerts == null)
		{
			List<ServerAlertRecord> list = (operationsCenterStore.Alerts = new List<ServerAlertRecord>());
		}
		operationsCenterStore = this.operationsCenterStore;
		if (operationsCenterStore.Actions == null)
		{
			List<ServerActionRecord> list3 = (operationsCenterStore.Actions = new List<ServerActionRecord>());
		}
		operationsCenterStore = this.operationsCenterStore;
		if (operationsCenterStore.Audit == null)
		{
			List<WebAuditRecord> list5 = (operationsCenterStore.Audit = new List<WebAuditRecord>());
		}
		operationsCenterStore = this.operationsCenterStore;
		if (operationsCenterStore.PlayerLoad == null)
		{
			List<PlayerLoadBucket> list7 = (operationsCenterStore.PlayerLoad = new List<PlayerLoadBucket>());
		}
		operationsCenterStore = this.operationsCenterStore;
		if (operationsCenterStore.ConfigSnapshots == null)
		{
			List<ConfigSnapshotRecord> list9 = (operationsCenterStore.ConfigSnapshots = new List<ConfigSnapshotRecord>());
		}
		operationsCenterStore = this.operationsCenterStore;
		if (operationsCenterStore.WorldSaves == null)
		{
			List<WorldSaveObservation> list11 = (operationsCenterStore.WorldSaves = new List<WorldSaveObservation>());
		}
		operationsCenterStore = this.operationsCenterStore;
		if (operationsCenterStore.NetworkSamples == null)
		{
			List<NetworkPerformanceSample> list13 = (operationsCenterStore.NetworkSamples = new List<NetworkPerformanceSample>());
		}
		operationsCenterStore = this.operationsCenterStore;
		if (operationsCenterStore.PerformanceSnapshots == null)
		{
			List<PerformanceSnapshotRecord> list15 = (operationsCenterStore.PerformanceSnapshots = new List<PerformanceSnapshotRecord>());
		}
		operationsCenterStore = this.operationsCenterStore;
		if (operationsCenterStore.MemoryInspectorSnapshots == null)
		{
			List<MemoryInspectorSnapshot> list17 = (operationsCenterStore.MemoryInspectorSnapshots = new List<MemoryInspectorSnapshot>());
		}
		EnsurePlayerLoadBuckets();
		SanitizeLegacyPlayerLoadHistory();
		TrimOperationsCenterStore();
		SaveOperationsCenterStore();
	}

	private void SaveOperationsCenterStore()
	{
		if (!module.Features.OperationsCenter)
		{
			return;
		}
		lock (operationsLock)
		{
			TrimOperationsCenterStore();
			SuiteConfigStorage.Store(api, operationsCenterStore, "NewfiesOperationsCenter.json");
		}
	}

	private void TrimOperationsCenterStore()
	{
		operationsCenterStore.Alerts = (operationsCenterStore.Alerts ?? new List<ServerAlertRecord>()).OrderByDescending((ServerAlertRecord x) => x.LastSeenUnixMilliseconds).Take(config.MaxAlerts).ToList();
		operationsCenterStore.Actions = (operationsCenterStore.Actions ?? new List<ServerActionRecord>()).OrderByDescending((ServerActionRecord x) => x.UpdatedUnixMilliseconds).Take(config.MaxActions).ToList();
		operationsCenterStore.Audit = (operationsCenterStore.Audit ?? new List<WebAuditRecord>()).OrderByDescending((WebAuditRecord x) => x.UnixMilliseconds).Take(config.MaxWebAuditEntries).ToList();
		foreach (WebAuditRecord item in operationsCenterStore.Audit)
		{
			item.Actor = BoundAuditField(item.Actor, 128);
			item.Remote = BoundAuditField(item.Remote, 128);
			item.Action = BoundAuditField(item.Action, 128);
			item.Detail = BoundAuditField(item.Detail, 2048);
			item.Result = BoundAuditField(item.Result, 512);
		}
		operationsCenterStore.ConfigSnapshots = (operationsCenterStore.ConfigSnapshots ?? new List<ConfigSnapshotRecord>()).OrderByDescending((ConfigSnapshotRecord x) => x.UnixMilliseconds).Take(config.MaxConfigSnapshots).ToList();
		operationsCenterStore.WorldSaves = (operationsCenterStore.WorldSaves ?? new List<WorldSaveObservation>()).OrderByDescending((WorldSaveObservation x) => x.UnixMilliseconds).Take(config.MaxWorldSaveObservations).ToList();
		operationsCenterStore.NetworkSamples = (operationsCenterStore.NetworkSamples ?? new List<NetworkPerformanceSample>()).OrderByDescending((NetworkPerformanceSample x) => x.UnixMilliseconds).Take(config.MaxNetworkSamples).ToList();
		operationsCenterStore.PerformanceSnapshots = (operationsCenterStore.PerformanceSnapshots ?? new List<PerformanceSnapshotRecord>()).OrderByDescending((PerformanceSnapshotRecord x) => x.UnixMilliseconds).Take(config.MaxPerformanceSnapshots).ToList();
		operationsCenterStore.MemoryInspectorSnapshots = (operationsCenterStore.MemoryInspectorSnapshots ?? new List<MemoryInspectorSnapshot>()).OrderByDescending((MemoryInspectorSnapshot x) => x.UnixMilliseconds).Take(16).ToList();
	}

	private void EnsurePlayerLoadBuckets()
	{
		if (operationsCenterStore.PlayerLoad.Count <= 0)
		{
			operationsCenterStore.PlayerLoad = new List<PlayerLoadBucket>
			{
				new PlayerLoadBucket
				{
					Label = "0-5",
					MinimumPlayers = 0,
					MaximumPlayers = 5
				},
				new PlayerLoadBucket
				{
					Label = "6-10",
					MinimumPlayers = 6,
					MaximumPlayers = 10
				},
				new PlayerLoadBucket
				{
					Label = "11-15",
					MinimumPlayers = 11,
					MaximumPlayers = 15
				},
				new PlayerLoadBucket
				{
					Label = "16-20",
					MinimumPlayers = 16,
					MaximumPlayers = 20
				},
				new PlayerLoadBucket
				{
					Label = "21-30",
					MinimumPlayers = 21,
					MaximumPlayers = 30
				},
				new PlayerLoadBucket
				{
					Label = "31+",
					MinimumPlayers = 31,
					MaximumPlayers = int.MaxValue
				}
			};
		}
	}

	private void SanitizeLegacyPlayerLoadHistory()
	{
		foreach (PlayerLoadBucket item in operationsCenterStore.PlayerLoad)
		{
			if (item.LegacyLifecycleCleanupApplied)
			{
				continue;
			}
			item.LegacyLifecycleCleanupApplied = true;
			if (item.Samples > 0 && !(item.DelayMaxMs < 30000.0) && !(item.DelayTotalMs <= 0.0) && !(item.DelayMaxMs < item.DelayTotalMs * 0.25))
			{
				long samples = item.Samples;
				double num = ((samples <= 0) ? 0.0 : (item.CpuTotalPercent / (double)samples));
				double delayMaxMs = item.DelayMaxMs;
				item.Samples = Math.Max(0L, item.Samples - 1);
				item.DelayTotalMs = Math.Max(0.0, item.DelayTotalMs - delayMaxMs);
				item.CpuTotalPercent = num * (double)item.Samples;
				if (item.StallSamples > 0)
				{
					item.StallSamples--;
				}
				item.ExcludedLifecycleSamples++;
				item.ExcludedLifecycleMaxDelayMs = Math.Max(item.ExcludedLifecycleMaxDelayMs, delayMaxMs);
				item.DelayMaxMs = 0.0;
			}
		}
	}

	private void RecordWebAudit(string action, string detail, string result, string remote = "", string actor = "")
	{
		if (!module.Features.OperationsCenter)
		{
			return;
		}
		lock (operationsLock)
		{
			operationsCenterStore.Audit.Insert(0, new WebAuditRecord
			{
				UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Actor = (string.IsNullOrWhiteSpace(actor) ? BoundAuditField(config.AdminUsername, 128) : BoundAuditField(actor, 128)),
				Remote = BoundAuditField(remote, 128),
				Action = BoundAuditField(action, 128),
				Detail = BoundAuditField(detail, 2048),
				Result = BoundAuditField(result, 512)
			});
			if (operationsCenterStore.Audit.Count > config.MaxWebAuditEntries)
			{
				operationsCenterStore.Audit.RemoveRange(config.MaxWebAuditEntries, operationsCenterStore.Audit.Count - config.MaxWebAuditEntries);
			}
		}
	}

	internal static string BoundAuditField(string? value, int max)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "";
		}
		string text = ((value.Length > max) ? $" [truncated; originalLength={value.Length}]" : "");
		int num = Math.Min(value.Length, Math.Max(0, max - text.Length));
		StringBuilder stringBuilder = new StringBuilder(max);
		for (int i = 0; i < num; i++)
		{
			stringBuilder.Append(char.IsControl(value[i]) ? ' ' : value[i]);
		}
		stringBuilder.Append(text);
		return stringBuilder.ToString();
	}

	private void OnGameWorldSave()
	{
		if (!module.Features.OperationsCenter)
		{
			return;
		}
		long started = Stopwatch.GetTimestamp();
		long unix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		int players = api.World.AllOnlinePlayers?.Length ?? 0;
		double delay = module.ServerHealth?.Current.MainThreadDelayMs ?? 0.0;
		api.Event.RegisterCallback((float _) =>
		{
			RunProtectedFeature("world-save-observer", "World Save Observer", "Safe", () =>
			{
				double totalMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				string severity = ((totalMilliseconds >= 5000.0) ? "Critical" : ((totalMilliseconds >= 2000.0) ? "High" : ((totalMilliseconds >= 750.0) ? "Watch" : "Info")));
				operationsCenterStore.WorldSaves.Insert(0, new WorldSaveObservation
				{
					UnixMilliseconds = unix,
					ObservedMainThreadWindowMs = totalMilliseconds,
					Players = players,
					DelayAtStartMs = delay,
					Severity = severity,
					Note = "Observed from GameWorldSave until the server main thread serviced the post-save callback. This includes save work plus normal callback/tick scheduling and is not pure disk-write time."
				});
				if (totalMilliseconds >= 2000.0)
				{
					UpsertAlert("world-save-stall", (totalMilliseconds >= 5000.0) ? "Critical" : "High", "WorldSave", $"World save occupied the main thread for about {totalMilliseconds:0}ms", "A real GameWorldSave event preceded the observed main-thread window.", "Open Performance Center and compare this timestamp with lag incidents. If they repeatedly align, investigate world/save storage performance.", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
				}
				SaveOperationsCenterStore();
			});
		}, 1);
	}

	private void SampleServerNetwork(int players)
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long totalSentBytes = api.Server.TotalSentBytes;
		long totalReceivedBytes = api.Server.TotalReceivedBytes;
		NetworkPerformanceSample networkPerformanceSample = operationsCenterStore.NetworkSamples.OrderByDescending((NetworkPerformanceSample x) => x.UnixMilliseconds).FirstOrDefault();
		double num2 = ((networkPerformanceSample == null) ? 0.0 : Math.Max(1.0, (double)(num - networkPerformanceSample.UnixMilliseconds) / 1000.0));
		double sentBytesPerSecond = ((networkPerformanceSample == null) ? 0.0 : ((double)Math.Max(0L, totalSentBytes - networkPerformanceSample.TotalSentBytes) / num2));
		double receivedBytesPerSecond = ((networkPerformanceSample == null) ? 0.0 : ((double)Math.Max(0L, totalReceivedBytes - networkPerformanceSample.TotalReceivedBytes) / num2));
		operationsCenterStore.NetworkSamples.Insert(0, new NetworkPerformanceSample
		{
			UnixMilliseconds = num,
			TotalSentBytes = totalSentBytes,
			TotalReceivedBytes = totalReceivedBytes,
			SentBytesPerSecond = sentBytesPerSecond,
			ReceivedBytesPerSecond = receivedBytesPerSecond,
			Players = players
		});
	}

	private void MaintainOperationsCenter()
	{
		if (!module.Features.OperationsCenter)
		{
			return;
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (lastOperationsMaintenanceUnixMilliseconds > 0 && num - lastOperationsMaintenanceUnixMilliseconds < 55000)
		{
			return;
		}
		lastOperationsMaintenanceUnixMilliseconds = num;
		ServerHealthService serverHealth = module.ServerHealth;
		if (serverHealth == null || !serverHealth.Enabled)
		{
			return;
		}
		ServerHealthSnapshot snapshot = serverHealth.GetSnapshot(300);
		if (snapshot.Samples.Count == 0)
		{
			return;
		}
		EnsurePlayerLoadBuckets();
		ServerHealthSample current = snapshot.Current;
		RunProtectedFeature("network-sampling", "Server Network Sampling", "Safe", () =>
		{
			SampleServerNetwork(current.Players);
		});
		PlayerLoadBucket playerLoadBucket = operationsCenterStore.PlayerLoad.FirstOrDefault((PlayerLoadBucket x) => current.Players >= x.MinimumPlayers && current.Players <= x.MaximumPlayers);
		if (playerLoadBucket != null && current.UnixMilliseconds > lastPlayerLoadSampleUnixMilliseconds)
		{
			lastPlayerLoadSampleUnixMilliseconds = current.UnixMilliseconds;
			bool flag = current.UnixMilliseconds > 0 && Math.Abs(num - current.UnixMilliseconds) <= 10000;
			if (current.MainThreadDelayMs >= 30000.0)
			{
				playerLoadBucket.ExcludedLifecycleSamples++;
				playerLoadBucket.ExcludedLifecycleMaxDelayMs = Math.Max(playerLoadBucket.ExcludedLifecycleMaxDelayMs, current.MainThreadDelayMs);
			}
			else if (flag)
			{
				playerLoadBucket.Samples++;
				playerLoadBucket.DelayTotalMs += current.MainThreadDelayMs;
				playerLoadBucket.CpuTotalPercent += current.CpuPercent;
				playerLoadBucket.DelayMaxMs = Math.Max(playerLoadBucket.DelayMaxMs, current.MainThreadDelayMs);
				playerLoadBucket.CpuMaxPercent = Math.Max(playerLoadBucket.CpuMaxPercent, current.CpuPercent);
				if (current.MainThreadDelayMs >= 100.0)
				{
					playerLoadBucket.StallSamples++;
				}
			}
		}
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		MemoryGcAnalysis memoryGcAnalysis = BuildMemoryGcAnalysis(snapshot);
		if (snapshot.SmartAnalysis.HealthScore < 50)
		{
			UpsertAlert("health-critical", "Critical", "Overall", $"Server health score is {snapshot.SmartAnalysis.HealthScore}/100", snapshot.SmartAnalysis.PrimaryFinding, "Open Performance Center and investigate the highest-confidence Smart Findings.", now);
		}
		if (snapshot.DiagnosticSummary.WorstDelayMs >= 500.0)
		{
			UpsertAlert("delay-500", "Critical", "MainThread", $"Severe main-thread delay reached {snapshot.DiagnosticSummary.WorstDelayMs:0}ms", snapshot.DiagnosticSummary.Summary, "Open the latest lag incident and run a 10s/30s engine profile if the problem repeats.", now);
		}
		else if (snapshot.DiagnosticSummary.StallCount >= 3)
		{
			UpsertAlert("repeat-stalls", "High", "MainThread", $"{snapshot.DiagnosticSummary.StallCount} slow/stall samples were recorded in the rolling diagnostic window", snapshot.DiagnosticSummary.Summary, "Open the Unified Performance Timeline and inspect whether CPU, GC, entities, chunks, or storage moved with the stalls.", now);
		}
		if (snapshot.DiagnosticSummary.BlockLoggerDroppedIncrease > 0)
		{
			UpsertAlert("blocklogger-drops", "Critical", "Storage", "BlockLogger dropped audit events", $"Dropped events increased by {snapshot.DiagnosticSummary.BlockLoggerDroppedIncrease:N0} in the diagnostic window.", "Treat storage/backpressure as urgent. Check writer latency, queue depth, and disk performance before increasing logging load.", now);
		}
		else if (snapshot.DiagnosticSummary.MaxBlockLoggerQueue >= Math.Max(1000, module.Config.MaxQueuedEvents * 3 / 4))
		{
			UpsertAlert("blocklogger-queue", "High", "Storage", "BlockLogger queue pressure is high", $"Queue reached {snapshot.DiagnosticSummary.MaxBlockLoggerQueue:N0}; normal capacity is {module.Config.MaxQueuedEvents:N0} with an emergency reserve of {module.Config.EmergencyOverflowCapacity:N0}.", "Inspect Storage/BlockLogger pressure and disk throughput. The emergency reserve protects short stalls, but sustained pressure can still exhaust it.", now);
		}
		if (memoryGcAnalysis.Available && (memoryGcAnalysis.State == "Investigate" || memoryGcAnalysis.State == "Critical"))
		{
			UpsertAlert("memory-growth", (memoryGcAnalysis.State == "Critical") ? "Critical" : "High", "Memory", memoryGcAnalysis.Finding, $"Heap slope {memoryGcAnalysis.HeapSlopeMbPerMinute:+0.0;-0.0;0} MB/min; estimated post-GC floor growth {memoryGcAnalysis.EstimatedPostGcFloorGrowthMb:+0;-0;0} MB.", memoryGcAnalysis.Recommendation, now);
		}
		List<PerformanceEntityGrowthRow> source = BuildEntityGrowthRows();
		PerformanceEntityGrowthRow performanceEntityGrowthRow = source.FirstOrDefault((PerformanceEntityGrowthRow x) =>
		{
			string severity = x.Severity;
			return (severity == "Critical" || severity == "High") ? true : false;
		});
		if (performanceEntityGrowthRow != null)
		{
			UpsertAlert("entity-growth:" + performanceEntityGrowthRow.Code, performanceEntityGrowthRow.Severity, "Entities", "Possible runaway entity growth: " + performanceEntityGrowthRow.Code, performanceEntityGrowthRow.Reason, "Open Performance Center, inspect the entity type and hottest chunks, then check spawn/config rules before culling.", now);
		}
		PerformanceEnvironmentChange performanceEnvironmentChange = performanceCenterStore.EnvironmentHistory.OrderByDescending((PerformanceEnvironmentChange x) => x.DetectedUnixMilliseconds).FirstOrDefault((PerformanceEnvironmentChange x) => x.Result == "Regressed");
		if (performanceEnvironmentChange != null && performanceEnvironmentChange.DetectedUnixMilliseconds >= now - 604800000)
		{
			UpsertAlert("environment-regression:" + performanceEnvironmentChange.AfterFingerprint, "High", "Compatibility", "Performance regressed after a mod/version environment change", string.Join(", ", performanceEnvironmentChange.ChangedMods.Take(6)) + $" | regression score {performanceEnvironmentChange.RegressionScore:+0.0;-0.0;0.0}%", "Review the changed mods, Harmony overlap report, and manual profiler attribution before reverting or disabling anything.", now);
		}
		foreach (ServerAlertRecord item in operationsCenterStore.Alerts.Where((ServerAlertRecord x) => x.Status == "Active" && now - x.LastSeenUnixMilliseconds > 300000))
		{
			item.Status = "Resolved";
		}
		RebuildActionQueue(snapshot, memoryGcAnalysis);
		SaveOperationsCenterStore();
	}

	private void UpsertAlert(string key, string severity, string category, string title, string evidence, string recommendedAction, long now)
	{
		ServerAlertRecord serverAlertRecord = operationsCenterStore.Alerts.FirstOrDefault((ServerAlertRecord x) => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
		bool flag = false;
		if (serverAlertRecord == null)
		{
			serverAlertRecord = new ServerAlertRecord
			{
				Id = Guid.NewGuid().ToString("N"),
				Key = key,
				FirstSeenUnixMilliseconds = now,
				LastSeenUnixMilliseconds = now,
				Severity = severity,
				Category = category,
				Title = title,
				Evidence = evidence,
				RecommendedAction = recommendedAction,
				Status = "Active",
				Occurrences = 1
			};
			operationsCenterStore.Alerts.Insert(0, serverAlertRecord);
			bool flag2 = ((severity == "Critical" || severity == "High") ? true : false);
			flag = flag2;
		}
		else
		{
			bool flag3 = serverAlertRecord.Status == "Resolved";
			serverAlertRecord.LastSeenUnixMilliseconds = now;
			serverAlertRecord.Severity = severity;
			serverAlertRecord.Category = category;
			serverAlertRecord.Title = title;
			serverAlertRecord.Evidence = evidence;
			serverAlertRecord.RecommendedAction = recommendedAction;
			serverAlertRecord.Occurrences++;
			if (flag3)
			{
				serverAlertRecord.Status = "Active";
				bool flag2 = ((severity == "Critical" || severity == "High") ? true : false);
				flag = flag2;
			}
		}
		if (flag)
		{
			NotifyAdminAlert(serverAlertRecord);
		}
	}

	private void NotifyAdminAlert(ServerAlertRecord alert)
	{
		api.Logger.Warning("[ServerEssentials Alert] {0} / {1}: {2} | {3}", alert.Severity, alert.Category, alert.Title, alert.Evidence);
		foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
		{
			if (item.HasPrivilege("controlserver"))
			{
				api.SendMessage(item, GlobalConstants.InfoLogChatGroup, "[Server Essentials] " + alert.Severity + ": " + alert.Title, EnumChatType.Notification);
			}
		}
	}

	private void RebuildActionQueue(ServerHealthSnapshot snapshot, MemoryGcAnalysis memory)
	{
		long updatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		List<ServerActionRecord> list = new List<ServerActionRecord>();
		foreach (ServerAlertRecord item in (from x in operationsCenterStore.Alerts.Where((ServerAlertRecord x) =>
			{
				string status = x.Status;
				return (status == "Active" || status == "Investigating") ? true : false;
			})
			orderby SeverityRankForOperations(x.Severity) descending
			select x).Take(20))
		{
			List<ServerActionRecord> list2 = list;
			ServerActionRecord serverActionRecord = new ServerActionRecord();
			serverActionRecord.Id = "alert:" + item.Id;
			serverActionRecord.SourceKey = item.Key;
			serverActionRecord.UpdatedUnixMilliseconds = updatedUnixMilliseconds;
			serverActionRecord.Priority = ((item.Severity == "Critical") ? "High" : item.Severity);
			serverActionRecord.Category = item.Category;
			serverActionRecord.Title = item.RecommendedAction;
			serverActionRecord.Reason = item.Title + " — " + item.Evidence;
			ServerActionRecord serverActionRecord2 = serverActionRecord;
			string category = item.Category;
			if (1 == 0)
			{
			}
			string targetSection = category switch
			{
				"Memory" => "memory", 
				"Entities" => "performance", 
				"Compatibility" => "performance", 
				"Storage" => "performance", 
				"WorldSave" => "actionCenter", 
				"Network" => "actionCenter", 
				_ => "profiler", 
			};
			if (1 == 0)
			{
			}
			serverActionRecord2.TargetSection = targetSection;
			serverActionRecord.Status = "Open";
			list2.Add(serverActionRecord);
		}
		foreach (ServerSmartSignal item2 in snapshot.SmartAnalysis.Signals.Take(8))
		{
			string text = "smart:" + item2.Category + ":" + item2.Title;
			List<ServerActionRecord> list3 = list;
			ServerActionRecord serverActionRecord3 = new ServerActionRecord();
			serverActionRecord3.Id = text;
			serverActionRecord3.SourceKey = text;
			serverActionRecord3.UpdatedUnixMilliseconds = updatedUnixMilliseconds;
			ServerActionRecord serverActionRecord4 = serverActionRecord3;
			string targetSection = item2.Severity;
			bool flag = ((targetSection == "Critical" || targetSection == "High") ? true : false);
			serverActionRecord4.Priority = (flag ? "High" : "Medium");
			serverActionRecord3.Category = item2.Category;
			serverActionRecord3.Title = item2.NextAction;
			serverActionRecord3.Reason = item2.Title + " — " + item2.Evidence;
			ServerActionRecord serverActionRecord5 = serverActionRecord3;
			string category2 = item2.Category;
			if (1 == 0)
			{
			}
			string targetSection2;
			switch (category2)
			{
			case "Memory":
			case "GC":
				targetSection2 = "memory";
				break;
			case "Entities":
			case "Chunks":
				targetSection2 = "performance";
				break;
			case "BlockLoggerIO":
				targetSection2 = "performance";
				break;
			default:
				targetSection2 = "profiler";
				break;
			}
			if (1 == 0)
			{
			}
			serverActionRecord5.TargetSection = targetSection2;
			serverActionRecord3.Status = "Open";
			list3.Add(serverActionRecord3);
		}
		if (memory.Available && memory.HeapSlopeMbPerMinute >= 25.0 && list.All((ServerActionRecord x) => x.SourceKey != "memory-trend"))
		{
			list.Add(new ServerActionRecord
			{
				Id = "memory-trend",
				SourceKey = "memory-trend",
				UpdatedUnixMilliseconds = updatedUnixMilliseconds,
				Priority = ((memory.State == "Critical") ? "High" : "Medium"),
				Category = "Memory",
				Title = "Inspect Memory & GC Center",
				Reason = memory.Finding,
				TargetSection = "memory",
				Status = "Open"
			});
		}
		Dictionary<string, string> dictionary = operationsCenterStore.Actions.ToDictionary((ServerActionRecord x) => x.SourceKey, (ServerActionRecord x) => x.Status, StringComparer.OrdinalIgnoreCase);
		foreach (ServerActionRecord item3 in list)
		{
			if (dictionary.TryGetValue(item3.SourceKey, out var value))
			{
				item3.Status = value;
			}
		}
		operationsCenterStore.Actions = (from g in list.GroupBy((ServerActionRecord x) => x.SourceKey, StringComparer.OrdinalIgnoreCase)
			select g.First() into x
			orderby (x.Priority == "High") ? 3 : ((!(x.Priority == "Medium")) ? 1 : 2) descending
			select x).Take(config.MaxActions).ToList();
	}

	private static int SeverityRankForOperations(string severity)
	{
		if (1 == 0)
		{
		}
		int result = severity switch
		{
			"Critical" => 4, 
			"High" => 3, 
			"Watch" => 2, 
			"Medium" => 2, 
			"Low" => 1, 
			_ => 0, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private MemoryInspectorSnapshot CaptureMemoryInspectorSnapshot(string label, ServerHealthSnapshot? health = null)
	{
		if (health == null)
		{
			health = module.ServerHealth?.GetSnapshot(600);
		}
		ServerHealthSample serverHealthSample = health?.Current ?? new ServerHealthSample();
		using Process process = Process.GetCurrentProcess();
		process.Refresh();
		GCMemoryInfo gCMemoryInfo = GC.GetGCMemoryInfo();
		List<MemoryInspectorTrackedStructure> structures = new List<MemoryInspectorTrackedStructure>();
		lock (operationsLock)
		{
			Add("Operations", "Admin audit records", typeof(WebAuditRecord).FullName ?? "WebAuditRecord", operationsCenterStore.Audit?.Count ?? 0);
			Add("Operations", "Active/retained alerts", typeof(ServerAlertRecord).FullName ?? "ServerAlertRecord", operationsCenterStore.Alerts?.Count ?? 0);
			Add("Operations", "Action-center records", typeof(ServerActionRecord).FullName ?? "ServerActionRecord", operationsCenterStore.Actions?.Count ?? 0);
			Add("Operations", "Performance snapshots", typeof(PerformanceSnapshotRecord).FullName ?? "PerformanceSnapshotRecord", operationsCenterStore.PerformanceSnapshots?.Count ?? 0);
			Add("Operations", "Configuration snapshots", typeof(ConfigSnapshotRecord).FullName ?? "ConfigSnapshotRecord", operationsCenterStore.ConfigSnapshots?.Count ?? 0);
			Add("Operations", "Network samples", typeof(NetworkPerformanceSample).FullName ?? "NetworkPerformanceSample", operationsCenterStore.NetworkSamples?.Count ?? 0);
			Add("Performance", "Archived lag incidents", typeof(PerformanceIncidentArchive).FullName ?? "PerformanceIncidentArchive", performanceCenterStore.Incidents?.Count ?? 0);
			Add("Performance", "Entity scan snapshots", typeof(PerformanceEntityScanSnapshot).FullName ?? "PerformanceEntityScanSnapshot", performanceCenterStore.EntityScans?.Count ?? 0);
			Add("Performance", "Environment changes", typeof(PerformanceEnvironmentChange).FullName ?? "PerformanceEnvironmentChange", performanceCenterStore.EnvironmentHistory?.Count ?? 0);
			Add("Memory", "Saved MemLeakInspector snapshots", typeof(MemoryInspectorSnapshot).FullName ?? "MemoryInspectorSnapshot", operationsCenterStore.MemoryInspectorSnapshots?.Count ?? 0);
		}
		structures.Add(new MemoryInspectorTrackedStructure
		{
			Category = "Runtime",
			Name = "Server Health samples in current view",
			RuntimeType = (typeof(ServerHealthSample).FullName ?? "ServerHealthSample"),
			Count = (health?.Samples?.Count).GetValueOrDefault(),
			Note = "Dashboard diagnostic sample objects, not the full Server Health retention buffer."
		});
		structures.Add(new MemoryInspectorTrackedStructure
		{
			Category = "Runtime",
			Name = "Loaded managed assemblies",
			RuntimeType = (typeof(Assembly).FullName ?? "System.Reflection.Assembly"),
			Count = AppDomain.CurrentDomain.GetAssemblies().LongLength,
			Note = "Assembly count is context only; it does not imply retained instances."
		});
		double num = ((serverHealthSample.ManagedHeapMb > 0.0) ? serverHealthSample.ManagedHeapMb : ((double)GC.GetTotalMemory(forceFullCollection: false) / 1048576.0));
		double gcCommittedMb = ((serverHealthSample.GcCommittedMb > 0.0) ? serverHealthSample.GcCommittedMb : ((double)gCMemoryInfo.TotalCommittedBytes / 1048576.0));
		double gcFragmentedMb = ((serverHealthSample.GcFragmentedMb > 0.0) ? serverHealthSample.GcFragmentedMb : ((double)gCMemoryInfo.FragmentedBytes / 1048576.0));
		double num2 = ((serverHealthSample.PrivateMemoryMb > 0.0) ? serverHealthSample.PrivateMemoryMb : ((double)process.PrivateMemorySize64 / 1048576.0));
		double workingSetMb = ((serverHealthSample.WorkingSetMb > 0.0) ? serverHealthSample.WorkingSetMb : ((double)process.WorkingSet64 / 1048576.0));
		double estimatedNativeMb = ((serverHealthSample.EstimatedNativeMb > 0.0) ? serverHealthSample.EstimatedNativeMb : Math.Max(0.0, num2 - num));
		return new MemoryInspectorSnapshot
		{
			Id = Guid.NewGuid().ToString("N"),
			UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Label = (string.IsNullOrWhiteSpace(label) ? "Memory snapshot" : label.Trim()),
			ManagedHeapMb = num,
			WorkingSetMb = workingSetMb,
			PrivateMemoryMb = num2,
			GcCommittedMb = gcCommittedMb,
			GcFragmentedMb = gcFragmentedMb,
			EstimatedNativeMb = estimatedNativeMb,
			TotalAllocatedMb = (double)GC.GetTotalAllocatedBytes() / 1048576.0,
			Gen0Collections = GC.CollectionCount(0),
			Gen1Collections = GC.CollectionCount(1),
			Gen2Collections = GC.CollectionCount(2),
			Players = serverHealthSample.Players,
			LoadedEntities = serverHealthSample.LoadedEntities,
			LoadedChunks = serverHealthSample.LoadedChunks,
			Structures = (from x in structures
				orderby x.Category, x.Count descending
				select x).ToList()
		};
		void Add(string category, string name, string runtimeType, long count, string note = "")
		{
			structures.Add(new MemoryInspectorTrackedStructure
			{
				Category = category,
				Name = name,
				RuntimeType = runtimeType,
				Count = Math.Max(0L, count),
				Note = note
			});
		}
	}

	private object BuildMemoryInspectorPayload(ServerHealthSnapshot? health)
	{
		List<MemoryInspectorSnapshot> snapshots;
		lock (operationsLock)
		{
			snapshots = (operationsCenterStore.MemoryInspectorSnapshots ?? new List<MemoryInspectorSnapshot>()).OrderByDescending((MemoryInspectorSnapshot x) => x.UnixMilliseconds).Take(16).ToList();
		}
		string value = FindExecutableOnPath("dotnet-gcdump");
		return new
		{
			current = CaptureMemoryInspectorSnapshot("Current", health),
			snapshots = snapshots,
			deepCapture = new
			{
				available = !string.IsNullOrWhiteSpace(value),
				tool = (string.IsNullOrWhiteSpace(value) ? "dotnet-gcdump not found in PATH" : "dotnet-gcdump available"),
				warning = "Deep heap capture can force a full Gen2 GC and pause a large server. Use it only during a controlled diagnostic window.",
				note = "Lightweight snapshots compare process/GC state and Suite-owned tracked structures. A true whole-heap object/type inventory requires an external .gcdump or full dump; MemLeakInspector does not automatically declare a culprit."
			}
		};
	}

	private static string? FindExecutableOnPath(string command)
	{
		try
		{
			string[] array = ((!OperatingSystem.IsWindows()) ? new string[1] { command } : new string[4]
			{
				command + ".exe",
				command + ".cmd",
				command + ".bat",
				command
			});
			string[] array2 = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			foreach (string path in array2)
			{
				string[] array3 = array;
				foreach (string path2 in array3)
				{
					string text = Path.Combine(path, path2);
					if (File.Exists(text))
					{
						return text;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private MemoryGcAnalysis BuildMemoryGcAnalysis(ServerHealthSnapshot snapshot)
	{
		List<ServerHealthSample> rows = snapshot.Samples.OrderBy((ServerHealthSample x) => x.UnixMilliseconds).ToList();
		if (rows.Count < 180)
		{
			return new MemoryGcAnalysis
			{
				Available = false,
				SampleCount = rows.Count,
				State = "Collecting",
				Finding = $"Collecting a stable memory baseline ({rows.Count}/180 samples).",
				Recommendation = "Keep lightweight diagnostics enabled for several minutes."
			};
		}
		double firstMs = rows[0].UnixMilliseconds;
		double meanX = rows.Average((ServerHealthSample x) => ((double)x.UnixMilliseconds - firstMs) / 60000.0);
		double num = CalculateSlope((ServerHealthSample x) => x.ManagedHeapMb);
		double num2 = CalculateSlope((ServerHealthSample x) => x.PrivateMemoryMb);
		double num3 = CalculateSlope((ServerHealthSample x) => x.EstimatedNativeMb);
		List<ServerHealthSample> list = rows;
		int gen0Increase = Math.Max(0, list[list.Count - 1].Gen0Collections - rows[0].Gen0Collections);
		List<ServerHealthSample> list2 = rows;
		int gen1Increase = Math.Max(0, list2[list2.Count - 1].Gen1Collections - rows[0].Gen1Collections);
		List<ServerHealthSample> list3 = rows;
		int num4 = Math.Max(0, list3[list3.Count - 1].Gen2Collections - rows[0].Gen2Collections);
		List<double> list4 = new List<double>();
		for (int num5 = 1; num5 < rows.Count; num5++)
		{
			if (rows[num5].Gen2Collections > rows[num5 - 1].Gen2Collections)
			{
				int num6 = Math.Min(rows.Count, num5 + 4);
				list4.Add(rows.Skip(num5).Take(num6 - num5).Min((ServerHealthSample x) => x.ManagedHeapMb));
			}
		}
		double num7 = ((list4.Count >= 2) ? list4.First() : rows.Take(Math.Min(60, rows.Count)).Min((ServerHealthSample x) => x.ManagedHeapMb));
		double num8 = ((list4.Count >= 2) ? list4.Last() : rows.TakeLast(Math.Min(60, rows.Count)).Min((ServerHealthSample x) => x.ManagedHeapMb));
		double num9 = num8 - num7;
		int num10 = 100;
		if (num >= 100.0)
		{
			num10 -= 35;
		}
		else if (num >= 50.0)
		{
			num10 -= 25;
		}
		else if (num >= 25.0)
		{
			num10 -= 12;
		}
		if (num9 >= 512.0)
		{
			num10 -= 35;
		}
		else if (num9 >= 256.0)
		{
			num10 -= 22;
		}
		else if (num9 >= 128.0)
		{
			num10 -= 10;
		}
		if (num4 >= 6)
		{
			num10 -= 15;
		}
		else if (num4 >= 3)
		{
			num10 -= 8;
		}
		if (num2 >= 250.0)
		{
			num10 -= 35;
		}
		else if (num2 >= 100.0)
		{
			num10 -= 20;
		}
		else if (num2 >= 50.0)
		{
			num10 -= 10;
		}
		if (num3 >= 250.0)
		{
			num10 -= 30;
		}
		else if (num3 >= 100.0)
		{
			num10 -= 18;
		}
		else if (num3 >= 50.0)
		{
			num10 -= 8;
		}
		num10 = Math.Clamp(num10, 0, 100);
		string state;
		if (num10 < 45)
		{
			state = "Critical";
		}
		else if (num10 < 70)
		{
			state = "Investigate";
		}
		else
		{
			state = ((num10 < 88) ? "Watch" : "Healthy");
		}
		string finding;
		string recommendation;
		if (num2 >= 100.0 && num3 >= 75.0 && num < 50.0)
		{
			finding = "Private process memory is rising much faster than the managed heap; this points toward native/engine retention rather than an ordinary C# heap leak.";
			recommendation = "Correlate private/native growth with loaded chunks/entities and SQLite activity. This pattern can come from engine chunk retention, SQLite/native buffers, compression/image buffers, or another unmanaged owner.";
		}
		else if (num9 >= 256.0 && num4 > 0)
		{
			finding = "Possible retained-memory growth: the estimated post-Gen2 heap floor is rising.";
			recommendation = "Check whether the heap repeatedly returns to a higher floor after Gen2 GC. Correlate the rise with entity growth, world generation, and recent mod changes before calling it a leak.";
		}
		else if (num >= 50.0)
		{
			finding = "Managed heap is rising quickly in the current diagnostic window.";
			recommendation = "Watch the next Gen2 collection. If the heap drops near its old baseline, this is allocation pressure rather than strong retained-memory evidence.";
		}
		else
		{
			List<ServerHealthSample> list5 = rows;
			if (list5[list5.Count - 1].GcFragmentedMb >= 512.0)
			{
				List<ServerHealthSample> list6 = rows;
				if (list6[list6.Count - 1].GcCommittedMb > 0.0)
				{
					List<ServerHealthSample> list7 = rows;
					double gcFragmentedMb = list7[list7.Count - 1].GcFragmentedMb;
					List<ServerHealthSample> list8 = rows;
					if (gcFragmentedMb / list8[list8.Count - 1].GcCommittedMb >= 0.2 && num9 < 128.0)
					{
						finding = "The GC reports substantial heap fragmentation without a rising post-Gen2 floor in this window. High committed/process memory alone does not establish a leak.";
						recommendation = "Compare loaded chunks, entities, players and logger queues at similar workloads. Profile large allocations if fragmentation persists; the diagnostic does not force collections or compact the live server heap.";
						goto IL_06c5;
					}
				}
			}
			if (num4 >= 6)
			{
				finding = "Gen2 collections are unusually frequent in the current window.";
				recommendation = "Use the manual engine profiler during a slow period and inspect allocation-heavy/entity/worldgen activity.";
			}
			else
			{
				finding = "Memory behavior does not currently show a strong retained-growth signal.";
				recommendation = "Keep diagnostics enabled during the workload that normally causes lag for a more representative memory trend.";
			}
		}
		goto IL_06c5;
		IL_06c5:
		MemoryGcAnalysis memoryGcAnalysis = new MemoryGcAnalysis
		{
			Available = true,
			SampleCount = rows.Count,
			State = state,
			Score = num10
		};
		List<ServerHealthSample> list9 = rows;
		memoryGcAnalysis.HeapCurrentMb = list9[list9.Count - 1].ManagedHeapMb;
		memoryGcAnalysis.HeapMinimumMb = rows.Min((ServerHealthSample x) => x.ManagedHeapMb);
		memoryGcAnalysis.HeapMaximumMb = rows.Max((ServerHealthSample x) => x.ManagedHeapMb);
		memoryGcAnalysis.HeapSlopeMbPerMinute = num;
		List<ServerHealthSample> list10 = rows;
		memoryGcAnalysis.PrivateCurrentMb = list10[list10.Count - 1].PrivateMemoryMb;
		memoryGcAnalysis.PrivateSlopeMbPerMinute = num2;
		List<ServerHealthSample> list11 = rows;
		memoryGcAnalysis.EstimatedNativeCurrentMb = list11[list11.Count - 1].EstimatedNativeMb;
		memoryGcAnalysis.EstimatedNativeSlopeMbPerMinute = num3;
		List<ServerHealthSample> list12 = rows;
		memoryGcAnalysis.GcCommittedCurrentMb = list12[list12.Count - 1].GcCommittedMb;
		List<ServerHealthSample> list13 = rows;
		memoryGcAnalysis.GcFragmentedCurrentMb = list13[list13.Count - 1].GcFragmentedMb;
		memoryGcAnalysis.Gen0Increase = gen0Increase;
		memoryGcAnalysis.Gen1Increase = gen1Increase;
		memoryGcAnalysis.Gen2Increase = num4;
		memoryGcAnalysis.EstimatedPostGcFloorStartMb = num7;
		memoryGcAnalysis.EstimatedPostGcFloorEndMb = num8;
		memoryGcAnalysis.EstimatedPostGcFloorGrowthMb = num9;
		memoryGcAnalysis.Finding = finding;
		memoryGcAnalysis.Recommendation = recommendation;
		return memoryGcAnalysis;
		double CalculateSlope(System.Func<ServerHealthSample, double> selector)
		{
			double num11 = rows.Average(selector);
			double num12 = 0.0;
			double num13 = 0.0;
			foreach (ServerHealthSample item in rows)
			{
				double num14 = ((double)item.UnixMilliseconds - firstMs) / 60000.0;
				double num15 = selector(item);
				num12 += (num14 - meanX) * (num15 - num11);
				num13 += (num14 - meanX) * (num14 - meanX);
			}
			return (num13 <= 0.0) ? 0.0 : (num12 / num13);
		}
	}

	private List<PerformanceScorecardRow> BuildPerformanceScorecard(ServerHealthSnapshot? snapshot, MemoryGcAnalysis memory)
	{
		if (snapshot == null || !snapshot.Enabled)
		{
			return new List<PerformanceScorecardRow>
			{
				new PerformanceScorecardRow
				{
					Name = "Overall",
					Score = 0,
					State = "Diagnostics off",
					Reason = "Enable lightweight diagnostics to build the server performance scorecard.",
					TargetSection = "actionCenter"
				}
			};
		}
		ServerDiagnosticSummary diagnosticSummary = snapshot.DiagnosticSummary;
		ServerSmartAnalysis smartAnalysis = snapshot.SmartAnalysis;
		int num = 100;
		if (diagnosticSummary.MaxCpuPercent >= 95.0)
		{
			num -= 40;
		}
		else if (diagnosticSummary.MaxCpuPercent >= 90.0)
		{
			num -= 28;
		}
		else if (diagnosticSummary.AverageCpuPercent >= 75.0)
		{
			num -= 20;
		}
		else if (diagnosticSummary.AverageCpuPercent >= 60.0)
		{
			num -= 10;
		}
		int num2 = 100;
		if (diagnosticSummary.WorstDelayMs >= 500.0)
		{
			num2 -= 55;
		}
		else if (diagnosticSummary.WorstDelayMs >= 250.0)
		{
			num2 -= 38;
		}
		else if (diagnosticSummary.WorstDelayMs >= 100.0)
		{
			num2 -= 24;
		}
		else if (diagnosticSummary.WorstDelayMs >= 50.0)
		{
			num2 -= 10;
		}
		num2 -= Math.Min(20, diagnosticSummary.StallCount * 3);
		int num3 = 100;
		ServerSmartSignal serverSmartSignal = (from x in smartAnalysis.Signals
			where x.Category == "Entities"
			orderby x.Score descending
			select x).FirstOrDefault();
		if (serverSmartSignal != null)
		{
			num3 -= Math.Clamp(serverSmartSignal.Score / 2, 10, 45);
		}
		if (smartAnalysis.RecentEntityMedian >= 20000.0)
		{
			num3 -= 25;
		}
		else if (smartAnalysis.RecentEntityMedian >= 12000.0)
		{
			num3 -= 12;
		}
		int num4 = 100;
		ServerSmartSignal serverSmartSignal2 = (from x in smartAnalysis.Signals
			where x.Category == "Chunks"
			orderby x.Score descending
			select x).FirstOrDefault();
		if (serverSmartSignal2 != null)
		{
			num4 -= Math.Clamp(serverSmartSignal2.Score / 2, 10, 40);
		}
		int num5 = 100;
		if (diagnosticSummary.BlockLoggerDroppedIncrease > 0)
		{
			num5 -= 55;
		}
		if (diagnosticSummary.MaxBlockLoggerQueue >= module.Config.MaxQueuedEvents * 3 / 4)
		{
			num5 -= 25;
		}
		else if (diagnosticSummary.MaxBlockLoggerQueue >= 1000)
		{
			num5 -= 12;
		}
		if (diagnosticSummary.MaxBlockLoggerWriterMs >= 250)
		{
			num5 -= 25;
		}
		else if (diagnosticSummary.MaxBlockLoggerWriterMs >= 100)
		{
			num5 -= 12;
		}
		int healthScore = smartAnalysis.HealthScore;
		List<PerformanceScorecardRow> rows = new List<PerformanceScorecardRow>();
		Add("Overall", healthScore, smartAnalysis.PrimaryFinding, "performance");
		Add("CPU", num, $"Rolling avg/max CPU {diagnosticSummary.AverageCpuPercent:0}% / {diagnosticSummary.MaxCpuPercent:0}%.", "profiler");
		Add("Main Thread", num2, $"Worst delay {diagnosticSummary.WorstDelayMs:0}ms; {diagnosticSummary.StallCount} stall sample(s).", "profiler");
		Add("Memory", memory.Available ? memory.Score : 100, memory.Finding, "memory");
		Add("Entities", num3, serverSmartSignal?.Evidence ?? $"Recent entity median {smartAnalysis.RecentEntityMedian:N0}.", "performance");
		Add("Chunks", num4, serverSmartSignal2?.Evidence ?? $"Loaded chunk max {diagnosticSummary.MaxLoadedChunks:N0}.", "performance");
		Add("Storage / BlockLogger", num5, $"Max queue {diagnosticSummary.MaxBlockLoggerQueue:N0}; writer {diagnosticSummary.MaxBlockLoggerWriterMs:N0}ms; drops +{diagnosticSummary.BlockLoggerDroppedIncrease:N0}.", "performance");
		return rows;
		void Add(string name, int score, string reason, string target)
		{
			score = ScoreState(score, out var state);
			rows.Add(new PerformanceScorecardRow
			{
				Name = name,
				Score = score,
				State = state,
				Reason = reason,
				TargetSection = target
			});
		}
		static int ScoreState(int score, out string state)
		{
			score = Math.Clamp(score, 0, 100);
			state = "";
		ref string reference = ref state;
			string text;
			if (score < 50)
			{
				text = "Critical";
			}
			else if (score < 70)
			{
				text = "Investigate";
			}
			else
			{
				text = ((score < 88) ? "Watch" : "Healthy");
			}
			reference = text;
			return score;
		}
	}

	private object BuildPlayerLoadAnalysis()
	{
		EnsurePlayerLoadBuckets();
		var list = operationsCenterStore.PlayerLoad.Select((PlayerLoadBucket x) => new
		{
			Label = x.Label,
			MinimumPlayers = x.MinimumPlayers,
			MaximumPlayers = x.MaximumPlayers,
			Samples = x.Samples,
			averageDelayMs = ((x.Samples <= 0) ? 0.0 : (x.DelayTotalMs / (double)x.Samples)),
			averageCpuPercent = ((x.Samples <= 0) ? 0.0 : (x.CpuTotalPercent / (double)x.Samples)),
			DelayMaxMs = x.DelayMaxMs,
			CpuMaxPercent = x.CpuMaxPercent,
			StallSamples = x.StallSamples,
			ExcludedLifecycleSamples = x.ExcludedLifecycleSamples,
			ExcludedLifecycleMaxDelayMs = x.ExcludedLifecycleMaxDelayMs,
			stallPercent = ((x.Samples <= 0) ? 0.0 : ((double)x.StallSamples * 100.0 / (double)x.Samples))
		}).ToList();
		return new
		{
			rows = list,
			comfortableRange = ((from x in list
				where x.Samples >= 10 && x.averageDelayMs < 50.0 && x.stallPercent < 10.0
				orderby x.MaximumPlayers descending
				select x).FirstOrDefault()?.Label ?? "Insufficient history"),
			excludedLifecycleSamples = list.Sum(x => x.ExcludedLifecycleSamples),
			excludedLifecycleMaxDelayMs = ((list.Count == 0) ? 0.0 : list.Max(x => x.ExcludedLifecycleMaxDelayMs)),
			note = "This models observed server behavior by player-count bucket. Startup/stale sampler discontinuities of 30 seconds or more are excluded from this workload model but remain visible in Server Health diagnostics. It is not a hard player limit."
		};
	}

	private object BuildInstrumentationStatus()
	{
		List<WorldSaveObservation> list = operationsCenterStore.WorldSaves.OrderByDescending((WorldSaveObservation x) => x.UnixMilliseconds).Take(20).ToList();
		NetworkPerformanceSample networkPerformanceSample = operationsCenterStore.NetworkSamples.OrderByDescending((NetworkPerformanceSample x) => x.UnixMilliseconds).FirstOrDefault();
		return new
		{
			worldSave = new
			{
				status = ((list.Count > 0) ? "Measured" : "Waiting for next world save"),
				exactTiming = true,
				measuredNow = (list.Count > 0),
				observationCount = list.Count,
				lastObservedMs = (list.FirstOrDefault()?.ObservedMainThreadWindowMs ?? 0.0),
				averageObservedMs = ((list.Count == 0) ? 0.0 : list.Average((WorldSaveObservation x) => x.ObservedMainThreadWindowMs)),
				worstObservedMs = ((list.Count == 0) ? 0.0 : list.Max((WorldSaveObservation x) => x.ObservedMainThreadWindowMs)),
				latest = list.FirstOrDefault(),
				summary = ((list.Count == 0) ? "GameWorldSave timing is hooked. Waiting for the next save event." : $"Last observed save window {list[0].ObservedMainThreadWindowMs:0}ms; worst of the retained recent set {list.Max((WorldSaveObservation x) => x.ObservedMainThreadWindowMs):0}ms."),
				next = "The timing is the observed main-thread window from GameWorldSave until a post-save callback runs. It is stronger than correlation alone, but still includes normal callback/tick scheduling rather than pure disk-write time."
			},
			blockEntities = new
			{
				status = "Activity proxy available",
				exactTiming = false,
				measuredNow = false,
				summary = "BlockLogger can rank busy block/container coordinates by recent activity. That is useful triage evidence, not block-entity CPU time.",
				next = "Add exact block-entity tick instrumentation before displaying per-instance milliseconds."
			},
			network = new
			{
				status = ((networkPerformanceSample == null) ? "Collecting" : "Measured"),
				exactBandwidth = true,
				measuredNow = (networkPerformanceSample != null),
				sentBytesPerSecond = (networkPerformanceSample?.SentBytesPerSecond ?? 0.0),
				receivedBytesPerSecond = (networkPerformanceSample?.ReceivedBytesPerSecond ?? 0.0),
				totalSentBytes = (networkPerformanceSample?.TotalSentBytes ?? api.Server.TotalSentBytes),
				totalReceivedBytes = (networkPerformanceSample?.TotalReceivedBytes ?? api.Server.TotalReceivedBytes),
				players = (networkPerformanceSample?.Players ?? api.World.AllOnlinePlayers?.Length ?? 0),
				summary = ((networkPerformanceSample == null) ? "Server-wide network byte counters are available; waiting for two maintenance samples to calculate rates." : $"Server-wide rate: {FormatBytesPerSecond(networkPerformanceSample.SentBytesPerSecond)} out / {FormatBytesPerSecond(networkPerformanceSample.ReceivedBytesPerSecond)} in."),
				next = "These are real server totals from IServerAPI. Per-mod/per-packet attribution is not claimed because the public API does not expose a global packet-cost event."
			},
			optimizationSafety = new
			{
				status = "Provider-safe",
				newfiesBuiltInDeepOptimizations = false,
				tungstenOptional = true,
				measurement = true,
				summary = "Optimization changes are recommendation/measurement driven. Tungsten remains optional; one measured experiment is allowed at a time to avoid mixed attribution."
			}
		};
	}

	private object BuildServerStatusSnapshot(int days)
	{
		PlayerCountAnalyticsSnapshot playerCountAnalyticsSnapshot = module.PlayerAnalytics?.GetSnapshot(days) ?? new PlayerCountAnalyticsSnapshot();
		PlayerCountAnalyticsSnapshot playerCountAnalyticsSnapshot2 = ((days >= 30) ? playerCountAnalyticsSnapshot : (module.PlayerAnalytics?.GetSnapshot() ?? playerCountAnalyticsSnapshot));
		IReadOnlyList<AdminPlayerSummary> players = module.Admin?.GetPlayers() ?? Array.Empty<AdminPlayerSummary>();
		List<AdminPlayerSummary> list = players.Where((AdminPlayerSummary x) => x.Online).ToList();
		(bool, string, long) tuple = module.Admin?.GetMaintenance() ?? (false, "", 0L);
		(long, string) tuple2 = module.Admin?.GetShutdown() ?? (0L, "");
		NetworkPerformanceSample networkPerformanceSample = operationsCenterStore.NetworkSamples.OrderByDescending((NetworkPerformanceSample x) => x.UnixMilliseconds).FirstOrDefault();
		PlayerReportSnapshot playerReportSnapshot = module.Reports?.GetSnapshot("all", 1);
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		int newPlayers24Hours = players.Count((AdminPlayerSummary x) => x.FirstSeenUnixMilliseconds >= now - 86400000);
		int num = players.Count((AdminPlayerSummary x) => x.FirstSeenUnixMilliseconds >= now - 604800000);
		int newPlayers30Days = players.Count((AdminPlayerSummary x) => x.FirstSeenUnixMilliseconds >= now - 2592000000u);
		int num2 = players.Count((AdminPlayerSummary x) => x.FirstSeenUnixMilliseconds >= now - 1209600000 && x.FirstSeenUnixMilliseconds < now - 604800000);
		int activePlayers24Hours = players.Count((AdminPlayerSummary x) => x.Online || x.LastSeenUnixMilliseconds >= now - 86400000);
		int activePlayers7Days = players.Count((AdminPlayerSummary x) => x.Online || x.LastSeenUnixMilliseconds >= now - 604800000);
		int activePlayers30Days = players.Count((AdminPlayerSummary x) => x.Online || x.LastSeenUnixMilliseconds >= now - 2592000000u);
		int returningPlayers7Days = players.Count((AdminPlayerSummary x) => (x.Online || x.LastSeenUnixMilliseconds >= now - 604800000) && x.FirstSeenUnixMilliseconds < now - 604800000);
		List<AdminPlayerSummary> list2 = players.Where((AdminPlayerSummary x) => x.FirstSeenUnixMilliseconds >= now - 3196800000u && x.FirstSeenUnixMilliseconds < now - 604800000).ToList();
		int num3 = list2.Count((AdminPlayerSummary x) => x.LastSeenUnixMilliseconds >= x.FirstSeenUnixMilliseconds + 604800000);
		double sevenDayReturnRate = ((list2.Count == 0) ? 0.0 : ((double)num3 * 100.0 / (double)list2.Count));
		double newPlayerWeekGrowthPercent;
		if (num2 != 0)
		{
			newPlayerWeekGrowthPercent = (double)(num - num2) * 100.0 / (double)num2;
		}
		else
		{
			newPlayerWeekGrowthPercent = ((num > 0) ? 100.0 : 0.0);
		}
		List<PlayerCountDailyPoint> list3 = playerCountAnalyticsSnapshot2.Daily.TakeLast(30).ToList();
		int peakPlayers30Days = ((list3.Count == 0) ? playerCountAnalyticsSnapshot.CurrentPlayers : list3.Max((PlayerCountDailyPoint x) => x.High));
		double averageDailyPeak30Days = ((list3.Count == 0) ? ((double)playerCountAnalyticsSnapshot.CurrentPlayers) : list3.Average((PlayerCountDailyPoint x) => x.High));
		var dailyNewPlayers = (from offset in Enumerable.Range(0, 30)
			select DateTime.Now.Date.AddDays(-(29 - offset))).Select((DateTime day) =>
		{
			long start = new DateTimeOffset(day).ToUnixTimeMilliseconds();
			long end = new DateTimeOffset(day.AddDays(1.0)).ToUnixTimeMilliseconds();
			return new
			{
				date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				count = players.Count((AdminPlayerSummary x) => x.FirstSeenUnixMilliseconds >= start && x.FirstSeenUnixMilliseconds < end)
			};
		}).ToList();
		return new
		{
			generatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			server = new
			{
				uptimeSeconds = api.Server.ServerUptimeSeconds,
				worldPlayTimeSeconds = api.Server.TotalWorldPlayTime,
				onlinePlayers = (api.World.AllOnlinePlayers?.Length ?? list.Count),
				trackedPlayers = players.Count,
				averagePingMilliseconds = ((list.Count == 0) ? 0.0 : list.Average((AdminPlayerSummary x) => x.PingMilliseconds)),
				maxPingMilliseconds = ((list.Count != 0) ? list.Max((AdminPlayerSummary x) => x.PingMilliseconds) : 0),
				loadedMods = api.ModLoader.Mods.Count(),
				totalSentBytes = api.Server.TotalSentBytes,
				totalReceivedBytes = api.Server.TotalReceivedBytes,
				sentBytesPerSecond = (networkPerformanceSample?.SentBytesPerSecond ?? 0.0),
				receivedBytesPerSecond = (networkPerformanceSample?.ReceivedBytesPerSecond ?? 0.0),
				serverTimeZone = TimeZoneInfo.Local.Id
			},
			maintenance = new
			{
				enabled = tuple.Item1,
				reason = tuple.Item2,
				untilUnixMilliseconds = tuple.Item3
			},
			shutdown = new
			{
				atUnixMilliseconds = tuple2.Item1,
				reason = tuple2.Item2
			},
			reports = new
			{
				open = (playerReportSnapshot?.OpenCount ?? 0),
				investigating = (playerReportSnapshot?.InvestigatingCount ?? 0),
				total = (playerReportSnapshot?.TotalCount ?? 0)
			},
			growth = new
			{
				totalKnownPlayers = players.Count,
				newPlayers24Hours = newPlayers24Hours,
				newPlayers7Days = num,
				newPlayers30Days = newPlayers30Days,
				previous7DaysNewPlayers = num2,
				newPlayerWeekGrowthPercent = newPlayerWeekGrowthPercent,
				activePlayers24Hours = activePlayers24Hours,
				activePlayers7Days = activePlayers7Days,
				activePlayers30Days = activePlayers30Days,
				returningPlayers7Days = returningPlayers7Days,
				sevenDayReturnRate = sevenDayReturnRate,
				sevenDayReturnCohort = list2.Count,
				peakPlayers30Days = peakPlayers30Days,
				averageDailyPeak30Days = averageDailyPeak30Days,
				dailyNewPlayers = dailyNewPlayers
			},
			onlinePlayers = list.Select((AdminPlayerSummary x) => new
			{
				name = x.Name,
				nickname = (module.SupporterNickname?.GetSavedNickname(x.PlayerUid) ?? string.Empty),
				playerUid = x.PlayerUid,
				pingMilliseconds = x.PingMilliseconds,
				afk = x.Afk,
				sessionSeconds = x.SessionSeconds,
				role = x.Role
			}).OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList(),
			analytics = playerCountAnalyticsSnapshot
		};
	}

	private void DecorateSupporterNicknames(IEnumerable<AdminPlayerSummary> players)
	{
		SupporterNicknameService supporterNickname = module.SupporterNickname;
		if (supporterNickname == null)
		{
			return;
		}
		foreach (AdminPlayerSummary player in players)
		{
			player.Nickname = supporterNickname.GetSavedNickname(player.PlayerUid);
		}
	}

	private void DecorateSupporterNicknames(PermissionsManagerSnapshot? snapshot)
	{
		if (snapshot?.OnlinePlayers == null)
		{
			return;
		}
		SupporterNicknameService supporterNickname = module.SupporterNickname;
		if (supporterNickname == null)
		{
			return;
		}
		foreach (PermissionPlayerSummary onlinePlayer in snapshot.OnlinePlayers)
		{
			onlinePlayer.Nickname = supporterNickname.GetSavedNickname(onlinePlayer.PlayerUid);
		}
	}

	private static string FormatBytesPerSecond(double value)
	{
		if (!(value >= 1048576.0))
		{
			if (!(value >= 1024.0))
			{
				return $"{value:0} B/s";
			}
			return $"{value / 1024.0:0.0} KB/s";
		}
		return $"{value / 1048576.0:0.00} MB/s";
	}

	private PerformanceSnapshotRecord? CapturePerformanceSnapshot(string label, out string error)
	{
		error = "";
		ServerHealthSnapshot serverHealthSnapshot = module.ServerHealth?.GetSnapshot(300);
		if (serverHealthSnapshot == null || !serverHealthSnapshot.Enabled)
		{
			error = "Enable lightweight diagnostics before capturing a performance snapshot.";
			return null;
		}
		if (!serverHealthSnapshot.SmartAnalysis.Available || serverHealthSnapshot.Samples.Count < 30)
		{
			error = $"Collect at least 30 diagnostic samples before capturing a performance snapshot. Current samples: {serverHealthSnapshot.Samples.Count}.";
			return null;
		}
		PerformanceEnvironmentSnapshot performanceEnvironmentSnapshot = CaptureEnvironmentSnapshot(serverHealthSnapshot);
		PerformanceSnapshotRecord performanceSnapshotRecord = new PerformanceSnapshotRecord
		{
			Id = Guid.NewGuid().ToString("N"),
			UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Label = (string.IsNullOrWhiteSpace(label) ? "Performance snapshot" : label.Trim()),
			Players = serverHealthSnapshot.Current.Players,
			ModFingerprint = performanceEnvironmentSnapshot.Fingerprint,
			Metrics = performanceEnvironmentSnapshot.Metrics
		};
		operationsCenterStore.PerformanceSnapshots.Insert(0, performanceSnapshotRecord);
		SaveOperationsCenterStore();
		return performanceSnapshotRecord;
	}

	private ConfigSnapshotRecord CaptureConfigSnapshot(string label, string actor)
	{
		ConfigSnapshotRecord configSnapshotRecord = new ConfigSnapshotRecord
		{
			Id = Guid.NewGuid().ToString("N"),
			UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Label = (string.IsNullOrWhiteSpace(label) ? "Manual snapshot" : label.Trim()),
			CreatedBy = actor,
			SuiteFeaturesJson = JsonSerializer.Serialize(module.FeatureSettings, jsonOptions),
			BlockLoggerJson = JsonSerializer.Serialize(module.Config, jsonOptions),
			WebDashboardJson = JsonSerializer.Serialize(config, jsonOptions),
			AnimalLimiterJson = ((AnimalLimiterSystem.Current?.Config == null) ? "" : JsonSerializer.Serialize(AnimalLimiterSystem.Current.Config, jsonOptions)),
			LoginProtectionJson = ((LoginProtectionSystem.Current?.Config == null) ? "" : JsonSerializer.Serialize(LoginProtectionSystem.Current.Config, jsonOptions)),
			PublicPlayerListJson = ((module.PlayerList == null) ? "" : JsonSerializer.Serialize(module.PlayerList.Config, jsonOptions))
		};
		operationsCenterStore.ConfigSnapshots.Insert(0, configSnapshotRecord);
		SaveOperationsCenterStore();
		return configSnapshotRecord;
	}

	private string RestoreConfigSnapshot(ConfigSnapshotRecord snapshot)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(snapshot.SuiteFeaturesJson))
			{
				SuiteFeatureConfig suiteFeatureConfig = JsonSerializer.Deserialize<SuiteFeatureConfig>(snapshot.SuiteFeaturesJson, jsonOptions);
				if (suiteFeatureConfig != null)
				{
					foreach (var entry in suiteFeatureConfig.GetEntries())
					{
						module.FeatureSettings.TrySet(entry.Name, entry.Enabled, out string _);
					}
					module.FeatureSettings.Save(api);
				}
			}
			BlockLoggerConfig blockLoggerConfig = JsonSerializer.Deserialize<BlockLoggerConfig>(snapshot.BlockLoggerJson, jsonOptions);
			if (blockLoggerConfig != null)
			{
				blockLoggerConfig.Clamp();
				module.Config.ApplyFrom(blockLoggerConfig);
				module.SaveRuntimeConfig();
			}
			WebDashboardConfig webDashboardConfig = JsonSerializer.Deserialize<WebDashboardConfig>(snapshot.WebDashboardJson, jsonOptions);
			if (webDashboardConfig != null)
			{
				webDashboardConfig.Clamp();
				webDashboardConfig.Password = "";
				config = webDashboardConfig;
				SaveConfig();
			}
			if (!string.IsNullOrWhiteSpace(snapshot.AnimalLimiterJson))
			{
				AnimalLimiterConfig animalLimiterConfig = JsonSerializer.Deserialize<AnimalLimiterConfig>(snapshot.AnimalLimiterJson, jsonOptions);
				if (animalLimiterConfig != null)
				{
					SuiteConfigStorage.Store(api, animalLimiterConfig, "NewfiesAnimalLimiter.json");
				}
			}
			if (!string.IsNullOrWhiteSpace(snapshot.LoginProtectionJson))
			{
				LoginProtectionConfig loginProtectionConfig = JsonSerializer.Deserialize<LoginProtectionConfig>(snapshot.LoginProtectionJson, jsonOptions);
				if (loginProtectionConfig != null)
				{
					loginProtectionConfig.Clamp();
					SuiteConfigStorage.Store(api, loginProtectionConfig, "NewfiesLoginProtection.json");
				}
			}
			if (!string.IsNullOrWhiteSpace(snapshot.PublicPlayerListJson) && module.PlayerList != null)
			{
				PublicPlayerListConfig publicPlayerListConfig = JsonSerializer.Deserialize<PublicPlayerListConfig>(snapshot.PublicPlayerListJson, jsonOptions);
				if (publicPlayerListConfig != null)
				{
					module.PlayerList.ApplyConfig(publicPlayerListConfig);
				}
			}
			RestartListener();
			return "Snapshot restored. BlockLogger/Web settings were applied immediately where supported. Suite feature gates, Animal Limiter, Login Protection, and Public Player List files were restored; restart the server so every master feature gate and runtime hook fully matches the snapshot.";
		}
		catch (Exception ex)
		{
			return "Restore failed: " + ex.GetType().Name + ": " + ex.Message;
		}
	}

	private void SetPasswordHash(string password)
	{
		byte[] bytes = RandomNumberGenerator.GetBytes(32);
		byte[] inArray = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), bytes, config.PasswordIterations, HashAlgorithmName.SHA256, 32);
		config.PasswordSalt = Convert.ToBase64String(bytes);
		config.PasswordHash = Convert.ToBase64String(inArray);
		config.Password = "";
	}

	private bool VerifyPassword(string password)
	{
		try
		{
			byte[] salt = Convert.FromBase64String(config.PasswordSalt);
			byte[] array = Convert.FromBase64String(config.PasswordHash);
			byte[] array2 = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, config.PasswordIterations, HashAlgorithmName.SHA256, array.Length);
			return CryptographicOperations.FixedTimeEquals(array, array2);
		}
		catch
		{
			return false;
		}
	}

	private void RestartListener()
	{
		StopListener();
		if (!config.Enabled)
		{
			return;
		}
		if (!TryParseBindAddress(config.BindAddress, out IPAddress address))
		{
			lock (stateLock)
			{
				lastError = "Invalid bind address '" + config.BindAddress + "'.";
			}
			api.Logger.Warning("[WebDashboard] {0}", LastError);
			return;
		}
		if (!IPAddress.IsLoopback(address) && !PasswordConfigured)
		{
			lock (stateLock)
			{
				lastError = "A non-loopback bind requires an admin password.";
			}
			api.Logger.Warning("[WebDashboard] refusing non-loopback bind {0}:{1} because no admin password is configured.", config.BindAddress, config.Port);
			return;
		}
		TcpListener newListener = null;
		CancellationTokenSource cts = null;
		Channel<QueuedWebClient> queue = null;
		List<Task> list = null;
		try
		{
			newListener = new TcpListener(address, config.Port);
			if (address.AddressFamily == AddressFamily.InterNetworkV6)
			{
				try
				{
					newListener.Server.DualMode = address.Equals(IPAddress.IPv6Any);
				}
				catch
				{
				}
			}
			newListener.Start(128);
			cts = new CancellationTokenSource();
			queue = Channel.CreateBounded<QueuedWebClient>(new BoundedChannelOptions(config.WebRequestQueueCapacity)
			{
				SingleWriter = true,
				SingleReader = false,
				FullMode = BoundedChannelFullMode.Wait,
				AllowSynchronousContinuations = false
			});
			list = new List<Task>(config.WebWorkerCount);
			for (int i = 0; i < config.WebWorkerCount; i++)
			{
				int workerId = i + 1;
				list.Add(Task.Run(() => WebWorkerLoopAsync(workerId, queue.Reader, cts.Token), CancellationToken.None));
			}
			Task task = Task.Run(() => AcceptLoopAsync(newListener, queue.Writer, cts.Token), CancellationToken.None);
			lock (stateLock)
			{
				listener = newListener;
				listenerCts = cts;
				listenerTask = task;
				webRequestQueue = queue;
				webWorkerTasks = list;
				lastError = "";
				startedUtc = DateTime.UtcNow;
			}
			Interlocked.Exchange(ref webQueuedRequests, 0);
			Interlocked.Exchange(ref webActiveWorkers, 0);
			if (LocalAnonymousMode)
			{
				api.Logger.Notification("[WebDashboard] local dashboard listening on {0}:{1} with no login required (loopback only). Workers={2}, queue={3}.", config.BindAddress, config.Port, config.WebWorkerCount, config.WebRequestQueueCapacity);
			}
			else
			{
				api.Logger.Notification("[WebDashboard] authenticated dashboard listening on {0}:{1}. Workers={2}, queue={3}.", config.BindAddress, config.Port, config.WebWorkerCount, config.WebRequestQueueCapacity);
			}
		}
		catch (Exception ex)
		{
			try
			{
				queue?.Writer.TryComplete();
			}
			catch
			{
			}
			try
			{
				cts?.Cancel();
			}
			catch
			{
			}
			try
			{
				newListener?.Stop();
			}
			catch
			{
			}
			ObserveListenerShutdownAsync(null, list ?? new List<Task>(), cts);
			lock (stateLock)
			{
				listener = null;
				listenerCts = null;
				listenerTask = null;
				webRequestQueue = null;
				webWorkerTasks = new List<Task>();
				lastError = ex.GetType().Name + ": " + ex.Message;
			}
			api.Logger.Error("[WebDashboard] failed to listen on {0}:{1}: {2}: {3}", config.BindAddress, config.Port, ex.GetType().Name, ex.Message);
		}
	}

	private void StopListener()
	{
		TcpListener tcpListener;
		CancellationTokenSource cancellationTokenSource;
		Task acceptTask;
		Channel<QueuedWebClient> channel;
		List<Task> workers;
		lock (stateLock)
		{
			tcpListener = listener;
			cancellationTokenSource = listenerCts;
			acceptTask = listenerTask;
			channel = webRequestQueue;
			workers = webWorkerTasks;
			listener = null;
			listenerCts = null;
			listenerTask = null;
			webRequestQueue = null;
			webWorkerTasks = new List<Task>();
		}
		try
		{
			channel?.Writer.TryComplete();
		}
		catch
		{
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
		if (channel != null)
		{
			QueuedWebClient item;
			while (channel.Reader.TryRead(out item))
			{
				try
				{
					item.Client.Dispose();
				}
				catch
				{
				}
			}
		}
		Interlocked.Exchange(ref webQueuedRequests, 0);
		Interlocked.Exchange(ref webActiveWorkers, 0);
		snapshotCache.Clear();
		ObserveListenerShutdownAsync(acceptTask, workers, cancellationTokenSource);
	}

	private static async Task ObserveListenerShutdownAsync(Task? acceptTask, IReadOnlyList<Task> workers, CancellationTokenSource? cts)
	{
		try
		{
			List<Task> tasks = new List<Task>(workers.Count + ((acceptTask != null) ? 1 : 0));
			if (acceptTask != null)
			{
				tasks.Add(acceptTask);
			}
			tasks.AddRange(workers);
			if (tasks.Count > 0)
			{
				await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch
		{
		}
		finally
		{
			try
			{
				cts?.Dispose();
			}
			catch
			{
			}
		}
	}

	private async Task AcceptLoopAsync(TcpListener activeListener, ChannelWriter<QueuedWebClient> writer, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				TcpClient client = null;
				try
				{
					client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					client.NoDelay = true;
					QueuedWebClient queued = new QueuedWebClient(client, Stopwatch.GetTimestamp());
					if (writer.TryWrite(queued))
					{
						client = null;
						int depth = Interlocked.Increment(ref webQueuedRequests);
						UpdatePeakQueueDepth(depth);
					}
					else
					{
						Interlocked.Increment(ref webRejectedBusyRequests);
						TcpClient rejected = client;
						client = null;
						RejectBusyClientFast(rejected);
					}
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
					if (!cancellationToken.IsCancellationRequested)
					{
						api.Logger.Warning("[WebDashboard] accept failed: {0}: {1}", ex3.GetType().Name, ex3.Message);
						await Task.Delay(500, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
			}
		}
		finally
		{
			try
			{
				writer.TryComplete();
			}
			catch
			{
			}
		}
	}

	private async Task WebWorkerLoopAsync(int workerId, ChannelReader<QueuedWebClient> reader, CancellationToken cancellationToken)
	{
		try
		{
			while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
			{
				QueuedWebClient queued;
				while (reader.TryRead(out queued))
				{
					Interlocked.Decrement(ref webQueuedRequests);
					Interlocked.Increment(ref webActiveWorkers);
					double queueWaitMs = Math.Max(0.0, (double)(Stopwatch.GetTimestamp() - queued.AcceptedTimestamp) / (double)Stopwatch.Frequency * 1000.0);
					try
					{
						if (!(queueWaitMs > (double)config.WebQueueMaxWaitMs))
						{
							await HandleClientAsync(queued.Client, cancellationToken, queued.AcceptedTimestamp, queueWaitMs).ConfigureAwait(continueOnCapturedContext: false);
							continue;
						}
						Interlocked.Increment(ref webRejectedBusyRequests);
						using CancellationTokenSource busyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
						busyCts.CancelAfter(TimeSpan.FromSeconds(1L));
						await WriteResponseAsync(queued.Client.GetStream(), 503, "text/plain; charset=utf-8", BusyResponseBytes, busyCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (OperationCanceledException)
					{
					}
					catch (Exception ex2)
					{
						Exception ex3 = ex2;
						if (!(ex3 is TimeoutException) || !(module.WebMap?.IsTerrainExportInProgress ?? false))
						{
							api.Logger.Warning("[WebDashboard] worker {0} request failed: {1}: {2}", workerId, ex3.GetType().Name, ex3.Message);
						}
					}
					finally
					{
						try
						{
							queued.Client.Dispose();
						}
						catch
						{
						}
						Interlocked.Decrement(ref webActiveWorkers);
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private static void RejectBusyClientFast(TcpClient client)
	{
		try
		{
			client.SendTimeout = 100;
			NetworkStream stream = client.GetStream();
			stream.Write(BusyHttpResponsePacket, 0, BusyHttpResponsePacket.Length);
			stream.Flush();
		}
		catch
		{
		}
		finally
		{
			try
			{
				client.Dispose();
			}
			catch
			{
			}
		}
	}

	private void UpdatePeakQueueDepth(int depth)
	{
		int num = Volatile.Read(in webPeakQueuedRequests);
		while (depth > num)
		{
			int num2 = Interlocked.CompareExchange(ref webPeakQueuedRequests, depth, num);
			if (num2 == num)
			{
				break;
			}
			num = num2;
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken, long acceptedTimestamp, double queueWaitMilliseconds)
	{
		client.NoDelay = true;
		IPAddress remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
		if (remote == null || !IsRemoteAllowed(remote))
		{
			await WriteResponseAsync(client.GetStream(), 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Forbidden"), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		linkedCts.CancelAfter(TimeSpan.FromSeconds(15L));
		NetworkStream stream = client.GetStream();
		WebRequest request = await ReadRequestAsync(stream, linkedCts.Token).ConfigureAwait(continueOnCapturedContext: false);
		if (request == null)
		{
			return;
		}
		if (request.Method == "POST" && request.Path == "/api/admin/database-storage/analyze")
		{
			linkedCts.CancelAfter(TimeSpan.FromSeconds(60L));
		}
		WebRequestMetricContext metric = new WebRequestMetricContext
		{
			UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Method = request.Method,
			Path = request.Path,
			StartedTimestamp = ((acceptedTimestamp > 0) ? acceptedTimestamp : Stopwatch.GetTimestamp()),
			QueueWaitMilliseconds = queueWaitMilliseconds
		};
		requestMetricContext.Value = metric;
		Interlocked.Increment(ref webActiveRequests);
		bool failed = false;
		try
		{
			await RouteAsync(stream, request, remote, linkedCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			if (ShouldInvalidateAfterResponse(request.Method, request.Path, metric.StatusCode) && !metric.ResponseRejected)
			{
				snapshotCache.Invalidate((string key) => CacheAffectedByMutation(key, request.Path));
			}
		}
		catch
		{
			failed = true;
			throw;
		}
		finally
		{
			requestMetricContext.Value = null;
			Interlocked.Decrement(ref webActiveRequests);
			CompleteWebRequest(metric, failed);
		}
	}

	internal static bool ShouldInvalidateAfterResponse(string method, string path, int status)
	{
		bool flag;
		switch (method)
		{
		case "POST":
		case "PUT":
		case "PATCH":
		case "DELETE":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		bool flag2 = flag && status >= 200 && status < 300 && path.StartsWith("/api/", StringComparison.Ordinal);
		bool flag3 = flag2;
		if (flag3)
		{
			bool flag4;
			switch (path)
			{
			case "/api/login":
			case "/api/logout":
			case "/api/auth/login":
			case "/api/auth/logout":
				flag4 = true;
				break;
			default:
				flag4 = false;
				break;
			}
			flag3 = !flag4;
		}
		return flag3;
	}

	internal static bool CacheAffectedByMutation(string key, string path)
	{
		bool flag = ((key == "live" || key == "dashboard") ? true : false);
		return flag || (key.StartsWith("anticheat", StringComparison.Ordinal) && path.StartsWith("/api/anticheat", StringComparison.Ordinal)) || (key == "discord-status" && path.StartsWith("/api/discord", StringComparison.Ordinal)) || (key.StartsWith("performance-", StringComparison.Ordinal) && (path.StartsWith("/api/performance", StringComparison.Ordinal) || path.StartsWith("/api/profiler", StringComparison.Ordinal) || path.StartsWith("/api/tick-debug", StringComparison.Ordinal))) || path.StartsWith("/api/operations/config-restore", StringComparison.Ordinal);
	}

	private DatabaseStorageViewerSnapshot GetDatabaseStorageViewerSnapshot()
	{
		lock (databaseStorageViewerLock)
		{
			if (cachedDatabaseStorageViewer != null)
			{
				RefreshDatabaseStorageFileTotals(cachedDatabaseStorageViewer);
				cachedDatabaseStorageViewer.CapturedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				return cachedDatabaseStorageViewer;
			}
		}
		AdminStorageHealth adminStorageHealth = module.Admin?.GetStorageHealth() ?? new AdminStorageHealth
		{
			Available = false
		};
		DatabaseHealthSnapshot databaseHealthSnapshot = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays) ?? new DatabaseHealthSnapshot
		{
			Available = false
		};
		DatabaseStorageViewerSnapshot databaseStorageViewerSnapshot = new DatabaseStorageViewerSnapshot
		{
			CapturedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			DetailedAnalysisAvailable = false,
			DetailedAnalysisUnixMilliseconds = 0L,
			Message = "Live file totals are shown. Press Analyze Storage for per-table/index usage; detailed scans are intentionally manual and cached."
		};
		databaseStorageViewerSnapshot.Databases.Add(DatabaseStorageAnalyzer.CreateLightweight("essentials", "Essentials Admin SQLite", adminStorageHealth.Path, adminStorageHealth.BackupCount, DatabaseStorageAnalyzer.GetBackupBytes(adminStorageHealth.Path, "essentials"), 0));
		databaseStorageViewerSnapshot.Databases.Add(DatabaseStorageAnalyzer.CreateLightweight("blocklogger", "BlockLogger SQLite", databaseHealthSnapshot.Path, databaseHealthSnapshot.BackupCount, databaseHealthSnapshot.BackupBytes, module.Config.RetentionDays));
		return databaseStorageViewerSnapshot;
	}

	private void RefreshDatabaseStorageFileTotals(DatabaseStorageViewerSnapshot snapshot)
	{
		AdminStorageHealth adminStorageHealth = module.Admin?.GetStorageHealth() ?? new AdminStorageHealth
		{
			Available = false
		};
		DatabaseHealthSnapshot databaseHealthSnapshot = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays) ?? new DatabaseHealthSnapshot
		{
			Available = false
		};
		foreach (DatabaseStorageDatabaseSnapshot database in snapshot.Databases)
		{
			if (database.Key.Equals("essentials", StringComparison.OrdinalIgnoreCase))
			{
				database.Available = adminStorageHealth.Available;
				database.DatabaseBytes = SafeFileLength(adminStorageHealth.Path);
				database.WalBytes = SafeFileLength(adminStorageHealth.Path + "-wal");
				database.ShmBytes = SafeFileLength(adminStorageHealth.Path + "-shm");
				database.BackupCount = adminStorageHealth.BackupCount;
				database.BackupBytes = DatabaseStorageAnalyzer.GetBackupBytes(adminStorageHealth.Path, "essentials");
			}
			else if (database.Key.Equals("blocklogger", StringComparison.OrdinalIgnoreCase))
			{
				database.Available = databaseHealthSnapshot.Available;
				database.DatabaseBytes = databaseHealthSnapshot.DatabaseBytes;
				database.WalBytes = databaseHealthSnapshot.WalBytes;
				database.ShmBytes = databaseHealthSnapshot.ShmBytes;
				database.BackupCount = databaseHealthSnapshot.BackupCount;
				database.BackupBytes = databaseHealthSnapshot.BackupBytes;
				database.RetentionDays = module.Config.RetentionDays;
			}
			if (!database.Detailed)
			{
				continue;
			}
			foreach (DatabaseStorageTableSnapshot table in database.Tables)
			{
				table.PercentOfDatabase = ((database.LogicalDatabaseBytes <= 0) ? 0.0 : ((double)table.TotalBytes * 100.0 / (double)database.LogicalDatabaseBytes));
			}
		}
	}

	private static long SafeFileLength(string? path)
	{
		try
		{
			return (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) ? new FileInfo(path).Length : 0;
		}
		catch
		{
			return 0L;
		}
	}

	private DatabaseStorageViewerSnapshot AnalyzeDatabaseStorage()
	{
		AdminStorageHealth adminStorageHealth = module.Admin?.GetStorageHealth() ?? new AdminStorageHealth
		{
			Available = false
		};
		DatabaseHealthSnapshot databaseHealthSnapshot = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays) ?? new DatabaseHealthSnapshot
		{
			Available = false
		};
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		DatabaseStorageViewerSnapshot databaseStorageViewerSnapshot = new DatabaseStorageViewerSnapshot
		{
			CapturedUnixMilliseconds = num,
			DetailedAnalysisUnixMilliseconds = num,
			Message = "Storage analysis finished. Exact page statistics and payload estimates are labeled per database. This report is cached until Analyze Storage is run again."
		};
		DatabaseStorageDatabaseSnapshot databaseStorageDatabaseSnapshot = DatabaseStorageAnalyzer.Analyze("essentials", "Essentials Admin SQLite", adminStorageHealth.Path, adminStorageHealth.BackupCount, DatabaseStorageAnalyzer.GetBackupBytes(adminStorageHealth.Path, "essentials"), 0);
		DatabaseStorageDatabaseSnapshot databaseStorageDatabaseSnapshot2 = DatabaseStorageAnalyzer.Analyze("blocklogger", "BlockLogger SQLite", databaseHealthSnapshot.Path, databaseHealthSnapshot.BackupCount, databaseHealthSnapshot.BackupBytes, module.Config.RetentionDays);
		databaseStorageViewerSnapshot.Databases.Add(databaseStorageDatabaseSnapshot);
		databaseStorageViewerSnapshot.Databases.Add(databaseStorageDatabaseSnapshot2);
		databaseStorageViewerSnapshot.DetailedAnalysisAvailable = databaseStorageDatabaseSnapshot.Detailed || databaseStorageDatabaseSnapshot2.Detailed;
		databaseStorageViewerSnapshot.Recommendations.AddRange(databaseStorageDatabaseSnapshot.Recommendations);
		databaseStorageViewerSnapshot.Recommendations.AddRange(databaseStorageDatabaseSnapshot2.Recommendations);
		lock (databaseStorageViewerLock)
		{
			cachedDatabaseStorageViewer = databaseStorageViewerSnapshot;
		}
		return databaseStorageViewerSnapshot;
	}

	private string GetAntiCheatApprovalUploadRoot()
	{
		string text = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "anticheat", "approval-uploads");
		Directory.CreateDirectory(text);
		return text;
	}

	private void PruneAntiCheatApprovalUploads()
	{
		DateTime dateTime = DateTime.UtcNow - AntiCheatApprovalUploadLifetime;
		KeyValuePair<string, AntiCheatApprovalUploadState>[] array = antiCheatApprovalUploads.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			KeyValuePair<string, AntiCheatApprovalUploadState> keyValuePair = array[i];
			if (keyValuePair.Value.CreatedUtc >= dateTime || !antiCheatApprovalUploads.TryRemove(keyValuePair.Key, out AntiCheatApprovalUploadState value))
			{
				continue;
			}
			try
			{
				if (File.Exists(value.TempPath))
				{
					File.Delete(value.TempPath);
				}
			}
			catch
			{
			}
		}
		try
		{
			string antiCheatApprovalUploadRoot = GetAntiCheatApprovalUploadRoot();
			foreach (string item in Directory.EnumerateFiles(antiCheatApprovalUploadRoot, "*.zip", SearchOption.TopDirectoryOnly))
			{
				try
				{
					if (File.GetLastWriteTimeUtc(item) < dateTime)
					{
						File.Delete(item);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private void RemoveAntiCheatApprovalUpload(string uploadId)
	{
		if (string.IsNullOrWhiteSpace(uploadId) || !antiCheatApprovalUploads.TryRemove(uploadId, out AntiCheatApprovalUploadState value))
		{
			return;
		}
		try
		{
			if (File.Exists(value.TempPath))
			{
				File.Delete(value.TempPath);
			}
		}
		catch
		{
		}
	}

	private async Task<AdminInventoryInspection> LoadSavedInventoryInspectionAsync(AdminService admin, string uid)
	{
		string worldFilepath = await RunOnMainThreadAsync(() => WorldSavePathResolver.Resolve(api.WorldManager, api.DataBasePath), "web-admin-inventory-savepath", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
		SavedPlayerInventoryData saved = await Task.Run(() => admin.ReadSavedInventory(worldFilepath, uid)).ConfigureAwait(continueOnCapturedContext: false);
		return await RunOnMainThreadAsync(() => admin.GetInventoryInspection(uid, saved), "web-admin-offline-inventory", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task RouteAsync(NetworkStream stream, WebRequest request, IPAddress remote, CancellationToken cancellationToken)
	{
		string path = request.Path;
		string remoteText = remote.ToString();
		if (request.Method == "GET" && (path == "/" || path == "/index.html" || path.Equals("/leaderboard", StringComparison.OrdinalIgnoreCase) || path.Equals("/leaderboard/", StringComparison.OrdinalIgnoreCase) || path.Equals("/leaderboard.html", StringComparison.OrdinalIgnoreCase)))
		{
			bool gzip = AcceptsDashboardGzip(request.Headers.TryGetValue("Accept-Encoding", out string encodings) ? encodings : null);
			Dictionary<string, string> headers = new Dictionary<string, string> { ["Vary"] = "Accept-Encoding" };
			if (gzip)
			{
				headers["Content-Encoding"] = "gzip";
			}
			await WriteResponseAsync(stream, 200, "text/html; charset=utf-8", gzip ? DashboardHtmlGzipBytes : DashboardHtmlBytes, cancellationToken, headers).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		if (request.Method == "GET" && path == "/api/session")
		{
			WebSession activeSession;
			if (IsLocalAnonymousRequest(remote))
			{
				await WriteJsonAsync(stream, 200, new
				{
					authenticated = true,
					username = "local",
					role = "Owner",
					owner = true,
					csrfToken = localAnonymousCsrfToken,
					expiresUtc = DateTime.MaxValue,
					localAnonymous = true,
					safeDiagnosticsMode = config.SafeDiagnosticsMode
				}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (TryGetSession(request, out activeSession))
			{
				await WriteJsonAsync(stream, 200, new
				{
					authenticated = true,
					username = activeSession.Username,
					role = activeSession.Role,
					owner = activeSession.IsOwner,
					csrfToken = activeSession.CsrfToken,
					expiresUtc = activeSession.ExpiresUtc,
					localAnonymous = false,
					safeDiagnosticsMode = config.SafeDiagnosticsMode
				}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			else
			{
				await WriteJsonAsync(stream, 200, new
				{
					authenticated = false,
					username = "",
					role = "",
					owner = false,
					csrfToken = "",
					expiresUtc = DateTime.MinValue,
					localAnonymous = false,
					safeDiagnosticsMode = config.SafeDiagnosticsMode
				}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			return;
		}
		if (request.Method == "POST" && path == "/api/login")
		{
			await HandleLoginAsync(stream, request, remote, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		WebSession webSession;
		if (IsLocalAnonymousRequest(remote))
		{
			webSession = new WebSession
			{
				Token = "",
				CsrfToken = localAnonymousCsrfToken,
				RemoteIp = remote.ToString(),
				Username = "local",
				Role = "Owner",
				IsOwner = true,
				CreatedUtc = startedUtc,
				ExpiresUtc = DateTime.MaxValue
			};
		}
		else if (!TryGetSession(request, out webSession))
		{
			await WriteJsonAsync(stream, 401, new
			{
				error = "Authentication required."
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string requestActor = webSession?.Username ?? (LocalAnonymousMode ? "local" : config.AdminUsername);
		if (path.StartsWith("/api/web/accounts", StringComparison.OrdinalIgnoreCase) && !(webSession?.IsOwner ?? false))
		{
			RecordWebAudit("panel-account-access", path, "Denied / owner required", remote.ToString(), requestActor);
			await WriteJsonAsync(stream, 403, new
			{
				error = "Only the panel Owner can manage administrator accounts."
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		int num;
		if (request.Method == "POST")
		{
			switch (path)
			{
			case "/api/custom-patches/save":
			case "/api/custom-patches/action":
			case "/api/content-patches/save":
			case "/api/content-patches/action":
			case "/api/content-patches/build":
				break;
			default:
				goto IL_1217;
			}
			if (!(webSession?.IsOwner ?? false))
			{
				num = ((!LocalAnonymousMode) ? 1 : 0);
				goto IL_1218;
			}
		}
		goto IL_1217;
		IL_1217:
		num = 0;
		goto IL_1218;
		IL_1218:
		if (num != 0)
		{
			RecordWebAudit("custom-patch-access", path, "Denied / owner required", remote.ToString(), requestActor);
			await WriteJsonAsync(stream, 403, new
			{
				error = "Only the panel Owner can create, edit, enable, disable, or delete runtime or generated content patches."
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		bool readOnlyAccount = string.Equals(webSession?.Role, "ReadOnly", StringComparison.OrdinalIgnoreCase);
		WebRoutePolicyInfo authorizationPolicy = FindWebRoutePolicy(request.Method, path);
		if (readOnlyAccount && ((request.Method == "POST" && path != "/api/logout") || (authorizationPolicy?.ExclusiveHeavyOperation ?? false)))
		{
			RecordWebAudit("read-only-denied", path, "Denied / read-only account", remote.ToString(), requestActor);
			await WriteJsonAsync(stream, 403, new
			{
				error = "This panel account is Read Only and cannot perform changes or launch heavy diagnostic operations."
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		if (request.Method == "POST" && !ConstantTimeEquals(request.Headers.TryGetValue("X-CSRF-Token", out string csrf) ? csrf : "", webSession.CsrfToken))
		{
			await WriteJsonAsync(stream, 403, new
			{
				error = "Invalid CSRF token."
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		if (TryGetMasterDisabledFeature(path, out string disabledFeature))
		{
			await WriteJsonAsync(stream, 503, new
			{
				error = disabledFeature + " is master-disabled. Enable it in Suite Feature Gates and restart the server.",
				featureDisabled = true,
				restartRequired = true
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		if (TryPrepareRouteOperation(request, out RouteOperationLease routeOperation, out string routePolicyError))
		{
			using (routeOperation)
			{
				WebRequestMetricContext routeMetric = requestMetricContext.Value;
				if (routeMetric != null)
				{
					routeMetric.RouteLease = routeOperation;
				}
				if (request.Method == "GET" && path == "/api/operations/diagnostics")
				{
					await WriteJsonAsync(stream, 200, await GetCachedMainThreadReadAsync("operations-retention", 5000, BuildRetentionDiagnostics, "web-retention-diagnostics").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/admin/block-history")
				{
					string player = GetQuery(request, "player", "").Trim();
					AdminPlayerSummary summary = await RunOnMainThreadAsync(() => this.module.Admin?.GetPlayer(player), "web-investigation-player", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
					if (summary == null)
					{
						await WriteJsonAsync(stream, 404, new
						{
							error = "Player not found."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					else if (this.module.Queries == null)
					{
						await WriteJsonAsync(stream, 503, new
						{
							error = "Block history is unavailable."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					else
					{
						List<LogEventRecord> history = await this.module.Queries.QueryPlayerUidAsync(summary.PlayerUid).ConfigureAwait(continueOnCapturedContext: false);
						await WriteJsonAsync(stream, 200, new
						{
							playerUid = summary.PlayerUid,
							history = history,
							limit = 100
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				else if (request.Method == "GET" && path == "/api/admin/investigation")
				{
					string player2 = GetQuery(request, "player", "").Trim();
					if (player2.Length == 0 || player2.Length > 128)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Choose a player."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					else
					{
						object payload = await RunOnMainThreadAsync(() => BuildPlayerInvestigation(player2), "web-player-investigation", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
						await WriteJsonAsync(stream, (payload == null) ? 404 : 200, payload ?? new
						{
							error = "Player not found."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				else if (request.Method == "GET" && path == "/api/live")
				{
					bool terrainExportBusy = this.module.WebMap?.IsTerrainExportInProgress ?? false;
					await WriteJsonAsync(stream, 200, await GetLiveSnapshotAsync(terrainExportBusy).ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/features")
				{
					await WriteJsonAsync(stream, 200, new
					{
						configFile = "NewfiesServerEssentialsFeatures.json",
						restartRequiredForChanges = true,
						features = (from x in this.module.FeatureSettings.GetEntries()
							select new
							{
								name = x.Name,
								enabled = x.Enabled
							}).ToList(),
						activeFeatures = (from x in this.module.Features.GetEntries()
							select new
							{
								name = x.Name,
								enabled = x.Enabled
							}).ToList()
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/features")
				{
					string feature = GetJsonString(request.Body, "feature", "").Trim();
					bool enabled = GetJsonBool(request.Body, "enabled", fallback: false);
					await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
					{
						if (string.Equals(feature, "all", StringComparison.OrdinalIgnoreCase))
						{
							this.module.FeatureSettings.SetAll(enabled);
							this.module.FeatureSettings.Save(api);
							return new
							{
								success = true,
								feature = "all",
								enabled = enabled,
								restartRequired = true,
								message = "All suite feature gates set to " + (enabled ? "ON" : "OFF") + ". Restart the server to fully apply."
							};
						}
						if (!this.module.FeatureSettings.TrySet(feature, enabled, out string canonicalName))
						{
							return new
							{
								success = false,
								feature = feature,
								enabled = enabled,
								restartRequired = false,
								message = "Unknown feature gate. Refresh the feature list and try again."
							};
						}
						this.module.FeatureSettings.Save(api);
						return new
						{
							success = true,
							feature = canonicalName,
							enabled = enabled,
							restartRequired = true,
							message = $"Feature '{canonicalName}' set to {(enabled ? "ON" : "OFF")}. Restart the server to fully unregister/register the subsystem."
						};
					}, "web-feature-gate-save").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/playerlist/config")
				{
					PublicPlayerListServer playerList = this.module.PlayerList;
					await WriteJsonAsync(stream, 200, new
					{
						available = (playerList != null),
						configFile = "NewfiesPlayerList.json",
						configSchemaVersion = (playerList?.Config.SchemaVersion ?? 6),
						styleVersion = 5,
						serverName = (playerList?.Config.ServerName ?? ""),
						effectiveServerName = (playerList?.DisplayServerName ?? api.Server.Config.ServerName ?? ""),
						backgroundOpacity = (playerList?.Config.BackgroundOpacity ?? 0.72),
						backgroundOpacityPercent = (int)Math.Round((playerList?.Config.BackgroundOpacity ?? 0.72) * 100.0),
						showRoles = (playerList?.Config.ShowRoles ?? true),
						showPing = (playerList?.Config.ShowPing ?? true),
						showSessionTime = (playerList?.Config.ShowSessionTime ?? true),
						layoutScale = (playerList?.Config.LayoutScale ?? 1.0),
						rowsPerColumn = (playerList?.Config.RowsPerColumn ?? 8),
						maxColumns = (playerList?.Config.MaxColumns ?? 4),
						alignment = (playerList?.Config.Alignment ?? "CenterTop"),
						offsetX = (playerList?.Config.OffsetX ?? 0),
						offsetY = (playerList?.Config.OffsetY ?? 22),
						showPlayerInitials = (playerList?.Config.ShowPlayerInitials ?? true),
						showCapacityBar = (playerList?.Config.ShowCapacityBar ?? true),
						showColumnHeaders = (playerList?.Config.ShowColumnHeaders ?? false),
						compactRows = (playerList?.Config.CompactRows ?? false),
						showPingBars = (playerList?.Config.ShowPingBars ?? true),
						showFooterHint = (playerList?.Config.ShowFooterHint ?? false),
						accentHex = (playerList?.Config.AccentHex ?? "#D6AE63"),
						useRoleNameColors = (playerList?.Config.UseRoleNameColors ?? false),
						roleNameColors = (playerList?.Config.RoleNameColors ?? new Dictionary<string, string>()),
						reservedSlotsEnabled = (playerList?.Config.ReservedSlotsEnabled ?? false),
						reservedSlots = (playerList?.Config.ReservedSlots ?? 0),
						reservedSlotRoles = (playerList?.Config.ReservedSlotRoles ?? new List<string>()),
						reservedSlotsFullMessage = (playerList?.Config.ReservedSlotsFullMessage ?? "Only reserved slots are left on this server."),
						maxClients = Math.Max(0, api.Server.Config.MaxClients),
						publicSlotLimit = (playerList?.PublicSlotLimit ?? Math.Max(0, api.Server.Config.MaxClients)),
						reservedEligibleOnline = (playerList?.ReservedEligibleOnlineCount ?? 0),
						publicOnline = (playerList?.PublicOnlineCount ?? api.World.AllOnlinePlayers?.Length ?? 0),
						reservedSlotRejections = (playerList?.ReservedSlotRejections ?? 0),
						configuredRoles = api.Server.Config.Roles.Select((IPlayerRole r) => new
						{
							code = (r.Code ?? ""),
							name = (r.Name ?? "")
						}).ToArray(),
						debugLogging = (playerList?.Config.DebugLogging ?? false),
						rosterSource = (playerList?.RosterSource ?? "Unavailable"),
						suiteRequiredOnClient = false,
						suiteVersion = "1.2.44",
						backendChannel = "newfies-public-playerlist-v1",
						backendBuild = "suite-1.2.44-playerlist-v5",
						protocolVersion = 1,
						snapshotCacheMs = 750,
						requestThrottleMs = 750,
						companionModId = "newfiesplayerlist",
						companionRecommendedVersion = "1.1.1",
						requestsReceived = (playerList?.RequestsReceived ?? 0),
						responsesSent = (playerList?.ResponsesSent ?? 0),
						requestsThrottled = (playerList?.RequestsThrottled ?? 0),
						responseSendFailures = (playerList?.ResponseSendFailures ?? 0),
						requestBuildFailures = (playerList?.RequestBuildFailures ?? 0),
						clientHellosReceived = (playerList?.ClientHellosReceived ?? 0),
						cachedPlayerCount = (playerList?.CachedPlayerCount ?? 0),
						cachedSnapshotAgeMs = (playerList?.CachedSnapshotAgeMs ?? (-1))
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/playerlist/config")
				{
					PublicPlayerListConfig incoming = new PublicPlayerListConfig
					{
						ServerName = GetJsonString(request.Body, "serverName", ""),
						BackgroundOpacity = GetJsonDouble(request.Body, "backgroundOpacity", 0.72),
						ShowRoles = GetJsonBool(request.Body, "showRoles", fallback: true),
						ShowPing = GetJsonBool(request.Body, "showPing", fallback: true),
						ShowSessionTime = GetJsonBool(request.Body, "showSessionTime", fallback: true),
						LayoutScale = GetJsonDouble(request.Body, "layoutScale", 1.0),
						RowsPerColumn = GetJsonInt(request.Body, "rowsPerColumn", 8),
						MaxColumns = GetJsonInt(request.Body, "maxColumns", 4),
						Alignment = GetJsonString(request.Body, "alignment", "CenterTop"),
						OffsetX = GetJsonInt(request.Body, "offsetX", 0),
						OffsetY = GetJsonInt(request.Body, "offsetY", 22),
						ShowPlayerInitials = GetJsonBool(request.Body, "showPlayerInitials", fallback: true),
						ShowCapacityBar = GetJsonBool(request.Body, "showCapacityBar", fallback: true),
						ShowColumnHeaders = GetJsonBool(request.Body, "showColumnHeaders", fallback: false),
						CompactRows = GetJsonBool(request.Body, "compactRows", fallback: false),
						ShowPingBars = GetJsonBool(request.Body, "showPingBars", fallback: true),
						ShowFooterHint = GetJsonBool(request.Body, "showFooterHint", fallback: false),
						AccentHex = GetJsonString(request.Body, "accentHex", "#D6AE63"),
						UseRoleNameColors = false,
						RoleNameColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
						ReservedSlotsEnabled = GetJsonBool(request.Body, "reservedSlotsEnabled", fallback: false),
						ReservedSlots = GetJsonInt(request.Body, "reservedSlots", 0),
						ReservedSlotRoles = GetJsonString(request.Body, "reservedSlotRoles", "").Split(new char[4] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
						ReservedSlotsFullMessage = GetJsonString(request.Body, "reservedSlotsFullMessage", "Only reserved slots are left on this server."),
						DebugLogging = GetJsonBool(request.Body, "debugLogging", fallback: false)
					};
					incoming.Clamp();
					await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
					{
						PublicPlayerListServer playerList2 = this.module.PlayerList;
						if (playerList2 == null)
						{
							return new
							{
								success = false,
								message = "Player-list backend is unavailable.",
								effectiveServerName = "",
								configSchemaVersion = 6
							};
						}
						incoming.UseRoleNameColors = playerList2.Config.UseRoleNameColors;
						incoming.RoleNameColors = new Dictionary<string, string>(playerList2.Config.RoleNameColors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
						incoming.Clamp();
						playerList2.ApplyConfig(incoming);
						RecordWebAudit("playerlist-config", $"Header='{playerList2.DisplayServerName}', opacity={playerList2.Config.BackgroundOpacity:P0}, scale={playerList2.Config.LayoutScale:0.00}, rows={playerList2.Config.RowsPerColumn}, columns={playerList2.Config.MaxColumns}, align={playerList2.Config.Alignment}, roleColors={(playerList2.Config.UseRoleNameColors ? (playerList2.Config.RoleNameColors?.Count ?? 0) : 0)}, reserved={(playerList2.Config.ReservedSlotsEnabled ? playerList2.Config.ReservedSlots : 0)}, reservedRoles={playerList2.Config.ReservedSlotRoles.Count}, debug={(playerList2.Config.DebugLogging ? "on" : "off")}", "Success", remote.ToString(), requestActor);
						return new
						{
							success = true,
							message = "Player-list configuration saved.",
							effectiveServerName = playerList2.DisplayServerName,
							configSchemaVersion = playerList2.Config.SchemaVersion
						};
					}, "web-playerlist-config-save").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/collection-audit")
				{
					await WriteJsonAsync(stream, 200, BuildCollectionAuditSnapshot(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/compatibility/ignore")
				{
					string fingerprint = GetJsonString(request.Body, "fingerprint", "").Trim().ToLowerInvariant();
					string reason = GetJsonString(request.Body, "reason", "").Trim();
					if (fingerprint.Length != 64)
					{
						await WriteJsonAsync(stream, 400, new
						{
							success = false,
							message = "Invalid compatibility fingerprint."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					else
					{
						CompatibilityIgnoreStore compatibilityIgnoreStore = compatibilityIgnores;
						if (compatibilityIgnoreStore.Ignores == null)
						{
							compatibilityIgnoreStore.Ignores = new List<CompatibilityIgnoreRecord>();
						}
						compatibilityIgnores.Ignores.RemoveAll((CompatibilityIgnoreRecord x) => string.Equals(x.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
						compatibilityIgnores.Ignores.Add(new CompatibilityIgnoreRecord
						{
							Fingerprint = fingerprint,
							Reason = reason,
							IgnoredBy = requestActor,
							IgnoredUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
						});
						SaveCompatibilityIgnores();
						WebDashboardService webDashboardService = this;
						string text = fingerprint;
						webDashboardService.RecordWebAudit("compatibility-ignore", text.Substring(0, 12), string.IsNullOrWhiteSpace(reason) ? "Ignored" : ("Ignored: " + reason), remoteText, requestActor);
						await WriteJsonAsync(stream, 200, new
						{
							success = true,
							message = "Compatibility notification ignored."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				else if (request.Method == "POST" && path == "/api/compatibility/unignore")
				{
					string fingerprint2 = GetJsonString(request.Body, "fingerprint", "").Trim().ToLowerInvariant();
					CompatibilityIgnoreStore compatibilityIgnoreStore = compatibilityIgnores;
					if (compatibilityIgnoreStore.Ignores == null)
					{
						compatibilityIgnoreStore.Ignores = new List<CompatibilityIgnoreRecord>();
					}
					int removed = compatibilityIgnores.Ignores.RemoveAll((CompatibilityIgnoreRecord x) => string.Equals(x.Fingerprint, fingerprint2, StringComparison.OrdinalIgnoreCase));
					if (removed > 0)
					{
						SaveCompatibilityIgnores();
					}
					WebDashboardService webDashboardService2 = this;
					string detail;
					if (fingerprint2.Length < 12)
					{
						detail = fingerprint2;
					}
					else
					{
						string text = fingerprint2;
						detail = text.Substring(0, 12);
					}
					webDashboardService2.RecordWebAudit("compatibility-unignore", detail, (removed > 0) ? "Restored" : "Not found", remoteText, requestActor);
					await WriteJsonAsync(stream, 200, new
					{
						success = true,
						message = ((removed > 0) ? "Compatibility notification restored." : "Ignore rule was already absent.")
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && (path == "/api/compatibility" || path == "/api/compatibility/deep"))
				{
					bool deep = path == "/api/compatibility/deep";
					await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => BuildCompatibilitySnapshot(deep), deep ? "web-compatibility-deep" : "web-compatibility").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/custom-patches")
				{
					await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => this.module.CustomPatches?.GetSnapshot(this.module.ServerHealth?.GetSnapshot(120, 300)) ?? new CustomPatchSnapshot
					{
						Available = false,
						Note = "Patch Studio is unavailable."
					}, "web-custom-patches").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/custom-patches/generate")
				{
					CustomPatchGenerateRequest incoming2 = null;
					try
					{
						incoming2 = JsonSerializer.Deserialize<CustomPatchGenerateRequest>(request.Body, jsonOptions);
					}
					catch
					{
					}
					CustomPatchActionResult result = this.module.CustomPatches?.Generate(incoming2 ?? new CustomPatchGenerateRequest()) ?? new CustomPatchActionResult
					{
						Success = false,
						Message = "Patch Studio is unavailable."
					};
					await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/custom-patches/save")
				{
					CustomPatchDefinition incoming3 = null;
					try
					{
						incoming3 = JsonSerializer.Deserialize<CustomPatchDefinition>(request.Body, jsonOptions);
					}
					catch (Exception ex)
					{
						await WriteJsonAsync(stream, 400, new
						{
							success = false,
							message = "Invalid patch definition: " + ex.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					CustomPatchActionResult result2 = await RunOnMainThreadAsync(() => this.module.CustomPatches?.SaveFromRequest(incoming3 ?? new CustomPatchDefinition()) ?? new CustomPatchActionResult
					{
						Success = false,
						Message = "Patch Studio is unavailable."
					}, "web-custom-patches-save").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("custom-patch-save", incoming3?.Name ?? "", result2.Success ? "Success" : result2.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result2.Success ? 200 : 400, result2, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/custom-patches/action")
				{
					string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
					string id = GetJsonString(request.Body, "id", "").Trim();
					if (1 == 0)
					{
					}
					CustomPatchActionResult customPatchActionResult2;
					switch (action)
					{
					case "enable":
						customPatchActionResult2 = await RunOnMainThreadAsync(() => this.module.CustomPatches?.SetEnabled(id, enabled: true) ?? new CustomPatchActionResult
						{
							Success = false,
							Message = "Patch Studio is unavailable."
						}, "web-custom-patches-enable").ConfigureAwait(continueOnCapturedContext: false);
						break;
					case "disable":
						customPatchActionResult2 = await RunOnMainThreadAsync(() => this.module.CustomPatches?.SetEnabled(id, enabled: false) ?? new CustomPatchActionResult
						{
							Success = false,
							Message = "Patch Studio is unavailable."
						}, "web-custom-patches-disable").ConfigureAwait(continueOnCapturedContext: false);
						break;
					case "delete":
						customPatchActionResult2 = await RunOnMainThreadAsync(() => this.module.CustomPatches?.Delete(id) ?? new CustomPatchActionResult
						{
							Success = false,
							Message = "Patch Studio is unavailable."
						}, "web-custom-patches-delete").ConfigureAwait(continueOnCapturedContext: false);
						break;
					default:
					{
						CustomPatchActionResult customPatchActionResult = new CustomPatchActionResult();
						customPatchActionResult.Success = false;
						customPatchActionResult.Message = "Unknown patch action.";
						customPatchActionResult2 = customPatchActionResult;
						break;
					}
					}
					if (1 == 0)
					{
					}
					CustomPatchActionResult result3 = customPatchActionResult2;
					RecordWebAudit("custom-patch-" + action, id, result3.Success ? "Success" : result3.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result3.Success ? 200 : 400, result3, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/content-patches/assets")
				{
					object result4 = this.module.CustomPatches?.GetContentAssets(GetQuery(request, "kind", ""), GetQuery(request, "search", ""));
					await WriteJsonAsync(stream, (result4 == null) ? 503 : 200, result4 ?? new
					{
						error = "Patch Generator is unavailable."
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/content-patches/export")
				{
					try
					{
						byte[] bytes = this.module.CustomPatches?.ExportContentPatchMod();
						if (bytes == null)
						{
							throw new InvalidOperationException("Patch Generator is unavailable.");
						}
						await WriteResponseAsync(stream, 200, "application/zip", bytes, cancellationToken, new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"NewfiesSuiteContentPatches.zip\"" }).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (Exception ex2)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = ex2.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				else if (request.Method == "GET" && path == "/api/content-patches/asset")
				{
					try
					{
						ContentAssetInspection result5 = this.module.CustomPatches?.InspectContentAsset(GetQuery(request, "file", ""));
						await WriteJsonAsync(stream, (result5 == null) ? 503 : 200, ((object)result5) ?? ((object)new
						{
							error = "Patch Generator is unavailable."
						}), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (Exception ex3)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = ex3.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				else if (request.Method == "POST" && path == "/api/content-patches/build")
				{
					ContentPatchGenerateRequest incoming4;
					try
					{
						incoming4 = JsonSerializer.Deserialize<ContentPatchGenerateRequest>(request.Body, jsonOptions);
					}
					catch
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid generator request."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					ContentPatchActionResult result6 = this.module.CustomPatches?.SaveGeneratedContentPatch(incoming4 ?? new ContentPatchGenerateRequest()) ?? new ContentPatchActionResult
					{
						Success = false,
						Message = "Patch Generator is unavailable."
					};
					RecordWebAudit("content-patch-build", incoming4?.Name ?? "", result6.Success ? "Restart required" : result6.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result6.Success ? 200 : 400, result6, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/content-patches")
				{
					ContentPatchSnapshot payload2 = this.module.CustomPatches?.GetContentSnapshot() ?? new ContentPatchSnapshot
					{
						Available = false,
						Note = "Content Patch Studio is unavailable."
					};
					await WriteJsonAsync(stream, 200, payload2, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/content-patches/generate")
				{
					ContentPatchGenerateRequest incoming5 = null;
					try
					{
						incoming5 = JsonSerializer.Deserialize<ContentPatchGenerateRequest>(request.Body, jsonOptions);
					}
					catch
					{
					}
					ContentPatchActionResult result7 = this.module.CustomPatches?.GenerateContentPatch(incoming5 ?? new ContentPatchGenerateRequest()) ?? new ContentPatchActionResult
					{
						Success = false,
						Message = "Content Patch Studio is unavailable."
					};
					await WriteJsonAsync(stream, result7.Success ? 200 : 400, result7, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/content-patches/save")
				{
					ContentPatchDefinition incoming6;
					try
					{
						incoming6 = JsonSerializer.Deserialize<ContentPatchDefinition>(request.Body, jsonOptions);
					}
					catch (Exception ex4)
					{
						await WriteJsonAsync(stream, 400, new
						{
							success = false,
							message = "Invalid content patch definition: " + ex4.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					ContentPatchActionResult result8 = this.module.CustomPatches?.SaveContentPatch(incoming6 ?? new ContentPatchDefinition()) ?? new ContentPatchActionResult
					{
						Success = false,
						Message = "Content Patch Studio is unavailable."
					};
					RecordWebAudit("content-patch-save", incoming6?.Name ?? "", result8.Success ? "Restart required" : result8.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result8.Success ? 200 : 400, result8, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/content-patches/action")
				{
					string action2 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
					string id2 = GetJsonString(request.Body, "id", "").Trim();
					if (1 == 0)
					{
					}
					ContentPatchActionResult contentPatchActionResult2;
					switch (action2)
					{
					case "enable":
					{
						ContentPatchActionResult contentPatchActionResult4 = this.module.CustomPatches?.SetContentPatchEnabled(id2, enabled: true);
						if (contentPatchActionResult4 == null)
						{
							ContentPatchActionResult contentPatchActionResult = new ContentPatchActionResult();
							contentPatchActionResult.Success = false;
							contentPatchActionResult.Message = "Content Patch Studio is unavailable.";
							contentPatchActionResult4 = contentPatchActionResult;
						}
						contentPatchActionResult2 = contentPatchActionResult4;
						break;
					}
					case "disable":
					{
						ContentPatchActionResult contentPatchActionResult5 = this.module.CustomPatches?.SetContentPatchEnabled(id2, enabled: false);
						if (contentPatchActionResult5 == null)
						{
							ContentPatchActionResult contentPatchActionResult = new ContentPatchActionResult();
							contentPatchActionResult.Success = false;
							contentPatchActionResult.Message = "Content Patch Studio is unavailable.";
							contentPatchActionResult5 = contentPatchActionResult;
						}
						contentPatchActionResult2 = contentPatchActionResult5;
						break;
					}
					case "delete":
					{
						ContentPatchActionResult contentPatchActionResult3 = this.module.CustomPatches?.DeleteContentPatch(id2);
						if (contentPatchActionResult3 == null)
						{
							ContentPatchActionResult contentPatchActionResult = new ContentPatchActionResult();
							contentPatchActionResult.Success = false;
							contentPatchActionResult.Message = "Content Patch Studio is unavailable.";
							contentPatchActionResult3 = contentPatchActionResult;
						}
						contentPatchActionResult2 = contentPatchActionResult3;
						break;
					}
					default:
					{
						ContentPatchActionResult contentPatchActionResult = new ContentPatchActionResult();
						contentPatchActionResult.Success = false;
						contentPatchActionResult.Message = "Unknown content patch action.";
						contentPatchActionResult2 = contentPatchActionResult;
						break;
					}
					}
					if (1 == 0)
					{
					}
					ContentPatchActionResult result9 = contentPatchActionResult2;
					RecordWebAudit("content-patch-" + action2, id2, result9.Success ? "Restart required" : result9.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result9.Success ? 200 : 400, result9, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/log-intelligence")
				{
					await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => logIntelligence?.GetSnapshot() ?? new LogIntelligenceSnapshot(), "web-log-intelligence").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/log-intelligence/block-entity")
				{
					string key = GetQuery(request, "key", "").Trim();
					await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => InspectBadBlockEntity(key), "web-bad-block-entity-inspect").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/player-analytics")
				{
					int days = Math.Clamp(GetQueryInt(request, "days", 30), 7, 365);
					await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => this.module.PlayerAnalytics?.GetSnapshot(days) ?? new PlayerCountAnalyticsSnapshot(), "web-player-analytics").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/server-status")
				{
					int days2 = Math.Clamp(GetQueryInt(request, "days", 30), 7, 365);
					await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => BuildServerStatusSnapshot(days2), "web-server-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/anticheat/status")
				{
					await WriteJsonAsync(stream, 200, await GetCachedMainThreadReadAsync("anticheat-nav-status", 2000, () => this.module.AntiCheat?.GetNavStatusSnapshot() ?? new AntiCheatNavStatusWebSnapshot(), "web-anticheat-nav-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/anticheat")
				{
					await WriteJsonAsync(stream, 200, await GetCachedMainThreadReadAsync("anticheat", 2000, () => this.module.AntiCheat?.GetSnapshot() ?? new AntiCheatWebSnapshot(), "web-anticheat").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/anticheat/client-mods")
				{
					await WriteJsonAsync(stream, 200, await GetCachedMainThreadReadAsync("anticheat-client-mods", 5000, () => this.module.AntiCheat?.GetClientModsSnapshot() ?? new List<AntiCheatPlayerWebSnapshot>(), "web-anticheat-client-mods").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/anticheat/player")
				{
					string player3 = GetQuery(request, "player", "").Trim();
					if (string.IsNullOrWhiteSpace(player3))
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "player is required."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					else
					{
						AntiCheatPlayerWebSnapshot payload3 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.GetPlayerSnapshot(player3), "web-anticheat-player").ConfigureAwait(continueOnCapturedContext: false);
						if (payload3 == null)
						{
							await WriteJsonAsync(stream, 404, new
							{
								error = "No anti-cheat session was found for that player."
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else
						{
							await WriteJsonAsync(stream, 200, payload3, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
					}
				}
				else if (request.Method == "GET" && path == "/api/anticheat/history")
				{
					string player4 = GetQuery(request, "player", "").Trim();
					string finding = GetQuery(request, "finding", "").Trim();
					int limit = Math.Clamp(GetQueryInt(request, "limit", 250), 1, 1000);
					AntiCheatHistoryWebSnapshot payload4 = this.module.AntiCheat?.GetHistorySnapshot(player4, limit, finding) ?? new AntiCheatHistoryWebSnapshot();
					await WriteJsonAsync(stream, 200, payload4, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/history/clear")
				{
					(bool Success, string Message, int Cleared) result10 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.ClearHistory() ?? (false, "Anti-cheat service is unavailable.", 0), "web-anticheat-history-clear").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-history-clear-all", $"cleared={result10.Cleared}", result10.Success ? "Success" : result10.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result10.Success ? 200 : 400, new
					{
						success = result10.Success,
						message = result10.Message,
						cleared = result10.Cleared
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/settings")
				{
					AntiCheatModuleSaveRequest payload5;
					try
					{
						payload5 = JsonSerializer.Deserialize<AntiCheatModuleSaveRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex5)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid Anti-Cheat module configuration: " + ex5.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					(bool Success, string Message) result11 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.SaveModuleSettings(payload5?.Enabled ?? false) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-settings").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-module-state", $"enabled={payload5?.Enabled}", result11.Success ? "Success" : result11.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result11.Success ? 200 : 400, new
					{
						success = result11.Success,
						message = result11.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/anticheat/full-config")
				{
					await WriteJsonAsync(stream, 200, new
					{
						json = await RunOnMainThreadAsync(() => this.module.AntiCheat?.GetFullConfigJson() ?? "{}", "web-anticheat-full-config-read").ConfigureAwait(continueOnCapturedContext: false)
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/full-config")
				{
					AntiCheatFullConfigSaveRequest payload6;
					try
					{
						payload6 = JsonSerializer.Deserialize<AntiCheatFullConfigSaveRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex6)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid full Anti-Cheat configuration request: " + ex6.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					(bool Success, string Message) result12 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.SaveFullConfigJson(payload6?.Json ?? "") ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-full-config-save").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-full-config", "full editable configuration", result12.Success ? "Success" : result12.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result12.Success ? 200 : 400, new
					{
						success = result12.Success,
						message = result12.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/client-settings")
				{
					AntiCheatClientScanSettingsSaveRequest payload7;
					try
					{
						payload7 = JsonSerializer.Deserialize<AntiCheatClientScanSettingsSaveRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex7)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid Client Integrity scan configuration: " + ex7.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					(bool Success, string Message) result13 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.SaveClientScanSettings(payload7 ?? new AntiCheatClientScanSettingsSaveRequest()) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-client-settings").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-client-settings", $"requireClient={payload7?.RequireClient};modDb={payload7?.ClientModDbVerificationEnabled};hardened={payload7?.RequireHardenedClientCapabilities};hello={payload7?.ClientHelloGraceSeconds}+{payload7?.ClientHelloDisconnectDelaySeconds};startup={payload7?.StartupChallengeTimeoutSeconds}/{payload7?.StartupChallengeHardLimitSeconds};heartbeat={payload7?.HeartbeatTimeoutSeconds};rescan={payload7?.RescanMinutes}", result13.Success ? "Success" : result13.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result13.Success ? 200 : 400, new
					{
						success = result13.Success,
						message = result13.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/packetguard")
				{
					AntiCheatPacketGuardSaveRequest payload8;
					try
					{
						payload8 = JsonSerializer.Deserialize<AntiCheatPacketGuardSaveRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex8)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid PacketGuard configuration: " + ex8.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					(bool Success, string Message) result14 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.SavePacketGuardSettings(payload8 ?? new AntiCheatPacketGuardSaveRequest()) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-packetguard").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-packetguard-save", $"enabled={payload8?.Enabled};playerWarnings={payload8?.PlayerWarningEnabled};noclip={payload8?.NoclipEnabled};movement={payload8?.MovementEnabled};reach={payload8?.ReachEnabled};mining={payload8?.MiningEnabled};history={payload8?.HistoryEnabled}", result14.Success ? "Success" : result14.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result14.Success ? 200 : 400, new
					{
						success = result14.Success,
						message = result14.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/oreshield")
				{
					AntiCheatOreShieldSaveRequest payload9;
					try
					{
						payload9 = JsonSerializer.Deserialize<AntiCheatOreShieldSaveRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex9)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid Ore Shield configuration: " + ex9.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					(bool Success, string Message) result15 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.SaveOreShieldSettings(payload9 ?? new AntiCheatOreShieldSaveRequest()) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-oreshield").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-oreshield-save", $"enabled={payload9?.Enabled};modded={payload9?.ProtectModdedOres};revealUnknown={payload9?.RevealOnUnknownNeighbor};strict={payload9?.StrictOreMatching};cache={payload9?.ChunkCacheEnabled};cacheMiB={payload9?.ChunkCacheMiB};maxChunks={payload9?.MaxCachedChunks};additional={(payload9?.AdditionalOrePatterns?.Count).GetValueOrDefault()};excluded={(payload9?.ExcludedBlockPatterns?.Count).GetValueOrDefault()}", result15.Success ? "Success" : result15.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result15.Success ? 200 : 400, new
					{
						success = result15.Success,
						message = result15.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/rescan")
				{
					string player5 = GetJsonString(request.Body, "player", "").Trim();
					string message = await RunOnMainThreadAsync(() => this.module.AntiCheat?.RequestRescan(player5) ?? "Anti-cheat service is unavailable.", "web-anticheat-rescan").ConfigureAwait(continueOnCapturedContext: false);
					bool success = message.StartsWith("Anti-cheat rescan requested", StringComparison.OrdinalIgnoreCase);
					RecordWebAudit("anticheat-rescan", player5, success ? "Success" : message, remoteText, requestActor);
					await WriteJsonAsync(stream, success ? 200 : 400, new { success, message }, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/evidence/request")
				{
					AntiCheatEvidenceRequest payload10;
					try
					{
						payload10 = JsonSerializer.Deserialize<AntiCheatEvidenceRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex10)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid suspicious package request: " + ex10.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					string player6 = (payload10?.Player ?? "").Trim();
					string modId = (payload10?.ModId ?? "").Trim();
					(bool Success, string Message) result16 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.RequestSuspiciousModPackage(player6, modId, requestActor) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-evidence-request").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-evidence-request", "player=" + player6 + ";mod=" + modId, result16.Success ? "Success" : result16.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result16.Success ? 200 : 400, new
					{
						success = result16.Success,
						message = result16.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/evidence/analyze")
				{
					AntiCheatEvidenceAnalyzeRequest payload11;
					try
					{
						payload11 = JsonSerializer.Deserialize<AntiCheatEvidenceAnalyzeRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex11)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid evidence comparison request: " + ex11.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					string fileName = (payload11?.FileName ?? "").Trim();
					(bool Success, string Message) result17 = this.module.AntiCheat?.AnalyzeEvidencePackageAgainstModDb(fileName) ?? (false, "Anti-cheat service is unavailable.");
					RecordWebAudit("anticheat-evidence-moddb-compare", fileName, result17.Success ? "Success" : result17.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result17.Success ? 200 : 400, new
					{
						success = result17.Success,
						message = result17.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/evidence/settings")
				{
					AntiCheatEvidenceSettingsSaveRequest payload12;
					try
					{
						payload12 = JsonSerializer.Deserialize<AntiCheatEvidenceSettingsSaveRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex12)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid evidence collection settings: " + ex12.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					(bool Success, string Message) result18 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.SaveEvidenceCollectionSettings(payload12 ?? new AntiCheatEvidenceSettingsSaveRequest()) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-evidence-settings").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-evidence-settings", $"enabled={payload12?.Enabled};maxMb={payload12?.MaxMb};timeout={payload12?.TimeoutSeconds};decline={payload12?.DeclineAction};hours={payload12?.DeclineTempBanHours}", result18.Success ? "Success" : result18.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result18.Success ? 200 : 400, new
					{
						success = result18.Success,
						message = result18.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/moddb/approval")
				{
					AntiCheatClientModApprovalRequest payload13;
					try
					{
						payload13 = JsonSerializer.Deserialize<AntiCheatClientModApprovalRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex13)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid client-mod approval request: " + ex13.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					AntiCheatClientModApprovalRequest requestPayload = payload13 ?? new AntiCheatClientModApprovalRequest();
					(bool Success, string Message) result19 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.UpdateApprovedClientModFingerprint(requestPayload, requestActor) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-moddb-approval").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-moddb-approval-" + (requestPayload.Action ?? "approve").Trim().ToLowerInvariant(), string.IsNullOrWhiteSpace(requestPayload.ModId) ? requestPayload.Id : (requestPayload.ModId + "@" + requestPayload.Version), result19.Success ? "Success" : result19.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result19.Success ? 200 : 400, new
					{
						success = result19.Success,
						message = result19.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/moddb/approval/upload")
				{
					PruneAntiCheatApprovalUploads();
					string action3 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
					if (action3 == "start")
					{
						string fileName2 = Path.GetFileName(GetJsonString(request.Body, "fileName", "")).Trim();
						long expectedBytes = GetJsonLong(request.Body, "sizeBytes", 0L);
						string label = GetJsonString(request.Body, "label", "").Trim();
						string notes = GetJsonString(request.Body, "notes", "").Trim();
						if (!Path.GetExtension(fileName2).Equals(".zip", StringComparison.OrdinalIgnoreCase))
						{
							await WriteJsonAsync(stream, 400, new
							{
								success = false,
								message = "Select a Vintage Story mod ZIP file."
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (expectedBytes <= 0 || expectedBytes > 100663296)
						{
							await WriteJsonAsync(stream, 400, new
							{
								success = false,
								message = $"ZIP size must be between 1 byte and {96L} MB."
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else
						{
							string uploadId = Guid.NewGuid().ToString("N");
							string tempPath = Path.Combine(GetAntiCheatApprovalUploadRoot(), uploadId + ".zip");
							try
							{
								using (File.Create(tempPath))
								{
								}
							}
							catch (Exception ex14)
							{
								await WriteJsonAsync(stream, 500, new
								{
									success = false,
									message = "Could not create the temporary fingerprint upload: " + ex14.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							AntiCheatApprovalUploadState state = new AntiCheatApprovalUploadState
							{
								UploadId = uploadId,
								TempPath = tempPath,
								FileName = fileName2,
								ExpectedBytes = expectedBytes,
								Label = ((label.Length > 160) ? label.Substring(0, 160) : label),
								Notes = ((notes.Length > 1200) ? notes.Substring(0, 1200) : notes),
								Actor = requestActor,
								CreatedUtc = DateTime.UtcNow
							};
							antiCheatApprovalUploads[uploadId] = state;
							await WriteJsonAsync(stream, 200, new
							{
								success = true,
								uploadId = uploadId,
								expectedBytes = expectedBytes,
								maxChunkBytes = 73728,
								message = "Upload session created. The ZIP will only be inspected and hashed; it will not be installed or executed."
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
					}
					else
					{
						string uploadIdValue = GetJsonString(request.Body, "uploadId", "").Trim();
						if (uploadIdValue.Length == 0 || !antiCheatApprovalUploads.TryGetValue(uploadIdValue, out AntiCheatApprovalUploadState uploadState))
						{
							await WriteJsonAsync(stream, 404, new
							{
								success = false,
								message = "Fingerprint upload session was not found or expired."
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (!string.Equals(uploadState.Actor, requestActor, StringComparison.OrdinalIgnoreCase))
						{
							await WriteJsonAsync(stream, 403, new
							{
								success = false,
								message = "This upload session belongs to a different WebPanel account."
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else
						{
							switch (action3)
							{
							case "cancel":
								RemoveAntiCheatApprovalUpload(uploadIdValue);
								await WriteJsonAsync(stream, 200, new
								{
									success = true,
									message = "Fingerprint upload cancelled."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								break;
							case "chunk":
							{
								long offset = GetJsonLong(request.Body, "offset", -1L);
								string dataBase64 = GetJsonString(request.Body, "dataBase64", "");
								byte[] bytes2;
								try
								{
									bytes2 = Convert.FromBase64String(dataBase64);
								}
								catch (FormatException)
								{
									await WriteJsonAsync(stream, 400, new
									{
										success = false,
										message = "Upload chunk was not valid base64."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									break;
								}
								if (bytes2.Length == 0 || bytes2.Length > 73728)
								{
									await WriteJsonAsync(stream, 400, new
									{
										success = false,
										message = $"Upload chunks must contain 1 to {73728} raw bytes."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									long received;
									lock (uploadState.Gate)
									{
										if (offset != uploadState.ReceivedBytes)
										{
											received = uploadState.ReceivedBytes;
										}
										else if (uploadState.ReceivedBytes + bytes2.Length > uploadState.ExpectedBytes)
										{
											received = -2L;
										}
										else
										{
											try
											{
												using FileStream fs = new FileStream(uploadState.TempPath, FileMode.Open, FileAccess.Write, FileShare.None);
												fs.Position = uploadState.ReceivedBytes;
												fs.Write(bytes2, 0, bytes2.Length);
												uploadState.ReceivedBytes += bytes2.Length;
												received = uploadState.ReceivedBytes;
											}
											catch
											{
												received = -3L;
											}
										}
									}
									switch (received)
									{
									case -2L:
										RemoveAntiCheatApprovalUpload(uploadIdValue);
										await WriteJsonAsync(stream, 400, new
										{
											success = false,
											message = "Upload exceeded the declared ZIP size and was cancelled."
										}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
										break;
									case -3L:
										RemoveAntiCheatApprovalUpload(uploadIdValue);
										await WriteJsonAsync(stream, 500, new
										{
											success = false,
											message = "Could not write the uploaded ZIP chunk."
										}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
										break;
									default:
										if (received != offset + bytes2.Length)
										{
											await WriteJsonAsync(stream, 409, new
											{
												success = false,
												message = "Upload offset did not match the server state.",
												receivedBytes = received
											}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
										}
										else
										{
											await WriteJsonAsync(stream, 200, new
											{
												success = true,
												receivedBytes = received,
												expectedBytes = uploadState.ExpectedBytes,
												complete = (received == uploadState.ExpectedBytes)
											}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
										}
										break;
									}
								}
								break;
							}
							case "commit":
								lock (uploadState.Gate)
								{
									if (uploadState.ReceivedBytes != uploadState.ExpectedBytes)
									{
										uploadState.ReceivedBytes = Math.Max(0L, uploadState.ReceivedBytes);
									}
								}
								if (uploadState.ReceivedBytes != uploadState.ExpectedBytes)
								{
									await WriteJsonAsync(stream, 409, new
									{
										success = false,
										message = "The ZIP upload is incomplete.",
										receivedBytes = uploadState.ReceivedBytes,
										expectedBytes = uploadState.ExpectedBytes
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									AntiCheatService antiCheat = this.module.AntiCheat;
									if (antiCheat == null)
									{
										RemoveAntiCheatApprovalUpload(uploadIdValue);
										await WriteJsonAsync(stream, 503, new
										{
											success = false,
											message = "Anti-cheat service is unavailable."
										}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									}
									else
									{
										(bool Success, string Message, AntiCheatClientModApprovalRequest? Approval) inspection = antiCheat.InspectClientModPackageForApproval(uploadState.TempPath, uploadState.FileName, uploadState.Label, uploadState.Notes);
										if (!inspection.Success || inspection.Approval == null)
										{
											RemoveAntiCheatApprovalUpload(uploadIdValue);
											RecordWebAudit("anticheat-moddb-upload-rejected", uploadState.FileName, inspection.Message, remoteText, requestActor);
											await WriteJsonAsync(stream, 400, new
											{
												success = false,
												message = inspection.Message
											}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
										}
										else
										{
											AntiCheatClientModApprovalRequest approval = inspection.Approval;
											(bool Success, string Message) result20 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.UpdateApprovedClientModFingerprint(approval, requestActor) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-moddb-upload-approval").ConfigureAwait(continueOnCapturedContext: false);
											RemoveAntiCheatApprovalUpload(uploadIdValue);
											RecordWebAudit("anticheat-moddb-upload-approval", $"{approval.ModId}@{approval.Version};file={uploadState.FileName};bytes={uploadState.ExpectedBytes}", result20.Success ? "Success" : result20.Message, remoteText, requestActor);
											await WriteJsonAsync(stream, result20.Success ? 200 : 400, new
											{
												success = result20.Success,
												message = result20.Message,
												modId = approval.ModId,
												version = approval.Version,
												label = approval.Label,
												contentSha256 = approval.ContentSha256,
												packageSha256 = approval.PackageSha256,
												inspected = inspection.Message
											}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
										}
									}
								}
								break;
							default:
								await WriteJsonAsync(stream, 400, new
								{
									success = false,
									message = "Unknown fingerprint upload action."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								break;
							}
						}
					}
				}
				else if (request.Method == "POST" && path == "/api/anticheat/moddb/resolve-all")
				{
					bool forceRefresh = GetJsonBool(request.Body, "forceRefresh", fallback: false);
					(bool Success, string Message, int UniqueTargets, int Queued, int FreshCached, int AlreadyPending) result21 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.ResolveAllClientModDbFingerprints(forceRefresh) ?? (false, "Anti-cheat service is unavailable.", 0, 0, 0, 0), "web-anticheat-moddb-resolve-all").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-moddb-resolve-all", forceRefresh ? "force-refresh" : "resolve-missing", result21.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result21.Success ? 200 : 400, new
					{
						success = result21.Success,
						message = result21.Message,
						uniqueTargets = result21.UniqueTargets,
						queued = result21.Queued,
						freshCached = result21.FreshCached,
						alreadyPending = result21.AlreadyPending
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/blacklist")
				{
					string action4 = GetJsonString(request.Body, "action", "").Trim();
					string modId2 = GetJsonString(request.Body, "modId", "").Trim();
					string sourcePlayer = GetJsonString(request.Body, "player", "").Trim();
					(bool Success, string Message) result22 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.UpdateForbiddenMod(action4, modId2, sourcePlayer) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-blacklist").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-blacklist-" + (string.IsNullOrWhiteSpace(action4) ? "unknown" : action4.ToLowerInvariant()), modId2, result22.Success ? "Success" : result22.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result22.Success ? 200 : 400, new
					{
						success = result22.Success,
						message = result22.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/blacklist/profile")
				{
					string action5 = GetJsonString(request.Body, "action", "").Trim();
					string profileId = GetJsonString(request.Body, "profileId", "").Trim();
					string name = GetJsonString(request.Body, "name", "").Trim();
					(bool Success, string Message, string ProfileId) result23 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.UpdateBlacklistProfile(action5, profileId, name) ?? (false, "Anti-cheat service is unavailable.", ""), "web-anticheat-blacklist-profile").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-blacklist-profile-" + (string.IsNullOrWhiteSpace(action5) ? "unknown" : action5.ToLowerInvariant()), string.IsNullOrWhiteSpace(name) ? profileId : name, result23.Success ? "Success" : result23.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result23.Success ? 200 : 400, new
					{
						success = result23.Success,
						message = result23.Message,
						profileId = result23.ProfileId
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/blacklist/resolve")
				{
					string modId3 = GetJsonString(request.Body, "modId", "").Trim().ToLowerInvariant();
					if (!(await RunOnMainThreadAsync(() => this.module.AntiCheat?.Config.ForbiddenModIds.Any((string x) => string.Equals(x?.Trim(), modId3, StringComparison.OrdinalIgnoreCase)) ?? false, "web-anticheat-blacklist-resolve-check").ConfigureAwait(continueOnCapturedContext: false)))
					{
						RecordWebAudit("anticheat-blacklist-resolve", modId3, "Add the mod ID to the blacklist before resolving its Mod DB fingerprint.", remoteText, requestActor);
						await WriteJsonAsync(stream, 400, new
						{
							success = false,
							message = "Add the mod ID to the blacklist before resolving its Mod DB fingerprint."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					else
					{
						AntiCheatModDbFingerprintResolution resolution = await AntiCheatModDbFingerprintResolver.ResolveAsync(releaseDepth: await RunOnMainThreadAsync(() => this.module.AntiCheat?.GetFingerprintReleaseDepth() ?? 6, "web-anticheat-blacklist-release-depth").ConfigureAwait(continueOnCapturedContext: false), modId: modId3, targetGameVersion: "1.22.7", cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						if (!resolution.Success)
						{
							RecordWebAudit("anticheat-blacklist-resolve", modId3, resolution.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, 502, new
							{
								success = false,
								message = resolution.Message
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else
						{
							(bool Success, string Message) result24 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.ApplyResolvedForbiddenFingerprint(resolution) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-blacklist-resolve-apply").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("anticheat-blacklist-resolve", modId3, result24.Success ? ("Success: " + resolution.Version) : result24.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result24.Success ? 200 : 400, new
							{
								success = result24.Success,
								message = result24.Message,
								modId = resolution.ModId,
								version = resolution.Version,
								fileName = resolution.FileName,
								packageSha256 = resolution.PackageSha256,
								contentSha256 = resolution.ContentSha256,
								assemblyCount = resolution.AssemblySha256s.Count,
								releaseCount = resolution.Releases.Count,
								versions = resolution.Releases.Select((AntiCheatModDbReleaseFingerprintResolution x) => x.Version).ToList()
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
					}
				}
				else if (request.Method == "POST" && path == "/api/anticheat/blacklist/fingerprint-settings")
				{
					int releaseDepth = 6;
					try
					{
						using JsonDocument doc = JsonDocument.Parse(request.Body);
						if (doc.RootElement.TryGetProperty("releaseDepth", out var depth) && depth.TryGetInt32(out var parsed))
						{
							releaseDepth = parsed;
						}
					}
					catch (Exception ex16)
					{
						await WriteJsonAsync(stream, 400, new
						{
							success = false,
							message = "Invalid fingerprint settings: " + ex16.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					(bool Success, string Message) result25 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.UpdateFingerprintReleaseDepth(releaseDepth) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-fingerprint-settings").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-fingerprint-depth", releaseDepth.ToString(), result25.Success ? "Success" : result25.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result25.Success ? 200 : 400, new
					{
						success = result25.Success,
						message = result25.Message,
						releaseDepth = Math.Clamp(releaseDepth, 1, 20)
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/blacklist/manual")
				{
					AntiCheatManualFingerprintSaveRequest payload14;
					try
					{
						payload14 = JsonSerializer.Deserialize<AntiCheatManualFingerprintSaveRequest>(request.Body, jsonOptions) ?? new AntiCheatManualFingerprintSaveRequest();
					}
					catch (Exception ex17)
					{
						await WriteJsonAsync(stream, 400, new
						{
							success = false,
							message = "Invalid manual fingerprint payload: " + ex17.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					(bool Success, string Message) result26 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.UpdateManualForbiddenFingerprint(payload14) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-blacklist-manual").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-blacklist-manual-" + (payload14.Action ?? "save").Trim().ToLowerInvariant(), payload14.ModId, result26.Success ? "Success" : result26.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result26.Success ? 200 : 400, new
					{
						success = result26.Success,
						message = result26.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/integrity-policy")
				{
					string action6 = GetJsonString(request.Body, "action", "").Trim();
					string hash = GetJsonString(request.Body, "hash", "").Trim();
					bool? requireApproved = null;
					try
					{
						using JsonDocument doc2 = JsonDocument.Parse(request.Body);
						if (doc2.RootElement.TryGetProperty("requireApproved", out var req) && (req.ValueKind == JsonValueKind.True || req.ValueKind == JsonValueKind.False))
						{
							requireApproved = req.GetBoolean();
						}
					}
					catch
					{
					}
					(bool Success, string Message) result27 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.UpdateClientIntegrityPolicy(action6, hash, requireApproved) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-integrity-policy").ConfigureAwait(continueOnCapturedContext: false);
					WebDashboardService webDashboardService3 = this;
					string text2 = action6;
					string text3;
					if (hash.Length <= 0)
					{
						text3 = "";
					}
					else
					{
						string text = hash;
						text3 = ":" + text.Substring(0, Math.Min(12, hash.Length));
					}
					webDashboardService3.RecordWebAudit("anticheat-integrity-policy", text2 + text3, result27.Success ? "Success" : result27.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result27.Success ? 200 : 400, new
					{
						success = result27.Success,
						message = result27.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/anticheat/consequences")
				{
					AntiCheatConsequenceSaveRequest payload15;
					try
					{
						payload15 = JsonSerializer.Deserialize<AntiCheatConsequenceSaveRequest>(request.Body, jsonOptions);
					}
					catch (Exception ex18)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid consequence configuration: " + ex18.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					(bool Success, string Message) result28 = await RunOnMainThreadAsync(() => this.module.AntiCheat?.SaveConsequenceRules(payload15?.Rules, payload15?.ApplyCurrent ?? false) ?? (false, "Anti-cheat service is unavailable."), "web-anticheat-consequences").ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("anticheat-consequences-save", $"rules={(payload15?.Rules?.Count).GetValueOrDefault()};applyCurrent={payload15?.ApplyCurrent ?? false}", result28.Success ? "Success" : result28.Message, remoteText, requestActor);
					await WriteJsonAsync(stream, result28.Success ? 200 : 400, new
					{
						success = result28.Success,
						message = result28.Message
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "GET" && path == "/api/customization")
				{
					await WriteJsonAsync(stream, 200, new
					{
						available = true,
						panelName = suiteCustomization.PanelName,
						headerSubtitle = suiteCustomization.HeaderSubtitle,
						loginKicker = suiteCustomization.LoginKicker,
						loginSubtitle = suiteCustomization.LoginSubtitle,
						dashboardTitle = suiteCustomization.DashboardTitle,
						dashboardSubtitle = suiteCustomization.DashboardSubtitle,
						searchPlaceholder = suiteCustomization.SearchPlaceholder,
						sidebarFooterText = suiteCustomization.SidebarFooterText,
						accentHex = suiteCustomization.AccentHex,
						theme = suiteCustomization.Theme,
						backgroundHex = suiteCustomization.BackgroundHex,
						panelHex = suiteCustomization.PanelHex,
						textHex = suiteCustomization.TextHex,
						logoUrl = suiteCustomization.LogoUrl,
						reduceMotion = suiteCustomization.ReduceMotion,
						surfaceTexture = suiteCustomization.SurfaceTexture,
						textureStrength = suiteCustomization.TextureStrength,
						showModuleIcons = suiteCustomization.ShowModuleIcons,
						highContrastBorders = suiteCustomization.HighContrastBorders,
						showBuildId = suiteCustomization.ShowBuildId,
						showHeaderLogo = suiteCustomization.ShowHeaderLogo,
						compactLayout = suiteCustomization.CompactLayout,
						sidebarWidth = suiteCustomization.SidebarWidth,
						cardRadius = suiteCustomization.CardRadius,
						showStatusBar = suiteCustomization.ShowStatusBar,
						showModuleStatus = suiteCustomization.ShowModuleStatus,
						showDashboardAttention = suiteCustomization.ShowDashboardAttention,
						showDashboardOperations = suiteCustomization.ShowDashboardOperations,
						buildId = "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1"
					}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/customization")
				{
					SuiteCustomizationConfig payload16;
					try
					{
						payload16 = JsonSerializer.Deserialize<SuiteCustomizationConfig>(request.Body, jsonOptions);
					}
					catch (Exception ex19)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid Suite customization settings: " + ex19.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					if (payload16 == null)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Suite customization settings are required."
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					else
					{
						payload16.Clamp();
						SuiteConfigStorage.Store(api, payload16, "NewfiesSuiteCustomization.json");
						suiteCustomization = payload16;
						RecordWebAudit("suite-customization", "panel=" + suiteCustomization.PanelName + ";accent=" + suiteCustomization.AccentHex, "Success", remoteText, requestActor);
						await WriteJsonAsync(stream, 200, new
						{
							success = true,
							message = "Suite customization saved and applied live.",
							customization = new
							{
								panelName = suiteCustomization.PanelName,
								headerSubtitle = suiteCustomization.HeaderSubtitle,
								loginKicker = suiteCustomization.LoginKicker,
								loginSubtitle = suiteCustomization.LoginSubtitle,
								dashboardTitle = suiteCustomization.DashboardTitle,
								dashboardSubtitle = suiteCustomization.DashboardSubtitle,
								searchPlaceholder = suiteCustomization.SearchPlaceholder,
								sidebarFooterText = suiteCustomization.SidebarFooterText,
								accentHex = suiteCustomization.AccentHex,
								theme = suiteCustomization.Theme,
								backgroundHex = suiteCustomization.BackgroundHex,
								panelHex = suiteCustomization.PanelHex,
								textHex = suiteCustomization.TextHex,
								logoUrl = suiteCustomization.LogoUrl,
								reduceMotion = suiteCustomization.ReduceMotion,
								surfaceTexture = suiteCustomization.SurfaceTexture,
								textureStrength = suiteCustomization.TextureStrength,
								showModuleIcons = suiteCustomization.ShowModuleIcons,
								highContrastBorders = suiteCustomization.HighContrastBorders,
								showBuildId = suiteCustomization.ShowBuildId,
								showHeaderLogo = suiteCustomization.ShowHeaderLogo,
								compactLayout = suiteCustomization.CompactLayout,
								sidebarWidth = suiteCustomization.SidebarWidth,
								cardRadius = suiteCustomization.CardRadius,
								showStatusBar = suiteCustomization.ShowStatusBar,
								showModuleStatus = suiteCustomization.ShowModuleStatus,
								showDashboardAttention = suiteCustomization.ShowDashboardAttention,
								showDashboardOperations = suiteCustomization.ShowDashboardOperations,
								buildId = "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1"
							}
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				else if (request.Method == "GET" && path == "/api/community-motd")
				{
					await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => CommunityMotdControlBridge.Request(api), "web-community-motd-get").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else if (request.Method == "POST" && path == "/api/community-motd")
				{
					string action7 = "save";
					string configJson = "";
					try
					{
						using JsonDocument doc3 = JsonDocument.Parse(request.Body);
						if (doc3.RootElement.TryGetProperty("action", out var actionNode) && actionNode.ValueKind == JsonValueKind.String)
						{
							action7 = (actionNode.GetString() ?? "save").Trim().ToLowerInvariant();
						}
						if (doc3.RootElement.TryGetProperty("config", out var configNode))
						{
							configJson = configNode.GetRawText();
						}
					}
					catch (Exception ex20)
					{
						await WriteJsonAsync(stream, 400, new
						{
							error = "Invalid Community MOTD request: " + ex20.Message
						}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						return;
					}
					if (1 == 0)
					{
					}
					string text;
					switch (action7)
					{
					case "save":
					case "apply":
						text = "apply";
						break;
					case "reload":
						text = "reload";
						break;
					case "resetseen":
					case "reset-seen":
						text = "resetseen";
						break;
					default:
						text = action7;
						break;
					}
					if (1 == 0)
					{
					}
					string operation = text;
					CommunityMotdControlSnapshot result29 = await RunOnMainThreadAsync(() => CommunityMotdControlBridge.Request(api, operation, configJson), "web-community-motd-" + operation).ConfigureAwait(continueOnCapturedContext: false);
					RecordWebAudit("community-motd-" + operation, result29.Version, result29.Success ? "Success" : result29.Message, remoteText, requestActor);
					WebDashboardService webDashboardService4 = this;
					NetworkStream stream2 = stream;
					int statusCode;
					if (result29.Success)
					{
						statusCode = 200;
					}
					else
					{
						statusCode = (result29.Available ? 400 : 503);
					}
					await webDashboardService4.WriteJsonAsync(stream2, statusCode, result29, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					bool flag;
					switch (path)
					{
					case "/api/roles/effective":
					case "/api/roles/preview":
					case "/api/roles/batch":
						flag = true;
						break;
					default:
						flag = false;
						break;
					}
					if (flag)
					{
						RoleManagementService roles = new RoleManagementService(api, () => this.module.PlayerCommands?.Config, () => this.module.PlayerList);
						object payload17;
						try
						{
							if (request.Method == "GET" && path == "/api/roles/effective")
							{
								payload17 = await RunOnMainThreadAsync(() => roles.PreviewPlayer(GetQuery(request, "player", ""), GetQuery(request, "role", "")), "web-role-effective", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
							}
							else if (request.Method == "POST" && path == "/api/roles/preview")
							{
								RoleEditorRequest draft = JsonSerializer.Deserialize<RoleEditorRequest>(request.Body, jsonOptions);
								payload17 = await RunOnMainThreadAsync(() => roles.PreviewDefinition(draft), "web-role-preview", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								if (!(request.Method == "POST") || !(path == "/api/roles/batch"))
								{
									await WriteJsonAsync(stream, 405, new
									{
										error = "Method not allowed."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									return;
								}
								RoleBatchRequest batch = JsonSerializer.Deserialize<RoleBatchRequest>(request.Body, jsonOptions);
								payload17 = await RunOnMainThreadAsync(() => roles.ChangeBatch(batch), "web-role-batch", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
								WebDashboardService webDashboardService5 = this;
								string detail2 = $"{((batch?.Preview ?? false) ? "Preview" : "Assignment")}; target={batch?.RoleCode}; players={string.Join(",", batch?.Players.Select((RolePlayerRequest p) => p.PlayerUid) ?? Array.Empty<string>())}";
								object obj6 = payload17.GetType().GetProperty("success")?.GetValue(payload17);
								webDashboardService5.RecordWebAudit("roles-batch", detail2, (obj6 is bool && (bool)obj6) ? "Success" : "Failed", remoteText, requestActor);
							}
						}
						catch (JsonException)
						{
							await WriteJsonAsync(stream, 400, new
							{
								error = "Invalid role request."
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							return;
						}
						await WriteJsonAsync(stream, 200, payload17, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					else
					{
						if (path == "/api/roles" || path == "/api/roles/players" || path == "/api/roles/player")
						{
							RoleManagementService roles2 = new RoleManagementService(api, () => this.module.PlayerCommands?.Config, () => this.module.PlayerList);
							if (request.Method == "GET" && path == "/api/roles")
							{
								await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(roles2.GetSnapshot, "web-roles-read", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							if (request.Method == "GET" && path == "/api/roles/players")
							{
								string search = GetQuery(request, "search", "");
								await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => roles2.GetPlayers(search, 100), "web-roles-players", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							if (request.Method == "POST" && path != "/api/roles/players")
							{
								RoleEditorResult result30;
								try
								{
									if (path == "/api/roles/player")
									{
										RolePlayerRequest payload18 = JsonSerializer.Deserialize<RolePlayerRequest>(request.Body, jsonOptions);
										result30 = await RunOnMainThreadAsync(() => roles2.SetPlayerRole(payload18), "web-roles-assign", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
									}
									else
									{
										RoleEditorRequest payload19 = JsonSerializer.Deserialize<RoleEditorRequest>(request.Body, jsonOptions);
										result30 = await RunOnMainThreadAsync(() => roles2.Change(payload19), "web-roles-edit", skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
									}
								}
								catch (JsonException ex22)
								{
									await WriteJsonAsync(stream, 400, new
									{
										success = false,
										message = "Invalid role request: " + ex22.Message
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									return;
								}
								RecordWebAudit("roles-change", path, result30.Success ? "Success" : result30.Message, remoteText, requestActor);
								WebDashboardService webDashboardService6 = this;
								NetworkStream stream3 = stream;
								int statusCode2;
								if (result30.Success)
								{
									statusCode2 = 200;
								}
								else
								{
									statusCode2 = (result30.Conflict ? 409 : 400);
								}
								await webDashboardService6.WriteJsonAsync(stream3, statusCode2, result30, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
						}
						if (request.Method == "GET" && path == "/api/playercommands")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => this.module.PlayerCommands?.GetSnapshot() ?? new PlayerCommandSnapshot
							{
								Available = false
							}, "web-playercommands-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/playercommands/permissions")
						{
							PermissionsManagerSnapshot payload20 = await RunOnMainThreadAsync(() => this.module.PlayerCommands?.GetPermissionsSnapshot() ?? new PermissionsManagerSnapshot(), "web-playercommands-permissions").ConfigureAwait(continueOnCapturedContext: false);
							DecorateSupporterNicknames(payload20);
							await WriteJsonAsync(stream, 200, payload20, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/playercommands/permissions")
						{
							PermissionChangeRequest payload21;
							try
							{
								payload21 = JsonSerializer.Deserialize<PermissionChangeRequest>(request.Body, jsonOptions);
							}
							catch (Exception ex23)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Invalid permission request: " + ex23.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							PermissionChangeResult result31 = await RunOnMainThreadAsync(() => this.module.PlayerCommands?.ChangePermission(payload21) ?? new PermissionChangeResult
							{
								Success = false,
								Message = "Player Commands feature unavailable."
							}, "web-playercommands-permission-change").ConfigureAwait(continueOnCapturedContext: false);
							DecorateSupporterNicknames(result31.Snapshot);
							RecordWebAudit("player-permissions", payload21?.Action + ":" + payload21?.Value, result31.Success ? "Success" : result31.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result31.Success ? 200 : 400, new
							{
								success = result31.Success,
								message = result31.Message,
								permissions = result31.Snapshot
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/playercommands/command")
						{
							string key2 = GetJsonString(request.Body, "key", "").Trim().ToLowerInvariant();
							bool enabled2 = GetJsonBool(request.Body, "enabled", fallback: true);
							PlayerCommandSaveResult result32 = await RunOnMainThreadAsync(() => this.module.PlayerCommands?.SetCommandEnabled(key2, enabled2) ?? new PlayerCommandSaveResult
							{
								Success = false,
								Message = "Player Commands feature is unavailable.",
								Snapshot = new PlayerCommandSnapshot
								{
									Available = false
								}
							}, "web-playercommands-command-toggle").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("playercommands-command", key2 + "=" + (enabled2 ? "on" : "off"), result32.Success ? "Success" : result32.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result32.Success ? 200 : 400, new
							{
								success = result32.Success,
								message = result32.Message,
								playerCommands = result32.Snapshot
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/playercommands/settings")
						{
							PlayerCommandSettingsRequest payload22;
							try
							{
								payload22 = JsonSerializer.Deserialize<PlayerCommandSettingsRequest>(request.Body, jsonOptions);
							}
							catch (Exception ex24)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Invalid player command settings: " + ex24.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							PlayerCommandSaveResult result33 = await RunOnMainThreadAsync(() => this.module.PlayerCommands?.SaveSettings(payload22) ?? new PlayerCommandSaveResult
							{
								Success = false,
								Message = "Player Commands feature is unavailable. Enable the playercommands feature gate and restart the server.",
								Snapshot = new PlayerCommandSnapshot
								{
									Available = false
								}
							}, "web-playercommands-settings").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("playercommands-settings", "spawn/msg/reply/starterkit/warps/back/homes/t2p/t2m/rtp", result33.Success ? "Success" : result33.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result33.Success ? 200 : 400, new
							{
								success = result33.Success,
								message = result33.Message,
								playerCommands = result33.Snapshot
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/warps")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => this.module.PlayerCommands?.Warps.GetSnapshot() ?? new WarpSnapshot
							{
								Available = false
							}, "web-warps-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/warps/settings")
						{
							WarpSettingsRequest payload23;
							try
							{
								payload23 = JsonSerializer.Deserialize<WarpSettingsRequest>(request.Body, jsonOptions);
							}
							catch (Exception ex25)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Invalid warp settings: " + ex25.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							WarpMutationResult result34 = await RunOnMainThreadAsync(() => this.module.PlayerCommands?.Warps.SaveDefaults(payload23) ?? new WarpMutationResult
							{
								Success = false,
								Message = "Player Commands feature is unavailable.",
								Snapshot = new WarpSnapshot
								{
									Available = false
								}
							}, "web-warps-settings").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("warps-settings", "defaults", result34.Success ? "Success" : result34.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result34.Success ? 200 : 400, result34, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/warps/upsert")
						{
							WarpMutationRequest payload24;
							try
							{
								payload24 = JsonSerializer.Deserialize<WarpMutationRequest>(request.Body, jsonOptions);
							}
							catch (Exception ex26)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Invalid warp: " + ex26.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							WarpMutationResult result35 = await RunOnMainThreadAsync(() => this.module.PlayerCommands?.Warps.Upsert(payload24) ?? new WarpMutationResult
							{
								Success = false,
								Message = "Player Commands feature is unavailable.",
								Snapshot = new WarpSnapshot
								{
									Available = false
								}
							}, "web-warps-upsert").ConfigureAwait(continueOnCapturedContext: false);
							string warpAuditDetail = ((payload24 == null) ? "" : $"{payload24.Name}; access={payload24.AccessMode}; roles={string.Join(',', payload24.AllowedRoles ?? new List<string>())}; privileges={string.Join(',', payload24.RequiredPrivileges ?? new List<string>())}");
							RecordWebAudit("warps-upsert", warpAuditDetail, result35.Success ? "Success" : result35.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result35.Success ? 200 : 400, result35, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/warps/delete")
						{
							string name2 = GetJsonString(request.Body, "name", "").Trim();
							WarpMutationResult result36 = await RunOnMainThreadAsync(() => this.module.PlayerCommands?.Warps.Delete(name2) ?? new WarpMutationResult
							{
								Success = false,
								Message = "Player Commands feature is unavailable.",
								Snapshot = new WarpSnapshot
								{
									Available = false
								}
							}, "web-warps-delete").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("warps-delete", name2, result36.Success ? "Success" : result36.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result36.Success ? 200 : 400, result36, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/warps/reload")
						{
							WarpMutationResult result37 = await RunOnMainThreadAsync(() => this.module.PlayerCommands?.Warps.Reload() ?? new WarpMutationResult
							{
								Success = false,
								Message = "Player Commands feature is unavailable.",
								Snapshot = new WarpSnapshot
								{
									Available = false
								}
							}, "web-warps-reload").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("warps-reload", "NewfiesWarps.json", result37.Success ? "Success" : result37.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result37.Success ? 200 : 400, result37, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/discord")
						{
							await WriteJsonAsync(stream, 200, await GetCachedMainThreadReadAsync("discord-status", 5000, () => DiscordControlBridge.Request(api), "web-discord-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/discord/settings")
						{
							string key3 = GetJsonString(request.Body, "key", "").Trim();
							bool boolValue = GetJsonBool(request.Body, "boolValue", fallback: false);
							int intValue = GetJsonInt(request.Body, "intValue", 0);
							string stringValue = GetJsonString(request.Body, "stringValue", "");
							string target = GetJsonString(request.Body, "target", "");
							DiscordControlSnapshot payload25 = await RunOnMainThreadAsync(() => DiscordControlBridge.Request(api, "set", key3, boolValue, intValue, stringValue, target), "web-discord-settings").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("discord-settings", key3, payload25.Success ? "Success" : payload25.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, payload25.Success ? 200 : 400, payload25, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/discord/test")
						{
							string target2 = GetJsonString(request.Body, "target", "chat").Trim();
							DiscordControlSnapshot payload26 = await RunOnMainThreadAsync(() => DiscordControlBridge.Request(api, "test", "", boolValue: false, 0, "", target2), "web-discord-test").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("discord-test", target2, payload26.Success ? "Success" : payload26.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, payload26.Success ? 200 : 400, payload26, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/restarts")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => this.module.AutomatedRestarts?.GetSnapshot() ?? new AutomatedRestartSnapshot
							{
								Available = false
							}, "web-automated-restarts").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/restarts/settings")
						{
							AutomatedRestartSettingsRequest settings = null;
							try
							{
								settings = JsonSerializer.Deserialize<AutomatedRestartSettingsRequest>(request.Body, jsonOptions);
							}
							catch (Exception ex27)
							{
								await WriteJsonAsync(stream, 400, new
								{
									success = false,
									message = "Invalid automated restart settings: " + ex27.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							(bool Success, string Message, AutomatedRestartSnapshot Snapshot) result38 = await RunOnMainThreadAsync(() => this.module.AutomatedRestarts?.SaveSettings(settings) ?? (false, "Automated restart service is unavailable.", new AutomatedRestartSnapshot
							{
								Available = false
							}), "web-automated-restarts-save").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("automated-restarts-save", string.Join(",", settings?.RestartTimesUtc ?? new List<string>()), result38.Success ? "Success" : result38.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result38.Success ? 200 : 400, new
							{
								success = result38.Success,
								message = result38.Message,
								restarts = result38.Snapshot
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/restarts/action")
						{
							string action8 = GetJsonString(request.Body, "action", "status").Trim().ToLowerInvariant();
							int customMinutes = Math.Clamp(GetJsonInt(request.Body, "minutes", 10), 1, 1440);
							string customReason = GetJsonString(request.Body, "reason", "Maintenance").Trim();
							(bool Success, string Message, AutomatedRestartSnapshot Snapshot) result39 = await RunOnMainThreadAsync(() =>
							{
								AutomatedRestartService automatedRestarts = this.module.AutomatedRestarts;
								if (automatedRestarts == null)
								{
									return (Success: false, Message: "Automated restart service is unavailable.", Snapshot: new AutomatedRestartSnapshot
									{
										Available = false
									});
								}
								if (1 == 0)
								{
								}
								string text4;
								switch (action8)
								{
								case "enable":
								case "on":
									text4 = automatedRestarts.SetEnabled(enabled: true);
									break;
								case "disable":
								case "off":
									text4 = automatedRestarts.SetEnabled(enabled: false);
									break;
								case "skip":
									text4 = automatedRestarts.SkipNext(requestActor);
									break;
								case "reload":
									text4 = automatedRestarts.Reload();
									break;
								case "schedule-custom":
									text4 = automatedRestarts.ScheduleCustomRestart(customMinutes, customReason, requestActor);
									break;
								case "cancel-custom":
									text4 = automatedRestarts.CancelCustomRestart(requestActor);
									break;
								case "status":
									text4 = automatedRestarts.StatusText();
									break;
								default:
									text4 = "Unknown automated restart action.";
									break;
								}
								if (1 == 0)
								{
								}
								string text5 = text4;
								bool item = !text5.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) && !text5.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
								return (Success: item, Message: text5, Snapshot: automatedRestarts.GetSnapshot());
							}, "web-automated-restarts-action").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("automated-restarts-" + action8, "schedule", result39.Success ? "Success" : result39.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result39.Success ? 200 : 400, new
							{
								success = result39.Success,
								message = result39.Message,
								restarts = result39.Snapshot
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/chat-display")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
							{
								ChatDisplaySnapshot chatDisplaySnapshot = this.module.ChatDisplay?.GetSnapshot() ?? new ChatDisplaySnapshot
								{
									Available = false
								};
								PublicPlayerListServer playerList2 = this.module.PlayerList;
								chatDisplaySnapshot.RoleColorRevision = RoleManagementService.ColorRevision(playerList2?.Config);
								chatDisplaySnapshot.UseRoleNameColors = playerList2?.Config.UseRoleNameColors ?? false;
								chatDisplaySnapshot.RoleNameColors = new Dictionary<string, string>(playerList2?.Config.RoleNameColors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
								return chatDisplaySnapshot;
							}, "web-chat-display").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/chat-display/settings")
						{
							ChatDisplaySettingsRequest settings2 = null;
							try
							{
								settings2 = JsonSerializer.Deserialize<ChatDisplaySettingsRequest>(request.Body, jsonOptions);
							}
							catch (Exception ex28)
							{
								await WriteJsonAsync(stream, 400, new
								{
									success = false,
									message = "Invalid chat display settings: " + ex28.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							(bool Success, string Message, ChatDisplaySnapshot Snapshot) result40 = await RunOnMainThreadAsync(() =>
							{
								ChatDisplayService chatDisplay = this.module.ChatDisplay;
								if (chatDisplay == null)
								{
									return (Success: false, Message: "Chat display service is unavailable.", Snapshot: new ChatDisplaySnapshot
									{
										Available = false
									});
								}
								if (settings2?.RoleColorRevision != null && settings2.RoleColorRevision != RoleManagementService.ColorRevision(this.module.PlayerList?.Config))
								{
									return (Success: false, Message: "Shared role name colors changed elsewhere. Reload Chat Display before saving; your draft has been kept.", Snapshot: chatDisplay.GetSnapshot());
								}
								(bool, string, ChatDisplaySnapshot) tuple = chatDisplay.SaveSettings(settings2);
								if (tuple.Item1 && this.module.PlayerList != null)
								{
									this.module.PlayerList.ApplyRoleNameColors(settings2?.UseRoleNameColors ?? false, settings2?.RoleNameColors);
								}
								ChatDisplaySnapshot item = tuple.Item3;
								PublicPlayerListServer playerList2 = this.module.PlayerList;
								item.RoleColorRevision = RoleManagementService.ColorRevision(playerList2?.Config);
								item.UseRoleNameColors = playerList2?.Config.UseRoleNameColors ?? false;
								item.RoleNameColors = new Dictionary<string, string>(playerList2?.Config.RoleNameColors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
								return (Success: tuple.Item1, Message: tuple.Item2, Snapshot: item);
							}, "web-chat-display-save").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("chat-display-save", $"role={settings2?.ShowRoleBeforeName};boldName={settings2?.BoldPlayerName};timestamp={settings2?.ShowTimestampBeforeMessage};roleColors={((settings2?.UseRoleNameColors ?? false) ? (settings2.RoleNameColors?.Count ?? 0) : 0)}", result40.Success ? "Success" : result40.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result40.Success ? 200 : 400, new
							{
								success = result40.Success,
								message = result40.Message,
								chatDisplay = result40.Snapshot
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/chatguard")
						{
							await WriteJsonAsync(stream, 200, BuildChatGuardWebPayload(await RunOnMainThreadAsync(() => ChatGuardControlBridge.Request(api), "web-chatguard-status").ConfigureAwait(continueOnCapturedContext: false)), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/chatguard/settings")
						{
							ChatGuardControlSnapshot result41 = await RunOnMainThreadAsync(() => ChatGuardControlBridge.Request(api, "save", request.Body, "", "", "", 10, permanent: false, "", requestActor), "web-chatguard-settings").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("chatguard-settings", "ChatGuard settings updated.", result41.Success ? "Success" : result41.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result41.Success ? 200 : 400, BuildChatGuardWebPayload(result41), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/chatguard/action")
						{
							string action9 = GetJsonString(request.Body, "action", "").Trim();
							string targetUid = GetJsonString(request.Body, "targetUid", "").Trim();
							string targetName = GetJsonString(request.Body, "targetName", "").Trim();
							string reason2 = GetJsonString(request.Body, "reason", "").Trim();
							int minutes = Math.Clamp(GetJsonInt(request.Body, "minutes", 10), 1, 43200);
							bool permanent = GetJsonBool(request.Body, "permanent", fallback: false);
							ChatGuardControlSnapshot result42 = await RunOnMainThreadAsync(() => ChatGuardControlBridge.Request(api, "action", "", action9, targetUid, targetName, minutes, permanent, reason2, requestActor), "web-chatguard-action").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("chatguard-" + action9.ToLowerInvariant(), string.IsNullOrWhiteSpace(targetName) ? targetUid : targetName, result42.Success ? "Success" : result42.Message, remoteText, requestActor);
							await WriteJsonAsync(stream, result42.Success ? 200 : 400, BuildChatGuardWebPayload(result42), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/chatguard/test")
						{
							string targetName2 = GetJsonString(request.Body, "targetName", "").Trim();
							string testMessage = GetJsonString(request.Body, "message", "");
							if (string.IsNullOrWhiteSpace(testMessage))
							{
								await WriteJsonAsync(stream, 400, new
								{
									success = false,
									message = "Enter a message to test."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								ChatGuardControlSnapshot result43 = await RunOnMainThreadAsync(() => ChatGuardControlBridge.Request(api, "test", "", "", "", targetName2, 10, permanent: false, "", testMessage: testMessage, actor: requestActor), "web-chatguard-test").ConfigureAwait(continueOnCapturedContext: false);
								await WriteJsonAsync(stream, result43.Success ? 200 : 400, BuildChatGuardWebPayload(result43), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/announcements")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => this.module.Announcements?.GetSnapshot() ?? new AnnouncementSnapshot(), "web-announcements").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/announcements/save")
						{
							string message2 = await RunOnMainThreadAsync(() => this.module.Announcements?.SaveFromJson(request.Body, jsonOptions) ?? "Announcements service is unavailable.", "web-announcements-save").ConfigureAwait(continueOnCapturedContext: false);
							bool success2 = !message2.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase) && !message2.StartsWith("Announcement configuration is required", StringComparison.OrdinalIgnoreCase) && !message2.StartsWith("Announcements service is unavailable", StringComparison.OrdinalIgnoreCase);
							AnnouncementSnapshot payload27 = await RunOnMainThreadAsync(() => this.module.Announcements?.GetSnapshot() ?? new AnnouncementSnapshot(), "web-announcements-save-result").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("announcements-save", "schedule", success2 ? "Success" : message2, remoteText, requestActor);
							await WriteJsonAsync(stream, success2 ? 200 : 400, new
							{
								success = success2,
								message = message2,
								announcements = payload27
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/announcements/action")
						{
							string action10 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
							string id3 = GetJsonString(request.Body, "id", "").Trim();
							string message3 = await RunOnMainThreadAsync(() =>
							{
								AnnouncementService announcements = this.module.Announcements;
								if (announcements == null)
								{
									return "Announcements service is unavailable.";
								}
								if (1 == 0)
								{
								}
								string result71;
								switch (action10)
								{
								case "run":
								case "test":
									result71 = announcements.RunNow(id3);
									break;
								case "reset":
									result71 = announcements.ResetRuns(id3);
									break;
								default:
									result71 = "Unknown announcement action.";
									break;
								}
								if (1 == 0)
								{
								}
								return result71;
							}, "web-announcements-action").ConfigureAwait(continueOnCapturedContext: false);
							bool success3 = !message3.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) && !message3.Contains("not found", StringComparison.OrdinalIgnoreCase) && !message3.Contains("unavailable", StringComparison.OrdinalIgnoreCase) && !message3.Contains("empty", StringComparison.OrdinalIgnoreCase);
							RecordWebAudit("announcements-" + action10, id3, success3 ? "Success" : message3, remoteText, requestActor);
							await WriteJsonAsync(stream, success3 ? 200 : 400, new
							{
								success = success3,
								message = message3
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/console")
						{
							int lines = Math.Clamp(GetQueryInt(request, "lines", 350), 50, 1000);
							WebConsoleSnapshot payload28 = this.module.WebConsole?.GetSnapshot(lines, GetQuery(request, "includeChat", "false").Equals("true", StringComparison.OrdinalIgnoreCase)) ?? new WebConsoleSnapshot();
							payload28.BlockEntityAlerts = logIntelligence?.GetBadBlockEntityConsoleAlerts() ?? new BadBlockEntitySnapshot();
							await WriteJsonAsync(stream, 200, payload28, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/leaderboard")
						{
							WebMapLeaderboardSnapshot payload29 = this.module.WebMap?.GetLeaderboardSnapshot() ?? WebMapLeaderboardSnapshot.Disabled("Leaderboard service is unavailable because the Web Map feature is master-disabled.");
							await WriteJsonAsync(stream, 200, payload29, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/webmap")
						{
							WebMapBridgeService webMap = this.module.WebMap;
							WebMapSnapshot payload30 = ((!(webMap?.IsTerrainExportInProgress ?? false)) ? (await RunOnMainThreadAsync(() => webMap?.GetSnapshot(logIntelligence?.GetBadBlockEntitySnapshot()) ?? new WebMapSnapshot
							{
								Note = "Native Web Map service is unavailable."
							}, "web-map-status").ConfigureAwait(continueOnCapturedContext: false)) : webMap.GetSnapshot(null, includeWaypointAudit: true, includeBadBlockSnapshot: false));
							await WriteJsonAsync(stream, 200, payload30, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/webmap/terrain/status")
						{
							WebMapTerrainExportStatus payload31 = this.module.WebMap?.GetTerrainStatusSnapshot() ?? new WebMapTerrainExportStatus
							{
								State = "unavailable",
								Phase = "unavailable",
								Message = "Native Web Map service is unavailable."
							};
							await WriteJsonAsync(stream, 200, payload31, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/webmap/config")
						{
							WebMapConfig incoming7 = new WebMapConfig
							{
								AllowLivePlayerData = GetJsonBool(request.Body, "allowLivePlayerData", fallback: false),
								Enabled = GetJsonBool(request.Body, "enabled", fallback: false),
								BindAddress = GetJsonString(request.Body, "bindAddress", "0.0.0.0"),
								Port = Math.Clamp(GetJsonInt(request.Body, "port", 9466), 1024, 65535),
								PublicUrl = GetJsonString(request.Body, "publicUrl", ""),
								DashboardUrl = GetJsonString(request.Body, "dashboardUrl", ""),
								Title = GetJsonString(request.Body, "title", "Newfie's Server Map"),
								ShowPlayers = GetJsonBool(request.Body, "showPlayers", fallback: false),
								ShowBadBlockEntities = GetJsonBool(request.Body, "showBadBlockEntities", fallback: false),
								ShowActivity = GetJsonBool(request.Body, "showActivity", fallback: false),
								ShowWaypoints = GetJsonBool(request.Body, "showWaypoints", fallback: true),
								ShowLeaderboards = GetJsonBool(request.Body, "showLeaderboards", fallback: true),
								LeaderboardShowPlaytime = GetJsonBool(request.Body, "leaderboardShowPlaytime", fallback: true),
								LeaderboardShowXSkills = GetJsonBool(request.Body, "leaderboardShowXSkills", fallback: true),
								LeaderboardTopPlayers = Math.Clamp(GetJsonInt(request.Body, "leaderboardTopPlayers", 5), 3, 20),
								LeaderboardRefreshSeconds = Math.Clamp(GetJsonInt(request.Body, "leaderboardRefreshSeconds", 60), 30, 300),
								ExportNormalTerrain = GetJsonBool(request.Body, "exportNormalTerrain", fallback: true),
								ExportSepiaTerrain = GetJsonBool(request.Body, "exportSepiaTerrain", fallback: true),
								ExportTopographicTerrain = GetJsonBool(request.Body, "exportTopographicTerrain", fallback: true),
								PlayerMajorWaypointLimit = Math.Clamp(GetJsonInt(request.Body, "playerMajorWaypointLimit", 5), 0, 100),
								PlayerMinorWaypointLimit = Math.Clamp(GetJsonInt(request.Body, "playerMinorWaypointLimit", 10), 0, 100),
								MaximumZoomOutSpanBlocks = Math.Clamp(GetJsonInt(request.Body, "maximumZoomOutSpanBlocks", 50000), 2048, 100000),
								DefaultViewSpanBlocks = Math.Clamp(GetJsonInt(request.Body, "defaultViewSpanBlocks", 8000), 512, 100000),
								ActivityWindowMinutes = Math.Clamp(GetJsonInt(request.Body, "activityWindowMinutes", 60), 5, 1440),
								MaxActivityMarkers = Math.Clamp(GetJsonInt(request.Body, "maxActivityMarkers", 150), 25, 200),
								ExportTerrainOnStart = GetJsonBool(request.Body, "exportTerrainOnStart", fallback: false),
								AutomaticStartupExportEnabled = GetJsonBool(request.Body, "automaticStartupExportEnabled", fallback: false),
								AutomaticStartupExportIntervalDays = Math.Clamp(GetJsonInt(request.Body, "automaticStartupExportIntervalDays", 7), 1, 365),
								LockServerDuringTerrainExport = GetJsonBool(request.Body, "lockServerDuringTerrainExport", fallback: true),
								UseIncrementalTerrainExport = GetJsonBool(request.Body, "useIncrementalTerrainExport", fallback: true),
								TerrainTileSize = Math.Clamp(GetJsonInt(request.Body, "terrainTileSize", 256), 128, 512)
							};
							WebMapConfig saved = await RunOnMainThreadAsync(() => this.module.WebMap?.SaveConfig(incoming7) ?? incoming7, "web-map-config").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("web-map-config", $"{saved.BindAddress}:{saved.Port}", "Success", remoteText, requestActor);
							await WriteJsonAsync(stream, 200, new
							{
								success = true,
								config = saved
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/webmap/export-schedule")
						{
							bool oneShot = GetJsonBool(request.Body, "exportTerrainOnStart", fallback: false);
							bool automaticEnabled = GetJsonBool(request.Body, "automaticStartupExportEnabled", fallback: false);
							int intervalDays = Math.Clamp(GetJsonInt(request.Body, "automaticStartupExportIntervalDays", 7), 1, 365);
							bool useIncremental = GetJsonBool(request.Body, "useIncrementalTerrainExport", fallback: true);
							bool lockDuringExport = GetJsonBool(request.Body, "lockServerDuringTerrainExport", fallback: true);
							WebMapConfig saved2 = await RunOnMainThreadAsync(() => this.module.WebMap?.SaveExportSchedule(oneShot, automaticEnabled, intervalDays, useIncremental, lockDuringExport) ?? new WebMapConfig
							{
								ExportTerrainOnStart = oneShot,
								AutomaticStartupExportEnabled = automaticEnabled,
								AutomaticStartupExportIntervalDays = intervalDays,
								UseIncrementalTerrainExport = useIncremental,
								LockServerDuringTerrainExport = lockDuringExport
							}, "web-map-export-schedule").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("web-map-export-schedule", oneShot ? "next-start-queued" : "next-start-off", "Success", remoteText, requestActor);
							await WriteJsonAsync(stream, 200, new
							{
								success = true,
								config = saved2
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/webmap/terrain/update")
						{
							string message4 = await RunOnMainThreadAsync(() => this.module.WebMap?.UpdateTerrainNow() ?? "Native Web Map service is unavailable.", "web-map-update-terrain").ConfigureAwait(continueOnCapturedContext: false);
							bool success4 = message4.StartsWith("Changed terrain update started", StringComparison.OrdinalIgnoreCase);
							RecordWebAudit("web-map-update-terrain", "terrain", success4 ? "Success" : message4, remoteText, requestActor);
							await WriteJsonAsync(stream, success4 ? 202 : 400, new
							{
								success = success4,
								message = message4
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/webmap/terrain/force")
						{
							string message5 = await RunOnMainThreadAsync(() => this.module.WebMap?.ForceTerrainExportNow() ?? "Native Web Map service is unavailable.", "web-map-force-terrain-export").ConfigureAwait(continueOnCapturedContext: false);
							bool success5 = message5.StartsWith("Full terrain rebuild started", StringComparison.OrdinalIgnoreCase);
							RecordWebAudit("web-map-force-terrain-export", "terrain", success5 ? "Success" : message5, remoteText, requestActor);
							await WriteJsonAsync(stream, success5 ? 202 : 400, new
							{
								success = success5,
								message = message5
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/webmap/waypoint/save")
						{
							WebMapWaypoint incoming8 = new WebMapWaypoint
							{
								Id = GetJsonString(request.Body, "id", ""),
								Name = GetJsonString(request.Body, "name", ""),
								Description = GetJsonString(request.Body, "description", ""),
								Type = GetJsonString(request.Body, "type", "major"),
								X = GetJsonDouble(request.Body, "x", double.NaN),
								Y = GetJsonDouble(request.Body, "y", double.NaN),
								Z = GetJsonDouble(request.Body, "z", double.NaN),
								Dimension = Math.Clamp(GetJsonInt(request.Body, "dimension", 0), -1024, 1024),
								Color = GetJsonString(request.Body, "color", "#38bdf8")
							};
							try
							{
								WebMapWaypoint savedWaypoint = await RunOnMainThreadAsync(() => this.module.WebMap?.SaveWaypoint(incoming8, requestActor) ?? throw new InvalidOperationException("Native Web Map service is unavailable."), "web-map-waypoint-save").ConfigureAwait(continueOnCapturedContext: false);
								RecordWebAudit("web-map-waypoint-save", $"{savedWaypoint.Name} @ {savedWaypoint.X:0.##},{savedWaypoint.Y:0.##},{savedWaypoint.Z:0.##}", "Success", remoteText, requestActor);
								await WriteJsonAsync(stream, 200, new
								{
									success = true,
									waypoint = savedWaypoint
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							catch (InvalidOperationException ex29)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = ex29.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/webmap/waypoint/delete")
						{
							string id4 = GetJsonString(request.Body, "id", "").Trim();
							if (id4.Length == 0)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Waypoint id is required."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else if (!(await RunOnMainThreadAsync(() => this.module.WebMap?.DeleteWaypoint(id4) ?? false, "web-map-waypoint-delete").ConfigureAwait(continueOnCapturedContext: false)))
							{
								await WriteJsonAsync(stream, 404, new
								{
									error = "Waypoint was not found."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								RecordWebAudit("web-map-waypoint-delete", id4, "Success", remoteText, requestActor);
								await WriteJsonAsync(stream, 200, new
								{
									success = true
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/console/input")
						{
							bool enabled3 = GetJsonBool(request.Body, "enabled", fallback: false);
							bool state2 = this.module.WebConsole?.SetInputEnabled(enabled3) ?? false;
							RecordWebAudit("web-console-input", enabled3 ? "enable" : "disable", "Success", remoteText, requestActor);
							await WriteJsonAsync(stream, 200, new
							{
								success = true,
								enabled = state2,
								expiresMinutes = (state2 ? 15 : 0)
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/console/command")
						{
							string input = GetJsonString(request.Body, "command", "").Trim();
							string mode = GetJsonString(request.Body, "mode", "auto").Trim().ToLowerInvariant();
							if (mode != "auto" && mode != "command" && mode != "message")
							{
								mode = "auto";
							}
							if (string.IsNullOrWhiteSpace(input) || input.Length > 1000)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Console input must contain 1-1000 characters."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								WebConsoleService? webConsole = this.module.WebConsole;
								if (webConsole == null || !webConsole.IsInputEnabled())
								{
									await WriteJsonAsync(stream, 403, new
									{
										error = "Web console input is OFF. Enable it first; the enable lease lasts 15 minutes."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									bool executeAsCommand = mode == "command" || (mode == "auto" && input.StartsWith('/'));
									WebConsoleCommandResult result44;
									string auditAction;
									string auditTarget;
									if (executeAsCommand)
									{
										string command = (input.StartsWith('/') ? input.Substring(1).TrimStart() : input);
										if (string.IsNullOrWhiteSpace(command))
										{
											await WriteJsonAsync(stream, 400, new
											{
												error = "Command is empty."
											}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
											return;
										}
										result44 = await ExecuteWebConsoleCommandAsync(command).ConfigureAwait(continueOnCapturedContext: false);
										auditAction = "web-console-command";
										auditTarget = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
									}
									else
									{
										result44 = await ExecuteWebConsoleMessageAsync(input, requestActor).ConfigureAwait(continueOnCapturedContext: false);
										auditAction = "web-console-message";
										auditTarget = $"broadcast ({input.Length} chars)";
									}
									this.module.WebConsole?.TouchInputLease();
									RecordWebAudit(auditAction, auditTarget, result44.Success ? "Success" : result44.Status, remoteText, requestActor);
									await WriteJsonAsync(stream, result44.Success ? 200 : 400, new
									{
										success = result44.Success,
										status = result44.Status,
										message = result44.Message,
										mode = (executeAsCommand ? "command" : "message")
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "GET" && path == "/api/mod-updates")
						{
							await WriteJsonAsync(stream, 200, this.module.ModUpdates?.GetSnapshot() ?? new ModUpdateSnapshot(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/mod-updates/check")
						{
							ModUpdateSnapshot modUpdateSnapshot = ((this.module.ModUpdates != null) ? (await this.module.ModUpdates.CheckAsync(force: true).ConfigureAwait(continueOnCapturedContext: false)) : new ModUpdateSnapshot
							{
								LastError = "Mod update checker is unavailable."
							});
							ModUpdateSnapshot payload32 = modUpdateSnapshot;
							RecordWebAudit("mod-update-check", "official-moddb", string.IsNullOrWhiteSpace(payload32.LastError) ? "Success" : payload32.LastError, remoteText, requestActor);
							await WriteJsonAsync(stream, string.IsNullOrWhiteSpace(payload32.LastError) ? 200 : 502, payload32, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/mod-updates/apply")
						{
							string modId4 = GetJsonString(request.Body, "modId", "").Trim();
							if (string.IsNullOrWhiteSpace(modId4))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "modId is required."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								ModUpdateApplyResult modUpdateApplyResult = ((this.module.ModUpdates != null) ? (await this.module.ModUpdates.ApplyUpdateAsync(modId4).ConfigureAwait(continueOnCapturedContext: false)) : new ModUpdateApplyResult
								{
									ModId = modId4,
									Message = "Mod update checker is unavailable."
								});
								ModUpdateApplyResult payload33 = modUpdateApplyResult;
								RecordWebAudit("mod-update-stage", modId4, payload33.Success ? $"Staged {payload33.InstalledVersion} -> {payload33.NewVersion}; restart required" : payload33.Message, remoteText, requestActor);
								await WriteJsonAsync(stream, payload33.Success ? 200 : 400, payload33, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/reports")
						{
							string status = GetQuery(request, "status", "all");
							int limit2 = Math.Clamp(GetQueryInt(request, "limit", 500), 1, 1000);
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => this.module.Reports?.GetSnapshot(status, limit2) ?? new PlayerReportSnapshot(), "web-reports").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/reports/detail")
						{
							long id5 = (long.TryParse(GetQuery(request, "id", "0"), out var parsedReportId) ? parsedReportId : 0);
							int offset2 = Math.Clamp(GetQueryInt(request, "offset", 0), 0, 1000000);
							int limit3 = Math.Clamp(GetQueryInt(request, "limit", 100), 1, 100);
							PlayerReportRecord report = await RunOnMainThreadAsync(() => this.module.Reports?.GetReport(id5, offset2, limit3), "web-report-detail").ConfigureAwait(continueOnCapturedContext: false);
							if (report == null)
							{
								await WriteJsonAsync(stream, 404, new
								{
									error = "Report was not found."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								await WriteJsonAsync(stream, 200, report, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/reports/action")
						{
							long id6 = GetJsonLong(request.Body, "id", 0L);
							string action11 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
							string value = GetJsonString(request.Body, "value", "").Trim();
							string note = GetJsonString(request.Body, "note", "").Trim();
							if (id6 <= 0 || string.IsNullOrWhiteSpace(action11))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Report id and action are required."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								string actor = requestActor;
								string message6 = await RunOnMainThreadAsync(() =>
								{
									ReportsService reports = this.module.Reports;
									if (reports == null)
									{
										return "Reports service is unavailable.";
									}
									if (1 == 0)
									{
									}
									string result71 = action11 switch
									{
										"status" => reports.SetStatus(id6, value, actor, note), 
										"investigate" => reports.SetStatus(id6, "investigating", actor, note), 
										"resolve" => reports.SetStatus(id6, "resolved", actor, note), 
										"reopen" => reports.SetStatus(id6, "open", actor, note), 
										"assign" => reports.Assign(id6, value, actor), 
										"unassign" => reports.Assign(id6, "", actor), 
										"note" => reports.AddNote(id6, actor, (note.Length > 0) ? note : value), 
										_ => "Unknown report action.", 
									};
									if (1 == 0)
									{
									}
									return result71;
								}, "web-report-action").ConfigureAwait(continueOnCapturedContext: false);
								PlayerReportSnapshot refreshed = await RunOnMainThreadAsync(() => this.module.Reports?.GetSnapshot() ?? new PlayerReportSnapshot(), "web-report-action-result").ConfigureAwait(continueOnCapturedContext: false);
								bool reportActionSucceeded = !message6.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) && !message6.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) && !message6.StartsWith("Reports service is unavailable", StringComparison.OrdinalIgnoreCase) && !message6.Contains("was not found", StringComparison.OrdinalIgnoreCase) && !message6.StartsWith("Status must", StringComparison.OrdinalIgnoreCase) && !message6.StartsWith("Note text is required", StringComparison.OrdinalIgnoreCase);
								await WriteJsonAsync(stream, 200, new
								{
									success = reportActionSucceeded,
									message = message6,
									reports = refreshed
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/log-intelligence/issues/clear")
						{
							int cleared = await RunOnMainThreadAsync(() => logIntelligence?.ClearIssueSignatures() ?? 0, "web-log-intelligence-clear-issues").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("log-intelligence-clear-issues", $"cleared={cleared}", "Success", remote.ToString(), requestActor);
							LogIntelligenceSnapshot snapshot = await RunOnMainThreadAsync(() => logIntelligence?.GetSnapshot() ?? new LogIntelligenceSnapshot(), "web-log-intelligence-clear-issues-result").ConfigureAwait(continueOnCapturedContext: false);
							await WriteJsonAsync(stream, 200, new
							{
								success = true,
								message = $"Cleared {cleared} retained issue signature(s).",
								cleared = cleared,
								logIntelligence = snapshot
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/log-intelligence/scan")
						{
							bool includeArchived = GetJsonBool(request.Body, "includeArchived", fallback: false);
							if (logIntelligence != null)
							{
								await logIntelligence.RunManualScanAsync(includeArchived).ConfigureAwait(continueOnCapturedContext: false);
							}
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => logIntelligence?.GetSnapshot() ?? new LogIntelligenceSnapshot(), "web-log-intelligence-scan-result").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/log-intelligence/preflight")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => logIntelligence?.RefreshPreflight(config.BindAddress, config.Port, config.Enabled, IsRunning) ?? new StartupPreflightSnapshot(), "web-log-intelligence-preflight").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/log-intelligence/block-entity/action")
						{
							string key4 = GetJsonString(request.Body, "key", "").Trim();
							string action12 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
							string value2 = GetJsonString(request.Body, "value", "").Trim();
							BadBlockEntityActionResult payload34 = await RunOnMainThreadAsync(() => ExecuteBadBlockEntityAction(key4, action12, value2, requestActor), "web-bad-block-entity-action").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit(result: payload34.Success ? "Success" : "Failed", action: "bad-block-entity-action", detail: $"{action12} key={key4} value={value2}", remote: remote.ToString(), actor: requestActor);
							await WriteJsonAsync(stream, 200, payload34, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/logout")
						{
							RecordWebAudit("logout", "Web session logged out.", "Success", remote.ToString(), requestActor);
							RemoveSession(request);
							await WriteJsonAsync(stream, 200, new
							{
								success = true
							}, cancellationToken, null, clearCookie: true).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/dashboard")
						{
							bool terrainExportBusy2 = this.module.WebMap?.IsTerrainExportInProgress ?? false;
							await WriteJsonAsync(stream, 200, await GetDashboardSnapshotAsync(terrainExportBusy2).ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/web/status")
						{
							var status2 = new
							{
								enabled = config.Enabled,
								running = IsRunning,
								bindAddress = config.BindAddress,
								port = config.Port,
								listenAddress = ListenAddress,
								localUrl = LocalDashboardUrl,
								publicUrl = PublicDashboardUrl,
								username = config.AdminUsername,
								panelAccounts = PanelAccountCount,
								authenticatedAs = requestActor,
								authenticatedRole = (webSession?.Role ?? "Owner"),
								passwordConfigured = PasswordConfigured,
								localAnonymousMode = LocalAnonymousMode,
								authenticationMode = (LocalAnonymousMode ? "local-loopback-no-login" : "password"),
								activeSessions = ActiveSessionCount,
								sessionTimeoutMinutes = config.SessionTimeoutMinutes,
								secureCookies = config.SecureCookies,
								safeDiagnosticsMode = config.SafeDiagnosticsMode,
								workerCount = config.WebWorkerCount,
								requestQueueCapacity = config.WebRequestQueueCapacity,
								queueMaxWaitMs = config.WebQueueMaxWaitMs,
								liveSnapshotMaxAgeMs = config.LiveSnapshotMaxAgeMs,
								dashboardSnapshotMaxAgeMs = config.DashboardSnapshotMaxAgeMs,
								allowedNetworks = config.AllowedNetworks,
								lastError = LastError,
								diagnostics = BuildWebDiagnosticsSnapshot(),
								uptimeSeconds = (IsRunning ? Math.Max(0.0, (DateTime.UtcNow - startedUtc).TotalSeconds) : 0.0)
							};
							await WriteJsonAsync(stream, 200, status2, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/web/accounts")
						{
							await WriteJsonAsync(stream, 200, BuildWebAdminAccountsPayload(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/web/accounts/create")
						{
							string username = GetJsonString(request.Body, "username", "").Trim();
							string password = GetJsonString(request.Body, "password", "");
							string roleInput = GetJsonString(request.Body, "role", "Administrator").Trim();
							if (!string.Equals(roleInput, "Administrator", StringComparison.OrdinalIgnoreCase) && !string.Equals(roleInput, "ReadOnly", StringComparison.OrdinalIgnoreCase) && !string.Equals(roleInput, "Read Only", StringComparison.OrdinalIgnoreCase))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Role must be Administrator or Read Only."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								string role = NormalizePanelRole(roleInput);
								if (!IsValidPanelUsername(username))
								{
									await WriteJsonAsync(stream, 400, new
									{
										error = "Username must be 3-64 characters using letters, numbers, dot, dash, or underscore."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else if (password.Length < 10 || password.Length > 256)
								{
									await WriteJsonAsync(stream, 400, new
									{
										error = "Password must be 10-256 characters."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									string createError = "";
									lock (webAccountLock)
									{
										if (string.Equals(username, config.AdminUsername, StringComparison.OrdinalIgnoreCase) || FindPanelAccount(username) != null)
										{
											createError = "That panel username already exists.";
										}
										else if (webAdminAccounts.Accounts.Count >= 50)
										{
											createError = $"The panel account limit of {50} has been reached.";
										}
										else
										{
											WebAdminAccountRecord account = new WebAdminAccountRecord
											{
												Id = Guid.NewGuid().ToString("N"),
												Username = username,
												Role = role,
												Enabled = true,
												PasswordIterations = config.PasswordIterations,
												CreatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
											};
											SetAccountPasswordHash(account, password);
											webAdminAccounts.Accounts.Add(account);
											SaveWebAdminAccounts();
										}
									}
									if (!string.IsNullOrEmpty(createError))
									{
										await WriteJsonAsync(stream, 409, new
										{
											error = createError
										}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									}
									else
									{
										RecordWebAudit("panel-account-create", $"Created {username} as {role}.", "Success", remote.ToString(), requestActor);
										await WriteJsonAsync(stream, 200, new
										{
											success = true,
											message = "Panel account " + username + " created.",
											accounts = BuildWebAdminAccountsPayload()
										}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									}
								}
							}
						}
						else if (request.Method == "POST" && path == "/api/web/accounts/action")
						{
							string id7 = GetJsonString(request.Body, "id", "").Trim();
							string action13 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
							string value3 = GetJsonString(request.Body, "value", "").Trim();
							string password2 = GetJsonString(request.Body, "password", "");
							if (string.IsNullOrWhiteSpace(id7) || string.IsNullOrWhiteSpace(action13))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Account id and action are required."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								string affectedUsername = "";
								string message7 = "";
								bool invalidateSessions = false;
								int statusCode3 = 200;
								lock (webAccountLock)
								{
									WebAdminAccountRecord account2 = webAdminAccounts.Accounts.FirstOrDefault((WebAdminAccountRecord x) => string.Equals(x.Id, id7, StringComparison.OrdinalIgnoreCase));
									if (account2 == null)
									{
										statusCode3 = 404;
										message7 = "Panel account was not found.";
									}
									else
									{
										affectedUsername = account2.Username;
										switch (action13)
										{
										case "enable":
											account2.Enabled = true;
											account2.LockedUntilUnixMilliseconds = 0L;
											account2.FailedLoginCount = 0;
											message7 = account2.Username + " enabled.";
											break;
										case "disable":
											account2.Enabled = false;
											invalidateSessions = true;
											message7 = account2.Username + " disabled and signed out.";
											break;
										case "delete":
											webAdminAccounts.Accounts.Remove(account2);
											invalidateSessions = true;
											message7 = account2.Username + " deleted and signed out.";
											break;
										case "unlock":
											account2.LockedUntilUnixMilliseconds = 0L;
											account2.FailedLoginCount = 0;
											message7 = account2.Username + " login lock cleared.";
											break;
										case "role":
											if (!string.Equals(value3, "Administrator", StringComparison.OrdinalIgnoreCase) && !string.Equals(value3, "ReadOnly", StringComparison.OrdinalIgnoreCase) && !string.Equals(value3, "Read Only", StringComparison.OrdinalIgnoreCase))
											{
												statusCode3 = 400;
												message7 = "Role must be Administrator or Read Only.";
											}
											else
											{
												account2.Role = NormalizePanelRole(value3);
												invalidateSessions = true;
												message7 = account2.Username + " role changed to " + account2.Role + "; existing sessions were signed out.";
											}
											break;
										case "password":
											if (password2.Length < 10 || password2.Length > 256)
											{
												statusCode3 = 400;
												message7 = "Password must be 10-256 characters.";
											}
											else
											{
												SetAccountPasswordHash(account2, password2);
												account2.LockedUntilUnixMilliseconds = 0L;
												account2.FailedLoginCount = 0;
												invalidateSessions = true;
												message7 = "Password reset for " + account2.Username + "; existing sessions were signed out.";
											}
											break;
										default:
											statusCode3 = 400;
											message7 = "Unknown panel account action.";
											break;
										}
										if (statusCode3 == 200)
										{
											SaveWebAdminAccounts();
										}
									}
								}
								if (invalidateSessions && !string.IsNullOrWhiteSpace(affectedUsername))
								{
									LogoutSessionsForUsername(affectedUsername);
								}
								RecordWebAudit("panel-account-" + action13, string.IsNullOrWhiteSpace(affectedUsername) ? id7 : affectedUsername, (statusCode3 == 200) ? "Success" : message7, remote.ToString(), requestActor);
								if (statusCode3 != 200)
								{
									await WriteJsonAsync(stream, statusCode3, new
									{
										error = message7
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									await WriteJsonAsync(stream, 200, new
									{
										success = true,
										message = message7,
										accounts = BuildWebAdminAccountsPayload()
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "GET" && path == "/api/admin/status")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync((Func<object>)(() =>
							{
								AdminService admin = this.module.Admin;
								if (admin == null)
								{
									return new
									{
										available = false
									};
								}
								(bool, string, long) maintenance = admin.GetMaintenance();
								(long, string) shutdown = admin.GetShutdown();
								return new
								{
									available = true,
									maintenance = new
									{
										enabled = maintenance.Item1,
										reason = maintenance.Item2,
										untilUnixMilliseconds = maintenance.Item3
									},
									shutdown = new
									{
										atUnixMilliseconds = shutdown.Item1,
										reason = shutdown.Item2
									},
									undo = new
									{
										available = !string.IsNullOrWhiteSpace(admin.GetUndoDescription()),
										description = admin.GetUndoDescription()
									},
									audit = admin.GetAudit(50)
								};
							}), "web-admin-status", false).ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/admin/players")
						{
							await WriteJsonAsync(stream, 200, new
							{
								players = await RunOnMainThreadAsync(() =>
								{
									IReadOnlyList<AdminPlayerSummary> readOnlyList = this.module.Admin?.GetPlayers() ?? Array.Empty<AdminPlayerSummary>();
									this.module.PlayerList?.DecorateClientPresence(readOnlyList);
									DecorateSupporterNicknames(readOnlyList);
									return readOnlyList;
								}, "web-admin-players").ConfigureAwait(continueOnCapturedContext: false)
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/admin/database-storage")
						{
							await WriteJsonAsync(stream, 200, GetDatabaseStorageViewerSnapshot(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/admin/database-storage/analyze")
						{
							if (!(await databaseStorageAnalysisGate.WaitAsync(0, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)))
							{
								await WriteJsonAsync(stream, 409, new
								{
									error = "Storage analysis is already running. Try again after it finishes."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								DatabaseStorageViewerSnapshot report2;
								try
								{
									report2 = await Task.Run((Func<DatabaseStorageViewerSnapshot>)AnalyzeDatabaseStorage, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								finally
								{
									databaseStorageAnalysisGate.Release();
								}
								RecordWebAudit("database-storage-analyze", "essentials.db + blocklogger.db", report2.DetailedAnalysisAvailable ? "Success" : report2.Message, remote.ToString(), requestActor);
								await WriteJsonAsync(stream, 200, report2, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/admin/player")
						{
							string player7 = GetQuery(request, "player", "").Trim();
							if (string.IsNullOrWhiteSpace(player7))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Player is required."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								bool includeDetails = GetQuery(request, "details", "all") != "summary";
								AdminPlayerDetailSnapshot payload35 = await RunOnMainThreadAsync(() =>
								{
									AdminService admin = this.module.Admin;
									AdminPlayerSummary adminPlayerSummary = admin?.GetPlayer(player7);
									if (admin == null || adminPlayerSummary == null)
									{
										return (AdminPlayerDetailSnapshot)null;
									}
									this.module.PlayerList?.DecorateClientPresence(new AdminPlayerSummary[1] { adminPlayerSummary });
									DecorateSupporterNicknames(new AdminPlayerSummary[1] { adminPlayerSummary });
									AdminInventoryInspection adminInventoryInspection = ((includeDetails && adminPlayerSummary.Online) ? admin.GetInventoryInspection(player7) : new AdminInventoryInspection
									{
										Status = new AdminInventoryViewStatus
										{
											Message = "Open Inventory and refresh to load item details."
										}
									});
									return new AdminPlayerDetailSnapshot
									{
										Player = adminPlayerSummary,
										Warnings = admin.GetWarnings(player7),
										Notes = admin.GetNotes(player7),
										History = admin.GetHistory(player7),
										Inventory = adminInventoryInspection.Slots,
										InventoryStatus = adminInventoryInspection.Status,
										InventorySnapshots = admin.GetInventorySnapshots(player7)
									};
								}, "web-admin-player-detail").ConfigureAwait(continueOnCapturedContext: false);
								if (payload35 == null)
								{
									await WriteJsonAsync(stream, 404, new
									{
										error = "Player not found."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									if (includeDetails && !payload35.Player.Online && this.module.Admin != null)
									{
										AdminInventoryInspection inspection2 = await LoadSavedInventoryInspectionAsync(this.module.Admin, payload35.Player.PlayerUid).ConfigureAwait(continueOnCapturedContext: false);
										payload35.Inventory = inspection2.Slots;
										payload35.InventoryStatus = inspection2.Status;
									}
									payload35.InventoryStatus.ReadOnly |= readOnlyAccount;
									if (includeDetails && this.module.Queries != null)
									{
										try
										{
											AdminPlayerDetailSnapshot adminPlayerDetailSnapshot = payload35;
											adminPlayerDetailSnapshot.BlockHistory = await this.module.Queries.QueryPlayerAsync(payload35.Player.Name, 100, null).ConfigureAwait(continueOnCapturedContext: false);
										}
										catch (Exception ex30)
										{
											Exception ex31 = ex30;
											api.Logger.Warning("[ServerEssentials/Admin] Could not load BlockLogger history for web player profile: {0}", ex31.Message);
										}
									}
									await WriteJsonAsync(stream, 200, payload35, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "GET" && path == "/api/admin/inventory")
						{
							string player8 = GetQuery(request, "player", "").Trim();
							if (string.IsNullOrWhiteSpace(player8))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Player is required."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								AdminInventoryDetailSnapshot payload36 = await RunOnMainThreadAsync(() =>
								{
									AdminService admin = this.module.Admin;
									AdminPlayerSummary adminPlayerSummary = admin?.GetPlayer(player8);
									if (admin == null || adminPlayerSummary == null)
									{
										return (AdminInventoryDetailSnapshot)null;
									}
									this.module.PlayerList?.DecorateClientPresence(new AdminPlayerSummary[1] { adminPlayerSummary });
									List<AdminAuditRecord> history2 = (from x in admin.GetHistory(player8, 120)
										where (x.Action ?? "").StartsWith("inventory-", StringComparison.OrdinalIgnoreCase)
										select x).Take(80).ToList();
									AdminInventoryInspection adminInventoryInspection = (adminPlayerSummary.Online ? admin.GetInventoryInspection(player8) : new AdminInventoryInspection());
									return new AdminInventoryDetailSnapshot
									{
										Player = adminPlayerSummary,
										Inventory = adminInventoryInspection.Slots,
										InventoryStatus = adminInventoryInspection.Status,
										InventorySnapshots = admin.GetInventorySnapshots(player8),
										History = history2
									};
								}, "web-admin-inventory-detail").ConfigureAwait(continueOnCapturedContext: false);
								if (payload36 == null)
								{
									await WriteJsonAsync(stream, 404, new
									{
										error = "Player not found."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									if (!payload36.Player.Online && this.module.Admin != null)
									{
										AdminInventoryInspection inspection3 = await LoadSavedInventoryInspectionAsync(this.module.Admin, payload36.Player.PlayerUid).ConfigureAwait(continueOnCapturedContext: false);
										payload36.Inventory = inspection3.Slots;
										payload36.InventoryStatus = inspection3.Status;
									}
									payload36.InventoryStatus.ReadOnly |= readOnlyAccount;
									await WriteJsonAsync(stream, 200, payload36, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "GET" && path == "/api/admin/items")
						{
							string search2 = GetQuery(request, "search", "").Trim();
							int limit4 = Math.Clamp(int.TryParse(GetQuery(request, "limit", "50"), out var parsedLimit) ? parsedLimit : 50, 1, 100);
							await WriteJsonAsync(stream, 200, new
							{
								items = await RunOnMainThreadAsync(() => this.module.Admin?.SearchItems(search2, limit4) ?? new List<AdminItemSearchResult>(), "web-admin-item-search").ConfigureAwait(continueOnCapturedContext: false)
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/admin/inventory/action")
						{
							string actor2 = requestActor;
							string action14 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
							string player9 = GetJsonString(request.Body, "player", "").Trim();
							string inventory = GetJsonString(request.Body, "inventory", "").Trim();
							string toInventory = GetJsonString(request.Body, "toInventory", "").Trim();
							string code = GetJsonString(request.Body, "code", "").Trim();
							string fingerprint3 = GetJsonString(request.Body, "fingerprint", "").Trim();
							string toFingerprint = GetJsonString(request.Body, "toFingerprint", "").Trim();
							int slot = GetJsonInt(request.Body, "slot", -1);
							int toSlot = GetJsonInt(request.Body, "toSlot", -1);
							int quantity = GetJsonInt(request.Body, "quantity", 1);
							string result45 = await RunOnMainThreadAsync(() =>
							{
								AdminService admin = this.module.Admin;
								if (admin == null)
								{
									return "Administration module unavailable.";
								}
								if (1 == 0)
								{
								}
								string result71 = action14 switch
								{
									"remove" => admin.RemoveInventorySlot(actor2, player9, inventory, slot, "web", fingerprint3), 
									"quantity" => admin.SetInventoryQuantity(actor2, player9, inventory, slot, quantity, fingerprint3, "web"), 
									"move" => admin.MoveInventorySlot(actor2, player9, inventory, slot, toInventory, toSlot, fingerprint3, toFingerprint, "web"), 
									"give" => admin.GiveInventoryItem(actor2, player9, code, quantity, inventory, slot, fingerprint3, "web"), 
									"replace" => admin.ReplaceInventorySlot(actor2, player9, inventory, slot, code, quantity, fingerprint3, "web"), 
									_ => "Unknown inventory action.", 
								};
								if (1 == 0)
								{
								}
								return result71;
							}, "web-admin-inventory-action").ConfigureAwait(continueOnCapturedContext: false);
							bool conflict = result45.StartsWith("Inventory changed", StringComparison.OrdinalIgnoreCase);
							bool success6 = result45.StartsWith("Removed ", StringComparison.OrdinalIgnoreCase) || result45.StartsWith("Changed ", StringComparison.OrdinalIgnoreCase) || result45.StartsWith("Moved/", StringComparison.OrdinalIgnoreCase) || result45.StartsWith("Gave ", StringComparison.OrdinalIgnoreCase) || result45.StartsWith("Replaced ", StringComparison.OrdinalIgnoreCase);
							RecordWebAudit("admin-inventory-" + action14, $"{player9} {inventory}[{slot}] {code} x{quantity}", result45, remote.ToString(), actor2);
							WebDashboardService webDashboardService7 = this;
							NetworkStream stream4 = stream;
							int statusCode4;
							if (conflict)
							{
								statusCode4 = 409;
							}
							else
							{
								statusCode4 = (success6 ? 200 : 400);
							}
							await webDashboardService7.WriteJsonAsync(stream4, statusCode4, new
							{
								success = success6,
								message = result45,
								conflict = conflict
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/admin/inventory/compare")
						{
							string player10 = GetQuery(request, "player", "").Trim();
							long id8 = (long.TryParse(GetQuery(request, "id", "0"), out var parsedId) ? parsedId : 0);
							(string Error, List<AdminInventoryDiffRow> Rows) payload37 = await RunOnMainThreadAsync(() =>
							{
								AdminService admin = this.module.Admin;
								if (admin == null || admin.GetPlayer(player10)?.Online != true)
								{
									return (Error: "Live snapshot comparison requires an online player.", Rows: new List<AdminInventoryDiffRow>());
								}
								return (!admin.GetInventorySnapshots(player10).Any((AdminInventorySnapshotRecord x) => x.Id == id8)) ? (Error: "Inventory snapshot not found.", Rows: new List<AdminInventoryDiffRow>()) : (Error: "", Rows: admin.CompareInventorySnapshot(player10, id8));
							}, "web-admin-inventory-compare").ConfigureAwait(continueOnCapturedContext: false);
							if (payload37.Error.Length > 0)
							{
								await WriteJsonAsync(stream, 409, new
								{
									error = payload37.Error
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								await WriteJsonAsync(stream, 200, new
								{
									differences = payload37.Rows
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/admin/inventory/snapshot")
						{
							string actor3 = requestActor;
							string action15 = GetJsonString(request.Body, "action", "create").Trim().ToLowerInvariant();
							string player11 = GetJsonString(request.Body, "player", "").Trim();
							string reason3 = GetJsonString(request.Body, "reason", "Manual web snapshot").Trim();
							long id9 = GetJsonLong(request.Body, "id", 0L);
							string result46 = await RunOnMainThreadAsync(() => (!(action15 == "restore")) ? (this.module.Admin?.CreateInventorySnapshot(actor3, player11, reason3, "web") ?? "Administration module unavailable.") : (this.module.Admin?.RestoreInventorySnapshot(actor3, player11, id9, "web") ?? "Administration module unavailable."), "web-admin-inventory-snapshot").ConfigureAwait(continueOnCapturedContext: false);
							bool success7 = result46.StartsWith("Inventory snapshot #", StringComparison.OrdinalIgnoreCase) || result46.StartsWith("Restored inventory snapshot #", StringComparison.OrdinalIgnoreCase);
							RecordWebAudit("admin-inventory-snapshot-" + action15, $"{player11} #{id9} {reason3}", result46, remote.ToString(), actor3);
							await WriteJsonAsync(stream, success7 ? 200 : 400, new
							{
								success = success7,
								message = result46
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/admin/action")
						{
							string actor4 = requestActor;
							string action16 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
							string player12 = GetJsonString(request.Body, "player", "").Trim();
							string duration = GetJsonString(request.Body, "duration", "").Trim();
							string reason4 = GetJsonString(request.Body, "reason", "").Trim();
							string note2 = GetJsonString(request.Body, "note", "").Trim();
							long id10 = GetJsonLong(request.Body, "id", 0L);
							if (action16 == "allowcharselonce")
							{
								AdminPlayerSummary summary2 = await RunOnMainThreadAsync(() => this.module.Admin?.GetPlayer(player12), "web-admin-resolve-player").ConfigureAwait(continueOnCapturedContext: false);
								if (summary2 == null || string.IsNullOrWhiteSpace(summary2.Name))
								{
									await WriteJsonAsync(stream, 404, new
									{
										error = "Player was not found."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									(bool Success, string Message) grant = await RunOnMainThreadAsync(() =>
									{
										IServerPlayer serverPlayer = this.module.Admin?.FindOnlinePlayer(summary2.PlayerUid) ?? this.module.Admin?.FindOnlinePlayer(summary2.Name);
										try
										{
											if (serverPlayer?.Entity != null)
											{
												serverPlayer.Entity.WatchedAttributes.SetBool("allowcharselonce", value: true);
												serverPlayer.Entity.WatchedAttributes.MarkPathDirty("allowcharselonce");
												serverPlayer.BroadcastPlayerData(sendInventory: true);
												this.module.Admin?.Audit(actor4, "allowcharselonce", serverPlayer.PlayerUID, serverPlayer.PlayerName, "Granted one additional .charsel use directly through Vintage Story's allowcharselonce watched attribute", "web");
												return (Success: true, Message: "Allowed " + serverPlayer.PlayerName + " to use .charsel one more time. They can run .charsel now.");
											}
											api.InjectConsole("/player " + QuoteCommandArgument(summary2.Name) + " allowcharselonce");
											this.module.Admin?.Audit(actor4, "allowcharselonce", summary2.PlayerUid, summary2.Name, "Queued the native /player allowcharselonce command for an offline player", "web");
											return (Success: true, Message: "Queued one-time character selection for " + summary2.Name + ". They can run .charsel after joining.");
										}
										catch (Exception ex39)
										{
											return (Success: false, Message: "Could not grant character selection to " + summary2.Name + ": " + ex39.Message);
										}
									}, "web-admin-allowcharselonce").ConfigureAwait(continueOnCapturedContext: false);
									RecordWebAudit("admin-allowcharselonce", summary2.Name, grant.Success ? "Success" : grant.Message, remote.ToString(), actor4);
									await WriteJsonAsync(stream, grant.Success ? 200 : 400, new
									{
										success = grant.Success,
										message = grant.Message
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
							else
							{
								string result47 = await RunOnMainThreadAsync(() => this.module.Admin?.ExecuteWebAction(actor4, action16, player12, duration, reason4, id10, note2) ?? "Administration module unavailable.", "web-admin-player-action").ConfigureAwait(continueOnCapturedContext: false);
								bool success8 = AdminService.IsSuccessfulActionResult(action16, result47);
								RecordWebAudit("admin-" + action16, player12 + ": " + reason4, success8 ? "Success" : result47, remote.ToString(), actor4);
								await WriteJsonAsync(stream, success8 ? 200 : 400, new
								{
									success = success8,
									message = result47
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/admin/maintenance")
						{
							string actor5 = requestActor;
							bool enabled4 = GetJsonBool(request.Body, "enabled", fallback: false);
							string reason5 = GetJsonString(request.Body, "reason", "Maintenance");
							string duration2 = GetJsonString(request.Body, "duration", "");
							if (enabled4 && !string.Equals(GetJsonString(request.Body, "confirmation", "").Trim(), "MAINTENANCE", StringComparison.OrdinalIgnoreCase))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Safety confirmation failed. Type MAINTENANCE to enable maintenance mode."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								long durationMs = 0L;
								if (!string.IsNullOrWhiteSpace(duration2) && (!AdminService.TryParseDuration(duration2, out durationMs, out var permanent2) | permanent2))
								{
									await WriteJsonAsync(stream, 400, new
									{
										error = "Invalid duration. Use 30m, 6h, 3d, 2w, etc."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									string result48 = await RunOnMainThreadAsync(() => this.module.Admin?.SetMaintenance(actor5, enabled4, reason5, durationMs, "web") ?? "Administration module unavailable.", "web-admin-maintenance").ConfigureAwait(continueOnCapturedContext: false);
									RecordWebAudit("admin-maintenance", reason5, result48, remote.ToString(), actor5);
									await WriteJsonAsync(stream, 200, new
									{
										success = true,
										message = result48
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "POST" && path == "/api/admin/shutdown")
						{
							string actor6 = requestActor;
							string action17 = GetJsonString(request.Body, "action", "schedule").Trim().ToLowerInvariant();
							string reason6 = GetJsonString(request.Body, "reason", "Scheduled shutdown");
							string duration3 = GetJsonString(request.Body, "duration", "");
							if (action17 != "cancel")
							{
								string expected = ((action17 == "restart") ? "RESTART" : "SHUTDOWN");
								string confirmation = GetJsonString(request.Body, "confirmation", "").Trim();
								if (!string.Equals(confirmation, expected, StringComparison.OrdinalIgnoreCase))
								{
									await WriteJsonAsync(stream, 400, new
									{
										error = "Safety confirmation failed. Type " + expected + " to schedule this action."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									return;
								}
							}
							string result49;
							if (action17 == "cancel")
							{
								result49 = await RunOnMainThreadAsync(() => this.module.Admin?.CancelShutdown(actor6, "web") ?? "Administration module unavailable.", "web-admin-shutdown-cancel").ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								if (!AdminService.TryParseDuration(duration3, out var delayMs, out var permanent3) | permanent3)
								{
									await WriteJsonAsync(stream, 400, new
									{
										error = "Invalid shutdown/restart delay. Use 30s, 5m, 1h, etc."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									return;
								}
								result49 = await RunOnMainThreadAsync(() => (!(action17 == "restart")) ? (this.module.Admin?.ScheduleShutdown(actor6, delayMs, reason6, "web") ?? "Administration module unavailable.") : (this.module.Admin?.ScheduleRestart(actor6, delayMs, reason6, "web") ?? "Administration module unavailable."), (action17 == "restart") ? "web-admin-restart-schedule" : "web-admin-shutdown-schedule").ConfigureAwait(continueOnCapturedContext: false);
							}
							RecordWebAudit("admin-shutdown", action17 + ": " + reason6, result49, remote.ToString(), actor6);
							await WriteJsonAsync(stream, 200, new
							{
								success = true,
								message = result49
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/admin/cleanup")
						{
							string actor7 = requestActor;
							bool confirm = GetJsonBool(request.Body, "confirm", fallback: false);
							if (confirm && !string.Equals(GetJsonString(request.Body, "confirmation", "").Trim(), "CLEANUP", StringComparison.OrdinalIgnoreCase))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Safety confirmation failed. Type CLEANUP to remove loaded dropped-item entities."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								int maxRemove = Math.Clamp(GetJsonInt(request.Body, "maxRemove", 100000), 1, 100000);
								CleanupReport result50 = await RunOnMainThreadAsync(() =>
								{
									CleanupReport cleanupReport = ((this.module.Admin == null) ? new CleanupReport
									{
										DryRun = true
									} : (confirm ? this.module.Admin.CleanupDrops(dryRun: false, maxRemove) : this.module.Admin.ScanCleanup()));
									this.module.Admin?.Audit(actor7, confirm ? "cleanup-drops" : "cleanup-entity-scan", "", "server", confirm ? $"Removed {cleanupReport.Removed} dropped item entities" : $"Scanned {cleanupReport.LoadedEntities} loaded entities; dropped={cleanupReport.DroppedItems}, dead/despawning={cleanupReport.DeadOrDespawning}", "web");
									return cleanupReport;
								}, "web-admin-cleanup-drops").ConfigureAwait(continueOnCapturedContext: false);
								RecordWebAudit("admin-cleanup", confirm ? "confirmed" : "dry-run", "Success", remote.ToString(), actor7);
								await WriteJsonAsync(stream, 200, result50, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/health")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
							{
								ServerUtilitiesSelfTest selfTest = BuildServerUtilitiesHealth();
								DatabaseHealthSnapshot database = this.module.Storage?.GetDatabaseHealth(this.module.Config.RetentionDays);
								ServerHealthSnapshot serverHealthSnapshot = this.module.ServerHealth?.GetSnapshot(120, 300);
								string text4;
								lock (hardeningLock)
								{
									text4 = activeHeavyOperation;
								}
								return new
								{
									selfTest = selfTest,
									database = database,
									adminDatabase = this.module.Admin?.GetStorageHealth(),
									webDiagnostics = BuildWebDiagnosticsSnapshot(),
									smartDiagnosis = serverHealthSnapshot?.SmartAnalysis,
									diagnosticSummary = serverHealthSnapshot?.DiagnosticSummary,
									retentionAndPrivacy = BuildRetentionAndPrivacyStatus(),
									safeDiagnosticsMode = config.SafeDiagnosticsMode,
									activeHeavyOperation = text4,
									buildId = "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1",
									targetGameVersion = "1.22.7"
								};
							}, "web-serverutilities-health").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/health/admin-database-integrity")
						{
							if (this.module.Admin == null)
							{
								await WriteJsonAsync(stream, 503, new
								{
									error = "Admin storage is unavailable."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								string result51 = await Task.Run(() => this.module.Admin.CheckStorageIntegrity(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								RecordWebAudit("admin-database-integrity", "essentials.db quick_check: " + result51, "Success", remote.ToString(), requestActor);
								await WriteJsonAsync(stream, 200, new
								{
									integrity = result51
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/health/admin-database-backup")
						{
							if (this.module.Admin == null)
							{
								await WriteJsonAsync(stream, 503, new
								{
									error = "Admin storage is unavailable."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else if (string.IsNullOrWhiteSpace(await Task.Run(() => this.module.Admin.BackupStorage("web-manual"), cancellationToken).ConfigureAwait(continueOnCapturedContext: false)))
							{
								await WriteJsonAsync(stream, 500, new
								{
									error = "Admin database backup failed. Check server logs."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								RecordWebAudit("admin-database-backup", "Created essentials.db backup", "Success", remote.ToString(), requestActor);
								await WriteJsonAsync(stream, 200, new
								{
									success = true,
									message = "Admin database backup created."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/health/database-backup")
						{
							LogStorageService storage = this.module.Storage;
							if (storage == null)
							{
								await WriteJsonAsync(stream, 503, new
								{
									error = "BlockLogger storage is unavailable."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else if (string.IsNullOrWhiteSpace(await storage.CreateDatabaseBackupAsync("web-manual").ConfigureAwait(continueOnCapturedContext: false)))
							{
								await WriteJsonAsync(stream, 409, new
								{
									error = "BlockLogger database backup could not start or is already running. Check server logs/health."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								RecordWebAudit("database-backup", "Created blocklogger.db backup", "Success", remote.ToString(), requestActor);
								await WriteJsonAsync(stream, 200, new
								{
									success = true,
									message = "BlockLogger database backup created."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/health/database-integrity")
						{
							if (IsFeatureCircuitOpen("database-integrity", out string circuitMessage))
							{
								await WriteJsonAsync(stream, 503, new
								{
									error = circuitMessage
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								LogStorageService storage2 = this.module.Storage;
								if (storage2 == null)
								{
									await WriteJsonAsync(stream, 503, new
									{
										error = "BlockLogger storage is unavailable."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									try
									{
										DatabaseHealthSnapshot db = await AwaitWithTimeout(storage2.CheckDatabaseIntegrityAsync(this.module.Config.RetentionDays), TimeSpan.FromSeconds(12L), "SQLite quick_check").ConfigureAwait(continueOnCapturedContext: false);
										RecordFeatureSuccess("database-integrity", "SQLite Integrity Check", "Moderate");
										RecordWebAudit("database-integrity", "SQLite quick_check: " + db.Integrity, "Success", remote.ToString(), requestActor);
										await WriteJsonAsync(stream, 200, db, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									}
									catch (Exception ex32)
									{
										RecordFeatureFailure("database-integrity", ex32, "SQLite Integrity Check");
										await WriteJsonAsync(stream, 503, new
										{
											error = ex32.GetType().Name + ": " + ex32.Message
										}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									}
								}
							}
						}
						else if (request.Method == "POST" && path == "/api/health/safe-mode")
						{
							bool enabled5 = GetJsonBool(request.Body, "enabled", fallback: true);
							string actor8 = requestActor;
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
							{
								config.SafeDiagnosticsMode = enabled5;
								SaveConfig();
								if (enabled5)
								{
									this.module.Config.ProfilerEnabled = true;
									this.module.SaveRuntimeConfig();
								}
								this.module.ServerHealth?.SetAutomaticIncidentProfilingEnabled(!enabled5);
								RecordWebAudit("safe-diagnostics-mode", enabled5 ? "Enabled" : "Disabled", "Success", remoteText, actor8);
								SaveOperationsCenterStore();
								return new
								{
									success = true,
									enabled = enabled5,
									message = (enabled5 ? "Safe Diagnostics Mode enabled. Lightweight diagnostics are ON; manual engine profiling and external optimizer changes are locked." : "Safe Diagnostics Mode disabled.")
								};
							}, "web-safe-diagnostics-mode").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/health/circuit-reset")
						{
							string key5 = GetJsonString(request.Body, "key", "").Trim();
							lock (hardeningLock)
							{
								FeatureBreakerState state4;
								if (string.IsNullOrWhiteSpace(key5))
								{
									foreach (FeatureBreakerState state3 in featureBreakers.Values)
									{
										state3.ConsecutiveFailures = 0;
										state3.OpenUntilUnixMilliseconds = 0L;
									}
								}
								else if (featureBreakers.TryGetValue(key5, out state4))
								{
									state4.ConsecutiveFailures = 0;
									state4.OpenUntilUnixMilliseconds = 0L;
								}
							}
							RecordWebAudit("circuit-breaker-reset", string.IsNullOrWhiteSpace(key5) ? "Reset all feature circuit breakers." : ("Reset circuit breaker " + key5 + "."), "Success", remote.ToString(), requestActor);
							await WriteJsonAsync(stream, 200, new
							{
								success = true,
								circuitBreakers = SnapshotCircuitBreakers()
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/operations/overview")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
							{
								MaintainOperationsCenter();
								ServerHealthSnapshot serverHealthSnapshot = this.module.ServerHealth?.GetSnapshot(300);
								MemoryGcAnalysis memory = ((serverHealthSnapshot == null) ? new MemoryGcAnalysis
								{
									Available = false,
									State = "Unavailable",
									Finding = "Server health diagnostics are unavailable."
								} : BuildMemoryGcAnalysis(serverHealthSnapshot));
								return new
								{
									diagnosticsEnabled = (this.module.ServerHealth?.Enabled ?? false),
									alerts = (from x in operationsCenterStore.Alerts
										orderby SeverityRankForOperations(x.Severity) descending, x.LastSeenUnixMilliseconds descending
										select x).Take(100).ToList(),
									actions = (from x in operationsCenterStore.Actions
										orderby (x.Priority == "High") ? 3 : ((!(x.Priority == "Medium")) ? 1 : 2) descending, x.UpdatedUnixMilliseconds descending
										select x).Take(100).ToList(),
									memory = memory,
									scorecard = BuildPerformanceScorecard(serverHealthSnapshot, memory),
									playerLoad = BuildPlayerLoadAnalysis(),
									worldSaves = operationsCenterStore.WorldSaves.OrderByDescending((WorldSaveObservation x) => x.UnixMilliseconds).Take(20).ToList(),
									networkSamples = operationsCenterStore.NetworkSamples.OrderByDescending((NetworkPerformanceSample x) => x.UnixMilliseconds).Take(60).ToList(),
									currentPerformance = ((serverHealthSnapshot == null) ? null : CaptureEnvironmentMetrics(serverHealthSnapshot)),
									performanceSnapshots = operationsCenterStore.PerformanceSnapshots.Take(config.MaxPerformanceSnapshots).ToList(),
									configSnapshots = operationsCenterStore.ConfigSnapshots.Select((ConfigSnapshotRecord x) => new { x.Id, x.UnixMilliseconds, x.Label, x.CreatedBy }).Take(20).ToList(),
									auditCount = operationsCenterStore.Audit.Count,
									instrumentation = BuildInstrumentationStatus()
								};
							}, "web-operations-overview").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/operations/memory")
						{
							await WriteJsonAsync(stream, 200, await GetCachedMainThreadReadAsync("operations-memory", 1500, () =>
							{
								ServerHealthSnapshot serverHealthSnapshot = this.module.ServerHealth?.GetSnapshot(600);
								return (serverHealthSnapshot == null) ? new
								{
									available = false,
									diagnosticsEnabled = false,
									analysis = new MemoryGcAnalysis
									{
										Available = false,
										State = "Unavailable",
										Finding = "Server health diagnostics are unavailable."
									},
									samples = new List<ServerHealthSample>(),
									inspector = BuildMemoryInspectorPayload(null)
								} : new
								{
									available = true,
									diagnosticsEnabled = serverHealthSnapshot.Enabled,
									analysis = BuildMemoryGcAnalysis(serverHealthSnapshot),
									samples = serverHealthSnapshot.Samples,
									inspector = BuildMemoryInspectorPayload(serverHealthSnapshot)
								};
							}, "web-operations-memory").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/operations/memory-snapshot")
						{
							string action18 = GetJsonString(request.Body, "action", "capture").Trim().ToLowerInvariant();
							string actor9 = requestActor;
							switch (action18)
							{
							case "capture":
							{
								string label2 = GetJsonString(request.Body, "label", "Memory snapshot").Trim();
								if (label2.Length > 80)
								{
									label2 = label2.Substring(0, 80);
								}
								MemoryInspectorSnapshot snapshot2 = CaptureMemoryInspectorSnapshot(label2);
								lock (operationsLock)
								{
									operationsCenterStore.MemoryInspectorSnapshots.Insert(0, snapshot2);
								}
								RecordWebAudit("memory-snapshot", snapshot2.Label, "Success", remoteText, actor9);
								SaveOperationsCenterStore();
								await WriteJsonAsync(stream, 200, new
								{
									success = true,
									snapshot = snapshot2
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								break;
							}
							case "delete":
							{
								string id11 = GetJsonString(request.Body, "id", "").Trim();
								bool removed3;
								lock (operationsLock)
								{
									removed3 = operationsCenterStore.MemoryInspectorSnapshots.RemoveAll((MemoryInspectorSnapshot x) => x.Id == id11) > 0;
								}
								if (!removed3)
								{
									await WriteJsonAsync(stream, 404, new
									{
										error = "Memory snapshot not found."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									RecordWebAudit("memory-snapshot-delete", id11, "Success", remoteText, actor9);
									SaveOperationsCenterStore();
									await WriteJsonAsync(stream, 200, new
									{
										success = true
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								break;
							}
							case "clear":
							{
								int removed2;
								lock (operationsLock)
								{
									removed2 = operationsCenterStore.MemoryInspectorSnapshots.Count;
									operationsCenterStore.MemoryInspectorSnapshots.Clear();
								}
								RecordWebAudit("memory-snapshot-clear", $"Removed {removed2} snapshot(s)", "Success", remoteText, actor9);
								SaveOperationsCenterStore();
								await WriteJsonAsync(stream, 200, new
								{
									success = true,
									removed = removed2
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								break;
							}
							default:
								await WriteJsonAsync(stream, 400, new
								{
									error = "Unsupported memory snapshot action."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								break;
							}
						}
						else if (request.Method == "GET" && path == "/api/operations/audit")
						{
							int auditCount;
							List<WebAuditRecord> auditRows;
							lock (operationsLock)
							{
								auditCount = operationsCenterStore.Audit.Count;
								auditRows = operationsCenterStore.Audit.OrderByDescending((WebAuditRecord x) => x.UnixMilliseconds).Take(300).ToList();
							}
							await WriteJsonAsync(stream, 200, new
							{
								count = auditCount,
								rows = auditRows
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/operations/alert-status")
						{
							string id12 = GetJsonString(request.Body, "id", "");
							string status3 = GetJsonString(request.Body, "status", "Active");
							switch (status3)
							{
							case "Active":
							case "Investigating":
							case "Resolved":
							case "Ignored":
								flag = true;
								break;
							default:
								flag = false;
								break;
							}
							if (!flag)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Invalid alert status."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								string actor10 = requestActor;
								ServerAlertRecord alert = await RunOnMainThreadAsync(() =>
								{
									ServerAlertRecord serverAlertRecord = operationsCenterStore.Alerts.FirstOrDefault((ServerAlertRecord x) => x.Id == id12);
									if (serverAlertRecord == null)
									{
										return (ServerAlertRecord)null;
									}
									serverAlertRecord.Status = status3;
									RecordWebAudit("alert-status", serverAlertRecord.Title + " -> " + status3, "Success", remoteText, actor10);
									SaveOperationsCenterStore();
									return serverAlertRecord;
								}, "web-alert-status").ConfigureAwait(continueOnCapturedContext: false);
								if (alert == null)
								{
									await WriteJsonAsync(stream, 404, new
									{
										error = "Alert not found."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									await WriteJsonAsync(stream, 200, new
									{
										success = true,
										alert = alert
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "POST" && path == "/api/operations/clear")
						{
							string target3;
							string[] ids;
							try
							{
								using JsonDocument document = JsonDocument.Parse(request.Body);
								target3 = document.RootElement.GetProperty("target").GetString() ?? "";
								JsonElement entries = document.RootElement.GetProperty("ids");
								string text = target3;
								bool flag2 = ((text == "alerts" || text == "actions") ? true : false);
								if (!flag2 || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 2000)
								{
									throw new ArgumentException("Choose alerts or actions and at most 2,000 displayed record IDs.");
								}
								ids = (from x in entries.EnumerateArray()
									select x.GetString() ?? "").ToArray();
								if (ids.Any((string x) => x.Length == 0 || x.Length > 512))
								{
									throw new ArgumentException("Invalid record ID.");
								}
							}
							catch (Exception ex33) when ((ex33 is JsonException || ex33 is InvalidOperationException || ex33 is KeyNotFoundException || ex33 is ArgumentException) ? true : false)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Invalid clear request."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							await WriteJsonAsync(stream, 200, new
							{
								success = true,
								cleared = await RunOnMainThreadAsync(() =>
								{
									List<ServerAlertRecord> alerts = operationsCenterStore.Alerts.ToList();
									List<ServerActionRecord> list = operationsCenterStore.Actions.ToList();
									string[] array = list.Select((ServerActionRecord x) => x.Status).ToArray();
									int num2 = ActionCenterWorkflow.Clear(operationsCenterStore, target3, ids);
									if (num2 > 0)
									{
										try
										{
											SaveOperationsCenterStore();
										}
										catch
										{
											for (int num3 = 0; num3 < list.Count; num3++)
											{
												list[num3].Status = array[num3];
											}
											operationsCenterStore.Alerts = alerts;
											operationsCenterStore.Actions = list;
											throw;
										}
										RecordWebAudit("operations-clear", $"target={target3};cleared={num2}", "Success", remoteText, requestActor);
									}
									return num2;
								}, "web-operations-clear").ConfigureAwait(continueOnCapturedContext: false)
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/operations/alert-remove")
						{
							string id13 = GetJsonString(request.Body, "id", "");
							string actor11 = requestActor;
							if (await RunOnMainThreadAsync(() =>
							{
								ServerAlertRecord serverAlertRecord = operationsCenterStore.Alerts.FirstOrDefault((ServerAlertRecord x) => x.Id == id13);
								if (serverAlertRecord == null)
								{
									return (ServerAlertRecord)null;
								}
								operationsCenterStore.Alerts.Remove(serverAlertRecord);
								operationsCenterStore.Actions.RemoveAll((ServerActionRecord x) => string.Equals(x.Id, "alert:" + id13, StringComparison.OrdinalIgnoreCase));
								RecordWebAudit("alert-remove", serverAlertRecord.Title, "Success", remoteText, actor11);
								SaveOperationsCenterStore();
								return serverAlertRecord;
							}, "web-alert-remove").ConfigureAwait(continueOnCapturedContext: false) == null)
							{
								await WriteJsonAsync(stream, 404, new
								{
									error = "Alert not found."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								await WriteJsonAsync(stream, 200, new
								{
									success = true,
									removedId = id13,
									note = "Removed from retained alert history. If the condition is still present, diagnostics may create a new alert."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/operations/action-status")
						{
							string id14 = GetJsonString(request.Body, "id", "");
							string status4 = GetJsonString(request.Body, "status", "Open");
							switch (status4)
							{
							case "Open":
							case "Investigating":
							case "Resolved":
							case "Ignored":
								flag = true;
								break;
							default:
								flag = false;
								break;
							}
							if (!flag)
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Invalid action status."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								string actor12 = requestActor;
								ServerActionRecord action19 = await RunOnMainThreadAsync(() =>
								{
									ServerActionRecord serverActionRecord = operationsCenterStore.Actions.FirstOrDefault((ServerActionRecord x) => x.Id == id14);
									if (serverActionRecord == null)
									{
										return (ServerActionRecord)null;
									}
									serverActionRecord.Status = status4;
									serverActionRecord.UpdatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
									RecordWebAudit("action-status", serverActionRecord.Title + " -> " + status4, "Success", remoteText, actor12);
									SaveOperationsCenterStore();
									return serverActionRecord;
								}, "web-action-status").ConfigureAwait(continueOnCapturedContext: false);
								if (action19 == null)
								{
									await WriteJsonAsync(stream, 404, new
									{
										error = "Action not found."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									await WriteJsonAsync(stream, 200, new
									{
										success = true,
										action = action19
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "POST" && path == "/api/operations/performance-snapshot")
						{
							string label3 = GetJsonString(request.Body, "label", "Performance snapshot");
							string actor13 = requestActor;
							PerformanceSnapshotRouteResult capture = await RunOnMainThreadAsync(() =>
							{
								PerformanceSnapshotRecord performanceSnapshotRecord = CapturePerformanceSnapshot(label3, out string error);
								if (performanceSnapshotRecord == null)
								{
									return new PerformanceSnapshotRouteResult(Success: false, error, null);
								}
								RecordWebAudit("performance-snapshot", performanceSnapshotRecord.Label, "Success", remoteText, actor13);
								SaveOperationsCenterStore();
								return new PerformanceSnapshotRouteResult(Success: true, "Performance snapshot captured.", performanceSnapshotRecord);
							}, "web-performance-snapshot").ConfigureAwait(continueOnCapturedContext: false);
							await WriteJsonAsync(stream, capture.Success ? 200 : 400, capture, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/operations/config-snapshot")
						{
							string label4 = GetJsonString(request.Body, "label", "Manual snapshot");
							string actor14 = requestActor;
							ConfigSnapshotRecord snapshot3 = await RunOnMainThreadAsync(() =>
							{
								ConfigSnapshotRecord configSnapshotRecord = CaptureConfigSnapshot(label4, actor14);
								RecordWebAudit("config-snapshot", configSnapshotRecord.Label, "Success", remoteText, actor14);
								SaveOperationsCenterStore();
								return configSnapshotRecord;
							}, "web-config-snapshot").ConfigureAwait(continueOnCapturedContext: false);
							await WriteJsonAsync(stream, 200, new
							{
								success = true,
								snapshot = new { snapshot3.Id, snapshot3.UnixMilliseconds, snapshot3.Label, snapshot3.CreatedBy }
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/operations/config-restore")
						{
							string id15 = GetJsonString(request.Body, "id", "");
							string confirmation2 = GetJsonString(request.Body, "confirmation", "").Trim();
							if (!string.Equals(confirmation2, "RESTORE CONFIG", StringComparison.OrdinalIgnoreCase))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Safety confirmation failed. Type RESTORE CONFIG to restore a configuration snapshot."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								string actor15 = requestActor;
								ConfigRestoreRouteResult restore = await RunOnMainThreadAsync(() =>
								{
									ConfigSnapshotRecord configSnapshotRecord = operationsCenterStore.ConfigSnapshots.FirstOrDefault((ConfigSnapshotRecord x) => x.Id == id15);
									if (configSnapshotRecord == null)
									{
										return new ConfigRestoreRouteResult(Success: false, NotFound: true, "Configuration snapshot not found.");
									}
									CaptureConfigSnapshot("Automatic backup before restoring " + configSnapshotRecord.Label, actor15);
									string text4 = RestoreConfigSnapshot(configSnapshotRecord);
									bool flag3 = !text4.StartsWith("Restore failed", StringComparison.OrdinalIgnoreCase);
									RecordWebAudit("config-restore", configSnapshotRecord.Label, flag3 ? "Success" : text4, remoteText, actor15);
									SaveOperationsCenterStore();
									return new ConfigRestoreRouteResult(flag3, NotFound: false, text4);
								}, "web-config-restore").ConfigureAwait(continueOnCapturedContext: false);
								if (restore.NotFound)
								{
									await WriteJsonAsync(stream, 404, new
									{
										error = restore.Message
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									await WriteJsonAsync(stream, restore.Success ? 200 : 400, new
									{
										success = restore.Success,
										message = restore.Message
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "GET" && path == "/api/server/mods")
						{
							ServerHealthSnapshot healthSnapshot = this.module.ServerHealth?.GetSnapshot(60, 300);
							Dictionary<string, ServerProfilerModHotspot> profileByMod = (healthSnapshot?.ModHotspots ?? new List<ServerProfilerModHotspot>()).Where((ServerProfilerModHotspot x) => !string.IsNullOrWhiteSpace(x.ModId)).ToDictionary((ServerProfilerModHotspot x) => x.ModId, StringComparer.OrdinalIgnoreCase);
							var offThreadByMod = (healthSnapshot?.OffThreadReports ?? new List<ServerProfilerOffThreadReport>()).Where((ServerProfilerOffThreadReport x) => !string.IsNullOrWhiteSpace(x.ModId)).GroupBy((ServerProfilerOffThreadReport x) => x.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, ServerProfilerOffThreadReport> g) => g.Key, (IGrouping<string, ServerProfilerOffThreadReport> g) => new
							{
								count = g.Count(),
								maxMs = g.Max((ServerProfilerOffThreadReport x) => x.DurationMs),
								totalMs = g.Sum((ServerProfilerOffThreadReport x) => x.DurationMs)
							}, StringComparer.OrdinalIgnoreCase);
							bool profileAvailable = healthSnapshot != null && healthSnapshot.LastProfile?.CompletedUnixMilliseconds > 0;
							var mods = await RunOnMainThreadAsync(() => (from x in api.ModLoader.Mods.Where((Mod mod) => mod?.Info != null).Select((Mod mod) =>
								{
									string text4 = mod.Info.ModID ?? "";
									profileByMod.TryGetValue(text4, out var value5);
									offThreadByMod.TryGetValue(text4, out var value6);
									string text5 = value5?.Confidence;
									if (1 == 0)
									{
									}
									double num2 = text5 switch
									{
										"High" => 1.0, 
										"Medium" => 0.85, 
										"Low" => 0.65, 
										_ => 0.0, 
									};
									if (1 == 0)
									{
									}
									double num3 = num2;
									double num4 = ((value5 == null) ? 0.0 : (Math.Min(45.0, value5.PercentOfProfileTickTime * 1.5) + Math.Min(30.0, value5.MaxSingleMs / 4.0) + Math.Min(15.0, (double)value5.SlowSegments * 1.5) + Math.Min(10.0, (value6?.maxMs ?? 0.0) / 10.0)));
									int num5 = ((value5 != null) ? ((int)Math.Round(Math.Clamp(num4 * num3, 0.0, 100.0))) : 0);
									string impact = ((!profileAvailable) ? "Not measured" : ((value5 == null) ? "No measured impact" : ((num5 >= 70) ? "Severe" : ((num5 >= 45) ? "High" : ((num5 >= 20) ? "Moderate" : "Low")))));
									return new
									{
										modId = text4,
										name = (mod.Info.Name ?? mod.Info.ModID ?? ""),
										version = (mod.Info.Version ?? ""),
										type = mod.Info.Type.ToString(),
										side = mod.Info.Side.ToString(),
										core = mod.Info.CoreMod,
										requiredOnClient = mod.Info.RequiredOnClient,
										requiredOnServer = mod.Info.RequiredOnServer,
										systems = (mod.Systems?.Count ?? 0),
										fileName = (mod.FileName ?? ""),
										measured = (value5 != null),
										profileAvailable = profileAvailable,
										impactScore = num5,
										impact = impact,
										tickShare = (value5?.PercentOfProfileTickTime ?? 0.0),
										totalMs = (value5?.TotalMs ?? 0.0),
										maxSingleMs = (value5?.MaxSingleMs ?? 0.0),
										calls = (value5?.Calls ?? 0),
										slowSegments = (value5?.SlowSegments ?? 0),
										confidence = (value5?.Confidence ?? "None"),
										topMarker = (value5?.TopMarker ?? ""),
										offThreadReports = (value6?.count ?? 0),
										offThreadMaxMs = (value6?.maxMs ?? 0.0)
									};
								})
								orderby x.measured descending, x.impactScore descending, (!x.core) ? 1 : 0
								select x).ThenBy(x => x.modId, StringComparer.OrdinalIgnoreCase).ToList(), "web-loaded-mods").ConfigureAwait(continueOnCapturedContext: false);
							await WriteJsonAsync(stream, 200, new
							{
								count = mods.Count,
								profileAvailable = profileAvailable,
								profileCompletedUnixMilliseconds = (healthSnapshot?.LastProfile?.CompletedUnixMilliseconds).GetValueOrDefault(),
								profileDurationSeconds = (healthSnapshot?.LastProfile?.DurationSeconds).GetValueOrDefault(),
								profileP95TickMs = (healthSnapshot?.LastProfile?.P95TickMs).GetValueOrDefault(),
								profileMaxTickMs = (healthSnapshot?.LastProfile?.MaxTickMs).GetValueOrDefault(),
								mods = mods
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/server/mods/detail")
						{
							string modId5 = GetQuery(request, "modId", "").Trim();
							if (string.IsNullOrWhiteSpace(modId5))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "modId is required"
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								ServerModPerformanceDetails details = this.module.ServerHealth?.GetModPerformanceDetails(modId5) ?? new ServerModPerformanceDetails
								{
									ModId = modId5
								};
								var modInfo = await RunOnMainThreadAsync(() => (from x in api.ModLoader.Mods
									where x?.Info != null
									where string.Equals(x.Info.ModID, modId5, StringComparison.OrdinalIgnoreCase)
									select new
									{
										modId = (x.Info.ModID ?? ""),
										name = (x.Info.Name ?? x.Info.ModID ?? ""),
										version = (x.Info.Version ?? ""),
										type = x.Info.Type.ToString(),
										side = x.Info.Side.ToString(),
										core = x.Info.CoreMod,
										systems = (x.Systems?.Count ?? 0),
										fileName = (x.FileName ?? "")
									}).FirstOrDefault(), "web-mod-performance-detail").ConfigureAwait(continueOnCapturedContext: false);
								await WriteJsonAsync(stream, (modInfo == null) ? 404 : 200, new
								{
									mod = modInfo,
									details = details,
									note = "Profiler attribution is measured evidence from the most recent bounded engine profile. It identifies where profiled time was attributed; it does not by itself prove a mod bug."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/performance/overview")
						{
							int windowSeconds = Math.Clamp(GetQueryInt(request, "window", 1800), 60, 1800);
							await WriteJsonAsync(stream, 200, await GetCachedMainThreadReadAsync("performance-overview:" + windowSeconds, 1000, () => BuildPerformanceOverview(windowSeconds), "web-performance-overview").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/performance/page-snapshot")
						{
							int windowSeconds2 = Math.Clamp(GetQueryInt(request, "window", 1800), 60, 1800);
							await WriteJsonAsync(stream, 200, await GetCachedMainThreadReadAsync("performance-page-snapshot:" + windowSeconds2, 5000, () =>
							{
								MaintainPerformanceCenter();
								return new
								{
									generatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
									overview = BuildPerformanceOverview(windowSeconds2),
									timeline = BuildPerformanceTimeline(windowSeconds2),
									entityScan = new
									{
										available = (lastPerformanceEntityScan != null),
										snapshot = lastPerformanceEntityScan,
										manualOnly = true,
										note = "Entity/chunk hotspot scanning is manual-only so Live Updates cannot repeatedly walk every loaded entity."
									},
									entityGrowth = new
									{
										scans = performanceCenterStore.EntityScans.Count,
										rows = BuildEntityGrowthRows(),
										note = "Type-level growth compares the last two manual entity scans. Total loaded-entity growth is also visible continuously in the health timeline."
									},
									environment = new
									{
										current = performanceCenterStore.CurrentEnvironment,
										history = performanceCenterStore.EnvironmentHistory.OrderByDescending((PerformanceEnvironmentChange x) => x.DetectedUnixMilliseconds).Take(20).ToList()
									},
									providers = BuildOptimizationProviders(),
									conflicts = BuildPatchConflictSnapshot()
								};
							}, "web-performance-page-snapshot").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/performance/incident")
						{
							long start = GetQueryLong(request, "start", 0L);
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => BuildIncidentDrilldown(start), "web-performance-incident").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/performance/timeline")
						{
							int windowSeconds3 = Math.Clamp(GetQueryInt(request, "window", 1800), 60, 1800);
							await WriteJsonAsync(stream, 200, await GetCachedMainThreadReadAsync("performance-timeline:" + windowSeconds3, 1000, () => BuildPerformanceTimeline(windowSeconds3), "web-performance-timeline").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/performance/entity-growth")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => new
							{
								scans = performanceCenterStore.EntityScans.Count,
								rows = BuildEntityGrowthRows(),
								note = "Type-level growth compares the last two manual entity scans. Total loaded-entity growth is also visible continuously in the health timeline."
							}, "web-performance-entity-growth").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/performance/environment")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
							{
								MaintainPerformanceCenter();
								return new
								{
									current = performanceCenterStore.CurrentEnvironment,
									history = performanceCenterStore.EnvironmentHistory.OrderByDescending((PerformanceEnvironmentChange x) => x.DetectedUnixMilliseconds).Take(20).ToList()
								};
							}, "web-performance-environment").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/performance/bundle")
						{
							if (IsFeatureCircuitOpen("diagnostic-bundle", out string bundleCircuit))
							{
								await WriteJsonAsync(stream, 503, new
								{
									error = bundleCircuit
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								var core = await RunOnMainThreadAsync(() =>
								{
									MaintainPerformanceCenter();
									return new
									{
										overview = BuildPerformanceOverview(1800),
										timeline = BuildPerformanceTimeline(1800),
										providers = BuildOptimizationProviders(),
										conflicts = BuildPatchConflictSnapshot(),
										tungsten = BuildTungstenStatus(),
										entityGrowth = BuildEntityGrowthRows(),
										performanceStore = performanceCenterStore,
										operations = new
										{
											alerts = operationsCenterStore.Alerts,
											actions = operationsCenterStore.Actions,
											playerLoad = BuildPlayerLoadAnalysis(),
											worldSaves = operationsCenterStore.WorldSaves,
											networkSamples = operationsCenterStore.NetworkSamples,
											audit = (from x in operationsCenterStore.Audit.Take(200)
												select new
												{
													UnixMilliseconds = x.UnixMilliseconds,
													Actor = x.Actor,
													remote = "(redacted in diagnostic bundle)",
													Action = x.Action,
													Detail = x.Detail,
													Result = x.Result
												}).ToList(),
											configSnapshots = operationsCenterStore.ConfigSnapshots.Select((ConfigSnapshotRecord x) => new { x.Id, x.UnixMilliseconds, x.Label, x.CreatedBy }).ToList()
										},
										loadedMods = (from x in api.ModLoader.Mods
											where x?.Info != null
											select new
											{
												modId = (x.Info.ModID ?? ""),
												name = (x.Info.Name ?? ""),
												version = (x.Info.Version ?? ""),
												fileName = (x.FileName ?? "")
											}).OrderBy(x => x.modId, StringComparer.OrdinalIgnoreCase).ToList()
									};
								}, "web-performance-bundle-core").ConfigureAwait(continueOnCapturedContext: false);
								Dictionary<string, object> bundleExtras = await RunOnMainThreadAsync(() => new Dictionary<string, object>
								{
									["compatibility.json"] = BuildCompatibilitySnapshot(deep: false),
									["log-intelligence.json"] = logIntelligence?.GetSnapshot() ?? new LogIntelligenceSnapshot(),
									["player-analytics.json"] = this.module.PlayerAnalytics?.GetSnapshot(90) ?? new PlayerCountAnalyticsSnapshot(),
									["announcements.json"] = this.module.Announcements?.GetSnapshot() ?? new AnnouncementSnapshot(),
									["mod-updates.json"] = this.module.ModUpdates?.GetSnapshot() ?? new ModUpdateSnapshot(),
									["anti-cheat.json"] = this.module.AntiCheat?.GetSnapshot() ?? new AntiCheatWebSnapshot(),
									["feature-gates.json"] = new
									{
										configFile = "NewfiesServerEssentialsFeatures.json",
										restartRequiredForChanges = true,
										features = (from x in this.module.FeatureSettings.GetEntries()
											select new
											{
												name = x.Name,
												enabled = x.Enabled
											}).ToList(),
										activeFeatures = (from x in this.module.Features.GetEntries()
											select new
											{
												name = x.Name,
												enabled = x.Enabled
											}).ToList()
									},
									["data-collection-audit.json"] = BuildCollectionAuditSnapshot(),
									["login-protection.json"] = BuildLoginProtectionConfigSnapshot(),
									["suite-self-test.json"] = BuildServerUtilitiesHealth(),
									["web-diagnostics.json"] = BuildWebDiagnosticsSnapshot(),
									["smart-diagnosis.json"] = this.module.ServerHealth?.GetSnapshot(240) ?? new ServerHealthSnapshot(),
									["loaded-mods.json"] = (from x in api.ModLoader.Mods
										where x?.Info != null
										select new
										{
											modId = (x.Info.ModID ?? ""),
											name = (x.Info.Name ?? ""),
											version = (x.Info.Version ?? ""),
											fileName = Path.GetFileName(x.FileName ?? "")
										}).OrderBy(x => x.modId, StringComparer.OrdinalIgnoreCase).ToList()
								}, "web-diagnostic-bundle-v2-extras").ConfigureAwait(continueOnCapturedContext: false);
								object blockActivity;
								if (this.module.Queries != null)
								{
									blockActivity = new
									{
										minutes = 60,
										rows = await AwaitWithTimeout(this.module.Queries.QueryRecentBlockActivityHotspotsAsync(60, 100), TimeSpan.FromSeconds(8L), "Diagnostic bundle BlockLogger activity query").ConfigureAwait(continueOnCapturedContext: false),
										measurement = "BlockLogger activity count by coordinate; not CPU timing."
									};
								}
								else
								{
									blockActivity = new
									{
										unavailable = true,
										reason = "BlockLogger query service unavailable."
									};
								}
								byte[] bundle;
								try
								{
									bundle = MeasureFeature("diagnostic-bundle", "Diagnostic Bundle Export", () => BuildDiagnosticBundleBytes(core, blockActivity, bundleExtras));
									RecordFeatureSuccess("diagnostic-bundle", "Diagnostic Bundle Export", "Moderate");
								}
								catch (Exception ex34)
								{
									RecordFeatureFailure("diagnostic-bundle", ex34, "Diagnostic Bundle Export");
									await WriteJsonAsync(stream, 503, new
									{
										error = ex34.GetType().Name + ": " + ex34.Message
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									return;
								}
								await WriteResponseAsync(stream, 200, "application/zip", bundle, cancellationToken, new Dictionary<string, string> { ["Content-Disposition"] = $"attachment; filename=\"newfies-diagnostics-v2-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip\"" }).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/performance/entity-scan")
						{
							await WriteJsonAsync(stream, 200, new
							{
								available = (lastPerformanceEntityScan != null),
								snapshot = lastPerformanceEntityScan,
								manualOnly = true,
								note = "Entity/chunk hotspot scanning is manual-only so Live Updates cannot repeatedly walk every loaded entity."
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/performance/entity-scan")
						{
							if (IsFeatureCircuitOpen("entity-scan", out string entityScanCircuit))
							{
								await WriteJsonAsync(stream, 503, new
								{
									error = entityScanCircuit
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								PerformanceEntityScanSnapshot scan;
								try
								{
									scan = await RunOnMainThreadAsync(() => MeasureFeature("entity-scan", "Entity / Chunk Hotspot Scan", (Func<PerformanceEntityScanSnapshot>)BuildPerformanceEntityScan), "web-performance-entity-scan").ConfigureAwait(continueOnCapturedContext: false);
									RecordFeatureSuccess("entity-scan", "Entity / Chunk Hotspot Scan", "Moderate");
								}
								catch (Exception ex35)
								{
									RecordFeatureFailure("entity-scan", ex35, "Entity / Chunk Hotspot Scan");
									await WriteJsonAsync(stream, 503, new
									{
										error = ex35.GetType().Name + ": " + ex35.Message
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									return;
								}
								lastPerformanceEntityScan = scan;
								performanceCenterStore.EntityScans.Insert(0, scan);
								SavePerformanceCenterStore();
								api.Logger.Notification("[WebDashboard] admin {0} manually scanned entity/chunk performance hotspots from {1}: {2} positioned entities.", requestActor, remote, scan.PositionedEntities);
								RecordWebAudit("entity-chunk-scan", $"Manual scan: {scan.PositionedEntities:N0} positioned / {scan.TotalLoadedEntities:N0} total entities.", "Success", remote.ToString(), requestActor);
								await WriteJsonAsync(stream, 200, new
								{
									success = true,
									snapshot = scan,
									manualOnly = true
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/performance/block-activity")
						{
							LogQueryService queries = this.module.Queries;
							if (queries == null)
							{
								await WriteJsonAsync(stream, 503, new
								{
									error = "BlockLogger query service is unavailable."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								int minutes2 = Math.Clamp(GetQueryInt(request, "minutes", 30), 1, 1440);
								int limit5 = Math.Clamp(GetQueryInt(request, "limit", 50), 1, 200);
								List<PerformanceBlockActivityHotspot> rows = await AwaitWithTimeout(queries.QueryRecentBlockActivityHotspotsAsync(minutes2, limit5), TimeSpan.FromSeconds(8L), "BlockLogger activity hotspot query").ConfigureAwait(continueOnCapturedContext: false);
								await WriteJsonAsync(stream, 200, new
								{
									minutes = minutes2,
									count = rows.Count,
									rows = rows,
									measurement = "BlockLogger activity count by coordinate; this is not block-entity CPU timing."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/performance/conflicts")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(BuildPatchConflictSnapshot, "web-performance-conflicts").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/performance/providers")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(BuildOptimizationProviders, "web-performance-providers").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/optimizations/tungsten")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(BuildTungstenStatus, "web-tungsten-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/optimizations/tungsten/test")
						{
							string code2 = GetJsonString(request.Body, "code", "").Trim().ToLowerInvariant();
							bool enabled6 = GetJsonBool(request.Body, "enabled", fallback: true);
							if (!TungstenOptimizations.Any((TungstenOptimizationInfo x) => x.Code == code2))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Unknown Tungsten optimization code."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								OptimizationExperimentPreparation preparation = await RunOnMainThreadAsync(() => PrepareOptimizationExperiment("tungsten", code2, enabled6), "web-tungsten-test-baseline").ConfigureAwait(continueOnCapturedContext: false);
								if (!preparation.Success || preparation.Experiment == null)
								{
									await WriteJsonAsync(stream, 400, new
									{
										error = preparation.Message
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									TungstenCommandResult result52 = await ExecuteTungstenCommandAsync("/tungsten " + code2 + " " + (enabled6 ? "on" : "off"), "web-tungsten-test-" + code2).ConfigureAwait(continueOnCapturedContext: false);
									if (result52.Success)
									{
										await RunOnMainThreadAsync(() =>
										{
											optimizationExperiments.Pending = preparation.Experiment;
											SaveOptimizationExperiments();
											return true;
										}, "web-tungsten-test-save").ConfigureAwait(continueOnCapturedContext: false);
									}
									api.Logger.Notification("[WebDashboard] admin {0} requested measured Tungsten {1}={2} from {3}. Result={4}.", requestActor, code2, enabled6, remote, result52.Status);
									RecordWebAudit("optimization-measure", "Tungsten " + code2 + " -> " + (enabled6 ? "ON" : "OFF"), result52.Status, remote.ToString(), requestActor);
									await WriteJsonAsync(stream, result52.Success ? 200 : 400, new
									{
										success = result52.Success,
										status = result52.Status,
										message = (result52.Success ? (result52.Message + " Baseline saved. Restart the server, let diagnostics collect for at least 60 seconds, then return to Optimizations to evaluate improvement.") : result52.Message),
										command = result52.Command,
										experiment = (result52.Success ? preparation.Experiment : null)
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "POST" && path == "/api/optimizations/experiment/evaluate")
						{
							bool allowSameRuntime = GetJsonBool(request.Body, "allowSameRuntime", fallback: false);
							OptimizationExperimentEvaluationResult evaluation = await RunOnMainThreadAsync(() => EvaluatePendingOptimizationExperiment(allowSameRuntime), "web-optimization-evaluate").ConfigureAwait(continueOnCapturedContext: false);
							await WriteJsonAsync(stream, evaluation.Success ? 200 : 400, evaluation, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/optimizations/tungsten/toggle")
						{
							string code3 = GetJsonString(request.Body, "code", "").Trim().ToLowerInvariant();
							bool enabled7 = GetJsonBool(request.Body, "enabled", fallback: true);
							if (!TungstenOptimizations.Any((TungstenOptimizationInfo x) => x.Code == code3))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Unknown Tungsten optimization code."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								TungstenCommandResult result53 = await ExecuteTungstenCommandAsync("/tungsten " + code3 + " " + (enabled7 ? "on" : "off"), "web-tungsten-" + code3).ConfigureAwait(continueOnCapturedContext: false);
								api.Logger.Notification("[WebDashboard] admin {0} requested Tungsten {1}={2} from {3}. Result={4}.", requestActor, code3, enabled7, remote, result53.Status);
								RecordWebAudit("optimization-toggle", "Tungsten " + code3 + " -> " + (enabled7 ? "ON" : "OFF"), result53.Status, remote.ToString(), requestActor);
								await WriteJsonAsync(stream, result53.Success ? 200 : 400, result53, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/optimizations/tungsten/all")
						{
							bool enabled8 = GetJsonBool(request.Body, "enabled", fallback: true);
							TungstenCommandResult result54 = await ExecuteTungstenCommandAsync("/tungsten all " + (enabled8 ? "on" : "off"), "web-tungsten-all").ConfigureAwait(continueOnCapturedContext: false);
							api.Logger.Notification("[WebDashboard] admin {0} requested Tungsten ALL={1} from {2}. Result={3}.", requestActor, enabled8, remote, result54.Status);
							RecordWebAudit("optimization-all", "Tungsten all -> " + (enabled8 ? "ON" : "OFF"), result54.Status, remote.ToString(), requestActor);
							await WriteJsonAsync(stream, result54.Success ? 200 : 400, result54, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/optimizations/tungsten/action")
						{
							string action20 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
							string command2;
							switch (action20)
							{
							case "status":
								command2 = "/tungsten";
								break;
							case "stats":
								command2 = "/tungsten stats";
								break;
							case "stats-on":
								command2 = "/tungsten stats on";
								break;
							case "stats-off":
								command2 = "/tungsten stats off";
								break;
							case "reload":
								command2 = "/tungsten reload";
								break;
							case "health":
								command2 = "/tungsten health";
								break;
							case "manifest":
								command2 = "/tungsten manifest";
								break;
							case "benchmark-on":
								command2 = "/tungsten benchmarkharness on";
								break;
							case "benchmark-off":
								command2 = "/tungsten benchmarkharness off";
								break;
							case "frameprofiler-on":
							{
								int thresholdMs = Math.Clamp(GetJsonInt(request.Body, "thresholdMs", 100), 10, 5000);
								command2 = $"/tungsten frameprofiler on {thresholdMs}";
								break;
							}
							case "frameprofiler-off":
								command2 = "/tungsten frameprofiler off";
								break;
							case "diag-on":
							case "diag-off":
							case "diag-dump":
							case "diag-reset":
							{
								string module = GetJsonString(request.Body, "module", "all").Trim().ToLowerInvariant();
								if (module != "all" && !TungstenOptimizations.Any((TungstenOptimizationInfo x) => x.Code == module))
								{
									await WriteJsonAsync(stream, 400, new
									{
										error = "Unknown Tungsten diagnostic module."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
									return;
								}
								command2 = string.Concat(str3: action20.Substring("diag-".Length), str0: "/tungsten diag ", str1: module, str2: " ");
								break;
							}
							default:
								await WriteJsonAsync(stream, 400, new
								{
									error = "Unknown Tungsten action."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							TungstenCommandResult result55 = await ExecuteTungstenCommandAsync(command2, "web-tungsten-action").ConfigureAwait(continueOnCapturedContext: false);
							api.Logger.Notification("[WebDashboard] admin {0} ran Tungsten action {1} from {2}. Result={3}.", requestActor, action20, remote, result55.Status);
							RecordWebAudit("optimization-action", "Tungsten action " + action20, result55.Status, remote.ToString(), requestActor);
							await WriteJsonAsync(stream, result55.Success ? 200 : 400, result55, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/optimizations/synergy")
						{
							TungstenCommandResult statusResult = await ExecuteSynergyCommandAsync("/synergy", "web-synergy-status").ConfigureAwait(continueOnCapturedContext: false);
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => BuildSynergyStatus(statusResult), "web-synergy-status-build").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/optimizations/synergy/test")
						{
							string code4 = GetJsonString(request.Body, "code", "").Trim().ToLowerInvariant();
							bool enabled9 = GetJsonBool(request.Body, "enabled", fallback: true);
							if (!SynergyOptimizations.Any((SynergyOptimizationInfo x) => x.Code == code4))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Unknown Synergy optimization code."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								OptimizationExperimentPreparation preparation2 = await RunOnMainThreadAsync(() => PrepareOptimizationExperiment("synergy", code4, enabled9), "web-synergy-test-baseline").ConfigureAwait(continueOnCapturedContext: false);
								if (!preparation2.Success || preparation2.Experiment == null)
								{
									await WriteJsonAsync(stream, 400, new
									{
										error = preparation2.Message
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									TungstenCommandResult result56 = await ExecuteSynergyCommandAsync("/synergy " + code4 + " " + (enabled9 ? "on" : "off"), "web-synergy-test-" + code4).ConfigureAwait(continueOnCapturedContext: false);
									if (result56.Success)
									{
										await RunOnMainThreadAsync(() =>
										{
											optimizationExperiments.Pending = preparation2.Experiment;
											SaveOptimizationExperiments();
											return true;
										}, "web-synergy-test-save").ConfigureAwait(continueOnCapturedContext: false);
									}
									api.Logger.Notification("[WebDashboard] admin {0} requested measured Synergy {1}={2} from {3}. Result={4}.", requestActor, code4, enabled9, remote, result56.Status);
									RecordWebAudit("optimization-measure", "Synergy " + code4 + " -> " + (enabled9 ? "ON" : "OFF"), result56.Status, remote.ToString(), requestActor);
									await WriteJsonAsync(stream, result56.Success ? 200 : 400, new
									{
										success = result56.Success,
										status = result56.Status,
										message = (result56.Success ? (result56.Message + " Baseline saved. Restart the server, let diagnostics collect for at least 60 seconds, then return to Optimizations to evaluate improvement.") : result56.Message),
										command = result56.Command,
										experiment = (result56.Success ? preparation2.Experiment : null)
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "POST" && path == "/api/optimizations/synergy/toggle")
						{
							string code5 = GetJsonString(request.Body, "code", "").Trim().ToLowerInvariant();
							bool enabled10 = GetJsonBool(request.Body, "enabled", fallback: true);
							if (!SynergyOptimizations.Any((SynergyOptimizationInfo x) => x.Code == code5))
							{
								await WriteJsonAsync(stream, 400, new
								{
									error = "Unknown Synergy optimization code."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								TungstenCommandResult result57 = await ExecuteSynergyCommandAsync("/synergy " + code5 + " " + (enabled10 ? "on" : "off"), "web-synergy-" + code5).ConfigureAwait(continueOnCapturedContext: false);
								api.Logger.Notification("[WebDashboard] admin {0} requested Synergy {1}={2} from {3}. Result={4}.", requestActor, code5, enabled10, remote, result57.Status);
								RecordWebAudit("optimization-toggle", "Synergy " + code5 + " -> " + (enabled10 ? "ON" : "OFF"), result57.Status, remote.ToString(), requestActor);
								await WriteJsonAsync(stream, result57.Success ? 200 : 400, result57, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/optimizations/synergy/all")
						{
							bool enabled11 = GetJsonBool(request.Body, "enabled", fallback: true);
							TungstenCommandResult result58 = await ExecuteSynergyCommandAsync("/synergy all " + (enabled11 ? "on" : "off"), "web-synergy-all").ConfigureAwait(continueOnCapturedContext: false);
							api.Logger.Notification("[WebDashboard] admin {0} requested Synergy ALL={1} from {2}. Result={3}.", requestActor, enabled11, remote, result58.Status);
							RecordWebAudit("optimization-all", "Synergy all -> " + (enabled11 ? "ON" : "OFF"), result58.Status, remote.ToString(), requestActor);
							await WriteJsonAsync(stream, result58.Success ? 200 : 400, result58, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/optimizations/synergy/action")
						{
							string action21 = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
							string command3;
							switch (action21)
							{
							case "status":
								command3 = "/synergy";
								break;
							case "stats":
								command3 = "/synergy stats";
								break;
							case "reload":
								command3 = "/synergy reload";
								break;
							case "diag-on":
							case "diag-off":
							case "diag-dump":
							case "diag-reset":
							{
								string module2 = GetJsonString(request.Body, "module", "all").Trim().ToLowerInvariant();
								if (string.IsNullOrWhiteSpace(module2))
								{
									module2 = "all";
								}
								string verb = action21.Substring("diag-".Length);
								command3 = "/synergy diag " + module2 + " " + verb;
								break;
							}
							default:
								await WriteJsonAsync(stream, 400, new
								{
									error = "Unknown Synergy action."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								return;
							}
							TungstenCommandResult result59 = await ExecuteSynergyCommandAsync(command3, "web-synergy-action").ConfigureAwait(continueOnCapturedContext: false);
							api.Logger.Notification("[WebDashboard] admin {0} ran Synergy action {1} from {2}. Result={3}.", requestActor, action21, remote, result59.Status);
							RecordWebAudit("optimization-action", "Synergy action " + action21, result59.Status, remote.ToString(), requestActor);
							await WriteJsonAsync(stream, result59.Success ? 200 : 400, result59, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/blocklogger/maintenance")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
							{
								LogStorageService storage4 = this.module.Storage;
								DatabaseHealthSnapshot databaseHealthSnapshot = storage4?.GetDatabaseHealth(this.module.Config.RetentionDays);
								return new
								{
									available = (storage4 != null),
									retentionDays = this.module.Config.RetentionDays,
									autoBackupIntervalHours = this.module.Config.AutoDatabaseBackupIntervalHours,
									backupRetentionCount = this.module.Config.DatabaseBackupRetentionCount,
									databaseBytes = (databaseHealthSnapshot?.DatabaseBytes ?? 0),
									walBytes = (databaseHealthSnapshot?.WalBytes ?? 0),
									backupBytes = (databaseHealthSnapshot?.BackupBytes ?? 0),
									backupCount = (databaseHealthSnapshot?.BackupCount ?? 0),
									backupInProgress = (databaseHealthSnapshot?.BackupInProgress ?? false),
									databasePath = (databaseHealthSnapshot?.Path ?? ""),
									note = "Automatic retention deletes old rows gradually. Manual purge can target individual data categories; VACUUM is optional because it can be expensive on a large live database."
								};
							}, "web-blocklogger-maintenance-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/blocklogger/maintenance/config")
						{
							int retentionDays = Math.Clamp(GetJsonInt(request.Body, "retentionDays", this.module.Config.RetentionDays), 0, 3650);
							int autoBackupIntervalHours = Math.Clamp(GetJsonInt(request.Body, "autoBackupIntervalHours", this.module.Config.AutoDatabaseBackupIntervalHours), 0, 8760);
							int backupRetentionCount = Math.Clamp(GetJsonInt(request.Body, "backupRetentionCount", this.module.Config.DatabaseBackupRetentionCount), 1, 20);
							var payload38 = await RunOnMainThreadAsync(() =>
							{
								this.module.Config.RetentionDays = retentionDays;
								this.module.Config.AutoDatabaseBackupIntervalHours = autoBackupIntervalHours;
								this.module.Config.DatabaseBackupRetentionCount = backupRetentionCount;
								this.module.SaveRuntimeConfig();
								return new
								{
									success = true,
									retentionDays = this.module.Config.RetentionDays,
									autoBackupIntervalHours = this.module.Config.AutoDatabaseBackupIntervalHours,
									backupRetentionCount = this.module.Config.DatabaseBackupRetentionCount,
									message = ((this.module.Config.AutoDatabaseBackupIntervalHours <= 0) ? $"Saved. Automatic database backups are disabled; {this.module.Config.DatabaseBackupRetentionCount} existing healthy backup(s) will be retained at most." : $"Saved. Automatic backups are eligible every {this.module.Config.AutoDatabaseBackupIntervalHours} hour(s), retaining at most {this.module.Config.DatabaseBackupRetentionCount} healthy backup file(s).")
								};
							}, "web-blocklogger-maintenance-config").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("blocklogger-storage-policy", $"retention={retentionDays}d autoBackup={autoBackupIntervalHours}h backups={backupRetentionCount}", "Success", remoteText, requestActor);
							await WriteJsonAsync(stream, 200, payload38, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/blocklogger/purge")
						{
							LogStorageService storage3 = this.module.Storage;
							if (storage3 == null)
							{
								await WriteJsonAsync(stream, 503, new
								{
									error = "BlockLogger storage is unavailable."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								int keepDays = Math.Clamp(GetJsonInt(request.Body, "keepDays", 30), 0, 3650);
								bool purgeEvents = GetJsonBool(request.Body, "purgeEvents", fallback: true);
								bool purgeSnapshots = GetJsonBool(request.Body, "purgeSnapshots", fallback: true);
								bool purgeEntityActivity = GetJsonBool(request.Body, "purgeEntityActivity", fallback: true);
								bool compactAfter = GetJsonBool(request.Body, "compactAfter", fallback: false);
								BlockLoggerPurgeResult result60 = await storage3.PurgeDataAsync(keepDays, purgeEvents, purgeSnapshots, purgeEntityActivity, compactAfter).ConfigureAwait(continueOnCapturedContext: false);
								RecordWebAudit("blocklogger-purge", $"keep={keepDays}d events={purgeEvents} snapshots={purgeSnapshots} entityActivity={purgeEntityActivity} compact={compactAfter}", result60.Success ? $"Removed {result60.TotalRemoved:N0}" : result60.Message, remoteText, requestActor);
								if (result60.Success)
								{
									await WriteJsonAsync(stream, 200, result60, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									await WriteJsonAsync(stream, 409, new
									{
										error = result60.Message
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "GET" && path == "/api/blocklogger/player")
						{
							await HandlePlayerQueryAsync(stream, request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/blocklogger/nearby")
						{
							await HandleNearbyQueryAsync(stream, request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/animals/status")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
							{
								AnimalLimiterSystem current = AnimalLimiterSystem.Current;
								return new
								{
									available = (current != null),
									lines = (current?.GetGuiStatusLines() ?? new List<string> { "Animal Limiter is not available." })
								};
							}, "web-animal-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/animals/rules")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => AnimalLimiterSystem.Current?.GetWebRuleSnapshot() ?? new AnimalLimiterRuleSnapshot
							{
								Available = false
							}, "web-animal-rules").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/animals/rules/set")
						{
							string pattern = GetJsonString(request.Body, "pattern", "").Trim();
							int limit6 = Math.Clamp(GetJsonInt(request.Body, "limit", 0), 0, 1000000);
							int radius = Math.Clamp(GetJsonInt(request.Body, "radiusChunks", 0), 0, 32);
							string message8 = await RunOnMainThreadAsync(() => AnimalLimiterSystem.Current?.SetRuleFromGui(pattern, limit6, radius) ?? "Animal Limiter is not available.", "web-animal-rule-set").ConfigureAwait(continueOnCapturedContext: false);
							bool success9 = !message8.Contains("not available", StringComparison.OrdinalIgnoreCase) && !message8.StartsWith("Enter", StringComparison.OrdinalIgnoreCase);
							RecordWebAudit("animal-rule-set", pattern, success9 ? "Success" : message8, remoteText, requestActor);
							await WriteJsonAsync(stream, success9 ? 200 : 400, new
							{
								success = success9,
								message = message8
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/animals/rules/remove")
						{
							string pattern2 = GetJsonString(request.Body, "pattern", "").Trim();
							string message9 = await RunOnMainThreadAsync(() => AnimalLimiterSystem.Current?.RemoveRuleFromGui(pattern2) ?? "Animal Limiter is not available.", "web-animal-rule-remove").ConfigureAwait(continueOnCapturedContext: false);
							bool success10 = message9.StartsWith("Removed rule", StringComparison.OrdinalIgnoreCase);
							RecordWebAudit("animal-rule-remove", pattern2, success10 ? "Success" : message9, remoteText, requestActor);
							await WriteJsonAsync(stream, success10 ? 200 : 400, new
							{
								success = success10,
								message = message9
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/animals/exclusions/add")
						{
							string pattern3 = GetJsonString(request.Body, "pattern", "").Trim();
							string message10 = await RunOnMainThreadAsync(() => AnimalLimiterSystem.Current?.AddExclusionFromGui(pattern3) ?? "Animal Limiter is not available.", "web-animal-exclusion-add").ConfigureAwait(continueOnCapturedContext: false);
							bool success11 = message10.StartsWith("Excluded ", StringComparison.OrdinalIgnoreCase) || message10.Contains("already exists", StringComparison.OrdinalIgnoreCase) || message10.Contains("already permanently protected", StringComparison.OrdinalIgnoreCase);
							RecordWebAudit("animal-exclusion-add", pattern3, success11 ? "Success" : message10, remoteText, requestActor);
							await WriteJsonAsync(stream, success11 ? 200 : 400, new
							{
								success = success11,
								message = message10
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/animals/exclusions/remove")
						{
							string pattern4 = GetJsonString(request.Body, "pattern", "").Trim();
							string message11 = await RunOnMainThreadAsync(() => AnimalLimiterSystem.Current?.RemoveExclusionFromGui(pattern4) ?? "Animal Limiter is not available.", "web-animal-exclusion-remove").ConfigureAwait(continueOnCapturedContext: false);
							bool success12 = message11.StartsWith("Removed exclusion", StringComparison.OrdinalIgnoreCase);
							RecordWebAudit("animal-exclusion-remove", pattern4, success12 ? "Success" : message11, remoteText, requestActor);
							await WriteJsonAsync(stream, success12 ? 200 : 400, new
							{
								success = success12,
								message = message11
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/animals/recommendations/start")
						{
							try
							{
								AnimalPopulationScanState payload39 = await RunOnMainThreadAsync(() =>
								{
									AnimalLimiterSystem current = AnimalLimiterSystem.Current;
									return (current == null) ? new AnimalPopulationScanState
									{
										Completed = true,
										Error = "Animal Limiter is not available."
									} : current.BeginRecommendationScan();
								}, "web-animal-recommendation-scan-start").ConfigureAwait(continueOnCapturedContext: false);
								RecordWebAudit("animal-recommendation-scan", $"Started Animal Limiter recommendation scan {payload39.ScanId}.", string.IsNullOrWhiteSpace(payload39.Error) ? "Started" : "Unavailable", remoteText, requestActor);
								await WriteJsonAsync(stream, string.IsNullOrWhiteSpace(payload39.Error) ? 200 : 503, payload39, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							catch (Exception ex36)
							{
								api.Logger.Warning("[WebDashboard] Animal Limiter recommendation scan failed safely: {0}: {1}", ex36.GetType().Name, ex36.Message);
								await WriteJsonAsync(stream, 503, new
								{
									error = "Recommendation scan could not start: " + ex36.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/animals/recommendations")
						{
							long scanId = 0L;
							if (request.Query.TryGetValue("id", out string recScanIdText))
							{
								long.TryParse(recScanIdText, out scanId);
							}
							AnimalLimiterRecommendationSnapshot payload40 = await RunOnMainThreadAsync(() => AnimalLimiterSystem.Current?.GetWebRecommendations(scanId) ?? new AnimalLimiterRecommendationSnapshot
							{
								ScanId = scanId,
								Error = "Animal Limiter is not available."
							}, "web-animal-recommendations").ConfigureAwait(continueOnCapturedContext: false);
							await WriteJsonAsync(stream, string.IsNullOrWhiteSpace(payload40.Error) ? 200 : 409, payload40, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/animals/scan")
						{
							try
							{
								AnimalPopulationScanState payload41 = await RunOnMainThreadAsync(() =>
								{
									AnimalLimiterSystem current = AnimalLimiterSystem.Current;
									return (current == null) ? new AnimalPopulationScanState
									{
										Completed = true,
										Error = "Animal Limiter is not available."
									} : current.BeginPopulationScan();
								}, "web-animal-population-scan-start").ConfigureAwait(continueOnCapturedContext: false);
								api.Logger.Notification("[WebDashboard] admin {0} started Animal Limiter population scan {1} from {2}.", requestActor, payload41.ScanId, remote);
								RecordWebAudit("animal-population-scan", $"Started incremental Animal Limiter population scan {payload41.ScanId}.", string.IsNullOrWhiteSpace(payload41.Error) ? "Started" : "Unavailable", remote.ToString(), requestActor);
								await WriteJsonAsync(stream, string.IsNullOrWhiteSpace(payload41.Error) ? 200 : 503, payload41, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							catch (Exception ex37)
							{
								api.Logger.Warning("[WebDashboard] Animal Limiter population scan start failed safely: {0}: {1}", ex37.GetType().Name, ex37.Message);
								await WriteJsonAsync(stream, 503, new
								{
									error = "Population scan could not start: " + ex37.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/animals/scan-status")
						{
							long scanId2 = 0L;
							if (request.Query.TryGetValue("id", out string scanIdText))
							{
								long.TryParse(scanIdText, out scanId2);
							}
							try
							{
								await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
								{
									AnimalLimiterSystem current = AnimalLimiterSystem.Current;
									return (current == null) ? new AnimalPopulationScanState
									{
										ScanId = scanId2,
										Completed = true,
										Error = "Animal Limiter is not available."
									} : current.GetPopulationScanState(scanId2);
								}, "web-animal-population-scan-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							catch (Exception ex38)
							{
								api.Logger.Warning("[WebDashboard] Animal Limiter population scan status failed safely: {0}: {1}", ex38.GetType().Name, ex38.Message);
								await WriteJsonAsync(stream, 503, new
								{
									error = "Population scan status unavailable: " + ex38.Message
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "GET" && path == "/api/loginprotection/config")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() => BuildLoginProtectionConfigSnapshot(), "web-loginprotection-config").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/loginprotection/config")
						{
							string actor16 = requestActor;
							LoginProtectionConfigUpdateResult result61 = await RunOnMainThreadAsync(() =>
							{
								CaptureConfigSnapshot("Automatic backup before Login Protection web edit", actor16);
								return ApplyLoginProtectionConfig(request.Body);
							}, "web-loginprotection-config-save").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("login-protection-config", "Login Protection settings updated from the web dashboard.", result61.Message, remote.ToString(), actor16);
							object responsePayload;
							if (result61.Success)
							{
								object savedConfig = await RunOnMainThreadAsync(BuildLoginProtectionConfigSnapshot, "web-loginprotection-config-after-save").ConfigureAwait(continueOnCapturedContext: false);
								responsePayload = new
								{
									success = true,
									message = result61.Message,
									config = savedConfig
								};
							}
							else
							{
								responsePayload = new
								{
									success = false,
									error = result61.Message
								};
							}
							await WriteJsonAsync(stream, result61.Success ? 200 : 400, responsePayload, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/loginprotection/status")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(() =>
							{
								LoginProtectionSystem current = LoginProtectionSystem.Current;
								return new
								{
									available = (current != null),
									lines = (current?.GetGuiStatusLines() ?? new List<string> { "Login Protection is not available." })
								};
							}, "web-loginprotection-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/tick-debug")
						{
							await WriteJsonAsync(stream, 200, await RunOnMainThreadAsync(BuildNativeTickDebugSnapshot, "web-tick-debug-status").ConfigureAwait(continueOnCapturedContext: false), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/tick-debug/enabled")
						{
							bool enabled12 = GetJsonBool(request.Body, "enabled", fallback: true);
							WebActionResult result62 = await RunOnMainThreadAsync(() =>
							{
								config.TickDebuggingEnabled = enabled12;
								SaveConfig();
								if (!enabled12 && nativeSlowTickCaptureRunning)
								{
									StopNativeSlowTickCaptureNow();
								}
								return new WebActionResult(Success: true, enabled12 ? "Tick Debugging enabled." : "Tick Debugging disabled.");
							}, "web-tick-debug-enabled").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("tick-debug-enabled", enabled12 ? "enabled" : "disabled", "Success", remote.ToString(), requestActor);
							await WriteJsonAsync(stream, 200, new
							{
								success = result62.Success,
								message = result62.Message,
								enabled = enabled12
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/tick-debug/slow-start")
						{
							if (!config.TickDebuggingEnabled)
							{
								await WriteJsonAsync(stream, 409, new
								{
									success = false,
									message = "Tick Debugging is disabled. Enable it before starting a capture."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								int thresholdMs2 = Math.Clamp(GetJsonInt(request.Body, "thresholdMs", 100), 20, 1000);
								int durationSeconds = Math.Clamp(GetJsonInt(request.Body, "durationSeconds", 30), 5, 120);
								WebActionResult result63 = await RunOnMainThreadAsync(() => StartNativeSlowTickCapture(thresholdMs2, durationSeconds, incidentCapture: false), "web-tick-debug-slow-start").ConfigureAwait(continueOnCapturedContext: false);
								RecordWebAudit("native-slow-ticks", $"Started {durationSeconds}s native slow-tick capture at {thresholdMs2}ms.", result63.Success ? "Success" : result63.Message, remote.ToString(), requestActor);
								await WriteJsonAsync(stream, result63.Success ? 200 : 409, result63, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/tick-debug/slow-stop")
						{
							WebActionResult result64 = await RunOnMainThreadAsync(StopNativeSlowTickCaptureNow, "web-tick-debug-slow-stop").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("native-slow-ticks", "Stopped native slow-tick capture.", result64.Success ? "Success" : result64.Message, remote.ToString(), requestActor);
							await WriteJsonAsync(stream, result64.Success ? 200 : 409, result64, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/tick-debug/incident")
						{
							if (!config.TickDebuggingEnabled)
							{
								await WriteJsonAsync(stream, 409, new
								{
									success = false,
									message = "Tick Debugging is disabled. Enable it before starting an incident capture."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								int thresholdMs3 = Math.Clamp(GetJsonInt(request.Body, "thresholdMs", 100), 20, 1000);
								int durationSeconds2 = Math.Clamp(GetJsonInt(request.Body, "durationSeconds", 15), 5, 30);
								WebActionResult result65 = await RunOnMainThreadAsync(() => StartNativeSlowTickCapture(thresholdMs3, durationSeconds2, incidentCapture: true), "web-tick-debug-incident").ConfigureAwait(continueOnCapturedContext: false);
								RecordWebAudit("native-incident-capture", $"Started {durationSeconds2}s incident capture at {thresholdMs3}ms.", result65.Success ? "Success" : result65.Message, remote.ToString(), requestActor);
								await WriteJsonAsync(stream, result65.Success ? 200 : 409, result65, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
						}
						else if (request.Method == "POST" && path == "/api/tick-debug/action")
						{
							if (!config.TickDebuggingEnabled)
							{
								await WriteJsonAsync(stream, 409, new
								{
									success = false,
									message = "Tick Debugging is disabled. Enable it before running native debug actions."
								}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
							}
							else
							{
								string action22 = GetJsonString(request.Body, "action", "tickhandlers").Trim().ToLowerInvariant();
								if (1 == 0)
								{
								}
								string text = action22 switch
								{
									"tickhandlers" => "/debug tickhandlers", 
									"mainthreadstate" => "/debug mainthreadstate", 
									"dump-gtblock" => "/debug tickhandlers dump gtblock", 
									"dump-gtentity" => "/debug tickhandlers dump gtentity", 
									"dump-dcblock" => "/debug tickhandlers dump dcblock", 
									"dump-sdcblock" => "/debug tickhandlers dump sdcblock", 
									"dump-dcentity" => "/debug tickhandlers dump dcentity", 
									_ => "", 
								};
								if (1 == 0)
								{
								}
								string command4 = text;
								if (command4.Length == 0)
								{
									await WriteJsonAsync(stream, 400, new
									{
										success = false,
										error = "Unsupported native debug action."
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								else
								{
									string logPath = (action22.StartsWith("dump-", StringComparison.Ordinal) ? ResolveNativeTickDebugLogPath(debugLog: true) : ResolveNativeTickDebugLogPath());
									long logOffset = SafeFileLength(logPath);
									WebConsoleCommandResult commandResult = await ExecuteWebConsoleCommandAsync(command4).ConfigureAwait(continueOnCapturedContext: false);
									string logDelta = ReadNativeTickDebugLogDelta(logPath, logOffset, 65536);
									lock (tickDebugLock)
									{
										nativeTickDebugLastAction = action22;
										nativeTickDebugLastActionUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
										nativeTickDebugLastOutput = CombineNativeDebugOutput(commandResult.Message, logDelta);
									}
									RecordWebAudit("native-tick-debug", "Executed " + command4 + ".", commandResult.Success ? "Success" : commandResult.Message, remote.ToString(), requestActor);
									await WriteJsonAsync(stream, commandResult.Success ? 200 : 409, new
									{
										success = commandResult.Success,
										message = commandResult.Message,
										action = action22
									}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
						else if (request.Method == "POST" && path == "/api/tick-debug/clear")
						{
							string target4 = GetJsonString(request.Body, "target", "all").Trim().ToLowerInvariant();
							WebActionResult result66 = await RunOnMainThreadAsync(() =>
							{
								lock (tickDebugLock)
								{
									string text4 = target4;
									if ((text4 == "action" || text4 == "all") ? true : false)
									{
										nativeTickDebugLastAction = "";
										nativeTickDebugLastActionUnixMilliseconds = 0L;
										nativeTickDebugLastOutput = "";
									}
									text4 = target4;
									if ((text4 == "slow" || text4 == "all") ? true : false)
									{
										nativeSlowTickLastExcerpt = "";
										nativeTickDebugLastIncidentSummary = "";
									}
								}
								return new WebActionResult(Success: true, (target4 == "all") ? "Tick Debugger output cleared." : ("Tick Debugger " + target4 + " output cleared."));
							}, "web-tick-debug-clear").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("native-tick-debug-clear", "Cleared Tick Debugger output target=" + target4 + ".", "Success", remote.ToString(), requestActor);
							await WriteJsonAsync(stream, 200, result66, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/profiler/clear")
						{
							string target5 = GetJsonString(request.Body, "target", "all").Trim().ToLowerInvariant();
							var result67 = await RunOnMainThreadAsync(() =>
							{
								ServerHealthService serverHealth = this.module.ServerHealth;
								if (serverHealth == null)
								{
									return new
									{
										success = false,
										message = "Server profiler is not available.",
										cleared = 0
									};
								}
								if (1 == 0)
								{
								}
								int num2 = target5 switch
								{
									"mods" => serverHealth.ClearModHotspots(), 
									"ticks" => serverHealth.ClearProfileSlowTicks(), 
									"all" => serverHealth.ClearModHotspots() + serverHealth.ClearProfileSlowTicks(), 
									_ => -1, 
								};
								if (1 == 0)
								{
								}
								int num3 = num2;
								if (num3 < 0)
								{
									return new
									{
										success = false,
										message = "Unsupported profiler clear target.",
										cleared = 0
									};
								}
								InvalidateReadOnlySnapshotCaches();
								return new
								{
									success = true,
									message = ((target5 == "all") ? "Retained profiler result lists cleared." : ("Retained profiler " + target5 + " cleared.")),
									cleared = num3
								};
							}, "web-profiler-clear").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("profiler-clear", "Cleared retained profiler target=" + target5 + ".", "Success", remote.ToString(), requestActor);
							await WriteJsonAsync(stream, 200, result67, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/performance/lag-history/clear")
						{
							var result68 = await RunOnMainThreadAsync(() =>
							{
								int liveCleared = this.module.ServerHealth?.ClearDiagnosticIncidents() ?? 0;
								int archivedCleared = performanceCenterStore.Incidents?.Count ?? 0;
								performanceCenterStore.Incidents?.Clear();
								SavePerformanceCenterStore();
								InvalidateReadOnlySnapshotCaches();
								return new
								{
									success = true,
									message = "Retained lag diagnostic history cleared.",
									liveCleared = liveCleared,
									archivedCleared = archivedCleared
								};
							}, "web-performance-lag-history-clear").ConfigureAwait(continueOnCapturedContext: false);
							RecordWebAudit("lag-history-clear", "Cleared retained Performance/Profiler lag diagnostic history.", "Success", remote.ToString(), requestActor);
							await WriteJsonAsync(stream, 200, result68, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "GET" && path == "/api/profiler")
						{
							int windowSeconds4 = Math.Clamp(GetQueryInt(request, "window", 300), 60, 1800);
							int graphPoints = Math.Clamp(GetQueryInt(request, "points", 240), 60, 600);
							ServerHealthSnapshot payload42 = await RunOnMainThreadAsync(() => this.module.ServerHealth?.GetSnapshot(graphPoints, windowSeconds4), "web-profiler-status").ConfigureAwait(continueOnCapturedContext: false);
							object responsePayload2 = ((payload42 == null) ? ((object)new
							{
								available = false
							}) : ((object)payload42));
							await WriteJsonAsync(stream, 200, responsePayload2, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/profiler/start")
						{
							int seconds = Math.Clamp(GetJsonInt(request.Body, "seconds", 10), 5, 30);
							WebActionResult result69 = await RunOnMainThreadAsync(() =>
							{
								ServerHealthService serverHealth = this.module.ServerHealth;
								if (serverHealth == null)
								{
									return new WebActionResult(Success: false, "Server profiler is not available.");
								}
								bool success13 = serverHealth.StartManualProfile(seconds, out string message12);
								return new WebActionResult(success13, message12);
							}, "web-profiler-start").ConfigureAwait(continueOnCapturedContext: false);
							api.Logger.Notification("[WebDashboard] admin {0} requested a {1}s engine profile from {2}. Result={3}.", requestActor, seconds, remote, result69.Success);
							RecordWebAudit("manual-profiler", $"Requested {seconds}s engine profile.", result69.Success ? "Success" : result69.Message, remote.ToString(), requestActor);
							await WriteJsonAsync(stream, result69.Success ? 200 : 409, result69, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else if (request.Method == "POST" && path == "/api/profiler/enabled")
						{
							bool value4 = GetJsonBool(request.Body, "enabled", fallback: false);
							WebActionResult result70 = await RunOnMainThreadAsync(() =>
							{
								this.module.Config.ProfilerEnabled = value4;
								this.module.SaveRuntimeConfig();
								return new WebActionResult(Success: true, value4 ? "Profiler diagnostics enabled." : "Profiler diagnostics disabled.");
							}, "web-profiler-enabled").ConfigureAwait(continueOnCapturedContext: false);
							api.Logger.Notification("[WebDashboard] admin {0} set profiler Enabled={1} from {2}.", requestActor, value4, remote);
							RecordWebAudit("diagnostics-toggle", "Profiler diagnostics -> " + (value4 ? "ON" : "OFF"), "Success", remote.ToString(), requestActor);
							await WriteJsonAsync(stream, 200, result70, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
						else
						{
							await WriteJsonAsync(stream, 404, new
							{
								error = "Not found."
							}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						}
					}
				}
				return;
			}
		}
		await WriteJsonAsync(stream, 429, new
		{
			error = routePolicyError,
			safeDiagnosticsMode = config.SafeDiagnosticsMode
		}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private object BuildNativeTickDebugSnapshot()
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long num2 = 0L;
		long num3 = 0L;
		try
		{
			num2 = api.Server.TotalSentBytes;
			num3 = api.Server.TotalReceivedBytes;
		}
		catch
		{
		}
		lock (tickDebugLock)
		{
			if (tickDebugNetworkSampleUnixMilliseconds > 0 && num > tickDebugNetworkSampleUnixMilliseconds)
			{
				double num4 = (double)(num - tickDebugNetworkSampleUnixMilliseconds) / 1000.0;
				tickDebugNetworkSendBytesPerSecond = (double)Math.Max(0L, num2 - tickDebugNetworkSentBytes) / num4;
				tickDebugNetworkReceiveBytesPerSecond = (double)Math.Max(0L, num3 - tickDebugNetworkReceivedBytes) / num4;
			}
			tickDebugNetworkSampleUnixMilliseconds = num;
			tickDebugNetworkSentBytes = num2;
			tickDebugNetworkReceivedBytes = num3;
			ServerHealthSnapshot serverHealthSnapshot = null;
			try
			{
				serverHealthSnapshot = module.ServerHealth?.GetSnapshot(60, 300);
			}
			catch
			{
			}
			ServerHealthSample serverHealthSample = serverHealthSnapshot?.Current;
			float tickBudgetMs = 0f;
			try
			{
				tickBudgetMs = api.Server.Config.TickTime;
			}
			catch
			{
			}
			ServerProfilerModHotspot serverProfilerModHotspot = serverHealthSnapshot?.ModHotspots.Where((ServerProfilerModHotspot x) => !x.IsCore && !string.IsNullOrWhiteSpace(x.ModId) && x.ModId != "<unattributed>" && x.Confidence != "Low").OrderByDescending((ServerProfilerModHotspot x) => x.TotalMs).FirstOrDefault();
			ServerProfileSlowTick serverProfileSlowTick = serverHealthSnapshot?.ProfileSlowTicks.OrderByDescending((ServerProfileSlowTick x) => x.TickMs).FirstOrDefault();
			return new
			{
				available = true,
				enabled = config.TickDebuggingEnabled,
				safeDiagnosticsMode = config.SafeDiagnosticsMode,
				tickBudgetMs = tickBudgetMs,
				currentMainThreadDelayMs = (serverHealthSample?.MainThreadDelayMs ?? 0.0),
				cpuPercent = (serverHealthSample?.CpuPercent ?? 0.0),
				players = (serverHealthSample?.Players ?? api.World.AllOnlinePlayers.Length),
				loadedEntities = (serverHealthSample?.LoadedEntities ?? 0),
				loadedChunks = (serverHealthSample?.LoadedChunks ?? 0),
				totalSentBytes = num2,
				totalReceivedBytes = num3,
				sendBytesPerSecond = tickDebugNetworkSendBytesPerSecond,
				receiveBytesPerSecond = tickDebugNetworkReceiveBytesPerSecond,
				slowTickCapture = new
				{
					running = nativeSlowTickCaptureRunning,
					incidentCapture = nativeSlowTickIncidentCapture,
					thresholdMs = nativeSlowTickThresholdMs,
					durationSeconds = nativeSlowTickDurationSeconds,
					startedUnixMilliseconds = nativeSlowTickStartedUnixMilliseconds,
					endsUnixMilliseconds = nativeSlowTickEndsUnixMilliseconds,
					remainingSeconds = (nativeSlowTickCaptureRunning ? Math.Max(0L, (nativeSlowTickEndsUnixMilliseconds - num + 999) / 1000) : 0),
					logPath = nativeSlowTickLogPath,
					lastExcerpt = nativeSlowTickLastExcerpt
				},
				lastAction = new
				{
					action = nativeTickDebugLastAction,
					unixMilliseconds = nativeTickDebugLastActionUnixMilliseconds,
					output = nativeTickDebugLastOutput
				},
				lastIncidentSummary = nativeTickDebugLastIncidentSummary,
				modAttribution = new
				{
					available = (serverProfilerModHotspot != null && (serverHealthSnapshot?.LastProfile.CompletedUnixMilliseconds ?? 0) > 0),
					modId = (serverProfilerModHotspot?.ModId ?? ""),
					modName = (serverProfilerModHotspot?.ModName ?? ""),
					confidence = (serverProfilerModHotspot?.Confidence ?? "Low"),
					tickSharePercent = (serverProfilerModHotspot?.PercentOfProfileTickTime ?? 0.0),
					maxSegmentMs = (serverProfilerModHotspot?.MaxSingleMs ?? 0.0),
					slowSegments = (serverProfilerModHotspot?.SlowSegments ?? 0),
					topMarker = (serverProfilerModHotspot?.TopMarker ?? ""),
					profileCompletedUnixMilliseconds = (serverHealthSnapshot?.LastProfile.CompletedUnixMilliseconds ?? 0),
					profileKind = (serverHealthSnapshot?.LastProfile.CaptureKind ?? ""),
					worstProfileTickMs = (serverProfileSlowTick?.TickMs ?? 0.0),
					worstProfileTickTopCauses = (serverProfileSlowTick?.TopCauseSummary ?? ""),
					note = ((serverProfilerModHotspot == null) ? "No non-core mod has strong attribution in the latest engine profile." : "Profiler attribution identifies where measured tick time was spent; it is not automatic proof of a mod defect.")
				},
				profiler = new
				{
					enabled = (serverHealthSnapshot?.Enabled ?? false),
					running = (serverHealthSnapshot?.ProfileRunning ?? false),
					remainingSeconds = (serverHealthSnapshot?.ProfileRemainingSeconds ?? 0),
					lastProfile = serverHealthSnapshot?.LastProfile,
					diagnosticSummary = serverHealthSnapshot?.DiagnosticSummary
				}
			};
		}
	}

	private WebActionResult StartNativeSlowTickCapture(int thresholdMs, int durationSeconds, bool incidentCapture)
	{
		lock (tickDebugLock)
		{
			if (nativeSlowTickCaptureRunning)
			{
				return new WebActionResult(Success: false, "A native slow-tick capture is already running.");
			}
		}
		string message = "";
		if (incidentCapture)
		{
			ServerHealthService serverHealth = module.ServerHealth;
			if (serverHealth == null)
			{
				return new WebActionResult(Success: false, "Server profiler is not available for incident capture.");
			}
			if (!serverHealth.StartManualProfile(durationSeconds, out message))
			{
				return new WebActionResult(Success: false, message);
			}
		}
		string path = ResolveNativeTickDebugLogPath();
		long num = SafeFileLength(path);
		long num2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		int generation;
		lock (tickDebugLock)
		{
			generation = ++nativeSlowTickCaptureGeneration;
			nativeSlowTickCaptureRunning = true;
			nativeSlowTickIncidentCapture = incidentCapture;
			nativeSlowTickThresholdMs = thresholdMs;
			nativeSlowTickDurationSeconds = durationSeconds;
			nativeSlowTickStartedUnixMilliseconds = num2;
			nativeSlowTickEndsUnixMilliseconds = num2 + (long)durationSeconds * 1000L;
			nativeSlowTickLogPath = path;
			nativeSlowTickLogOffset = num;
			nativeTickDebugLastAction = (incidentCapture ? "incident-capture" : "slow-tick-capture");
			nativeTickDebugLastActionUnixMilliseconds = num2;
			nativeTickDebugLastOutput = $"Starting native slow-tick capture at {thresholdMs}ms for {durationSeconds}s." + (incidentCapture ? (" Engine profiler: " + message) : "");
		}
		FrameProfilerUtil frameProfiler = api.World.FrameProfiler;
		bool flag = frameProfiler.Enabled || frameProfiler.PrintSlowTicks;
		lock (tickDebugLock)
		{
			nativeSlowTickWasEnabled = frameProfiler.PrintSlowTicks;
			nativeSlowTickPreviousThreshold = frameProfiler.PrintSlowTicksThreshold;
		}
		frameProfiler.PrintSlowTicks = true;
		frameProfiler.PrintSlowTicksThreshold = thresholdMs;
		if (!flag)
		{
			frameProfiler.Begin("Newfies Suite native slow-tick capture transition");
		}
		api.Event.RegisterCallback((float _) =>
		{
			FinishNativeSlowTickCapture(generation);
		}, durationSeconds * 1000);
		string message2 = (incidentCapture ? $"Incident capture started for {durationSeconds}s: native slow ticks ≥{thresholdMs}ms plus engine profile." : $"Native slow-tick capture started for {durationSeconds}s at ≥{thresholdMs}ms.");
		return new WebActionResult(Success: true, message2);
	}

	private WebActionResult StopNativeSlowTickCaptureNow()
	{
		int generation;
		lock (tickDebugLock)
		{
			if (!nativeSlowTickCaptureRunning)
			{
				return new WebActionResult(Success: false, "No native slow-tick capture is running.");
			}
			generation = nativeSlowTickCaptureGeneration;
		}
		FinishNativeSlowTickCapture(generation);
		return new WebActionResult(Success: true, "Native slow-tick capture stopped.");
	}

	private void FinishNativeSlowTickCapture(int generation)
	{
		string path;
		long offset;
		bool flag;
		lock (tickDebugLock)
		{
			if (!nativeSlowTickCaptureRunning || generation != nativeSlowTickCaptureGeneration)
			{
				return;
			}
			path = nativeSlowTickLogPath;
			offset = nativeSlowTickLogOffset;
			flag = nativeSlowTickIncidentCapture;
			nativeSlowTickCaptureRunning = false;
			nativeSlowTickEndsUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
		try
		{
			FrameProfilerUtil frameProfiler = api.World.FrameProfiler;
			bool printSlowTicks;
			int printSlowTicksThreshold;
			lock (tickDebugLock)
			{
				printSlowTicks = nativeSlowTickWasEnabled;
				printSlowTicksThreshold = nativeSlowTickPreviousThreshold;
			}
			frameProfiler.PrintSlowTicksThreshold = printSlowTicksThreshold;
			frameProfiler.PrintSlowTicks = printSlowTicks;
		}
		catch
		{
		}
		string text = ReadNativeTickDebugLogDelta(path, offset, 131072);
		string text2 = "";
		if (flag)
		{
			try
			{
				ServerHealthSnapshot serverHealthSnapshot = module.ServerHealth?.GetSnapshot(120, 300);
				ServerDiagnosticSummary serverDiagnosticSummary = serverHealthSnapshot?.DiagnosticSummary;
				ServerProfileSummary serverProfileSummary = serverHealthSnapshot?.LastProfile;
				ServerProfilerModHotspot serverProfilerModHotspot = serverHealthSnapshot?.ModHotspots.Where((ServerProfilerModHotspot x) => !x.IsCore && !string.IsNullOrWhiteSpace(x.ModId) && x.ModId != "<unattributed>" && x.Confidence != "Low").OrderByDescending((ServerProfilerModHotspot x) => x.TotalMs).FirstOrDefault();
				string text3 = ((serverProfilerModHotspot == null) ? " no strong non-core mod attribution." : $" likely mod: {serverProfilerModHotspot.ModId} ({serverProfilerModHotspot.Confidence}, {serverProfilerModHotspot.PercentOfProfileTickTime:0.0}% tick share, max {serverProfilerModHotspot.MaxSingleMs:0.0}ms).");
				text2 = $"Native incident capture completed. Worst rolling main-thread delay: {serverDiagnosticSummary?.WorstDelayMs ?? 0.0:0.0}ms; profile ticks: {serverProfileSummary?.TickCount ?? 0}; profiled tick time: {(serverProfileSummary?.AttributedMs ?? 0.0) + (serverProfileSummary?.UnattributedMs ?? 0.0):0.0}ms;" + text3;
			}
			catch (Exception ex)
			{
				text2 = "Native incident capture completed; profile summary unavailable: " + ex.Message;
			}
		}
		lock (tickDebugLock)
		{
			nativeSlowTickLastExcerpt = (string.IsNullOrWhiteSpace(text) ? "Capture completed. No new slow-tick breakdown lines were found in the current server log during this window." : text);
			nativeTickDebugLastOutput = (flag ? text2 : "Native slow-tick capture completed.");
			if (flag)
			{
				nativeTickDebugLastIncidentSummary = text2;
			}
			nativeSlowTickIncidentCapture = false;
		}
	}

	private string ResolveNativeTickDebugLogPath(bool debugLog = false)
	{
		if (!debugLog)
		{
			try
			{
				string text = logIntelligence?.GetSnapshot().CurrentLogPath ?? "";
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text;
				}
			}
			catch
			{
			}
		}
		try
		{
			string orCreateDataPath = api.GetOrCreateDataPath("Logs");
			string text2 = (debugLog ? "server-debug" : "server-main");
			string[] array = new string[2]
			{
				text2 + ".txt",
				text2 + ".log"
			};
			string[] array2 = array;
			foreach (string path in array2)
			{
				string text3 = Path.Combine(orCreateDataPath, path);
				if (File.Exists(text3))
				{
					return text3;
				}
			}
			return Directory.Exists(orCreateDataPath) ? (Directory.GetFiles(orCreateDataPath, text2 + "*", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "") : "";
		}
		catch
		{
			return "";
		}
	}

	private static string ReadNativeTickDebugLogDelta(string path, long offset, int maxBytes)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return "";
		}
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (offset < 0 || offset > fileStream.Length)
			{
				offset = Math.Max(0L, fileStream.Length - maxBytes);
			}
			long num = fileStream.Length - offset;
			if (num <= 0)
			{
				return "";
			}
			if (num > maxBytes)
			{
				offset = fileStream.Length - maxBytes;
			}
			fileStream.Seek(offset, SeekOrigin.Begin);
			using StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, true, 4096, false);
			string text = streamReader.ReadToEnd();
			if (text.Length > 24000)
			{
				string text2 = text;
				text = text2.Substring(text2.Length - 24000);
			}
			return text.Trim();
		}
		catch
		{
			return "";
		}
	}

	private static string CombineNativeDebugOutput(string commandOutput, string logDelta)
	{
		commandOutput = commandOutput?.Trim() ?? "";
		logDelta = logDelta?.Trim() ?? "";
		if (commandOutput.Length == 0)
		{
			return logDelta;
		}
		if (logDelta.Length == 0)
		{
			return commandOutput;
		}
		return commandOutput + "\n\n--- Native log output ---\n" + logDelta;
	}

	private ServerUtilitiesSelfTest BuildServerUtilitiesHealth()
	{
		return MeasureFeature("self-test", "Server Essentials Suite Self-Test", () =>
		{
			List<SelfTestCheck> checks = new List<SelfTestCheck>();
			SuiteFeatureConfig features = module.Features;
			LogStorageService storage = module.Storage;
			if (storage == null)
			{
				Check("database", "BlockLogger SQLite", features.BlockLogger ? "Failed" : "OK", features.BlockLogger ? "Storage service is not available." : "Feature master-disabled; no BlockLogger storage worker is expected.");
			}
			else
			{
				DatabaseHealthSnapshot databaseHealth = storage.GetDatabaseHealth(module.Config.RetentionDays);
				string status = ((!databaseHealth.Ready) ? "Warning" : ((!string.IsNullOrWhiteSpace(databaseHealth.LastError) && databaseHealth.LastErrorUnixMilliseconds > DateTimeOffset.UtcNow.AddMinutes(-10.0).ToUnixTimeMilliseconds()) ? "Warning" : "OK"));
				Check("database", "BlockLogger SQLite", status, $"Ready={databaseHealth.Ready}; DB={FormatBytes(databaseHealth.DatabaseBytes)}; WAL={FormatBytes(databaseHealth.WalBytes)}; queue={databaseHealth.PendingEvents:N0}/{databaseHealth.QueueCapacity:N0}; last error={(string.IsNullOrWhiteSpace(databaseHealth.LastError) ? "none" : databaseHealth.LastError)}");
			}
			AdminStorageHealth adminStorageHealth = module.Admin?.GetStorageHealth();
			if (adminStorageHealth == null || !adminStorageHealth.Available)
			{
				Check("admin-database", "Essentials Admin SQLite", features.Administration ? "Warning" : "OK", features.Administration ? "Admin storage service is unavailable." : "Administration master-disabled; no admin database worker is expected.");
			}
			else
			{
				string status2 = ((!string.IsNullOrWhiteSpace(adminStorageHealth.LastError) && adminStorageHealth.LastErrorUnixMilliseconds > DateTimeOffset.UtcNow.AddMinutes(-10.0).ToUnixTimeMilliseconds()) ? "Warning" : "OK");
				Check("admin-database", "Essentials Admin SQLite", status2, $"DB={FormatBytes(adminStorageHealth.DatabaseBytes)}; WAL={FormatBytes(adminStorageHealth.WalBytes)}; players={adminStorageHealth.Players:N0}; snapshots={adminStorageHealth.InventorySnapshots:N0}; last save={adminStorageHealth.LastSaveMilliseconds:N0}ms; integrity={adminStorageHealth.Integrity}");
			}
			int num;
			lock (stateLock)
			{
				num = webWorkerTasks.Count((Task x) => !x.IsCompleted);
			}
			bool flag = !config.Enabled || !IsRunning || num == config.WebWorkerCount;
			string status3 = (((config.Enabled == IsRunning) & flag) ? "OK" : (config.Enabled ? "Failed" : "OK"));
			Check("web", "Web Server", status3, (!config.Enabled) ? "Disabled by configuration." : (IsRunning ? $"Listening at {ListenAddress}; workers={num}/{config.WebWorkerCount}; queue={Math.Max(0, Volatile.Read(in webQueuedRequests))}/{config.WebRequestQueueCapacity}; max wait={config.WebQueueMaxWaitMs}ms." : ("Enabled but not listening. Last error: " + LastError)));
			Check("diagnostics", "Profiler / Diagnostics", (module.ServerHealth != null || !features.PerformanceDiagnostics) ? "OK" : "Failed", (!features.PerformanceDiagnostics) ? "Feature master-disabled; no diagnostics sampler is expected." : ((module.ServerHealth == null) ? "ServerHealthService is unavailable." : (module.ServerHealth.Enabled ? "Lightweight diagnostics enabled." : "Available; lightweight diagnostics currently OFF.")));
			PlayerCountAnalyticsSnapshot playerCountAnalyticsSnapshot = module.PlayerAnalytics?.GetSnapshot(7);
			Check("player-analytics", "Player Analytics", (playerCountAnalyticsSnapshot != null || !features.PlayerAnalytics) ? "OK" : "Failed", (!features.PlayerAnalytics) ? "Feature master-disabled; no analytics reconciliation/save work is expected." : ((playerCountAnalyticsSnapshot == null) ? "PlayerCountAnalyticsService is unavailable." : $"Tracking {playerCountAnalyticsSnapshot.CurrentPlayers} online player(s); {playerCountAnalyticsSnapshot.Daily.Count} daily bucket(s); server time zone={playerCountAnalyticsSnapshot.ServerTimeZone}."));
			PlayerReportSnapshot playerReportSnapshot = module.Reports?.GetSnapshot("all", 1);
			Check("reports", "Reports & Tickets", (playerReportSnapshot != null || !features.Reports) ? "OK" : "Failed", (!features.Reports) ? "Feature master-disabled; no reports timer/state service is expected." : ((playerReportSnapshot == null) ? "ReportsService is unavailable." : $"Reports={playerReportSnapshot.TotalCount}; open={playerReportSnapshot.OpenCount}; investigating={playerReportSnapshot.InvestigatingCount}; root /report owner={playerReportSnapshot.RootCommandOwner}."));
			AntiCheatWebSnapshot antiCheatWebSnapshot = module.AntiCheat?.GetSnapshot();
			Check("anti-cheat", "Integrated Anti-Cheat Server", (antiCheatWebSnapshot == null) ? (features.AntiCheat ? "Failed" : "OK") : ((antiCheatWebSnapshot.Enabled && antiCheatWebSnapshot.Available) ? "OK" : "Warning"), (!features.AntiCheat) ? "Feature master-disabled; no anti-cheat network channel/watchdog is expected." : ((antiCheatWebSnapshot == null) ? "AntiCheatService is unavailable." : ((antiCheatWebSnapshot.Enabled && antiCheatWebSnapshot.Available) ? $"Protocol={antiCheatWebSnapshot.ProtocolVersion}; clients={antiCheatWebSnapshot.ClientsConnected}/{antiCheatWebSnapshot.OnlinePlayers}; suspicious={antiCheatWebSnapshot.Suspicious}; missing/timed out={antiCheatWebSnapshot.MissingOrTimedOut}." : ((!antiCheatWebSnapshot.Enabled) ? "Integrated anti-cheat server is disabled in NewfiesAntiCheat.json." : (string.IsNullOrWhiteSpace(antiCheatWebSnapshot.AvailabilityReason) ? "Configured ON but anti-cheat runtime is unavailable." : antiCheatWebSnapshot.AvailabilityReason)))));
			Check("blocklogger-config", "BlockLogger Config", (module.Config.SchemaVersion >= 3) ? "OK" : "Warning", $"Schema {module.Config.SchemaVersion}; expected {3}.");
			Check("web-config", "Web Dashboard Config", (config.SchemaVersion >= 2) ? "OK" : "Warning", $"Schema {config.SchemaVersion}; expected {2}.");
			if (AnimalLimiterSystem.Current?.Config != null)
			{
				Check("animal-config", "Animal Limiter Config", (AnimalLimiterSystem.Current.Config.SchemaVersion >= 2) ? "OK" : "Warning", $"Schema {AnimalLimiterSystem.Current.Config.SchemaVersion}; expected {2}.");
			}
			if (LoginProtectionSystem.Current?.Config != null)
			{
				Check("login-config", "Login Protection Config", (LoginProtectionSystem.Current.Config.SchemaVersion >= 2) ? "OK" : "Warning", $"Schema {LoginProtectionSystem.Current.Config.SchemaVersion}; expected {2}.");
			}
			Check("performance-store", "Performance Store", (!features.PerformanceCenter || performanceCenterStore.SchemaVersion == 2) ? "OK" : "Warning", (!features.PerformanceCenter) ? "Feature master-disabled; store maintenance is not running." : $"Schema {performanceCenterStore.SchemaVersion}; expected {2}.");
			Check("operations-store", "Operations Store", (!features.OperationsCenter || operationsCenterStore.SchemaVersion == 2) ? "OK" : "Warning", (!features.OperationsCenter) ? "Feature master-disabled; store maintenance is not running." : $"Schema {operationsCenterStore.SchemaVersion}; expected {2}.");
			Check("optimization-store", "Optimization Experiment Store", (optimizationExperiments.SchemaVersion == 2) ? "OK" : "Warning", $"Schema {optimizationExperiments.SchemaVersion}; expected {2}.");
			try
			{
				string orCreateDataPath = api.GetOrCreateDataPath("NewfiesServerUtilities");
				string path = Path.Combine(orCreateDataPath, ".write-test-" + Guid.NewGuid().ToString("N"));
				File.WriteAllText(path, "ok");
				File.Delete(path);
				Check("data-path", "Writable Data Directory", "OK", orCreateDataPath);
			}
			catch (Exception ex)
			{
				Check("data-path", "Writable Data Directory", "Failed", ex.GetType().Name + ": " + ex.Message);
			}
			Check("animal-limiter", "Animal Limiter", (AnimalLimiterSystem.Current != null || !features.AnimalLimiter) ? "OK" : "Warning", (!features.AnimalLimiter) ? "Feature master-disabled; no limiter hooks/timer are expected." : ((AnimalLimiterSystem.Current != null) ? "Subsystem available." : "Subsystem not available."));
			Check("login-protection", "Login Protection", (LoginProtectionSystem.Current != null || !features.LoginProtection) ? "OK" : "Warning", (!features.LoginProtection) ? "Feature master-disabled; no login/respawn hooks are expected." : ((LoginProtectionSystem.Current != null) ? "Subsystem available." : "Subsystem not available."));
			bool flag2 = api.ModLoader.Mods.Any((Mod x) => x?.Info != null && string.Equals(x.Info.ModID, "tungsten", StringComparison.OrdinalIgnoreCase));
			Check("tungsten", "Tungsten Provider", "OK", flag2 ? "Optional Tungsten provider detected." : "Not installed; optimization controls remain unavailable.", "External");
			Check("security", "Web Security", (!IsLoopbackBind(config.BindAddress) && !PasswordConfigured) ? "Failed" : "OK", IsLoopbackBind(config.BindAddress) ? "Loopback-only binding." : (PasswordConfigured ? "Remote-capable binding with password authentication configured." : "Unsafe remote binding without password."));
			Check("safe-mode", "Safe Diagnostics Mode", "OK", config.SafeDiagnosticsMode ? "ON: lightweight diagnostics forced on; manual engine profiler and external optimizer changes locked." : "OFF.");
			List<FeatureCircuitBreakerStatus> list = SnapshotCircuitBreakers();
			foreach (FeatureCircuitBreakerStatus item in list.Where((FeatureCircuitBreakerStatus x) => x.Open))
			{
				Check("breaker:" + item.Key, item.Name + " Circuit Breaker", "Warning", item.LastError, item.Safety);
			}
			string overall = (checks.Any((SelfTestCheck x) => x.Status == "Failed") ? "Failed" : (checks.Any((SelfTestCheck x) => x.Status == "Warning") ? "Warning" : "OK"));
			string modVersion = api.ModLoader.Mods.FirstOrDefault((Mod x) => x?.Info != null && string.Equals(x.Info.ModID, "newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase))?.Info?.Version ?? "1.2.44";
			return lastSelfTest = new ServerUtilitiesSelfTest
			{
				BuildId = "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1",
				ModVersion = modVersion,
				TargetGameVersion = "1.22.7",
				UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Overall = overall,
				Checks = checks,
				CircuitBreakers = list,
				Overhead = SnapshotOverhead(),
				WebPolicies = WebRoutePolicies.ToList()
			};
			void Check(string key, string name, string status4, string detail, string safety = "Safe")
			{
				checks.Add(new SelfTestCheck
				{
					Key = key,
					Name = name,
					Status = status4,
					Detail = detail,
					Safety = safety
				});
			}
		});
	}

	private object BuildRetentionAndPrivacyStatus()
	{
		return new
		{
			retention = new
			{
				blockLoggerDays = module.Config.RetentionDays,
				incidents = config.MaxArchivedIncidents,
				entityScans = config.MaxEntityScanHistory,
				environmentChanges = config.MaxEnvironmentChanges,
				alerts = config.MaxAlerts,
				actions = config.MaxActions,
				webAudit = config.MaxWebAuditEntries,
				configSnapshots = config.MaxConfigSnapshots,
				performanceSnapshots = config.MaxPerformanceSnapshots,
				worldSaveObservations = config.MaxWorldSaveObservations,
				networkSamples = config.MaxNetworkSamples,
				optimizationExperiments = config.MaxOptimizationExperiments
			},
			exportPrivacy = new
			{
				sessionTokensExported = false,
				csrfTokensExported = false,
				passwordHashesExported = false,
				passwordSaltsExported = false,
				oneTimePasswordsExported = false,
				remoteAuditAddressesExported = false,
				configSnapshotContentsExported = false,
				note = "Diagnostic bundles include configuration snapshot metadata only. Authentication secrets and raw snapshot JSON are excluded; audit remote addresses are redacted."
			}
		};
	}

	private static string FormatBytes(long value)
	{
		if (value < 1073741824)
		{
			if (value < 1048576)
			{
				if (value < 1024)
				{
					return $"{value} B";
				}
				return $"{(double)value / 1024.0:0.0} KB";
			}
			return $"{(double)value / 1048576.0:0.0} MB";
		}
		return $"{(double)value / 1073741824.0:0.00} GB";
	}

	private object BuildTerrainExportDashboardFallback()
	{
		return new
		{
			utc = DateTime.UtcNow,
			exportBusy = true,
			playersOnline = 0,
			blockLogger = (object)null,
			profiler = (object)null,
			animalLimiter = new
			{
				available = false,
				enabled = false,
				trackedEntities = 0,
				trackedRegions = 0
			},
			loginProtection = new
			{
				available = false,
				enabled = false,
				activeProtected = 0
			},
			operations = new
			{
				activeAlerts = 0,
				criticalAlerts = 0,
				openActions = 0,
				configSnapshots = 0
			},
			web = new
			{
				running = IsRunning,
				bindAddress = config.BindAddress,
				port = config.Port,
				sessions = ActiveSessionCount
			},
			note = "Terrain export is running. Main-thread dashboard metrics are temporarily paused and the web panel is serving cached status."
		};
	}

	private Task<object> GetLiveSnapshotAsync(bool terrainExportBusy)
	{
		if (terrainExportBusy)
		{
			if (snapshotCache.TryGet("live", out object payload))
			{
				Interlocked.Increment(ref webSnapshotCacheHits);
				return Task.FromResult(payload);
			}
			return Task.FromResult((object)new
			{
				utc = DateTime.UtcNow,
				exportBusy = true,
				note = "Terrain export is running; a lightweight cached dashboard snapshot will resume when the game thread is free."
			});
		}
		return GetCachedMainThreadReadAsync("live", config.LiveSnapshotMaxAgeMs, BuildLiveSnapshot, "web-live-snapshot");
	}

	private Task<object> GetDashboardSnapshotAsync(bool terrainExportBusy)
	{
		if (terrainExportBusy)
		{
			if (snapshotCache.TryGet("dashboard", out object payload))
			{
				Interlocked.Increment(ref webSnapshotCacheHits);
				return Task.FromResult(payload);
			}
			return Task.FromResult(BuildTerrainExportDashboardFallback());
		}
		return GetCachedMainThreadReadAsync("dashboard", config.DashboardSnapshotMaxAgeMs, BuildDashboardSnapshot, "web-dashboard-status");
	}

	private Task<object> GetCachedMainThreadReadAsync(string key, int maxAgeMs, Func<object> builder, string taskCode)
	{
		Task<object> async = snapshotCache.GetAsync(key, maxAgeMs, async () =>
		{
			object payload = await RunOnMainThreadAsync(() =>
			{
				if (disposed)
				{
					throw new ObjectDisposedException("WebDashboardService");
				}
				if (!config.Enabled)
				{
					throw new InvalidOperationException("Web panel snapshot refresh is paused.");
				}
				return builder();
			}, taskCode, skipIfTimedOut: true).ConfigureAwait(continueOnCapturedContext: false);
			Interlocked.Increment(ref webSnapshotRefreshes);
			return payload;
		}, out var servedCached, (Exception ex) =>
		{
			api.Logger.Debug("[WebDashboard] cached read refresh {0} skipped: {1}: {2}", key, ex.GetType().Name, ex.Message);
		}, !disposed && config.Enabled);
		if (servedCached)
		{
			Interlocked.Increment(ref webSnapshotCacheHits);
		}
		else
		{
			Interlocked.Increment(ref webSnapshotCacheMisses);
		}
		return async;
	}

	private void InvalidateReadOnlySnapshotCaches()
	{
		snapshotCache.Invalidate();
	}

	private object BuildDashboardSnapshot()
	{
		LogStorageService storage = module.Storage;
		ServerHealthSnapshot serverHealthSnapshot = module.ServerHealth?.GetSnapshot(120);
		AnimalLimiterSystem current = AnimalLimiterSystem.Current;
		LoginProtectionSystem current2 = LoginProtectionSystem.Current;
		return new
		{
			utc = DateTime.UtcNow,
			playersOnline = (api.World.AllOnlinePlayers?.Length ?? 0),
			blockLogger = ((storage == null) ? null : new
			{
				ready = storage.IsReady,
				fullFidelity = (module.Config.LogEntitySpawn && module.Config.LogEntityDeath && module.Config.LogEntityDespawn && !module.Config.AggregateEntitySpawnDespawn && module.Config.BulkEntityPositionSampleIntervalMs == 0),
				pending = storage.PendingEvents,
				peakPending = storage.PeakPendingEvents,
				overflowPending = storage.PendingOverflowEvents,
				overflowCapacity = storage.EmergencyOverflowCapacity,
				overflowAccepted = storage.OverflowAcceptedEvents,
				overflowRejected = storage.OverflowRejectedEvents,
				dropped = storage.DroppedEvents,
				written = storage.WrittenEvents,
				writerMs = storage.LastBatchElapsedMs,
				activeContainerSessions = module.ActiveContainerSessions,
				containerSessionLimit = module.Config.MaxTrackedContainerSessions,
				containerSessionsEvicted = module.ContainerSessionsEvicted,
				containerSessionsWithExpiredInventory = module.ContainerSessionsWithExpiredInventory
			}),
			profiler = ((serverHealthSnapshot == null) ? null : new
			{
				enabled = serverHealthSnapshot.Enabled,
				running = serverHealthSnapshot.ProfileRunning,
				current = serverHealthSnapshot.Current,
				diagnostic = serverHealthSnapshot.DiagnosticSummary,
				smart = serverHealthSnapshot.SmartAnalysis,
				recentLagEvents = serverHealthSnapshot.DiagnosticIncidents.Take(5).ToList(),
				recentIncidentGroups = serverHealthSnapshot.SmartAnalysis.IncidentGroups.Take(5).ToList(),
				lastProfile = serverHealthSnapshot.LastProfile
			}),
			animalLimiter = new
			{
				available = (current != null),
				enabled = (current?.Config.Enabled == true),
				trackedEntities = (current?.HealthTrackedEntityCount ?? 0),
				trackedRegions = (current?.HealthTrackedRegionCount ?? 0)
			},
			loginProtection = new
			{
				available = (current2 != null),
				enabled = (current2?.Config.Enabled ?? false),
				activeProtected = (current2?.ActiveProtectedCount ?? 0)
			},
			operations = new
			{
				activeAlerts = operationsCenterStore.Alerts.Count((ServerAlertRecord x) =>
				{
					string status = x.Status;
					return (status == "Active" || status == "Investigating") ? true : false;
				}),
				criticalAlerts = operationsCenterStore.Alerts.Count((ServerAlertRecord x) =>
				{
					string status = x.Status;
					bool flag = ((status == "Active" || status == "Investigating") ? true : false);
					return flag && x.Severity == "Critical";
				}),
				openActions = operationsCenterStore.Actions.Count((ServerActionRecord x) =>
				{
					string status = x.Status;
					return (status == "Open" || status == "Investigating") ? true : false;
				}),
				configSnapshots = operationsCenterStore.ConfigSnapshots.Count
			},
			web = new
			{
				running = IsRunning,
				bindAddress = config.BindAddress,
				port = config.Port,
				sessions = ActiveSessionCount
			}
		};
	}

	private async Task HandlePlayerQueryAsync(NetworkStream stream, WebRequest request, CancellationToken cancellationToken)
	{
		LogQueryService queries = module.Queries;
		if (queries == null)
		{
			await WriteJsonAsync(stream, 503, new
			{
				error = "BlockLogger query service is unavailable."
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string player = GetQuery(request, "player", "").Trim();
		if (player.Length == 0)
		{
			await WriteJsonAsync(stream, 400, new
			{
				error = "Enter a player name or UID."
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		if (player.Length > 80)
		{
			player = player.Substring(0, 80);
		}
		int limit = Math.Clamp(GetQueryInt(request, "limit", module.Config.DefaultDisplayCount), 1, module.Config.MaxDisplayCount);
		int offset = Math.Clamp(GetQueryInt(request, "offset", 0), 0, 2000);
		string category = NormalizePlayerCategory(GetQuery(request, "category", "any"));
		List<LogEventRecord> rows = await AwaitWithTimeout(queries.QueryPlayerAsync(player, limit, category, offset), TimeSpan.FromSeconds(8L), "BlockLogger player query").ConfigureAwait(continueOnCapturedContext: false);
		await WriteJsonAsync(stream, 200, new
		{
			count = rows.Count,
			offset = offset,
			limit = limit,
			hasPrevious = (offset > 0),
			hasNext = (rows.Count >= limit && offset + limit < 2000),
			rows = rows.Select((LogEventRecord row) => FormatWebRecord(row, null)).ToList()
		}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task HandleNearbyQueryAsync(NetworkStream stream, WebRequest request, CancellationToken cancellationToken)
	{
		LogQueryService queries = module.Queries;
		if (queries == null)
		{
			await WriteJsonAsync(stream, 503, new
			{
				error = "BlockLogger query service is unavailable."
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string view = GetQuery(request, "view", "blocks").Trim().ToLowerInvariant();
		bool flag;
		switch (view)
		{
		case "blocks":
		case "containers":
		case "entities":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			view = "blocks";
		}
		int x = GetQueryInt(request, "x", 0);
		int y = GetQueryInt(request, "y", 0);
		int z = GetQueryInt(request, "z", 0);
		int dimension = GetQueryInt(request, "dimension", 0);
		int radius = Math.Clamp(GetQueryInt(request, "radius", module.Config.DefaultRadius), 0, module.Config.MaxRadius);
		int limit = Math.Clamp(GetQueryInt(request, "limit", module.Config.DefaultDisplayCount), 1, module.Config.MaxDisplayCount);
		int offset = Math.Clamp(GetQueryInt(request, "offset", 0), 0, 2000);
		string search = GetQuery(request, "search", "").Trim();
		if (search.Length > 80)
		{
			search = search.Substring(0, 80);
		}
		string filter = (string.IsNullOrWhiteSpace(search) ? null : search);
		string tag = NormalizeWebAction(view, GetQuery(request, "action", "any"));
		Task<List<LogEventRecord>> queryTask = ((view == "entities") ? queries.QueryEntitiesAsync(x, y, z, dimension, radius, limit, filter, tag, offset) : queries.QueryBlocksAsync(x, y, z, dimension, radius, limit, filter, tag, offset));
		List<LogEventRecord> rows = await AwaitWithTimeout(queryTask, TimeSpan.FromSeconds(8L), "BlockLogger nearby query").ConfigureAwait(continueOnCapturedContext: false);
		WebTarget target = new WebTarget(x, y, z);
		await WriteJsonAsync(stream, 200, new
		{
			view = view,
			count = rows.Count,
			offset = offset,
			limit = limit,
			hasPrevious = (offset > 0),
			hasNext = (rows.Count >= limit && offset + limit < 2000),
			radius = radius,
			target = target,
			rows = rows.Select((LogEventRecord row) => FormatWebRecord(row, target)).ToList()
		}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static object FormatWebRecord(LogEventRecord record, WebTarget? target)
	{
		string actor = (string.IsNullOrWhiteSpace(record.PlayerName) ? "Unknown" : record.PlayerName);
		string text = Meta(record, "entityCode") ?? "";
		string text2;
		if (string.IsNullOrWhiteSpace(record.BlockCode))
		{
			text2 = ((text.Length > 0) ? text : "unknown");
		}
		else
		{
			text2 = record.BlockCode;
		}
		LogActionType action = record.Action;
		if (1 == 0)
		{
		}
		string text3 = action switch
		{
			LogActionType.ContainerTake => $"took {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} from {text2}", 
			LogActionType.ContainerPut => $"put {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} into {text2}", 
			LogActionType.ContainerOpen => "opened " + text2, 
			LogActionType.BlockBreak => "broke " + text2, 
			LogActionType.BlockPlace => "placed " + text2, 
			LogActionType.BlockUse => "used " + text2, 
			LogActionType.EntityInteract => "interacted with " + ((text.Length > 0) ? text : "entity"), 
			LogActionType.EntitySpawn => "spawned " + ((text.Length > 0) ? text : "entity") + CountSuffix(record), 
			LogActionType.EntityDeath => ((text.Length > 0) ? text : "entity") + " died", 
			LogActionType.EntityDespawn => "despawned " + ((text.Length > 0) ? text : "entity") + CountSuffix(record), 
			_ => record.Action.ToString(), 
		};
		if (1 == 0)
		{
		}
		string detail = text3;
		return new
		{
			timestamp = IngameTimeFormatter.FormatNewfoundlandTimestamp(record.TimestampUtc),
			timestampUtc = record.TimestampUtc,
			actor = actor,
			actorUid = record.PlayerUid,
			action = record.Action.ToString(),
			detail = detail,
			x = record.X,
			y = record.Y,
			z = record.Z,
			deltaX = ((!(target == null)) ? (record.X - target.X) : 0),
			deltaZ = ((!(target == null)) ? (record.Z - target.Z) : 0),
			ingameDate = record.IngameDate,
			blockCode = record.BlockCode,
			entityCode = text,
			meta = record.Meta
		};
	}

	private static string? Meta(LogEventRecord record, string key)
	{
		string value;
		return (record.Meta != null && record.Meta.TryGetValue(key, out value)) ? value : null;
	}

	private static int Quantity(LogEventRecord record)
	{
		int result;
		return int.TryParse(Meta(record, "quantity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
	}

	private static string CountSuffix(LogEventRecord record)
	{
		long result;
		return (long.TryParse(Meta(record, "eventCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 1) ? $" x{result:N0}" : "";
	}

	private bool TryAuthenticateWebUser(string username, string password, string remoteKey, out WebIdentity identity, out bool accountLocked)
	{
		identity = new WebIdentity("", "", IsOwner: false);
		accountLocked = false;
		if (username == null || username.Length > 256 || password == null || password.Length > 256)
		{
			return false;
		}
		username = username.Trim();
		if (username.Length == 0 || username.Length > 64)
		{
			return false;
		}
		if (PasswordConfigured && ConstantTimeEquals(username, config.AdminUsername) && VerifyPassword(password))
		{
			identity = new WebIdentity(config.AdminUsername, "Owner", IsOwner: true);
			return true;
		}
		lock (webAccountLock)
		{
			WebAdminAccountRecord webAdminAccountRecord = FindPanelAccount(username);
			if (webAdminAccountRecord == null || !webAdminAccountRecord.Enabled)
			{
				return false;
			}
			long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			if (webAdminAccountRecord.LockedUntilUnixMilliseconds > num)
			{
				accountLocked = true;
				return false;
			}
			if (!VerifyAccountPassword(webAdminAccountRecord, password))
			{
				webAdminAccountRecord.FailedLoginCount++;
				if (webAdminAccountRecord.FailedLoginCount >= config.MaxFailedLogins)
				{
					webAdminAccountRecord.FailedLoginCount = 0;
					webAdminAccountRecord.LockedUntilUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(config.LockoutMinutes).ToUnixTimeMilliseconds();
					accountLocked = true;
				}
				SaveWebAdminAccounts();
				return false;
			}
			webAdminAccountRecord.FailedLoginCount = 0;
			webAdminAccountRecord.LockedUntilUnixMilliseconds = 0L;
			webAdminAccountRecord.LastLoginUnixMilliseconds = num;
			webAdminAccountRecord.LastLoginRemote = remoteKey;
			SaveWebAdminAccounts();
			identity = new WebIdentity(webAdminAccountRecord.Username, webAdminAccountRecord.Role, IsOwner: false);
			return true;
		}
	}

	private async Task HandleLoginAsync(NetworkStream stream, WebRequest request, IPAddress remote, CancellationToken cancellationToken)
	{
		string remoteKey = remote.ToString();
		bool currentlyLocked;
		lock (authLock)
		{
			PruneFailedLoginsUnsafe();
			currentlyLocked = failedLogins.TryGetValue(remoteKey, out FailedLoginState state) && state.LockedUntilUtc > DateTime.UtcNow;
		}
		if (currentlyLocked)
		{
			RecordWebAudit("login", "Login attempted while source was temporarily locked.", "Locked", remoteKey, "unknown");
			await WriteJsonAsync(stream, 429, new
			{
				error = "Too many failed logins. Try again later."
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string username = GetJsonString(request.Body, "username", "");
		string password = GetJsonString(request.Body, "password", "");
		if (!TryAuthenticateWebUser(username, password, remoteKey, out WebIdentity identity, out bool accountLocked))
		{
			bool lockedNow = accountLocked;
			if (!accountLocked)
			{
				lock (authLock)
				{
					if (!failedLogins.TryGetValue(remoteKey, out FailedLoginState state2))
					{
						EnsureFailedLoginCapacityUnsafe();
						state2 = new FailedLoginState();
						failedLogins[remoteKey] = state2;
					}
					state2.Failures++;
					state2.LastFailureUtc = DateTime.UtcNow;
					if (state2.Failures >= config.MaxFailedLogins)
					{
						state2.LockedUntilUtc = DateTime.UtcNow.AddMinutes(config.LockoutMinutes);
						state2.Failures = 0;
						lockedNow = true;
					}
				}
			}
			api.Logger.Warning("[WebDashboard] failed login from {0}{1}.", remote, lockedNow ? " (temporarily locked)" : "");
			RecordWebAudit("login", "Failed login for username '" + username + "'.", lockedNow ? "Failed / locked" : "Failed", remoteKey, string.IsNullOrWhiteSpace(username) ? "unknown" : username);
			await WriteJsonAsync(stream, lockedNow ? 429 : 401, new
			{
				error = (lockedNow ? "Too many failed logins. Try again later." : "Invalid username or password.")
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string token = RandomToken(32);
		string csrf = RandomToken(24);
		DateTime expires = DateTime.UtcNow.AddMinutes(config.SessionTimeoutMinutes);
		lock (authLock)
		{
			failedLogins.Remove(remoteKey);
			PruneSessionsUnsafe();
			EnsureSessionCapacityUnsafe();
			sessions[token] = new WebSession
			{
				Token = token,
				CsrfToken = csrf,
				RemoteIp = remoteKey,
				Username = identity.Username,
				Role = identity.Role,
				IsOwner = identity.IsOwner,
				ExpiresUtc = expires,
				CreatedUtc = DateTime.UtcNow
			};
		}
		api.Logger.Notification("[WebDashboard] panel login for {0} ({1}) from {2}.", identity.Username, identity.Role, remote);
		RecordWebAudit("login", "Administrator authenticated.", "Success", remoteKey, identity.Username);
		await WriteJsonAsync(setCookie: $"nsu_session={token}; Path=/; HttpOnly; SameSite=Strict; Max-Age={config.SessionTimeoutMinutes * 60}" + (config.SecureCookies ? "; Secure" : ""), stream: stream, statusCode: 200, value: new
		{
			success = true,
			username = identity.Username,
			role = identity.Role,
			owner = identity.IsOwner,
			csrfToken = csrf,
			expiresUtc = expires,
			safeDiagnosticsMode = config.SafeDiagnosticsMode
		}, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private bool TryGetSession(WebRequest request, out WebSession? session)
	{
		session = null;
		if (!TryGetCookie(request, "nsu_session", out string value) || string.IsNullOrWhiteSpace(value))
		{
			return false;
		}
		lock (authLock)
		{
			PruneSessionsUnsafe();
			if (!sessions.TryGetValue(value, out session))
			{
				return false;
			}
			return true;
		}
	}

	private void RemoveSession(WebRequest request)
	{
		if (!TryGetCookie(request, "nsu_session", out string value))
		{
			return;
		}
		lock (authLock)
		{
			sessions.Remove(value);
		}
	}

	private void PruneSessionsUnsafe()
	{
		DateTime now = DateTime.UtcNow;
		foreach (string item in (from x in sessions
			where x.Value.ExpiresUtc <= now
			select x.Key).ToList())
		{
			sessions.Remove(item);
		}
	}

	private void EnsureSessionCapacityUnsafe()
	{
		if (sessions.Count >= 128)
		{
			string text = (from x in sessions
				orderby x.Value.CreatedUtc
				select x.Key).FirstOrDefault();
			if (!string.IsNullOrEmpty(text))
			{
				sessions.Remove(text);
			}
		}
	}

	private void EnsureFailedLoginCapacityUnsafe()
	{
		if (failedLogins.Count >= 2048)
		{
			string text = (from x in failedLogins
				orderby x.Value.LastFailureUtc
				select x.Key).FirstOrDefault();
			if (!string.IsNullOrEmpty(text))
			{
				failedLogins.Remove(text);
			}
		}
	}

	private void PruneFailedLoginsUnsafe()
	{
		DateTime cutoff = DateTime.UtcNow.AddHours(-24.0);
		foreach (string item in (from x in failedLogins
			where x.Value.LockedUntilUtc <= DateTime.UtcNow && x.Value.LastFailureUtc < cutoff
			select x.Key).ToList())
		{
			failedLogins.Remove(item);
		}
	}

	private bool IsLocalAnonymousRequest(IPAddress remote)
	{
		return LocalAnonymousMode && IPAddress.IsLoopback(remote);
	}

	private bool IsRemoteAllowed(IPAddress remote)
	{
		if (config.AllowedNetworks.Count == 0)
		{
			return true;
		}
		foreach (string allowedNetwork in config.AllowedNetworks)
		{
			if (IpNetworkContains(allowedNetwork, remote))
			{
				return true;
			}
		}
		return false;
	}

	private static bool IpNetworkContains(string entry, IPAddress remote)
	{
		entry = (entry ?? "").Trim();
		if (entry.Length == 0)
		{
			return false;
		}
		string[] array = entry.Split('/', 2);
		if (!IPAddress.TryParse(array[0], out IPAddress address))
		{
			return false;
		}
		if (address.AddressFamily == AddressFamily.InterNetwork && remote.IsIPv4MappedToIPv6)
		{
			remote = remote.MapToIPv4();
		}
		if (address.AddressFamily != remote.AddressFamily)
		{
			return false;
		}
		byte[] addressBytes = address.GetAddressBytes();
		byte[] addressBytes2 = remote.GetAddressBytes();
		if (array.Length == 1)
		{
			return addressBytes.SequenceEqual(addressBytes2);
		}
		if (!int.TryParse(array[1], out var result))
		{
			return false;
		}
		int num = addressBytes.Length * 8;
		if (result < 0 || result > num)
		{
			return false;
		}
		int num2 = result / 8;
		int num3 = result % 8;
		for (int i = 0; i < num2; i++)
		{
			if (addressBytes[i] != addressBytes2[i])
			{
				return false;
			}
		}
		if (num3 == 0)
		{
			return true;
		}
		int num4 = 255 << 8 - num3;
		return (addressBytes[num2] & num4) == (addressBytes2[num2] & num4);
	}

	private static bool TryParseBindAddress(string? value, out IPAddress address)
	{
		value = (value ?? "").Trim();
		if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase) || value.Equals("loopback", StringComparison.OrdinalIgnoreCase))
		{
			address = IPAddress.Loopback;
			return true;
		}
		if (value == "0.0.0.0" || value == "*")
		{
			address = IPAddress.Any;
			return true;
		}
		if (value == "::")
		{
			address = IPAddress.IPv6Any;
			return true;
		}
		if (IPAddress.TryParse(value, out IPAddress address2))
		{
			address = address2;
			return true;
		}
		address = IPAddress.None;
		return false;
	}

	private static bool IsLoopbackBind(string? bindAddress)
	{
		IPAddress address;
		return TryParseBindAddress(bindAddress, out address) && IPAddress.IsLoopback(address);
	}

	private static string DisplayBindAddress(string address)
	{
		if ((address == "0.0.0.0" || address == "*") ? true : false)
		{
			return "<server-ip>";
		}
		if (address == "::")
		{
			return "[server-ipv6]";
		}
		if (IPAddress.TryParse(address, out IPAddress address2) && address2.AddressFamily == AddressFamily.InterNetworkV6)
		{
			return "[" + address + "]";
		}
		return address;
	}

	private void RegisterCommands()
	{
		api.RegisterCommand("serverutils", "Configure Newfies Server Essentials Suite", "/serverutils web status|on|off|bind|port|publicurl|username|password|sessions|logoutall|reload|securecookies", HandleCommand, "controlserver");
	}

	private void HandleCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = (args.PopWord() ?? "").Trim().ToLowerInvariant();
		if (text != "web")
		{
			SendCommand(player, groupId, "Usage: /serverutils web status|on|off|bind <ip>|port <port>|publicurl <url|off>|username <name>|password <password>|sessions|logoutall|reload|securecookies on|off", success: false);
			return;
		}
		string text2 = (args.PopWord() ?? "status").Trim().ToLowerInvariant();
		bool flag = true;
		string text3;
		switch (text2)
		{
		case "status":
			text3 = string.Join(" | ", GetGuiStatusLines().Take(10));
			break;
		case "on":
			text3 = SetEnabled(value: true);
			flag = IsRunning;
			break;
		case "off":
			text3 = SetEnabled(value: false);
			break;
		case "bind":
			text3 = SetBindAddress(args.PopWord());
			flag = !text3.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase);
			break;
		case "port":
		{
			flag = int.TryParse(args.PopWord(), out var result);
			text3 = (flag ? SetPort(result) : "Usage: /serverutils web port <1-65535>");
			break;
		}
		case "publicurl":
			text3 = SetPublicUrl(args.PopWord());
			flag = !text3.StartsWith("Public URL must", StringComparison.OrdinalIgnoreCase) && !text3.StartsWith("Public URL is too long", StringComparison.OrdinalIgnoreCase);
			break;
		case "username":
			text3 = SetUsername(args.PopWord());
			flag = !text3.Contains("cannot be empty", StringComparison.OrdinalIgnoreCase);
			break;
		case "password":
		{
			string password = args.PopWord() ?? "";
			text3 = SetPassword(password);
			flag = text3.StartsWith("Web admin password updated", StringComparison.Ordinal);
			break;
		}
		case "sessions":
			text3 = $"Active web sessions: {ActiveSessionCount}.";
			break;
		case "logoutall":
			LogoutAllSessions();
			text3 = "All web sessions invalidated.";
			break;
		case "reload":
			ReloadConfig();
			text3 = ((IsRunning || !config.Enabled) ? "Web config reloaded." : ("Web config reloaded, but listener failed: " + LastError));
			flag = IsRunning || !config.Enabled;
			break;
		case "securecookies":
		{
			string text4 = (args.PopWord() ?? "").Trim().ToLowerInvariant();
			if ((!(text4 == "on") && !(text4 == "off")) || 1 == 0)
			{
				flag = false;
				text3 = "Usage: /serverutils web securecookies on|off";
			}
			else
			{
				text3 = SetSecureCookies(text4 == "on");
			}
			break;
		}
		default:
			flag = false;
			text3 = "Unknown web command. Use /serverutils web status.";
			break;
		}
		SendCommand(player, groupId, text3, flag);
	}

	private void SendCommand(IServerPlayer player, int groupId, string message, bool success)
	{
		api.SendMessage(player, groupId, message, (!success) ? EnumChatType.CommandError : EnumChatType.CommandSuccess);
	}

	private void MaintainPerformanceCenter()
	{
		if (!module.Features.PerformanceCenter)
		{
			return;
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (lastPerformanceMaintenanceUnixMilliseconds > 0 && num - lastPerformanceMaintenanceUnixMilliseconds < 10000)
		{
			return;
		}
		lastPerformanceMaintenanceUnixMilliseconds = num;
		ServerHealthService serverHealth = module.ServerHealth;
		if (serverHealth == null || !serverHealth.Enabled)
		{
			return;
		}
		ServerHealthSnapshot snapshot = serverHealth.GetSnapshot(1800);
		long num2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		bool flag = false;
		foreach (ServerLagIncidentGroup group in snapshot.SmartAnalysis.IncidentGroups)
		{
			if (group.EndUnixMilliseconds > num2 - 15000)
			{
				continue;
			}
			string id = $"{group.StartUnixMilliseconds:x}-{group.EndUnixMilliseconds:x}";
			if (performanceCenterStore.Incidents.Any((PerformanceIncidentArchive x) => x.Id == id))
			{
				continue;
			}
			List<ServerHealthSample> samples = snapshot.Samples;
			List<ServerHealthSample> during = samples.Where((ServerHealthSample x) => x.UnixMilliseconds >= group.StartUnixMilliseconds && x.UnixMilliseconds <= group.EndUnixMilliseconds).ToList();
			DatabaseHealthSnapshot database = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays) ?? new DatabaseHealthSnapshot
			{
				Available = false,
				Message = "BlockLogger storage unavailable."
			};
			WebServerDiagnosticsSnapshot webServerDiagnosticsSnapshot = BuildWebDiagnosticsSnapshot();
			List<WebAuditRecord> recentAdminActions;
			lock (operationsLock)
			{
				recentAdminActions = (from x in operationsCenterStore.Audit ?? new List<WebAuditRecord>()
					where x.UnixMilliseconds >= @group.StartUnixMilliseconds - 120000 && x.UnixMilliseconds <= @group.EndUnixMilliseconds + 30000
					orderby x.UnixMilliseconds descending
					select x).Take(12).ToList();
			}
			performanceCenterStore.Incidents.Add(new PerformanceIncidentArchive
			{
				Id = id,
				StartUnixMilliseconds = group.StartUnixMilliseconds,
				EndUnixMilliseconds = group.EndUnixMilliseconds,
				CapturedUnixMilliseconds = num2,
				DurationSeconds = group.DurationSeconds,
				Severity = group.Severity,
				PrimaryCategory = group.PrimaryCategory,
				Confidence = group.Confidence,
				Evidence = group.Evidence,
				NextAction = group.NextAction,
				LikelyModId = group.LikelyModId,
				LikelyModName = group.LikelyModName,
				ModConfidence = group.ModConfidence,
				ModEvidence = group.ModEvidence,
				ModAttributionSource = group.ModAttributionSource,
				ModProfileCompletedUnixMilliseconds = group.ModProfileCompletedUnixMilliseconds,
				WorstDelayMs = group.WorstDelayMs,
				MaxCpuPercent = group.MaxCpuPercent,
				Before = samples.Where((ServerHealthSample x) => x.UnixMilliseconds >= group.StartUnixMilliseconds - 30000 && x.UnixMilliseconds < group.StartUnixMilliseconds).ToList(),
				During = during,
				After = samples.Where((ServerHealthSample x) => x.UnixMilliseconds > group.EndUnixMilliseconds && x.UnixMilliseconds <= group.EndUnixMilliseconds + 15000).ToList(),
				Database = database,
				WebServer = webServerDiagnosticsSnapshot,
				RecentAdminActions = recentAdminActions,
				CauseScores = BuildIncidentCauseScores(group, during, webServerDiagnosticsSnapshot, database)
			});
			flag = true;
		}
		PerformanceEnvironmentSnapshot environment = CaptureEnvironmentSnapshot(snapshot);
		if (performanceCenterStore.CurrentEnvironment == null)
		{
			performanceCenterStore.CurrentEnvironment = environment;
			flag = true;
		}
		else if (!string.Equals(performanceCenterStore.CurrentEnvironment.Fingerprint, environment.Fingerprint, StringComparison.Ordinal))
		{
			PerformanceEnvironmentSnapshot currentEnvironment = performanceCenterStore.CurrentEnvironment;
			performanceCenterStore.EnvironmentHistory.Insert(0, new PerformanceEnvironmentChange
			{
				DetectedUnixMilliseconds = num2,
				BeforeFingerprint = currentEnvironment.Fingerprint,
				AfterFingerprint = environment.Fingerprint,
				ChangedMods = DescribeModChanges(currentEnvironment.Mods, environment.Mods),
				Before = currentEnvironment.Metrics,
				After = environment.Metrics,
				Result = "Collecting",
				Confidence = "Low",
				Reason = "Mod environment changed. Collecting at least 60 current diagnostic samples before grading the regression."
			});
			performanceCenterStore.CurrentEnvironment = environment;
			flag = true;
		}
		else
		{
			performanceCenterStore.CurrentEnvironment = environment;
			PerformanceEnvironmentChange performanceEnvironmentChange = performanceCenterStore.EnvironmentHistory.FirstOrDefault((PerformanceEnvironmentChange x) => x.Result == "Collecting" && string.Equals(x.AfterFingerprint, environment.Fingerprint, StringComparison.Ordinal));
			if (performanceEnvironmentChange != null)
			{
				performanceEnvironmentChange.After = environment.Metrics;
				if (environment.Metrics.SampleCount >= 60)
				{
					GradeEnvironmentChange(performanceEnvironmentChange);
					flag = true;
				}
			}
		}
		if (flag)
		{
			SavePerformanceCenterStore();
		}
	}

	private PerformanceEnvironmentSnapshot CaptureEnvironmentSnapshot(ServerHealthSnapshot snapshot)
	{
		if (environmentModIdentityCache == null)
		{
			environmentModIdentityCache = (from x in api.ModLoader.Mods
				where x?.Info != null
				select new PerformanceModIdentity
				{
					ModId = (x.Info.ModID ?? ""),
					Name = (x.Info.Name ?? x.Info.ModID ?? ""),
					Version = (x.Info.Version ?? ""),
					FileName = (x.FileName ?? "")
				}).OrderBy((PerformanceModIdentity x) => x.ModId, StringComparer.OrdinalIgnoreCase).ThenBy((PerformanceModIdentity x) => x.Version, StringComparer.OrdinalIgnoreCase).ToList();
			string s = string.Join("\n", environmentModIdentityCache.Select((PerformanceModIdentity x) => $"{x.ModId}|{x.Version}|{x.FileName}"));
			environmentModFingerprintCache = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
		}
		return new PerformanceEnvironmentSnapshot
		{
			Fingerprint = environmentModFingerprintCache,
			CapturedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Mods = environmentModIdentityCache,
			Metrics = CaptureEnvironmentMetrics(snapshot)
		};
	}

	private static PerformanceEnvironmentMetrics CaptureEnvironmentMetrics(ServerHealthSnapshot snapshot)
	{
		return new PerformanceEnvironmentMetrics
		{
			SampleCount = snapshot.Samples.Count,
			HealthScore = snapshot.SmartAnalysis.HealthScore,
			DelayMedianMs = snapshot.SmartAnalysis.RecentDelayMedianMs,
			CpuMedianPercent = snapshot.SmartAnalysis.RecentCpuMedianPercent,
			HeapMedianMb = snapshot.SmartAnalysis.RecentHeapMedianMb,
			EntityMedian = snapshot.SmartAnalysis.RecentEntityMedian,
			QueueMedian = snapshot.SmartAnalysis.RecentQueueMedian,
			WriterMedianMs = snapshot.SmartAnalysis.RecentWriterMedianMs,
			P95ProfileTickMs = snapshot.LastProfile.P95TickMs
		};
	}

	private static List<string> DescribeModChanges(List<PerformanceModIdentity> before, List<PerformanceModIdentity> after)
	{
		Dictionary<string, PerformanceModIdentity> dictionary = before.ToDictionary((PerformanceModIdentity x) => x.ModId, (PerformanceModIdentity x) => x, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, PerformanceModIdentity> dictionary2 = after.ToDictionary((PerformanceModIdentity x) => x.ModId, (PerformanceModIdentity x) => x, StringComparer.OrdinalIgnoreCase);
		List<string> list = new List<string>();
		foreach (PerformanceModIdentity value2 in dictionary2.Values)
		{
			if (!dictionary.TryGetValue(value2.ModId, out var value))
			{
				list.Add("+ " + value2.ModId + " " + value2.Version);
			}
			else if (!string.Equals(value.Version, value2.Version, StringComparison.OrdinalIgnoreCase) || !string.Equals(value.FileName, value2.FileName, StringComparison.OrdinalIgnoreCase))
			{
				list.Add($"~ {value2.ModId} {value.Version} -> {value2.Version}");
			}
		}
		foreach (PerformanceModIdentity value3 in dictionary.Values)
		{
			if (!dictionary2.ContainsKey(value3.ModId))
			{
				list.Add("- " + value3.ModId + " " + value3.Version);
			}
		}
		return list.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static void GradeEnvironmentChange(PerformanceEnvironmentChange change)
	{
		double num = LowerIsBetterPercent(change.Before.DelayMedianMs, change.After.DelayMedianMs, 5.0);
		double num2 = LowerIsBetterPercent(change.Before.CpuMedianPercent, change.After.CpuMedianPercent, 10.0);
		double num3 = LowerIsBetterPercent(change.Before.WriterMedianMs, change.After.WriterMedianMs, 5.0);
		double num4 = LowerIsBetterPercent(change.Before.QueueMedian, change.After.QueueMedian, 10.0);
		double num5 = LowerIsBetterPercent(change.Before.P95ProfileTickMs, change.After.P95ProfileTickMs, 10.0);
		double num6 = change.After.HealthScore - change.Before.HealthScore;
		double value = num * 0.35 + num2 * 0.25 + num3 * 0.1 + num4 * 0.1 + num5 * 0.15 + num6 * 0.05;
		value = Math.Clamp(value, -100.0, 100.0);
		change.RegressionScore = 0.0 - value;
		PerformanceEnvironmentChange performanceEnvironmentChange = change;
		string result;
		if (value >= 8.0)
		{
			result = "Improved";
		}
		else
		{
			result = ((value <= -8.0) ? "Regressed" : "No meaningful change");
		}
		performanceEnvironmentChange.Result = result;
		change.Confidence = ((change.Before.SampleCount >= 60 && change.After.SampleCount >= 60) ? "Medium" : "Low");
		change.Reason = string.Join(" | ", new string[6]
		{
			"Delay " + SignedPercent(num),
			"CPU " + SignedPercent(num2),
			"Writer " + SignedPercent(num3),
			"Queue " + SignedPercent(num4),
			"Profile P95 " + SignedPercent(num5),
			$"Health {num6:+0;-0;0} points"
		});
	}

	private List<PerformanceEntityGrowthRow> BuildEntityGrowthRows()
	{
		List<PerformanceEntityScanSnapshot> list = performanceCenterStore.EntityScans.OrderByDescending((PerformanceEntityScanSnapshot x) => x.UnixMilliseconds).Take(2).ToList();
		if (list.Count < 2)
		{
			return new List<PerformanceEntityGrowthRow>();
		}
		PerformanceEntityScanSnapshot performanceEntityScanSnapshot = list[0];
		PerformanceEntityScanSnapshot performanceEntityScanSnapshot2 = list[1];
		Dictionary<string, int> beforeMap = performanceEntityScanSnapshot2.EntityTypes.ToDictionary((PerformanceEntityTypeHotspot x) => x.Code, (PerformanceEntityTypeHotspot x) => x.Count, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> afterMap = performanceEntityScanSnapshot.EntityTypes.ToDictionary((PerformanceEntityTypeHotspot x) => x.Code, (PerformanceEntityTypeHotspot x) => x.Count, StringComparer.OrdinalIgnoreCase);
		return (from x in afterMap.Keys.Union(beforeMap.Keys, StringComparer.OrdinalIgnoreCase).Select((string code) =>
			{
				beforeMap.TryGetValue(code, out var value);
				afterMap.TryGetValue(code, out var value2);
				int num = value2 - value;
				double num2 = ((value <= 0) ? ((double)((value2 > 0) ? 100 : 0)) : ((double)num * 100.0 / (double)value));
				string text = ((num >= 500 && num2 >= 100.0) ? "Critical" : ((num >= 250 && num2 >= 75.0) ? "High" : ((num >= 100 && num2 >= 40.0) ? "Watch" : "Info")));
				return new PerformanceEntityGrowthRow
				{
					Code = code,
					Before = value,
					After = value2,
					Delta = num,
					PercentChange = num2,
					Severity = text,
					Reason = ((text == "Info") ? "No strong scan-to-scan runaway signal." : $"Entity population changed {num:+#;-#;0} ({num2:+0;-0;0}%) between the last two manual scans.")
				};
			})
			where x.Delta != 0
			select x).OrderByDescending((PerformanceEntityGrowthRow x) =>
		{
			int result = ((x.Severity == "Critical") ? 4 : ((x.Severity == "High") ? 3 : ((!(x.Severity == "Watch")) ? 1 : 2)));
			return result;
		}).ThenByDescending((PerformanceEntityGrowthRow x) => Math.Abs(x.Delta)).Take(100)
			.ToList();
	}

	private object BuildIncidentDrilldown(long startUnixMilliseconds)
	{
		MaintainPerformanceCenter();
		PerformanceIncidentArchive incident = performanceCenterStore.Incidents.OrderBy((PerformanceIncidentArchive x) => Math.Min(Math.Abs(x.StartUnixMilliseconds - startUnixMilliseconds), (startUnixMilliseconds >= x.StartUnixMilliseconds && startUnixMilliseconds <= x.EndUnixMilliseconds) ? 0 : Math.Abs(x.EndUnixMilliseconds - startUnixMilliseconds))).FirstOrDefault();
		if (incident == null || (startUnixMilliseconds > 0 && startUnixMilliseconds < incident.StartUnixMilliseconds - 120000) || (startUnixMilliseconds > 0 && startUnixMilliseconds > incident.EndUnixMilliseconds + 120000))
		{
			return new
			{
				found = false,
				message = "No archived black-box incident matched that timestamp."
			};
		}
		return new
		{
			found = true,
			incident = incident,
			beforeSummary = Summary(incident.Before),
			duringSummary = Summary(incident.During),
			afterSummary = Summary(incident.After),
			entityGrowth = BuildEntityGrowthRows().Take(10).ToList(),
			latestEntityScan = lastPerformanceEntityScan,
			environment = (from x in performanceCenterStore.EnvironmentHistory
				where Math.Abs(x.DetectedUnixMilliseconds - incident.StartUnixMilliseconds) <= 1800000
				orderby Math.Abs(x.DetectedUnixMilliseconds - incident.StartUnixMilliseconds)
				select x).FirstOrDefault()
		};
		static object Summary(List<ServerHealthSample> rows)
		{
			if (rows.Count == 0)
			{
				return new
				{
					samples = 0,
					delayAvg = 0.0,
					delayMax = 0.0,
					cpuAvg = 0.0,
					cpuMax = 0.0,
					heapAvg = 0.0,
					entitiesAvg = 0.0,
					chunksAvg = 0.0,
					queueMax = 0,
					writerMax = 0L
				};
			}
			return new
			{
				samples = rows.Count,
				delayAvg = rows.Average((ServerHealthSample x) => x.MainThreadDelayMs),
				delayMax = rows.Max((ServerHealthSample x) => x.MainThreadDelayMs),
				cpuAvg = rows.Average((ServerHealthSample x) => x.CpuPercent),
				cpuMax = rows.Max((ServerHealthSample x) => x.CpuPercent),
				heapAvg = rows.Average((ServerHealthSample x) => x.ManagedHeapMb),
				entitiesAvg = rows.Average((ServerHealthSample x) => x.LoadedEntities),
				chunksAvg = rows.Average((ServerHealthSample x) => x.LoadedChunks),
				queueMax = rows.Max((ServerHealthSample x) => x.BlockLoggerQueue),
				writerMax = rows.Max((ServerHealthSample x) => x.BlockLoggerWriterMs)
			};
		}
	}

	private object BuildPerformanceTimeline(int windowSeconds)
	{
		ServerHealthSnapshot serverHealthSnapshot = module.ServerHealth?.GetSnapshot(600, windowSeconds);
		if (serverHealthSnapshot == null)
		{
			return new
			{
				available = false,
				samples = Array.Empty<object>()
			};
		}
		long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)windowSeconds * 1000L;
		return new
		{
			available = true,
			samples = serverHealthSnapshot.Samples,
			incidents = (from x in performanceCenterStore.Incidents
				where x.StartUnixMilliseconds >= cutoff
				orderby x.StartUnixMilliseconds
				select x).ToList(),
			environmentChanges = (from x in performanceCenterStore.EnvironmentHistory
				where x.DetectedUnixMilliseconds >= cutoff
				orderby x.DetectedUnixMilliseconds
				select x).ToList(),
			entityScans = (from x in performanceCenterStore.EntityScans
				where x.UnixMilliseconds >= cutoff
				orderby x.UnixMilliseconds
				select new { x.UnixMilliseconds, x.TotalLoadedEntities, x.PositionedEntities }).ToList()
		};
	}

	private object BuildPerformanceWindowSummary(ServerHealthSnapshot health)
	{
		List<ServerHealthSample> list = (health.Samples ?? new List<ServerHealthSample>()).OrderBy((ServerHealthSample x) => x.UnixMilliseconds).ToList();
		if (list.Count == 0)
		{
			return new
			{
				available = false,
				sampleCount = 0
			};
		}
		ServerHealthSample serverHealthSample = list[0];
		ServerHealthSample serverHealthSample2 = list[list.Count - 1];
		double durationSeconds = Math.Max(0.0, (double)(serverHealthSample2.UnixMilliseconds - serverHealthSample.UnixMilliseconds) / 1000.0);
		long blockLoggerDroppedIncrease = Math.Max(0L, serverHealthSample2.BlockLoggerDropped - serverHealthSample.BlockLoggerDropped);
		int gen2CollectionsIncrease = Math.Max(0, serverHealthSample2.Gen2Collections - serverHealthSample.Gen2Collections);
		return new
		{
			available = true,
			sampleCount = list.Count,
			durationSeconds = durationSeconds,
			delayMedianMs = Median(list.Select((ServerHealthSample x) => x.MainThreadDelayMs)),
			delayP95Ms = Percentile(list.Select((ServerHealthSample x) => x.MainThreadDelayMs)),
			delayMaxMs = list.Max((ServerHealthSample x) => x.MainThreadDelayMs),
			cpuAveragePercent = list.Average((ServerHealthSample x) => x.CpuPercent),
			cpuP95Percent = Percentile(list.Select((ServerHealthSample x) => x.CpuPercent)),
			cpuMaxPercent = list.Max((ServerHealthSample x) => x.CpuPercent),
			heapDeltaMb = serverHealthSample2.ManagedHeapMb - serverHealthSample.ManagedHeapMb,
			privateMemoryDeltaMb = serverHealthSample2.PrivateMemoryMb - serverHealthSample.PrivateMemoryMb,
			entityDelta = serverHealthSample2.LoadedEntities - serverHealthSample.LoadedEntities,
			maxBlockLoggerQueue = list.Max((ServerHealthSample x) => x.BlockLoggerQueue),
			maxBlockLoggerWriterMs = list.Max((ServerHealthSample x) => x.BlockLoggerWriterMs),
			blockLoggerDroppedIncrease = blockLoggerDroppedIncrease,
			gen2CollectionsIncrease = gen2CollectionsIncrease,
			delayOver100Count = list.Count((ServerHealthSample x) => x.MainThreadDelayMs >= 100.0),
			delayOver250Count = list.Count((ServerHealthSample x) => x.MainThreadDelayMs >= 250.0)
		};
		static double Median(IEnumerable<double> source)
		{
			double[] array = source.OrderBy((double x) => x).ToArray();
			if (array.Length == 0)
			{
				return 0.0;
			}
			int num = array.Length / 2;
			return (array.Length % 2 == 0) ? ((array[num - 1] + array[num]) / 2.0) : array[num];
		}
		static double Percentile(IEnumerable<double> source)
		{
			double[] array = source.OrderBy((double x) => x).ToArray();
			if (array.Length == 0)
			{
				return 0.0;
			}
			int num = (int)Math.Min(array.Length - 1, Math.Ceiling((double)array.Length * 0.95) - 1.0);
			return array[num];
		}
	}

	private object BuildPerformanceOverview(int windowSeconds)
	{
		ServerHealthSnapshot serverHealthSnapshot = module.ServerHealth?.GetSnapshot(360, windowSeconds);
		if (serverHealthSnapshot == null)
		{
			return new
			{
				available = false,
				message = "Server health diagnostics are unavailable.",
				entityScan = lastPerformanceEntityScan
			};
		}
		MaintainPerformanceCenter();
		List<PerformanceIncidentArchive> list = (from x in performanceCenterStore.Incidents
			where x.StartUnixMilliseconds >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)windowSeconds * 1000L
			orderby x.EndUnixMilliseconds descending
			select x).Take(40).ToList();
		List<PerformanceEntityGrowthRow> source = BuildEntityGrowthRows();
		IReadOnlyList<SuiteTimingMetricSnapshot> snapshot = SuiteTimingRegistry.GetSnapshot();
		object windowSummary = BuildPerformanceWindowSummary(serverHealthSnapshot);
		ServerProfilerModHotspot serverProfilerModHotspot = (from x in serverHealthSnapshot.ModHotspots
			where !x.IsCore && !string.IsNullOrWhiteSpace(x.ModId) && x.ModId != "<unattributed>" && x.Confidence != "Low"
			orderby x.TotalMs descending
			select x).FirstOrDefault();
		return new
		{
			available = true,
			diagnosticsEnabled = serverHealthSnapshot.Enabled,
			current = serverHealthSnapshot.Current,
			diagnosticSummary = serverHealthSnapshot.DiagnosticSummary,
			smart = serverHealthSnapshot.SmartAnalysis,
			incidents = list,
			incidentCount = list.Count,
			archivedIncidentCount = performanceCenterStore.Incidents.Count,
			entityScan = lastPerformanceEntityScan,
			entityGrowth = source.Take(20).ToList(),
			environmentCurrent = performanceCenterStore.CurrentEnvironment,
			environmentHistory = performanceCenterStore.EnvironmentHistory.OrderByDescending((PerformanceEnvironmentChange x) => x.DetectedUnixMilliseconds).Take(5).ToList(),
			webDiagnostics = BuildWebDiagnosticsSnapshot(),
			databaseDiagnostics = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays),
			suiteTimings = snapshot,
			windowSummary = windowSummary,
			automaticModAttribution = new
			{
				enabled = serverHealthSnapshot.AutomaticIncidentProfilingEnabled,
				profileDurationSeconds = serverHealthSnapshot.AutomaticIncidentProfileDurationSeconds,
				cooldownSeconds = serverHealthSnapshot.AutomaticIncidentProfileCooldownSeconds,
				lastTriggeredUnixMilliseconds = serverHealthSnapshot.LastAutomaticIncidentTriggerUnixMilliseconds,
				lastTriggerDelayMs = serverHealthSnapshot.LastAutomaticIncidentTriggerDelayMs,
				latestProfileCompletedUnixMilliseconds = serverHealthSnapshot.LastProfile.CompletedUnixMilliseconds,
				latestProfileKind = serverHealthSnapshot.LastProfile.CaptureKind,
				likelyModId = (serverProfilerModHotspot?.ModId ?? ""),
				likelyModName = (serverProfilerModHotspot?.ModName ?? ""),
				confidence = (serverProfilerModHotspot?.Confidence ?? "Low"),
				tickSharePercent = (serverProfilerModHotspot?.PercentOfProfileTickTime ?? 0.0),
				maxSegmentMs = (serverProfilerModHotspot?.MaxSingleMs ?? 0.0)
			},
			entityScanManualOnly = true,
			notes = new string[4] { "Automatic lag incidents use lightweight rolling diagnostics; high/critical main-thread stalls without a strong known subsystem correlation can trigger a short cooldown-limited profiler burst for mod attribution.", "Entity/chunk hotspot scans are manual-only.", "Block/container activity hotspots count recent BlockLogger events and do not represent block-entity CPU timing.", "Harmony conflict inspection reports multiple patch owners on the same method; overlap is a warning, not proof of incompatibility." }
		};
	}

	private PerformanceEntityScanSnapshot BuildPerformanceEntityScan()
	{
		Dictionary<string, (string, int, HashSet<string>)> dictionary = new Dictionary<string, (string, int, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, (int, int, int, int, int, Dictionary<string, int>)> dictionary2 = new Dictionary<string, (int, int, int, int, int, Dictionary<string, int>)>(StringComparer.Ordinal);
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		foreach (Entity value5 in api.World.LoadedEntities.Values)
		{
			if (value5 == null)
			{
				continue;
			}
			num3++;
			string key = value5.Code?.ToString() ?? value5.Properties?.Code?.ToString() ?? value5.GetType().Name;
			string name = value5.GetType().Name;
			if (!EntityPositionResolver.TryGetCoordinates(value5, out var x, out var _, out var z, out var dimension))
			{
				num2++;
				if (!dictionary.TryGetValue(key, out var value))
				{
					value = (name, 0, new HashSet<string>());
				}
				value.Item2++;
				dictionary[key] = value;
				continue;
			}
			num++;
			int num4 = (int)Math.Floor(x / 32.0);
			int num5 = (int)Math.Floor(z / 32.0);
			string text = $"{dimension}:{num4}:{num5}";
			if (!dictionary.TryGetValue(key, out var value2))
			{
				value2 = (name, 0, new HashSet<string>());
			}
			value2.Item2++;
			value2.Item3.Add(text);
			dictionary[key] = value2;
			if (!dictionary2.TryGetValue(text, out var value3))
			{
				value3 = (num4, num5, dimension, 0, 0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
			}
			value3.Item4++;
			value3.Item6.TryGetValue(key, out var value4);
			value3.Item6[key] = value4 + 1;
			if (value5 is EntityPlayer)
			{
				value3.Item5++;
			}
			dictionary2[text] = value3;
		}
		List<PerformanceEntityTypeHotspot> entityTypes = (from performanceEntityTypeHotspot in dictionary.Select((KeyValuePair<string, (string RuntimeType, int Count, HashSet<string> Chunks)> pair) => new PerformanceEntityTypeHotspot
			{
				Code = pair.Key,
				RuntimeType = pair.Value.RuntimeType,
				Count = pair.Value.Count,
				ChunkCount = pair.Value.Chunks.Count
			})
			orderby performanceEntityTypeHotspot.Count descending
			select performanceEntityTypeHotspot).ThenBy((PerformanceEntityTypeHotspot performanceEntityTypeHotspot) => performanceEntityTypeHotspot.Code, StringComparer.OrdinalIgnoreCase).Take(100).ToList();
		List<PerformanceChunkHotspot> chunks = (from performanceChunkHotspot in dictionary2.Values.Select(((int X, int Z, int Dimension, int Entities, int Players, Dictionary<string, int> Types) chunk) =>
			{
				IEnumerable<string> values = from keyValuePair in chunk.Types.OrderByDescending((KeyValuePair<string, int> keyValuePair) => keyValuePair.Value).ThenBy((KeyValuePair<string, int> keyValuePair) => keyValuePair.Key, StringComparer.OrdinalIgnoreCase).Take(4)
					select $"{keyValuePair.Key} ×{keyValuePair.Value}";
				int pressureScore = chunk.Entities + chunk.Types.Count * 3 + chunk.Players * 25;
				return new PerformanceChunkHotspot
				{
					ChunkX = chunk.X,
					ChunkZ = chunk.Z,
					Dimension = chunk.Dimension,
					EntityCount = chunk.Entities,
					PlayerCount = chunk.Players,
					UniqueEntityTypes = chunk.Types.Count,
					TopEntityTypes = string.Join(", ", values),
					PressureScore = pressureScore
				};
			})
			orderby performanceChunkHotspot.PressureScore descending, performanceChunkHotspot.EntityCount descending
			select performanceChunkHotspot).Take(200).ToList();
		return new PerformanceEntityScanSnapshot
		{
			UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			TotalLoadedEntities = num3,
			PositionedEntities = num,
			UnpositionedEntities = num2,
			EntityTypes = entityTypes,
			Chunks = chunks
		};
	}

	private object BuildLiveSnapshot()
	{
		ServerHealthService serverHealth = module.ServerHealth;
		ServerHealthSample serverHealthSample = serverHealth?.Current ?? new ServerHealthSample();
		LogStorageService storage = module.Storage;
		AnimalLimiterSystem current = AnimalLimiterSystem.Current;
		LoginProtectionSystem current2 = LoginProtectionSystem.Current;
		int activeAlerts;
		int criticalAlerts;
		int openActions;
		lock (operationsLock)
		{
			activeAlerts = operationsCenterStore.Alerts.Count((ServerAlertRecord x) =>
			{
				string status = x.Status;
				return (status == "Active" || status == "Investigating") ? true : false;
			});
			criticalAlerts = operationsCenterStore.Alerts.Count((ServerAlertRecord x) =>
			{
				string status = x.Status;
				bool flag = ((status == "Active" || status == "Investigating") ? true : false);
				return flag && x.Severity == "Critical";
			});
			openActions = operationsCenterStore.Actions.Count((ServerActionRecord x) =>
			{
				string status = x.Status;
				return (status == "Open" || status == "Investigating") ? true : false;
			});
		}
		return new
		{
			buildId = "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1",
			unixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			diagnosticsEnabled = (serverHealth?.Enabled ?? false),
			current = new
			{
				MainThreadDelayMs = serverHealthSample.MainThreadDelayMs,
				CpuPercent = serverHealthSample.CpuPercent,
				WorkingSetMb = serverHealthSample.WorkingSetMb,
				ManagedHeapMb = serverHealthSample.ManagedHeapMb,
				Players = (api.World.AllOnlinePlayers?.Length ?? 0),
				LoadedEntities = serverHealthSample.LoadedEntities,
				LoadedChunks = serverHealthSample.LoadedChunks,
				BlockLoggerQueue = serverHealthSample.BlockLoggerQueue,
				BlockLoggerWriterMs = serverHealthSample.BlockLoggerWriterMs
			},
			blockLogger = new
			{
				ready = (storage?.IsReady ?? false),
				pending = (storage?.PendingEvents ?? 0),
				dropped = (storage?.DroppedEvents ?? 0),
				activeContainerSessions = module.ActiveContainerSessions,
				containerSessionLimit = module.Config.MaxTrackedContainerSessions,
				containerSessionsEvicted = module.ContainerSessionsEvicted,
				containerSessionsWithExpiredInventory = module.ContainerSessionsWithExpiredInventory
			},
			animalLimiter = new
			{
				available = (current != null),
				enabled = (current?.Config.Enabled == true),
				trackedEntities = (current?.HealthTrackedEntityCount ?? 0),
				trackedRegions = (current?.HealthTrackedRegionCount ?? 0)
			},
			loginProtection = new
			{
				available = (current2 != null),
				enabled = (current2?.Config.Enabled ?? false),
				activeProtected = (current2?.ActiveProtectedCount ?? 0)
			},
			operations = new { activeAlerts, criticalAlerts, openActions },
			web = new
			{
				running = IsRunning,
				activeRequests = Volatile.Read(in webActiveRequests)
			}
		};
	}

	private object BuildCollectionAuditSnapshot()
	{
		List<FeatureOverheadSnapshot> measuredSuiteOverhead = SnapshotOverhead();
		WebServerDiagnosticsSnapshot webDiagnostics = BuildWebDiagnosticsSnapshot();
		object[] collectors = new object[18]
		{
			new
			{
				name = "Server health core sampler",
				cadence = "1 second",
				thread = "Main thread callback",
				cost = "Low",
				automatic = true,
				cached = true,
				detail = "Scheduler delay, process CPU, memory/GC counters, online player count, loaded-entity dictionary Count, BlockLogger queue/writer state."
			},
			new
			{
				name = "Loaded chunk count",
				cadence = "30 seconds",
				thread = "Main thread callback",
				cost = "Moderate",
				automatic = true,
				cached = true,
				detail = "LoadedChunkIndices is intentionally sampled only every 30 seconds and the last value is reused between samples."
			},
			new
			{
				name = "Smart Diagnosis",
				cadence = "On snapshot request, cached for 5 seconds",
				thread = "Main thread / in-memory",
				cost = "Low-Moderate",
				automatic = true,
				cached = true,
				detail = "Median/MAD, incident grouping, trend and confidence calculations over bounded in-memory history. No world/entity walk."
			},
			new
			{
				name = "Operations maintenance",
				cadence = "60 seconds",
				thread = "Main thread callback",
				cost = "Low",
				automatic = true,
				cached = true,
				detail = "Updates bounded alerts/actions, player-load buckets, world-save/network observations and retention."
			},
			new
			{
				name = "Player-count analytics",
				cadence = "Join/leave events + 60 second reconciliation",
				thread = "Main thread callback",
				cost = "Very Low",
				automatic = true,
				cached = true,
				detail = "Tracks only player UIDs/count transitions and time-weighted daily/hourly buckets. No entity/world scan and independent of Profiler diagnostics."
			},
			new
			{
				name = "Reports persistence",
				cadence = "On report changes + dirty flush up to every 60 seconds/world save",
				thread = "Main thread callback",
				cost = "Very Low",
				automatic = true,
				cached = true,
				detail = "Serializes only the bounded per-world report queue when dirty. No world/entity scan; new-report notifications iterate only currently online players."
			},
			new
			{
				name = "Announcement scheduler",
				cadence = "10 seconds",
				thread = "Main thread callback",
				cost = "Very Low",
				automatic = true,
				cached = true,
				detail = "Checks only the bounded announcement-rule list for due timestamps. No world/entity scan; messages are sent only when a rule becomes due."
			},
			new
			{
				name = "Web console process sampler",
				cadence = "2 seconds",
				thread = "Main thread callback",
				cost = "Very Low",
				automatic = true,
				cached = true,
				detail = "Samples current-process CPU/working-set and online-player count. Log text is read only when the Web Console page requests it."
			},
			new
			{
				name = "Mod update checker",
				cadence = "Delayed startup + every 6 hours, or manual",
				thread = "Background HTTP",
				cost = "Very Low local CPU / external request",
				automatic = true,
				cached = true,
				detail = "Sends installed non-core mod identifiers/versions and the target game version to the official Vintage Story Mod DB compatibility endpoint. It never downloads or replaces mod files."
			},
			new
			{
				name = "Player admin tick",
				cadence = "250 ms listener; historical expiry/AFK work throttled to 1 second",
				thread = "Main thread callback",
				cost = "Low",
				automatic = true,
				cached = true,
				detail = "250 ms cadence is retained only for frozen-player enforcement and shutdown timing. Known-player expiry and movement/AFK sampling run at 1 Hz."
			},
			new
			{
				name = "BlockLogger retention queue",
				cadence = "1 hour",
				thread = "Main thread trigger / background storage",
				cost = "Very Low",
				automatic = true,
				cached = true,
				detail = "Queues retention work hourly; it does not perform a world scan."
			},
			new
			{
				name = "Container session cleanup",
				cadence = "30 seconds",
				thread = "Main thread callback",
				cost = "Very Low",
				automatic = true,
				cached = true,
				detail = "Prunes expired in-memory container tracking sessions only."
			},
			new
			{
				name = "WebDashboard v2 live snapshot",
				cadence = "Browser requests every 3 seconds while Live is ON",
				thread = "Main thread read of cached state",
				cost = "Very Low",
				automatic = false,
				cached = true,
				detail = "Reads the newest health sample and counters only; it does not build the full dashboard or walk entities/chunks."
			},
			new
			{
				name = "Animal population scan",
				cadence = "Manual only",
				thread = "Incremental across server ticks",
				cost = "Bounded",
				automatic = false,
				cached = false,
				detail = "Processes with a per-tick time budget and exposes progress through a lightweight status endpoint."
			},
			new
			{
				name = "Entity/chunk hotspot scan",
				cadence = "Manual only",
				thread = "Main thread",
				cost = "High",
				automatic = false,
				cached = true,
				detail = "Walks loaded entities only after an explicit administrator request. Protected by cooldown/exclusive-operation policy."
			},
			new
			{
				name = "Harmony compatibility deep scan",
				cadence = "Manual only",
				thread = "Main thread reflection",
				cost = "Moderate",
				automatic = false,
				cached = false,
				detail = "Inspects patched methods/owners only when Run Deep Scan is pressed; ordinary Compatibility Center loading does not run it."
			},
			new
			{
				name = "BlockLogger activity hotspot query",
				cadence = "Manual / support export",
				thread = "Background SQLite",
				cost = "Moderate",
				automatic = false,
				cached = false,
				detail = "Bounded recent-coordinate aggregation. Activity evidence only, not CPU timing."
			},
			new
			{
				name = "Bounded Vintage Story engine profile",
				cadence = "Manual 10s/30s + automatic 3s major-stall burst",
				thread = "Engine profiler",
				cost = "High / diagnostic / time-limited",
				automatic = true,
				cached = false,
				detail = "Manual profiles provide full diagnosis. Automatic attribution is limited to unexplained 750ms+ stalls, a 3-second capture, a 300-second cooldown, and is disabled by Safe Diagnostics Mode."
			}
		};
		return new
		{
			version = 2,
			generatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			collectors = collectors,
			measuredSuiteOverhead = measuredSuiteOverhead,
			webDiagnostics = webDiagnostics,
			safeguards = new string[6] { "No automatic full loaded-entity walk.", "No automatic Animal Limiter population scan.", "Loaded chunk count is throttled and cached.", "Smart Diagnosis is computed from bounded in-memory samples and cached for 5 seconds.", "Heavy web routes use cooldowns and/or the exclusive heavy-operation gate.", "Feature circuit breakers isolate repeatedly failing nonessential diagnostics." }
		};
	}

	private object BuildCompatibilitySnapshot(bool deep)
	{
		var mods = (from x in api.ModLoader.Mods
			where x?.Info != null
			select new
			{
				modId = (x.Info.ModID ?? ""),
				name = (x.Info.Name ?? ""),
				version = (x.Info.Version ?? ""),
				fileName = Path.GetFileName(x.FileName ?? "")
			}).OrderBy(x => x.modId, StringComparer.OrdinalIgnoreCase).ToList();
		bool flag = Has("th3essentials");
		bool isStratum = RuntimeCompatibility.IsStratum;
		bool flag2 = Has("synergy");
		bool flag3 = Has("tungsten");
		bool flag4 = Has("blocklogger") || Has("newfiesblockloggingsuite") || Has("newfiesblocklogger");
		List<object> checks = new List<object>();
		int critical = 0;
		int watch = 0;
		int ignoredCount = 0;
		CompatibilityIgnoreStore compatibilityIgnoreStore = compatibilityIgnores;
		if (compatibilityIgnoreStore.Ignores == null)
		{
			List<CompatibilityIgnoreRecord> list = (compatibilityIgnoreStore.Ignores = new List<CompatibilityIgnoreRecord>());
		}
		Add(flag4 ? "Critical" : "OK", "Suite", "Legacy Server Essentials / BlockLogger duplicate", flag4 ? "A legacy BlockLogger/Newfies logging mod is loaded alongside the current suite. This can duplicate commands, writers and web listeners." : "No legacy BlockLogger/Newfies logging mod ID is loaded alongside the current suite.", flag4 ? "Remove the legacy ZIP and keep only the current Newfies Server Essentials Suite build." : "");
		if (flag)
		{
			Add("OK", "Commands", "Th3Essentials command ownership", "Th3Essentials " + VersionOf("th3essentials") + " detected. Root /restart, /shutdown, /back, /spawn, /msg, /r, /starterkit, /setstarterkit and starter-kit reset aliases are left to Th3Essentials. NSE fallbacks remain under /nse; warp aliases use independent safe ownership checks, and the Player Commands page independently selects the Starter Kit provider.", "No action required unless Th3Essentials reports its own startup error.");
		}
		else
		{
			Add("Info", "Commands", "Th3Essentials not detected", "Newfies Server Essentials may own its normal root shutdown/back/spawn/msg/reply/starter-kit aliases because Th3Essentials is absent. Root /restart is additionally delegated when Stratum or another provider owns it.");
		}
		if (isStratum)
		{
			Add("OK", "Runtime", "Stratum server runtime", "Stratum detected via " + RuntimeCompatibility.StratumDetection + ". NSE intentionally does not register the root /restart alias so Stratum keeps command ownership. /nse restart remains available.", "No action required. Keep only one root /restart provider.");
		}
		if (flag2)
		{
			string text = VersionOf("synergy");
			bool flag5 = text == "1.1.25";
			Add(flag5 ? "Watch" : "OK", "Optimizer", "Synergy " + text, flag5 ? "Synergy 1.1.25 is installed on the suite's Vintage Story 1.22.7 target. Its public validation window was through 1.22.6, so treat this as a compatibility watch rather than a proven problem." : "Synergy detected. Server Essentials controls it only through its public command interface and does not embed its implementation.", flag5 ? "Use measured tests and the Harmony deep scan if behavior changes after updates." : "");
		}
		if (flag3)
		{
			Add("OK", "Optimizer", "Tungsten " + VersionOf("tungsten"), "Tungsten detected. Server Essentials uses its documented command interface and measured before/after experiments.");
		}
		if (flag2 & flag3)
		{
			Add("Info", "Optimizer", "Multiple optimization providers", "Synergy and Tungsten are both loaded. Their coexistence is not automatically a conflict; use the deep Harmony scan and measured tests instead of assuming overlap.");
		}
		string text2 = VersionOf("game");
		Add(string.Equals(text2, "1.22.7", StringComparison.OrdinalIgnoreCase) ? "OK" : "Watch", "Game", "Vintage Story target", $"Loaded game mod version is {text2}; this suite build targets {"1.22.7"}.", string.Equals(text2, "1.22.7", StringComparison.OrdinalIgnoreCase) ? "" : "Rebuild/retest the suite against the loaded game version before relying on unsupported APIs.");
		object harmony = (deep ? BuildPatchConflictSnapshot() : new
		{
			available = false,
			skipped = true,
			conflictCount = 0,
			conflicts = Array.Empty<object>(),
			owners = Array.Empty<object>(),
			message = "Deep Harmony inspection is manual-only. Press Run Deep Scan to inspect patch ownership overlaps."
		});
		string text3 = module.Reports?.RootCommandOwner ?? "Unknown";
		bool flag6 = module.Reports?.RootCommandRegisteredBySuite ?? false;
		PlayerCommandSnapshot playerCommandsSnapshot = module.PlayerCommands?.GetSnapshot();
		string owner = ((api.ChatCommands.Get("smite") != null) ? "Newfies Server Essentials / existing provider" : "Unregistered");
		NewfiesAfkModSystem? current = NewfiesAfkModSystem.Current;
		string owner2;
		if (current != null && current.RootAfkCommandRegistered)
		{
			owner2 = "Newfies Server Essentials";
		}
		else if (api.ChatCommands.Get("afk") != null)
		{
			owner2 = "Another registered provider";
		}
		else
		{
			owner2 = ((NewfiesAfkModSystem.Current == null) ? "AFK feature disabled" : "Unregistered");
		}
		var commandOwnership = new[]
		{
			new
			{
				command = "/restart",
				owner = (flag ? "Th3Essentials" : (isStratum ? "Stratum" : "Newfies Server Essentials / existing provider")),
				suiteFallback = "/nse restart",
				status = "OK"
			},
			new
			{
				command = "/restarttime",
				owner = (module.AutomatedRestarts?.GetSnapshot().RootRestartTimeOwner ?? (flag ? "Th3Essentials" : "Unregistered")),
				suiteFallback = "/nse restarttime",
				status = "OK"
			},
			new
			{
				command = "/shutdown",
				owner = (flag ? "Th3Essentials" : "Newfies Server Essentials"),
				suiteFallback = "/nse shutdown",
				status = "OK"
			},
			new
			{
				command = "/afk",
				owner = owner2,
				suiteFallback = "/nse afk",
				status = ((NewfiesAfkModSystem.Current == null) ? "Info" : "OK")
			},
			new
			{
				command = "/afkinfo",
				owner = ((api.ChatCommands.Get("afkinfo") != null) ? "Newfies Server Essentials / existing provider" : "Unregistered"),
				suiteFallback = "/nse afkinfo",
				status = "OK"
			},
			new
			{
				command = "/back",
				owner = PlayerCommandOwner("back"),
				suiteFallback = "/nse back",
				status = ((PlayerCommand("back") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/home",
				owner = PlayerCommandOwner("home"),
				suiteFallback = "/nse home",
				status = ((PlayerCommand("home") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/sethome",
				owner = PlayerCommandOwner("sethome"),
				suiteFallback = "/nse sethome",
				status = ((PlayerCommand("sethome") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/delhome",
				owner = PlayerCommandOwner("delhome"),
				suiteFallback = "/nse delhome",
				status = ((PlayerCommand("delhome") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/homes",
				owner = PlayerCommandOwner("homes"),
				suiteFallback = "/nse homes",
				status = ((PlayerCommand("homes") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/t2p",
				owner = PlayerCommandOwner("t2p"),
				suiteFallback = "/nse t2p",
				status = ((PlayerCommand("t2p") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/t2m",
				owner = PlayerCommandOwner("t2m"),
				suiteFallback = "/nse t2m",
				status = ((PlayerCommand("t2m") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/tpaccept",
				owner = ((api.ChatCommands.Get("tpaccept") != null) ? "Newfies Server Essentials / existing provider" : "Unregistered"),
				suiteFallback = "/nse tpaccept",
				status = ((api.ChatCommands.Get("tpaccept") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/rtp",
				owner = PlayerCommandOwner("rtp"),
				suiteFallback = "/nse rtp",
				status = ((PlayerCommand("rtp") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/spawn",
				owner = (flag ? "Th3Essentials" : "Newfies Server Essentials / existing provider"),
				suiteFallback = "/nse spawn",
				status = "OK"
			},
			new
			{
				command = "/msg",
				owner = (flag ? "Th3Essentials" : "Newfies Server Essentials / existing provider"),
				suiteFallback = "/nse msg",
				status = "OK"
			},
			new
			{
				command = "/r",
				owner = (flag ? "Th3Essentials" : "Newfies Server Essentials / existing provider"),
				suiteFallback = "/nse r",
				status = "OK"
			},
			new
			{
				command = "/warp",
				owner = PlayerCommandOwner("warp"),
				suiteFallback = "/nse warp",
				status = ((PlayerCommand("warp") == null) ? "Info" : "OK")
			},
			new
			{
				command = "/smite",
				owner = owner,
				suiteFallback = "/nse smite",
				status = ((api.ChatCommands.Get("smite") != null) ? "OK" : "Info")
			},
			new
			{
				command = "/setjail",
				owner = ((api.ChatCommands.Get("setjail") != null) ? "Newfies Server Essentials / existing provider" : "Unregistered"),
				suiteFallback = "/nse setjail",
				status = ((api.ChatCommands.Get("setjail") != null) ? "OK" : "Info")
			},
			new
			{
				command = "/jail",
				owner = ((api.ChatCommands.Get("jail") != null) ? "Newfies Server Essentials / existing provider" : "Unregistered"),
				suiteFallback = "/nse jail",
				status = ((api.ChatCommands.Get("jail") != null) ? "OK" : "Info")
			},
			new
			{
				command = "/unjail",
				owner = ((api.ChatCommands.Get("unjail") != null) ? "Newfies Server Essentials / existing provider" : "Unregistered"),
				suiteFallback = "/nse unjail",
				status = ((api.ChatCommands.Get("unjail") != null) ? "OK" : "Info")
			},
			new
			{
				command = "/jailinfo",
				owner = ((api.ChatCommands.Get("jailinfo") != null) ? "Newfies Server Essentials / existing provider" : "Unregistered"),
				suiteFallback = "/nse jailinfo",
				status = ((api.ChatCommands.Get("jailinfo") != null) ? "OK" : "Info")
			},
			new
			{
				command = "/report",
				owner = (flag6 ? "Newfies Server Essentials" : text3),
				suiteFallback = "/nse report",
				status = "OK"
			},
			new
			{
				command = "/nse",
				owner = "Newfies Server Essentials",
				suiteFallback = "",
				status = "OK"
			},
			new
			{
				command = "/loginprot",
				owner = "Newfies Login Protection",
				suiteFallback = "Web Login Protection page",
				status = "OK"
			},
			new
			{
				command = "/synergy",
				owner = (flag2 ? "Synergy" : "Not loaded"),
				suiteFallback = "Optimizations page",
				status = (flag2 ? "OK" : "Info")
			},
			new
			{
				command = "/tungsten",
				owner = (flag3 ? "Tungsten" : "Not loaded"),
				suiteFallback = "Optimizations page",
				status = (flag3 ? "OK" : "Info")
			}
		};
		long generatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		int count = mods.Count;
		string summary;
		if (critical > 0)
		{
			summary = "Conflict";
		}
		else
		{
			summary = ((watch > 0) ? "Watch" : "Healthy");
		}
		return new
		{
			generatedUnixMilliseconds = generatedUnixMilliseconds,
			buildId = "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1",
			targetGameVersion = "1.22.7",
			loadedModCount = count,
			summary = summary,
			criticalCount = critical,
			watchCount = watch,
			ignoredCount = ignoredCount,
			checks = checks,
			commandOwnership = commandOwnership,
			harmony = harmony,
			mods = mods.Take(500).ToList(),
			deepScan = deep
		};
		void Add(string severity, string category, string title, string detail, string action = "")
		{
			string fingerprint = CompatibilityFingerprint(category, title, detail);
			CompatibilityIgnoreRecord compatibilityIgnoreRecord = compatibilityIgnores.Ignores.FirstOrDefault((CompatibilityIgnoreRecord x) => string.Equals(x.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
			bool flag7 = compatibilityIgnoreRecord != null && !string.Equals(severity, "OK", StringComparison.OrdinalIgnoreCase) && !string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase);
			if (flag7)
			{
				ignoredCount++;
			}
			if (!flag7 && string.Equals(severity, "Critical", StringComparison.OrdinalIgnoreCase))
			{
				critical++;
			}
			if (!flag7 && string.Equals(severity, "Watch", StringComparison.OrdinalIgnoreCase))
			{
				watch++;
			}
			checks.Add(new
			{
				severity = severity,
				category = category,
				title = title,
				detail = detail,
				action = action,
				fingerprint = fingerprint,
				ignored = flag7,
				ignoredBy = (compatibilityIgnoreRecord?.IgnoredBy ?? ""),
				ignoredReason = (compatibilityIgnoreRecord?.Reason ?? ""),
				ignoredUnixMilliseconds = (compatibilityIgnoreRecord?.IgnoredUnixMilliseconds ?? 0)
			});
		}
		bool Has(string id)
		{
			return mods.Any(x => string.Equals(x.modId, id, StringComparison.OrdinalIgnoreCase));
		}
		PlayerCommandStatus? PlayerCommand(string key)
		{
			return playerCommandsSnapshot?.Commands?.FirstOrDefault((PlayerCommandStatus x) => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
		}
		string PlayerCommandOwner(string key)
		{
			return PlayerCommand(key)?.RootProvider ?? ((module.PlayerCommands == null) ? "Player Commands feature disabled" : "Unregistered");
		}
		string VersionOf(string id)
		{
			return mods.FirstOrDefault(x => string.Equals(x.modId, id, StringComparison.OrdinalIgnoreCase))?.version ?? "";
		}
	}

	private object BuildLoginProtectionConfigSnapshot()
	{
		LoginProtectionSystem current = LoginProtectionSystem.Current;
		if (current == null)
		{
			return new
			{
				available = false
			};
		}
		LoginProtectionConfig loginProtectionConfig = current.Config;
		return new
		{
			available = true,
			activeProtected = current.ActiveProtectedCount,
			pendingProtected = current.PendingProtectedCount,
			config = new
			{
				loginProtectionConfig.Enabled, loginProtectionConfig.ProtectionSeconds, loginProtectionConfig.ProtectOnJoin, loginProtectionConfig.ProtectOnRespawn, loginProtectionConfig.AnnounceOnJoin, loginProtectionConfig.AnnounceOnEnd, loginProtectionConfig.BlockPvP, loginProtectionConfig.CancelOnAttack, loginProtectionConfig.ProtectFallDamage, loginProtectionConfig.ExtinguishFire,
				loginProtectionConfig.RecoverSwimmingOnLogin
			}
		};
	}

	private LoginProtectionConfigUpdateResult ApplyLoginProtectionConfig(string body)
	{
		LoginProtectionSystem current = LoginProtectionSystem.Current;
		if (current == null)
		{
			return new LoginProtectionConfigUpdateResult(Success: false, "Login Protection is not available.");
		}
		try
		{
			LoginProtectionConfig loginProtectionConfig = current.Config;
			bool jsonBool = GetJsonBool(body, "enabled", loginProtectionConfig.Enabled);
			if (jsonBool != loginProtectionConfig.Enabled)
			{
				current.SetBooleanSettingFromGui("enabled", jsonBool);
			}
			loginProtectionConfig.ProtectionSeconds = Math.Clamp(GetJsonInt(body, "protectionSeconds", loginProtectionConfig.ProtectionSeconds), 1, 3600);
			loginProtectionConfig.ProtectOnJoin = GetJsonBool(body, "protectOnJoin", loginProtectionConfig.ProtectOnJoin);
			loginProtectionConfig.ProtectOnRespawn = GetJsonBool(body, "protectOnRespawn", loginProtectionConfig.ProtectOnRespawn);
			loginProtectionConfig.AnnounceOnJoin = GetJsonBool(body, "announceOnJoin", loginProtectionConfig.AnnounceOnJoin);
			loginProtectionConfig.AnnounceOnEnd = GetJsonBool(body, "announceOnEnd", loginProtectionConfig.AnnounceOnEnd);
			loginProtectionConfig.BlockPvP = GetJsonBool(body, "blockPvP", loginProtectionConfig.BlockPvP);
			loginProtectionConfig.CancelOnAttack = GetJsonBool(body, "cancelOnAttack", loginProtectionConfig.CancelOnAttack);
			loginProtectionConfig.ProtectFallDamage = GetJsonBool(body, "protectFallDamage", loginProtectionConfig.ProtectFallDamage);
			loginProtectionConfig.ExtinguishFire = GetJsonBool(body, "extinguishFire", loginProtectionConfig.ExtinguishFire);
			loginProtectionConfig.RecoverSwimmingOnLogin = GetJsonBool(body, "recoverSwimmingOnLogin", loginProtectionConfig.RecoverSwimmingOnLogin);
			loginProtectionConfig.Clamp();
			current.SaveConfig();
			return new LoginProtectionConfigUpdateResult(Success: true, "Login Protection configuration saved.");
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[WebDashboard] Login Protection config update failed safely: {0}: {1}", ex.GetType().Name, ex.Message);
			return new LoginProtectionConfigUpdateResult(Success: false, ex.GetType().Name + ": " + ex.Message);
		}
	}

	private object BuildOptimizationProviders()
	{
		List<Mod> source = api.ModLoader.Mods.Where((Mod x) => x?.Info != null).ToList();
		Mod mod = source.FirstOrDefault((Mod x) => string.Equals(x.Info.ModID, "tungsten", StringComparison.OrdinalIgnoreCase));
		Mod mod2 = source.FirstOrDefault((Mod x) => string.Equals(x.Info.ModID, "synergy", StringComparison.OrdinalIgnoreCase));
		return new
		{
			providers = new object[3]
			{
				Provider("newfies", "Newfies Server Essentials Suite", installed: true, "1.2.44", "Administration / diagnostics / advisor / safety / measurement", "Owns BlockLogger, Smart Diagnosis, optimization recommendations, before/after measurements, and performance web UI."),
				Provider("tungsten", "Tungsten", mod != null, mod?.Info?.Version ?? "", "Optional optimization engine", (mod == null) ? "Not installed. The 27 Tungsten optimization toggles remain disabled." : "Detected. The Optimizations page can control its documented /tungsten commands."),
				Provider("synergy", "Synergy", mod2 != null, mod2?.Info?.Version ?? "", "External optimizer / compatibility signal", (mod2 == null) ? "Not installed." : "Detected. The Optimizations page can read status and control Synergy through its documented /synergy commands, with Server Essentials recommendations and measured before/after tests.")
			}
		};
		static object Provider(string id, string name, bool installed, string version, string role, string detail)
		{
			return new { id, name, installed, version, role, detail };
		}
	}

	private object BuildPatchConflictSnapshot()
	{
		if (IsFeatureCircuitOpen("harmony-inspector", out string message))
		{
			return new
			{
				available = false,
				harmonyDetected = true,
				patchedMethodCount = 0,
				conflictCount = 0,
				conflicts = Array.Empty<object>(),
				owners = Array.Empty<object>(),
				circuitBreakerOpen = true,
				message = message
			};
		}
		Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly assembly2) => assembly2.GetType("HarmonyLib.Harmony", throwOnError: false) != null);
		if (assembly == null)
		{
			RecordFeatureSuccess("harmony-inspector", "Harmony Patch Inspector", "Moderate");
			return new
			{
				available = false,
				harmonyDetected = false,
				patchedMethodCount = 0,
				conflictCount = 0,
				conflicts = Array.Empty<object>(),
				owners = Array.Empty<object>(),
				message = "HarmonyLib was not found in the current AppDomain."
			};
		}
		try
		{
			Type type = assembly.GetType("HarmonyLib.Harmony", throwOnError: false);
			MethodInfo methodInfo = type?.GetMethod("GetAllPatchedMethods", BindingFlags.Static | BindingFlags.Public);
			MethodInfo methodInfo2 = type?.GetMethod("GetPatchInfo", BindingFlags.Static | BindingFlags.Public);
			if (methodInfo == null || methodInfo2 == null)
			{
				return new
				{
					available = false,
					harmonyDetected = true,
					patchedMethodCount = 0,
					conflictCount = 0,
					conflicts = Array.Empty<object>(),
					owners = Array.Empty<object>(),
					message = "Harmony was detected, but patch inspection methods were not available."
				};
			}
			List<MethodBase> list = (methodInfo.Invoke(null, null) as IEnumerable)?.Cast<object>().OfType<MethodBase>().ToList() ?? new List<MethodBase>();
			List<object> list2 = new List<object>();
			Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (MethodBase item in list.Take(5000))
			{
				object obj = methodInfo2.Invoke(null, new object[1] { item });
				if (obj == null)
				{
					continue;
				}
				List<string> list3 = (obj.GetType().GetProperty("Owners", BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj) as IEnumerable)?.Cast<object>().Select((object x) => x?.ToString() ?? "").Where((string x) => !string.IsNullOrWhiteSpace(x))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase)
					.ToList() ?? new List<string>();
				foreach (string item2 in list3)
				{
					dictionary.TryGetValue(item2, out var value);
					dictionary[item2] = value + 1;
				}
				if (list3.Count >= 2)
				{
					list2.Add(new
					{
						declaringType = (item.DeclaringType?.FullName ?? ""),
						method = item.Name,
						signature = (item.ToString() ?? item.Name),
						ownerCount = list3.Count,
						owners = list3,
						severity = ((list3.Count >= 3) ? "High" : "Watch"),
						note = "Multiple Harmony owners patch this method. This is overlap evidence, not proof that the patches conflict."
					});
				}
			}
			var owners = (from x in dictionary.OrderByDescending((KeyValuePair<string, int> x) => x.Value).ThenBy((KeyValuePair<string, int> x) => x.Key, StringComparer.OrdinalIgnoreCase).Take(100)
				select new
				{
					owner = x.Key,
					patchedMethods = x.Value
				}).ToList();
			RecordFeatureSuccess("harmony-inspector", "Harmony Patch Inspector", "Moderate");
			return new
			{
				available = true,
				harmonyDetected = true,
				harmonyAssembly = (assembly.GetName().Name ?? "Harmony"),
				patchedMethodCount = list.Count,
				conflictCount = list2.Count,
				conflicts = list2.Take(200).ToList(),
				owners = owners,
				message = "Harmony patch overlap inspection completed."
			};
		}
		catch (Exception ex)
		{
			RecordFeatureFailure("harmony-inspector", ex, "Harmony Patch Inspector");
			return new
			{
				available = false,
				harmonyDetected = true,
				patchedMethodCount = 0,
				conflictCount = 0,
				conflicts = Array.Empty<object>(),
				owners = Array.Empty<object>(),
				message = "Harmony patch inspection failed safely: " + ex.Message
			};
		}
	}

	private object BuildTungstenStatus()
	{
		TryAutoEvaluateOptimizationExperiment();
		Mod mod = api.ModLoader.Mods.FirstOrDefault((Mod x) => x?.Info != null && string.Equals(x.Info.ModID, "tungsten", StringComparison.OrdinalIgnoreCase));
		IChatCommand command = null;
		try
		{
			command = api.ChatCommands.Get("tungsten");
		}
		catch
		{
		}
		HashSet<string> registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (command?.AllSubcommands != null)
		{
			foreach (string key in command.AllSubcommands.Keys)
			{
				registered.Add(key);
			}
			foreach (IChatCommand item in command.Subcommands ?? Enumerable.Empty<IChatCommand>())
			{
				registered.Add(item.Name);
			}
		}
		ServerHealthSnapshot health = null;
		try
		{
			health = module.ServerHealth?.GetSnapshot(120, 300);
		}
		catch
		{
		}
		Dictionary<string, TungstenOptimizationRecommendation> recommendations = BuildTungstenRecommendations(health);
		var list = TungstenOptimizations.Select((TungstenOptimizationInfo x) =>
		{
			recommendations.TryGetValue(x.Code, out TungstenOptimizationRecommendation value);
			string code = x.Code;
			string name2 = x.Name;
			string description = x.Description;
			string category = x.Category;
			bool commandAvailable = command != null && (registered.Count == 0 || registered.Contains(x.Code));
			TungstenOptimizationRecommendation tungstenOptimizationRecommendation = value;
			if (tungstenOptimizationRecommendation == null)
			{
				tungstenOptimizationRecommendation = new TungstenOptimizationRecommendation
				{
					Code = x.Code,
					Score = 0
				};
				TungstenOptimizationRecommendation tungstenOptimizationRecommendation2 = tungstenOptimizationRecommendation;
				ServerHealthSnapshot serverHealthSnapshot2 = health;
				tungstenOptimizationRecommendation2.Level = ((serverHealthSnapshot2 != null && serverHealthSnapshot2.Enabled) ? "Not indicated" : "Need diagnostics");
				TungstenOptimizationRecommendation tungstenOptimizationRecommendation3 = tungstenOptimizationRecommendation;
				ServerHealthSnapshot serverHealthSnapshot3 = health;
				tungstenOptimizationRecommendation3.Reason = ((serverHealthSnapshot3 != null && serverHealthSnapshot3.Enabled) ? "Current Smart Diagnosis does not show a strong signal for this optimization." : "Enable BlockLogger Profiler diagnostics for at least a minute to generate server-specific recommendations.");
			}
			return new
			{
				code = code,
				name = name2,
				description = description,
				category = category,
				commandAvailable = commandAvailable,
				restartRecommended = true,
				recommendation = tungstenOptimizationRecommendation
			};
		}).ToList();
		bool flag = mod != null;
		bool flag2 = command != null;
		string obj3 = mod?.Info?.Version ?? "";
		string obj4 = mod?.Info?.Name ?? "Tungsten";
		int count = list.Count;
		bool safeDiagnosticsMode = config.SafeDiagnosticsMode;
		string text = ((mod == null) ? "This BlockLogger build does not copy Tungsten's optimization engine. Install the separate Tungsten mod to enable these controls." : "");
		ServerHealthSnapshot serverHealthSnapshot = health;
		bool installed = flag;
		bool commandRegistered = flag2;
		string version = obj3;
		string name = obj4;
		int optimizationCount = count;
		var optimizations = list;
		bool safeDiagnosticsMode2 = safeDiagnosticsMode;
		string installMessage = text;
		string recommendationState;
		if (serverHealthSnapshot == null || !serverHealthSnapshot.Enabled)
		{
			recommendationState = "Profiler diagnostics are OFF. Enable them for at least 60 seconds for server-specific optimization recommendations and before/after testing.";
		}
		else
		{
			recommendationState = (health.SmartAnalysis.Available ? $"Smart Diagnosis active: {health.SmartAnalysis.State}, health {health.SmartAnalysis.HealthScore}/100." : "Diagnostics are enabled but still learning the server baseline.");
		}
		return new
		{
			installed = installed,
			commandRegistered = commandRegistered,
			version = version,
			name = name,
			optimizationCount = optimizationCount,
			optimizations = optimizations,
			documentedVersion = "1.3.7",
			requiresTungsten = true,
			safeDiagnosticsMode = safeDiagnosticsMode2,
			installMessage = installMessage,
			recommendationState = recommendationState,
			pendingExperiment = optimizationExperiments.Pending,
			experimentHistory = optimizationExperiments.History.OrderByDescending((OptimizationExperiment x) => x.CreatedUnixMilliseconds).Take(10).ToList(),
			note = ((mod == null) ? "Tungsten is not installed. Install Tungsten separately to use these controls." : "Controls call Tungsten's public /tungsten command interface. Most Tungsten optimization changes require a server restart."),
			supports = new
			{
				status = (command != null),
				stats = (command != null),
				reload = (command != null),
				health = (command != null && (registered.Count == 0 || registered.Contains("health"))),
				manifest = (command != null && (registered.Count == 0 || registered.Contains("manifest"))),
				diagnostics = (command != null && (registered.Count == 0 || registered.Contains("diag"))),
				benchmarkHarness = (command != null && (registered.Count == 0 || registered.Contains("benchmarkharness"))),
				frameProfiler = (command != null && (registered.Count == 0 || registered.Contains("frameprofiler")))
			}
		};
	}

	private Dictionary<string, TungstenOptimizationRecommendation> BuildTungstenRecommendations(ServerHealthSnapshot? health)
	{
		Dictionary<string, int> score = TungstenOptimizations.ToDictionary((TungstenOptimizationInfo x) => x.Code, (TungstenOptimizationInfo _) => 0, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, List<string>> reasons = TungstenOptimizations.ToDictionary((TungstenOptimizationInfo x) => x.Code, (TungstenOptimizationInfo _) => new List<string>(), StringComparer.OrdinalIgnoreCase);
		if (health == null || !health.Enabled || !health.SmartAnalysis.Available)
		{
			return new Dictionary<string, TungstenOptimizationRecommendation>(StringComparer.OrdinalIgnoreCase);
		}
		ServerSmartAnalysis smartAnalysis = health.SmartAnalysis;
		foreach (ServerSmartSignal signal in smartAnalysis.Signals)
		{
			string text = (string.IsNullOrWhiteSpace(signal.Title) ? signal.Category : signal.Title);
			switch (signal.Category)
			{
			case "Entities":
				Boost(new string[3] { "entitylistreuse", "entitysimulationoptimization", "getentitiesaroundoptimization" }, 28, "Entity pressure detected: " + text);
				Boost(new string[2] { "entitydespawnpacketoptimization", "bulkentityattributespacketoptimization" }, 15, "Entity-related lag makes entity packet/despawn hot paths more relevant.");
				break;
			case "Chunks":
				Boost(new string[2] { "chunkloadingoptimization", "chunkunloadingoptimization" }, 35, "Chunk/world activity correlated with lag: " + text);
				Boost(new string[3] { "genterrazeroallocoptimization", "genterrabitarrayoptimization", "genterranativenoiseoptimization" }, 20, "Chunk/world activity suggests world-generation optimizations may help during exploration.");
				break;
			case "GC":
				Boost(new string[7] { "entitylistreuse", "blocklistreuse", "getdropslistreuse", "eventmanagerlistreuse", "physicsmanagerlistoptimization", "physicsmanagermethodlistoptimization", "getentitiesaroundoptimization" }, 24, "GC/allocation pressure detected: " + text);
				Boost(new string[1] { "genterrazeroallocoptimization" }, 18, "Zero-allocation worldgen is relevant when GC pressure rises during terrain generation.");
				break;
			case "CPU":
				Boost(new string[3] { "servermainlinqoptimization", "getplayersaroundoptimization", "getentitiesaroundoptimization" }, 24, "CPU saturation correlated with lag: " + text);
				Boost(new string[3] { "broadcastlinqoptimization", "recipebaselinqoptimization", "wildcardfastmatchoptimization" }, 12, "These reduce CPU/allocation overhead in frequently-called server paths.");
				break;
			case "MainThread":
				Boost(new string[3] { "servermainlinqoptimization", "eventmanagerlistreuse", "getentitiesaroundoptimization" }, 16, "Main-thread scheduling is falling behind: " + text);
				break;
			case "Memory":
				Boost(new string[7] { "entitylistreuse", "blocklistreuse", "getdropslistreuse", "eventmanagerlistreuse", "physicsmanagerlistoptimization", "physicsmanagermethodlistoptimization", "genterrazeroallocoptimization" }, 22, "Memory/heap growth detected: " + text);
				break;
			}
		}
		if (smartAnalysis.RecentEntityMedian >= 12000.0)
		{
			Boost(new string[3] { "entitylistreuse", "entitysimulationoptimization", "getentitiesaroundoptimization" }, 22, $"Loaded entity median is high ({smartAnalysis.RecentEntityMedian:N0}).");
		}
		if (health.Current.Players >= 10)
		{
			Boost(new string[4] { "getplayersaroundoptimization", "servermainlinqoptimization", "broadcastlinqoptimization", "bulkentityattributespacketoptimization" }, 24, $"There are {health.Current.Players} players online; player-query/broadcast optimizations become more valuable at higher concurrency.");
		}
		else if (health.Current.Players >= 5)
		{
			Boost(new string[3] { "getplayersaroundoptimization", "servermainlinqoptimization", "broadcastlinqoptimization" }, 12, $"There are {health.Current.Players} players online; player-query overhead is becoming relevant.");
		}
		Dictionary<string, TungstenOptimizationRecommendation> dictionary = new Dictionary<string, TungstenOptimizationRecommendation>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, int> item in score)
		{
			int value = item.Value;
			if (value > 0)
			{
				dictionary[item.Key] = new TungstenOptimizationRecommendation
				{
					Code = item.Key,
					Score = value,
					Level = ((value >= 70) ? "High" : ((value >= 45) ? "Medium" : "Low")),
					Reason = string.Join(" ", reasons[item.Key].Take(3))
				};
			}
		}
		return dictionary;
		void Boost(IEnumerable<string> codes, int points, string reason)
		{
			foreach (string code in codes)
			{
				if (score.ContainsKey(code))
				{
					score[code] = Math.Min(100, score[code] + points);
					if (!reasons[code].Contains(reason))
					{
						reasons[code].Add(reason);
					}
				}
			}
		}
	}

	private object BuildSynergyStatus(TungstenCommandResult statusResult)
	{
		TryAutoEvaluateOptimizationExperiment();
		Mod mod = api.ModLoader.Mods.FirstOrDefault((Mod x) => x?.Info != null && string.Equals(x.Info.ModID, "synergy", StringComparison.OrdinalIgnoreCase));
		IChatCommand command = null;
		try
		{
			command = api.ChatCommands.Get("synergy");
		}
		catch
		{
		}
		HashSet<string> registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (command?.AllSubcommands != null)
		{
			foreach (string key in command.AllSubcommands.Keys)
			{
				registered.Add(key);
			}
			foreach (IChatCommand item in command.Subcommands ?? Enumerable.Empty<IChatCommand>())
			{
				registered.Add(item.Name);
			}
		}
		ServerHealthSnapshot health = null;
		try
		{
			health = module.ServerHealth?.GetSnapshot(120, 300);
		}
		catch
		{
		}
		Dictionary<string, TungstenOptimizationRecommendation> recommendations = BuildSynergyRecommendations(health);
		string statusText = (statusResult.Success ? statusResult.Message : "");
		var list = SynergyOptimizations.Select((SynergyOptimizationInfo x) =>
		{
			recommendations.TryGetValue(x.Code, out TungstenOptimizationRecommendation value);
			bool? enabled = ParseSynergyEnabled(statusText, x.Code);
			string code = x.Code;
			string name2 = x.Name;
			string description = x.Description;
			string category = x.Category;
			string scope = x.Scope;
			string caution = x.Caution;
			bool commandAvailable = command != null && (registered.Count == 0 || registered.Contains(x.Code));
			TungstenOptimizationRecommendation tungstenOptimizationRecommendation = value;
			if (tungstenOptimizationRecommendation == null)
			{
				tungstenOptimizationRecommendation = new TungstenOptimizationRecommendation
				{
					Code = x.Code,
					Score = 0
				};
				TungstenOptimizationRecommendation tungstenOptimizationRecommendation2 = tungstenOptimizationRecommendation;
				ServerHealthSnapshot serverHealthSnapshot2 = health;
				tungstenOptimizationRecommendation2.Level = ((serverHealthSnapshot2 != null && serverHealthSnapshot2.Enabled) ? "Not indicated" : "Need diagnostics");
				TungstenOptimizationRecommendation tungstenOptimizationRecommendation3 = tungstenOptimizationRecommendation;
				ServerHealthSnapshot serverHealthSnapshot3 = health;
				tungstenOptimizationRecommendation3.Reason = ((serverHealthSnapshot3 != null && serverHealthSnapshot3.Enabled) ? "Current Smart Diagnosis does not show a strong server-side signal for this optimization." : "Enable Server Essentials diagnostics for at least a minute to generate workload-specific recommendations.");
			}
			return new
			{
				code = code,
				name = name2,
				description = description,
				category = category,
				scope = scope,
				caution = caution,
				enabled = enabled,
				commandAvailable = commandAvailable,
				restartRecommended = true,
				recommendation = tungstenOptimizationRecommendation
			};
		}).ToList();
		string text = mod?.Info?.Version ?? "";
		bool flag = mod != null && string.Equals("1.22.7", "1.22.7", StringComparison.OrdinalIgnoreCase) && string.Equals(text, "1.1.25", StringComparison.OrdinalIgnoreCase);
		bool flag2 = mod != null;
		bool flag3 = command != null;
		bool success = statusResult.Success;
		string obj3 = mod?.Info?.Name ?? "Synergy";
		int count = list.Count;
		bool safeDiagnosticsMode = config.SafeDiagnosticsMode;
		ServerHealthSnapshot serverHealthSnapshot = health;
		bool installed = flag2;
		bool commandRegistered = flag3;
		bool statusCommandSucceeded = success;
		string version = text;
		string name = obj3;
		int optimizationCount = count;
		var optimizations = list;
		bool safeDiagnosticsMode2 = safeDiagnosticsMode;
		string recommendationState;
		if (serverHealthSnapshot == null || !serverHealthSnapshot.Enabled)
		{
			recommendationState = "Profiler diagnostics are OFF. Enable them for at least 60 seconds for workload-specific recommendations and measured tests.";
		}
		else
		{
			recommendationState = (health.SmartAnalysis.Available ? $"Smart Diagnosis active: {health.SmartAnalysis.State}, health {health.SmartAnalysis.HealthScore}/100." : "Diagnostics are enabled but still learning the server baseline.");
		}
		return new
		{
			installed = installed,
			commandRegistered = commandRegistered,
			statusCommandSucceeded = statusCommandSucceeded,
			version = version,
			name = name,
			optimizationCount = optimizationCount,
			optimizations = optimizations,
			documentedVersion = "1.1.25",
			safeDiagnosticsMode = safeDiagnosticsMode2,
			recommendationState = recommendationState,
			pendingExperiment = optimizationExperiments.Pending,
			experimentHistory = optimizationExperiments.History.OrderByDescending((OptimizationExperiment x) => x.CreatedUnixMilliseconds).Take(10).ToList(),
			note = ((mod == null) ? "Synergy is not installed. Install it separately to enable these controls." : "Controls call Synergy's public /synergy command interface. Configuration changes require a server restart."),
			compatibilityWarning = (flag ? "Synergy 1.1.25 publicly documents validation for Vintage Story 1.22.5-1.22.6. This server targets 1.22.7, so watch Synergy startup logs/circuit-breaker output for skipped patches." : ""),
			statusText = statusResult.Message,
			statusError = (statusResult.Success ? "" : statusResult.Message),
			supports = new
			{
				status = (command != null),
				stats = (command != null && (registered.Count == 0 || registered.Contains("stats"))),
				reload = (command != null && (registered.Count == 0 || registered.Contains("reload"))),
				diagnostics = (command != null && (registered.Count == 0 || registered.Contains("diag"))),
				all = (command != null && (registered.Count == 0 || registered.Contains("all")))
			}
		};
	}

	private Dictionary<string, TungstenOptimizationRecommendation> BuildSynergyRecommendations(ServerHealthSnapshot? health)
	{
		Dictionary<string, int> score = SynergyOptimizations.ToDictionary((SynergyOptimizationInfo x) => x.Code, (SynergyOptimizationInfo _) => 0, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, List<string>> reasons = SynergyOptimizations.ToDictionary((SynergyOptimizationInfo x) => x.Code, (SynergyOptimizationInfo _) => new List<string>(), StringComparer.OrdinalIgnoreCase);
		if (health == null || !health.Enabled || !health.SmartAnalysis.Available)
		{
			return new Dictionary<string, TungstenOptimizationRecommendation>(StringComparer.OrdinalIgnoreCase);
		}
		ServerSmartAnalysis smartAnalysis = health.SmartAnalysis;
		ServerHealthSample current = health.Current;
		ServerDiagnosticSummary diagnosticSummary = health.DiagnosticSummary;
		if (smartAnalysis.RecentEntityMedian >= 10000.0)
		{
			Boost(new string[3] { "activationrange", "pathfindingthrottle", "aithrottle" }, 45, $"Loaded entity median is very high ({smartAnalysis.RecentEntityMedian:N0}).");
			Boost(new string[3] { "collisionfastpath", "repulsethrottle", "pathfindingpool" }, 30, "High entity density increases physics, repulsion, and pathfinding work.");
		}
		else if (smartAnalysis.RecentEntityMedian >= 4000.0)
		{
			Boost(new string[3] { "activationrange", "pathfindingthrottle", "aithrottle" }, 28, $"Loaded entity median is elevated ({smartAnalysis.RecentEntityMedian:N0}).");
			Boost(new string[3] { "collisionfastpath", "repulsethrottle", "pathfindingpool" }, 18, "Entity-heavy workloads can benefit from physics/pathfinding allocation reductions.");
		}
		if (smartAnalysis.RecentCpuMedianPercent >= 80.0)
		{
			Boost(new string[4] { "activationrange", "pathfindingthrottle", "aithrottle", "collisionfastpath" }, 28, $"Recent CPU median is high ({smartAnalysis.RecentCpuMedianPercent:0.0}%).");
		}
		else if (smartAnalysis.RecentCpuMedianPercent >= 60.0)
		{
			Boost(new string[3] { "activationrange", "pathfindingthrottle", "aithrottle" }, 14, $"Recent CPU median is elevated ({smartAnalysis.RecentCpuMedianPercent:0.0}%).");
		}
		if (diagnosticSummary.Gen2Collections >= 2)
		{
			Boost(new string[2] { "pathfindingpool", "blocktickpool" }, 28, $"Recent diagnostics recorded {diagnosticSummary.Gen2Collections} Gen2 collections; allocation-reduction optimizations are more relevant.");
		}
		if (current.Players >= 20)
		{
			Boost(new string[5] { "networkflush", "inventoryscan", "sendfrequency", "attributedelta", "deltaencoding" }, 35, $"There are {current.Players} players online; network/inventory scaling optimizations are more valuable.");
			Boost(new string[2] { "spawnpriority", "hysteresis" }, 15, "Higher concurrency increases entity tracking and login/teleport churn.");
		}
		else if (current.Players >= 8)
		{
			Boost(new string[4] { "networkflush", "sendfrequency", "attributedelta", "deltaencoding" }, 20, $"There are {current.Players} players online; network overhead is becoming more relevant.");
			Boost(new string[1] { "inventoryscan" }, 10, "Inventory dirty scans become more relevant as connected player count grows.");
		}
		if (current.LoadedChunks >= 5000)
		{
			Boost(new string[1] { "blocktickpool" }, 22, $"Loaded chunk count is high ({current.LoadedChunks:N0}); random block-tick allocation pressure may be more significant.");
		}
		if (diagnosticSummary.WorstDelayMs >= 500.0 && current.LoadedEntities >= 2000)
		{
			Boost(new string[4] { "activationrange", "aithrottle", "pathfindingthrottle", "repulsethrottle" }, 18, $"Recent worst scheduler delay reached {diagnosticSummary.WorstDelayMs:0}ms while entity load is substantial.");
		}
		Dictionary<string, TungstenOptimizationRecommendation> dictionary = new Dictionary<string, TungstenOptimizationRecommendation>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, int> item in score)
		{
			if (item.Value > 0)
			{
				int value = item.Value;
				dictionary[item.Key] = new TungstenOptimizationRecommendation
				{
					Code = item.Key,
					Score = value,
					Level = ((value >= 70) ? "High" : ((value >= 45) ? "Medium" : "Low")),
					Reason = string.Join(" ", reasons[item.Key].Take(3))
				};
			}
		}
		return dictionary;
		void Boost(IEnumerable<string> codes, int amount, string reason)
		{
			foreach (string code in codes)
			{
				if (score.ContainsKey(code))
				{
					score[code] = Math.Min(100, score[code] + amount);
					if (!reasons[code].Contains(reason, StringComparer.OrdinalIgnoreCase))
					{
						reasons[code].Add(reason);
					}
				}
			}
		}
	}

	private static bool? ParseSynergyEnabled(string statusText, string code)
	{
		if (string.IsNullOrWhiteSpace(statusText))
		{
			return null;
		}
		string[] array = statusText.Replace("\r", "").Split('\n');
		foreach (string text in array)
		{
			string text2 = text.Trim();
			if (text2.StartsWith(code + ":", StringComparison.OrdinalIgnoreCase))
			{
				string text3 = text2.Substring(code.Length + 1).Trim();
				if (text3.StartsWith("ON", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				if (text3.StartsWith("OFF", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
			}
		}
		return null;
	}

	private OptimizationExperimentPreparation PrepareOptimizationExperiment(string providerId, string code, bool enabled)
	{
		if (optimizationExperiments.Pending != null)
		{
			return new OptimizationExperimentPreparation(Success: false, "A measured optimization experiment is already pending for " + optimizationExperiments.Pending.OptimizationName + ". Finish/evaluate it before starting another so the result can be attributed to one change.", null);
		}
		string text;
		string name;
		if (string.Equals(providerId, "synergy", StringComparison.OrdinalIgnoreCase))
		{
			SynergyOptimizationInfo synergyOptimizationInfo = SynergyOptimizations.FirstOrDefault((SynergyOptimizationInfo x) => x.Code == code);
			if (synergyOptimizationInfo == null)
			{
				return new OptimizationExperimentPreparation(Success: false, "Unknown Synergy optimization.", null);
			}
			providerId = "synergy";
			text = "Synergy";
			name = synergyOptimizationInfo.Name;
		}
		else
		{
			TungstenOptimizationInfo tungstenOptimizationInfo = TungstenOptimizations.FirstOrDefault((TungstenOptimizationInfo x) => x.Code == code);
			if (tungstenOptimizationInfo == null)
			{
				return new OptimizationExperimentPreparation(Success: false, "Unknown Tungsten optimization.", null);
			}
			providerId = "tungsten";
			text = "Tungsten";
			name = tungstenOptimizationInfo.Name;
		}
		OptimizationMetricSnapshot optimizationMetricSnapshot = CaptureOptimizationMetrics(out string error);
		if (optimizationMetricSnapshot == null)
		{
			return new OptimizationExperimentPreparation(Success: false, error, null);
		}
		OptimizationExperiment experiment = new OptimizationExperiment
		{
			Id = Guid.NewGuid().ToString("N"),
			ProviderId = providerId,
			ProviderName = text,
			OptimizationCode = code,
			OptimizationName = name,
			TargetEnabled = enabled,
			CreatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			RuntimeId = runtimeId,
			Status = "AwaitingRestart",
			Baseline = optimizationMetricSnapshot,
			Result = "",
			Confidence = "Pending",
			Reason = "Baseline captured before changing " + text + ". Restart the server because optimizer toggles are applied at startup, then allow diagnostics to collect for at least 60 seconds."
		};
		return new OptimizationExperimentPreparation(Success: true, "Baseline captured.", experiment);
	}

	private OptimizationMetricSnapshot? CaptureOptimizationMetrics(out string error)
	{
		error = "";
		ServerHealthService serverHealth = module.ServerHealth;
		if (serverHealth == null)
		{
			error = "Server diagnostics are unavailable.";
			return null;
		}
		ServerHealthSnapshot snapshot = serverHealth.GetSnapshot(120, 300);
		if (!snapshot.Enabled)
		{
			error = "Enable BlockLogger Profiler diagnostics before starting a measured optimization test. The expensive manual engine profile is not required.";
			return null;
		}
		if (!snapshot.SmartAnalysis.Available || snapshot.Samples.Count < 60)
		{
			error = $"Diagnostics need at least 60 seconds of samples before a measured optimization test. Current graph samples: {snapshot.Samples.Count}.";
			return null;
		}
		return new OptimizationMetricSnapshot
		{
			CapturedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			SampleCount = snapshot.Samples.Count,
			Players = snapshot.Current.Players,
			HealthScore = snapshot.SmartAnalysis.HealthScore,
			SmartState = snapshot.SmartAnalysis.State,
			DelayMedianMs = snapshot.SmartAnalysis.RecentDelayMedianMs,
			CpuMedianPercent = snapshot.SmartAnalysis.RecentCpuMedianPercent,
			HeapMedianMb = snapshot.SmartAnalysis.RecentHeapMedianMb,
			EntityMedian = snapshot.SmartAnalysis.RecentEntityMedian,
			QueueMedian = snapshot.SmartAnalysis.RecentQueueMedian,
			WriterMedianMs = snapshot.SmartAnalysis.RecentWriterMedianMs,
			WorstDelayMs = snapshot.DiagnosticSummary.WorstDelayMs,
			Gen2Collections = snapshot.DiagnosticSummary.Gen2Collections
		};
	}

	private void TryAutoEvaluateOptimizationExperiment()
	{
		OptimizationExperiment pending = optimizationExperiments.Pending;
		if (pending != null && !string.Equals(pending.RuntimeId, runtimeId, StringComparison.Ordinal))
		{
			OptimizationExperimentEvaluationResult optimizationExperimentEvaluationResult = EvaluatePendingOptimizationExperiment(allowSameRuntime: false);
			if (!optimizationExperimentEvaluationResult.Success && optimizationExperiments.Pending != null && optimizationExperiments.Pending.Status != "CollectingAfterRestart")
			{
				optimizationExperiments.Pending.Status = "CollectingAfterRestart";
				optimizationExperiments.Pending.Reason = optimizationExperimentEvaluationResult.Message;
				SaveOptimizationExperiments();
			}
		}
	}

	private OptimizationExperimentEvaluationResult EvaluatePendingOptimizationExperiment(bool allowSameRuntime)
	{
		OptimizationExperiment pending = optimizationExperiments.Pending;
		if (pending == null)
		{
			return new OptimizationExperimentEvaluationResult(Success: false, "No measured optimization experiment is pending.", null);
		}
		bool flag = !string.Equals(pending.RuntimeId, runtimeId, StringComparison.Ordinal);
		if (!flag && !allowSameRuntime)
		{
			pending.Status = "AwaitingRestart";
			pending.Reason = "Restart the server before evaluating. " + (string.IsNullOrWhiteSpace(pending.ProviderName) ? "The optimizer" : pending.ProviderName) + " applies these optimization changes at startup.";
			SaveOptimizationExperiments();
			return new OptimizationExperimentEvaluationResult(Success: false, pending.Reason, pending);
		}
		OptimizationMetricSnapshot optimizationMetricSnapshot = CaptureOptimizationMetrics(out string error);
		if (optimizationMetricSnapshot == null)
		{
			pending.Status = (flag ? "CollectingAfterRestart" : "CollectingAfterChange");
			pending.Reason = error;
			SaveOptimizationExperiments();
			return new OptimizationExperimentEvaluationResult(Success: false, error, pending);
		}
		pending.After = optimizationMetricSnapshot;
		double value = PercentDifference(optimizationMetricSnapshot.Players, pending.Baseline.Players);
		double value2 = PercentDifference(optimizationMetricSnapshot.EntityMedian, pending.Baseline.EntityMedian);
		double num = LowerIsBetterPercent(pending.Baseline.DelayMedianMs, optimizationMetricSnapshot.DelayMedianMs, 5.0);
		double num2 = LowerIsBetterPercent(pending.Baseline.CpuMedianPercent, optimizationMetricSnapshot.CpuMedianPercent, 10.0);
		double num3 = LowerIsBetterPercent(pending.Baseline.WriterMedianMs, optimizationMetricSnapshot.WriterMedianMs, 5.0);
		double num4 = LowerIsBetterPercent(pending.Baseline.QueueMedian, optimizationMetricSnapshot.QueueMedian, 10.0);
		double num5 = LowerIsBetterPercent(pending.Baseline.Gen2Collections, optimizationMetricSnapshot.Gen2Collections, 1.0);
		double num6 = optimizationMetricSnapshot.HealthScore - pending.Baseline.HealthScore;
		double value3 = num * 0.35 + num2 * 0.25 + num3 * 0.15 + num4 * 0.1 + num5 * 0.1 + num6 * 0.05;
		value3 = (pending.ImprovementScore = Math.Clamp(value3, -100.0, 100.0));
		bool flag2 = Math.Abs(value) > 35.0 || Math.Abs(value2) > 35.0;
		bool flag3 = Math.Abs(value) > 20.0 || Math.Abs(value2) > 20.0;
		OptimizationExperiment optimizationExperiment = pending;
		string confidence;
		if (flag2)
		{
			confidence = "Low";
		}
		else
		{
			confidence = (flag3 ? "Medium" : "High");
		}
		optimizationExperiment.Confidence = confidence;
		if (flag2)
		{
			pending.Result = "Inconclusive";
		}
		else if (value3 >= 7.0)
		{
			pending.Result = "Improved";
		}
		else if (value3 <= -7.0)
		{
			pending.Result = "Regressed";
		}
		else
		{
			pending.Result = "No meaningful change";
		}
		pending.Status = "Completed";
		pending.Reason = string.Join(" | ", new string[8]
		{
			"Delay " + SignedPercent(num),
			"CPU " + SignedPercent(num2),
			"Writer " + SignedPercent(num3),
			"Queue " + SignedPercent(num4),
			"Gen2 " + SignedPercent(num5),
			$"Health {num6:+0;-0;0} points",
			$"Player load {value:+0;-0;0}%",
			$"Entity load {value2:+0;-0;0}%"
		});
		optimizationExperiments.History.Insert(0, pending);
		optimizationExperiments.Pending = null;
		SaveOptimizationExperiments();
		return new OptimizationExperimentEvaluationResult(Success: true, $"{pending.OptimizationName}: {pending.Result} ({pending.ImprovementScore:+0.0;-0.0;0.0}% weighted score, {pending.Confidence} confidence).", pending);
	}

	private static double LowerIsBetterPercent(double before, double after, double floor)
	{
		double num = Math.Max(Math.Abs(before), floor);
		return Math.Clamp((before - after) * 100.0 / num, -200.0, 200.0);
	}

	private static double PercentDifference(double current, double baseline)
	{
		double num = Math.Max(Math.Abs(baseline), 1.0);
		return (current - baseline) * 100.0 / num;
	}

	private static string SignedPercent(double value)
	{
		return $"{value:+0.0;-0.0;0.0}%";
	}

	private Task<TungstenCommandResult> ExecuteTungstenCommandAsync(string commandLine, string taskCode)
	{
		return ExecuteExternalOptimizerCommandAsync("tungsten", "Tungsten", commandLine, taskCode);
	}

	private Task<TungstenCommandResult> ExecuteSynergyCommandAsync(string commandLine, string taskCode)
	{
		return ExecuteExternalOptimizerCommandAsync("synergy", "Synergy", commandLine, taskCode);
	}

	private Task<TungstenCommandResult> ExecuteExternalOptimizerCommandAsync(string modId, string displayName, string commandLine, string taskCode)
	{
		TaskCompletionSource<TungstenCommandResult> tcs = new TaskCompletionSource<TungstenCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		api.Event.EnqueueMainThreadTask(() =>
		{
			try
			{
				Mod mod = api.ModLoader.Mods.FirstOrDefault((Mod x) => x?.Info != null && string.Equals(x.Info.ModID, modId, StringComparison.OrdinalIgnoreCase));
				if (mod == null)
				{
					tcs.TrySetResult(new TungstenCommandResult(Success: false, "NotInstalled", displayName + " is not installed.", commandLine));
				}
				else
				{
					Caller caller = new Caller
					{
						Type = EnumCallerType.Console,
						CallerPrivileges = new string[1] { "*" },
						CallerRole = "admin",
						FromChatGroupId = 0
					};
					TextCommandCallingArgs args = new TextCommandCallingArgs
					{
						Caller = caller,
						LanguageCode = "en"
					};
					api.ChatCommands.ExecuteUnparsed(commandLine, args, (TextCommandResult result) =>
					{
						try
						{
							string message = FormatCommandResult(result, "Tungsten command");
							bool success = result != null && (result.Status == EnumCommandStatus.Success || result.Status == EnumCommandStatus.UnknownLegacy);
							tcs.TrySetResult(new TungstenCommandResult(success, result?.Status.ToString() ?? "Unknown", message, commandLine));
						}
						catch (Exception ex2)
						{
							tcs.TrySetResult(new TungstenCommandResult(Success: false, "Error", ex2.Message, commandLine));
						}
					});
				}
			}
			catch (Exception ex)
			{
				tcs.TrySetResult(new TungstenCommandResult(Success: false, "Error", ex.Message, commandLine));
			}
		}, taskCode);
		return AwaitTungstenCommandWithTimeoutAsync(tcs.Task, commandLine);
	}

	private static async Task<TungstenCommandResult> AwaitTungstenCommandWithTimeoutAsync(Task<TungstenCommandResult> commandTask, string commandLine)
	{
		if (await Task.WhenAny(commandTask, Task.Delay(TimeSpan.FromSeconds(8L))).ConfigureAwait(continueOnCapturedContext: false) == commandTask)
		{
			return await commandTask.ConfigureAwait(continueOnCapturedContext: false);
		}
		return new TungstenCommandResult(Success: false, "Timeout", "Tungsten command did not complete within 8 seconds. Check the server log; the command may still have executed.", commandLine);
	}

	private static string FormatCommandResult(TextCommandResult? result, string commandLabel = "Command")
	{
		if (result == null)
		{
			return "No command result was returned.";
		}
		string text = result.StatusMessage ?? "";
		if (result.MessageParams != null && result.MessageParams.Length != 0)
		{
			try
			{
				text = string.Format(CultureInfo.InvariantCulture, text, result.MessageParams);
			}
			catch
			{
			}
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			text = ((result.Status == EnumCommandStatus.Success) ? (commandLabel + " completed successfully. Some detailed output may be written to the server log.") : $"{commandLabel} returned {result.Status}.");
		}
		return text;
	}

	private void CompleteWebRequest(WebRequestMetricContext metric, bool failed)
	{
		double num = Math.Max(0.0, (double)(Stopwatch.GetTimestamp() - metric.StartedTimestamp) / (double)Stopwatch.Frequency * 1000.0);
		int num2;
		long responseBytes;
		double queueWaitMilliseconds;
		double mainThreadWaitMilliseconds;
		double mainThreadWorkMilliseconds;
		lock (metric.Sync)
		{
			num2 = metric.StatusCode;
			responseBytes = metric.ResponseBytes;
			queueWaitMilliseconds = metric.QueueWaitMilliseconds;
			mainThreadWaitMilliseconds = metric.MainThreadWaitMilliseconds;
			mainThreadWorkMilliseconds = metric.MainThreadWorkMilliseconds;
		}
		if (num2 <= 0)
		{
			num2 = (failed ? 500 : 200);
		}
		SuiteTimingRegistry.Record("Web Dashboard request", num);
		if (queueWaitMilliseconds > 0.0)
		{
			SuiteTimingRegistry.Record("Web Dashboard worker queue wait", queueWaitMilliseconds);
		}
		if (mainThreadWaitMilliseconds > 0.0)
		{
			SuiteTimingRegistry.Record("Web Dashboard main-thread wait", mainThreadWaitMilliseconds);
		}
		if (mainThreadWorkMilliseconds > 0.0)
		{
			SuiteTimingRegistry.Record("Web Dashboard main-thread work", mainThreadWorkMilliseconds);
		}
		WebRequestDiagnostic item = new WebRequestDiagnostic
		{
			UnixMilliseconds = metric.UnixMilliseconds,
			Method = metric.Method,
			Path = metric.Path,
			StatusCode = num2,
			ResponseBytes = responseBytes,
			TotalMilliseconds = num,
			QueueWaitMilliseconds = queueWaitMilliseconds,
			MainThreadWaitMilliseconds = mainThreadWaitMilliseconds,
			MainThreadWorkMilliseconds = mainThreadWorkMilliseconds,
			Note = (failed ? "Request handler threw before completing normally." : ((queueWaitMilliseconds >= 100.0) ? "Request spent notable time waiting for an available web worker." : ((mainThreadWaitMilliseconds >= 100.0) ? "Most request delay may be waiting for the Vintage Story main thread." : ((mainThreadWorkMilliseconds >= 50.0) ? "Request performed notable work on the Vintage Story main thread." : ""))))
		};
		lock (webMetricsLock)
		{
			webTotalRequests++;
			webTotalResponseBytes += responseBytes;
			if ((num2 >= 500) | failed)
			{
				webErrorResponses++;
			}
			if (num2 == 401 || num2 == 403)
			{
				webUnauthorizedResponses++;
			}
			if (num2 == 429)
			{
				webRateLimitedResponses++;
			}
			string text = metric.Path;
			string key = metric.Method + " " + text;
			if (!webEndpointMetrics.TryGetValue(key, out WebEndpointMetricsState value))
			{
				if (webEndpointMetrics.Count >= 128)
				{
					text = "<other>";
					key = metric.Method + " " + text;
				}
				if (!webEndpointMetrics.TryGetValue(key, out value))
				{
					value = new WebEndpointMetricsState
					{
						Method = metric.Method,
						Path = text
					};
					webEndpointMetrics[key] = value;
				}
			}
			value.Requests++;
			value.TotalMilliseconds += num;
			value.MaxMilliseconds = Math.Max(value.MaxMilliseconds, num);
			value.ResponseBytes += responseBytes;
			value.QueueWaitTotalMilliseconds += queueWaitMilliseconds;
			value.MainThreadWaitTotalMilliseconds += mainThreadWaitMilliseconds;
			value.MainThreadWorkTotalMilliseconds += mainThreadWorkMilliseconds;
			if ((num2 >= 400) | failed)
			{
				value.Errors++;
			}
			value.RecentDurations.Enqueue(num);
			while (value.RecentDurations.Count > 120)
			{
				value.RecentDurations.Dequeue();
			}
			recentWebRequests.Enqueue(item);
			long num3 = DateTimeOffset.UtcNow.AddMinutes(-10.0).ToUnixTimeMilliseconds();
			while (recentWebRequests.Count > 0 && recentWebRequests.Peek().UnixMilliseconds < num3)
			{
				recentWebRequests.Dequeue();
			}
			while (recentWebRequests.Count > 500)
			{
				recentWebRequests.Dequeue();
			}
		}
	}

	private WebServerDiagnosticsSnapshot BuildWebDiagnosticsSnapshot()
	{
		lock (webMetricsLock)
		{
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			List<double> rows = (from x in webEndpointMetrics.Values.SelectMany((WebEndpointMetricsState x) => x.RecentDurations)
				orderby x
				select x).ToList();
			List<double> list = (from x in recentWebRequests
				select x.QueueWaitMilliseconds into x
				where x > 0.0
				orderby x
				select x).ToList();
			long num = Math.Max(0L, webTotalRequests);
			double num2 = webEndpointMetrics.Values.Sum((WebEndpointMetricsState x) => x.TotalMilliseconds);
			double num3 = webEndpointMetrics.Values.Sum((WebEndpointMetricsState x) => x.MainThreadWaitTotalMilliseconds);
			double num4 = webEndpointMetrics.Values.Sum((WebEndpointMetricsState x) => x.MainThreadWorkTotalMilliseconds);
			return new WebServerDiagnosticsSnapshot
			{
				CapturedUnixMilliseconds = now,
				ActiveRequests = Math.Max(0, Volatile.Read(in webActiveRequests)),
				ConnectionCapacity = config.WebWorkerCount + config.WebRequestQueueCapacity,
				WorkerCount = config.WebWorkerCount,
				ActiveWorkers = Math.Max(0, Volatile.Read(in webActiveWorkers)),
				QueueDepth = Math.Max(0, Volatile.Read(in webQueuedRequests)),
				QueueCapacity = config.WebRequestQueueCapacity,
				PeakQueueDepth = Math.Max(0, Volatile.Read(in webPeakQueuedRequests)),
				RejectedBusyRequests = Math.Max(0L, Interlocked.Read(in webRejectedBusyRequests)),
				AverageQueueWaitMilliseconds = ((list.Count == 0) ? 0.0 : list.Average()),
				P95QueueWaitMilliseconds = Percentile(list),
				SnapshotCacheHits = Math.Max(0L, Interlocked.Read(in webSnapshotCacheHits)),
				SnapshotCacheMisses = Math.Max(0L, Interlocked.Read(in webSnapshotCacheMisses)),
				SnapshotRefreshes = Math.Max(0L, Interlocked.Read(in webSnapshotRefreshes)),
				TotalRequests = num,
				ErrorResponses = webErrorResponses,
				UnauthorizedResponses = webUnauthorizedResponses,
				RateLimitedResponses = webRateLimitedResponses,
				TotalResponseBytes = webTotalResponseBytes,
				RequestsLastMinute = recentWebRequests.Count((WebRequestDiagnostic x) => x.UnixMilliseconds >= now - 60000),
				AverageRequestMilliseconds = ((num == 0L) ? 0.0 : (num2 / (double)num)),
				P95RequestMilliseconds = Percentile(rows),
				MaxRequestMilliseconds = ((webEndpointMetrics.Values.Count == 0) ? 0.0 : webEndpointMetrics.Values.Max((WebEndpointMetricsState x) => x.MaxMilliseconds)),
				AverageMainThreadWaitMilliseconds = ((num == 0L) ? 0.0 : (num3 / (double)num)),
				AverageMainThreadWorkMilliseconds = ((num == 0L) ? 0.0 : (num4 / (double)num)),
				Endpoints = (from x in webEndpointMetrics.Values.Select((WebEndpointMetricsState x) =>
					{
						List<double> rows2 = x.RecentDurations.OrderBy((double v) => v).ToList();
						return new WebEndpointDiagnostic
						{
							Method = x.Method,
							Path = x.Path,
							Requests = x.Requests,
							Errors = x.Errors,
							ResponseBytes = x.ResponseBytes,
							AverageMilliseconds = ((x.Requests == 0L) ? 0.0 : (x.TotalMilliseconds / (double)x.Requests)),
							P95Milliseconds = Percentile(rows2),
							MaxMilliseconds = x.MaxMilliseconds,
							AverageMainThreadWaitMilliseconds = ((x.Requests == 0L) ? 0.0 : (x.MainThreadWaitTotalMilliseconds / (double)x.Requests)),
							AverageMainThreadWorkMilliseconds = ((x.Requests == 0L) ? 0.0 : (x.MainThreadWorkTotalMilliseconds / (double)x.Requests)),
							AverageQueueWaitMilliseconds = ((x.Requests == 0L) ? 0.0 : (x.QueueWaitTotalMilliseconds / (double)x.Requests))
						};
					})
					orderby x.P95Milliseconds descending, x.Requests descending
					select x).Take(30).ToList(),
				RecentSlowRequests = (from x in recentWebRequests
					where x.TotalMilliseconds >= 100.0 || x.StatusCode >= 400
					orderby x.UnixMilliseconds descending
					select x).Take(30).ToList()
			};
		}
		static double Percentile(List<double> list2)
		{
			return (list2.Count == 0) ? 0.0 : list2[Math.Clamp((int)Math.Ceiling((double)list2.Count * 0.95) - 1, 0, list2.Count - 1)];
		}
	}

	private List<IncidentCauseScore> BuildIncidentCauseScores(ServerLagIncidentGroup group, IReadOnlyList<ServerHealthSample> during, WebServerDiagnosticsSnapshot web, DatabaseHealthSnapshot database)
	{
		List<IncidentCauseScore> list = new List<IncidentCauseScore>();
		if (!string.IsNullOrWhiteSpace(group.LikelyModId))
		{
			int score = (string.Equals(group.ModConfidence, "High", StringComparison.OrdinalIgnoreCase) ? 92 : 58);
			list.Add(new IncidentCauseScore
			{
				Category = "Profiler attribution: " + group.LikelyModId,
				Score = score,
				Confidence = group.ModConfidence,
				Evidence = (string.IsNullOrWhiteSpace(group.ModEvidence) ? ("A profile overlapping this incident attributed significant tick work to " + group.LikelyModId + ".") : group.ModEvidence)
			});
		}
		int num = 5;
		if (group.BlockLoggerDropped > 0)
		{
			num = 95;
		}
		else if (group.MaxBlockLoggerQueue >= 1000 || group.MaxBlockLoggerWriterMs >= 250 || database.P95QueryMilliseconds >= 500.0)
		{
			num = 75;
		}
		else if (group.MaxBlockLoggerQueue >= 250 || group.MaxBlockLoggerWriterMs >= 100 || database.P95QueryMilliseconds >= 200.0)
		{
			num = 45;
		}
		else if (group.MaxBlockLoggerQueue >= 50 || group.MaxBlockLoggerWriterMs >= 40 || database.P95QueryMilliseconds >= 100.0)
		{
			num = 20;
		}
		list.Add(new IncidentCauseScore
		{
			Category = "BlockLogger storage",
			Score = num,
			Confidence = ((during.Count >= 3) ? "High" : "Medium"),
			Evidence = ((num <= 10) ? $"Queue stayed at or below {group.MaxBlockLoggerQueue:N0}; writer max {group.MaxBlockLoggerWriterMs:N0}ms; query P95 {database.P95QueryMilliseconds:0.0}ms; no event drops. BlockLogger storage is unlikely to be the cause." : $"Queue max {group.MaxBlockLoggerQueue:N0}; writer max {group.MaxBlockLoggerWriterMs:N0}ms; query P95 {database.P95QueryMilliseconds:0.0}ms; dropped {group.BlockLoggerDropped:N0}.")
		});
		List<WebRequestDiagnostic> list2 = web.RecentSlowRequests.Where((WebRequestDiagnostic x) => x.UnixMilliseconds >= group.StartUnixMilliseconds - 15000 && x.UnixMilliseconds <= group.EndUnixMilliseconds + 15000).ToList();
		double num2 = ((list2.Count == 0) ? 0.0 : list2.Max((WebRequestDiagnostic x) => x.MainThreadWorkMilliseconds));
		double value = ((list2.Count == 0) ? 0.0 : list2.Max((WebRequestDiagnostic x) => x.MainThreadWaitMilliseconds));
		double num3 = ((list2.Count == 0) ? 0.0 : list2.Max((WebRequestDiagnostic x) => x.QueueWaitMilliseconds));
		int score2;
		if (num2 >= 100.0)
		{
			score2 = 80;
		}
		else if (num2 >= 50.0)
		{
			score2 = 55;
		}
		else if (num2 >= 20.0)
		{
			score2 = 30;
		}
		else if (num3 >= 500.0)
		{
			score2 = 25;
		}
		else
		{
			score2 = (list2.Any((WebRequestDiagnostic x) => x.TotalMilliseconds >= 500.0) ? 20 : 5);
		}
		list.Add(new IncidentCauseScore
		{
			Category = "Web dashboard",
			Score = score2,
			Confidence = ((list2.Count > 0) ? "Medium" : "Low"),
			Evidence = ((list2.Count == 0) ? "No slow/error web request was recorded near this incident." : $"{list2.Count} slow/error web request(s) nearby; max worker-queue wait {num3:0.0}ms, max main-thread work {num2:0.0}ms, max main-thread wait {value:0.0}ms.")
		});
		int score3 = ((group.Gen2Collections > 0) ? Math.Min(90, 45 + group.Gen2Collections * 15) : 5);
		list.Add(new IncidentCauseScore
		{
			Category = "Garbage collection",
			Score = score3,
			Confidence = ((during.Count >= 2) ? "High" : "Medium"),
			Evidence = ((group.Gen2Collections > 0) ? $"Gen2 collections increased by {group.Gen2Collections} during the grouped incident." : "No Gen2 collection was correlated with this incident.")
		});
		int num4;
		if (Math.Abs(group.MaxEntityDelta) >= 500)
		{
			num4 = 75;
		}
		else if (Math.Abs(group.MaxEntityDelta) >= 200)
		{
			num4 = 45;
		}
		else
		{
			num4 = ((Math.Abs(group.MaxEntityDelta) >= 75) ? 20 : 5);
		}
		list.Add(new IncidentCauseScore
		{
			Category = "Entity population",
			Score = num4,
			Confidence = "Medium",
			Evidence = ((num4 <= 10) ? $"Largest sampled entity-count change was {group.MaxEntityDelta:+#;-#;0}." : $"Largest sampled entity-count change was {group.MaxEntityDelta:+#;-#;0}; inspect entity hotspots if this repeats.")
		});
		int score4;
		if (Math.Abs(group.MaxChunkDelta) >= 300)
		{
			score4 = 70;
		}
		else if (Math.Abs(group.MaxChunkDelta) >= 100)
		{
			score4 = 40;
		}
		else
		{
			score4 = ((Math.Abs(group.MaxChunkDelta) >= 30) ? 20 : 5);
		}
		list.Add(new IncidentCauseScore
		{
			Category = "Chunk load/unload",
			Score = score4,
			Confidence = "Low",
			Evidence = $"Largest sampled loaded-chunk change was {group.MaxChunkDelta:+#;-#;0}; chunk count is intentionally refreshed only about every 30 seconds."
		});
		return list.OrderByDescending((IncidentCauseScore x) => x.Score).ToList();
	}

	private static string QuoteCommandArgument(string value)
	{
		if (value == null)
		{
			value = "";
		}
		return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
	}

	private async Task<WebConsoleCommandResult> ExecuteWebConsoleCommandAsync(string command)
	{
		TaskCompletionSource<WebConsoleCommandResult> tcs = new TaskCompletionSource<WebConsoleCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		api.Event.EnqueueMainThreadTask(() =>
		{
			try
			{
				Caller caller = new Caller
				{
					Type = EnumCallerType.Console,
					CallerPrivileges = new string[1] { "*" },
					CallerRole = "admin",
					FromChatGroupId = 0
				};
				TextCommandCallingArgs args = new TextCommandCallingArgs
				{
					Caller = caller,
					LanguageCode = "en"
				};
				api.ChatCommands.ExecuteUnparsed(command, args, (TextCommandResult result) =>
				{
					try
					{
						bool success = result != null && (result.Status == EnumCommandStatus.Success || result.Status == EnumCommandStatus.UnknownLegacy);
						tcs.TrySetResult(new WebConsoleCommandResult(success, result?.Status.ToString() ?? "Unknown", FormatCommandResult(result)));
					}
					catch (Exception ex2)
					{
						tcs.TrySetResult(new WebConsoleCommandResult(Success: false, "Error", ex2.Message));
					}
				});
			}
			catch (Exception ex)
			{
				tcs.TrySetResult(new WebConsoleCommandResult(Success: false, "Error", ex.Message));
			}
		}, "web-console-command");
		if (await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(8L))).ConfigureAwait(continueOnCapturedContext: false) == tcs.Task)
		{
			return await tcs.Task.ConfigureAwait(continueOnCapturedContext: false);
		}
		return new WebConsoleCommandResult(Success: false, "Timeout", "Command did not return within 8 seconds. Check the console output; it may still have executed.");
	}

	private async Task<WebConsoleCommandResult> ExecuteWebConsoleMessageAsync(string message, string actor)
	{
		try
		{
			await RunOnMainThreadAsync(() =>
			{
				api.BroadcastMessageToAllGroups(message, EnumChatType.Notification);
				api.Logger.Notification("[WebConsole] {0} broadcast a server message ({1} chars).", actor, message.Length);
				return true;
			}, "web-console-message").ConfigureAwait(continueOnCapturedContext: false);
			return new WebConsoleCommandResult(Success: true, "Success", "Server message broadcast to online players.");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return new WebConsoleCommandResult(Success: false, "Error", ex2.Message);
		}
	}

	private async Task<T> RunOnMainThreadAsync<T>(Func<T> action, string taskCode, bool skipIfTimedOut = false)
	{
		TaskCompletionSource<T> tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		WebRequestMetricContext requestMetric = requestMetricContext.Value;
		skipIfTimedOut |= requestMetric?.Method == "GET";
		RouteOperationLease workLease = requestMetric?.RouteLease;
		workLease?.Retain();
		long queuedTimestamp = Stopwatch.GetTimestamp();
		int readState = 0;
		int responseTimedOut = 0;
		try
		{
			api.Event.EnqueueMainThreadTask(() =>
			{
				if (skipIfTimedOut && Interlocked.CompareExchange(ref readState, 1, 0) != 0)
				{
					tcs.TrySetCanceled();
					workLease?.Dispose();
					return;
				}
				long timestamp = Stopwatch.GetTimestamp();
				if (requestMetric != null)
				{
					lock (requestMetric.Sync)
					{
						requestMetric.MainThreadWaitMilliseconds += (double)(timestamp - queuedTimestamp) / (double)Stopwatch.Frequency * 1000.0;
					}
				}
				Stopwatch stopwatch = Stopwatch.StartNew();
				try
				{
					if (disposed)
					{
						throw new ObjectDisposedException("WebDashboardService");
					}
					T val = action();
					if (Volatile.Read(in responseTimedOut) != 0 && requestMetric != null && !(requestMetric.Method == "GET") && !object.Equals(val?.GetType().GetProperty("Success", BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public)?.GetValue(val), false))
					{
						snapshotCache.Invalidate((string key) => CacheAffectedByMutation(key, requestMetric.Path));
					}
					tcs.TrySetResult(val);
				}
				catch (Exception exception)
				{
					tcs.TrySetException(exception);
				}
				finally
				{
					stopwatch.Stop();
					workLease?.Dispose();
					if (requestMetric != null)
					{
						lock (requestMetric.Sync)
						{
							requestMetric.MainThreadWorkMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
						}
					}
				}
			}, taskCode);
		}
		catch
		{
			workLease?.Dispose();
			throw;
		}
		if (await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(8L))).ConfigureAwait(continueOnCapturedContext: false) != tcs.Task)
		{
			Volatile.Write(ref responseTimedOut, 1);
			if (skipIfTimedOut)
			{
				Interlocked.CompareExchange(ref readState, 2, 0);
				tcs.TrySetCanceled();
			}
			throw new TimeoutException("Main-thread task '" + taskCode + "' did not complete within 8 seconds.");
		}
		return await tcs.Task.ConfigureAwait(continueOnCapturedContext: false);
	}

	private static async Task<T> AwaitWithTimeout<T>(Task<T> task, TimeSpan timeout, string operation)
	{
		if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(continueOnCapturedContext: false) != task)
		{
			throw new TimeoutException($"{operation} did not complete within {timeout.TotalSeconds:0} seconds.");
		}
		return await task.ConfigureAwait(continueOnCapturedContext: false);
	}

	private object InspectBadBlockEntity(string key)
	{
		if (logIntelligence == null)
		{
			return new
			{
				success = false,
				error = "Log Intelligence / bad block-entity tracking is unavailable."
			};
		}
		if (!logIntelligence.TryGetBadBlockEntity(key, out BadBlockEntityRecord record))
		{
			return new
			{
				success = false,
				error = "Tracked block entity was not found."
			};
		}
		BlockPos blockPos = new BlockPos(record.X, record.Y, record.Z, record.Dimension);
		if (!api.World.BlockAccessor.IsValidPos(blockPos))
		{
			return new
			{
				success = false,
				error = "The tracked coordinate is outside the valid world range.",
				record = record
			};
		}
		try
		{
			Block block = api.World.BlockAccessor.GetBlock(blockPos);
			BlockEntity blockEntity = api.World.BlockAccessor.GetBlockEntity(blockPos);
			string text = block?.Code?.ToString() ?? "game:air";
			string text2 = blockEntity?.GetType().FullName ?? "";
			string a = blockEntity?.GetType().Name ?? "";
			bool blockMatchesLogged = string.Equals(text, record.BlockCode, StringComparison.OrdinalIgnoreCase);
			bool blockEntityMatchesLogged = blockEntity != null && (string.Equals(a, record.BlockEntityType, StringComparison.Ordinal) || string.Equals(text2, record.BlockEntityType, StringComparison.Ordinal));
			return new
			{
				success = true,
				record = record,
				live = new
				{
					validPosition = true,
					currentBlockCode = text,
					blockId = (block?.Id ?? 0),
					blockClass = (block?.GetType().FullName ?? ""),
					blockEntityPresent = (blockEntity != null),
					blockEntityClass = text2,
					blockMatchesLogged = blockMatchesLogged,
					blockEntityMatchesLogged = blockEntityMatchesLogged
				}
			};
		}
		catch (Exception ex)
		{
			return new
			{
				success = false,
				error = "Live block inspection failed safely: " + ex.GetType().Name + ": " + ex.Message,
				record = record
			};
		}
	}

	private BadBlockEntityActionResult ExecuteBadBlockEntityAction(string key, string action, string value, string actor)
	{
		BadBlockEntityActionResult badBlockEntityActionResult = new BadBlockEntityActionResult();
		if (logIntelligence == null)
		{
			badBlockEntityActionResult.Message = "Log Intelligence / bad block-entity tracking is unavailable.";
			return badBlockEntityActionResult;
		}
		action = (action ?? "").Trim().ToLowerInvariant();
		if (action == "clearall")
		{
			int num = logIntelligence.ClearAllBadBlockEntities(actor, out string message);
			badBlockEntityActionResult.Success = true;
			badBlockEntityActionResult.Message = message;
			badBlockEntityActionResult.Detail = new
			{
				removed = num,
				trackerOnly = true
			};
			badBlockEntityActionResult.Snapshot = logIntelligence.GetBadBlockEntitySnapshot();
			api.Logger.Notification($"[NewfiesServerEssentials] Bad Block Entity Tracker cleared by {actor}: {num} tracked record(s) removed; world data unchanged.");
			return badBlockEntityActionResult;
		}
		if (!logIntelligence.TryGetBadBlockEntity(key, out BadBlockEntityRecord record))
		{
			badBlockEntityActionResult.Message = "Tracked block entity was not found.";
			badBlockEntityActionResult.Snapshot = logIntelligence.GetBadBlockEntitySnapshot();
			return badBlockEntityActionResult;
		}
		BlockPos blockPos = new BlockPos(record.X, record.Y, record.Z, record.Dimension);
		try
		{
			switch (action)
			{
			case "stop":
			case "resume":
			{
				bool tracking = action == "resume";
				badBlockEntityActionResult.Success = logIntelligence.SetBadBlockEntityTracking(key, tracking, actor, out string message2);
				badBlockEntityActionResult.Message = message2;
				break;
			}
			case "teleport":
			{
				if (string.IsNullOrWhiteSpace(value))
				{
					badBlockEntityActionResult.Message = "Enter an online player name or UID to teleport.";
					break;
				}
				IServerPlayer serverPlayer = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => string.Equals(p.PlayerName, value, StringComparison.OrdinalIgnoreCase) || string.Equals(p.PlayerUID, value, StringComparison.OrdinalIgnoreCase));
				if (serverPlayer == null || serverPlayer.Entity == null)
				{
					badBlockEntityActionResult.Message = "Online player '" + value + "' was not found.";
					break;
				}
				BlockPos position = new BlockPos(record.X, record.Y + 1, record.Z, record.Dimension);
				PacketGuardTrustedState.Mark(serverPlayer, "dashboard-teleport");
				serverPlayer.Entity.TeleportTo(position);
				badBlockEntityActionResult.Success = true;
				badBlockEntityActionResult.Message = $"Teleported {serverPlayer.PlayerName} above {record.X}, {record.Y}, {record.Z}.";
				logIntelligence.RecordBadBlockEntityAction(key, actor, "Teleport", serverPlayer.PlayerName);
				break;
			}
			case "remove":
			{
				if (!api.World.BlockAccessor.IsValidPos(blockPos))
				{
					badBlockEntityActionResult.Message = "The tracked coordinate is outside the valid world range.";
					break;
				}
				string text3 = api.World.BlockAccessor.GetBlock(blockPos)?.Code?.ToString() ?? "game:air";
				if (!string.Equals(text3, record.BlockCode, StringComparison.OrdinalIgnoreCase))
				{
					badBlockEntityActionResult.Message = $"Safety check stopped removal because the live block is now '{text3}', not '{record.BlockCode}'. Refresh/inspect before acting.";
					break;
				}
				BlockEntity blockEntity = api.World.BlockAccessor.GetBlockEntity(blockPos);
				if (blockEntity == null)
				{
					badBlockEntityActionResult.Message = "No block entity currently exists at that position.";
					break;
				}
				string text4 = blockEntity.GetType().FullName ?? blockEntity.GetType().Name;
				api.World.BlockAccessor.RemoveBlockEntity(blockPos);
				badBlockEntityActionResult.Success = true;
				badBlockEntityActionResult.Message = "Removed block entity " + text4 + ". The block itself remains in place.";
				logIntelligence.RecordBadBlockEntityAction(key, actor, "Remove block entity", text4);
				api.Logger.Warning("[BadBlockEntityTracker] {0} removed block entity {1} at {2},{3},{4} (dim {5}); block {6} remains.", actor, text4, record.X, record.Y, record.Z, record.Dimension, record.BlockCode);
				break;
			}
			case "replace":
			{
				if (!api.World.BlockAccessor.IsValidPos(blockPos))
				{
					badBlockEntityActionResult.Message = "The tracked coordinate is outside the valid world range.";
					break;
				}
				string text = (value ?? "").Trim();
				if (string.IsNullOrWhiteSpace(text))
				{
					badBlockEntityActionResult.Message = "Replacement block code is required.";
					break;
				}
				if (!text.Contains(':'))
				{
					text = "game:" + text;
				}
				string text2 = api.World.BlockAccessor.GetBlock(blockPos)?.Code?.ToString() ?? "game:air";
				if (!string.Equals(text2, record.BlockCode, StringComparison.OrdinalIgnoreCase))
				{
					badBlockEntityActionResult.Message = $"Safety check stopped replacement because the live block is now '{text2}', not '{record.BlockCode}'. Refresh/inspect before acting.";
					break;
				}
				Block block = api.World.GetBlock(new AssetLocation(text));
				if (block == null)
				{
					badBlockEntityActionResult.Message = "Block '" + text + "' could not be resolved.";
					break;
				}
				api.World.BlockAccessor.SetBlock(0, blockPos);
				if (block.Id != 0)
				{
					api.World.BlockAccessor.SetBlock(block.Id, blockPos);
				}
				api.World.BlockAccessor.MarkBlockModified(blockPos);
				badBlockEntityActionResult.Success = true;
				badBlockEntityActionResult.Message = $"Replaced {text2} with {block.Code}. Existing block-entity state was discarded and no drops were created.";
				logIntelligence.RecordBadBlockEntityAction(key, actor, "Replace block", block.Code?.ToString() ?? text);
				api.Logger.Warning("[BadBlockEntityTracker] {0} replaced block {1} with {2} at {3},{4},{5} (dim {6}); previous block-entity state was discarded.", actor, text2, block.Code, record.X, record.Y, record.Z, record.Dimension);
				break;
			}
			default:
				badBlockEntityActionResult.Message = "Unknown bad block-entity action.";
				break;
			}
		}
		catch (Exception ex)
		{
			badBlockEntityActionResult.Success = false;
			badBlockEntityActionResult.Message = "Action failed safely: " + ex.GetType().Name + ": " + ex.Message;
		}
		badBlockEntityActionResult.Snapshot = logIntelligence.GetBadBlockEntitySnapshot();
		return badBlockEntityActionResult;
	}

	private Task<WebRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		return WebHttpRequestParser.ReadAsync(stream, cancellationToken);
	}

	private static string GetQuery(WebRequest request, string key, string fallback)
	{
		string value;
		return request.Query.TryGetValue(key, out value) ? value : fallback;
	}

	private static int GetQueryInt(WebRequest request, string key, int fallback)
	{
		int result;
		return int.TryParse(GetQuery(request, key, ""), out result) ? result : fallback;
	}

	private static long GetQueryLong(WebRequest request, string key, long fallback)
	{
		long result;
		return long.TryParse(GetQuery(request, key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
	}

	private static object BuildChatGuardWebPayload(ChatGuardControlSnapshot snapshot)
	{
		JsonElement jsonElement = ParseJsonElement(snapshot.ConfigJson, "{}");
		JsonElement recent = ParseJsonElement(snapshot.RecentJson, "[]");
		JsonElement mutes = ParseJsonElement(snapshot.MutesJson, "[]");
		JsonElement warnings = ParseJsonElement(snapshot.WarningsJson, "[]");
		JsonElement ruleStats = ParseJsonElement(snapshot.RuleStatsJson, "[]");
		JsonElement onlinePlayers = ParseJsonElement(snapshot.OnlinePlayersJson, "[]");
		JsonElement test = ParseJsonElement(snapshot.TestJson, "{}");
		return new
		{
			available = snapshot.Available,
			success = snapshot.Success,
			message = snapshot.Message,
			version = snapshot.Version,
			enabled = snapshot.Enabled,
			mode = snapshot.Mode,
			suiteIntegrated = snapshot.SuiteIntegrated,
			stateStorageReady = snapshot.StateStorageReady,
			stateStorageError = snapshot.StateStorageError,
			auditDroppedEntries = snapshot.AuditDroppedEntries,
			auditStorageError = snapshot.AuditStorageError,
			retainedSamplePlayers = snapshot.RetainedSamplePlayers,
			scannedMessages = snapshot.ScannedMessages,
			matchedRules = snapshot.MatchedRules,
			blockedMessages = snapshot.BlockedMessages,
			warningsIssued = snapshot.WarningsIssued,
			mutesIssued = snapshot.MutesIssued,
			kicksIssued = snapshot.KicksIssued,
			activeMutes = snapshot.ActiveMutes,
			config = jsonElement,
			recent = recent,
			mutes = mutes,
			warnings = warnings,
			ruleStats = ruleStats,
			onlinePlayers = onlinePlayers,
			test = test
		};
	}

	private static JsonElement ParseJsonElement(string? raw, string fallback)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? fallback : raw);
			return jsonDocument.RootElement.Clone();
		}
		catch
		{
			using JsonDocument jsonDocument2 = JsonDocument.Parse(fallback);
			return jsonDocument2.RootElement.Clone();
		}
	}

	private static string GetJsonString(string json, string name, string fallback)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
			JsonElement value;
			return (jsonDocument.RootElement.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String) ? (value.GetString() ?? fallback) : fallback;
		}
		catch
		{
			return fallback;
		}
	}

	private static Dictionary<string, string> GetJsonStringDictionary(string json, string name)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
			if (!jsonDocument.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
			{
				return dictionary;
			}
			foreach (JsonProperty item in value.EnumerateObject())
			{
				if (item.Value.ValueKind == JsonValueKind.String)
				{
					dictionary[item.Name] = item.Value.GetString() ?? string.Empty;
					if (dictionary.Count >= 64)
					{
						break;
					}
				}
			}
		}
		catch
		{
		}
		return dictionary;
	}

	private static int GetJsonInt(string json, string name, int fallback)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
			JsonElement value;
			int value2;
			return (jsonDocument.RootElement.TryGetProperty(name, out value) && value.TryGetInt32(out value2)) ? value2 : fallback;
		}
		catch
		{
			return fallback;
		}
	}

	private static double GetJsonDouble(string json, string name, double fallback)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
			JsonElement value;
			double value2;
			return (jsonDocument.RootElement.TryGetProperty(name, out value) && value.TryGetDouble(out value2)) ? value2 : fallback;
		}
		catch
		{
			return fallback;
		}
	}

	private static long GetJsonLong(string json, string name, long fallback)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
			JsonElement value;
			long value2;
			return (jsonDocument.RootElement.TryGetProperty(name, out value) && value.TryGetInt64(out value2)) ? value2 : fallback;
		}
		catch
		{
			return fallback;
		}
	}

	private static bool GetJsonBool(string json, string name, bool fallback)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
			if (!jsonDocument.RootElement.TryGetProperty(name, out var value))
			{
				return fallback;
			}
			JsonValueKind valueKind = value.ValueKind;
			if (1 == 0)
			{
			}
			bool result = valueKind switch
			{
				JsonValueKind.True => true, 
				JsonValueKind.False => false, 
				_ => fallback, 
			};
			if (1 == 0)
			{
			}
			return result;
		}
		catch
		{
			return fallback;
		}
	}

	private static bool TryGetCookie(WebRequest request, string name, out string value)
	{
		value = "";
		if (!request.Headers.TryGetValue("Cookie", out string value2))
		{
			return false;
		}
		string[] array = value2.Split(';', StringSplitOptions.RemoveEmptyEntries);
		foreach (string text in array)
		{
			int num = text.IndexOf('=');
			if (num > 0)
			{
				string text2 = text.Substring(0, num).Trim();
				if (text2.Equals(name, StringComparison.Ordinal))
				{
					value = text.Substring(num + 1).Trim();
					return true;
				}
			}
		}
		return false;
	}

	private byte[] BuildDiagnosticBundleBytes(object core, object blockActivity, IReadOnlyDictionary<string, object> extras)
	{
		using MemoryStream memoryStream = new MemoryStream();
		ZipArchive zip = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true);
		try
		{
			AddJson("performance-core.json", core);
			AddJson("block-activity.json", blockActivity);
			AddJson("optimization-experiments.json", optimizationExperiments);
			foreach (KeyValuePair<string, object> extra in extras)
			{
				AddJson(extra.Key, extra.Value);
			}
			AddJson("hardening-summary.json", new
			{
				buildId = "newfies-server-essentials-suite-1.2.44-ms11-motd-roleplay1",
				targetGameVersion = "1.22.7",
				retentionAndPrivacy = BuildRetentionAndPrivacyStatus(),
				circuitBreakers = SnapshotCircuitBreakers(),
				selfOverhead = SnapshotOverhead(),
				webRoutePolicies = WebRoutePolicies
			});
			ZipArchiveEntry zipArchiveEntry = zip.CreateEntry("README.txt", CompressionLevel.Fastest);
			using Stream stream = zipArchiveEntry.Open();
			using StreamWriter streamWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			streamWriter.WriteLine("Newfies Server Essentials Suite diagnostic bundle v2");
			streamWriter.WriteLine($"Generated UTC: {DateTime.UtcNow:O}");
			streamWriter.WriteLine();
			streamWriter.WriteLine("Contents:");
			streamWriter.WriteLine("- performance-core.json: health timeline, archived black-box incidents, entity scans/growth, environment changes, alerts/actions, player-load history, world-save observations, server network samples, providers, Harmony overlaps, loaded mods, and redacted web-audit metadata");
			streamWriter.WriteLine("- block-activity.json: recent BlockLogger coordinate activity (activity evidence, not CPU timing)");
			streamWriter.WriteLine("- optimization-experiments.json: measured Tungsten/Synergy before/after tests");
			streamWriter.WriteLine("- compatibility.json: lightweight known-rule/command ownership compatibility report (Harmony deep scan stays manual)");
			streamWriter.WriteLine("- data-collection-audit.json: automatic/manual collector cadence, cost policy and measured suite/web overhead");
			streamWriter.WriteLine("- login-protection.json: current non-secret Login Protection configuration and active count");
			streamWriter.WriteLine("- suite-self-test.json: subsystem health, circuit breakers and storage checks");
			streamWriter.WriteLine("- web-diagnostics.json: endpoint timing/main-thread wait/work evidence");
			streamWriter.WriteLine("- smart-diagnosis.json: rolling health samples, Smart Diagnosis, trends, incidents and profiler evidence");
			streamWriter.WriteLine("- loaded-mods.json: loaded mod IDs/names/versions/files");
			streamWriter.WriteLine("- hardening-summary.json: build ID, retention/export privacy policy, circuit-breaker state, self-overhead, and web endpoint safety policy; no authentication secrets");
			streamWriter.WriteLine();
			streamWriter.WriteLine("Interpretation note: correlation/activity/population evidence is not automatically mod blame. Mod attribution comes only from bounded Vintage Story engine-profiler evidence and is confidence-labelled.");
		}
		finally
		{
			if (zip != null)
			{
				((IDisposable)zip).Dispose();
			}
		}
		return memoryStream.ToArray();
		void AddJson(string name, object value)
		{
			ZipArchiveEntry zipArchiveEntry2 = zip.CreateEntry(name, CompressionLevel.Fastest);
			using Stream stream2 = zipArchiveEntry2.Open();
			using StreamWriter streamWriter2 = new StreamWriter(stream2, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			streamWriter2.Write(JsonSerializer.Serialize(value, jsonOptions));
		}
	}

	private async Task WriteJsonAsync(NetworkStream stream, int statusCode, object value, CancellationToken cancellationToken, string? setCookie = null, bool clearCookie = false)
	{
		byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions);
		WebRequestMetricContext metric = requestMetricContext.Value;
		if (metric != null && metric.Method != "GET")
		{
			using JsonDocument response = JsonDocument.Parse(body);
			int num;
			if (response.RootElement.ValueKind == JsonValueKind.Object)
			{
				JsonElement value2 = response.RootElement;
				num = (((value2.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) || response.RootElement.TryGetProperty("error", out value2)) ? 1 : 0);
			}
			else
			{
				num = 0;
			}
			if (num != 0)
			{
				metric.ResponseRejected = true;
			}
		}
		Dictionary<string, string> extra = new Dictionary<string, string>();
		if (!string.IsNullOrWhiteSpace(setCookie))
		{
			extra["Set-Cookie"] = setCookie;
		}
		else if (clearCookie)
		{
			extra["Set-Cookie"] = "nsu_session=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0" + (config.SecureCookies ? "; Secure" : "");
		}
		await WriteResponseAsync(stream, statusCode, "application/json; charset=utf-8", body, cancellationToken, extra).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string contentType, byte[] body, CancellationToken cancellationToken, Dictionary<string, string>? extraHeaders = null)
	{
		WebRequestMetricContext metric = requestMetricContext.Value;
		if (metric != null)
		{
			lock (metric.Sync)
			{
				metric.StatusCode = statusCode;
				metric.ResponseBytes += body.LongLength;
			}
		}
		if (1 == 0)
		{
		}
		string text = statusCode switch
		{
			200 => "OK", 
			400 => "Bad Request", 
			401 => "Unauthorized", 
			403 => "Forbidden", 
			404 => "Not Found", 
			409 => "Conflict", 
			429 => "Too Many Requests", 
			500 => "Internal Server Error", 
			503 => "Service Unavailable", 
			_ => "OK", 
		};
		if (1 == 0)
		{
		}
		string reason = text;
		StringBuilder header = new StringBuilder();
		StringBuilder stringBuilder = header;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(12, 2, stringBuilder);
		handler.AppendLiteral("HTTP/1.1 ");
		handler.AppendFormatted(statusCode);
		handler.AppendLiteral(" ");
		handler.AppendFormatted(reason);
		handler.AppendLiteral("\r\n");
		stringBuilder2.Append(ref handler);
		stringBuilder = header;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder);
		handler.AppendLiteral("Content-Type: ");
		handler.AppendFormatted(contentType);
		handler.AppendLiteral("\r\n");
		stringBuilder3.Append(ref handler);
		stringBuilder = header;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder);
		handler.AppendLiteral("Content-Length: ");
		handler.AppendFormatted(body.Length);
		handler.AppendLiteral("\r\n");
		stringBuilder4.Append(ref handler);
		header.Append("Connection: close\r\n");
		header.Append("Cache-Control: no-store\r\n");
		header.Append("X-Content-Type-Options: nosniff\r\n");
		header.Append("X-Frame-Options: DENY\r\n");
		header.Append("Referrer-Policy: no-referrer\r\n");
		header.Append("Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n");
		if (extraHeaders != null)
		{
			foreach (KeyValuePair<string, string> pair in extraHeaders)
			{
				stringBuilder = header;
				StringBuilder stringBuilder5 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(4, 2, stringBuilder);
				handler.AppendFormatted(pair.Key);
				handler.AppendLiteral(": ");
				handler.AppendFormatted(pair.Value);
				handler.AppendLiteral("\r\n");
				stringBuilder5.Append(ref handler);
			}
		}
		header.Append("\r\n");
		byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
		await stream.WriteAsync(headerBytes.AsMemory(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		await stream.WriteAsync(body.AsMemory(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static string RandomToken(int bytes)
	{
		byte[] bytes2 = RandomNumberGenerator.GetBytes(bytes);
		return Convert.ToHexString(bytes2).ToLowerInvariant();
	}

	private static bool ConstantTimeEquals(string left, string right)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(left ?? "");
		byte[] bytes2 = Encoding.UTF8.GetBytes(right ?? "");
		if (bytes.Length != bytes2.Length)
		{
			byte[] array = new byte[bytes.Length];
			CryptographicOperations.FixedTimeEquals(bytes, array);
			return false;
		}
		return CryptographicOperations.FixedTimeEquals(bytes, bytes2);
	}

	private static string? NormalizePlayerCategory(string value)
	{
		string text = value.Trim().ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result = text switch
		{
			"blocks" => "blocks", 
			"containers" => "containers", 
			"entities" => "entities", 
			_ => null, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string? NormalizeWebAction(string view, string value)
	{
		value = value.Trim().ToLowerInvariant();
		if (value.Length == 0 || value == "any")
		{
			return (view == "containers") ? "container" : null;
		}
		if (1 == 0)
		{
		}
		string result;
		if (!(view == "containers"))
		{
			if (view == "entities")
			{
				bool flag;
				switch (value)
				{
				case "entityspawn":
				case "entitydeath":
				case "entitydespawn":
				case "entityinteract":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				result = (flag ? value : null);
			}
			else
			{
				bool flag;
				switch (value)
				{
				case "break":
				case "place":
				case "interact":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				result = (flag ? value : null);
			}
		}
		else
		{
			bool flag;
			switch (value)
			{
			case "open":
			case "take":
			case "put":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			result = (flag ? value : "container");
		}
		if (1 == 0)
		{
		}
		return result;
	}
}
