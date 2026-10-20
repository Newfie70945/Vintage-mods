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
using BlockLogger;
using BlockLogger.AnimalLimiter;
using BlockLogger.Afk;
using BlockLogger.Admin;
using BlockLogger.Analytics;
using BlockLogger.AntiCheat;
using BlockLogger.Config;
using BlockLogger.Compatibility;
using BlockLogger.Models;
using BlockLogger.Services;
using BlockLogger.ServerHealth;
using BlockLogger.WebMap;
using BlockLogger.LoginProtection;
using BlockLogger.Integrations;
using BlockLogger.PlayerList;
using BlockLogger.PlayerCommands;
using BlockLogger.Roles;
using BlockLogger.Restarts;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.WebDashboard;

/// <summary>
/// Small self-contained HTTP/1.1 admin server.
///
/// Deliberately uses TcpListener rather than HttpListener so binding to a real
/// LAN/public interface does not depend on Windows HTTP.sys URL ACL setup.
/// Game-state operations are marshalled onto Vintage Story's main thread.
/// </summary>
public sealed partial class WebDashboardService : IDisposable
{
    private const string ConfigFile = "NewfiesWebServer.json";
    private const string AdminAccountsFile = "NewfiesWebAdminAccounts.json";
    private const string OptimizationExperimentsFile = "NewfiesOptimizationExperiments.json";
    private const string PerformanceCenterFile = "NewfiesPerformanceCenter.json";
    private const string OperationsCenterFile = "NewfiesOperationsCenter.json";
    private const string CompatibilityIgnoresFile = "NewfiesCompatibilityIgnores.json";
    public const string BuildId = SuiteRelease.BuildId;
    public const string TargetGameVersion = SuiteRelease.TargetGameVersion;
    private const int PersistentSchemaVersion = 2;
    private const int BlockLoggerSchemaVersion = 3;
    public const int CompatibilityIgnoreSchemaVersion = 1;
    private const int MaxWebAdminAccounts = 50;
    private const long MaxAntiCheatApprovalUploadBytes = 96L * 1024 * 1024;
    private const int MaxAntiCheatApprovalChunkBytes = 72 * 1024;
    private static readonly TimeSpan AntiCheatApprovalUploadLifetime = TimeSpan.FromMinutes(15);

    // Observed Player Load is a capacity/workload model, not a lifecycle profiler.
    // Server startup, debugger pauses, host sleep/resume and shutdown can create a
    // single enormous scheduler interval. Keep those events in ServerHealth
    // diagnostics, but do not let them poison a player-count bucket indefinitely.
    private const double PlayerLoadLifecycleDiscontinuityMs = 30_000d;
    private const long PlayerLoadFreshSampleWindowMs = 10_000;

    // Tungsten is All Rights Reserved. BlockLogger does not copy or embed any
    // Tungsten implementation. This catalog is only a web control surface for
    // Tungsten's documented public /tungsten command interface.
    private static readonly TungstenOptimizationInfo[] TungstenOptimizations =
    {
        new("entitylistreuse", "Entity List Reuse", "Entity spawning", "Lists"),
        new("blocklistreuse", "Block List Reuse", "Block operations", "Lists"),
        new("getdropslistreuse", "Get Drops List Reuse", "Block breaking", "Lists"),
        new("eventmanagerlistreuse", "Event Manager List Reuse", "Event system", "Lists"),
        new("chunkloadingoptimization", "Chunk Loading", "Chunk loading", "Chunks"),
        new("chunkunloadingoptimization", "Chunk Unloading", "Chunk unloading", "Chunks"),
        new("entitysimulationoptimization", "Entity Simulation", "Entity ticking", "Entities"),
        new("cookingcontaineroptimization", "Cooking Container", "Cooking containers", "Containers"),
        new("containeroptimization", "Container", "Inventory/container work", "Containers"),
        new("gridrecipeoptimization", "Grid Recipe", "Crafting recipe matching", "Recipes"),
        new("propickreadingoptimization", "Propick Reading", "Prospecting-pick readings", "Gameplay"),
        new("sendplayerentitydeathsoptimization", "Send Player Entity Deaths", "Player-death broadcasts", "Network"),
        new("physicsmanagerlistoptimization", "Physics Manager List", "Physics tick list reuse", "Physics"),
        new("physicsmanagermethodlistoptimization", "Physics Manager Method List", "Physics method list reuse", "Physics"),
        new("servermainlinqoptimization", "ServerMain LINQ", "Player/server queries", "Server"),
        new("placeholderoptimization", "Placeholder", "Registry placeholder processing", "Registry"),
        new("wildcardfastmatchoptimization", "Wildcard Fast Match", "Wildcard matching", "Registry"),
        new("getentitiesaroundoptimization", "Get Entities Around", "Entity queries", "Entities"),
        new("entitydespawnpacketoptimization", "Entity Despawn Packet", "Despawn packet handling", "Network"),
        new("recipebaselinqoptimization", "RecipeBase LINQ", "Recipe LINQ paths", "Recipes"),
        new("broadcastlinqoptimization", "Broadcast LINQ", "Server broadcast paths", "Network"),
        new("bulkentityattributespacketoptimization", "Bulk Entity Attributes Packet", "Entity attribute packets", "Network"),
        new("classregistryfrozenoptimization", "ClassRegistry Frozen", "Registry compaction/frozen lookup", "Registry"),
        new("getplayersaroundoptimization", "Get Players Around", "Nearby-player queries", "Server"),
        new("genterrazeroallocoptimization", "GenTerra Zero Alloc", "Worldgen column-noise allocations", "Worldgen"),
        new("genterrabitarrayoptimization", "GenTerra BitArray", "Worldgen BitArray work", "Worldgen"),
        new("genterranativenoiseoptimization", "GenTerra Native Noise", "Worldgen native-noise path", "Worldgen")
    };
    // Synergy is an external All Rights Reserved mod. The suite does not copy
    // or embed Synergy implementation code; this catalog is a web control and
    // advisory surface for Synergy's documented public /synergy command API.
    private static readonly SynergyOptimizationInfo[] SynergyOptimizations =
    {
        new("activationrange", "Entity Activation Range", "Skips most distant non-player entity ticks while preserving safety/whitelisted behaviors.", "Entities", "Server", "Can affect modded entities that require continuous far-away ticking; disable this optimization for those workloads."),
        new("collisionfastpath", "Entity Collision Fast-Path", "Skips terrain collision resolution for stationary entities when Synergy's safety conditions are met.", "Physics", "Server", "Best fit for entity-heavy areas such as animal pens; players are excluded by Synergy."),
        new("networkflush", "Network Flush Consolidation", "Batches small TCP sends and flushes them at tick boundaries to reduce syscall overhead.", "Network", "Server", "Most useful with several concurrent players; large/compressed packets still use Synergy's immediate path."),
        new("inventoryscan", "Inventory Dirty Scan", "Skips the full dirty-slot inventory scan when nothing has changed.", "Inventories", "Server", "Usually a small gain; becomes more relevant on high-player-count servers."),
        new("pathfindingpool", "Pathfinding Node Pooling", "Pools A* path nodes to reduce pathfinding allocations and GC pressure.", "Pathfinding", "Server", "Allocation optimization; useful when pathfinding/GC evidence is present."),
        new("pathfindingthrottle", "Pathfinding Distance Throttle", "Reduces pathfinding request frequency as entities get farther from players.", "Pathfinding", "Server", "Distant entities may react slightly later by design."),
        new("repulsethrottle", "Repulse Agents Throttle", "Reduces expensive entity repulsion checks for distant entities.", "Entities", "Server", "Especially useful for dense animal/entity groups because repulsion work can scale poorly."),
        new("aithrottle", "AI Brain Tick Throttle", "Reduces AI task evaluation frequency by distance while preserving running/survival-critical tasks.", "AI", "Server", "Use profiler evidence when possible; Synergy exempts urgent/combat/survival cases."),
        new("hysteresis", "Tracking Hysteresis", "Keeps entities tracked inside a buffer around the normal tracking boundary to reduce pop-in/out.", "Fluidity", "Server", "Fluidity feature rather than a direct TPS optimization."),
        new("sendfrequency", "Distance Send Frequency", "Reduces position update frequency for distant entities to lower bandwidth.", "Network", "Server", "If distant animals look choppy, this is the first Synergy option to test OFF."),
        new("attributedelta", "Attribute Sync No-Op Skip", "Avoids unnecessary full attribute-tree resync work for no-op attribute removals.", "Network", "Server", "Bandwidth/serialization optimization; benefit depends on mod/entity behavior."),
        new("spawnpriority", "Spawn Priority Ordering", "Prioritizes nearer tracked entities after login/teleport for better perceived loading.", "Fluidity", "Server", "Primarily improves perceived loading order, not server TPS."),
        new("entityloadbudget", "Entity Load Budgeting", "Budgets client entity creation across ticks after login/teleport to avoid frame spikes.", "Client Fluidity", "Client", "Requires Synergy on the client to provide the client-side benefit."),
        new("deltaencoding", "Delta Position Encoding", "Uses compact delta-encoded entity position packets for Synergy-capable clients.", "Network", "Server + Client", "Bandwidth savings require Synergy on both server and client; vanilla clients keep the vanilla packet path."),
        new("blocktickpool", "Block Tick Pooling", "Pools BlockPos instances in random block ticking to reduce allocation pressure.", "World / GC", "Server", "Most valuable when block ticking contributes measurable allocation/GC pressure.")
    };

    private static readonly WebRoutePolicyInfo[] WebRoutePolicies =
    {
        new() { Method = "GET", Path = "/api/dashboard", Access = "Authenticated read", Risk = "Read-only", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/web/status", Access = "Authenticated read", Risk = "Read-only", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/customization", Access = "Authenticated read", Risk = "Reads WebPanel branding and presentation preferences", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/customization", Access = "Administrator/Owner + CSRF", Risk = "Changes WebPanel branding and presentation preferences", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/community-motd", Access = "Authenticated read", Risk = "Reads Community MOTD companion status and configuration", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/community-motd", Access = "Administrator/Owner + CSRF", Risk = "Changes Community MOTD configuration or resets MOTD seen-state", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/playercommands", Access = "Authenticated read", Risk = "Player command availability, provider ownership, and settings read", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/playercommands/settings", Access = "Administrator/Owner + CSRF", Risk = "Enables/disables player command subsystems and changes cooldown/message policy", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/playercommands/command", Access = "Administrator/Owner + CSRF", Risk = "Enables or disables one player-facing Suite command", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/roles", Access = "Administrator/Owner + CSRF", CooldownSeconds=1, Risk = "Changes game roles and shared Suite policies", Safety = "High" },
        new() { Method = "POST", Path = "/api/roles/batch", Access = "Administrator/Owner + CSRF", CooldownSeconds = 1, Risk = "Preview or assign up to 50 player roles", Safety = "High" },
        new() { Method = "POST", Path = "/api/roles/preview", Access = "Administrator/Owner + CSRF", Risk = "Read-only role draft preview", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/roles/player", Access = "Administrator/Owner + CSRF", CooldownSeconds=1, Risk = "Assigns online or offline player roles", Safety = "High" },
        new() { Method = "GET", Path = "/api/playercommands/permissions", Access = "Authenticated read", Risk = "Configured roles, online-player roles, and direct privilege overrides", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/playercommands/permissions", Access = "Administrator/Owner + CSRF", Risk = "Changes an online player role or permanent direct privilege override", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/warps", Access = "Authenticated read", Risk = "Warp destinations and cost policy read", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/warps/settings", Access = "Administrator/Owner + CSRF", Risk = "Changes default warp policy", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/warps/upsert", Access = "Administrator/Owner + CSRF", Risk = "Creates or changes a teleport destination, access policy, and optional item cost", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/warps/delete", Access = "Administrator/Owner + CSRF", Risk = "Deletes a configured warp", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/warps/reload", Access = "Administrator/Owner + CSRF", Risk = "Reloads warp configuration from disk", Safety = "Moderate", CooldownSeconds = 2 },
        new() { Method = "GET", Path = "/api/discord", Access = "Authenticated read", Risk = "Discord integration/link status and configuration read (bot token excluded)", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/discord/settings", Access = "Administrator/Owner + CSRF", Risk = "Changes Discord integration, announcement routing, or account-linking settings", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/discord/test", Access = "Administrator/Owner + CSRF", Risk = "Queues a Discord test announcement", Safety = "Moderate", CooldownSeconds = 2 },
        new() { Method = "GET", Path = "/api/web/accounts", Access = "Owner only", Risk = "Panel account metadata read", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/web/accounts/create", Access = "Owner + CSRF", Risk = "Creates a panel administrator account", Safety = "Admin / High impact", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/web/accounts/action", Access = "Owner + CSRF", Risk = "Changes/disables/deletes panel administrator accounts", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/live", Access = "Authenticated read", Risk = "Cached/lightweight live state", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/features", Access = "Authenticated read", Risk = "Master feature-gate configuration read", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/features", Access = "Administrator/Owner + CSRF", Risk = "Changes which suite subsystems start after restart", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/playerlist/config", Access = "Authenticated read", Risk = "Public Tab player-list configuration and diagnostics read", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/playerlist/config", Access = "Administrator/Owner + CSRF", Risk = "Changes public Tab player-list appearance, layout, reserved-slot access, and debug settings", Safety = "Low", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/compatibility", Access = "Authenticated read", Risk = "Loaded-mod compatibility inspection", Safety = "Safe", CooldownSeconds = 2 },
        new() { Method = "GET", Path = "/api/compatibility/deep", Access = "Authenticated read", Risk = "Harmony patch-overlap inspection", Safety = "Moderate", CooldownSeconds = 20, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/compatibility/ignore", Access = "Authenticated + CSRF", Risk = "Suppresses a compatibility notification", Safety = "Safe", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/compatibility/unignore", Access = "Authenticated + CSRF", Risk = "Restores a compatibility notification", Safety = "Safe", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/log-intelligence", Access = "Authenticated read", Risk = "Cached log diagnosis/preflight state", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/log-intelligence/block-entity", Access = "Authenticated read", Risk = "Live inspection of one tracked block/block entity", Safety = "Safe", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/log-intelligence/block-entity/action", Access = "Administrator/Owner + CSRF", Risk = "Can stop/resume tracking, clear tracker entries, teleport an online player, remove a block entity, or replace a block", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/log-intelligence/issues/clear", Access = "Administrator/Owner + CSRF", Risk = "Clears retained grouped issue signatures without deleting server logs", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/player-analytics", Access = "Authenticated read", Risk = "Read-only player-count history", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/server-status", Access = "Authenticated read", Risk = "Read-only server status and player analytics", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/anticheat/status", Access = "Authenticated read", Risk = "Lightweight Anti-Cheat navigation health counters only", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/anticheat", Access = "Authenticated read", Risk = "Read-only anti-cheat client/session status", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/anticheat/client-mods", Access = "Authenticated read", Risk = "On-demand client mod comparison inventories", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/anticheat/player", Access = "Authenticated read", Risk = "Sensitive anti-cheat client mod/finding detail", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/anticheat/history", Access = "Authenticated read", Risk = "Persistent anti-cheat incident history including evidence and positions", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/anticheat/history/clear", Access = "Administrator/Owner + CSRF", Risk = "Permanently deletes all persistent anti-cheat incident reports", Safety = "High", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/anticheat/settings", Access = "Administrator/Owner + CSRF", Risk = "Enables or disables live Anti-Cheat enforcement, including client scans, PacketGuard and automatic consequences; installed companion dependency remains required on clients", Safety = "High", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/anticheat/client-settings", Access = "Administrator/Owner + CSRF", Risk = "Changes Client Integrity handshake/startup scan timeouts, startup quarantine gate, rescan cadence, hardened transport requirement, and official ModDB verification of client mods", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/anticheat/full-config", Access = "Authenticated read", Risk = "Reads the complete Anti-Cheat configuration for advanced administration", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/anticheat/full-config", Access = "Administrator/Owner + CSRF", Risk = "Replaces the complete editable Anti-Cheat configuration; module lifecycle and schema version remain protected", Safety = "High", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/anticheat/packetguard", Access = "Administrator/Owner + CSRF", Risk = "Changes server-authoritative packet/movement/reach/mining protection and false-positive exemptions", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/oreshield", Access = "Administrator/Owner + CSRF", Risk = "Changes Ore Shield concealment, host/ore matching and chunk-cache policy", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/rescan", Access = "Administrator/Owner + CSRF", Risk = "Requests an immediate client anti-cheat rescan", Safety = "Moderate", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/anticheat/evidence/request", Access = "Administrator/Owner + CSRF", Risk = "Requests player authorization to transfer the exact loaded suspicious mod package; accepted packages are stored under SuspectedHacks and explicit decline triggers the configured server policy", Safety = "High", CooldownSeconds = 3 },
        new() { Method = "POST", Path = "/api/anticheat/evidence/analyze", Access = "Administrator/Owner + CSRF", Risk = "Hashes a captured suspicious mod package and compares its bounded file manifest with cached official same-version ModDB candidates", Safety = "Moderate", CooldownSeconds = 3 },
        new() { Method = "POST", Path = "/api/anticheat/evidence/settings", Access = "Administrator/Owner + CSRF", Risk = "Changes suspicious package evidence collection limits", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/blacklist", Access = "Administrator/Owner + CSRF", Risk = "Changes forbidden mod IDs/fingerprints and re-evaluates current sessions", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/moddb/approval", Access = "Administrator/Owner + CSRF", Risk = "Adds or removes an exact ModID/version/normalized-content fingerprint exception for intentional private or patched client mods", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/moddb/approval/upload", Access = "Administrator/Owner + CSRF", Risk = "Receives a bounded mod ZIP in chunks, inspects root modinfo.json, hashes package/content without executing it, and can approve that exact client-mod fingerprint", Safety = "High" },
        new() { Method = "POST", Path = "/api/anticheat/moddb/resolve-all", Access = "Administrator/Owner + CSRF", Risk = "Queues exact official ModDB fingerprint resolution for all unique third-party client mod ID/version pairs currently reported by online clients", Safety = "Moderate", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/anticheat/blacklist/profile", Access = "Administrator/Owner + CSRF", Risk = "Loads, saves, or deletes named blacklist profiles and can replace the active blacklist", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/blacklist/resolve", Access = "Administrator/Owner + CSRF", Risk = "Downloads an official Mod DB release in memory and stores blacklist package/content fingerprints", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/blacklist/fingerprint-settings", Access = "Administrator/Owner + CSRF", Risk = "Changes how many recent Mod DB releases are fingerprinted per blacklisted mod", Safety = "Medium", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/blacklist/manual", Access = "Administrator/Owner + CSRF", Risk = "Adds, edits, or removes operator-supplied exact blacklist fingerprints and re-evaluates current sessions", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/integrity-policy", Access = "Administrator/Owner + CSRF", Risk = "Changes approved Client Integrity build hashes and build-lock enforcement", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/anticheat/consequences", Access = "Administrator/Owner + CSRF", Risk = "Changes automatic anti-cheat moderation consequences; can kick, temporarily ban, or freeze matching players", Safety = "High", CooldownSeconds = 2 },
        new() { Method = "GET", Path = "/api/restarts", Access = "Authenticated read", Risk = "Automated restart schedule/status read", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/restarts/settings", Access = "Administrator/Owner + CSRF", Risk = "Changes automatic restart schedule and warning behavior", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/restarts/action", Access = "Administrator/Owner + CSRF", Risk = "Can skip/reload/toggle automatic restarts or schedule/cancel a one-off restart", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/chat-display", Access = "Authenticated read", Risk = "Chat presentation settings read", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/chat-display/settings", Access = "Administrator/Owner + CSRF", Risk = "Changes player chat presentation and shared role name colors", Safety = "Low", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/chatguard", Access = "Authenticated read", Risk = "Optional standalone ChatGuard moderation status and audit summary", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/chatguard/settings", Access = "Administrator/Owner + CSRF", Risk = "Changes standalone ChatGuard moderation rules and enforcement mode", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/chatguard/action", Access = "Administrator/Owner + CSRF", Risk = "Can warn, temporarily/permanently mute, unmute, or clear ChatGuard warnings through the standalone module", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/chatguard/test", Access = "Administrator/Owner + CSRF", Risk = "Dry-runs a message through ChatGuard rules without changing moderation state or counters", Safety = "Safe", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/announcements", Access = "Authenticated read", Risk = "Announcement schedule/configuration read", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/announcements/save", Access = "Authenticated + CSRF", Risk = "Announcement schedule/configuration change", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/announcements/action", Access = "Authenticated + CSRF", Risk = "Immediate announcement/reset action", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/console", Access = "Authenticated read", Risk = "Server log/console mirror", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/console/input", Access = "Authenticated + CSRF", Risk = "Enables web console command input for 15 minutes", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/console/command", Access = "Authenticated + CSRF", Risk = "Executes a Vintage Story server command or broadcasts a server message", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/webmap", Access = "Authenticated read", Risk = "Native map host status plus cached map-layer data", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/leaderboard", Access = "Authenticated read", Risk = "Cached community progression leaderboard including offline XSkills progress", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/webmap/terrain/status", Access = "Authenticated read", Risk = "Lightweight terrain export progress/status only", Safety = "Low" },
        new() { Method = "POST", Path = "/api/webmap/config", Access = "Administrator/Owner + CSRF", Risk = "Changes native map listener, public layers, and one-shot next-start terrain export/lock configuration", Safety = "Admin", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/webmap/export-schedule", Access = "Administrator/Owner + CSRF", Risk = "Changes only startup terrain export scheduling/mode settings while preserving the Web Map listener and public-layer configuration", Safety = "Admin", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/webmap/terrain/update", Access = "Administrator/Owner + CSRF", Risk = "Explicitly updates only changed/new/missing live terrain tiles when the current cache is compatible", Safety = "Admin", CooldownSeconds = 3 },
        new() { Method = "POST", Path = "/api/webmap/terrain/force", Access = "Administrator/Owner + CSRF", Risk = "Explicitly starts a live terrain export; may temporarily increase server disk/CPU load", Safety = "Admin", CooldownSeconds = 5 },
        new() { Method = "POST", Path = "/api/webmap/waypoint/save", Access = "Administrator/Owner + CSRF", Risk = "Creates or edits a persistent public Web Map waypoint", Safety = "Admin", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/webmap/waypoint/delete", Access = "Administrator/Owner + CSRF", Risk = "Deletes a persistent public Web Map waypoint", Safety = "Admin", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/mod-updates", Access = "Authenticated read", Risk = "Cached Mod DB update status", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/mod-updates/check", Access = "Authenticated + CSRF", Risk = "Outbound official Mod DB update check", Safety = "Moderate", CooldownSeconds = 30, ExclusiveHeavyOperation = false },
        new() { Method = "POST", Path = "/api/mod-updates/apply", Access = "Administrator/Owner + CSRF", Risk = "Downloads, validates, backs up and replaces a loaded ZIP mod package on disk; restart required", Safety = "High", CooldownSeconds = 10, ExclusiveHeavyOperation = true },
        new() { Method = "GET", Path = "/api/reports", Access = "Authenticated read", Risk = "Sensitive issue report queue", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/reports/detail", Access = "Authenticated read", Risk = "Paged private report history", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/reports/action", Access = "Authenticated + CSRF", Risk = "Moderation workflow change", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/log-intelligence/scan", Access = "Authenticated + CSRF", Risk = "Manual current/archive log scan", Safety = "Moderate", CooldownSeconds = 20, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/log-intelligence/preflight", Access = "Authenticated + CSRF", Risk = "Loaded-mod/config/command preflight inspection", Safety = "Safe", CooldownSeconds = 5 },
        new() { Method = "GET", Path = "/api/collection-audit", Access = "Authenticated read", Risk = "Read-only collection-cost audit", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/loginprotection/config", Access = "Authenticated read", Risk = "Configuration read", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/loginprotection/config", Access = "Authenticated + CSRF", Risk = "Configuration-changing", Safety = "Moderate", CooldownSeconds = 2 },
        new() { Method = "GET", Path = "/api/admin/status", Access = "Authenticated read", Risk = "Sensitive read", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/admin/players", Access = "Authenticated read", Risk = "Sensitive read", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/admin/database-storage", Access = "Authenticated read", Risk = "Cached database disk-usage viewer", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/admin/database-storage/analyze", Access = "Administrator/Owner + CSRF", Risk = "Manual SQLite page-usage analysis", Safety = "Moderate", CooldownSeconds = 15, ExclusiveHeavyOperation = true },
        new() { Method = "GET", Path = "/api/admin/player", Access = "Authenticated read", Risk = "Sensitive read", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/admin/inventory", Access = "Authenticated read", Risk = "Live or read-only saved player inventory, snapshots and inventory-only audit history", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/admin/items", Access = "Authenticated read", Risk = "Item catalog search", Safety = "Low" },
        new() { Method = "POST", Path = "/api/admin/inventory/action", Access = "Authenticated + CSRF", Risk = "Inventory mutation", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/admin/inventory/compare", Access = "Authenticated read", Risk = "Inventory snapshot comparison", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/admin/inventory/snapshot", Access = "Authenticated + CSRF", Risk = "Inventory snapshot/restore", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/admin/action", Access = "Authenticated + CSRF", Risk = "Player administration", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/admin/maintenance", Access = "Authenticated + CSRF", Risk = "Server access control", Safety = "Admin / High impact", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/admin/shutdown", Access = "Authenticated + CSRF", Risk = "Server shutdown", Safety = "Admin / High impact", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/admin/cleanup", Access = "Authenticated + CSRF", Risk = "Entity cleanup", Safety = "Admin / High impact", CooldownSeconds = 5, ExclusiveHeavyOperation = true },
        new() { Method = "GET", Path = "/api/health", Access = "Authenticated read", Risk = "Read-only", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/health/database-integrity", Access = "Authenticated + CSRF", Risk = "Diagnostic action", Safety = "Moderate", CooldownSeconds = 60, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/health/safe-mode", Access = "Authenticated + CSRF", Risk = "Configuration-changing", Safety = "Moderate", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/health/circuit-reset", Access = "Authenticated + CSRF", Risk = "Diagnostic recovery", Safety = "Moderate", CooldownSeconds = 2 },
        new() { Method = "GET", Path = "/api/operations/overview", Access = "Authenticated read", Risk = "Read-only", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/operations/diagnostics", Access = "Authenticated read", Risk = "Read-only counters", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/admin/investigation", Access = "Authenticated read", Risk = "Sensitive player investigation", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/operations/memory", Access = "Authenticated read", Risk = "Read-only", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/operations/memory-snapshot", Access = "Authenticated + CSRF", Risk = "Captures or manages lightweight memory investigation snapshots", Safety = "Safe", CooldownSeconds = 2 },
        new() { Method = "GET", Path = "/api/operations/audit", Access = "Authenticated read", Risk = "Sensitive read", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/operations/alert-status", Access = "Authenticated + CSRF", Risk = "Workflow change", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/operations/clear", Access = "Administrator/Owner + CSRF", Risk = "Clears reviewed Action Center records", Safety = "Safe", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/operations/action-status", Access = "Authenticated + CSRF", Risk = "Workflow change", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/operations/performance-snapshot", Access = "Authenticated + CSRF", Risk = "Diagnostic action", Safety = "Safe", CooldownSeconds = 10 },
        new() { Method = "POST", Path = "/api/operations/config-snapshot", Access = "Authenticated + CSRF", Risk = "Configuration backup", Safety = "Moderate", CooldownSeconds = 5 },
        new() { Method = "POST", Path = "/api/operations/config-restore", Access = "Authenticated + CSRF", Risk = "Configuration-changing", Safety = "Admin / High impact", CooldownSeconds = 30, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/performance/entity-scan", Access = "Authenticated + CSRF", Risk = "Heavy diagnostic action", Safety = "Moderate", CooldownSeconds = 30, ExclusiveHeavyOperation = true },
        new() { Method = "GET", Path = "/api/performance/conflicts", Access = "Authenticated read", Risk = "Heavy diagnostic action", Safety = "Moderate", CooldownSeconds = 20, ExclusiveHeavyOperation = true },
        new() { Method = "GET", Path = "/api/performance/bundle", Access = "Authenticated read", Risk = "Heavy export", Safety = "Moderate", CooldownSeconds = 30, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/animals/scan", Access = "Authenticated + CSRF", Risk = "Bounded incremental diagnostic", Safety = "Moderate", CooldownSeconds = 15, ExclusiveHeavyOperation = false },
        new() { Method = "GET", Path = "/api/animals/scan-status", Access = "Authenticated read", Risk = "Lightweight progress poll", Safety = "Safe", CooldownSeconds = 0, ExclusiveHeavyOperation = false },
        new() { Method = "GET", Path = "/api/tick-debug", Access = "Authenticated read", Risk = "Read-only native tick diagnostics", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/tick-debug/enabled", Access = "Administrator/Owner + CSRF", Risk = "Enables or disables Tick Debugger actions", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/tick-debug/slow-start", Access = "Authenticated + CSRF", Risk = "Native slow-tick logging", Safety = "Moderate", CooldownSeconds = 10, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/tick-debug/slow-stop", Access = "Authenticated + CSRF", Risk = "Stops native slow-tick logging", Safety = "Safe", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/tick-debug/action", Access = "Authenticated + CSRF", Risk = "Native diagnostic command", Safety = "Moderate", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/tick-debug/incident", Access = "Authenticated + CSRF", Risk = "Combined native slow-tick and engine profile capture", Safety = "Admin / High impact", CooldownSeconds = 30, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/profiler/start", Access = "Authenticated + CSRF", Risk = "Engine profiling", Safety = "Admin / High impact", CooldownSeconds = 30, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/profiler/enabled", Access = "Authenticated + CSRF", Risk = "Diagnostic configuration", Safety = "Safe", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/profiler/clear", Access = "Authenticated + CSRF", Risk = "Clears retained profiler result lists", Safety = "Safe", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/performance/lag-history/clear", Access = "Authenticated + CSRF", Risk = "Clears retained lag diagnostic history", Safety = "Safe", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/tick-debug/clear", Access = "Authenticated + CSRF", Risk = "Clears retained Tick Debugger output", Safety = "Safe", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/optimizations/tungsten/test", Access = "Authenticated + CSRF", Risk = "External optimizer change", Safety = "External / High impact", CooldownSeconds = 10, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/optimizations/tungsten/toggle", Access = "Authenticated + CSRF", Risk = "External optimizer change", Safety = "External / High impact", CooldownSeconds = 3 },
        new() { Method = "POST", Path = "/api/optimizations/tungsten/all", Access = "Authenticated + CSRF", Risk = "External optimizer change", Safety = "External / High impact", CooldownSeconds = 10, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/optimizations/tungsten/action", Access = "Authenticated + CSRF", Risk = "External optimizer command", Safety = "External", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/optimizations/synergy/test", Access = "Authenticated + CSRF", Risk = "External optimizer change", Safety = "External / High impact", CooldownSeconds = 10, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/optimizations/synergy/toggle", Access = "Authenticated + CSRF", Risk = "External optimizer change", Safety = "External / High impact", CooldownSeconds = 3 },
        new() { Method = "POST", Path = "/api/optimizations/synergy/all", Access = "Authenticated + CSRF", Risk = "External optimizer change", Safety = "External / High impact", CooldownSeconds = 10, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/optimizations/synergy/action", Access = "Authenticated + CSRF", Risk = "External optimizer command", Safety = "External", CooldownSeconds = 2 },
        new() { Method = "GET", Path = "/api/animals/status", Access = "Authenticated read", Risk = "Animal Limiter runtime status", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/animals/rules", Access = "Authenticated read", Risk = "Animal Limiter rule configuration read", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/animals/rules/set", Access = "Authenticated + CSRF", Risk = "Creates or updates an Animal Limiter rule", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/animals/rules/remove", Access = "Authenticated + CSRF", Risk = "Removes an Animal Limiter rule", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/animals/exclusions/add", Access = "Authenticated + CSRF", Risk = "Adds an Animal Limiter exclusion", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/animals/exclusions/remove", Access = "Authenticated + CSRF", Risk = "Removes an Animal Limiter exclusion", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/animals/recommendations/start", Access = "Authenticated + CSRF", Risk = "Manual loaded-entity recommendation scan", Safety = "Moderate", CooldownSeconds = 10, ExclusiveHeavyOperation = true },
        new() { Method = "GET", Path = "/api/animals/recommendations", Access = "Authenticated read", Risk = "Animal Limiter recommendation results", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/blocklogger/nearby", Access = "Authenticated read", Risk = "Sensitive nearby audit lookup", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/blocklogger/player", Access = "Authenticated read", Risk = "Sensitive player audit lookup", Safety = "Moderate" },
        new() { Method = "GET", Path = "/api/blocklogger/recording", Access = "Authenticated read", Risk = "Live recording and snapshot recovery status", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/blocklogger/recording", Access = "Authenticated + CSRF", Risk = "Change future audit recording", Safety = "Admin / High impact", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/blocklogger/maintenance", Access = "Authenticated read", Risk = "BlockLogger storage policy/status", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/blocklogger/maintenance/config", Access = "Administrator/Owner + CSRF", Risk = "Changes BlockLogger retention and backup policy", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/blocklogger/purge", Access = "Administrator/Owner + CSRF", Risk = "Deletes selected historical BlockLogger database rows and may compact SQLite", Safety = "High", CooldownSeconds = 10, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/health/admin-database-backup", Access = "Authenticated + CSRF", Risk = "Admin database backup", Safety = "Moderate", CooldownSeconds = 15 },
        new() { Method = "POST", Path = "/api/health/admin-database-integrity", Access = "Authenticated + CSRF", Risk = "Admin database integrity check", Safety = "Moderate", CooldownSeconds = 30 },
        new() { Method = "POST", Path = "/api/health/database-backup", Access = "Authenticated + CSRF", Risk = "BlockLogger database backup", Safety = "Moderate", CooldownSeconds = 15 },
        new() { Method = "GET", Path = "/api/loginprotection/status", Access = "Authenticated read", Risk = "Login Protection runtime status", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/operations/alert-remove", Access = "Authenticated + CSRF", Risk = "Removes an operations alert", Safety = "Safe", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/optimizations/experiment/evaluate", Access = "Authenticated + CSRF", Risk = "Optimization experiment evaluation", Safety = "Moderate", CooldownSeconds = 2 },
        new() { Method = "GET", Path = "/api/optimizations/synergy", Access = "Authenticated read", Risk = "External optimizer status", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/block-migration", Access = "Authenticated read", Risk = "Missing-block scan and loaded-chunk migration status", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/block-migration", Access = "Owner + CSRF", Risk = "Configure numeric block ID migration as chunks load", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/optimizations/newfies", Access = "Authenticated read", Risk = "Optimizer status", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/optimizations/newfies", Access = "Administrator/Owner + CSRF", Risk = "Optimizer module settings / capture controls", Safety = "High", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/optimizations/tungsten", Access = "Authenticated read", Risk = "External optimizer status", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/performance/block-activity", Access = "Authenticated read", Risk = "Read-only BlockLogger activity aggregation", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/performance/entity-growth", Access = "Authenticated read", Risk = "Read-only entity growth analysis", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/performance/entity-scan", Access = "Authenticated read", Risk = "Cached entity scan result", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/performance/environment", Access = "Authenticated read", Risk = "Read-only environment history", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/performance/incident", Access = "Authenticated read", Risk = "Read-only incident detail", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/performance/overview", Access = "Authenticated read", Risk = "Read-only performance overview", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/performance/page-snapshot", Access = "Authenticated read", Risk = "Cached/coalesced Performance Center page snapshot", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/performance/providers", Access = "Authenticated read", Risk = "Read-only provider status", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/performance/timeline", Access = "Authenticated read", Risk = "Read-only performance timeline", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/profiler", Access = "Authenticated read", Risk = "Read-only profiler state", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/server/mods", Access = "Authenticated read", Risk = "Loaded mod inventory and profiler attribution", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/server/mods/detail", Access = "Authenticated read", Risk = "Loaded mod profiler detail", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/custom-patches", Access = "Authenticated read", Risk = "Reads constrained custom Harmony patch definitions and runtime telemetry", Safety = "Moderate" },
        new() { Method = "POST", Path = "/api/custom-patches/save", Access = "Owner + CSRF", Risk = "Creates or edits a declarative runtime Harmony patch", Safety = "High", CooldownSeconds = 2, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/custom-patches/action", Access = "Owner + CSRF", Risk = "Enables, disables, deletes, or reapplies a custom runtime Harmony patch", Safety = "High", CooldownSeconds = 2, ExclusiveHeavyOperation = true },
        new() { Method = "POST", Path = "/api/custom-patches/generate", Access = "Administrator/Owner + CSRF", Risk = "Generates a disabled declarative patch template; does not apply code", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "GET", Path = "/api/content-patches", Access = "Authenticated read", Risk = "Reads generated JSON/recipe patch definitions and restart state", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/content-patches/assets", Access = "Authenticated read", Risk = "Searches loaded recipe, item and block asset names", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/content-patches/asset", Access = "Authenticated read", Risk = "Inspects one loaded content asset", Safety = "Safe" },
        new() { Method = "GET", Path = "/api/content-patches/export", Access = "Authenticated read", Risk = "Downloads the managed enabled content patch pack", Safety = "Safe" },
        new() { Method = "POST", Path = "/api/content-patches/build", Access = "Owner + CSRF", Risk = "Validates and stages a generated content change for next restart", Safety = "High", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/content-patches/generate", Access = "Administrator/Owner + CSRF", Risk = "Generates a disabled JSON/recipe patch template", Safety = "Moderate", CooldownSeconds = 1 },
        new() { Method = "POST", Path = "/api/content-patches/save", Access = "Owner + CSRF", Risk = "Writes a managed Vintage Story content-patch mod for next restart", Safety = "High", CooldownSeconds = 2 },
        new() { Method = "POST", Path = "/api/content-patches/action", Access = "Owner + CSRF", Risk = "Enables, disables, or deletes generated content patches for next restart", Safety = "High", CooldownSeconds = 2 }
    };

    private const int MaxWebEndpointMetricKeys = 128;
    private const int MaxWebSessions = 128;
    private const int MaxFailedLoginSources = 2048;

    private readonly ICoreServerAPI api;
    private readonly BlockLoggerModule module;
    private readonly object stateLock = new();
    private readonly object authLock = new();
    private readonly object webAccountLock = new();
    private readonly object operationsLock = new();
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    private static readonly byte[] DashboardHtmlBytes = Encoding.UTF8.GetBytes(WebDashboardPage.Html);
    private static readonly byte[] DashboardHtmlGzipBytes = CompressDashboardHtml();

    private static byte[] CompressDashboardHtml()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(DashboardHtmlBytes);
        return output.ToArray();
    }

    internal static bool AcceptsDashboardGzip(string? acceptEncoding)
    {
        bool wildcard = false;
        foreach (string entry in (acceptEncoding ?? "").Split(','))
        {
            string[] parts = entry.Split(';');
            string coding = parts[0].Trim();
            double quality = 1;
            foreach (string parameter in parts.Skip(1))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length == 2 && pair[0].Trim().Equals("q", StringComparison.OrdinalIgnoreCase) &&
                    (!double.TryParse(pair[1].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality) || quality < 0 || quality > 1)) quality = 0;
            }
            if (coding.Equals("gzip", StringComparison.OrdinalIgnoreCase)) return quality > 0;
            if (coding == "*") wildcard = quality > 0;
        }
        return wildcard;
    }
    private static readonly byte[] BusyResponseBytes = Encoding.UTF8.GetBytes("Server busy - try again shortly.");
    private static readonly byte[] BusyHttpResponsePacket = Encoding.ASCII.GetBytes(
        "HTTP/1.1 503 Service Unavailable\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "Content-Length: 32\r\n" +
        "Connection: close\r\n" +
        "Cache-Control: no-store\r\n" +
        "Retry-After: 1\r\n\r\n" +
        "Server busy - try again shortly.");
    private readonly SemaphoreSlim heavyOperationGate = new(1, 1);
    private readonly object hardeningLock = new();
    private readonly Dictionary<string, FeatureBreakerState> featureBreakers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FeatureOverheadState> featureOverhead =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object webMetricsLock = new();
    private readonly Dictionary<string, WebEndpointMetricsState> webEndpointMetrics =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<WebRequestDiagnostic> recentWebRequests = new();
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
    private static readonly AsyncLocal<WebRequestMetricContext?> requestMetricContext = new();
    private readonly Dictionary<string, long> operationLastStarted =
        new(StringComparer.OrdinalIgnoreCase);
    private string activeHeavyOperation = "";
    private ServerUtilitiesSelfTest lastSelfTest = new();
    private LogIntelligenceService? logIntelligence;

    // Native Vintage Story tick-debugging state. Heavy native diagnostics remain
    // explicitly admin-triggered; the status endpoint itself is lightweight.
    private readonly object tickDebugLock = new();
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

    private readonly Dictionary<string, WebSession> sessions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FailedLoginState> failedLogins =
        new(StringComparer.OrdinalIgnoreCase);

    private WebDashboardConfig config = new();
    private SuiteCustomizationConfig suiteCustomization = new();
    private readonly SemaphoreSlim databaseStorageAnalysisGate=new(1,1);
    private WebAdminAccountStore webAdminAccounts = new();
    private OptimizationExperimentStore optimizationExperiments = new();
    private PerformanceEntityScanSnapshot? lastPerformanceEntityScan;
    private PerformanceCenterStore performanceCenterStore = new();
    private OperationsCenterStore operationsCenterStore = new();
    private CompatibilityIgnoreStore compatibilityIgnores = new();
    private long performanceMaintenanceListenerId = -1;
    private long lastPerformanceMaintenanceUnixMilliseconds;
    private long lastOperationsMaintenanceUnixMilliseconds;
    private long lastPlayerLoadSampleUnixMilliseconds;
    // One in-flight main-thread build per server-owned key, including cold loads.
    private readonly CoalescedSnapshotCache snapshotCache = new();
    private sealed class AntiCheatApprovalUploadState
    {
        public object Gate { get; } = new();
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

    private readonly ConcurrentDictionary<string, AntiCheatApprovalUploadState> antiCheatApprovalUploads =
        new(StringComparer.Ordinal);

    private readonly object databaseStorageViewerLock = new();
    private DatabaseStorageViewerSnapshot? cachedDatabaseStorageViewer;
    private List<PerformanceModIdentity>? environmentModIdentityCache;
    private string environmentModFingerprintCache = "";
    private readonly string runtimeId = Guid.NewGuid().ToString("N");
    private TcpListener? listener;
    private CancellationTokenSource? listenerCts;
    private Task? listenerTask;
    private Channel<QueuedWebClient>? webRequestQueue;
    private List<Task> webWorkerTasks = new();
    private string lastError = "";
    private DateTime startedUtc;
    private readonly string localAnonymousCsrfToken = RandomToken(24);
    private bool disposed;

    public WebDashboardService(ICoreServerAPI api, BlockLoggerModule module)
    {
        this.api = api;
        this.module = module;
    }

    public WebDashboardConfig Config => config;

    public bool PasswordConfigured =>
        !string.IsNullOrWhiteSpace(config.PasswordSalt) &&
        !string.IsNullOrWhiteSpace(config.PasswordHash);

    public bool LocalAnonymousMode =>
        config.Enabled &&
        IsLoopbackBind(config.BindAddress) &&
        !PasswordConfigured;

    public bool IsRunning
    {
        get
        {
            lock (stateLock) return listener != null;
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
                return webAdminAccounts.Accounts?.Count ?? 0;
        }
    }

    public string LastError
    {
        get
        {
            lock (stateLock) return lastError;
        }
    }

    public BadBlockEntitySnapshot? GetBadBlockEntitySnapshotForMap()
    {
        try { return logIntelligence?.GetBadBlockEntitySnapshot(); }
        catch { return null; }
    }

    public string ListenAddress =>
        $"http://{DisplayBindAddress(config.BindAddress)}:{config.Port}/";

    public string LocalDashboardUrl =>
        $"http://127.0.0.1:{config.Port}/";

    public string PublicDashboardUrl =>
        string.IsNullOrWhiteSpace(config.PublicUrl)
            ? ""
            : config.PublicUrl;

    public void Start()
    {
        LoadConfig();
        LoadSuiteCustomization();
        LoadWebAdminAccounts();
        LoadOptimizationExperiments();
        LoadCompatibilityIgnores();
        if (module.Features.PerformanceCenter) LoadPerformanceCenterStore();
        if (module.Features.OperationsCenter) LoadOperationsCenterStore();

        if (module.Features.LogIntelligence)
        {
            try
            {
                logIntelligence = new LogIntelligenceService(
                    api,
                    () => module.ServerHealth?.GetSnapshot(120, 300));
                logIntelligence.Start(config.BindAddress, config.Port, config.Enabled);
            }
            catch (Exception ex)
            {
                api.Logger.Warning(
                    "[LogIntelligence] startup preflight/log collector disabled after initialization failure: {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
                try { logIntelligence?.Dispose(); } catch { }
                logIntelligence = null;
            }
        }
        else
        {
            api.Logger.Notification("[LogIntelligence] disabled by {0}; no log scan listener or state writer registered.", SuiteFeatureConfig.ConfigFile);
        }

        if (config.SafeDiagnosticsMode && module.Features.PerformanceDiagnostics)
        {
            module.Config.ProfilerEnabled = true;
            module.SaveRuntimeConfig();
        }

        if (module.ModUpdates != null) module.ModUpdates.AllowSameMinorGameUpdates = config.AllowSameMinorGameModUpdates;
        module.ServerHealth?.SetAutomaticIncidentProfilingEnabled(config.AutomaticIncidentProfilingEnabled && !config.SafeDiagnosticsMode);

        RegisterCommands();

        if (module.Features.OperationsCenter)
            api.Event.GameWorldSave += OnGameWorldSave;

        if (performanceMaintenanceListenerId < 0 && (module.Features.PerformanceCenter || module.Features.OperationsCenter))
        {
            performanceMaintenanceListenerId = api.Event.RegisterGameTickListener(
                _ =>
                {
                    if (module.Features.PerformanceCenter)
                    {
                        RunProtectedFeature(
                            "performance-maintenance",
                            "Performance Center Maintenance",
                            "Safe",
                            MaintainPerformanceCenter);
                    }

                    if (module.Features.OperationsCenter)
                    {
                        RunProtectedFeature(
                            "operations-maintenance",
                            "Operations Center Maintenance",
                            "Safe",
                            MaintainOperationsCenter);
                    }
                },
                ex => api.Logger.Warning(
                    "[WebDashboard] performance/operations maintenance callback failed: {0}: {1}",
                    ex.GetType().Name,
                    ex.Message),
                60_000);
        }

        if (config.Enabled)
            RestartListener();
        else
            api.Logger.Notification("[WebDashboard] disabled by saved config. Fresh installs default to local loopback access.");

        // This ModSystem starts before the integrated Login Protection and
        // Animal Limiter systems. Running the self-test synchronously here makes
        // those healthy subsystems look missing. Defer the startup report until
        // game ticking begins, after every server-side ModSystem has initialized.
        api.Event.RegisterCallback(_ => RunStartupSelfTestSafe(), 250);
    }

    private void RunStartupSelfTestSafe()
    {
        if (disposed) return;

        try
        {
            lastSelfTest = BuildServerUtilitiesHealth();
            api.Logger.Notification(
                "[ServerEssentials] Startup self-test: {0}. Build={1}",
                lastSelfTest.Overall,
                BuildId);

            foreach (var check in lastSelfTest.Checks
                         .Where(x => x.Status != "OK"))
            {
                api.Logger.Warning(
                    "[ServerEssentials] Self-test {0}: {1} - {2}",
                    check.Status,
                    check.Name,
                    check.Detail);
            }
        }
        catch (Exception ex)
        {
            api.Logger.Warning(
                "[ServerEssentials] deferred startup self-test failed: {0}: {1}",
                ex.GetType().Name,
                ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        StopListener();
        api.Event.GameWorldSave -= OnGameWorldSave;

        logIntelligence?.Dispose();
        logIntelligence = null;

        if (performanceMaintenanceListenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(performanceMaintenanceListenerId);
            performanceMaintenanceListenerId = -1;
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
        catch { }

        foreach (string uploadId in antiCheatApprovalUploads.Keys.ToArray())
            RemoveAntiCheatApprovalUpload(uploadId);

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
            $"Build: {BuildId} (VS target {TargetGameVersion})",
            $"Enabled: {config.Enabled}",
            $"Running: {IsRunning}",
            $"Bind address: {config.BindAddress}",
            $"Port: {config.Port}",
            $"Listen URL: {ListenAddress}",
            $"Local GUI URL: {LocalDashboardUrl}",
            $"Public GUI URL: {(string.IsNullOrWhiteSpace(PublicDashboardUrl) ? "(not configured)" : PublicDashboardUrl)}",
            $"Admin username: {config.AdminUsername}",
            $"Password configured: {PasswordConfigured}",
            $"Authentication mode: {(LocalAnonymousMode ? "LOCAL LOOPBACK / NO LOGIN" : PasswordConfigured ? "PASSWORD LOGIN" : "PASSWORD REQUIRED FOR REMOTE BIND")}",
            $"Additional panel accounts: {PanelAccountCount}",
            $"Active web sessions: {ActiveSessionCount}",
            $"Web workers: {config.WebWorkerCount}",
            $"Web request queue: {Math.Max(0, Volatile.Read(ref webQueuedRequests))}/{config.WebRequestQueueCapacity} (max wait {config.WebQueueMaxWaitMs}ms)",
            $"Session timeout: {config.SessionTimeoutMinutes} minutes",
            $"Secure cookies: {config.SecureCookies}",
            $"Allowed networks: {(config.AllowedNetworks.Count == 0 ? "any" : string.Join(", ", config.AllowedNetworks))}",
            $"Last error: {(string.IsNullOrWhiteSpace(LastError) ? "(none)" : LastError)}",
            "",
            LocalAnonymousMode
                ? "Fresh local mode is reachable only from this machine. Set a password before changing to a non-loopback bind."
                : "Direct HTTP is plaintext. For Internet exposure, place this listener behind HTTPS (Caddy/nginx/Cloudflare Tunnel or equivalent)."
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
            return IsRunning
                ? LocalAnonymousMode
                    ? $"Local web server enabled at {ListenAddress} without login (loopback only)."
                    : $"Web server enabled at {ListenAddress}"
                : $"Web server could not start: {LastError}";
        }

        StopListener();
        return "Web server disabled.";
    }

    public string SetBindAddress(string? value)
    {
        value = (value ?? "").Trim();
        if (!TryParseBindAddress(value, out var parsedAddress))
            return "Invalid bind address. Use a specific IP, 0.0.0.0, ::, localhost, or loopback.";

        if (!IPAddress.IsLoopback(parsedAddress) && !PasswordConfigured)
            return "Remote/non-loopback binding requires an admin password. Set the web password first.";

        config.BindAddress = value;
        config.Clamp();
        SaveConfig();

        if (config.Enabled) RestartListener();
        return IsRunning || !config.Enabled
            ? $"Bind address set to {config.BindAddress}."
            : $"Bind saved, but listener failed: {LastError}";
    }

    public string SetPort(int value)
    {
        config.Port = Math.Clamp(value, 1, 65535);
        SaveConfig();

        if (config.Enabled) RestartListener();
        return IsRunning || !config.Enabled
            ? $"Web port set to {config.Port}."
            : $"Port saved, but listener failed: {LastError}";
    }

    public string SetPublicUrl(string? value)
    {
        value = (value ?? "").Trim();

        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("none", StringComparison.OrdinalIgnoreCase))
            value = "";

        if (!string.IsNullOrEmpty(value))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp &&
                 uri.Scheme != Uri.UriSchemeHttps))
                return "Public URL must be an absolute http:// or https:// URL, or 'off'.";

            if (value.Length > 512)
                return "Public URL is too long.";

            if (!value.EndsWith("/", StringComparison.Ordinal))
                value += "/";
        }

        config.PublicUrl = value;
        SaveConfig();
        return string.IsNullOrWhiteSpace(config.PublicUrl)
            ? "Public web URL cleared."
            : $"Public web URL set to {config.PublicUrl}";
    }

    public string SetUsername(string? value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "Username cannot be empty.";

        if (value.Length > 64)
            value = value[..64];

        lock (webAccountLock)
        {
            if (FindPanelAccount(value) != null)
                return "That username is already used by an additional panel account.";
        }

        config.AdminUsername = value;
        SaveConfig();
        LogoutAllSessions();
        return $"Web admin username set to {config.AdminUsername}. Existing sessions were invalidated.";
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
        return value
            ? "Secure web cookies enabled. Existing sessions were invalidated."
            : "Secure web cookies disabled. Existing sessions were invalidated.";
    }

    public string SetPassword(string? password)
    {
        password ??= "";
        if (password.Length < 10)
            return "Password must be at least 10 characters.";

        if (password.Length > 256)
            return "Password is too long.";

        SetPasswordHash(password);
        SaveConfig();
        LogoutAllSessions();

        if (config.Enabled)
            RestartListener();

        return "Web admin password updated. Existing sessions were invalidated.";
    }

    public void LogoutAllSessions()
    {
        lock (authLock)
        {
            sessions.Clear();
        }
    }

    private T MeasureFeature<T>(
        string key,
        string name,
        Func<T> action)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            RecordFeatureOverhead(
                key,
                name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private void MeasureFeature(
        string key,
        string name,
        Action action)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            action();
        }
        finally
        {
            RecordFeatureOverhead(
                key,
                name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private void RecordFeatureOverhead(
        string key,
        string name,
        double milliseconds)
    {
        lock (hardeningLock)
        {
            if (!featureOverhead.TryGetValue(key, out var state))
            {
                state = new FeatureOverheadState
                {
                    Key = key,
                    Name = name
                };
                featureOverhead[key] = state;
            }

            state.Calls++;
            state.LastMilliseconds = milliseconds;
            state.TotalMilliseconds += milliseconds;
            state.MaxMilliseconds =
                Math.Max(state.MaxMilliseconds, milliseconds);
        }
    }

    private void RunProtectedFeature(
        string key,
        string name,
        string safety,
        Action action)
    {
        if (IsFeatureCircuitOpen(key, out _))
            return;

        try
        {
            MeasureFeature(key, name, action);
            RecordFeatureSuccess(key, name, safety);
        }
        catch (Exception ex)
        {
            RecordFeatureFailure(key, ex, name, safety);
            api.Logger.Warning(
                "[ServerEssentials] Nonessential feature {0} failed safely: {1}: {2}",
                name,
                ex.GetType().Name,
                ex.Message);
        }
    }

    private bool TryRunProtectedFeature<T>(
        string key,
        string name,
        string safety,
        Func<T> action,
        out T? value,
        out string error)
        where T : class
    {
        value = null;

        if (IsFeatureCircuitOpen(key, out error))
            return false;

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
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private void RecordFeatureSuccess(
        string key,
        string name,
        string safety = "Safe")
    {
        lock (hardeningLock)
        {
            if (!featureBreakers.TryGetValue(key, out var state))
            {
                state = new FeatureBreakerState
                {
                    Key = key,
                    Name = name,
                    Safety = safety
                };
                featureBreakers[key] = state;
            }

            state.Name = name;
            state.Safety = safety;
            state.ConsecutiveFailures = 0;

            if (state.OpenUntilUnixMilliseconds <=
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                state.OpenUntilUnixMilliseconds = 0;
        }
    }

    private void RecordFeatureFailure(
        string key,
        Exception ex,
        string name,
        string safety = "Moderate")
    {
        long now =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (hardeningLock)
        {
            if (!featureBreakers.TryGetValue(key, out var state))
            {
                state = new FeatureBreakerState
                {
                    Key = key,
                    Name = name,
                    Safety = safety
                };
                featureBreakers[key] = state;
            }

            state.Name = name;
            state.Safety = safety;
            state.ConsecutiveFailures++;
            state.TotalFailures++;
            state.LastFailureUnixMilliseconds = now;
            state.LastError =
                $"{ex.GetType().Name}: {ex.Message}";

            if (state.ConsecutiveFailures >= 3)
            {
                state.OpenUntilUnixMilliseconds =
                    now + 5 * 60_000L;

                api.Logger.Warning(
                    "[ServerEssentials] Circuit breaker OPEN for {0} for 5 minutes after {1} consecutive failures. Core BlockLogger continues running.",
                    name,
                    state.ConsecutiveFailures);
            }
        }
    }

    private bool IsFeatureCircuitOpen(
        string key,
        out string message)
    {
        lock (hardeningLock)
        {
            if (!featureBreakers.TryGetValue(key, out var state))
            {
                message = "";
                return false;
            }

            long now =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (state.OpenUntilUnixMilliseconds <= now)
            {
                state.OpenUntilUnixMilliseconds = 0;
                if (state.ConsecutiveFailures >= 3)
                    state.ConsecutiveFailures = 0;
                message = "";
                return false;
            }

            message =
                $"{state.Name} circuit breaker is open until {DateTimeOffset.FromUnixTimeMilliseconds(state.OpenUntilUnixMilliseconds):u}. Last error: {state.LastError}";
            return true;
        }
    }

    private List<FeatureCircuitBreakerStatus>
        SnapshotCircuitBreakers()
    {
        long now =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (hardeningLock)
        {
            return featureBreakers.Values
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new FeatureCircuitBreakerStatus
                {
                    Key = x.Key,
                    Name = x.Name,
                    ConsecutiveFailures = x.ConsecutiveFailures,
                    TotalFailures = x.TotalFailures,
                    Open = x.OpenUntilUnixMilliseconds > now,
                    OpenUntilUnixMilliseconds =
                        x.OpenUntilUnixMilliseconds,
                    LastError = x.LastError,
                    LastFailureUnixMilliseconds =
                        x.LastFailureUnixMilliseconds,
                    Safety = x.Safety
                })
                .ToList();
        }
    }

    private List<FeatureOverheadSnapshot> SnapshotOverhead()
    {
        lock (hardeningLock)
        {
            return featureOverhead.Values
                .OrderByDescending(x => x.MaxMilliseconds)
                .Select(x => new FeatureOverheadSnapshot
                {
                    Key = x.Key,
                    Name = x.Name,
                    Calls = x.Calls,
                    LastMilliseconds = x.LastMilliseconds,
                    AverageMilliseconds = x.Calls <= 0
                        ? 0
                        : x.TotalMilliseconds / x.Calls,
                    MaxMilliseconds = x.MaxMilliseconds
                })
                .ToList();
        }
    }

    private WebRoutePolicyInfo? FindWebRoutePolicy(
        string method,
        string path) =>
        WebRoutePolicies.FirstOrDefault(x =>
            string.Equals(
                x.Method,
                method,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                x.Path,
                path,
                StringComparison.OrdinalIgnoreCase));

    private bool SafeModeBlocksRoute(
        WebRequest request,
        WebRoutePolicyInfo? policy)
    {
        if (!config.SafeDiagnosticsMode)
            return false;

        string path = request.Path;

        if (path == "/api/profiler/enabled" &&
            !GetJsonBool(request.Body, "enabled", false))
            return true;

        if (path == "/api/profiler/start" ||
            path == "/api/tick-debug/slow-start" ||
            path == "/api/tick-debug/incident" ||
            path == "/api/optimizations/tungsten/test" ||
            path == "/api/optimizations/tungsten/toggle" ||
            path == "/api/optimizations/tungsten/all" ||
            path == "/api/optimizations/synergy/test" ||
            path == "/api/optimizations/synergy/toggle" ||
            path == "/api/optimizations/synergy/all")
            return true;

        if (path == "/api/block-migration" && request.Method == "POST") return GetJsonString(request.Body,"operation","") is not ("cancel" or "loaded-disable" or "rules-clear" or "search" or "mapping-advice" or "check-destinations");

        if (path == "/api/optimizations/newfies" && request.Method == "POST")
            return GetJsonString(request.Body, "operation", "set") != "profile-stop";

        if (path == "/api/tick-debug/action")
        {
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            if (action.StartsWith("dump-", StringComparison.Ordinal))
                return true;
        }

        if (path == "/api/optimizations/tungsten/action" ||
            path == "/api/optimizations/synergy/action")
        {
            string action = GetJsonString(
                request.Body,
                "action",
                "").Trim().ToLowerInvariant();

            return action is not (
                "status" or
                "stats" or
                "health" or
                "manifest" or
                "diag-dump");
        }

        return false;
    }

    private bool TryGetMasterDisabledFeature(string path, out string featureName)
    {
        featureName = "";

        if (!module.Features.BlockLogger && path.StartsWith("/api/blocklogger", StringComparison.OrdinalIgnoreCase)) featureName = "BlockLogger";
        else if (!module.Features.Administration && path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)) featureName = "Administration";
        else if (!module.Features.AntiCheat && path.StartsWith("/api/anticheat", StringComparison.OrdinalIgnoreCase)) featureName = "Anti-Cheat";
        else if (!module.Features.AnimalLimiter && path.StartsWith("/api/animals", StringComparison.OrdinalIgnoreCase)) featureName = "Animal Limiter";
        else if (!module.Features.LoginProtection && path.StartsWith("/api/loginprotection", StringComparison.OrdinalIgnoreCase)) featureName = "Login Protection";
        else if (!module.Features.Announcements && path.StartsWith("/api/announcements", StringComparison.OrdinalIgnoreCase)) featureName = "Announcements";
        else if (!module.Features.Reports && path.StartsWith("/api/reports", StringComparison.OrdinalIgnoreCase)) featureName = "Reports";
        else if (!module.Features.PlayerAnalytics && path.StartsWith("/api/player-analytics", StringComparison.OrdinalIgnoreCase)) featureName = "Player Analytics";
        else if (!module.Features.WebConsole && path.StartsWith("/api/console", StringComparison.OrdinalIgnoreCase)) featureName = "Web Console";
        else if (!module.Features.WebMap && path.StartsWith("/api/webmap", StringComparison.OrdinalIgnoreCase)) featureName = "Web Map";
        else if (!module.Features.ModUpdateChecker && path.StartsWith("/api/mod-updates", StringComparison.OrdinalIgnoreCase)) featureName = "Mod Update Checker";
        else if (!module.Features.LogIntelligence && path.StartsWith("/api/log-intelligence", StringComparison.OrdinalIgnoreCase)) featureName = "Log Intelligence";
        else if (!module.Features.OperationsCenter && path.StartsWith("/api/operations", StringComparison.OrdinalIgnoreCase)) featureName = "Operations Center";
        else if (!module.Features.PerformanceCenter && path.StartsWith("/api/performance", StringComparison.OrdinalIgnoreCase)) featureName = "Performance Center";
        else if (!module.Features.PerformanceDiagnostics && (path.StartsWith("/api/profiler", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/tick-debug", StringComparison.OrdinalIgnoreCase))) featureName = "Performance Diagnostics";

        return featureName.Length > 0;
    }

    private bool TryPrepareRouteOperation(
        WebRequest request,
        out RouteOperationLease? lease,
        out string error)
    {
        lease = null;
        error = "";

        var policy = FindWebRoutePolicy(
            request.Method,
            request.Path);

        if (SafeModeBlocksRoute(request, policy))
        {
            error =
                "Safe Diagnostics Mode is enabled. Manual engine/native tick captures and external optimizer changes are locked.";
            return false;
        }

        if (policy == null)
            return true;

        long now =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string operationKey =
            $"{policy.Method}:{policy.Path}";

        lock (hardeningLock)
        {
            if (policy.CooldownSeconds > 0 &&
                operationLastStarted.TryGetValue(
                    operationKey,
                    out long last))
            {
                long remaining =
                    policy.CooldownSeconds * 1000L -
                    (now - last);

                if (remaining > 0)
                {
                    error =
                        $"This action is rate-limited. Try again in {Math.Max(1, (int)Math.Ceiling(remaining / 1000d))} second(s).";
                    return false;
                }
            }
        }

        if (policy.ExclusiveHeavyOperation)
        {
            if (!heavyOperationGate.Wait(0))
            {
                lock (hardeningLock)
                {
                    error =
                        string.IsNullOrWhiteSpace(activeHeavyOperation)
                            ? "Another heavy performance operation is already running."
                            : $"Another heavy performance operation is already running: {activeHeavyOperation}.";
                }
                return false;
            }

            lock (hardeningLock)
                activeHeavyOperation = policy.Path;
        }

        lock (hardeningLock)
            operationLastStarted[operationKey] = now;

        lease = new RouteOperationLease(
            this,
            operationKey,
            policy.Path,
            policy.ExclusiveHeavyOperation);

        return true;
    }

    private void FinishRouteOperation(
        string operationKey,
        string displayName,
        bool releaseHeavy,
        long startedTimestamp)
    {
        RecordFeatureOverhead(
            "route:" + operationKey,
            "Web " + displayName,
            Stopwatch.GetElapsedTime(startedTimestamp)
                .TotalMilliseconds);

        if (!releaseHeavy)
            return;

        lock (hardeningLock)
            activeHeavyOperation = "";

        try { heavyOperationGate.Release(); } catch { }
    }

    private T LoadModConfigSafe<T>(
        string filename,
        Func<T> createDefault,
        string featureKey)
        where T : class
    {
        var migration = ConfigMigrationService.Prepare(api, filename, featureKey);

        try
        {
            var loaded = BlockLogger.Services.SuiteConfigStorage.Load<T>(api, filename);
            if (loaded != null)
            {
                ConfigMigrationService.ApplyIfNeeded(api, loaded, migration);
                return loaded;
            }
            return createDefault();
        }
        catch (ConfigMigrationException)
        {
            // A migration/backup failure must not be reclassified as corrupt JSON or replaced
            // with defaults. Let startup fail this feature safely while preserving the source.
            throw;
        }
        catch (Exception ex)
        {
            string backup = BackupCorruptModConfig(filename);

            api.Logger.Error(
                "[ServerEssentials] Could not load {0}: {1}: {2}. Corrupt/invalid file backup: {3}",
                filename,
                ex.GetType().Name,
                ex.Message,
                backup);

            RecordFeatureFailure(
                featureKey,
                ex,
                "Safe config recovery");

            return createDefault();
        }
    }

    private string BackupCorruptModConfig(string filename)
    {
        try
        {
            string source = SuiteConfigStorage.GetConfigPath(api, filename);
            if (!File.Exists(source))
                source = SuiteConfigStorage.FindLegacyRootFile(api, filename) ?? "";

            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                return "(source file not found; fresh defaults created)";

            string stem = Path.GetFileNameWithoutExtension(filename);
            string extension = Path.GetExtension(filename);
            string target = Path.Combine(
                SuiteConfigStorage.GetBackupRoot(api),
                $"{stem}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}");

            File.Move(source, target, overwrite: false);
            return target;
        }
        catch (Exception ex)
        {
            return $"(backup failed: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private void LoadConfig()
    {
        var loaded = LoadModConfigSafe(
            ConfigFile,
            () => new WebDashboardConfig(),
            "web-config");

        loaded.Clamp();
        config = loaded;

        if (!string.IsNullOrEmpty(config.Password))
        {
            SetPasswordHash(config.Password);
            config.Password = "";
            api.Logger.Notification(
                "[WebDashboard] consumed the one-time Password field from {0} and replaced it with a PBKDF2 hash.",
                ConfigFile);
        }

        SaveConfig();
    }

    private void SaveConfig()
    {
        config.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, config, ConfigFile);
    }

    private void LoadSuiteCustomization()
    {
        suiteCustomization = SuiteCustomizationConfig.Load(api);
    }

    private void SaveSuiteCustomization()
    {
        suiteCustomization.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, suiteCustomization, SuiteCustomizationConfig.ConfigFile);
    }

    private void LoadCompatibilityIgnores()
    {
        var loaded = LoadModConfigSafe(
            CompatibilityIgnoresFile,
            () => new CompatibilityIgnoreStore(),
            "compatibility-ignores");
        loaded.Ignores ??= new List<CompatibilityIgnoreRecord>();
        compatibilityIgnores = loaded;
        SaveCompatibilityIgnores();
    }

    private void SaveCompatibilityIgnores()
    {
        compatibilityIgnores.Ignores ??= new List<CompatibilityIgnoreRecord>();
        BlockLogger.Services.SuiteConfigStorage.Store(api, compatibilityIgnores, CompatibilityIgnoresFile);
    }

    private static string CompatibilityFingerprint(string category, string title, string detail)
    {
        string raw = $"{category.Trim()}\n{title.Trim()}\n{detail.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private void LoadWebAdminAccounts()
    {
        var loaded = LoadModConfigSafe(
            AdminAccountsFile,
            () => new WebAdminAccountStore(),
            "web-admin-accounts");

        loaded.Accounts ??= new List<WebAdminAccountRecord>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<WebAdminAccountRecord>();
        foreach (var account in loaded.Accounts)
        {
            if (account == null) continue;
            account.Username = (account.Username ?? "").Trim();
            if (!IsValidPanelUsername(account.Username)) continue;
            if (string.Equals(account.Username, config.AdminUsername, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(account.Username)) continue;
            account.Id = string.IsNullOrWhiteSpace(account.Id) ? Guid.NewGuid().ToString("N") : account.Id.Trim();
            account.Role = NormalizePanelRole(account.Role);
            account.PasswordIterations = Math.Clamp(account.PasswordIterations, 100_000, 1_000_000);
            if (account.CreatedUnixMilliseconds <= 0)
                account.CreatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            cleaned.Add(account);
            if (cleaned.Count >= MaxWebAdminAccounts) break;
        }

        loaded.Accounts = cleaned;
        webAdminAccounts = loaded;
        SaveWebAdminAccounts();
    }

    private void SaveWebAdminAccounts()
    {
        webAdminAccounts.Accounts ??= new List<WebAdminAccountRecord>();
        if (webAdminAccounts.Accounts.Count > MaxWebAdminAccounts)
            webAdminAccounts.Accounts = webAdminAccounts.Accounts.Take(MaxWebAdminAccounts).ToList();
        BlockLogger.Services.SuiteConfigStorage.Store(api, webAdminAccounts, AdminAccountsFile);
    }

    private static bool IsValidPanelUsername(string? username)
    {
        username = (username ?? "").Trim();
        if (username.Length < 3 || username.Length > 64) return false;
        foreach (char c in username)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
                return false;
        }
        return true;
    }

    private static string NormalizePanelRole(string? role) =>
        string.Equals(role, "ReadOnly", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, "Read Only", StringComparison.OrdinalIgnoreCase)
            ? "ReadOnly"
            : "Administrator";

    private static void SetAccountPasswordHash(WebAdminAccountRecord account, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            account.PasswordIterations,
            HashAlgorithmName.SHA256,
            32);
        account.PasswordSalt = Convert.ToBase64String(salt);
        account.PasswordHash = Convert.ToBase64String(hash);
        account.LastPasswordChangedUnixMilliseconds =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static bool VerifyAccountPassword(WebAdminAccountRecord account, string password)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(account.PasswordSalt);
            byte[] expected = Convert.FromBase64String(account.PasswordHash);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                account.PasswordIterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private WebAdminAccountRecord? FindPanelAccount(string? username) =>
        webAdminAccounts.Accounts.FirstOrDefault(x =>
            string.Equals(x.Username, (username ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private void LogoutSessionsForUsername(string username)
    {
        lock (authLock)
        {
            foreach (string key in sessions
                         .Where(x => string.Equals(x.Value.Username, username, StringComparison.OrdinalIgnoreCase))
                         .Select(x => x.Key)
                         .ToList())
                sessions.Remove(key);
        }
    }

    private object BuildWebAdminAccountsPayload()
    {
        lock (webAccountLock)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var accounts = webAdminAccounts.Accounts
                .OrderBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    x.Id,
                    x.Username,
                    x.Role,
                    x.Enabled,
                    x.CreatedUnixMilliseconds,
                    x.LastLoginUnixMilliseconds,
                    x.LastLoginRemote,
                    x.LastPasswordChangedUnixMilliseconds,
                    locked = x.LockedUntilUnixMilliseconds > now,
                    x.LockedUntilUnixMilliseconds
                })
                .ToList();

            return new
            {
                owner = new
                {
                    username = config.AdminUsername,
                    role = "Owner",
                    enabled = true,
                    passwordConfigured = PasswordConfigured
                },
                accounts,
                count = accounts.Count,
                maximum = MaxWebAdminAccounts
            };
        }
    }

    private void LoadOptimizationExperiments()
    {
        optimizationExperiments = LoadModConfigSafe(
            OptimizationExperimentsFile,
            () => new OptimizationExperimentStore(),
            "optimization-store");
        optimizationExperiments.SchemaVersion = PersistentSchemaVersion;

        optimizationExperiments.History ??= new List<OptimizationExperiment>();
        if (optimizationExperiments.History.Count > config.MaxOptimizationExperiments)
            optimizationExperiments.History =
                optimizationExperiments.History
                    .OrderByDescending(x => x.CreatedUnixMilliseconds)
                    .Take(config.MaxOptimizationExperiments)
                    .ToList();

        SaveOptimizationExperiments();
    }

    private void SaveOptimizationExperiments()
    {
        optimizationExperiments.History ??= new List<OptimizationExperiment>();
        if (optimizationExperiments.History.Count > config.MaxOptimizationExperiments)
            optimizationExperiments.History =
                optimizationExperiments.History
                    .OrderByDescending(x => x.CreatedUnixMilliseconds)
                    .Take(config.MaxOptimizationExperiments)
                    .ToList();

        BlockLogger.Services.SuiteConfigStorage.Store(api, 
            optimizationExperiments,
            OptimizationExperimentsFile);
    }


    private void LoadPerformanceCenterStore()
    {
        performanceCenterStore = LoadModConfigSafe(
            PerformanceCenterFile,
            () => new PerformanceCenterStore(),
            "performance-store");
        performanceCenterStore.SchemaVersion = PersistentSchemaVersion;

        performanceCenterStore.Incidents ??= new List<PerformanceIncidentArchive>();
        performanceCenterStore.EntityScans ??= new List<PerformanceEntityScanSnapshot>();
        performanceCenterStore.EnvironmentHistory ??= new List<PerformanceEnvironmentChange>();

        lastPerformanceEntityScan =
            performanceCenterStore.EntityScans
                .OrderByDescending(x => x.UnixMilliseconds)
                .FirstOrDefault();

        TrimPerformanceCenterStore();
        SavePerformanceCenterStore();
    }

    private void SavePerformanceCenterStore()
    {
        if (!module.Features.PerformanceCenter)
            return;

        TrimPerformanceCenterStore();
        BlockLogger.Services.SuiteConfigStorage.Store(api, performanceCenterStore, PerformanceCenterFile);
    }

    private void TrimPerformanceCenterStore()
    {
        performanceCenterStore.Incidents =
            (performanceCenterStore.Incidents ?? new List<PerformanceIncidentArchive>())
                .OrderByDescending(x => x.StartUnixMilliseconds)
                .Take(config.MaxArchivedIncidents)
                .ToList();

        performanceCenterStore.EntityScans =
            (performanceCenterStore.EntityScans ?? new List<PerformanceEntityScanSnapshot>())
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(config.MaxEntityScanHistory)
                .ToList();

        performanceCenterStore.EnvironmentHistory =
            (performanceCenterStore.EnvironmentHistory ?? new List<PerformanceEnvironmentChange>())
                .OrderByDescending(x => x.DetectedUnixMilliseconds)
                .Take(config.MaxEnvironmentChanges)
                .ToList();
    }

    private void LoadOperationsCenterStore()
    {
        operationsCenterStore = LoadModConfigSafe(
            OperationsCenterFile,
            () => new OperationsCenterStore(),
            "operations-store");
        operationsCenterStore.SchemaVersion = PersistentSchemaVersion;

        operationsCenterStore.Alerts ??= new List<ServerAlertRecord>();
        operationsCenterStore.Actions ??= new List<ServerActionRecord>();
        operationsCenterStore.Audit ??= new List<WebAuditRecord>();
        operationsCenterStore.PlayerLoad ??= new List<PlayerLoadBucket>();
        operationsCenterStore.ConfigSnapshots ??= new List<ConfigSnapshotRecord>();
        operationsCenterStore.WorldSaves ??= new List<WorldSaveObservation>();
        operationsCenterStore.NetworkSamples ??= new List<NetworkPerformanceSample>();
        operationsCenterStore.PerformanceSnapshots ??= new List<PerformanceSnapshotRecord>();
        operationsCenterStore.MemoryInspectorSnapshots ??= new List<MemoryInspectorSnapshot>();

        EnsurePlayerLoadBuckets();
        SanitizeLegacyPlayerLoadHistory();
        TrimOperationsCenterStore();
        SaveOperationsCenterStore();
    }

    private void SaveOperationsCenterStore()
    {
        if (!module.Features.OperationsCenter)
            return;

        lock (operationsLock)
        {
            TrimOperationsCenterStore();
            BlockLogger.Services.SuiteConfigStorage.Store(api, 
                operationsCenterStore,
                OperationsCenterFile);
        }
    }

    private void TrimOperationsCenterStore()
    {
        operationsCenterStore.Alerts =
            (operationsCenterStore.Alerts ?? new List<ServerAlertRecord>())
                .OrderByDescending(x => x.LastSeenUnixMilliseconds)
                .Take(config.MaxAlerts)
                .ToList();

        operationsCenterStore.Actions =
            (operationsCenterStore.Actions ?? new List<ServerActionRecord>())
                .OrderByDescending(x => x.UpdatedUnixMilliseconds)
                .Take(config.MaxActions)
                .ToList();

        operationsCenterStore.Audit =
            (operationsCenterStore.Audit ?? new List<WebAuditRecord>())
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(config.MaxWebAuditEntries)
                .ToList();
        foreach (var record in operationsCenterStore.Audit)
        {
            record.Actor = BoundAuditField(record.Actor, 128); record.Remote = BoundAuditField(record.Remote, 128);
            record.Action = BoundAuditField(record.Action, 128); record.Detail = BoundAuditField(record.Detail, 2048);
            record.Result = BoundAuditField(record.Result, 512);
        }

        operationsCenterStore.ConfigSnapshots =
            (operationsCenterStore.ConfigSnapshots ?? new List<ConfigSnapshotRecord>())
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(config.MaxConfigSnapshots)
                .ToList();

        operationsCenterStore.WorldSaves =
            (operationsCenterStore.WorldSaves ?? new List<WorldSaveObservation>())
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(config.MaxWorldSaveObservations)
                .ToList();

        operationsCenterStore.NetworkSamples =
            (operationsCenterStore.NetworkSamples ?? new List<NetworkPerformanceSample>())
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(config.MaxNetworkSamples)
                .ToList();

        operationsCenterStore.PerformanceSnapshots =
            (operationsCenterStore.PerformanceSnapshots ?? new List<PerformanceSnapshotRecord>())
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(config.MaxPerformanceSnapshots)
                .ToList();

        operationsCenterStore.MemoryInspectorSnapshots =
            (operationsCenterStore.MemoryInspectorSnapshots ?? new List<MemoryInspectorSnapshot>())
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(16)
                .ToList();
    }

    private void EnsurePlayerLoadBuckets()
    {
        if (operationsCenterStore.PlayerLoad.Count > 0)
            return;

        operationsCenterStore.PlayerLoad = new List<PlayerLoadBucket>
        {
            new() { Label = "0-5", MinimumPlayers = 0, MaximumPlayers = 5 },
            new() { Label = "6-10", MinimumPlayers = 6, MaximumPlayers = 10 },
            new() { Label = "11-15", MinimumPlayers = 11, MaximumPlayers = 15 },
            new() { Label = "16-20", MinimumPlayers = 16, MaximumPlayers = 20 },
            new() { Label = "21-30", MinimumPlayers = 21, MaximumPlayers = 30 },
            new() { Label = "31+", MinimumPlayers = 31, MaximumPlayers = int.MaxValue }
        };
    }

    private void SanitizeLegacyPlayerLoadHistory()
    {
        foreach (var bucket in operationsCenterStore.PlayerLoad)
        {
            if (bucket.LegacyLifecycleCleanupApplied)
                continue;

            bucket.LegacyLifecycleCleanupApplied = true;

            // Older builds stored a raw cumulative mean. A single startup/suspend
            // interval could therefore account for nearly the entire lifetime delay
            // total (for example a 259-second first sample in the 0-5 bucket). We
            // only remove a legacy maximum when it is unmistakably a lifecycle
            // discontinuity and dominates the accumulated delay.
            if (bucket.Samples <= 0 ||
                bucket.DelayMaxMs < PlayerLoadLifecycleDiscontinuityMs ||
                bucket.DelayTotalMs <= 0 ||
                bucket.DelayMaxMs < bucket.DelayTotalMs * 0.25d)
                continue;

            long oldSamples = bucket.Samples;
            double oldCpuAverage = oldSamples <= 0
                ? 0
                : bucket.CpuTotalPercent / oldSamples;
            double excludedDelay = bucket.DelayMaxMs;

            bucket.Samples = Math.Max(0, bucket.Samples - 1);
            bucket.DelayTotalMs = Math.Max(0, bucket.DelayTotalMs - excludedDelay);
            bucket.CpuTotalPercent = oldCpuAverage * bucket.Samples;
            if (bucket.StallSamples > 0)
                bucket.StallSamples--;

            bucket.ExcludedLifecycleSamples++;
            bucket.ExcludedLifecycleMaxDelayMs = Math.Max(
                bucket.ExcludedLifecycleMaxDelayMs,
                excludedDelay);

            // The persisted aggregate does not contain the second-highest delay,
            // so do not invent one. Normal runtime samples will repopulate this max.
            bucket.DelayMaxMs = 0;
        }
    }

    private void RecordWebAudit(
        string action,
        string detail,
        string result,
        string remote = "",
        string actor = "")
    {
        if (!module.Features.OperationsCenter)
            return;

        lock (operationsLock)
        {
            operationsCenterStore.Audit.Insert(
                0,
                new WebAuditRecord
                {
                    UnixMilliseconds =
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Actor = string.IsNullOrWhiteSpace(actor)
                        ? BoundAuditField(config.AdminUsername, 128)
                        : BoundAuditField(actor, 128),
                    Remote = BoundAuditField(remote, 128),
                    Action = BoundAuditField(action, 128),
                    Detail = BoundAuditField(detail, 2048),
                    Result = BoundAuditField(result, 512)
                });

            if (operationsCenterStore.Audit.Count > config.MaxWebAuditEntries)
                operationsCenterStore.Audit.RemoveRange(
                    config.MaxWebAuditEntries,
                    operationsCenterStore.Audit.Count -
                    config.MaxWebAuditEntries);
        }
    }

    internal static string BoundAuditField(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string suffix = value.Length > max ? $" [truncated; originalLength={value.Length}]" : "";
        int length = Math.Min(value.Length, Math.Max(0, max - suffix.Length));
        var bounded = new StringBuilder(max);
        for (int i = 0; i < length; i++) bounded.Append(char.IsControl(value[i]) ? ' ' : value[i]);
        bounded.Append(suffix);
        return bounded.ToString();
    }

    private void OnGameWorldSave()
    {
        if (!module.Features.OperationsCenter)
            return;

        long started = Stopwatch.GetTimestamp();
        long unix =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int players = api.World.AllOnlinePlayers?.Length ?? 0;
        double delay =
            module.ServerHealth?.Current.MainThreadDelayMs ?? 0;

        // GameWorldSave fires immediately before world data is saved. A tiny
        // callback runs once the server main thread is servicing callbacks
        // again. The elapsed value therefore represents the observed
        // main-thread save window, not pure disk I/O time.
        api.Event.RegisterCallback(
            _ => RunProtectedFeature(
                "world-save-observer",
                "World Save Observer",
                "Safe",
                () =>
                {
                    double elapsed =
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                    string severity =
                        elapsed >= 5000 ? "Critical" :
                        elapsed >= 2000 ? "High" :
                        elapsed >= 750 ? "Watch" :
                        "Info";

                    operationsCenterStore.WorldSaves.Insert(
                        0,
                        new WorldSaveObservation
                        {
                            UnixMilliseconds = unix,
                            ObservedMainThreadWindowMs = elapsed,
                            Players = players,
                            DelayAtStartMs = delay,
                            Severity = severity,
                            Note =
                                "Observed from GameWorldSave until the server main thread serviced the post-save callback. This includes save work plus normal callback/tick scheduling and is not pure disk-write time."
                        });

                    if (elapsed >= 2000)
                    {
                        UpsertAlert(
                            "world-save-stall",
                            elapsed >= 5000 ? "Critical" : "High",
                            "WorldSave",
                            $"World save occupied the main thread for about {elapsed:0}ms",
                            "A real GameWorldSave event preceded the observed main-thread window.",
                            "Open Performance Center and compare this timestamp with lag incidents. If they repeatedly align, investigate world/save storage performance.",
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }

                    SaveOperationsCenterStore();
                }),
            1);
    }

    private void SampleServerNetwork(int players)
    {
        long networkNow =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long sentTotal = api.Server.TotalSentBytes;
        long receivedTotal = api.Server.TotalReceivedBytes;

        var previousNetwork = operationsCenterStore.NetworkSamples
            .OrderByDescending(x => x.UnixMilliseconds)
            .FirstOrDefault();

        double elapsedNetworkSeconds =
            previousNetwork == null
                ? 0
                : Math.Max(
                    1,
                    (networkNow - previousNetwork.UnixMilliseconds) / 1000d);

        double sentRate =
            previousNetwork == null
                ? 0
                : Math.Max(
                    0,
                    sentTotal - previousNetwork.TotalSentBytes) /
                  elapsedNetworkSeconds;

        double receivedRate =
            previousNetwork == null
                ? 0
                : Math.Max(
                    0,
                    receivedTotal - previousNetwork.TotalReceivedBytes) /
                  elapsedNetworkSeconds;

        operationsCenterStore.NetworkSamples.Insert(
            0,
            new NetworkPerformanceSample
            {
                UnixMilliseconds = networkNow,
                TotalSentBytes = sentTotal,
                TotalReceivedBytes = receivedTotal,
                SentBytesPerSecond = sentRate,
                ReceivedBytesPerSecond = receivedRate,
                Players = players
            });
    }

    private void MaintainOperationsCenter()
    {
        if (!module.Features.OperationsCenter) return;
        long operationNow =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (lastOperationsMaintenanceUnixMilliseconds > 0 &&
            operationNow - lastOperationsMaintenanceUnixMilliseconds < 55_000)
            return;

        lastOperationsMaintenanceUnixMilliseconds = operationNow;

        var healthService = module.ServerHealth;
        if (healthService == null || !healthService.Enabled)
            return;

        var snapshot = healthService.GetSnapshot(300, 1800);
        if (snapshot.Samples.Count == 0)
            return;

        EnsurePlayerLoadBuckets();

        var current = snapshot.Current;

        RunProtectedFeature(
            "network-sampling",
            "Server Network Sampling",
            "Safe",
            () => SampleServerNetwork(current.Players));

        var bucket = operationsCenterStore.PlayerLoad.FirstOrDefault(
            x => current.Players >= x.MinimumPlayers &&
                 current.Players <= x.MaximumPlayers);

        if (bucket != null && current.UnixMilliseconds > lastPlayerLoadSampleUnixMilliseconds)
        {
            lastPlayerLoadSampleUnixMilliseconds = current.UnixMilliseconds;

            bool freshHealthSample =
                current.UnixMilliseconds > 0 &&
                Math.Abs(operationNow - current.UnixMilliseconds) <= PlayerLoadFreshSampleWindowMs;
            bool lifecycleDiscontinuity =
                current.MainThreadDelayMs >= PlayerLoadLifecycleDiscontinuityMs;

            if (lifecycleDiscontinuity)
            {
                bucket.ExcludedLifecycleSamples++;
                bucket.ExcludedLifecycleMaxDelayMs = Math.Max(
                    bucket.ExcludedLifecycleMaxDelayMs,
                    current.MainThreadDelayMs);
            }
            else if (freshHealthSample)
            {
                bucket.Samples++;
                bucket.DelayTotalMs += current.MainThreadDelayMs;
                bucket.CpuTotalPercent += current.CpuPercent;
                bucket.DelayMaxMs = Math.Max(
                    bucket.DelayMaxMs,
                    current.MainThreadDelayMs);
                bucket.CpuMaxPercent = Math.Max(
                    bucket.CpuMaxPercent,
                    current.CpuPercent);

                if (current.MainThreadDelayMs >= 100)
                    bucket.StallSamples++;
            }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var memory = BuildMemoryGcAnalysis(snapshot);

        if (snapshot.SmartAnalysis.HealthScore < 50)
        {
            UpsertAlert(
                "health-critical",
                "Critical",
                "Overall",
                $"Server health score is {snapshot.SmartAnalysis.HealthScore}/100",
                snapshot.SmartAnalysis.PrimaryFinding,
                "Open Performance Center and investigate the highest-confidence Smart Findings.",
                now);
        }

        if (snapshot.DiagnosticSummary.WorstDelayMs >= 500)
        {
            UpsertAlert(
                "delay-500",
                "Critical",
                "MainThread",
                $"Severe main-thread delay reached {snapshot.DiagnosticSummary.WorstDelayMs:0}ms",
                snapshot.DiagnosticSummary.Summary,
                "Open the latest lag incident and run a 10s/30s engine profile if the problem repeats.",
                now);
        }
        else if (snapshot.DiagnosticSummary.StallCount >= 3)
        {
            UpsertAlert(
                "repeat-stalls",
                "High",
                "MainThread",
                $"{snapshot.DiagnosticSummary.StallCount} slow/stall samples were recorded in the rolling diagnostic window",
                snapshot.DiagnosticSummary.Summary,
                "Open the Unified Performance Timeline and inspect whether CPU, GC, entities, chunks, or storage moved with the stalls.",
                now);
        }

        if (snapshot.DiagnosticSummary.BlockLoggerDroppedIncrease > 0)
        {
            UpsertAlert(
                "blocklogger-drops",
                "Critical",
                "Storage",
                "BlockLogger dropped audit events",
                $"Dropped events increased by {snapshot.DiagnosticSummary.BlockLoggerDroppedIncrease:N0} in the diagnostic window.",
                "Treat storage/backpressure as urgent. Check writer latency, queue depth, and disk performance before increasing logging load.",
                now);
        }
        else if (snapshot.DiagnosticSummary.MaxBlockLoggerQueue >=
                 Math.Max(1000, module.Config.MaxQueuedEvents * 3 / 4))
        {
            UpsertAlert(
                "blocklogger-queue",
                "High",
                "Storage",
                "BlockLogger queue pressure is high",
                $"Queue reached {snapshot.DiagnosticSummary.MaxBlockLoggerQueue:N0}; normal capacity is {module.Config.MaxQueuedEvents:N0} with an emergency reserve of {module.Config.EmergencyOverflowCapacity:N0}.",
                "Inspect Storage/BlockLogger pressure and disk throughput. The emergency reserve protects short stalls, but sustained pressure can still exhaust it.",
                now);
        }

        if (memory.Available &&
            (memory.State == "Investigate" ||
             memory.State == "Critical"))
        {
            UpsertAlert(
                "memory-growth",
                memory.State == "Critical" ? "Critical" : "High",
                "Memory",
                memory.Finding,
                $"Heap slope {memory.HeapSlopeMbPerMinute:+0.0;-0.0;0} MB/min; estimated post-GC floor growth {memory.EstimatedPostGcFloorGrowthMb:+0;-0;0} MB.",
                memory.Recommendation,
                now);
        }

        var growth = BuildEntityGrowthRows();
        var runaway = growth.FirstOrDefault(x =>
            x.Severity is "Critical" or "High");

        if (runaway != null)
        {
            UpsertAlert(
                $"entity-growth:{runaway.Code}",
                runaway.Severity,
                "Entities",
                $"Possible runaway entity growth: {runaway.Code}",
                runaway.Reason,
                "Open Performance Center, inspect the entity type and hottest chunks, then check spawn/config rules before culling.",
                now);
        }

        var latestRegression =
            performanceCenterStore.EnvironmentHistory
                .OrderByDescending(x => x.DetectedUnixMilliseconds)
                .FirstOrDefault(x => x.Result == "Regressed");

        if (latestRegression != null &&
            latestRegression.DetectedUnixMilliseconds >= now - 7 * 24 * 60 * 60_000L)
        {
            UpsertAlert(
                $"environment-regression:{latestRegression.AfterFingerprint}",
                "High",
                "Compatibility",
                "Performance regressed after a mod/version environment change",
                string.Join(", ", latestRegression.ChangedMods.Take(6)) +
                $" | regression score {latestRegression.RegressionScore:+0.0;-0.0;0.0}%",
                "Review the changed mods, Harmony overlap report, and manual profiler attribution before reverting or disabling anything.",
                now);
        }

        foreach (var alert in operationsCenterStore.Alerts
                     .Where(x =>
                         x.Status == "Active" &&
                         now - x.LastSeenUnixMilliseconds > 5 * 60_000L))
        {
            alert.Status = "Resolved";
        }

        RebuildActionQueue(snapshot, memory);
        SaveOperationsCenterStore();
    }

    private void UpsertAlert(
        string key,
        string severity,
        string category,
        string title,
        string evidence,
        string recommendedAction,
        long now)
    {
        var alert = operationsCenterStore.Alerts.FirstOrDefault(
            x => string.Equals(
                x.Key,
                key,
                StringComparison.OrdinalIgnoreCase));

        bool notify = false;

        if (alert == null)
        {
            alert = new ServerAlertRecord
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
            operationsCenterStore.Alerts.Insert(0, alert);
            notify = severity is "Critical" or "High";
        }
        else
        {
            bool wasResolved = alert.Status == "Resolved";

            alert.LastSeenUnixMilliseconds = now;
            alert.Severity = severity;
            alert.Category = category;
            alert.Title = title;
            alert.Evidence = evidence;
            alert.RecommendedAction = recommendedAction;
            alert.Occurrences++;

            if (wasResolved)
            {
                alert.Status = "Active";
                notify = severity is "Critical" or "High";
            }
        }

        if (notify)
            NotifyAdminAlert(alert);
    }

    private void NotifyAdminAlert(ServerAlertRecord alert)
    {
        api.Logger.Warning(
            "[ServerEssentials Alert] {0} / {1}: {2} | {3}",
            alert.Severity,
            alert.Category,
            alert.Title,
            alert.Evidence);

        // Use Vintage Story's built-in Info log chat group.  This group is one
        // of the engine's default chat groups, so the client HUD already has
        // the corresponding rich-text element before any server message can
        // arrive.  Creating a custom group at runtime caused a 1.22.7 client
        // crash when an alert packet reached the HUD before that custom tab
        // had been composed.
        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (!player.HasPrivilege("controlserver")) continue;

            api.SendMessage(
                player,
                GlobalConstants.InfoLogChatGroup,
                $"[Server Essentials] {alert.Severity}: {alert.Title}",
                EnumChatType.Notification);
        }
    }

    private void RebuildActionQueue(
        ServerHealthSnapshot snapshot,
        MemoryGcAnalysis memory)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var desired = new List<ServerActionRecord>();

        foreach (var alert in operationsCenterStore.Alerts
                     .Where(x => x.Status is "Active" or "Investigating")
                     .OrderByDescending(x => SeverityRankForOperations(x.Severity))
                     .Take(20))
        {
            desired.Add(
                new ServerActionRecord
                {
                    Id = $"alert:{alert.Id}",
                    SourceKey = alert.Key,
                    UpdatedUnixMilliseconds = now,
                    Priority = alert.Severity == "Critical" ? "High" : alert.Severity,
                    Category = alert.Category,
                    Title = alert.RecommendedAction,
                    Reason = alert.Title + " — " + alert.Evidence,
                    TargetSection = alert.Category switch
                    {
                        "Memory" => "memory",
                        "Entities" => "performance",
                        "Compatibility" => "performance",
                        "Storage" => "performance",
                        "WorldSave" => "actionCenter",
                        "Network" => "actionCenter",
                        _ => "profiler"
                    },
                    Status = "Open"
                });
        }

        foreach (var signal in snapshot.SmartAnalysis.Signals.Take(8))
        {
            string sourceKey = $"smart:{signal.Category}:{signal.Title}";
            desired.Add(
                new ServerActionRecord
                {
                    Id = sourceKey,
                    SourceKey = sourceKey,
                    UpdatedUnixMilliseconds = now,
                    Priority = signal.Severity is "Critical" or "High"
                        ? "High"
                        : "Medium",
                    Category = signal.Category,
                    Title = signal.NextAction,
                    Reason = signal.Title + " — " + signal.Evidence,
                    TargetSection = signal.Category switch
                    {
                        "Memory" or "GC" => "memory",
                        "Entities" or "Chunks" => "performance",
                        "BlockLoggerIO" => "performance",
                        _ => "profiler"
                    },
                    Status = "Open"
                });
        }

        if (memory.Available &&
            memory.HeapSlopeMbPerMinute >= 25 &&
            desired.All(x => x.SourceKey != "memory-trend"))
        {
            desired.Add(
                new ServerActionRecord
                {
                    Id = "memory-trend",
                    SourceKey = "memory-trend",
                    UpdatedUnixMilliseconds = now,
                    Priority = memory.State == "Critical" ? "High" : "Medium",
                    Category = "Memory",
                    Title = "Inspect Memory & GC Center",
                    Reason = memory.Finding,
                    TargetSection = "memory",
                    Status = "Open"
                });
        }

        var oldStatus = operationsCenterStore.Actions.ToDictionary(
            x => x.SourceKey,
            x => x.Status,
            StringComparer.OrdinalIgnoreCase);

        foreach (var action in desired)
        {
            if (oldStatus.TryGetValue(action.SourceKey, out string? status))
                action.Status = status;
        }

        operationsCenterStore.Actions = desired
            .GroupBy(x => x.SourceKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x =>
                x.Priority == "High" ? 3 :
                x.Priority == "Medium" ? 2 : 1)
            .Take(config.MaxActions)
            .ToList();
    }

    private static int SeverityRankForOperations(string severity) =>
        severity switch
        {
            "Critical" => 4,
            "High" => 3,
            "Watch" => 2,
            "Medium" => 2,
            "Low" => 1,
            _ => 0
        };

    private MemoryInspectorSnapshot CaptureMemoryInspectorSnapshot(string label, ServerHealthSnapshot? health = null)
    {
        health ??= module.ServerHealth?.GetSnapshot(600, 1800);
        var current = health?.Current ?? new ServerHealthSample();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        const double mb = 1048576d;

        var structures = new List<MemoryInspectorTrackedStructure>();
        lock (operationsLock)
        {
            void Add(string category, string name, string runtimeType, long count, string note = "") =>
                structures.Add(new MemoryInspectorTrackedStructure
                {
                    Category = category,
                    Name = name,
                    RuntimeType = runtimeType,
                    Count = Math.Max(0, count),
                    Note = note
                });

            Add("Operations", "Admin audit records", typeof(WebAuditRecord).FullName ?? nameof(WebAuditRecord), operationsCenterStore.Audit?.Count ?? 0);
            Add("Operations", "Active/retained alerts", typeof(ServerAlertRecord).FullName ?? nameof(ServerAlertRecord), operationsCenterStore.Alerts?.Count ?? 0);
            Add("Operations", "Action-center records", typeof(ServerActionRecord).FullName ?? nameof(ServerActionRecord), operationsCenterStore.Actions?.Count ?? 0);
            Add("Operations", "Performance snapshots", typeof(PerformanceSnapshotRecord).FullName ?? nameof(PerformanceSnapshotRecord), operationsCenterStore.PerformanceSnapshots?.Count ?? 0);
            Add("Operations", "Configuration snapshots", typeof(ConfigSnapshotRecord).FullName ?? nameof(ConfigSnapshotRecord), operationsCenterStore.ConfigSnapshots?.Count ?? 0);
            Add("Operations", "Network samples", typeof(NetworkPerformanceSample).FullName ?? nameof(NetworkPerformanceSample), operationsCenterStore.NetworkSamples?.Count ?? 0);
            Add("Performance", "Archived lag incidents", typeof(PerformanceIncidentArchive).FullName ?? nameof(PerformanceIncidentArchive), performanceCenterStore.Incidents?.Count ?? 0);
            Add("Performance", "Entity scan snapshots", typeof(PerformanceEntityScanSnapshot).FullName ?? nameof(PerformanceEntityScanSnapshot), performanceCenterStore.EntityScans?.Count ?? 0);
            Add("Performance", "Environment changes", typeof(PerformanceEnvironmentChange).FullName ?? nameof(PerformanceEnvironmentChange), performanceCenterStore.EnvironmentHistory?.Count ?? 0);
            Add("Memory", "Saved MemLeakInspector snapshots", typeof(MemoryInspectorSnapshot).FullName ?? nameof(MemoryInspectorSnapshot), operationsCenterStore.MemoryInspectorSnapshots?.Count ?? 0);
        }

        structures.Add(new MemoryInspectorTrackedStructure
        {
            Category = "Runtime",
            Name = "Server Health samples in current view",
            RuntimeType = typeof(ServerHealthSample).FullName ?? nameof(ServerHealthSample),
            Count = health?.Samples?.Count ?? 0,
            Note = "Dashboard diagnostic sample objects, not the full Server Health retention buffer."
        });
        structures.Add(new MemoryInspectorTrackedStructure
        {
            Category = "Runtime",
            Name = "Loaded managed assemblies",
            RuntimeType = typeof(System.Reflection.Assembly).FullName ?? "System.Reflection.Assembly",
            Count = AppDomain.CurrentDomain.GetAssemblies().LongLength,
            Note = "Assembly count is context only; it does not imply retained instances."
        });

        double managed = current.ManagedHeapMb > 0 ? current.ManagedHeapMb : GC.GetTotalMemory(false) / mb;
        double committed = current.GcCommittedMb > 0 ? current.GcCommittedMb : gc.TotalCommittedBytes / mb;
        double fragmented = current.GcFragmentedMb > 0 ? current.GcFragmentedMb : gc.FragmentedBytes / mb;
        double privateMb = current.PrivateMemoryMb > 0 ? current.PrivateMemoryMb : process.PrivateMemorySize64 / mb;
        double workingMb = current.WorkingSetMb > 0 ? current.WorkingSetMb : process.WorkingSet64 / mb;
        double nativeMb = current.EstimatedNativeMb > 0 ? current.EstimatedNativeMb : Math.Max(0, privateMb - managed);

        return new MemoryInspectorSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Label = string.IsNullOrWhiteSpace(label) ? "Memory snapshot" : label.Trim(),
            ManagedHeapMb = managed,
            WorkingSetMb = workingMb,
            PrivateMemoryMb = privateMb,
            GcCommittedMb = committed,
            GcFragmentedMb = fragmented,
            EstimatedNativeMb = nativeMb,
            TotalAllocatedMb = GC.GetTotalAllocatedBytes(false) / mb,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            Players = current.Players,
            LoadedEntities = current.LoadedEntities,
            LoadedChunks = current.LoadedChunks,
            Structures = structures.OrderBy(x => x.Category).ThenByDescending(x => x.Count).ToList()
        };
    }

    private object BuildMemoryInspectorPayload(ServerHealthSnapshot? health)
    {
        List<MemoryInspectorSnapshot> snapshots;
        lock (operationsLock)
        {
            snapshots = (operationsCenterStore.MemoryInspectorSnapshots ?? new List<MemoryInspectorSnapshot>())
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(16)
                .ToList();
        }

        string? gcDumpTool = FindExecutableOnPath("dotnet-gcdump");
        return new
        {
            current = CaptureMemoryInspectorSnapshot("Current", health),
            snapshots,
            deepCapture = new
            {
                available = !string.IsNullOrWhiteSpace(gcDumpTool),
                tool = string.IsNullOrWhiteSpace(gcDumpTool) ? "dotnet-gcdump not found in PATH" : "dotnet-gcdump available",
                warning = "Deep heap capture can force a full Gen2 GC and pause a large server. Use it only during a controlled diagnostic window.",
                note = "Lightweight snapshots compare process/GC state and Suite-owned tracked structures. A true whole-heap object/type inventory requires an external .gcdump or full dump; MemLeakInspector does not automatically declare a culprit."
            }
        };
    }

    private static string? FindExecutableOnPath(string command)
    {
        try
        {
            string[] names = OperatingSystem.IsWindows()
                ? new[] { command + ".exe", command + ".cmd", command + ".bat", command }
                : new[] { command };
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (string name in names)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { }
        return null;
    }

    private MemoryGcAnalysis BuildMemoryGcAnalysis(
        ServerHealthSnapshot snapshot)
    {
        var rows = snapshot.Samples
            .OrderBy(x => x.UnixMilliseconds)
            .ToList();

        // Memory slope/floor estimates are noisy while a world is settling. Require
        // three minutes of post-warm-up samples before presenting Watch/Investigate
        // language so normal startup allocation is not mistaken for retained growth.
        if (rows.Count < 180)
        {
            return new MemoryGcAnalysis
            {
                Available = false,
                SampleCount = rows.Count,
                State = "Collecting",
                Finding = $"Collecting a stable memory baseline ({rows.Count}/180 samples).",
                Recommendation =
                    "Keep lightweight diagnostics enabled for several minutes."
            };
        }

        double firstMs = rows[0].UnixMilliseconds;
        double meanX = rows.Average(x =>
            (x.UnixMilliseconds - firstMs) / 60_000d);

        double CalculateSlope(System.Func<ServerHealthSample, double> selector)
        {
            double meanY = rows.Average(selector);
            double numerator = 0;
            double denominator = 0;
            foreach (var row in rows)
            {
                double x = (row.UnixMilliseconds - firstMs) / 60_000d;
                double y = selector(row);
                numerator += (x - meanX) * (y - meanY);
                denominator += (x - meanX) * (x - meanX);
            }
            return denominator <= 0 ? 0 : numerator / denominator;
        }

        double slope = CalculateSlope(x => x.ManagedHeapMb);
        double privateSlope = CalculateSlope(x => x.PrivateMemoryMb);
        double nativeSlope = CalculateSlope(x => x.EstimatedNativeMb);

        int gen0 = Math.Max(
            0,
            rows[^1].Gen0Collections - rows[0].Gen0Collections);
        int gen1 = Math.Max(
            0,
            rows[^1].Gen1Collections - rows[0].Gen1Collections);
        int gen2 = Math.Max(
            0,
            rows[^1].Gen2Collections - rows[0].Gen2Collections);

        var postGc = new List<double>();
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i].Gen2Collections > rows[i - 1].Gen2Collections)
            {
                int end = Math.Min(rows.Count, i + 4);
                postGc.Add(
                    rows.Skip(i)
                        .Take(end - i)
                        .Min(x => x.ManagedHeapMb));
            }
        }

        double floorStart = postGc.Count >= 2
            ? postGc.First()
            : rows.Take(Math.Min(60, rows.Count))
                .Min(x => x.ManagedHeapMb);

        double floorEnd = postGc.Count >= 2
            ? postGc.Last()
            : rows.TakeLast(Math.Min(60, rows.Count))
                .Min(x => x.ManagedHeapMb);

        double floorGrowth = floorEnd - floorStart;

        int score = 100;
        if (slope >= 100) score -= 35;
        else if (slope >= 50) score -= 25;
        else if (slope >= 25) score -= 12;

        if (floorGrowth >= 512) score -= 35;
        else if (floorGrowth >= 256) score -= 22;
        else if (floorGrowth >= 128) score -= 10;

        if (gen2 >= 6) score -= 15;
        else if (gen2 >= 3) score -= 8;

        if (privateSlope >= 250) score -= 35;
        else if (privateSlope >= 100) score -= 20;
        else if (privateSlope >= 50) score -= 10;

        if (nativeSlope >= 250) score -= 30;
        else if (nativeSlope >= 100) score -= 18;
        else if (nativeSlope >= 50) score -= 8;

        score = Math.Clamp(score, 0, 100);

        string state =
            score < 45 ? "Critical" :
            score < 70 ? "Investigate" :
            score < 88 ? "Watch" :
            "Healthy";

        string finding;
        string recommendation;

        if (privateSlope >= 100 && nativeSlope >= 75 && slope < 50)
        {
            finding =
                "Private process memory is rising much faster than the managed heap; this points toward native/engine retention rather than an ordinary C# heap leak.";
            recommendation =
                "Correlate private/native growth with loaded chunks/entities and SQLite activity. This pattern can come from engine chunk retention, SQLite/native buffers, compression/image buffers, or another unmanaged owner.";
        }
        else if (floorGrowth >= 256 && gen2 > 0)
        {
            finding =
                "Possible retained-memory growth: the estimated post-Gen2 heap floor is rising.";
            recommendation =
                "Check whether the heap repeatedly returns to a higher floor after Gen2 GC. Correlate the rise with entity growth, world generation, and recent mod changes before calling it a leak.";
        }
        else if (slope >= 50)
        {
            finding =
                "Managed heap is rising quickly in the current diagnostic window.";
            recommendation =
                "Watch the next Gen2 collection. If the heap drops near its old baseline, this is allocation pressure rather than strong retained-memory evidence.";
        }
        else if (rows[^1].GcFragmentedMb >= 512 && rows[^1].GcCommittedMb > 0 &&
                 rows[^1].GcFragmentedMb / rows[^1].GcCommittedMb >= 0.20 && floorGrowth < 128)
        {
            finding =
                "The GC reports substantial heap fragmentation without a rising post-Gen2 floor in this window. High committed/process memory alone does not establish a leak.";
            recommendation =
                "Compare loaded chunks, entities, players and logger queues at similar workloads. Profile large allocations if fragmentation persists; the diagnostic does not force collections or compact the live server heap.";
        }
        else if (gen2 >= 6)
        {
            finding =
                "Gen2 collections are unusually frequent in the current window.";
            recommendation =
                "Use the manual engine profiler during a slow period and inspect allocation-heavy/entity/worldgen activity.";
        }
        else
        {
            finding =
                "Memory behavior does not currently show a strong retained-growth signal.";
            recommendation =
                "Keep diagnostics enabled during the workload that normally causes lag for a more representative memory trend.";
        }

        return new MemoryGcAnalysis
        {
            Available = true,
            SampleCount = rows.Count,
            State = state,
            Score = score,
            HeapCurrentMb = rows[^1].ManagedHeapMb,
            HeapMinimumMb = rows.Min(x => x.ManagedHeapMb),
            HeapMaximumMb = rows.Max(x => x.ManagedHeapMb),
            HeapSlopeMbPerMinute = slope,
            PrivateCurrentMb = rows[^1].PrivateMemoryMb,
            PrivateSlopeMbPerMinute = privateSlope,
            EstimatedNativeCurrentMb = rows[^1].EstimatedNativeMb,
            EstimatedNativeSlopeMbPerMinute = nativeSlope,
            GcCommittedCurrentMb = rows[^1].GcCommittedMb,
            GcFragmentedCurrentMb = rows[^1].GcFragmentedMb,
            Gen0Increase = gen0,
            Gen1Increase = gen1,
            Gen2Increase = gen2,
            EstimatedPostGcFloorStartMb = floorStart,
            EstimatedPostGcFloorEndMb = floorEnd,
            EstimatedPostGcFloorGrowthMb = floorGrowth,
            Finding = finding,
            Recommendation = recommendation
        };
    }

    private List<PerformanceScorecardRow> BuildPerformanceScorecard(
        ServerHealthSnapshot? snapshot,
        MemoryGcAnalysis memory)
    {
        if (snapshot == null || !snapshot.Enabled)
        {
            return new List<PerformanceScorecardRow>
            {
                new()
                {
                    Name = "Overall",
                    Score = 0,
                    State = "Diagnostics off",
                    Reason = "Enable lightweight diagnostics to build the server performance scorecard.",
                    TargetSection = "actionCenter"
                }
            };
        }

        int ScoreState(
            int score,
            out string state)
        {
            score = Math.Clamp(score, 0, 100);
            state =
                score < 50 ? "Critical" :
                score < 70 ? "Investigate" :
                score < 88 ? "Watch" :
                "Healthy";
            return score;
        }

        var diag = snapshot.DiagnosticSummary;
        var smart = snapshot.SmartAnalysis;

        int cpu = 100;
        if (diag.MaxCpuPercent >= 95) cpu -= 40;
        else if (diag.MaxCpuPercent >= 90) cpu -= 28;
        else if (diag.AverageCpuPercent >= 75) cpu -= 20;
        else if (diag.AverageCpuPercent >= 60) cpu -= 10;

        int main = 100;
        if (diag.WorstDelayMs >= 500) main -= 55;
        else if (diag.WorstDelayMs >= 250) main -= 38;
        else if (diag.WorstDelayMs >= 100) main -= 24;
        else if (diag.WorstDelayMs >= 50) main -= 10;
        main -= Math.Min(20, diag.StallCount * 3);

        int entities = 100;
        var entitySignal = smart.Signals
            .Where(x => x.Category == "Entities")
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        if (entitySignal != null)
            entities -= Math.Clamp(entitySignal.Score / 2, 10, 45);
        if (smart.RecentEntityMedian >= 20_000) entities -= 25;
        else if (smart.RecentEntityMedian >= 12_000) entities -= 12;

        int chunks = 100;
        var chunkSignal = smart.Signals
            .Where(x => x.Category == "Chunks")
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        if (chunkSignal != null)
            chunks -= Math.Clamp(chunkSignal.Score / 2, 10, 40);

        int storage = 100;
        if (diag.BlockLoggerDroppedIncrease > 0) storage -= 55;
        if (diag.MaxBlockLoggerQueue >= module.Config.MaxQueuedEvents * 3 / 4)
            storage -= 25;
        else if (diag.MaxBlockLoggerQueue >= 1000)
            storage -= 12;
        if (diag.MaxBlockLoggerWriterMs >= 250) storage -= 25;
        else if (diag.MaxBlockLoggerWriterMs >= 100) storage -= 12;

        int overall = smart.HealthScore;

        var rows = new List<PerformanceScorecardRow>();

        void Add(
            string name,
            int score,
            string reason,
            string target)
        {
            score = ScoreState(score, out string state);
            rows.Add(
                new PerformanceScorecardRow
                {
                    Name = name,
                    Score = score,
                    State = state,
                    Reason = reason,
                    TargetSection = target
                });
        }

        Add(
            "Overall",
            overall,
            smart.PrimaryFinding,
            "performance");

        Add(
            "CPU",
            cpu,
            $"Rolling avg/max CPU {diag.AverageCpuPercent:0}% / {diag.MaxCpuPercent:0}%.",
            "profiler");

        Add(
            "Main Thread",
            main,
            $"Worst delay {diag.WorstDelayMs:0}ms; {diag.StallCount} stall sample(s).",
            "profiler");

        Add(
            "Memory",
            memory.Available ? memory.Score : 100,
            memory.Finding,
            "memory");

        Add(
            "Entities",
            entities,
            entitySignal?.Evidence ??
            $"Recent entity median {smart.RecentEntityMedian:N0}.",
            "performance");

        Add(
            "Chunks",
            chunks,
            chunkSignal?.Evidence ??
            $"Loaded chunk max {diag.MaxLoadedChunks:N0}.",
            "performance");

        Add(
            "Storage / BlockLogger",
            storage,
            $"Max queue {diag.MaxBlockLoggerQueue:N0}; writer {diag.MaxBlockLoggerWriterMs:N0}ms; drops +{diag.BlockLoggerDroppedIncrease:N0}.",
            "performance");

        return rows;
    }

    private object BuildPlayerLoadAnalysis()
    {
        EnsurePlayerLoadBuckets();

        var rows = operationsCenterStore.PlayerLoad
            .Select(x => new
            {
                x.Label,
                x.MinimumPlayers,
                x.MaximumPlayers,
                x.Samples,
                averageDelayMs = x.Samples <= 0
                    ? 0
                    : x.DelayTotalMs / x.Samples,
                averageCpuPercent = x.Samples <= 0
                    ? 0
                    : x.CpuTotalPercent / x.Samples,
                x.DelayMaxMs,
                x.CpuMaxPercent,
                x.StallSamples,
                x.ExcludedLifecycleSamples,
                x.ExcludedLifecycleMaxDelayMs,
                stallPercent = x.Samples <= 0
                    ? 0
                    : x.StallSamples * 100d / x.Samples
            })
            .ToList();

        var comfortable = rows
            .Where(x =>
                x.Samples >= 10 &&
                x.averageDelayMs < 50 &&
                x.stallPercent < 10)
            .OrderByDescending(x => x.MaximumPlayers)
            .FirstOrDefault();

        return new
        {
            rows,
            comfortableRange = comfortable?.Label ?? "Insufficient history",
            excludedLifecycleSamples = rows.Sum(x => x.ExcludedLifecycleSamples),
            excludedLifecycleMaxDelayMs = rows.Count == 0
                ? 0
                : rows.Max(x => x.ExcludedLifecycleMaxDelayMs),
            note =
                "This models observed server behavior by player-count bucket. Startup/stale sampler discontinuities of 30 seconds or more are excluded from this workload model but remain visible in Server Health diagnostics. It is not a hard player limit."
        };
    }

    private object BuildInstrumentationStatus()
    {
        var saves = operationsCenterStore.WorldSaves
            .OrderByDescending(x => x.UnixMilliseconds)
            .Take(20)
            .ToList();

        var latestNetwork = operationsCenterStore.NetworkSamples
            .OrderByDescending(x => x.UnixMilliseconds)
            .FirstOrDefault();

        return new
        {
            worldSave = new
            {
                status = saves.Count > 0
                    ? "Measured"
                    : "Waiting for next world save",
                exactTiming = true,
                measuredNow = saves.Count > 0,
                observationCount = saves.Count,
                lastObservedMs = saves.FirstOrDefault()?.ObservedMainThreadWindowMs ?? 0,
                averageObservedMs = saves.Count == 0
                    ? 0
                    : saves.Average(x => x.ObservedMainThreadWindowMs),
                worstObservedMs = saves.Count == 0
                    ? 0
                    : saves.Max(x => x.ObservedMainThreadWindowMs),
                latest = saves.FirstOrDefault(),
                summary = saves.Count == 0
                    ? "GameWorldSave timing is hooked. Waiting for the next save event."
                    : $"Last observed save window {saves[0].ObservedMainThreadWindowMs:0}ms; worst of the retained recent set {saves.Max(x => x.ObservedMainThreadWindowMs):0}ms.",
                next =
                    "The timing is the observed main-thread window from GameWorldSave until a post-save callback runs. It is stronger than correlation alone, but still includes normal callback/tick scheduling rather than pure disk-write time."
            },
            blockEntities = new
            {
                status = "Activity proxy available",
                exactTiming = false,
                measuredNow = false,
                summary =
                    "BlockLogger can rank busy block/container coordinates by recent activity. That is useful triage evidence, not block-entity CPU time.",
                next =
                    "Add exact block-entity tick instrumentation before displaying per-instance milliseconds."
            },
            network = new
            {
                status = latestNetwork == null
                    ? "Collecting"
                    : "Measured",
                exactBandwidth = true,
                measuredNow = latestNetwork != null,
                sentBytesPerSecond =
                    latestNetwork?.SentBytesPerSecond ?? 0,
                receivedBytesPerSecond =
                    latestNetwork?.ReceivedBytesPerSecond ?? 0,
                totalSentBytes =
                    latestNetwork?.TotalSentBytes ?? api.Server.TotalSentBytes,
                totalReceivedBytes =
                    latestNetwork?.TotalReceivedBytes ?? api.Server.TotalReceivedBytes,
                players = latestNetwork?.Players ??
                    (api.World.AllOnlinePlayers?.Length ?? 0),
                summary = latestNetwork == null
                    ? "Server-wide network byte counters are available; waiting for two maintenance samples to calculate rates."
                    : $"Server-wide rate: {FormatBytesPerSecond(latestNetwork.SentBytesPerSecond)} out / {FormatBytesPerSecond(latestNetwork.ReceivedBytesPerSecond)} in.",
                next =
                    "These are real server totals from IServerAPI. Per-mod/per-packet attribution is not claimed because the public API does not expose a global packet-cost event."
            },
            optimizationSafety = new
            {
                status = "Provider-safe",
                newfiesBuiltInDeepOptimizations = false,
                tungstenOptional = true,
                measurement = true,
                summary =
                    "Optimization changes are recommendation/measurement driven. Tungsten remains optional; one measured experiment is allowed at a time to avoid mixed attribution."
            }
        };
    }

    private object BuildServerStatusSnapshot(int days)
    {
        var analytics = module.PlayerAnalytics?.GetSnapshot(days) ?? new PlayerCountAnalyticsSnapshot();
        var analytics30 = days >= 30 ? analytics : (module.PlayerAnalytics?.GetSnapshot(30) ?? analytics);
        var players = module.Admin?.GetPlayers() ?? Array.Empty<AdminPlayerSummary>();
        var online = players.Where(x => x.Online).ToList();
        var maintenance = module.Admin?.GetMaintenance() ?? (false, "", 0L);
        var shutdown = module.Admin?.GetShutdown() ?? (0L, "");
        var network = operationsCenterStore.NetworkSamples
            .OrderByDescending(x => x.UnixMilliseconds)
            .FirstOrDefault();
        var reports = module.Reports?.GetSnapshot("all", 1);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long DayMs = 86_400_000L;
        int new24h = players.Count(x => x.FirstSeenUnixMilliseconds >= now - DayMs);
        int new7d = players.Count(x => x.FirstSeenUnixMilliseconds >= now - 7 * DayMs);
        int new30d = players.Count(x => x.FirstSeenUnixMilliseconds >= now - 30 * DayMs);
        int previous7dNew = players.Count(x => x.FirstSeenUnixMilliseconds >= now - 14 * DayMs && x.FirstSeenUnixMilliseconds < now - 7 * DayMs);
        int active24h = players.Count(x => x.Online || x.LastSeenUnixMilliseconds >= now - DayMs);
        int active7d = players.Count(x => x.Online || x.LastSeenUnixMilliseconds >= now - 7 * DayMs);
        int active30d = players.Count(x => x.Online || x.LastSeenUnixMilliseconds >= now - 30 * DayMs);
        int returning7d = players.Count(x => (x.Online || x.LastSeenUnixMilliseconds >= now - 7 * DayMs) && x.FirstSeenUnixMilliseconds < now - 7 * DayMs);
        var retentionCohort = players.Where(x => x.FirstSeenUnixMilliseconds >= now - 37 * DayMs && x.FirstSeenUnixMilliseconds < now - 7 * DayMs).ToList();
        int retentionReturned = retentionCohort.Count(x => x.LastSeenUnixMilliseconds >= x.FirstSeenUnixMilliseconds + 7 * DayMs);
        double sevenDayReturnRate = retentionCohort.Count == 0 ? 0 : retentionReturned * 100d / retentionCohort.Count;
        double newPlayerWeekGrowthPercent = previous7dNew == 0 ? (new7d > 0 ? 100d : 0d) : (new7d - previous7dNew) * 100d / previous7dNew;
        var daily30 = analytics30.Daily.TakeLast(30).ToList();
        int peakPlayers30Days = daily30.Count == 0 ? analytics.CurrentPlayers : daily30.Max(x => x.High);
        double averageDailyPeak30Days = daily30.Count == 0 ? analytics.CurrentPlayers : daily30.Average(x => x.High);

        var newPlayersDaily = Enumerable.Range(0, 30)
            .Select(offset => DateTime.Now.Date.AddDays(-(29 - offset)))
            .Select(day =>
            {
                long start = new DateTimeOffset(day).ToUnixTimeMilliseconds();
                long end = new DateTimeOffset(day.AddDays(1)).ToUnixTimeMilliseconds();
                return new
                {
                    date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    count = players.Count(x => x.FirstSeenUnixMilliseconds >= start && x.FirstSeenUnixMilliseconds < end)
                };
            })
            .ToList();

        return new
        {
            generatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            server = new
            {
                uptimeSeconds = api.Server.ServerUptimeSeconds,
                worldPlayTimeSeconds = api.Server.TotalWorldPlayTime,
                onlinePlayers = api.World.AllOnlinePlayers?.Length ?? online.Count,
                trackedPlayers = players.Count,
                averagePingMilliseconds = online.Count == 0 ? 0 : online.Average(x => x.PingMilliseconds),
                maxPingMilliseconds = online.Count == 0 ? 0 : online.Max(x => x.PingMilliseconds),
                loadedMods = api.ModLoader.Mods.Count(),
                totalSentBytes = api.Server.TotalSentBytes,
                totalReceivedBytes = api.Server.TotalReceivedBytes,
                sentBytesPerSecond = network?.SentBytesPerSecond ?? 0,
                receivedBytesPerSecond = network?.ReceivedBytesPerSecond ?? 0,
                serverTimeZone = TimeZoneInfo.Local.Id
            },
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
            reports = new
            {
                open = reports?.OpenCount ?? 0,
                investigating = reports?.InvestigatingCount ?? 0,
                total = reports?.TotalCount ?? 0
            },
            growth = new
            {
                totalKnownPlayers = players.Count,
                newPlayers24Hours = new24h,
                newPlayers7Days = new7d,
                newPlayers30Days = new30d,
                previous7DaysNewPlayers = previous7dNew,
                newPlayerWeekGrowthPercent,
                activePlayers24Hours = active24h,
                activePlayers7Days = active7d,
                activePlayers30Days = active30d,
                returningPlayers7Days = returning7d,
                sevenDayReturnRate,
                sevenDayReturnCohort = retentionCohort.Count,
                peakPlayers30Days,
                averageDailyPeak30Days,
                dailyNewPlayers = newPlayersDaily
            },
            onlinePlayers = online.Select(x => new
            {
                name = x.Name,
                nickname = module.SupporterNickname?.GetSavedNickname(x.PlayerUid) ?? string.Empty,
                playerUid = x.PlayerUid,
                pingMilliseconds = x.PingMilliseconds,
                afk = x.Afk,
                sessionSeconds = x.SessionSeconds,
                role = x.Role
            }).OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList(),
            analytics
        };
    }

    private void DecorateSupporterNicknames(IEnumerable<AdminPlayerSummary> players)
    {
        var nicknames = module.SupporterNickname;
        if (nicknames == null) return;
        foreach (var player in players)
            player.Nickname = nicknames.GetSavedNickname(player.PlayerUid);
    }

    private void DecorateSupporterNicknames(PermissionsManagerSnapshot? snapshot)
    {
        if (snapshot?.OnlinePlayers == null) return;
        var nicknames = module.SupporterNickname;
        if (nicknames == null) return;
        foreach (var player in snapshot.OnlinePlayers)
            player.Nickname = nicknames.GetSavedNickname(player.PlayerUid);
    }

    private static string FormatBytesPerSecond(double value)
    {
        if (value >= 1024d * 1024d)
            return $"{value / (1024d * 1024d):0.00} MB/s";
        if (value >= 1024d)
            return $"{value / 1024d:0.0} KB/s";
        return $"{value:0} B/s";
    }

    private PerformanceSnapshotRecord? CapturePerformanceSnapshot(
        string label,
        out string error)
    {
        error = "";

        var health = module.ServerHealth?.GetSnapshot(300, 1800);
        if (health == null || !health.Enabled)
        {
            error =
                "Enable lightweight diagnostics before capturing a performance snapshot.";
            return null;
        }

        if (!health.SmartAnalysis.Available ||
            health.Samples.Count < 30)
        {
            error =
                $"Collect at least 30 diagnostic samples before capturing a performance snapshot. Current samples: {health.Samples.Count}.";
            return null;
        }

        var environment = CaptureEnvironmentSnapshot(health);

        var snapshot = new PerformanceSnapshotRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            UnixMilliseconds =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Label = string.IsNullOrWhiteSpace(label)
                ? "Performance snapshot"
                : label.Trim(),
            Players = health.Current.Players,
            ModFingerprint = environment.Fingerprint,
            Metrics = environment.Metrics
        };

        operationsCenterStore.PerformanceSnapshots.Insert(0, snapshot);
        SaveOperationsCenterStore();
        return snapshot;
    }

    private ConfigSnapshotRecord CaptureConfigSnapshot(
        string label,
        string actor)
    {
        var snapshot = new ConfigSnapshotRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            UnixMilliseconds =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Label = string.IsNullOrWhiteSpace(label)
                ? "Manual snapshot"
                : label.Trim(),
            CreatedBy = actor,
            SuiteFeaturesJson = JsonSerializer.Serialize(
                module.FeatureSettings,
                jsonOptions),
            BlockLoggerJson = JsonSerializer.Serialize(
                module.Config,
                jsonOptions),
            WebDashboardJson = JsonSerializer.Serialize(
                config,
                jsonOptions),
            AnimalLimiterJson = AnimalLimiterSystem.Current?.Config == null
                ? ""
                : JsonSerializer.Serialize(
                    AnimalLimiterSystem.Current.Config,
                    jsonOptions),
            LoginProtectionJson =
                LoginProtectionSystem.Current?.Config == null
                    ? ""
                    : JsonSerializer.Serialize(
                        LoginProtectionSystem.Current.Config,
                        jsonOptions),
            PublicPlayerListJson = module.PlayerList == null
                ? ""
                : JsonSerializer.Serialize(
                    module.PlayerList.Config,
                    jsonOptions)
        };

        operationsCenterStore.ConfigSnapshots.Insert(0, snapshot);
        SaveOperationsCenterStore();
        return snapshot;
    }

    private string RestoreConfigSnapshot(ConfigSnapshotRecord snapshot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(snapshot.SuiteFeaturesJson))
            {
                var suiteFeatures = JsonSerializer.Deserialize<SuiteFeatureConfig>(
                    snapshot.SuiteFeaturesJson,
                    jsonOptions);
                if (suiteFeatures != null)
                {
                    foreach (var entry in suiteFeatures.GetEntries())
                        module.FeatureSettings.TrySet(entry.Name, entry.Enabled, out _);
                    module.FeatureSettings.Save(api);
                }
            }

            var blockLogger =
                JsonSerializer.Deserialize<BlockLogger.Config.BlockLoggerConfig>(
                    snapshot.BlockLoggerJson,
                    jsonOptions);

            if (blockLogger != null)
            {
                blockLogger.Clamp();
                module.Config.ApplyFrom(blockLogger);
                module.SaveRuntimeConfig();
            }

            var web =
                JsonSerializer.Deserialize<WebDashboardConfig>(
                    snapshot.WebDashboardJson,
                    jsonOptions);

            if (web != null)
            {
                web.Clamp();

                // Never restore transient one-time plaintext passwords.
                web.Password = "";
                config = web;
                SaveConfig();
            }

            if (!string.IsNullOrWhiteSpace(snapshot.AnimalLimiterJson))
            {
                var animal =
                    JsonSerializer.Deserialize<AnimalLimiterConfig>(
                        snapshot.AnimalLimiterJson,
                        jsonOptions);

                if (animal != null)
                {
                    BlockLogger.Services.SuiteConfigStorage.Store(api, 
                        animal,
                        "NewfiesAnimalLimiter.json");
                }
            }

            if (!string.IsNullOrWhiteSpace(snapshot.LoginProtectionJson))
            {
                var login =
                    JsonSerializer.Deserialize<LoginProtectionConfig>(
                        snapshot.LoginProtectionJson,
                        jsonOptions);

                if (login != null)
                {
                    login.Clamp();
                    BlockLogger.Services.SuiteConfigStorage.Store(api, 
                        login,
                        "NewfiesLoginProtection.json");
                }
            }

            if (!string.IsNullOrWhiteSpace(snapshot.PublicPlayerListJson) && module.PlayerList != null)
            {
                var playerList = JsonSerializer.Deserialize<PublicPlayerListConfig>(
                    snapshot.PublicPlayerListJson,
                    jsonOptions);
                if (playerList != null)
                    module.PlayerList.ApplyConfig(playerList);
            }

            RestartListener();

            return
                "Snapshot restored. BlockLogger/Web settings were applied immediately where supported. Suite feature gates, Animal Limiter, Login Protection, and Public Player List files were restored; restart the server so every master feature gate and runtime hook fully matches the snapshot.";
        }
        catch (Exception ex)
        {
            return
                $"Restore failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void SetPasswordHash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            config.PasswordIterations,
            HashAlgorithmName.SHA256,
            32);

        config.PasswordSalt = Convert.ToBase64String(salt);
        config.PasswordHash = Convert.ToBase64String(hash);
        config.Password = "";
    }

    private bool VerifyPassword(string password)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(config.PasswordSalt);
            byte[] expected = Convert.FromBase64String(config.PasswordHash);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                config.PasswordIterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private void RestartListener()
    {
        StopListener();

        if (!config.Enabled) return;

        if (!TryParseBindAddress(config.BindAddress, out var address))
        {
            lock (stateLock)
                lastError = $"Invalid bind address '{config.BindAddress}'.";
            api.Logger.Warning("[WebDashboard] {0}", LastError);
            return;
        }

        if (!IPAddress.IsLoopback(address) && !PasswordConfigured)
        {
            lock (stateLock)
                lastError = "A non-loopback bind requires an admin password.";
            api.Logger.Warning(
                "[WebDashboard] refusing non-loopback bind {0}:{1} because no admin password is configured.",
                config.BindAddress,
                config.Port);
            return;
        }

        TcpListener? newListener = null;
        CancellationTokenSource? cts = null;
        Channel<QueuedWebClient>? queue = null;
        List<Task>? workers = null;

        try
        {
            newListener = new TcpListener(address, config.Port);
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                try { newListener.Server.DualMode = address.Equals(IPAddress.IPv6Any); }
                catch { }
            }

            newListener.Start(128);
            cts = new CancellationTokenSource();
            queue = Channel.CreateBounded<QueuedWebClient>(
                new BoundedChannelOptions(config.WebRequestQueueCapacity)
                {
                    SingleWriter = true,
                    SingleReader = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });

            workers = new List<Task>(config.WebWorkerCount);
            for (int i = 0; i < config.WebWorkerCount; i++)
            {
                int workerId = i + 1;
                workers.Add(Task.Run(
                    () => WebWorkerLoopAsync(workerId, queue.Reader, cts.Token),
                    CancellationToken.None));
            }

            Task acceptTask = Task.Run(
                () => AcceptLoopAsync(newListener, queue.Writer, cts.Token),
                CancellationToken.None);

            lock (stateLock)
            {
                listener = newListener;
                listenerCts = cts;
                listenerTask = acceptTask;
                webRequestQueue = queue;
                webWorkerTasks = workers;
                lastError = "";
                startedUtc = DateTime.UtcNow;
            }

            Interlocked.Exchange(ref webQueuedRequests, 0);
            Interlocked.Exchange(ref webActiveWorkers, 0);

            if (LocalAnonymousMode)
            {
                api.Logger.Notification(
                    "[WebDashboard] local dashboard listening on {0}:{1} with no login required (loopback only). Workers={2}, queue={3}.",
                    config.BindAddress,
                    config.Port,
                    config.WebWorkerCount,
                    config.WebRequestQueueCapacity);
            }
            else
            {
                api.Logger.Notification(
                    "[WebDashboard] authenticated dashboard listening on {0}:{1}. Workers={2}, queue={3}.",
                    config.BindAddress,
                    config.Port,
                    config.WebWorkerCount,
                    config.WebRequestQueueCapacity);
            }
        }
        catch (Exception ex)
        {
            try { queue?.Writer.TryComplete(); } catch { }
            try { cts?.Cancel(); } catch { }
            try { newListener?.Stop(); } catch { }
            _ = ObserveListenerShutdownAsync(null, workers ?? new List<Task>(), cts);

            lock (stateLock)
            {
                listener = null;
                listenerCts = null;
                listenerTask = null;
                webRequestQueue = null;
                webWorkerTasks = new List<Task>();
                lastError = $"{ex.GetType().Name}: {ex.Message}";
            }

            api.Logger.Error(
                "[WebDashboard] failed to listen on {0}:{1}: {2}: {3}",
                config.BindAddress,
                config.Port,
                ex.GetType().Name,
                ex.Message);
        }
    }

    private void StopListener()
    {
        TcpListener? toStop;
        CancellationTokenSource? toCancel;
        Task? toWait;
        Channel<QueuedWebClient>? toQueue;
        List<Task> toWorkers;

        lock (stateLock)
        {
            toStop = listener;
            toCancel = listenerCts;
            toWait = listenerTask;
            toQueue = webRequestQueue;
            toWorkers = webWorkerTasks;
            listener = null;
            listenerCts = null;
            listenerTask = null;
            webRequestQueue = null;
            webWorkerTasks = new List<Task>();
        }

        try { toQueue?.Writer.TryComplete(); } catch { }
        try { toCancel?.Cancel(); } catch { }
        try { toStop?.Stop(); } catch { }

        if (toQueue != null)
        {
            while (toQueue.Reader.TryRead(out var queued))
            {
                try { queued.Client.Dispose(); } catch { }
            }
        }

        Interlocked.Exchange(ref webQueuedRequests, 0);
        Interlocked.Exchange(ref webActiveWorkers, 0);

        snapshotCache.Clear();

        // Never block the Vintage Story/main caller waiting for network workers.
        // Cancellation closes the listener immediately; cleanup is observed off-path.
        _ = ObserveListenerShutdownAsync(toWait, toWorkers, toCancel);
    }

    private static async Task ObserveListenerShutdownAsync(
        Task? acceptTask,
        IReadOnlyList<Task> workers,
        CancellationTokenSource? cts)
    {
        try
        {
            var tasks = new List<Task>(workers.Count + (acceptTask == null ? 0 : 1));
            if (acceptTask != null) tasks.Add(acceptTask);
            tasks.AddRange(workers);
            if (tasks.Count > 0)
                await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            try { cts?.Dispose(); } catch { }
        }
    }

    private async Task AcceptLoopAsync(
        TcpListener activeListener,
        ChannelWriter<QueuedWebClient> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    client.NoDelay = true;

                    var queued = new QueuedWebClient(client, Stopwatch.GetTimestamp());
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
                    try { client?.Dispose(); } catch { }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    try { client?.Dispose(); } catch { }
                    break;
                }
                catch (Exception ex)
                {
                    try { client?.Dispose(); } catch { }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        api.Logger.Warning(
                            "[WebDashboard] accept failed: {0}: {1}",
                            ex.GetType().Name,
                            ex.Message);
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            try { writer.TryComplete(); } catch { }
        }
    }

    private async Task WebWorkerLoopAsync(
        int workerId,
        ChannelReader<QueuedWebClient> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var queued))
                {
                    Interlocked.Decrement(ref webQueuedRequests);
                    Interlocked.Increment(ref webActiveWorkers);
                    double queueWaitMs = Math.Max(
                        0,
                        (Stopwatch.GetTimestamp() - queued.AcceptedTimestamp) /
                        (double)Stopwatch.Frequency * 1000d);

                    try
                    {
                        if (queueWaitMs > config.WebQueueMaxWaitMs)
                        {
                            Interlocked.Increment(ref webRejectedBusyRequests);
                            using var busyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            busyCts.CancelAfter(TimeSpan.FromSeconds(1));
                            await WriteResponseAsync(
                                queued.Client.GetStream(),
                                503,
                                "text/plain; charset=utf-8",
                                BusyResponseBytes,
                                busyCts.Token).ConfigureAwait(false);
                            continue;
                        }

                        await HandleClientAsync(
                            queued.Client,
                            cancellationToken,
                            queued.AcceptedTimestamp,
                            queueWaitMs).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal when the listener is stopped/restarted.
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is TimeoutException && module.WebMap?.IsTerrainExportInProgress == true))
                        {
                            api.Logger.Warning(
                                "[WebDashboard] worker {0} request failed: {1}: {2}",
                                workerId,
                                ex.GetType().Name,
                                ex.Message);
                        }
                    }
                    finally
                    {
                        try { queued.Client.Dispose(); } catch { }
                        Interlocked.Decrement(ref webActiveWorkers);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal listener shutdown/restart.
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
        catch { }
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }

    private void UpdatePeakQueueDepth(int depth)
    {
        int observed = Volatile.Read(ref webPeakQueuedRequests);
        while (depth > observed)
        {
            int prior = Interlocked.CompareExchange(ref webPeakQueuedRequests, depth, observed);
            if (prior == observed) break;
            observed = prior;
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken,
        long acceptedTimestamp,
        double queueWaitMilliseconds)
    {
        client.NoDelay = true;

        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (remote == null || !IsRemoteAllowed(remote))
        {
            await WriteResponseAsync(
                client.GetStream(),
                403,
                "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("Forbidden"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(15));

        NetworkStream stream = client.GetStream();
        var request = await ReadRequestAsync(stream, linkedCts.Token).ConfigureAwait(false);
        if (request == null) return;

        // Per-table SQLite page accounting is an explicit manual diagnostic and can
        // legitimately take longer on a multi-GB BlockLogger database. Keep ordinary
        // dashboard requests on the short timeout while giving this one bounded headroom.
        if (request.Method == "POST" && request.Path == "/api/admin/database-storage/analyze")
            linkedCts.CancelAfter(TimeSpan.FromSeconds(60));

        var metric = new WebRequestMetricContext
        {
            UnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Method = request.Method,
            Path = request.Path,
            StartedTimestamp = acceptedTimestamp > 0 ? acceptedTimestamp : Stopwatch.GetTimestamp(),
            QueueWaitMilliseconds = queueWaitMilliseconds
        };

        requestMetricContext.Value = metric;
        Interlocked.Increment(ref webActiveRequests);
        bool failed = false;
        try
        {
            await RouteAsync(stream, request, remote, linkedCts.Token).ConfigureAwait(false);
            if (ShouldInvalidateAfterResponse(request.Method, request.Path, metric.StatusCode) && !metric.ResponseRejected)
                snapshotCache.Invalidate(key => CacheAffectedByMutation(key, request.Path));
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

    internal static bool ShouldInvalidateAfterResponse(string method, string path, int status) =>
        method is "POST" or "PUT" or "PATCH" or "DELETE" && status >= 200 && status < 300 &&
        path.StartsWith("/api/", StringComparison.Ordinal) &&
        path is not ("/api/login" or "/api/logout" or "/api/auth/login" or "/api/auth/logout");

    internal static bool CacheAffectedByMutation(string key, string path) =>
        key is "live" or "dashboard" ||
        (key.StartsWith("anticheat", StringComparison.Ordinal) && path.StartsWith("/api/anticheat", StringComparison.Ordinal)) ||
        (key == "discord-status" && path.StartsWith("/api/discord", StringComparison.Ordinal)) ||
        (key.StartsWith("performance-", StringComparison.Ordinal) &&
            (path.StartsWith("/api/performance", StringComparison.Ordinal) || path.StartsWith("/api/profiler", StringComparison.Ordinal) || path.StartsWith("/api/tick-debug", StringComparison.Ordinal))) ||
        path.StartsWith("/api/operations/config-restore", StringComparison.Ordinal);

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

        var adminHealth = module.Admin?.GetStorageHealth() ?? new AdminStorageHealth { Available = false };
        var blockHealth = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays) ?? new DatabaseHealthSnapshot { Available = false };
        var snapshot = new DatabaseStorageViewerSnapshot
        {
            CapturedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DetailedAnalysisAvailable = false,
            DetailedAnalysisUnixMilliseconds = 0,
            Message = "Live file totals are shown. Press Analyze Storage for per-table/index usage; detailed scans are intentionally manual and cached."
        };

        snapshot.Databases.Add(DatabaseStorageAnalyzer.CreateLightweight(
            "essentials",
            "Essentials Admin SQLite",
            adminHealth.Path,
            adminHealth.BackupCount,
            DatabaseStorageAnalyzer.GetBackupBytes(adminHealth.Path, "essentials"),
            0));
        snapshot.Databases.Add(DatabaseStorageAnalyzer.CreateLightweight(
            "blocklogger",
            "BlockLogger SQLite",
            blockHealth.Path,
            blockHealth.BackupCount,
            blockHealth.BackupBytes,
            module.Config.RetentionDays));
        return snapshot;
    }

    private void RefreshDatabaseStorageFileTotals(DatabaseStorageViewerSnapshot snapshot)
    {
        var adminHealth = module.Admin?.GetStorageHealth() ?? new AdminStorageHealth { Available = false };
        var blockHealth = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays) ?? new DatabaseHealthSnapshot { Available = false };
        foreach (var db in snapshot.Databases)
        {
            if (db.Key.Equals("essentials", StringComparison.OrdinalIgnoreCase))
            {
                db.Available = adminHealth.Available;
                db.DatabaseBytes = SafeFileLength(adminHealth.Path);
                db.WalBytes = SafeFileLength(adminHealth.Path + "-wal");
                db.ShmBytes = SafeFileLength(adminHealth.Path + "-shm");
                db.BackupCount = adminHealth.BackupCount;
                db.BackupBytes = DatabaseStorageAnalyzer.GetBackupBytes(adminHealth.Path, "essentials");
            }
            else if (db.Key.Equals("blocklogger", StringComparison.OrdinalIgnoreCase))
            {
                db.Available = blockHealth.Available;
                db.DatabaseBytes = blockHealth.DatabaseBytes;
                db.WalBytes = blockHealth.WalBytes;
                db.ShmBytes = blockHealth.ShmBytes;
                db.BackupCount = blockHealth.BackupCount;
                db.BackupBytes = blockHealth.BackupBytes;
                db.RetentionDays = module.Config.RetentionDays;
            }

            if (db.Detailed)
            {
                foreach (var table in db.Tables)
                    table.PercentOfDatabase = db.LogicalDatabaseBytes <= 0 ? 0 : table.TotalBytes * 100d / db.LogicalDatabaseBytes;
            }
        }
    }

    private static long SafeFileLength(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private DatabaseStorageViewerSnapshot AnalyzeDatabaseStorage()
    {
        var adminHealth = module.Admin?.GetStorageHealth() ?? new AdminStorageHealth { Available = false };
        var blockHealth = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays) ?? new DatabaseHealthSnapshot { Available = false };
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = new DatabaseStorageViewerSnapshot
        {
            CapturedUnixMilliseconds = now,
            DetailedAnalysisUnixMilliseconds = now,
            Message = "Storage analysis finished. Exact page statistics and payload estimates are labeled per database. This report is cached until Analyze Storage is run again."
        };

        var admin = DatabaseStorageAnalyzer.Analyze(
            "essentials",
            "Essentials Admin SQLite",
            adminHealth.Path,
            adminHealth.BackupCount,
            DatabaseStorageAnalyzer.GetBackupBytes(adminHealth.Path, "essentials"),
            0);
        var blockLogger = DatabaseStorageAnalyzer.Analyze(
            "blocklogger",
            "BlockLogger SQLite",
            blockHealth.Path,
            blockHealth.BackupCount,
            blockHealth.BackupBytes,
            module.Config.RetentionDays);
        snapshot.Databases.Add(admin);
        snapshot.Databases.Add(blockLogger);
        snapshot.DetailedAnalysisAvailable = admin.Detailed || blockLogger.Detailed;
        snapshot.Recommendations.AddRange(admin.Recommendations);
        snapshot.Recommendations.AddRange(blockLogger.Recommendations);

        lock (databaseStorageViewerLock)
            cachedDatabaseStorageViewer = snapshot;
        return snapshot;
    }

    private string GetAntiCheatApprovalUploadRoot()
    {
        string root = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "anticheat", "approval-uploads");
        Directory.CreateDirectory(root);
        return root;
    }

    private void PruneAntiCheatApprovalUploads()
    {
        DateTime cutoff = DateTime.UtcNow - AntiCheatApprovalUploadLifetime;
        foreach (var pair in antiCheatApprovalUploads.ToArray())
        {
            if (pair.Value.CreatedUtc >= cutoff) continue;
            if (antiCheatApprovalUploads.TryRemove(pair.Key, out var stale))
            {
                try { if (File.Exists(stale.TempPath)) File.Delete(stale.TempPath); } catch { }
            }
        }

        try
        {
            string root = GetAntiCheatApprovalUploadRoot();
            foreach (string file in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private void RemoveAntiCheatApprovalUpload(string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) return;
        if (!antiCheatApprovalUploads.TryRemove(uploadId, out var state)) return;
        try { if (File.Exists(state.TempPath)) File.Delete(state.TempPath); } catch { }
    }

    private async Task<AdminInventoryInspection> LoadSavedInventoryInspectionAsync(AdminService admin, string uid)
    {
        string worldFilepath = await RunOnMainThreadAsync(() => WorldSavePathResolver.Resolve(api.WorldManager, api.DataBasePath), "web-admin-inventory-savepath", skipIfTimedOut: true).ConfigureAwait(false);
        var saved = await Task.Run(() => admin.ReadSavedInventory(worldFilepath, uid)).ConfigureAwait(false);
        // A reconnect during the disk read resolves to the live server inventory here.
        return await RunOnMainThreadAsync(() => admin.GetInventoryInspection(uid, saved), "web-admin-offline-inventory", skipIfTimedOut: true).ConfigureAwait(false);
    }

    private async Task RouteAsync(
        NetworkStream stream,
        WebRequest request,
        IPAddress remote,
        CancellationToken cancellationToken)
    {
        string path = request.Path;
        string remoteText = remote.ToString();

        if (request.Method == "GET" && (path == "/" || path == "/index.html" ||
            path.Equals("/leaderboard", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/leaderboard/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/leaderboard.html", StringComparison.OrdinalIgnoreCase)))
        {
            bool gzip = AcceptsDashboardGzip(request.Headers.TryGetValue("Accept-Encoding", out var encodings) ? encodings : null);
            var headers = new Dictionary<string, string> { ["Vary"] = "Accept-Encoding" };
            if (gzip) headers["Content-Encoding"] = "gzip";
            await WriteResponseAsync(
                stream,
                200,
                "text/html; charset=utf-8",
                gzip ? DashboardHtmlGzipBytes : DashboardHtmlBytes,
                cancellationToken, headers).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/session")
        {
            if (IsLocalAnonymousRequest(remote))
            {
                await WriteJsonAsync(
                    stream,
                    200,
                    new
                    {
                        authenticated = true,
                        username = "local",
                        role = "Owner",
                        owner = true,
                        csrfToken = localAnonymousCsrfToken,
                        expiresUtc = DateTime.MaxValue,
                        localAnonymous = true,
                        safeDiagnosticsMode = config.SafeDiagnosticsMode
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (TryGetSession(request, out var activeSession))
            {
                await WriteJsonAsync(
                    stream,
                    200,
                    new
                    {
                        authenticated = true,
                        username = activeSession!.Username,
                        role = activeSession.Role,
                        owner = activeSession.IsOwner,
                        csrfToken = activeSession.CsrfToken,
                        expiresUtc = activeSession.ExpiresUtc,
                        localAnonymous = false,
                        safeDiagnosticsMode = config.SafeDiagnosticsMode
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(
                stream,
                200,
                new
                {
                    authenticated = false,
                    username = "",
                    role = "",
                    owner = false,
                    csrfToken = "",
                    expiresUtc = DateTime.MinValue,
                    localAnonymous = false,
                    safeDiagnosticsMode = config.SafeDiagnosticsMode
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/login")
        {
            await HandleLoginAsync(stream, request, remote, cancellationToken).ConfigureAwait(false);
            return;
        }

        WebSession? webSession;
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
            await WriteJsonAsync(
                stream,
                401,
                new { error = "Authentication required." },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string requestActor = webSession?.Username ?? (LocalAnonymousMode ? "local" : config.AdminUsername);

        if (path.StartsWith("/api/web/accounts", StringComparison.OrdinalIgnoreCase) &&
            !(webSession?.IsOwner ?? false))
        {
            RecordWebAudit(
                "panel-account-access",
                path,
                "Denied / owner required",
                remote.ToString(),
                requestActor);
            await WriteJsonAsync(
                stream,
                403,
                new { error = "Only the panel Owner can manage administrator accounts." },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/block-migration" && !(webSession?.IsOwner ?? false) && !LocalAnonymousMode)
        {
            await WriteJsonAsync(stream,403,new {error="Only the panel Owner can configure block ID migration."},cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            (path == "/api/custom-patches/save" || path == "/api/custom-patches/action" ||
             path == "/api/content-patches/save" || path == "/api/content-patches/action" || path == "/api/content-patches/build") &&
            !(webSession?.IsOwner ?? false) && !LocalAnonymousMode)
        {
            RecordWebAudit(
                "custom-patch-access",
                path,
                "Denied / owner required",
                remote.ToString(),
                requestActor);
            await WriteJsonAsync(
                stream,
                403,
                new { error = "Only the panel Owner can create, edit, enable, disable, or delete runtime or generated content patches." },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        bool readOnlyAccount =
            string.Equals(webSession?.Role, "ReadOnly", StringComparison.OrdinalIgnoreCase);
        var authorizationPolicy = FindWebRoutePolicy(request.Method, path);
        if (readOnlyAccount &&
            ((request.Method == "POST" && path != "/api/logout") ||
             (authorizationPolicy?.ExclusiveHeavyOperation ?? false)))
        {
            RecordWebAudit(
                "read-only-denied",
                path,
                "Denied / read-only account",
                remote.ToString(),
                requestActor);
            await WriteJsonAsync(
                stream,
                403,
                new { error = "This panel account is Read Only and cannot perform changes or launch heavy diagnostic operations." },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            !ConstantTimeEquals(
                request.Headers.TryGetValue("X-CSRF-Token", out var csrf) ? csrf : "",
                webSession!.CsrfToken))
        {
            await WriteJsonAsync(
                stream,
                403,
                new { error = "Invalid CSRF token." },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!(request.Method=="GET" && path=="/api/blocklogger/recording") && TryGetMasterDisabledFeature(path, out string disabledFeature))
        {
            await WriteJsonAsync(
                stream,
                503,
                new
                {
                    error = $"{disabledFeature} is master-disabled. Enable it in Suite Feature Gates and restart the server.",
                    featureDisabled = true,
                    restartRequired = true
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryPrepareRouteOperation(
                request,
                out var routeOperation,
                out string routePolicyError))
        {
            await WriteJsonAsync(
                stream,
                429,
                new
                {
                    error = routePolicyError,
                    safeDiagnosticsMode = config.SafeDiagnosticsMode
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using var routeOperationLease = routeOperation;
        if (requestMetricContext.Value is { } routeMetric) routeMetric.RouteLease = routeOperation;

        if (request.Method == "GET" && path == "/api/operations/diagnostics")
        {
            var payload = await GetCachedMainThreadReadAsync("operations-retention", 5000,
                BuildRetentionDiagnostics, "web-retention-diagnostics").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request.Method == "GET" && path == "/api/admin/block-history")
        {
            string player = GetQuery(request, "player", "").Trim();
            var summary = await RunOnMainThreadAsync(() => module.Admin?.GetPlayer(player), "web-investigation-player", skipIfTimedOut: true).ConfigureAwait(false);
            if (summary == null) { await WriteJsonAsync(stream, 404, new { error = "Player not found." }, cancellationToken).ConfigureAwait(false); return; }
            if (module.Queries == null) { await WriteJsonAsync(stream, 503, new { error = "Block history is unavailable." }, cancellationToken).ConfigureAwait(false); return; }
            var history = await module.Queries.QueryPlayerUidAsync(summary.PlayerUid, 100).ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, new { playerUid = summary.PlayerUid, history, limit = 100 }, cancellationToken).ConfigureAwait(false); return;
        }
        if (request.Method == "GET" && path == "/api/admin/investigation")
        {
            string player = GetQuery(request, "player", "").Trim();
            if (player.Length == 0 || player.Length > 128)
            { await WriteJsonAsync(stream, 400, new { error = "Choose a player." }, cancellationToken).ConfigureAwait(false); return; }
            var payload = await RunOnMainThreadAsync(() => BuildPlayerInvestigation(player),
                "web-player-investigation", skipIfTimedOut: true).ConfigureAwait(false);
            await WriteJsonAsync(stream, payload == null ? 404 : 200, payload ?? new { error = "Player not found." }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/live")
        {
            bool terrainExportBusy = module.WebMap?.IsTerrainExportInProgress == true;
            object payload = await GetLiveSnapshotAsync(terrainExportBusy).ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/features")
        {
            await WriteJsonAsync(stream, 200, new
            {
                configFile = SuiteFeatureConfig.ConfigFile,
                restartRequiredForChanges = true,
                features = module.FeatureSettings.GetEntries().Select(x => new { name = x.Name, enabled = x.Enabled }).ToList(),
                activeFeatures = module.Features.GetEntries().Select(x => new { name = x.Name, enabled = x.Enabled }).ToList()
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/features")
        {
            string feature = GetJsonString(request.Body, "feature", "").Trim();
            bool enabled = GetJsonBool(request.Body, "enabled", false);
            var payload = await RunOnMainThreadAsync(() =>
            {
                if (string.Equals(feature, "all", StringComparison.OrdinalIgnoreCase))
                {
                    module.FeatureSettings.SetAll(enabled);
                    module.FeatureSettings.Save(api);
                    return new { success = true, feature = "all", enabled, restartRequired = true, message = $"All suite feature gates set to {(enabled ? "ON" : "OFF")}. Restart the server to fully apply." };
                }

                if (!module.FeatureSettings.TrySet(feature, enabled, out string canonical))
                    return new { success = false, feature, enabled, restartRequired = false, message = "Unknown feature gate. Refresh the feature list and try again." };

                module.FeatureSettings.Save(api);
                return new { success = true, feature = canonical, enabled, restartRequired = true, message = $"Feature '{canonical}' set to {(enabled ? "ON" : "OFF")}. Restart the server to fully unregister/register the subsystem." };
            }, "web-feature-gate-save").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/playerlist/config")
        {
            var playerList = module.PlayerList;
            await WriteJsonAsync(stream, 200, new
            {
                available = playerList != null,
                configFile = PublicPlayerListServer.ConfigFile,
                configSchemaVersion = playerList?.Config.SchemaVersion ?? 6,
                styleVersion = 5,
                serverName = playerList?.Config.ServerName ?? "",
                effectiveServerName = playerList?.DisplayServerName ?? (api.Server.Config.ServerName ?? ""),
                backgroundOpacity = playerList?.Config.BackgroundOpacity ?? 0.72,
                backgroundOpacityPercent = (int)Math.Round((playerList?.Config.BackgroundOpacity ?? 0.72) * 100d),
                showRoles = playerList?.Config.ShowRoles ?? true,
                showPing = playerList?.Config.ShowPing ?? true,
                showSessionTime = playerList?.Config.ShowSessionTime ?? true,
                layoutScale = playerList?.Config.LayoutScale ?? 1.0,
                rowsPerColumn = playerList?.Config.RowsPerColumn ?? 8,
                maxColumns = playerList?.Config.MaxColumns ?? 4,
                alignment = playerList?.Config.Alignment ?? "CenterTop",
                offsetX = playerList?.Config.OffsetX ?? 0,
                offsetY = playerList?.Config.OffsetY ?? 22,
                showPlayerInitials = playerList?.Config.ShowPlayerInitials ?? true,
                showCapacityBar = playerList?.Config.ShowCapacityBar ?? true,
                showColumnHeaders = playerList?.Config.ShowColumnHeaders ?? false,
                compactRows = playerList?.Config.CompactRows ?? false,
                showPingBars = playerList?.Config.ShowPingBars ?? true,
                showFooterHint = playerList?.Config.ShowFooterHint ?? false,
                accentHex = playerList?.Config.AccentHex ?? "#D6AE63",
                useRoleNameColors = playerList?.Config.UseRoleNameColors ?? false,
                roleNameColors = playerList?.Config.RoleNameColors ?? new Dictionary<string, string>(),
                reservedSlotsEnabled = playerList?.Config.ReservedSlotsEnabled ?? false,
                reservedSlots = playerList?.Config.ReservedSlots ?? 0,
                reservedSlotRoles = playerList?.Config.ReservedSlotRoles ?? new List<string>(),
                reservedSlotsFullMessage = playerList?.Config.ReservedSlotsFullMessage ?? "Only reserved slots are left on this server.",
                maxClients = Math.Max(0, api.Server.Config.MaxClients),
                publicSlotLimit = playerList?.PublicSlotLimit ?? Math.Max(0, api.Server.Config.MaxClients),
                reservedEligibleOnline = playerList?.ReservedEligibleOnlineCount ?? 0,
                publicOnline = playerList?.PublicOnlineCount ?? (api.World.AllOnlinePlayers?.Length ?? 0),
                reservedSlotRejections = playerList?.ReservedSlotRejections ?? 0,
                configuredRoles = api.Server.Config.Roles.Select(r => new { code = r.Code ?? "", name = r.Name ?? "" }).ToArray(),
                debugLogging = playerList?.Config.DebugLogging ?? false,
                rosterSource = playerList?.RosterSource ?? "Unavailable",
                suiteRequiredOnClient = false,
                suiteVersion = SuiteRelease.Version,
                backendChannel = PublicPlayerListServer.ChannelName,
                backendBuild = "suite-" + SuiteRelease.Version + "-playerlist-v5",
                protocolVersion = 1,
                snapshotCacheMs = 750,
                requestThrottleMs = 750,
                companionModId = "newfiesplayerlist",
                companionRecommendedVersion = "1.1.1",
                requestsReceived = playerList?.RequestsReceived ?? 0,
                responsesSent = playerList?.ResponsesSent ?? 0,
                requestsThrottled = playerList?.RequestsThrottled ?? 0,
                responseSendFailures = playerList?.ResponseSendFailures ?? 0,
                requestBuildFailures = playerList?.RequestBuildFailures ?? 0,
                clientHellosReceived = playerList?.ClientHellosReceived ?? 0,
                cachedPlayerCount = playerList?.CachedPlayerCount ?? 0,
                cachedSnapshotAgeMs = playerList?.CachedSnapshotAgeMs ?? -1
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/playerlist/config")
        {
            var incoming = new PublicPlayerListConfig
            {
                ServerName = GetJsonString(request.Body, "serverName", ""),
                BackgroundOpacity = GetJsonDouble(request.Body, "backgroundOpacity", 0.72),
                ShowRoles = GetJsonBool(request.Body, "showRoles", true),
                ShowPing = GetJsonBool(request.Body, "showPing", true),
                ShowSessionTime = GetJsonBool(request.Body, "showSessionTime", true),
                LayoutScale = GetJsonDouble(request.Body, "layoutScale", 1.0),
                RowsPerColumn = GetJsonInt(request.Body, "rowsPerColumn", 8),
                MaxColumns = GetJsonInt(request.Body, "maxColumns", 4),
                Alignment = GetJsonString(request.Body, "alignment", "CenterTop"),
                OffsetX = GetJsonInt(request.Body, "offsetX", 0),
                OffsetY = GetJsonInt(request.Body, "offsetY", 22),
                ShowPlayerInitials = GetJsonBool(request.Body, "showPlayerInitials", true),
                ShowCapacityBar = GetJsonBool(request.Body, "showCapacityBar", true),
                ShowColumnHeaders = GetJsonBool(request.Body, "showColumnHeaders", false),
                CompactRows = GetJsonBool(request.Body, "compactRows", false),
                ShowPingBars = GetJsonBool(request.Body, "showPingBars", true),
                ShowFooterHint = GetJsonBool(request.Body, "showFooterHint", false),
                AccentHex = GetJsonString(request.Body, "accentHex", "#D6AE63"),
                // Role-name colors are managed from Chat Display. They are restored from the
                // current Player List config on the game thread just before this config is applied.
                UseRoleNameColors = false,
                RoleNameColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ReservedSlotsEnabled = GetJsonBool(request.Body, "reservedSlotsEnabled", false),
                ReservedSlots = GetJsonInt(request.Body, "reservedSlots", 0),
                ReservedSlotRoles = GetJsonString(request.Body, "reservedSlotRoles", "")
                    .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                ReservedSlotsFullMessage = GetJsonString(request.Body, "reservedSlotsFullMessage", "Only reserved slots are left on this server."),
                DebugLogging = GetJsonBool(request.Body, "debugLogging", false)
            };
            incoming.Clamp();

            var payload = await RunOnMainThreadAsync(() =>
            {
                var playerList = module.PlayerList;
                if (playerList == null)
                    return new { success = false, message = "Player-list backend is unavailable.", effectiveServerName = "", configSchemaVersion = 6 };

                // Keep the shared role-color settings owned by Chat Display when Player List
                // appearance/reserved-slot settings are saved from their own page.
                incoming.UseRoleNameColors = playerList.Config.UseRoleNameColors;
                incoming.RoleNameColors = new Dictionary<string, string>(
                    playerList.Config.RoleNameColors ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
                incoming.Clamp();
                playerList.ApplyConfig(incoming);
                RecordWebAudit(
                    "playerlist-config",
                    $"Header='{playerList.DisplayServerName}', opacity={playerList.Config.BackgroundOpacity:P0}, scale={playerList.Config.LayoutScale:0.00}, rows={playerList.Config.RowsPerColumn}, columns={playerList.Config.MaxColumns}, align={playerList.Config.Alignment}, roleColors={(playerList.Config.UseRoleNameColors ? playerList.Config.RoleNameColors?.Count ?? 0 : 0)}, reserved={(playerList.Config.ReservedSlotsEnabled ? playerList.Config.ReservedSlots : 0)}, reservedRoles={playerList.Config.ReservedSlotRoles.Count}, debug={(playerList.Config.DebugLogging ? "on" : "off")}",
                    "Success",
                    remote.ToString(),
                    requestActor);

                return new
                {
                    success = true,
                    message = "Player-list configuration saved.",
                    effectiveServerName = playerList.DisplayServerName,
                    configSchemaVersion = playerList.Config.SchemaVersion
                };
            }, "web-playerlist-config-save").ConfigureAwait(false);

            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/collection-audit")
        {
            await WriteJsonAsync(
                stream,
                200,
                BuildCollectionAuditSnapshot(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/compatibility/ignore")
        {
            string fingerprint = GetJsonString(request.Body, "fingerprint", "").Trim().ToLowerInvariant();
            string reason = GetJsonString(request.Body, "reason", "").Trim();
            if (fingerprint.Length != 64)
            {
                await WriteJsonAsync(stream, 400, new { success = false, message = "Invalid compatibility fingerprint." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            compatibilityIgnores.Ignores ??= new List<CompatibilityIgnoreRecord>();
            compatibilityIgnores.Ignores.RemoveAll(x => string.Equals(x.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            compatibilityIgnores.Ignores.Add(new CompatibilityIgnoreRecord
            {
                Fingerprint = fingerprint, Reason = reason, IgnoredBy = requestActor,
                IgnoredUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            SaveCompatibilityIgnores();
            RecordWebAudit("compatibility-ignore", fingerprint[..12], string.IsNullOrWhiteSpace(reason) ? "Ignored" : "Ignored: " + reason, remoteText, requestActor);
            await WriteJsonAsync(stream, 200, new { success = true, message = "Compatibility notification ignored." }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/compatibility/unignore")
        {
            string fingerprint = GetJsonString(request.Body, "fingerprint", "").Trim().ToLowerInvariant();
            compatibilityIgnores.Ignores ??= new List<CompatibilityIgnoreRecord>();
            int removed = compatibilityIgnores.Ignores.RemoveAll(x => string.Equals(x.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) SaveCompatibilityIgnores();
            RecordWebAudit("compatibility-unignore", fingerprint.Length >= 12 ? fingerprint[..12] : fingerprint, removed > 0 ? "Restored" : "Not found", remoteText, requestActor);
            await WriteJsonAsync(stream, 200, new { success = true, message = removed > 0 ? "Compatibility notification restored." : "Ignore rule was already absent." }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && (path == "/api/compatibility" || path == "/api/compatibility/deep"))
        {
            bool deep = path == "/api/compatibility/deep";
            var payload = await RunOnMainThreadAsync(
                () => BuildCompatibilitySnapshot(deep),
                deep ? "web-compatibility-deep" : "web-compatibility").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }


        if (request.Method == "GET" && path == "/api/custom-patches")
        {
            var payload = await RunOnMainThreadAsync(
                () => module.CustomPatches?.GetSnapshot(module.ServerHealth?.GetSnapshot(120, 300)) ?? new CustomPatchSnapshot { Available = false, Note = "Patch Studio is unavailable." },
                "web-custom-patches").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/custom-patches/generate")
        {
            CustomPatchGenerateRequest? incoming = null;
            try { incoming = JsonSerializer.Deserialize<CustomPatchGenerateRequest>(request.Body, jsonOptions); } catch { }
            var result = module.CustomPatches?.Generate(incoming ?? new CustomPatchGenerateRequest())
                ?? new CustomPatchActionResult { Success = false, Message = "Patch Studio is unavailable." };
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/custom-patches/save")
        {
            CustomPatchDefinition? incoming = null;
            try { incoming = JsonSerializer.Deserialize<CustomPatchDefinition>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { success = false, message = "Invalid patch definition: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => module.CustomPatches?.SaveFromRequest(incoming ?? new CustomPatchDefinition())
                    ?? new CustomPatchActionResult { Success = false, Message = "Patch Studio is unavailable." },
                "web-custom-patches-save").ConfigureAwait(false);
            RecordWebAudit("custom-patch-save", incoming?.Name ?? "", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/custom-patches/action")
        {
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string id = GetJsonString(request.Body, "id", "").Trim();
            CustomPatchActionResult result = action switch
            {
                "enable" => await RunOnMainThreadAsync(() => module.CustomPatches?.SetEnabled(id, true) ?? new CustomPatchActionResult { Success = false, Message = "Patch Studio is unavailable." }, "web-custom-patches-enable").ConfigureAwait(false),
                "disable" => await RunOnMainThreadAsync(() => module.CustomPatches?.SetEnabled(id, false) ?? new CustomPatchActionResult { Success = false, Message = "Patch Studio is unavailable." }, "web-custom-patches-disable").ConfigureAwait(false),
                "delete" => await RunOnMainThreadAsync(() => module.CustomPatches?.Delete(id) ?? new CustomPatchActionResult { Success = false, Message = "Patch Studio is unavailable." }, "web-custom-patches-delete").ConfigureAwait(false),
                _ => new CustomPatchActionResult { Success = false, Message = "Unknown patch action." }
            };
            RecordWebAudit("custom-patch-" + action, id, result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/content-patches/assets")
        {
            var result = module.CustomPatches?.GetContentAssets(GetQuery(request, "kind", ""), GetQuery(request, "search", ""));
            await WriteJsonAsync(stream, result == null ? 503 : 200, result ?? new { error = "Patch Generator is unavailable." }, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request.Method == "GET" && path == "/api/content-patches/export")
        {
            try
            {
                byte[]? bytes = module.CustomPatches?.ExportContentPatchMod();
                if (bytes == null) throw new InvalidOperationException("Patch Generator is unavailable.");
                await WriteResponseAsync(stream, 200, "application/zip", bytes, cancellationToken,
                    new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"NewfiesSuiteContentPatches.zip\"" }).ConfigureAwait(false);
            }
            catch (Exception ex) { await WriteJsonAsync(stream, 400, new { error = ex.Message }, cancellationToken).ConfigureAwait(false); }
            return;
        }
        if (request.Method == "GET" && path == "/api/content-patches/asset")
        {
            try
            {
                var result = module.CustomPatches?.InspectContentAsset(GetQuery(request, "file", ""));
                await WriteJsonAsync(stream, result == null ? 503 : 200, (object?)result ?? new { error = "Patch Generator is unavailable." }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) { await WriteJsonAsync(stream, 400, new { error = ex.Message }, cancellationToken).ConfigureAwait(false); }
            return;
        }
        if (request.Method == "POST" && path == "/api/content-patches/build")
        {
            ContentPatchGenerateRequest? incoming;
            try { incoming = JsonSerializer.Deserialize<ContentPatchGenerateRequest>(request.Body, jsonOptions); }
            catch { await WriteJsonAsync(stream, 400, new { error = "Invalid generator request." }, cancellationToken).ConfigureAwait(false); return; }
            var result = module.CustomPatches?.SaveGeneratedContentPatch(incoming ?? new ContentPatchGenerateRequest())
                ?? new ContentPatchActionResult { Success = false, Message = "Patch Generator is unavailable." };
            RecordWebAudit("content-patch-build", incoming?.Name ?? "", result.Success ? "Restart required" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request.Method == "GET" && path == "/api/content-patches")
        {
            var payload = module.CustomPatches?.GetContentSnapshot()
                ?? new ContentPatchSnapshot { Available = false, Note = "Content Patch Studio is unavailable." };
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/content-patches/generate")
        {
            ContentPatchGenerateRequest? incoming = null;
            try { incoming = JsonSerializer.Deserialize<ContentPatchGenerateRequest>(request.Body, jsonOptions); } catch { }
            var result = module.CustomPatches?.GenerateContentPatch(incoming ?? new ContentPatchGenerateRequest())
                ?? new ContentPatchActionResult { Success = false, Message = "Content Patch Studio is unavailable." };
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/content-patches/save")
        {
            ContentPatchDefinition? incoming = null;
            try { incoming = JsonSerializer.Deserialize<ContentPatchDefinition>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { success = false, message = "Invalid content patch definition: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = module.CustomPatches?.SaveContentPatch(incoming ?? new ContentPatchDefinition())
                ?? new ContentPatchActionResult { Success = false, Message = "Content Patch Studio is unavailable." };
            RecordWebAudit("content-patch-save", incoming?.Name ?? "", result.Success ? "Restart required" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/content-patches/action")
        {
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string id = GetJsonString(request.Body, "id", "").Trim();
            ContentPatchActionResult result = action switch
            {
                "enable" => module.CustomPatches?.SetContentPatchEnabled(id, true) ?? new ContentPatchActionResult { Success = false, Message = "Content Patch Studio is unavailable." },
                "disable" => module.CustomPatches?.SetContentPatchEnabled(id, false) ?? new ContentPatchActionResult { Success = false, Message = "Content Patch Studio is unavailable." },
                "delete" => module.CustomPatches?.DeleteContentPatch(id) ?? new ContentPatchActionResult { Success = false, Message = "Content Patch Studio is unavailable." },
                _ => new ContentPatchActionResult { Success = false, Message = "Unknown content patch action." }
            };
            RecordWebAudit("content-patch-" + action, id, result.Success ? "Restart required" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/log-intelligence")
        {
            var payload = await RunOnMainThreadAsync(
                () => logIntelligence?.GetSnapshot() ?? new LogIntelligenceSnapshot(),
                "web-log-intelligence").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/log-intelligence/block-entity")
        {
            string key = GetQuery(request, "key", "").Trim();
            var payload = await RunOnMainThreadAsync(
                () => InspectBadBlockEntity(key),
                "web-bad-block-entity-inspect").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/player-analytics")
        {
            int days = Math.Clamp(GetQueryInt(request, "days", 30), 7, 365);
            var payload = await RunOnMainThreadAsync(
                () => module.PlayerAnalytics?.GetSnapshot(days) ?? new PlayerCountAnalyticsSnapshot(),
                "web-player-analytics").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/server-status")
        {
            int days = Math.Clamp(GetQueryInt(request, "days", 30), 7, 365);
            var payload = await RunOnMainThreadAsync(
                () => BuildServerStatusSnapshot(days),
                "web-server-status").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/anticheat/status")
        {
            var payload = await GetCachedMainThreadReadAsync(
                "anticheat-nav-status", 2000,
                () => module.AntiCheat?.GetNavStatusSnapshot() ?? new AntiCheatNavStatusWebSnapshot(),
                "web-anticheat-nav-status").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/anticheat")
        {
            var payload = await GetCachedMainThreadReadAsync(
                "anticheat", 2000,
                () => module.AntiCheat?.GetSnapshot() ?? new AntiCheatWebSnapshot(),
                "web-anticheat").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/anticheat/client-mods")
        {
            var payload = await GetCachedMainThreadReadAsync(
                "anticheat-client-mods", 5000,
                () => module.AntiCheat?.GetClientModsSnapshot() ?? new List<AntiCheatPlayerWebSnapshot>(),
                "web-anticheat-client-mods").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/anticheat/player")
        {
            string player = GetQuery(request, "player", "").Trim();
            if (string.IsNullOrWhiteSpace(player))
            {
                await WriteJsonAsync(stream, 400, new { error = "player is required." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var payload = await RunOnMainThreadAsync(
                () => module.AntiCheat?.GetPlayerSnapshot(player),
                "web-anticheat-player").ConfigureAwait(false);
            if (payload == null)
            {
                await WriteJsonAsync(stream, 404, new { error = "No anti-cheat session was found for that player." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/anticheat/history")
        {
            string player = GetQuery(request, "player", "").Trim();
            string finding = GetQuery(request, "finding", "").Trim();
            int limit = Math.Clamp(GetQueryInt(request, "limit", 250), 1, 1000);
            // History owns its own synchronization and does not touch live game entities.
            // Keeping this CPU-heavy correlation work off the game thread prevents dashboard
            // history views from stalling gameplay.
            var payload = module.AntiCheat?.GetHistorySnapshot(player, limit, finding) ?? new AntiCheatHistoryWebSnapshot();
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/history/clear")
        {
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.ClearHistory() ?? (Success: false, Message: "Anti-cheat service is unavailable.", Cleared: 0),
                "web-anticheat-history-clear").ConfigureAwait(false);
            RecordWebAudit(
                "anticheat-history-clear-all",
                $"cleared={result.Cleared}",
                result.Success ? "Success" : result.Message,
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message, cleared = result.Cleared }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/settings")
        {
            AntiCheatModuleSaveRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<AntiCheatModuleSaveRequest>(request.Body, jsonOptions);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid Anti-Cheat module configuration: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.SaveModuleSettings(payload?.Enabled ?? false) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-settings").ConfigureAwait(false);
            RecordWebAudit(
                "anticheat-module-state",
                $"enabled={payload?.Enabled}",
                result.Success ? "Success" : result.Message,
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }


        if (request.Method == "GET" && path == "/api/anticheat/full-config")
        {
            var json = await RunOnMainThreadAsync(
                () => module.AntiCheat?.GetFullConfigJson() ?? "{}",
                "web-anticheat-full-config-read").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, new { json }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/full-config")
        {
            AntiCheatFullConfigSaveRequest? payload;
            try { payload = JsonSerializer.Deserialize<AntiCheatFullConfigSaveRequest>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid full Anti-Cheat configuration request: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.SaveFullConfigJson(payload?.Json ?? "") ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-full-config-save").ConfigureAwait(false);
            RecordWebAudit("anticheat-full-config", "full editable configuration", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/client-settings")
        {
            AntiCheatClientScanSettingsSaveRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<AntiCheatClientScanSettingsSaveRequest>(request.Body, jsonOptions);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid Client Integrity scan configuration: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.SaveClientScanSettings(payload ?? new AntiCheatClientScanSettingsSaveRequest()) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-client-settings").ConfigureAwait(false);
            RecordWebAudit(
                "anticheat-client-settings",
                $"requireClient={payload?.RequireClient};modDb={payload?.ClientModDbVerificationEnabled};hardened={payload?.RequireHardenedClientCapabilities};hello={payload?.ClientHelloGraceSeconds}+{payload?.ClientHelloDisconnectDelaySeconds};startup={payload?.StartupChallengeTimeoutSeconds}/{payload?.StartupChallengeHardLimitSeconds};heartbeat={payload?.HeartbeatTimeoutSeconds};rescan={payload?.RescanMinutes}",
                result.Success ? "Success" : result.Message,
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/packetguard")
        {
            AntiCheatPacketGuardSaveRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<AntiCheatPacketGuardSaveRequest>(request.Body, jsonOptions);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid PacketGuard configuration: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.SavePacketGuardSettings(payload ?? new AntiCheatPacketGuardSaveRequest()) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-packetguard").ConfigureAwait(false);
            RecordWebAudit(
                "anticheat-packetguard-save",
                $"enabled={payload?.Enabled};playerWarnings={payload?.PlayerWarningEnabled};noclip={payload?.NoclipEnabled};movement={payload?.MovementEnabled};reach={payload?.ReachEnabled};mining={payload?.MiningEnabled};history={payload?.HistoryEnabled}",
                result.Success ? "Success" : result.Message,
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/oreshield")
        {
            AntiCheatOreShieldSaveRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<AntiCheatOreShieldSaveRequest>(request.Body, jsonOptions);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid Ore Shield configuration: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.SaveOreShieldSettings(payload ?? new AntiCheatOreShieldSaveRequest()) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-oreshield").ConfigureAwait(false);
            RecordWebAudit(
                "anticheat-oreshield-save",
                $"enabled={payload?.Enabled};modded={payload?.ProtectModdedOres};revealUnknown={payload?.RevealOnUnknownNeighbor};strict={payload?.StrictOreMatching};cache={payload?.ChunkCacheEnabled};cacheMiB={payload?.ChunkCacheMiB};maxChunks={payload?.MaxCachedChunks};additional={payload?.AdditionalOrePatterns?.Count ?? 0};excluded={payload?.ExcludedBlockPatterns?.Count ?? 0}",
                result.Success ? "Success" : result.Message,
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/rescan")
        {
            string player = GetJsonString(request.Body, "player", "").Trim();
            string message = await RunOnMainThreadAsync(
                () => module.AntiCheat?.RequestRescan(player) ?? "Anti-cheat service is unavailable.",
                "web-anticheat-rescan").ConfigureAwait(false);
            bool success = message.StartsWith("Anti-cheat rescan requested", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("anticheat-rescan", player, success ? "Success" : message, remoteText, requestActor);
            await WriteJsonAsync(stream, success ? 200 : 400, new { success, message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/evidence/request")
        {
            AntiCheatEvidenceRequest? payload;
            try { payload = JsonSerializer.Deserialize<AntiCheatEvidenceRequest>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid suspicious package request: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            string player = (payload?.Player ?? "").Trim();
            string modId = (payload?.ModId ?? "").Trim();
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.RequestSuspiciousModPackage(player, modId, requestActor) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-evidence-request").ConfigureAwait(false);
            RecordWebAudit("anticheat-evidence-request", $"player={player};mod={modId}", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/evidence/analyze")
        {
            AntiCheatEvidenceAnalyzeRequest? payload;
            try { payload = JsonSerializer.Deserialize<AntiCheatEvidenceAnalyzeRequest>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid evidence comparison request: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            string fileName = (payload?.FileName ?? "").Trim();
            // File comparison only touches the saved evidence ZIP and synchronized Anti-Cheat caches;
            // keep archive hashing off the main game thread.
            var result = module.AntiCheat?.AnalyzeEvidencePackageAgainstModDb(fileName) ?? (Success: false, Message: "Anti-cheat service is unavailable.");
            RecordWebAudit("anticheat-evidence-moddb-compare", fileName, result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/evidence/settings")
        {
            AntiCheatEvidenceSettingsSaveRequest? payload;
            try { payload = JsonSerializer.Deserialize<AntiCheatEvidenceSettingsSaveRequest>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid evidence collection settings: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.SaveEvidenceCollectionSettings(payload ?? new AntiCheatEvidenceSettingsSaveRequest()) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-evidence-settings").ConfigureAwait(false);
            RecordWebAudit("anticheat-evidence-settings", $"enabled={payload?.Enabled};maxMb={payload?.MaxMb};timeout={payload?.TimeoutSeconds};decline={payload?.DeclineAction};hours={payload?.DeclineTempBanHours}", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/moddb/approval")
        {
            AntiCheatClientModApprovalRequest? payload;
            try { payload = JsonSerializer.Deserialize<AntiCheatClientModApprovalRequest>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid client-mod approval request: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var requestPayload = payload ?? new AntiCheatClientModApprovalRequest();
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.UpdateApprovedClientModFingerprint(requestPayload, requestActor) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-moddb-approval").ConfigureAwait(false);
            RecordWebAudit("anticheat-moddb-approval-" + ((requestPayload.Action ?? "approve").Trim().ToLowerInvariant()), string.IsNullOrWhiteSpace(requestPayload.ModId) ? requestPayload.Id : requestPayload.ModId + "@" + requestPayload.Version, result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/moddb/approval/upload")
        {
            PruneAntiCheatApprovalUploads();
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();

            if (action == "start")
            {
                string fileName = Path.GetFileName(GetJsonString(request.Body, "fileName", "")).Trim();
                long expectedBytes = GetJsonLong(request.Body, "sizeBytes", 0);
                string label = GetJsonString(request.Body, "label", "").Trim();
                string notes = GetJsonString(request.Body, "notes", "").Trim();

                if (!Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(stream, 400, new { success = false, message = "Select a Vintage Story mod ZIP file." }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (expectedBytes <= 0 || expectedBytes > MaxAntiCheatApprovalUploadBytes)
                {
                    await WriteJsonAsync(stream, 400, new { success = false, message = $"ZIP size must be between 1 byte and {MaxAntiCheatApprovalUploadBytes / (1024 * 1024)} MB." }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                string uploadId = Guid.NewGuid().ToString("N");
                string tempPath = Path.Combine(GetAntiCheatApprovalUploadRoot(), uploadId + ".zip");
                try
                {
                    using (File.Create(tempPath)) { }
                }
                catch (Exception ex)
                {
                    await WriteJsonAsync(stream, 500, new { success = false, message = "Could not create the temporary fingerprint upload: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var state = new AntiCheatApprovalUploadState
                {
                    UploadId = uploadId,
                    TempPath = tempPath,
                    FileName = fileName,
                    ExpectedBytes = expectedBytes,
                    Label = label.Length > 160 ? label[..160] : label,
                    Notes = notes.Length > 1200 ? notes[..1200] : notes,
                    Actor = requestActor,
                    CreatedUtc = DateTime.UtcNow
                };
                antiCheatApprovalUploads[uploadId] = state;
                await WriteJsonAsync(stream, 200, new
                {
                    success = true,
                    uploadId,
                    expectedBytes,
                    maxChunkBytes = MaxAntiCheatApprovalChunkBytes,
                    message = "Upload session created. The ZIP will only be inspected and hashed; it will not be installed or executed."
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            string uploadIdValue = GetJsonString(request.Body, "uploadId", "").Trim();
            if (uploadIdValue.Length == 0 || !antiCheatApprovalUploads.TryGetValue(uploadIdValue, out var uploadState))
            {
                await WriteJsonAsync(stream, 404, new { success = false, message = "Fingerprint upload session was not found or expired." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!string.Equals(uploadState.Actor, requestActor, StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 403, new { success = false, message = "This upload session belongs to a different WebPanel account." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (action == "cancel")
            {
                RemoveAntiCheatApprovalUpload(uploadIdValue);
                await WriteJsonAsync(stream, 200, new { success = true, message = "Fingerprint upload cancelled." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (action == "chunk")
            {
                long offset = GetJsonLong(request.Body, "offset", -1);
                string dataBase64 = GetJsonString(request.Body, "dataBase64", "");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(dataBase64); }
                catch (FormatException)
                {
                    await WriteJsonAsync(stream, 400, new { success = false, message = "Upload chunk was not valid base64." }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (bytes.Length == 0 || bytes.Length > MaxAntiCheatApprovalChunkBytes)
                {
                    await WriteJsonAsync(stream, 400, new { success = false, message = $"Upload chunks must contain 1 to {MaxAntiCheatApprovalChunkBytes} raw bytes." }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                long received;
                lock (uploadState.Gate)
                {
                    if (offset != uploadState.ReceivedBytes)
                    {
                        received = uploadState.ReceivedBytes;
                    }
                    else if (uploadState.ReceivedBytes + bytes.Length > uploadState.ExpectedBytes)
                    {
                        received = -2;
                    }
                    else
                    {
                        try
                        {
                            using var fs = new FileStream(uploadState.TempPath, FileMode.Open, FileAccess.Write, FileShare.None);
                            fs.Position = uploadState.ReceivedBytes;
                            fs.Write(bytes, 0, bytes.Length);
                            uploadState.ReceivedBytes += bytes.Length;
                            received = uploadState.ReceivedBytes;
                        }
                        catch
                        {
                            received = -3;
                        }
                    }
                }

                if (received == -2)
                {
                    RemoveAntiCheatApprovalUpload(uploadIdValue);
                    await WriteJsonAsync(stream, 400, new { success = false, message = "Upload exceeded the declared ZIP size and was cancelled." }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (received == -3)
                {
                    RemoveAntiCheatApprovalUpload(uploadIdValue);
                    await WriteJsonAsync(stream, 500, new { success = false, message = "Could not write the uploaded ZIP chunk." }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (received != offset + bytes.Length)
                {
                    await WriteJsonAsync(stream, 409, new { success = false, message = "Upload offset did not match the server state.", receivedBytes = received }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(stream, 200, new
                {
                    success = true,
                    receivedBytes = received,
                    expectedBytes = uploadState.ExpectedBytes,
                    complete = received == uploadState.ExpectedBytes
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (action == "commit")
            {
                lock (uploadState.Gate)
                {
                    if (uploadState.ReceivedBytes != uploadState.ExpectedBytes)
                    {
                        // Leave the upload alive so the browser can finish any missing chunks.
                        uploadState.ReceivedBytes = Math.Max(0, uploadState.ReceivedBytes);
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
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var antiCheat = module.AntiCheat;
                if (antiCheat == null)
                {
                    RemoveAntiCheatApprovalUpload(uploadIdValue);
                    await WriteJsonAsync(stream, 503, new { success = false, message = "Anti-cheat service is unavailable." }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var inspection = antiCheat.InspectClientModPackageForApproval(
                    uploadState.TempPath,
                    uploadState.FileName,
                    uploadState.Label,
                    uploadState.Notes);
                if (!inspection.Success || inspection.Approval == null)
                {
                    RemoveAntiCheatApprovalUpload(uploadIdValue);
                    RecordWebAudit("anticheat-moddb-upload-rejected", uploadState.FileName, inspection.Message, remoteText, requestActor);
                    await WriteJsonAsync(stream, 400, new { success = false, message = inspection.Message }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var approval = inspection.Approval;
                var result = await RunOnMainThreadAsync(
                    () => module.AntiCheat?.UpdateApprovedClientModFingerprint(approval, requestActor)
                        ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                    "web-anticheat-moddb-upload-approval").ConfigureAwait(false);

                RemoveAntiCheatApprovalUpload(uploadIdValue);
                RecordWebAudit(
                    "anticheat-moddb-upload-approval",
                    $"{approval.ModId}@{approval.Version};file={uploadState.FileName};bytes={uploadState.ExpectedBytes}",
                    result.Success ? "Success" : result.Message,
                    remoteText,
                    requestActor);
                await WriteJsonAsync(stream, result.Success ? 200 : 400, new
                {
                    success = result.Success,
                    message = result.Message,
                    modId = approval.ModId,
                    version = approval.Version,
                    label = approval.Label,
                    contentSha256 = approval.ContentSha256,
                    packageSha256 = approval.PackageSha256,
                    inspected = inspection.Message
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(stream, 400, new { success = false, message = "Unknown fingerprint upload action." }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/moddb/resolve-all")
        {
            bool forceRefresh = GetJsonBool(request.Body, "forceRefresh", false);
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.ResolveAllClientModDbFingerprints(forceRefresh)
                    ?? (Success: false, Message: "Anti-cheat service is unavailable.", UniqueTargets: 0, Queued: 0, FreshCached: 0, AlreadyPending: 0),
                "web-anticheat-moddb-resolve-all").ConfigureAwait(false);
            RecordWebAudit("anticheat-moddb-resolve-all", forceRefresh ? "force-refresh" : "resolve-missing", result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new
            {
                success = result.Success,
                message = result.Message,
                uniqueTargets = result.UniqueTargets,
                queued = result.Queued,
                freshCached = result.FreshCached,
                alreadyPending = result.AlreadyPending
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/blacklist")
        {
            string action = GetJsonString(request.Body, "action", "").Trim();
            string modId = GetJsonString(request.Body, "modId", "").Trim();
            string sourcePlayer = GetJsonString(request.Body, "player", "").Trim();
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.UpdateForbiddenMod(action, modId, sourcePlayer) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-blacklist").ConfigureAwait(false);
            RecordWebAudit(
                "anticheat-blacklist-" + (string.IsNullOrWhiteSpace(action) ? "unknown" : action.ToLowerInvariant()),
                modId,
                result.Success ? "Success" : result.Message,
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/blacklist/profile")
        {
            string action = GetJsonString(request.Body, "action", "").Trim();
            string profileId = GetJsonString(request.Body, "profileId", "").Trim();
            string name = GetJsonString(request.Body, "name", "").Trim();
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.UpdateBlacklistProfile(action, profileId, name) ?? (Success: false, Message: "Anti-cheat service is unavailable.", ProfileId: ""),
                "web-anticheat-blacklist-profile").ConfigureAwait(false);
            RecordWebAudit(
                "anticheat-blacklist-profile-" + (string.IsNullOrWhiteSpace(action) ? "unknown" : action.ToLowerInvariant()),
                string.IsNullOrWhiteSpace(name) ? profileId : name,
                result.Success ? "Success" : result.Message,
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message, profileId = result.ProfileId }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/blacklist/resolve")
        {
            string modId = GetJsonString(request.Body, "modId", "").Trim().ToLowerInvariant();
            bool isBlacklisted = await RunOnMainThreadAsync(
                () => module.AntiCheat?.Config.ForbiddenModIds.Any(x => string.Equals(x?.Trim(), modId, StringComparison.OrdinalIgnoreCase)) ?? false,
                "web-anticheat-blacklist-resolve-check").ConfigureAwait(false);
            if (!isBlacklisted)
            {
                const string notBlacklisted = "Add the mod ID to the blacklist before resolving its Mod DB fingerprint.";
                RecordWebAudit("anticheat-blacklist-resolve", modId, notBlacklisted, remoteText, requestActor);
                await WriteJsonAsync(stream, 400, new { success = false, message = notBlacklisted }, cancellationToken).ConfigureAwait(false);
                return;
            }

            int releaseDepth = await RunOnMainThreadAsync(
                () => module.AntiCheat?.GetFingerprintReleaseDepth() ?? AntiCheatModDbFingerprintResolver.DefaultReleaseFingerprints,
                "web-anticheat-blacklist-release-depth").ConfigureAwait(false);
            var resolution = await AntiCheatModDbFingerprintResolver.ResolveAsync(modId, TargetGameVersion, releaseDepth, cancellationToken).ConfigureAwait(false);
            if (!resolution.Success)
            {
                RecordWebAudit("anticheat-blacklist-resolve", modId, resolution.Message, remoteText, requestActor);
                await WriteJsonAsync(stream, 502, new { success = false, message = resolution.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.ApplyResolvedForbiddenFingerprint(resolution) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-blacklist-resolve-apply").ConfigureAwait(false);
            RecordWebAudit("anticheat-blacklist-resolve", modId, result.Success ? $"Success: {resolution.Version}" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new
            {
                success = result.Success,
                message = result.Message,
                modId = resolution.ModId,
                version = resolution.Version,
                fileName = resolution.FileName,
                packageSha256 = resolution.PackageSha256,
                contentSha256 = resolution.ContentSha256,
                assemblyCount = resolution.AssemblySha256s.Count,
                releaseCount = resolution.Releases.Count,
                versions = resolution.Releases.Select(x => x.Version).ToList()
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/blacklist/fingerprint-settings")
        {
            int releaseDepth = 6;
            try
            {
                using var doc = JsonDocument.Parse(request.Body);
                if (doc.RootElement.TryGetProperty("releaseDepth", out var depth) && depth.TryGetInt32(out int parsed)) releaseDepth = parsed;
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { success = false, message = "Invalid fingerprint settings: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.UpdateFingerprintReleaseDepth(releaseDepth) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-fingerprint-settings").ConfigureAwait(false);
            RecordWebAudit("anticheat-fingerprint-depth", releaseDepth.ToString(), result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message, releaseDepth = Math.Clamp(releaseDepth, 1, AntiCheatModDbFingerprintResolver.MaxReleaseFingerprints) }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/blacklist/manual")
        {
            AntiCheatManualFingerprintSaveRequest payload;
            try
            {
                payload = JsonSerializer.Deserialize<AntiCheatManualFingerprintSaveRequest>(request.Body, jsonOptions) ?? new AntiCheatManualFingerprintSaveRequest();
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { success = false, message = "Invalid manual fingerprint payload: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.UpdateManualForbiddenFingerprint(payload) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-blacklist-manual").ConfigureAwait(false);
            RecordWebAudit("anticheat-blacklist-manual-" + ((payload.Action ?? "save").Trim().ToLowerInvariant()), payload.ModId, result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/integrity-policy")
        {
            string action = GetJsonString(request.Body, "action", "").Trim();
            string hash = GetJsonString(request.Body, "hash", "").Trim();
            bool? requireApproved = null;
            try
            {
                using var doc = JsonDocument.Parse(request.Body);
                if (doc.RootElement.TryGetProperty("requireApproved", out var req) && (req.ValueKind == JsonValueKind.True || req.ValueKind == JsonValueKind.False)) requireApproved = req.GetBoolean();
            }
            catch { }
            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.UpdateClientIntegrityPolicy(action, hash, requireApproved) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-integrity-policy").ConfigureAwait(false);
            RecordWebAudit("anticheat-integrity-policy", action + (hash.Length > 0 ? ":" + hash[..Math.Min(12, hash.Length)] : ""), result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/anticheat/consequences")
        {
            AntiCheatConsequenceSaveRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<AntiCheatConsequenceSaveRequest>(request.Body, jsonOptions);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid consequence configuration: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await RunOnMainThreadAsync(
                () => module.AntiCheat?.SaveConsequenceRules(payload?.Rules, payload?.ApplyCurrent ?? false) ?? (Success: false, Message: "Anti-cheat service is unavailable."),
                "web-anticheat-consequences").ConfigureAwait(false);
            RecordWebAudit(
                "anticheat-consequences-save",
                $"rules={payload?.Rules?.Count ?? 0};applyCurrent={payload?.ApplyCurrent ?? false}",
                result.Success ? "Success" : result.Message,
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/customization")
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
                theme=suiteCustomization.Theme,backgroundHex=suiteCustomization.BackgroundHex,panelHex=suiteCustomization.PanelHex,textHex=suiteCustomization.TextHex,logoUrl=suiteCustomization.LogoUrl,reduceMotion=suiteCustomization.ReduceMotion,
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
                buildId = BuildId
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/customization")
        {
            SuiteCustomizationConfig? payload;
            try
            {
                payload = JsonSerializer.Deserialize<SuiteCustomizationConfig>(request.Body, jsonOptions);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid Suite customization settings: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (payload == null)
            {
                await WriteJsonAsync(stream, 400, new { error = "Suite customization settings are required." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            payload.Clamp();
            BlockLogger.Services.SuiteConfigStorage.Store(api,payload,SuiteCustomizationConfig.ConfigFile);
            suiteCustomization = payload;
            RecordWebAudit("suite-customization", $"panel={suiteCustomization.PanelName};accent={suiteCustomization.AccentHex}", "Success", remoteText, requestActor);
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
                theme=suiteCustomization.Theme,backgroundHex=suiteCustomization.BackgroundHex,panelHex=suiteCustomization.PanelHex,textHex=suiteCustomization.TextHex,logoUrl=suiteCustomization.LogoUrl,reduceMotion=suiteCustomization.ReduceMotion,
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
                    buildId = BuildId
                }
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/community-motd")
        {
            var snapshot = await RunOnMainThreadAsync(
                () => CommunityMotdControlBridge.Request(api, "get"),
                "web-community-motd-get").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, snapshot, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/community-motd")
        {
            string action = "save";
            string configJson = "";
            try
            {
                using var doc = JsonDocument.Parse(request.Body);
                if (doc.RootElement.TryGetProperty("action", out var actionNode) && actionNode.ValueKind == JsonValueKind.String)
                    action = (actionNode.GetString() ?? "save").Trim().ToLowerInvariant();
                if (doc.RootElement.TryGetProperty("config", out var configNode))
                    configJson = configNode.GetRawText();
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid Community MOTD request: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            string operation = action switch
            {
                "save" or "apply" => "apply",
                "reload" => "reload",
                "resetseen" or "reset-seen" => "resetseen",
                _ => action
            };

            var result = await RunOnMainThreadAsync(
                () => CommunityMotdControlBridge.Request(api, operation, configJson),
                "web-community-motd-" + operation).ConfigureAwait(false);

            RecordWebAudit("community-motd-" + operation, result.Version, result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : (result.Available ? 400 : 503), result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path is "/api/roles/effective" or "/api/roles/preview" or "/api/roles/batch")
        {
            var roles = new RoleManagementService(api, () => module.PlayerCommands?.Config, () => module.PlayerList);
            object payload;
            try
            {
                if (request.Method == "GET" && path == "/api/roles/effective")
                    payload = await RunOnMainThreadAsync(() => roles.PreviewPlayer(GetQuery(request, "player", ""), GetQuery(request, "role", "")), "web-role-effective", skipIfTimedOut: true).ConfigureAwait(false);
                else if (request.Method == "POST" && path == "/api/roles/preview")
                {
                    var draft = JsonSerializer.Deserialize<RoleEditorRequest>(request.Body, jsonOptions);
                    payload = await RunOnMainThreadAsync(() => roles.PreviewDefinition(draft), "web-role-preview", skipIfTimedOut: true).ConfigureAwait(false);
                }
                else if (request.Method == "POST" && path == "/api/roles/batch")
                {
                    var batch = JsonSerializer.Deserialize<RoleBatchRequest>(request.Body, jsonOptions);
                    payload = await RunOnMainThreadAsync(() => roles.ChangeBatch(batch), "web-role-batch", skipIfTimedOut: true).ConfigureAwait(false);
                    RecordWebAudit("roles-batch", $"{(batch?.Preview == true ? "Preview" : "Assignment")}; target={batch?.RoleCode}; players={string.Join(",", batch?.Players.Select(p => p.PlayerUid) ?? Array.Empty<string>())}", payload.GetType().GetProperty("success")?.GetValue(payload) is true ? "Success" : "Failed", remoteText, requestActor);
                }
                else { await WriteJsonAsync(stream, 405, new { error = "Method not allowed." }, cancellationToken).ConfigureAwait(false); return; }
            }
            catch (JsonException) { await WriteJsonAsync(stream, 400, new { error = "Invalid role request." }, cancellationToken).ConfigureAwait(false); return; }
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false); return;
        }

        if (path == "/api/roles" || path == "/api/roles/players" || path == "/api/roles/player")
        {
            var roles = new RoleManagementService(api, () => module.PlayerCommands?.Config, () => module.PlayerList);
            if(request.Method == "GET" && path == "/api/roles")
            {
                var snapshot = await RunOnMainThreadAsync(roles.GetSnapshot, "web-roles-read", skipIfTimedOut:true).ConfigureAwait(false);
                await WriteJsonAsync(stream,200,snapshot,cancellationToken).ConfigureAwait(false); return;
            }
            if(request.Method == "GET" && path == "/api/roles/players")
            {
                string search=GetQuery(request,"search","");
                var players=await RunOnMainThreadAsync(()=>roles.GetPlayers(search,100),"web-roles-players",skipIfTimedOut:true).ConfigureAwait(false);
                await WriteJsonAsync(stream,200,players,cancellationToken).ConfigureAwait(false); return;
            }
            if(request.Method == "POST" && path != "/api/roles/players")
            {
                RoleEditorResult result;
                try {
                    if(path=="/api/roles/player") {
                        var payload=JsonSerializer.Deserialize<RolePlayerRequest>(request.Body,jsonOptions);
                        result=await RunOnMainThreadAsync(()=>roles.SetPlayerRole(payload),"web-roles-assign",skipIfTimedOut:true).ConfigureAwait(false);
                    } else {
                        var payload=JsonSerializer.Deserialize<RoleEditorRequest>(request.Body,jsonOptions);
                        result=await RunOnMainThreadAsync(()=>roles.Change(payload),"web-roles-edit",skipIfTimedOut:true).ConfigureAwait(false);
                    }
                } catch(JsonException ex) { await WriteJsonAsync(stream,400,new {success=false,message="Invalid role request: "+ex.Message},cancellationToken).ConfigureAwait(false); return; }
                RecordWebAudit("roles-change",path,result.Success?"Success":result.Message,remoteText,requestActor);
                await WriteJsonAsync(stream,result.Success?200:result.Conflict?409:400,result,cancellationToken).ConfigureAwait(false); return;
            }
        }

        if (request.Method == "GET" && path == "/api/playercommands")
        {
            var payload = await RunOnMainThreadAsync(
                () => module.PlayerCommands?.GetSnapshot() ?? new PlayerCommandSnapshot
                {
                    Available = false
                },
                "web-playercommands-status").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/playercommands/permissions")
        {
            var payload = await RunOnMainThreadAsync(() => module.PlayerCommands?.GetPermissionsSnapshot() ?? new PermissionsManagerSnapshot(), "web-playercommands-permissions").ConfigureAwait(false);
            DecorateSupporterNicknames(payload);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/playercommands/permissions")
        {
            PermissionChangeRequest? payload;
            try { payload = JsonSerializer.Deserialize<PermissionChangeRequest>(request.Body, jsonOptions); }
            catch (Exception ex) { await WriteJsonAsync(stream, 400, new { error = "Invalid permission request: " + ex.Message }, cancellationToken).ConfigureAwait(false); return; }
            var result = await RunOnMainThreadAsync(() => module.PlayerCommands?.ChangePermission(payload) ?? new PermissionChangeResult { Success=false, Message="Player Commands feature unavailable." }, "web-playercommands-permission-change").ConfigureAwait(false);
            DecorateSupporterNicknames(result.Snapshot);
            RecordWebAudit("player-permissions", $"{payload?.Action}:{payload?.Value}", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success=result.Success, message=result.Message, permissions=result.Snapshot }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/playercommands/command")
        {
            string key = GetJsonString(request.Body, "key", "").Trim().ToLowerInvariant();
            bool enabled = GetJsonBool(request.Body, "enabled", true);
            var result = await RunOnMainThreadAsync(
                () => module.PlayerCommands?.SetCommandEnabled(key, enabled) ?? new PlayerCommandSaveResult
                {
                    Success = false,
                    Message = "Player Commands feature is unavailable.",
                    Snapshot = new PlayerCommandSnapshot { Available = false }
                },
                "web-playercommands-command-toggle").ConfigureAwait(false);
            RecordWebAudit("playercommands-command", $"{key}={(enabled ? "on" : "off")}", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message, playerCommands = result.Snapshot }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/playercommands/settings")
        {
            PlayerCommandSettingsRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<PlayerCommandSettingsRequest>(request.Body, jsonOptions);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid player command settings: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await RunOnMainThreadAsync(
                () => module.PlayerCommands?.SaveSettings(payload) ?? new PlayerCommandSaveResult
                {
                    Success = false,
                    Message = "Player Commands feature is unavailable. Enable the playercommands feature gate and restart the server.",
                    Snapshot = new PlayerCommandSnapshot { Available = false }
                },
                "web-playercommands-settings").ConfigureAwait(false);
            RecordWebAudit("playercommands-settings", "spawn/msg/reply/starterkit/warps/back/homes/t2p/t2m/rtp", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(
                stream,
                result.Success ? 200 : 400,
                new { success = result.Success, message = result.Message, playerCommands = result.Snapshot },
                cancellationToken).ConfigureAwait(false);
            return;
        }


        if (request.Method == "GET" && path == "/api/warps")
        {
            var payload = await RunOnMainThreadAsync(
                () => module.PlayerCommands?.Warps.GetSnapshot() ?? new WarpSnapshot { Available = false },
                "web-warps-status").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/warps/settings")
        {
            WarpSettingsRequest? payload;
            try { payload = JsonSerializer.Deserialize<WarpSettingsRequest>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid warp settings: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => module.PlayerCommands?.Warps.SaveDefaults(payload) ?? new WarpMutationResult { Success = false, Message = "Player Commands feature is unavailable.", Snapshot = new WarpSnapshot { Available = false } },
                "web-warps-settings").ConfigureAwait(false);
            RecordWebAudit("warps-settings", "defaults", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/warps/upsert")
        {
            WarpMutationRequest? payload;
            try { payload = JsonSerializer.Deserialize<WarpMutationRequest>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid warp: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => module.PlayerCommands?.Warps.Upsert(payload) ?? new WarpMutationResult { Success = false, Message = "Player Commands feature is unavailable.", Snapshot = new WarpSnapshot { Available = false } },
                "web-warps-upsert").ConfigureAwait(false);
            string warpAuditDetail = payload == null ? "" : $"{payload.Name}; access={payload.AccessMode}; roles={string.Join(',', payload.AllowedRoles ?? new List<string>())}; privileges={string.Join(',', payload.RequiredPrivileges ?? new List<string>())}";
            RecordWebAudit("warps-upsert", warpAuditDetail, result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/warps/delete")
        {
            string name = GetJsonString(request.Body, "name", "").Trim();
            var result = await RunOnMainThreadAsync(
                () => module.PlayerCommands?.Warps.Delete(name) ?? new WarpMutationResult { Success = false, Message = "Player Commands feature is unavailable.", Snapshot = new WarpSnapshot { Available = false } },
                "web-warps-delete").ConfigureAwait(false);
            RecordWebAudit("warps-delete", name, result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/warps/reload")
        {
            var result = await RunOnMainThreadAsync(
                () => module.PlayerCommands?.Warps.Reload() ?? new WarpMutationResult { Success = false, Message = "Player Commands feature is unavailable.", Snapshot = new WarpSnapshot { Available = false } },
                "web-warps-reload").ConfigureAwait(false);
            RecordWebAudit("warps-reload", WarpDatabase.ConfigFile, result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/discord")
        {
            // Discord configuration/status changes infrequently compared with dashboard polling.
            // Serve a stale-while-refresh snapshot so normal page reads do not compete for the
            // Vintage Story main thread on every refresh. Non-GET mutations invalidate this cache.
            var payload = await GetCachedMainThreadReadAsync(
                "discord-status",
                5_000,
                () => DiscordControlBridge.Request(api, "get"),
                "web-discord-status").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/discord/settings")
        {
            string key = GetJsonString(request.Body, "key", "").Trim();
            bool boolValue = GetJsonBool(request.Body, "boolValue", false);
            int intValue = GetJsonInt(request.Body, "intValue", 0);
            string stringValue = GetJsonString(request.Body, "stringValue", "");
            string target = GetJsonString(request.Body, "target", "");
            var payload = await RunOnMainThreadAsync(
                () => DiscordControlBridge.Request(api, "set", key, boolValue, intValue, stringValue, target),
                "web-discord-settings").ConfigureAwait(false);
            RecordWebAudit("discord-settings", key, payload.Success ? "Success" : payload.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, payload.Success ? 200 : 400, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/discord/test")
        {
            string target = GetJsonString(request.Body, "target", "chat").Trim();
            var payload = await RunOnMainThreadAsync(
                () => DiscordControlBridge.Request(api, "test", target: target),
                "web-discord-test").ConfigureAwait(false);
            RecordWebAudit("discord-test", target, payload.Success ? "Success" : payload.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, payload.Success ? 200 : 400, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/restarts")
        {
            var payload = await RunOnMainThreadAsync(
                () => module.AutomatedRestarts?.GetSnapshot() ?? new AutomatedRestartSnapshot { Available = false },
                "web-automated-restarts").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/restarts/settings")
        {
            AutomatedRestartSettingsRequest? settings = null;
            try { settings = JsonSerializer.Deserialize<AutomatedRestartSettingsRequest>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { success = false, message = "Invalid automated restart settings: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => module.AutomatedRestarts?.SaveSettings(settings) ?? (false, "Automated restart service is unavailable.", new AutomatedRestartSnapshot { Available = false }),
                "web-automated-restarts-save").ConfigureAwait(false);
            RecordWebAudit("automated-restarts-save", string.Join(",", settings?.RestartTimesUtc ?? new List<string>()), result.Item1 ? "Success" : result.Item2, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Item1 ? 200 : 400, new { success = result.Item1, message = result.Item2, restarts = result.Item3 }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/restarts/action")
        {
            string action = GetJsonString(request.Body, "action", "status").Trim().ToLowerInvariant();
            int customMinutes = Math.Clamp(GetJsonInt(request.Body, "minutes", 10), 1, 24 * 60);
            string customReason = GetJsonString(request.Body, "reason", "Maintenance").Trim();
            var result = await RunOnMainThreadAsync(() =>
            {
                var svc = module.AutomatedRestarts;
                if (svc == null) return (Success: false, Message: "Automated restart service is unavailable.", Snapshot: new AutomatedRestartSnapshot { Available = false });
                string message = action switch
                {
                    "enable" or "on" => svc.SetEnabled(true),
                    "disable" or "off" => svc.SetEnabled(false),
                    "skip" => svc.SkipNext(requestActor),
                    "reload" => svc.Reload(),
                    "schedule-custom" => svc.ScheduleCustomRestart(customMinutes, customReason, requestActor),
                    "cancel-custom" => svc.CancelCustomRestart(requestActor),
                    "status" => svc.StatusText(),
                    _ => "Unknown automated restart action."
                };
                bool success = !message.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) && !message.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
                return (Success: success, Message: message, Snapshot: svc.GetSnapshot());
            }, "web-automated-restarts-action").ConfigureAwait(false);
            RecordWebAudit("automated-restarts-" + action, "schedule", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, message = result.Message, restarts = result.Snapshot }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/chat-display")
        {
            var payload = await RunOnMainThreadAsync(() =>
            {
                var snapshot = module.ChatDisplay?.GetSnapshot() ?? new ChatDisplaySnapshot { Available = false };
                var playerList = module.PlayerList;
                snapshot.RoleColorRevision=RoleManagementService.ColorRevision(playerList?.Config);
                snapshot.UseRoleNameColors = playerList?.Config.UseRoleNameColors ?? false;
                snapshot.RoleNameColors = new Dictionary<string, string>(
                    playerList?.Config.RoleNameColors ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
                return snapshot;
            }, "web-chat-display").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/chat-display/settings")
        {
            ChatDisplaySettingsRequest? settings = null;
            try { settings = JsonSerializer.Deserialize<ChatDisplaySettingsRequest>(request.Body, jsonOptions); }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new { success = false, message = "Invalid chat display settings: " + ex.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(() =>
            {
                var chatDisplay = module.ChatDisplay;
                if (chatDisplay == null)
                    return (Success: false, Message: "Chat display service is unavailable.", Snapshot: new ChatDisplaySnapshot { Available = false });

                if(settings?.RoleColorRevision!=null&&settings.RoleColorRevision!=RoleManagementService.ColorRevision(module.PlayerList?.Config))
                    return (Success:false,Message:"Shared role name colors changed elsewhere. Reload Chat Display before saving; your draft has been kept.",Snapshot:chatDisplay.GetSnapshot());
                var save = chatDisplay.SaveSettings(settings);
                if (save.Success && module.PlayerList != null)
                    module.PlayerList.ApplyRoleNameColors(settings?.UseRoleNameColors ?? false, settings?.RoleNameColors);

                var snapshot = save.Snapshot;
                var playerList = module.PlayerList;
                snapshot.RoleColorRevision=RoleManagementService.ColorRevision(playerList?.Config);
                snapshot.UseRoleNameColors = playerList?.Config.UseRoleNameColors ?? false;
                snapshot.RoleNameColors = new Dictionary<string, string>(
                    playerList?.Config.RoleNameColors ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
                return (Success:save.Success, Message:save.Message, Snapshot:snapshot);
            }, "web-chat-display-save").ConfigureAwait(false);
            RecordWebAudit(
                "chat-display-save",
                $"role={settings?.ShowRoleBeforeName};boldName={settings?.BoldPlayerName};timestamp={settings?.ShowTimestampBeforeMessage};roleColors={(settings?.UseRoleNameColors == true ? settings.RoleNameColors?.Count ?? 0 : 0)}",
                result.Item1 ? "Success" : result.Item2,
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, result.Item1 ? 200 : 400, new { success = result.Item1, message = result.Item2, chatDisplay = result.Item3 }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/chatguard")
        {
            var snapshot = await RunOnMainThreadAsync(
                () => ChatGuardControlBridge.Request(api),
                "web-chatguard-status").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, BuildChatGuardWebPayload(snapshot), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/chatguard/settings")
        {
            var result = await RunOnMainThreadAsync(
                () => ChatGuardControlBridge.Request(api, operation: "save", configJson: request.Body, actor: requestActor),
                "web-chatguard-settings").ConfigureAwait(false);
            RecordWebAudit("chatguard-settings", "ChatGuard settings updated.", result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, BuildChatGuardWebPayload(result), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/chatguard/action")
        {
            string action = GetJsonString(request.Body, "action", "").Trim();
            string targetUid = GetJsonString(request.Body, "targetUid", "").Trim();
            string targetName = GetJsonString(request.Body, "targetName", "").Trim();
            string reason = GetJsonString(request.Body, "reason", "").Trim();
            int minutes = Math.Clamp(GetJsonInt(request.Body, "minutes", 10), 1, 43200);
            bool permanent = GetJsonBool(request.Body, "permanent", false);
            var result = await RunOnMainThreadAsync(
                () => ChatGuardControlBridge.Request(api, operation: "action", action: action, targetUid: targetUid, targetName: targetName, minutes: minutes, permanent: permanent, reason: reason, actor: requestActor),
                "web-chatguard-action").ConfigureAwait(false);
            RecordWebAudit("chatguard-" + action.ToLowerInvariant(), string.IsNullOrWhiteSpace(targetName) ? targetUid : targetName, result.Success ? "Success" : result.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, BuildChatGuardWebPayload(result), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/chatguard/test")
        {
            string targetName = GetJsonString(request.Body, "targetName", "").Trim();
            string testMessage = GetJsonString(request.Body, "message", "");
            if (string.IsNullOrWhiteSpace(testMessage))
            {
                await WriteJsonAsync(stream, 400, new { success = false, message = "Enter a message to test." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => ChatGuardControlBridge.Request(api, operation: "test", targetName: targetName, testMessage: testMessage, actor: requestActor),
                "web-chatguard-test").ConfigureAwait(false);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, BuildChatGuardWebPayload(result), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/announcements")
        {
            var payload = await RunOnMainThreadAsync(
                () => module.Announcements?.GetSnapshot() ?? new BlockLogger.Announcements.AnnouncementSnapshot(),
                "web-announcements").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/announcements/save")
        {
            string message = await RunOnMainThreadAsync(
                () => module.Announcements?.SaveFromJson(request.Body, jsonOptions) ?? "Announcements service is unavailable.",
                "web-announcements-save").ConfigureAwait(false);
            bool success = !message.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase) &&
                           !message.StartsWith("Announcement configuration is required", StringComparison.OrdinalIgnoreCase) &&
                           !message.StartsWith("Announcements service is unavailable", StringComparison.OrdinalIgnoreCase);
            var payload = await RunOnMainThreadAsync(
                () => module.Announcements?.GetSnapshot() ?? new BlockLogger.Announcements.AnnouncementSnapshot(),
                "web-announcements-save-result").ConfigureAwait(false);
            RecordWebAudit("announcements-save", "schedule", success ? "Success" : message, remoteText, requestActor);
            await WriteJsonAsync(stream, success ? 200 : 400, new { success, message, announcements = payload }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/announcements/action")
        {
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string id = GetJsonString(request.Body, "id", "").Trim();
            string message = await RunOnMainThreadAsync(
                () =>
                {
                    var svc = module.Announcements;
                    if (svc == null) return "Announcements service is unavailable.";
                    return action switch
                    {
                        "run" or "test" => svc.RunNow(id),
                        "reset" => svc.ResetRuns(id),
                        _ => "Unknown announcement action."
                    };
                },
                "web-announcements-action").ConfigureAwait(false);
            bool success = !message.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) &&
                           !message.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                           !message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) &&
                           !message.Contains("empty", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("announcements-" + action, id, success ? "Success" : message, remoteText, requestActor);
            await WriteJsonAsync(stream, success ? 200 : 400, new { success, message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/console")
        {
            int lines = Math.Clamp(GetQueryInt(request, "lines", 350), 50, 1000);
            var payload = module.WebConsole?.GetSnapshot(lines, GetQuery(request,"includeChat", "false").Equals("true",StringComparison.OrdinalIgnoreCase)) ?? new BlockLogger.WebConsole.WebConsoleSnapshot();
            payload.BlockEntityAlerts = logIntelligence?.GetBadBlockEntityConsoleAlerts() ?? new BadBlockEntitySnapshot();
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/leaderboard")
        {
            var payload = module.WebMap?.GetLeaderboardSnapshot()
                ?? WebMapLeaderboardSnapshot.Disabled("Leaderboard service is unavailable because the Web Map feature is master-disabled.");
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/webmap")
        {
            WebMapSnapshot payload;
            var webMap = module.WebMap;
            if (webMap?.IsTerrainExportInProgress == true)
            {
                // Startup terrain export intentionally owns the game thread for long stretches.
                // WebMap snapshot state is internally locked/cached, so serve it directly rather
                // than queueing an 8-second main-thread task that cannot run until export finishes.
                payload = webMap.GetSnapshot(null, includeWaypointAudit: true, includeBadBlockSnapshot: false);
            }
            else
            {
                payload = await RunOnMainThreadAsync(
                    () => webMap?.GetSnapshot(logIntelligence?.GetBadBlockEntitySnapshot()) ?? new WebMapSnapshot { Note = "Native Web Map service is unavailable." },
                    "web-map-status").ConfigureAwait(false);
            }
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/webmap/terrain/status")
        {
            var payload = module.WebMap?.GetTerrainStatusSnapshot() ?? new BlockLogger.WebMap.WebMapTerrainExportStatus
            {
                State = "unavailable",
                Phase = "unavailable",
                Message = "Native Web Map service is unavailable."
            };
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/webmap/config")
        {
            var incoming = new WebMapConfig
            {
                // Explicit admin-controlled privacy toggle. The dashboard sends this in
                // lockstep with the Players layer so enabling Players also enables sampling.
                AllowLivePlayerData = GetJsonBool(request.Body, "allowLivePlayerData", false),
                Enabled = GetJsonBool(request.Body, "enabled", false),
                BindAddress = GetJsonString(request.Body, "bindAddress", "0.0.0.0"),
                Port = Math.Clamp(GetJsonInt(request.Body, "port", 9466), 1024, 65535),
                PublicUrl = GetJsonString(request.Body, "publicUrl", ""),
                DashboardUrl = GetJsonString(request.Body, "dashboardUrl", ""),
                Title = GetJsonString(request.Body, "title", "Newfie's Server Map"),
                ShowPlayers = GetJsonBool(request.Body, "showPlayers", false),
                ShowBadBlockEntities = GetJsonBool(request.Body, "showBadBlockEntities", false),
                ShowActivity = GetJsonBool(request.Body, "showActivity", false),
                ShowWaypoints = GetJsonBool(request.Body, "showWaypoints", true),
                ShowLeaderboards = GetJsonBool(request.Body, "showLeaderboards", true),
                LeaderboardShowPlaytime = GetJsonBool(request.Body, "leaderboardShowPlaytime", true),
                LeaderboardShowXSkills = GetJsonBool(request.Body, "leaderboardShowXSkills", true),
                LeaderboardTopPlayers = Math.Clamp(GetJsonInt(request.Body, "leaderboardTopPlayers", 5), 3, 20),
                LeaderboardRefreshSeconds = Math.Clamp(GetJsonInt(request.Body, "leaderboardRefreshSeconds", 60), 30, 300),
                ExportNormalTerrain = GetJsonBool(request.Body, "exportNormalTerrain", true),
                ExportSepiaTerrain = GetJsonBool(request.Body, "exportSepiaTerrain", true),
                ExportTopographicTerrain = GetJsonBool(request.Body, "exportTopographicTerrain", true),
                PlayerMajorWaypointLimit = Math.Clamp(GetJsonInt(request.Body, "playerMajorWaypointLimit", 5), 0, 100),
                PlayerMinorWaypointLimit = Math.Clamp(GetJsonInt(request.Body, "playerMinorWaypointLimit", 10), 0, 100),
                MaximumZoomOutSpanBlocks = Math.Clamp(GetJsonInt(request.Body, "maximumZoomOutSpanBlocks", 50000), 2048, 100000),
                DefaultViewSpanBlocks = Math.Clamp(GetJsonInt(request.Body, "defaultViewSpanBlocks", 8000), 512, 100000),
                ActivityWindowMinutes = Math.Clamp(GetJsonInt(request.Body, "activityWindowMinutes", 60), 5, 1440),
                MaxActivityMarkers = Math.Clamp(GetJsonInt(request.Body, "maxActivityMarkers", 150), 25, 200),
                ExportTerrainOnStart = GetJsonBool(request.Body, "exportTerrainOnStart", false),
                AutomaticStartupExportEnabled = GetJsonBool(request.Body, "automaticStartupExportEnabled", false),
                AutomaticStartupExportIntervalDays = Math.Clamp(GetJsonInt(request.Body, "automaticStartupExportIntervalDays", 7), 1, 365),
                LockServerDuringTerrainExport = GetJsonBool(request.Body, "lockServerDuringTerrainExport", true),
                UseIncrementalTerrainExport = GetJsonBool(request.Body, "useIncrementalTerrainExport", true),
                TerrainTileSize = Math.Clamp(GetJsonInt(request.Body, "terrainTileSize", 256), 128, 512)
            };
            var saved = await RunOnMainThreadAsync(
                () => module.WebMap?.SaveConfig(incoming) ?? incoming,
                "web-map-config").ConfigureAwait(false);
            RecordWebAudit("web-map-config", $"{saved.BindAddress}:{saved.Port}", "Success", remoteText, requestActor);
            await WriteJsonAsync(stream, 200, new { success = true, config = saved }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/webmap/export-schedule")
        {
            bool oneShot = GetJsonBool(request.Body, "exportTerrainOnStart", false);
            bool automaticEnabled = GetJsonBool(request.Body, "automaticStartupExportEnabled", false);
            int intervalDays = Math.Clamp(GetJsonInt(request.Body, "automaticStartupExportIntervalDays", 7), 1, 365);
            bool useIncremental = GetJsonBool(request.Body, "useIncrementalTerrainExport", true);
            bool lockDuringExport = GetJsonBool(request.Body, "lockServerDuringTerrainExport", true);

            var saved = await RunOnMainThreadAsync(
                () => module.WebMap?.SaveExportSchedule(oneShot, automaticEnabled, intervalDays, useIncremental, lockDuringExport)
                      ?? new WebMapConfig
                      {
                          ExportTerrainOnStart = oneShot,
                          AutomaticStartupExportEnabled = automaticEnabled,
                          AutomaticStartupExportIntervalDays = intervalDays,
                          UseIncrementalTerrainExport = useIncremental,
                          LockServerDuringTerrainExport = lockDuringExport
                      },
                "web-map-export-schedule").ConfigureAwait(false);
            RecordWebAudit("web-map-export-schedule", oneShot ? "next-start-queued" : "next-start-off", "Success", remoteText, requestActor);
            await WriteJsonAsync(stream, 200, new { success = true, config = saved }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/webmap/terrain/update")
        {
            string message = await RunOnMainThreadAsync(
                () => module.WebMap?.UpdateTerrainNow() ?? "Native Web Map service is unavailable.",
                "web-map-update-terrain").ConfigureAwait(false);
            bool success = message.StartsWith("Changed terrain update started", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("web-map-update-terrain", "terrain", success ? "Success" : message, remoteText, requestActor);
            await WriteJsonAsync(stream, success ? 202 : 400, new { success, message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/webmap/terrain/force")
        {
            string message = await RunOnMainThreadAsync(
                () => module.WebMap?.ForceTerrainExportNow() ?? "Native Web Map service is unavailable.",
                "web-map-force-terrain-export").ConfigureAwait(false);
            bool success = message.StartsWith("Full terrain rebuild started", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("web-map-force-terrain-export", "terrain", success ? "Success" : message, remoteText, requestActor);
            await WriteJsonAsync(stream, success ? 202 : 400, new { success, message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/webmap/waypoint/save")
        {
            var incoming = new WebMapWaypoint
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
                var savedWaypoint = await RunOnMainThreadAsync(
                    () => module.WebMap?.SaveWaypoint(incoming, requestActor) ?? throw new InvalidOperationException("Native Web Map service is unavailable."),
                    "web-map-waypoint-save").ConfigureAwait(false);
                RecordWebAudit("web-map-waypoint-save", $"{savedWaypoint.Name} @ {savedWaypoint.X:0.##},{savedWaypoint.Y:0.##},{savedWaypoint.Z:0.##}", "Success", remoteText, requestActor);
                await WriteJsonAsync(stream, 200, new { success = true, waypoint = savedWaypoint }, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await WriteJsonAsync(stream, 400, new { error = ex.Message }, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (request.Method == "POST" && path == "/api/webmap/waypoint/delete")
        {
            string id = GetJsonString(request.Body, "id", "").Trim();
            if (id.Length == 0)
            {
                await WriteJsonAsync(stream, 400, new { error = "Waypoint id is required." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            bool removed = await RunOnMainThreadAsync(
                () => module.WebMap?.DeleteWaypoint(id) ?? false,
                "web-map-waypoint-delete").ConfigureAwait(false);
            if (!removed)
            {
                await WriteJsonAsync(stream, 404, new { error = "Waypoint was not found." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            RecordWebAudit("web-map-waypoint-delete", id, "Success", remoteText, requestActor);
            await WriteJsonAsync(stream, 200, new { success = true }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/console/input")
        {
            bool enabled = GetJsonBool(request.Body, "enabled", false);
            bool state = module.WebConsole?.SetInputEnabled(enabled) ?? false;
            RecordWebAudit("web-console-input", enabled ? "enable" : "disable", "Success", remoteText, requestActor);
            await WriteJsonAsync(stream, 200, new { success = true, enabled = state, expiresMinutes = state ? 15 : 0 }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/console/command")
        {
            string input = GetJsonString(request.Body, "command", "").Trim();
            string mode = GetJsonString(request.Body, "mode", "auto").Trim().ToLowerInvariant();
            if (mode != "auto" && mode != "command" && mode != "message") mode = "auto";
            if (string.IsNullOrWhiteSpace(input) || input.Length > 1000)
            {
                await WriteJsonAsync(stream, 400, new { error = "Console input must contain 1-1000 characters." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (module.WebConsole?.IsInputEnabled() != true)
            {
                await WriteJsonAsync(stream, 403, new { error = "Web console input is OFF. Enable it first; the enable lease lasts 15 minutes." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool executeAsCommand = mode == "command" || (mode == "auto" && input.StartsWith('/'));
            WebConsoleCommandResult result;
            string auditAction;
            string auditTarget;
            if (executeAsCommand)
            {
                string command = input.StartsWith('/') ? input[1..].TrimStart() : input;
                if (string.IsNullOrWhiteSpace(command))
                {
                    await WriteJsonAsync(stream, 400, new { error = "Command is empty." }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                result = await ExecuteWebConsoleCommandAsync(command).ConfigureAwait(false);
                auditAction = "web-console-command";
                auditTarget = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            }
            else
            {
                result = await ExecuteWebConsoleMessageAsync(input, requestActor).ConfigureAwait(false);
                auditAction = "web-console-message";
                auditTarget = $"broadcast ({input.Length} chars)";
            }

            module.WebConsole?.TouchInputLease();
            RecordWebAudit(auditAction, auditTarget, result.Success ? "Success" : result.Status, remoteText, requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 400, new { success = result.Success, status = result.Status, message = result.Message, mode = executeAsCommand ? "command" : "message" }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/mod-updates")
        {
            await WriteJsonAsync(stream, 200, module.ModUpdates?.GetSnapshot() ?? new BlockLogger.ModUpdates.ModUpdateSnapshot(), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/mod-updates/check")
        {
            var payload = module.ModUpdates == null
                ? new BlockLogger.ModUpdates.ModUpdateSnapshot { LastError = "Mod update checker is unavailable." }
                : await module.ModUpdates.CheckAsync(true).ConfigureAwait(false);
            RecordWebAudit("mod-update-check", "official-moddb", string.IsNullOrWhiteSpace(payload.LastError) ? "Success" : payload.LastError, remoteText, requestActor);
            await WriteJsonAsync(stream, string.IsNullOrWhiteSpace(payload.LastError) ? 200 : 502, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/mod-updates/apply")
        {
            string modId = GetJsonString(request.Body, "modId", "").Trim();
            if (string.IsNullOrWhiteSpace(modId))
            {
                await WriteJsonAsync(stream, 400, new { error = "modId is required." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var payload = module.ModUpdates == null
                ? new BlockLogger.ModUpdates.ModUpdateApplyResult { ModId = modId, Message = "Mod update checker is unavailable." }
                : await module.ModUpdates.ApplyUpdateAsync(modId).ConfigureAwait(false);
            RecordWebAudit("mod-update-stage", modId, payload.Success ? $"Staged {payload.InstalledVersion} -> {payload.NewVersion}; restart required" : payload.Message, remoteText, requestActor);
            await WriteJsonAsync(stream, payload.Success ? 200 : 400, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/reports")
        {
            string status = GetQuery(request, "status", "all");
            int limit = Math.Clamp(GetQueryInt(request, "limit", 500), 1, 1000);
            var payload = await RunOnMainThreadAsync(
                () => module.Reports?.GetSnapshot(status, limit) ?? new BlockLogger.Reports.PlayerReportSnapshot(),
                "web-reports").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/reports/detail")
        {
            long id = long.TryParse(GetQuery(request, "id", "0"), out var parsedReportId) ? parsedReportId : 0;
            int offset = Math.Clamp(GetQueryInt(request, "offset", 0), 0, 1_000_000);
            int limit = Math.Clamp(GetQueryInt(request, "limit", 100), 1, 100);
            var report = await RunOnMainThreadAsync(() => module.Reports?.GetReport(id, offset, limit), "web-report-detail").ConfigureAwait(false);
            if (report == null) await WriteJsonAsync(stream, 404, new { error = "Report was not found." }, cancellationToken).ConfigureAwait(false);
            else await WriteJsonAsync(stream, 200, report, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/reports/action")
        {
            long id = GetJsonLong(request.Body, "id", 0);
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string value = GetJsonString(request.Body, "value", "").Trim();
            string note = GetJsonString(request.Body, "note", "").Trim();
            if (id <= 0 || string.IsNullOrWhiteSpace(action))
            {
                await WriteJsonAsync(stream, 400, new { error = "Report id and action are required." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            string actor = requestActor;
            string message = await RunOnMainThreadAsync(
                () =>
                {
                    var reports = module.Reports;
                    if (reports == null) return "Reports service is unavailable.";
                    return action switch
                    {
                        "status" => reports.SetStatus(id, value, actor, note),
                        "investigate" => reports.SetStatus(id, "investigating", actor, note),
                        "resolve" => reports.SetStatus(id, "resolved", actor, note),
                        "reopen" => reports.SetStatus(id, "open", actor, note),
                        "assign" => reports.Assign(id, value, actor),
                        "unassign" => reports.Assign(id, "", actor),
                        "note" => reports.AddNote(id, actor, note.Length > 0 ? note : value),
                        _ => "Unknown report action."
                    };
                },
                "web-report-action").ConfigureAwait(false);

            var refreshed = await RunOnMainThreadAsync(
                () => module.Reports?.GetSnapshot("all", 500) ?? new BlockLogger.Reports.PlayerReportSnapshot(),
                "web-report-action-result").ConfigureAwait(false);
            bool reportActionSucceeded =
                !message.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) &&
                !message.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) &&
                !message.StartsWith("Reports service is unavailable", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("was not found", StringComparison.OrdinalIgnoreCase) &&
                !message.StartsWith("Status must", StringComparison.OrdinalIgnoreCase) &&
                !message.StartsWith("Note text is required", StringComparison.OrdinalIgnoreCase);
            await WriteJsonAsync(stream, 200, new { success = reportActionSucceeded, message, reports = refreshed }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/log-intelligence/issues/clear")
        {
            int cleared = await RunOnMainThreadAsync(
                () => logIntelligence?.ClearIssueSignatures() ?? 0,
                "web-log-intelligence-clear-issues").ConfigureAwait(false);
            RecordWebAudit("log-intelligence-clear-issues", $"cleared={cleared}", "Success", remote.ToString(), requestActor);
            var snapshot = await RunOnMainThreadAsync(
                () => logIntelligence?.GetSnapshot() ?? new LogIntelligenceSnapshot(),
                "web-log-intelligence-clear-issues-result").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, new { success = true, message = $"Cleared {cleared} retained issue signature(s).", cleared, logIntelligence = snapshot }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/log-intelligence/scan")
        {
            bool includeArchived = GetJsonBool(request.Body, "includeArchived", false);
            if (logIntelligence != null)
                await logIntelligence.RunManualScanAsync(includeArchived).ConfigureAwait(false);
            var payload = await RunOnMainThreadAsync(
                () => logIntelligence?.GetSnapshot() ?? new LogIntelligenceSnapshot(),
                "web-log-intelligence-scan-result").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/log-intelligence/preflight")
        {
            var payload = await RunOnMainThreadAsync(
                () => logIntelligence?.RefreshPreflight(
                    config.BindAddress,
                    config.Port,
                    config.Enabled,
                    IsRunning) ?? new StartupPreflightSnapshot(),
                "web-log-intelligence-preflight").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/log-intelligence/block-entity/action")
        {
            string key = GetJsonString(request.Body, "key", "").Trim();
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string value = GetJsonString(request.Body, "value", "").Trim();
            var payload = await RunOnMainThreadAsync(
                () => ExecuteBadBlockEntityAction(key, action, value, requestActor),
                "web-bad-block-entity-action").ConfigureAwait(false);

            string auditResult = payload.Success ? "Success" : "Failed";
            RecordWebAudit(
                "bad-block-entity-action",
                $"{action} key={key} value={value}",
                auditResult,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/logout")
        {
            RecordWebAudit(
                "logout",
                "Web session logged out.",
                "Success",
                remote.ToString(),
                requestActor);
            RemoveSession(request);
            await WriteJsonAsync(
                stream,
                200,
                new { success = true },
                cancellationToken,
                clearCookie: true).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/dashboard")
        {
            bool terrainExportBusy = module.WebMap?.IsTerrainExportInProgress == true;
            object dashboard = await GetDashboardSnapshotAsync(terrainExportBusy).ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, dashboard, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/web/status")
        {
            var status = new
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
                authenticatedRole = webSession?.Role ?? "Owner",
                passwordConfigured = PasswordConfigured,
                localAnonymousMode = LocalAnonymousMode,
                authenticationMode = LocalAnonymousMode ? "local-loopback-no-login" : "password",
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
                uptimeSeconds = IsRunning
                    ? Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds)
                    : 0
            };
            await WriteJsonAsync(stream, 200, status, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/web/accounts")
        {
            await WriteJsonAsync(
                stream,
                200,
                BuildWebAdminAccountsPayload(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/web/accounts/create")
        {
            string username = GetJsonString(request.Body, "username", "").Trim();
            string password = GetJsonString(request.Body, "password", "");
            string roleInput = GetJsonString(request.Body, "role", "Administrator").Trim();
            if (!(string.Equals(roleInput, "Administrator", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(roleInput, "ReadOnly", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(roleInput, "Read Only", StringComparison.OrdinalIgnoreCase)))
            {
                await WriteJsonAsync(stream, 400, new { error = "Role must be Administrator or Read Only." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            string role = NormalizePanelRole(roleInput);

            if (!IsValidPanelUsername(username))
            {
                await WriteJsonAsync(stream, 400, new { error = "Username must be 3-64 characters using letters, numbers, dot, dash, or underscore." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (password.Length < 10 || password.Length > 256)
            {
                await WriteJsonAsync(stream, 400, new { error = "Password must be 10-256 characters." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            string createError = "";
            lock (webAccountLock)
            {
                if (string.Equals(username, config.AdminUsername, StringComparison.OrdinalIgnoreCase) ||
                    FindPanelAccount(username) != null)
                {
                    createError = "That panel username already exists.";
                }
                else if (webAdminAccounts.Accounts.Count >= MaxWebAdminAccounts)
                {
                    createError = $"The panel account limit of {MaxWebAdminAccounts} has been reached.";
                }
                else
                {
                    var account = new WebAdminAccountRecord
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
                await WriteJsonAsync(stream, 409, new { error = createError }, cancellationToken).ConfigureAwait(false);
                return;
            }

            RecordWebAudit(
                "panel-account-create",
                $"Created {username} as {role}.",
                "Success",
                remote.ToString(),
                requestActor);
            await WriteJsonAsync(
                stream,
                200,
                new { success = true, message = $"Panel account {username} created.", accounts = BuildWebAdminAccountsPayload() },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/web/accounts/action")
        {
            string id = GetJsonString(request.Body, "id", "").Trim();
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string value = GetJsonString(request.Body, "value", "").Trim();
            string password = GetJsonString(request.Body, "password", "");

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(action))
            {
                await WriteJsonAsync(stream, 400, new { error = "Account id and action are required." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            string affectedUsername = "";
            string message = "";
            bool invalidateSessions = false;
            int statusCode = 200;

            lock (webAccountLock)
            {
                var account = webAdminAccounts.Accounts.FirstOrDefault(x =>
                    string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (account == null)
                {
                    statusCode = 404;
                    message = "Panel account was not found.";
                }
                else
                {
                    affectedUsername = account.Username;
                    switch (action)
                    {
                        case "enable":
                            account.Enabled = true;
                            account.LockedUntilUnixMilliseconds = 0;
                            account.FailedLoginCount = 0;
                            message = $"{account.Username} enabled.";
                            break;
                        case "disable":
                            account.Enabled = false;
                            invalidateSessions = true;
                            message = $"{account.Username} disabled and signed out.";
                            break;
                        case "delete":
                            webAdminAccounts.Accounts.Remove(account);
                            invalidateSessions = true;
                            message = $"{account.Username} deleted and signed out.";
                            break;
                        case "unlock":
                            account.LockedUntilUnixMilliseconds = 0;
                            account.FailedLoginCount = 0;
                            message = $"{account.Username} login lock cleared.";
                            break;
                        case "role":
                            if (!(string.Equals(value, "Administrator", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(value, "ReadOnly", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(value, "Read Only", StringComparison.OrdinalIgnoreCase)))
                            {
                                statusCode = 400;
                                message = "Role must be Administrator or Read Only.";
                            }
                            else
                            {
                                account.Role = NormalizePanelRole(value);
                                invalidateSessions = true;
                                message = $"{account.Username} role changed to {account.Role}; existing sessions were signed out.";
                            }
                            break;
                        case "password":
                            if (password.Length < 10 || password.Length > 256)
                            {
                                statusCode = 400;
                                message = "Password must be 10-256 characters.";
                            }
                            else
                            {
                                SetAccountPasswordHash(account, password);
                                account.LockedUntilUnixMilliseconds = 0;
                                account.FailedLoginCount = 0;
                                invalidateSessions = true;
                                message = $"Password reset for {account.Username}; existing sessions were signed out.";
                            }
                            break;
                        default:
                            statusCode = 400;
                            message = "Unknown panel account action.";
                            break;
                    }

                    if (statusCode == 200)
                        SaveWebAdminAccounts();
                }
            }

            if (invalidateSessions && !string.IsNullOrWhiteSpace(affectedUsername))
                LogoutSessionsForUsername(affectedUsername);

            RecordWebAudit(
                "panel-account-" + action,
                string.IsNullOrWhiteSpace(affectedUsername) ? id : affectedUsername,
                statusCode == 200 ? "Success" : message,
                remote.ToString(),
                requestActor);

            if (statusCode != 200)
            {
                await WriteJsonAsync(stream, statusCode, new { error = message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(
                stream,
                200,
                new { success = true, message, accounts = BuildWebAdminAccountsPayload() },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/admin/status")
        {
            var payload = await RunOnMainThreadAsync(
                () =>
                {
                    var admin = module.Admin;
                    if (admin == null) return (object)new { available = false };
                    var maintenance = admin.GetMaintenance();
                    var shutdown = admin.GetShutdown();
                    return new
                    {
                        available = true,
                        maintenance = new { enabled = maintenance.Enabled, reason = maintenance.Reason, untilUnixMilliseconds = maintenance.Until },
                        shutdown = new { atUnixMilliseconds = shutdown.At, reason = shutdown.Reason },
                        undo = new { available = !string.IsNullOrWhiteSpace(admin.GetUndoDescription()), description = admin.GetUndoDescription() },
                        audit = admin.GetAudit(50)
                    };
                },
                "web-admin-status").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/admin/players")
        {
            var payload = await RunOnMainThreadAsync(
                () =>
                {
                    var players = module.Admin?.GetPlayers() ?? Array.Empty<AdminPlayerSummary>();
                    module.PlayerList?.DecorateClientPresence(players);
                    DecorateSupporterNicknames(players);
                    return players;
                },
                "web-admin-players").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, new { players = payload }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/admin/database-storage")
        {
            await WriteJsonAsync(
                stream,
                200,
                GetDatabaseStorageViewerSnapshot(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/admin/database-storage/analyze")
        {
            if(!await databaseStorageAnalysisGate.WaitAsync(0,cancellationToken).ConfigureAwait(false)){
                await WriteJsonAsync(stream,409,new {error="Storage analysis is already running. Try again after it finishes."},cancellationToken).ConfigureAwait(false);return;
            }
            DatabaseStorageViewerSnapshot report;
            try{report=await Task.Run(AnalyzeDatabaseStorage,cancellationToken).ConfigureAwait(false);}finally{databaseStorageAnalysisGate.Release();}
            RecordWebAudit(
                "database-storage-analyze",
                "essentials.db + blocklogger.db",
                report.DetailedAnalysisAvailable ? "Success" : report.Message,
                remote.ToString(),
                requestActor);
            await WriteJsonAsync(stream, 200, report, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/admin/player")
        {
            string player = GetQuery(request, "player", "").Trim();
            if (string.IsNullOrWhiteSpace(player))
            {
                await WriteJsonAsync(stream, 400, new { error = "Player is required." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool includeDetails = GetQuery(request, "details", "all") != "summary";
            var payload = await RunOnMainThreadAsync(
                () =>
                {
                    var admin = module.Admin;
                    var summary = admin?.GetPlayer(player);
                    if (admin == null || summary == null) return null;
                    module.PlayerList?.DecorateClientPresence(new[] { summary });
                    DecorateSupporterNicknames(new[] { summary });
                    var inspection = includeDetails && summary.Online ? admin.GetInventoryInspection(player) : new AdminInventoryInspection { Status = new AdminInventoryViewStatus { Message = "Open Inventory and refresh to load item details." } };
                    return new AdminPlayerDetailSnapshot
                    {
                        Player = summary,
                        Warnings = admin.GetWarnings(player),
                        Notes = admin.GetNotes(player),
                        History = admin.GetHistory(player, 100),
                        Inventory = inspection.Slots,
                        InventoryStatus = inspection.Status,
                        InventorySnapshots = admin.GetInventorySnapshots(player, 12)
                    };
                },
                "web-admin-player-detail").ConfigureAwait(false);

            if (payload == null)
            {
                await WriteJsonAsync(stream, 404, new { error = "Player not found." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (includeDetails && !payload.Player.Online && module.Admin != null)
            {
                var inspection = await LoadSavedInventoryInspectionAsync(module.Admin, payload.Player.PlayerUid).ConfigureAwait(false);
                payload.Inventory = inspection.Slots;
                payload.InventoryStatus = inspection.Status;
            }
            payload.InventoryStatus.ReadOnly |= readOnlyAccount;

            if (includeDetails && module.Queries != null)
            {
                try
                {
                    payload.BlockHistory = await module.Queries.QueryPlayerAsync(payload.Player.Name, 100, null).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[ServerEssentials/Admin] Could not load BlockLogger history for web player profile: {0}", ex.Message);
                }
            }

            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/admin/inventory")
        {
            string player = GetQuery(request, "player", "").Trim();
            if (string.IsNullOrWhiteSpace(player))
            {
                await WriteJsonAsync(stream, 400, new { error = "Player is required." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var payload = await RunOnMainThreadAsync(
                () =>
                {
                    var admin = module.Admin;
                    var summary = admin?.GetPlayer(player);
                    if (admin == null || summary == null) return null;
                    module.PlayerList?.DecorateClientPresence(new[] { summary });
                    var history = admin.GetHistory(player, 120)
                        .Where(x => (x.Action ?? "").StartsWith("inventory-", StringComparison.OrdinalIgnoreCase))
                        .Take(80)
                        .ToList();
                    var inspection = summary.Online ? admin.GetInventoryInspection(player) : new AdminInventoryInspection();
                    return new AdminInventoryDetailSnapshot
                    {
                        Player = summary,
                        Inventory = inspection.Slots,
                        InventoryStatus = inspection.Status,
                        InventorySnapshots = admin.GetInventorySnapshots(player, 12),
                        History = history
                    };
                },
                "web-admin-inventory-detail").ConfigureAwait(false);

            if (payload == null)
            {
                await WriteJsonAsync(stream, 404, new { error = "Player not found." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!payload.Player.Online && module.Admin != null)
            {
                var inspection = await LoadSavedInventoryInspectionAsync(module.Admin, payload.Player.PlayerUid).ConfigureAwait(false);
                payload.Inventory = inspection.Slots;
                payload.InventoryStatus = inspection.Status;
            }
            payload.InventoryStatus.ReadOnly |= readOnlyAccount;
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/admin/items")
        {
            string search = GetQuery(request, "search", "").Trim();
            int limit = Math.Clamp(int.TryParse(GetQuery(request, "limit", "50"), out int parsedLimit) ? parsedLimit : 50, 1, 100);
            var payload = await RunOnMainThreadAsync(
                () => module.Admin?.SearchItems(search, limit) ?? new List<AdminItemSearchResult>(),
                "web-admin-item-search").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, new { items = payload }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/admin/inventory/action")
        {
            string actor = requestActor;
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string player = GetJsonString(request.Body, "player", "").Trim();
            string inventory = GetJsonString(request.Body, "inventory", "").Trim();
            string toInventory = GetJsonString(request.Body, "toInventory", "").Trim();
            string code = GetJsonString(request.Body, "code", "").Trim();
            string fingerprint = GetJsonString(request.Body, "fingerprint", "").Trim();
            string toFingerprint = GetJsonString(request.Body, "toFingerprint", "").Trim();
            int slot = GetJsonInt(request.Body, "slot", -1);
            int toSlot = GetJsonInt(request.Body, "toSlot", -1);
            int quantity = GetJsonInt(request.Body, "quantity", 1);

            var result = await RunOnMainThreadAsync(
                () =>
                {
                    var admin = module.Admin;
                    if (admin == null) return "Administration module unavailable.";
                    return action switch
                    {
                        "remove" => admin.RemoveInventorySlot(actor, player, inventory, slot, "web", fingerprint),
                        "quantity" => admin.SetInventoryQuantity(actor, player, inventory, slot, quantity, fingerprint, "web"),
                        "move" => admin.MoveInventorySlot(actor, player, inventory, slot, toInventory, toSlot, fingerprint, toFingerprint, "web"),
                        "give" => admin.GiveInventoryItem(actor, player, code, quantity, inventory, slot, fingerprint, "web"),
                        "replace" => admin.ReplaceInventorySlot(actor, player, inventory, slot, code, quantity, fingerprint, "web"),
                        _ => "Unknown inventory action."
                    };
                },
                "web-admin-inventory-action").ConfigureAwait(false);

            bool conflict = result.StartsWith("Inventory changed", StringComparison.OrdinalIgnoreCase);
            bool success = result.StartsWith("Removed ", StringComparison.OrdinalIgnoreCase)
                           || result.StartsWith("Changed ", StringComparison.OrdinalIgnoreCase)
                           || result.StartsWith("Moved/", StringComparison.OrdinalIgnoreCase)
                           || result.StartsWith("Gave ", StringComparison.OrdinalIgnoreCase)
                           || result.StartsWith("Replaced ", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("admin-inventory-" + action, $"{player} {inventory}[{slot}] {code} x{quantity}", result, remote.ToString(), actor);
            await WriteJsonAsync(stream, conflict ? 409 : success ? 200 : 400, new { success, message = result, conflict }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/admin/inventory/compare")
        {
            string player = GetQuery(request, "player", "").Trim();
            long id = long.TryParse(GetQuery(request, "id", "0"), out long parsedId) ? parsedId : 0;
            var payload = await RunOnMainThreadAsync(
                () =>
                {
                    var admin = module.Admin;
                    if (admin?.GetPlayer(player)?.Online != true) return (Error: "Live snapshot comparison requires an online player.", Rows: new List<AdminInventoryDiffRow>());
                    if (!admin.GetInventorySnapshots(player).Any(x => x.Id == id)) return (Error: "Inventory snapshot not found.", Rows: new List<AdminInventoryDiffRow>());
                    return (Error: "", Rows: admin.CompareInventorySnapshot(player, id));
                },
                "web-admin-inventory-compare").ConfigureAwait(false);
            if (payload.Error.Length > 0)
            {
                await WriteJsonAsync(stream, 409, new { error = payload.Error }, cancellationToken).ConfigureAwait(false);
                return;
            }
            await WriteJsonAsync(stream, 200, new { differences = payload.Rows }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/admin/inventory/snapshot")
        {
            string actor = requestActor;
            string action = GetJsonString(request.Body, "action", "create").Trim().ToLowerInvariant();
            string player = GetJsonString(request.Body, "player", "").Trim();
            string reason = GetJsonString(request.Body, "reason", "Manual web snapshot").Trim();
            long id = GetJsonLong(request.Body, "id", 0);
            var result = await RunOnMainThreadAsync(
                () => action == "restore"
                    ? module.Admin?.RestoreInventorySnapshot(actor, player, id, "web") ?? "Administration module unavailable."
                    : module.Admin?.CreateInventorySnapshot(actor, player, reason, "web") ?? "Administration module unavailable.",
                "web-admin-inventory-snapshot").ConfigureAwait(false);
            bool success = result.StartsWith("Inventory snapshot #", StringComparison.OrdinalIgnoreCase)
                           || result.StartsWith("Restored inventory snapshot #", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("admin-inventory-snapshot-" + action, $"{player} #{id} {reason}", result, remote.ToString(), actor);
            await WriteJsonAsync(stream, success ? 200 : 400, new { success, message = result }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/admin/action")
        {
            string actor = requestActor;
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string player = GetJsonString(request.Body, "player", "").Trim();
            string duration = GetJsonString(request.Body, "duration", "").Trim();
            string reason = GetJsonString(request.Body, "reason", "").Trim();
            string note = GetJsonString(request.Body, "note", "").Trim();
            long id = GetJsonLong(request.Body, "id", 0);

            if (action == "allowcharselonce")
            {
                var summary = await RunOnMainThreadAsync(
                    () => module.Admin?.GetPlayer(player),
                    "web-admin-resolve-player").ConfigureAwait(false);
                if (summary == null || string.IsNullOrWhiteSpace(summary.Name))
                {
                    await WriteJsonAsync(stream, 404, new { error = "Player was not found." }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var grant = await RunOnMainThreadAsync(
                    () =>
                    {
                        var target = module.Admin?.FindOnlinePlayer(summary.PlayerUid)
                                     ?? module.Admin?.FindOnlinePlayer(summary.Name);

                        try
                        {
                            if (target?.Entity != null)
                            {
                                // Vintage Story's .charsel client command checks this exact watched attribute.
                                // Grant it directly instead of routing through ChatCommands.ExecuteUnparsed,
                                // which can return NoSuchCommand from the WebPanel console caller on 1.22.x.
                                target.Entity.WatchedAttributes.SetBool("allowcharselonce", true);
                                target.Entity.WatchedAttributes.MarkPathDirty("allowcharselonce");
                                target.BroadcastPlayerData(true);

                                module.Admin?.Audit(
                                    actor,
                                    "allowcharselonce",
                                    target.PlayerUID,
                                    target.PlayerName,
                                    "Granted one additional .charsel use directly through Vintage Story's allowcharselonce watched attribute",
                                    "web");

                                return (Success: true, Message: $"Allowed {target.PlayerName} to use .charsel one more time. They can run .charsel now.");
                            }

                            // Vanilla also supports previously-connected offline players. For those, hand the
                            // exact documented command to the real server console instead of the WebPanel
                            // ChatCommands caller that caused the NoSuchCommand false failure.
                            api.InjectConsole($"/player {QuoteCommandArgument(summary.Name)} allowcharselonce");
                            module.Admin?.Audit(
                                actor,
                                "allowcharselonce",
                                summary.PlayerUid,
                                summary.Name,
                                "Queued the native /player allowcharselonce command for an offline player",
                                "web");
                            return (Success: true, Message: $"Queued one-time character selection for {summary.Name}. They can run .charsel after joining.");
                        }
                        catch (Exception ex)
                        {
                            return (Success: false, Message: $"Could not grant character selection to {summary.Name}: {ex.Message}");
                        }
                    },
                    "web-admin-allowcharselonce").ConfigureAwait(false);

                RecordWebAudit("admin-allowcharselonce", summary.Name, grant.Success ? "Success" : grant.Message, remote.ToString(), actor);
                await WriteJsonAsync(stream, grant.Success ? 200 : 400, new { success = grant.Success, message = grant.Message }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await RunOnMainThreadAsync(
                () => module.Admin?.ExecuteWebAction(actor, action, player, duration, reason, id, note)
                      ?? "Administration module unavailable.",
                "web-admin-player-action").ConfigureAwait(false);

            bool success = AdminService.IsSuccessfulActionResult(action, result);
            RecordWebAudit("admin-" + action, $"{player}: {reason}", success ? "Success" : result, remote.ToString(), actor);
            await WriteJsonAsync(stream, success ? 200 : 400, new { success, message = result }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/admin/maintenance")
        {
            string actor = requestActor;
            bool enabled = GetJsonBool(request.Body, "enabled", false);
            string reason = GetJsonString(request.Body, "reason", "Maintenance");
            string duration = GetJsonString(request.Body, "duration", "");
            if (enabled && !string.Equals(GetJsonString(request.Body, "confirmation", "").Trim(), "MAINTENANCE", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 400, new { error = "Safety confirmation failed. Type MAINTENANCE to enable maintenance mode." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            long durationMs = 0;
            if (!string.IsNullOrWhiteSpace(duration) &&
                (!AdminService.TryParseDuration(duration, out durationMs, out bool permanent) || permanent))
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid duration. Use 30m, 6h, 3d, 2w, etc." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => module.Admin?.SetMaintenance(actor, enabled, reason, durationMs, "web") ?? "Administration module unavailable.",
                "web-admin-maintenance").ConfigureAwait(false);
            RecordWebAudit("admin-maintenance", reason, result, remote.ToString(), actor);
            await WriteJsonAsync(stream, 200, new { success = true, message = result }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/admin/shutdown")
        {
            string actor = requestActor;
            string action = GetJsonString(request.Body, "action", "schedule").Trim().ToLowerInvariant();
            string reason = GetJsonString(request.Body, "reason", "Scheduled shutdown");
            string duration = GetJsonString(request.Body, "duration", "");
            string result;
            if (action != "cancel")
            {
                string expected = action == "restart" ? "RESTART" : "SHUTDOWN";
                string confirmation = GetJsonString(request.Body, "confirmation", "").Trim();
                if (!string.Equals(confirmation, expected, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(stream, 400, new { error = $"Safety confirmation failed. Type {expected} to schedule this action." }, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            if (action == "cancel")
            {
                result = await RunOnMainThreadAsync(
                    () => module.Admin?.CancelShutdown(actor, "web") ?? "Administration module unavailable.",
                    "web-admin-shutdown-cancel").ConfigureAwait(false);
            }
            else
            {
                if (!AdminService.TryParseDuration(duration, out long delayMs, out bool permanent) || permanent)
                {
                    await WriteJsonAsync(stream, 400, new { error = "Invalid shutdown/restart delay. Use 30s, 5m, 1h, etc." }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                result = await RunOnMainThreadAsync(
                    () => action == "restart"
                        ? module.Admin?.ScheduleRestart(actor, delayMs, reason, "web") ?? "Administration module unavailable."
                        : module.Admin?.ScheduleShutdown(actor, delayMs, reason, "web") ?? "Administration module unavailable.",
                    action == "restart" ? "web-admin-restart-schedule" : "web-admin-shutdown-schedule").ConfigureAwait(false);
            }
            RecordWebAudit("admin-shutdown", $"{action}: {reason}", result, remote.ToString(), actor);
            await WriteJsonAsync(stream, 200, new { success = true, message = result }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/admin/cleanup")
        {
            string actor = requestActor;
            bool confirm = GetJsonBool(request.Body, "confirm", false);
            if (confirm && !string.Equals(GetJsonString(request.Body, "confirmation", "").Trim(), "CLEANUP", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 400, new { error = "Safety confirmation failed. Type CLEANUP to remove loaded dropped-item entities." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            int maxRemove = Math.Clamp(GetJsonInt(request.Body, "maxRemove", 100000), 1, 100000);
            var result = await RunOnMainThreadAsync(
                () =>
                {
                    var cleanup = module.Admin == null
                        ? new CleanupReport { DryRun = true }
                        : confirm ? module.Admin.CleanupDrops(false, maxRemove) : module.Admin.ScanCleanup();
                    module.Admin?.Audit(actor, confirm ? "cleanup-drops" : "cleanup-entity-scan", "", "server",
                        confirm
                            ? $"Removed {cleanup.Removed} dropped item entities"
                            : $"Scanned {cleanup.LoadedEntities} loaded entities; dropped={cleanup.DroppedItems}, dead/despawning={cleanup.DeadOrDespawning}", "web");
                    return cleanup;
                },
                "web-admin-cleanup-drops").ConfigureAwait(false);
            RecordWebAudit("admin-cleanup", confirm ? "confirmed" : "dry-run", "Success", remote.ToString(), actor);
            await WriteJsonAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/health")
        {
            var health = await RunOnMainThreadAsync(
                () =>
                {
                    var selfTest = BuildServerUtilitiesHealth();
                    var database = module.Storage?.GetDatabaseHealth(
                        module.Config.RetentionDays);
                    var healthSnapshot = module.ServerHealth?.GetSnapshot(120, 300);

                    string activeHeavy;
                    lock (hardeningLock)
                        activeHeavy = activeHeavyOperation;

                    return new
                    {
                        selfTest,
                        database,
                        adminDatabase = module.Admin?.GetStorageHealth(),
                        webDiagnostics = BuildWebDiagnosticsSnapshot(),
                        smartDiagnosis = healthSnapshot?.SmartAnalysis,
                        diagnosticSummary = healthSnapshot?.DiagnosticSummary,
                        retentionAndPrivacy =
                            BuildRetentionAndPrivacyStatus(),
                        safeDiagnosticsMode =
                            config.SafeDiagnosticsMode,
                        activeHeavyOperation = activeHeavy,
                        buildId = BuildId,
                        targetGameVersion = TargetGameVersion
                    };
                },
                "web-serverutilities-health").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                health,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/health/admin-database-integrity")
        {
            if (module.Admin == null)
            {
                await WriteJsonAsync(stream, 503, new { error = "Admin storage is unavailable." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await Task.Run(() => module.Admin.CheckStorageIntegrity(), cancellationToken).ConfigureAwait(false);
            RecordWebAudit("admin-database-integrity", $"essentials.db quick_check: {result}", "Success", remote.ToString(), requestActor);
            await WriteJsonAsync(stream, 200, new { integrity = result }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/health/admin-database-backup")
        {
            if (module.Admin == null)
            {
                await WriteJsonAsync(stream, 503, new { error = "Admin storage is unavailable." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var pathResult = await Task.Run(() => module.Admin.BackupStorage("web-manual"), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pathResult))
            {
                await WriteJsonAsync(stream, 500, new { error = "Admin database backup failed. Check server logs." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            RecordWebAudit("admin-database-backup", "Created essentials.db backup", "Success", remote.ToString(), requestActor);
            await WriteJsonAsync(stream, 200, new { success = true, message = "Admin database backup created." }, cancellationToken).ConfigureAwait(false);
            return;
        }


        if (request.Method == "POST" && path == "/api/health/database-backup")
        {
            var storage = module.Storage;
            if (storage == null)
            {
                await WriteJsonAsync(stream, 503, new { error = "BlockLogger storage is unavailable." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var pathResult = await storage.CreateDatabaseBackupAsync("web-manual").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pathResult))
            {
                await WriteJsonAsync(stream, 409, new { error = "BlockLogger database backup could not start or is already running. Check server logs/health." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            RecordWebAudit("database-backup", "Created blocklogger.db backup", "Success", remote.ToString(), requestActor);
            await WriteJsonAsync(stream, 200, new { success = true, message = "BlockLogger database backup created." }, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request.Method == "POST" &&
            path == "/api/health/database-integrity")
        {
            if (IsFeatureCircuitOpen(
                    "database-integrity",
                    out string circuitMessage))
            {
                await WriteJsonAsync(
                    stream,
                    503,
                    new { error = circuitMessage },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var storage = module.Storage;
            if (storage == null)
            {
                await WriteJsonAsync(
                    stream,
                    503,
                    new { error = "BlockLogger storage is unavailable." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var db = await AwaitWithTimeout(
                    storage.CheckDatabaseIntegrityAsync(
                        module.Config.RetentionDays),
                    TimeSpan.FromSeconds(12),
                    "SQLite quick_check").ConfigureAwait(false);

                RecordFeatureSuccess(
                    "database-integrity",
                    "SQLite Integrity Check",
                    "Moderate");

                RecordWebAudit(
                    "database-integrity",
                    $"SQLite quick_check: {db.Integrity}",
                    "Success",
                    remote.ToString(),
                    requestActor);

                await WriteJsonAsync(
                    stream,
                    200,
                    db,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordFeatureFailure(
                    "database-integrity",
                    ex,
                    "SQLite Integrity Check",
                    "Moderate");

                await WriteJsonAsync(
                    stream,
                    503,
                    new
                    {
                        error =
                            $"{ex.GetType().Name}: {ex.Message}"
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (request.Method == "POST" && path == "/api/mod-updates/compatibility")
        {
            bool enabled = GetJsonBool(request.Body, "allowSameMinorGameUpdates", false);
            var result = await RunOnMainThreadAsync(() =>
            {
                bool previous = config.AllowSameMinorGameModUpdates;
                config.AllowSameMinorGameModUpdates = enabled;
                try { SaveConfig(); }
                catch { config.AllowSameMinorGameModUpdates = previous; throw; }
                if (module.ModUpdates != null) module.ModUpdates.AllowSameMinorGameUpdates = enabled;
                RecordWebAudit("mod-update-compatibility", enabled ? "Earlier same-minor patches allowed" : "Exact game tags", "Success", remoteText, requestActor);
                return new { success = true, enabled };
            }, "mod-update-compatibility").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/performance/automatic-profiling")
        {
            bool enabled = GetJsonBool(request.Body, "enabled", false);
            var result = await RunOnMainThreadAsync(() =>
            {
                bool previous = config.AutomaticIncidentProfilingEnabled;
                config.AutomaticIncidentProfilingEnabled = enabled;
                try { SaveConfig(); }
                catch { config.AutomaticIncidentProfilingEnabled = previous; throw; }
                module.ServerHealth?.SetAutomaticIncidentProfilingEnabled(enabled && !config.SafeDiagnosticsMode);
                RecordWebAudit("automatic-profiling", enabled ? "Enabled" : "Disabled", "Success", remoteText, requestActor);
                return new { success = true, enabled = enabled && !config.SafeDiagnosticsMode,
                    configured = enabled, message = enabled
                        ? "Automatic engine captures opted in. Captures add diagnostic overhead; Safe Diagnostics Mode still blocks them."
                        : "Automatic engine captures disabled. Lightweight health sampling and manual captures remain available." };
            }, "automatic-profiling").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            path == "/api/health/safe-mode")
        {
            bool enabled = GetJsonBool(
                request.Body,
                "enabled",
                true);

            string actor = requestActor;

            var result = await RunOnMainThreadAsync(
                () =>
                {
                    config.SafeDiagnosticsMode = enabled;
                    SaveConfig();

                    if (enabled)
                    {
                        module.Config.ProfilerEnabled = true;
                        module.SaveRuntimeConfig();
                    }

                    module.ServerHealth?.SetAutomaticIncidentProfilingEnabled(config.AutomaticIncidentProfilingEnabled && !enabled);

                    RecordWebAudit(
                        "safe-diagnostics-mode",
                        enabled ? "Enabled" : "Disabled",
                        "Success",
                        remoteText,
                        actor);
                    SaveOperationsCenterStore();

                    return new
                    {
                        success = true,
                        enabled,
                        message = enabled
                            ? "Safe Diagnostics Mode enabled. Lightweight diagnostics are ON; manual engine profiling and external optimizer changes are locked."
                            : "Safe Diagnostics Mode disabled."
                    };
                },
                "web-safe-diagnostics-mode").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                result,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            path == "/api/health/circuit-reset")
        {
            string key = GetJsonString(
                request.Body,
                "key",
                "").Trim();

            lock (hardeningLock)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    foreach (var state in featureBreakers.Values)
                    {
                        state.ConsecutiveFailures = 0;
                        state.OpenUntilUnixMilliseconds = 0;
                    }
                }
                else if (featureBreakers.TryGetValue(key, out var state))
                {
                    state.ConsecutiveFailures = 0;
                    state.OpenUntilUnixMilliseconds = 0;
                }
            }

            RecordWebAudit(
                "circuit-breaker-reset",
                string.IsNullOrWhiteSpace(key)
                    ? "Reset all feature circuit breakers."
                    : $"Reset circuit breaker {key}.",
                "Success",
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(
                stream,
                200,
                new
                {
                    success = true,
                    circuitBreakers = SnapshotCircuitBreakers()
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/operations/overview")
        {
            var overview = await RunOnMainThreadAsync(
                () =>
                {
                    MaintainOperationsCenter();

                    var health =
                        module.ServerHealth?.GetSnapshot(300, 1800);
                    var memory = health == null
                        ? new MemoryGcAnalysis
                        {
                            Available = false,
                            State = "Unavailable",
                            Finding = "Server health diagnostics are unavailable."
                        }
                        : BuildMemoryGcAnalysis(health);

                    return new
                    {
                        diagnosticsEnabled = module.ServerHealth?.Enabled == true,
                        alerts = operationsCenterStore.Alerts
                            .OrderByDescending(x =>
                                SeverityRankForOperations(x.Severity))
                            .ThenByDescending(x => x.LastSeenUnixMilliseconds)
                            .Take(100)
                            .ToList(),
                        actions = operationsCenterStore.Actions
                            .OrderByDescending(x =>
                                x.Priority == "High" ? 3 :
                                x.Priority == "Medium" ? 2 : 1)
                            .ThenByDescending(x => x.UpdatedUnixMilliseconds)
                            .Take(100)
                            .ToList(),
                        memory,
                        scorecard = BuildPerformanceScorecard(health, memory),
                        playerLoad = BuildPlayerLoadAnalysis(),
                        worldSaves = operationsCenterStore.WorldSaves
                            .OrderByDescending(x => x.UnixMilliseconds)
                            .Take(20)
                            .ToList(),
                        networkSamples = operationsCenterStore.NetworkSamples
                            .OrderByDescending(x => x.UnixMilliseconds)
                            .Take(60)
                            .ToList(),
                        currentPerformance = health == null
                            ? null
                            : CaptureEnvironmentMetrics(health),
                        performanceSnapshots = operationsCenterStore.PerformanceSnapshots
                            .Take(config.MaxPerformanceSnapshots)
                            .ToList(),
                        configSnapshots = operationsCenterStore.ConfigSnapshots
                            .Select(x => new
                            {
                                x.Id,
                                x.UnixMilliseconds,
                                x.Label,
                                x.CreatedBy
                            })
                            .Take(20)
                            .ToList(),
                        auditCount = operationsCenterStore.Audit.Count,
                        instrumentation = BuildInstrumentationStatus()
                    };
                },
                "web-operations-overview").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                overview,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/operations/memory")
        {
            var memory = await GetCachedMainThreadReadAsync(
                "operations-memory", 1500,
                () =>
                {
                    var health =
                        module.ServerHealth?.GetSnapshot(600, 1800);

                    if (health == null)
                    {
                        return new
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
                        };
                    }

                    return new
                    {
                        available = true,
                        diagnosticsEnabled = health.Enabled,
                        analysis = BuildMemoryGcAnalysis(health),
                        samples = health.Samples,
                        inspector = BuildMemoryInspectorPayload(health)
                    };
                },
                "web-operations-memory").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                memory,
                cancellationToken).ConfigureAwait(false);
            return;
        }


        if (request.Method == "POST" && path == "/api/operations/memory-snapshot")
        {
            string action = GetJsonString(request.Body, "action", "capture").Trim().ToLowerInvariant();
            string actor = requestActor;

            if (action == "capture")
            {
                string label = GetJsonString(request.Body, "label", "Memory snapshot").Trim();
                if (label.Length > 80) label = label[..80];
                var snapshot = CaptureMemoryInspectorSnapshot(label);
                lock (operationsLock)
                {
                    operationsCenterStore.MemoryInspectorSnapshots.Insert(0, snapshot);
                }
                RecordWebAudit("memory-snapshot", snapshot.Label, "Success", remoteText, actor);
                SaveOperationsCenterStore();
                await WriteJsonAsync(stream, 200, new { success = true, snapshot }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (action == "delete")
            {
                string id = GetJsonString(request.Body, "id", "").Trim();
                bool removed;
                lock (operationsLock)
                    removed = operationsCenterStore.MemoryInspectorSnapshots.RemoveAll(x => x.Id == id) > 0;
                if (!removed)
                {
                    await WriteJsonAsync(stream, 404, new { error = "Memory snapshot not found." }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                RecordWebAudit("memory-snapshot-delete", id, "Success", remoteText, actor);
                SaveOperationsCenterStore();
                await WriteJsonAsync(stream, 200, new { success = true }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (action == "clear")
            {
                int removed;
                lock (operationsLock)
                {
                    removed = operationsCenterStore.MemoryInspectorSnapshots.Count;
                    operationsCenterStore.MemoryInspectorSnapshots.Clear();
                }
                RecordWebAudit("memory-snapshot-clear", $"Removed {removed} snapshot(s)", "Success", remoteText, actor);
                SaveOperationsCenterStore();
                await WriteJsonAsync(stream, 200, new { success = true, removed }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(stream, 400, new { error = "Unsupported memory snapshot action." }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/operations/audit")
        {
            List<WebAuditRecord> auditRows;
            int auditCount;

            lock (operationsLock)
            {
                auditCount = operationsCenterStore.Audit.Count;
                auditRows = operationsCenterStore.Audit
                    .OrderByDescending(x => x.UnixMilliseconds)
                    .Take(300)
                    .ToList();
            }

            await WriteJsonAsync(
                stream,
                200,
                new
                {
                    count = auditCount,
                    rows = auditRows
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            path == "/api/operations/alert-status")
        {
            string id = GetJsonString(request.Body, "id", "");
            string status = GetJsonString(
                request.Body,
                "status",
                "Active");

            if (status is not ("Active" or "Investigating" or "Resolved" or "Ignored"))
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    new { error = "Invalid alert status." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            string actor = requestActor;

            var alert = await RunOnMainThreadAsync(
                () =>
                {
                    var found = operationsCenterStore.Alerts
                        .FirstOrDefault(x => x.Id == id);

                    if (found == null)
                        return null;

                    found.Status = status;
                    RecordWebAudit(
                        "alert-status",
                        $"{found.Title} -> {status}",
                        "Success",
                        remoteText,
                        actor);
                    SaveOperationsCenterStore();
                    return found;
                },
                "web-alert-status").ConfigureAwait(false);

            if (alert == null)
            {
                await WriteJsonAsync(
                    stream,
                    404,
                    new { error = "Alert not found." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(
                stream,
                200,
                new { success = true, alert },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/operations/clear")
        {
            string target;
            string[] ids;
            try
            {
                using var document = JsonDocument.Parse(request.Body);
                target = document.RootElement.GetProperty("target").GetString() ?? "";
                var entries = document.RootElement.GetProperty("ids");
                if (target is not ("alerts" or "actions") || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 2000)
                    throw new ArgumentException("Choose alerts or actions and at most 2,000 displayed record IDs.");
                ids = entries.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                if (ids.Any(x => x.Length == 0 || x.Length > 512)) throw new ArgumentException("Invalid record ID.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                await WriteJsonAsync(stream, 400, new { error = "Invalid clear request." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            int cleared = await RunOnMainThreadAsync(() =>
            {
                var priorAlerts = operationsCenterStore.Alerts.ToList();
                var priorActions = operationsCenterStore.Actions.ToList();
                var priorStatuses = priorActions.Select(x => x.Status).ToArray();
                int count = ActionCenterWorkflow.Clear(operationsCenterStore, target, ids);
                if (count > 0)
                {
                    try { SaveOperationsCenterStore(); }
                    catch
                    {
                        for (int index = 0; index < priorActions.Count; index++) priorActions[index].Status = priorStatuses[index];
                        operationsCenterStore.Alerts = priorAlerts;
                        operationsCenterStore.Actions = priorActions;
                        throw;
                    }
                    RecordWebAudit("operations-clear", $"target={target};cleared={count}", "Success", remoteText, requestActor);
                }
                return count;
            }, "web-operations-clear").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, new { success = true, cleared }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            path == "/api/operations/alert-remove")
        {
            string id = GetJsonString(request.Body, "id", "");
            string actor = requestActor;

            var removed = await RunOnMainThreadAsync(
                () =>
                {
                    var found = operationsCenterStore.Alerts
                        .FirstOrDefault(x => x.Id == id);

                    if (found == null)
                        return null;

                    operationsCenterStore.Alerts.Remove(found);
                    operationsCenterStore.Actions.RemoveAll(x =>
                        string.Equals(x.Id, $"alert:{id}", StringComparison.OrdinalIgnoreCase));

                    RecordWebAudit(
                        "alert-remove",
                        found.Title,
                        "Success",
                        remoteText,
                        actor);
                    SaveOperationsCenterStore();
                    return found;
                },
                "web-alert-remove").ConfigureAwait(false);

            if (removed == null)
            {
                await WriteJsonAsync(
                    stream,
                    404,
                    new { error = "Alert not found." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(
                stream,
                200,
                new
                {
                    success = true,
                    removedId = id,
                    note = "Removed from retained alert history. If the condition is still present, diagnostics may create a new alert."
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            path == "/api/operations/action-status")
        {
            string id = GetJsonString(request.Body, "id", "");
            string status = GetJsonString(
                request.Body,
                "status",
                "Open");

            if (status is not ("Open" or "Investigating" or "Resolved" or "Ignored"))
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    new { error = "Invalid action status." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            string actor = requestActor;

            var action = await RunOnMainThreadAsync(
                () =>
                {
                    var found = operationsCenterStore.Actions
                        .FirstOrDefault(x => x.Id == id);

                    if (found == null)
                        return null;

                    found.Status = status;
                    found.UpdatedUnixMilliseconds =
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    RecordWebAudit(
                        "action-status",
                        $"{found.Title} -> {status}",
                        "Success",
                        remoteText,
                        actor);
                    SaveOperationsCenterStore();
                    return found;
                },
                "web-action-status").ConfigureAwait(false);

            if (action == null)
            {
                await WriteJsonAsync(
                    stream,
                    404,
                    new { error = "Action not found." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(
                stream,
                200,
                new { success = true, action },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            path == "/api/operations/performance-snapshot")
        {
            string label = GetJsonString(
                request.Body,
                "label",
                "Performance snapshot");

            string actor = requestActor;

            var capture = await RunOnMainThreadAsync(
                () =>
                {
                    var snapshot = CapturePerformanceSnapshot(
                        label,
                        out string error);

                    if (snapshot == null)
                    {
                        return new PerformanceSnapshotRouteResult(
                            false,
                            error,
                            null);
                    }

                    RecordWebAudit(
                        "performance-snapshot",
                        snapshot.Label,
                        "Success",
                        remoteText,
                        actor);
                    SaveOperationsCenterStore();

                    return new PerformanceSnapshotRouteResult(
                        true,
                        "Performance snapshot captured.",
                        snapshot);
                },
                "web-performance-snapshot").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                capture.Success ? 200 : 400,
                capture,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            path == "/api/operations/config-snapshot")
        {
            string label = GetJsonString(
                request.Body,
                "label",
                "Manual snapshot");

            string actor = requestActor;

            var snapshot = await RunOnMainThreadAsync(
                () =>
                {
                    var created = CaptureConfigSnapshot(
                        label,
                        actor);

                    RecordWebAudit(
                        "config-snapshot",
                        created.Label,
                        "Success",
                        remoteText,
                        actor);
                    SaveOperationsCenterStore();
                    return created;
                },
                "web-config-snapshot").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                new
                {
                    success = true,
                    snapshot = new
                    {
                        snapshot.Id,
                        snapshot.UnixMilliseconds,
                        snapshot.Label,
                        snapshot.CreatedBy
                    }
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" &&
            path == "/api/operations/config-restore")
        {
            string id = GetJsonString(request.Body, "id", "");
            string confirmation = GetJsonString(request.Body, "confirmation", "").Trim();
            if (!string.Equals(confirmation, "RESTORE CONFIG", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 400, new { error = "Safety confirmation failed. Type RESTORE CONFIG to restore a configuration snapshot." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            string actor = requestActor;

            var restore = await RunOnMainThreadAsync(
                () =>
                {
                    var existing = operationsCenterStore.ConfigSnapshots
                        .FirstOrDefault(x => x.Id == id);

                    if (existing == null)
                    {
                        return new ConfigRestoreRouteResult(
                            false,
                            true,
                            "Configuration snapshot not found.");
                    }

                    CaptureConfigSnapshot(
                        $"Automatic backup before restoring {existing.Label}",
                        actor);

                    string message = RestoreConfigSnapshot(existing);
                    bool success = !message.StartsWith(
                        "Restore failed",
                        StringComparison.OrdinalIgnoreCase);

                    RecordWebAudit(
                        "config-restore",
                        existing.Label,
                        success ? "Success" : message,
                        remoteText,
                        actor);
                    SaveOperationsCenterStore();

                    return new ConfigRestoreRouteResult(
                        success,
                        false,
                        message);
                },
                "web-config-restore").ConfigureAwait(false);

            if (restore.NotFound)
            {
                await WriteJsonAsync(
                    stream,
                    404,
                    new { error = restore.Message },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(
                stream,
                restore.Success ? 200 : 400,
                new
                {
                    success = restore.Success,
                    message = restore.Message
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/server/mods")
        {
            var healthSnapshot = module.ServerHealth?.GetSnapshot(60, 300);
            var profileByMod = (healthSnapshot?.ModHotspots ?? new List<ServerProfilerModHotspot>())
                .Where(x => !string.IsNullOrWhiteSpace(x.ModId))
                .ToDictionary(x => x.ModId, StringComparer.OrdinalIgnoreCase);
            var offThreadByMod = (healthSnapshot?.OffThreadReports ?? new List<ServerProfilerOffThreadReport>())
                .Where(x => !string.IsNullOrWhiteSpace(x.ModId))
                .GroupBy(x => x.ModId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => new
                    {
                        count = g.Count(),
                        maxMs = g.Max(x => x.DurationMs),
                        totalMs = g.Sum(x => x.DurationMs)
                    },
                    StringComparer.OrdinalIgnoreCase);

            bool profileAvailable = healthSnapshot?.LastProfile?.CompletedUnixMilliseconds > 0;

            var mods = await RunOnMainThreadAsync(
                () => api.ModLoader.Mods
                    .Where(mod => mod?.Info != null)
                    .Select(mod =>
                    {
                        string modId = mod.Info.ModID ?? "";
                        profileByMod.TryGetValue(modId, out var perf);
                        offThreadByMod.TryGetValue(modId, out var offThread);

                        double confidenceWeight = perf?.Confidence switch
                        {
                            "High" => 1.0,
                            "Medium" => 0.85,
                            "Low" => 0.65,
                            _ => 0.0
                        };

                        double rawImpact = perf == null
                            ? 0
                            : Math.Min(45, perf.PercentOfProfileTickTime * 1.5) +
                              Math.Min(30, perf.MaxSingleMs / 4.0) +
                              Math.Min(15, perf.SlowSegments * 1.5) +
                              Math.Min(10, (offThread?.maxMs ?? 0) / 10.0);
                        int impactScore = perf == null
                            ? 0
                            : (int)Math.Round(Math.Clamp(rawImpact * confidenceWeight, 0, 100));

                        string impact = !profileAvailable
                            ? "Not measured"
                            : perf == null
                                ? "No measured impact"
                                : impactScore >= 70 ? "Severe"
                                : impactScore >= 45 ? "High"
                                : impactScore >= 20 ? "Moderate"
                                : "Low";

                        return new
                        {
                            modId,
                            name = mod.Info.Name ?? mod.Info.ModID ?? "",
                            version = mod.Info.Version ?? "",
                            type = mod.Info.Type.ToString(),
                            side = mod.Info.Side.ToString(),
                            core = mod.Info.CoreMod,
                            requiredOnClient = mod.Info.RequiredOnClient,
                            requiredOnServer = mod.Info.RequiredOnServer,
                            systems = mod.Systems?.Count ?? 0,
                            fileName = mod.FileName ?? "",
                            measured = perf != null,
                            profileAvailable,
                            impactScore,
                            impact,
                            tickShare = perf?.PercentOfProfileTickTime ?? 0,
                            totalMs = perf?.TotalMs ?? 0,
                            maxSingleMs = perf?.MaxSingleMs ?? 0,
                            calls = perf?.Calls ?? 0,
                            slowSegments = perf?.SlowSegments ?? 0,
                            confidence = perf?.Confidence ?? "None",
                            topMarker = perf?.TopMarker ?? "",
                            offThreadReports = offThread?.count ?? 0,
                            offThreadMaxMs = offThread?.maxMs ?? 0
                        };
                    })
                    .OrderByDescending(x => x.measured)
                    .ThenByDescending(x => x.impactScore)
                    .ThenBy(x => x.core ? 0 : 1)
                    .ThenBy(x => x.modId, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                "web-loaded-mods").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                new
                {
                    count = mods.Count,
                    profileAvailable,
                    profileCompletedUnixMilliseconds = healthSnapshot?.LastProfile?.CompletedUnixMilliseconds ?? 0,
                    profileDurationSeconds = healthSnapshot?.LastProfile?.DurationSeconds ?? 0,
                    profileP95TickMs = healthSnapshot?.LastProfile?.P95TickMs ?? 0,
                    profileMaxTickMs = healthSnapshot?.LastProfile?.MaxTickMs ?? 0,
                    mods
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/server/mods/detail")
        {
            string modId = GetQuery(request, "modId", "").Trim();
            if (string.IsNullOrWhiteSpace(modId))
            {
                await WriteJsonAsync(stream, 400, new { error = "modId is required" }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var details = module.ServerHealth?.GetModPerformanceDetails(modId) ?? new ServerModPerformanceDetails { ModId = modId };
            var modInfo = await RunOnMainThreadAsync(
                () => api.ModLoader.Mods
                    .Where(x => x?.Info != null)
                    .Where(x => string.Equals(x.Info.ModID, modId, StringComparison.OrdinalIgnoreCase))
                    .Select(x => new
                    {
                        modId = x.Info.ModID ?? "",
                        name = x.Info.Name ?? x.Info.ModID ?? "",
                        version = x.Info.Version ?? "",
                        type = x.Info.Type.ToString(),
                        side = x.Info.Side.ToString(),
                        core = x.Info.CoreMod,
                        systems = x.Systems?.Count ?? 0,
                        fileName = x.FileName ?? ""
                    })
                    .FirstOrDefault(),
                "web-mod-performance-detail").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                modInfo == null ? 404 : 200,
                new
                {
                    mod = modInfo,
                    details,
                    note = "Profiler attribution is measured evidence from the most recent bounded engine profile. It identifies where profiled time was attributed; it does not by itself prove a mod bug."
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/overview")
        {
            int windowSeconds = Math.Clamp(
                GetQueryInt(request, "window", 1800),
                60,
                3600);

            var overview = await GetCachedMainThreadReadAsync(
                "performance-overview:" + windowSeconds, 1000,
                () => BuildPerformanceOverview(windowSeconds),
                "web-performance-overview").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                overview,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/page-snapshot")
        {
            int windowSeconds = Math.Clamp(
                GetQueryInt(request, "window", 1800),
                60,
                3600);

            var snapshot = await GetCachedMainThreadReadAsync(
                "performance-page-snapshot:" + windowSeconds, 5000,
                () =>
                {
                    MaintainPerformanceCenter();
                    return new
                    {
                        generatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        overview = BuildPerformanceOverview(windowSeconds),
                        timeline = BuildPerformanceTimeline(windowSeconds),
                        entityScan = new
                        {
                            available = lastPerformanceEntityScan != null,
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
                            history = performanceCenterStore.EnvironmentHistory
                                .OrderByDescending(x => x.DetectedUnixMilliseconds)
                                .Take(20)
                                .ToList()
                        },
                        providers = BuildOptimizationProviders(),
                        conflicts = BuildPatchConflictSnapshot()
                    };
                },
                "web-performance-page-snapshot").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                snapshot,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/incident")
        {
            long start = GetQueryLong(request, "start", 0);

            var detail = await RunOnMainThreadAsync(
                () => BuildIncidentDrilldown(start),
                "web-performance-incident").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                detail,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/timeline")
        {
            int windowSeconds = Math.Clamp(
                GetQueryInt(request, "window", 1800),
                60,
                3600);

            var timeline = await GetCachedMainThreadReadAsync(
                "performance-timeline:" + windowSeconds, 1000,
                () => BuildPerformanceTimeline(windowSeconds),
                "web-performance-timeline").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                timeline,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/entity-growth")
        {
            var growth = await RunOnMainThreadAsync(
                () => new
                {
                    scans = performanceCenterStore.EntityScans.Count,
                    rows = BuildEntityGrowthRows(),
                    note = "Type-level growth compares the last two manual entity scans. Total loaded-entity growth is also visible continuously in the health timeline."
                },
                "web-performance-entity-growth").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                growth,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/environment")
        {
            var environment = await RunOnMainThreadAsync(
                () =>
                {
                    MaintainPerformanceCenter();
                    return new
                    {
                        current = performanceCenterStore.CurrentEnvironment,
                        history = performanceCenterStore.EnvironmentHistory
                            .OrderByDescending(x => x.DetectedUnixMilliseconds)
                            .Take(20)
                            .ToList()
                    };
                },
                "web-performance-environment").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                environment,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/bundle")
        {
            if (IsFeatureCircuitOpen(
                    "diagnostic-bundle",
                    out string bundleCircuit))
            {
                await WriteJsonAsync(
                    stream,
                    503,
                    new { error = bundleCircuit },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var core = await RunOnMainThreadAsync(
                () =>
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
                            audit = operationsCenterStore.Audit
                                .Take(200)
                                .Select(x => new
                                {
                                    x.UnixMilliseconds,
                                    x.Actor,
                                    remote = "(redacted in diagnostic bundle)",
                                    x.Action,
                                    x.Detail,
                                    x.Result
                                })
                                .ToList(),
                            configSnapshots = operationsCenterStore.ConfigSnapshots
                                .Select(x => new
                                {
                                    x.Id,
                                    x.UnixMilliseconds,
                                    x.Label,
                                    x.CreatedBy
                                })
                                .ToList()
                        },
                        loadedMods = api.ModLoader.Mods
                            .Where(x => x?.Info != null)
                            .Select(x => new
                            {
                                modId = x.Info.ModID ?? "",
                                name = x.Info.Name ?? "",
                                version = x.Info.Version ?? "",
                                fileName = x.FileName ?? ""
                            })
                            .OrderBy(x => x.modId, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    };
                },
                "web-performance-bundle-core").ConfigureAwait(false);

            var bundleExtras = await RunOnMainThreadAsync(
                () => new Dictionary<string, object>
                {
                    ["compatibility.json"] = BuildCompatibilitySnapshot(false),
                    ["log-intelligence.json"] = logIntelligence?.GetSnapshot() ?? new LogIntelligenceSnapshot(),
                    ["player-analytics.json"] = module.PlayerAnalytics?.GetSnapshot(90) ?? new PlayerCountAnalyticsSnapshot(),
                    ["announcements.json"] = module.Announcements?.GetSnapshot() ?? new BlockLogger.Announcements.AnnouncementSnapshot(),
                    ["mod-updates.json"] = module.ModUpdates?.GetSnapshot() ?? new BlockLogger.ModUpdates.ModUpdateSnapshot(),
                    ["anti-cheat.json"] = module.AntiCheat?.GetSnapshot() ?? new AntiCheatWebSnapshot(),
                    ["feature-gates.json"] = new
                    {
                        configFile = SuiteFeatureConfig.ConfigFile,
                        restartRequiredForChanges = true,
                        features = module.FeatureSettings.GetEntries().Select(x => new { name = x.Name, enabled = x.Enabled }).ToList(),
                        activeFeatures = module.Features.GetEntries().Select(x => new { name = x.Name, enabled = x.Enabled }).ToList()
                    },
                    ["data-collection-audit.json"] = BuildCollectionAuditSnapshot(),
                    ["login-protection.json"] = BuildLoginProtectionConfigSnapshot(),
                    ["suite-self-test.json"] = BuildServerUtilitiesHealth(),
                    ["web-diagnostics.json"] = BuildWebDiagnosticsSnapshot(),
                    ["smart-diagnosis.json"] = module.ServerHealth?.GetSnapshot(240, 1800) ?? new ServerHealthSnapshot(),
                    ["loaded-mods.json"] = api.ModLoader.Mods
                        .Where(x => x?.Info != null)
                        .Select(x => new
                        {
                            modId = x.Info.ModID ?? "",
                            name = x.Info.Name ?? "",
                            version = x.Info.Version ?? "",
                            fileName = Path.GetFileName(x.FileName ?? "")
                        })
                        .OrderBy(x => x.modId, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                },
                "web-diagnostic-bundle-v2-extras").ConfigureAwait(false);

            object blockActivity;
            if (module.Queries != null)
            {
                var rows = await AwaitWithTimeout(
                    module.Queries.QueryRecentBlockActivityHotspotsAsync(60, 100),
                    TimeSpan.FromSeconds(8),
                    "Diagnostic bundle BlockLogger activity query").ConfigureAwait(false);

                blockActivity = new
                {
                    minutes = 60,
                    rows,
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
                bundle = MeasureFeature(
                    "diagnostic-bundle",
                    "Diagnostic Bundle Export",
                    () => BuildDiagnosticBundleBytes(
                        core,
                        blockActivity,
                        bundleExtras));

                RecordFeatureSuccess(
                    "diagnostic-bundle",
                    "Diagnostic Bundle Export",
                    "Moderate");
            }
            catch (Exception ex)
            {
                RecordFeatureFailure(
                    "diagnostic-bundle",
                    ex,
                    "Diagnostic Bundle Export",
                    "Moderate");

                await WriteJsonAsync(
                    stream,
                    503,
                    new { error = $"{ex.GetType().Name}: {ex.Message}" },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(
                stream,
                200,
                "application/zip",
                bundle,
                cancellationToken,
                new Dictionary<string, string>
                {
                    ["Content-Disposition"] =
                        $"attachment; filename=\"newfies-diagnostics-v2-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip\""
                }).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/entity-scan")
        {
            await WriteJsonAsync(
                stream,
                200,
                new
                {
                    available = lastPerformanceEntityScan != null,
                    snapshot = lastPerformanceEntityScan,
                    manualOnly = true,
                    note = "Entity/chunk hotspot scanning is manual-only so Live Updates cannot repeatedly walk every loaded entity."
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/performance/entity-scan")
        {
            if (IsFeatureCircuitOpen(
                    "entity-scan",
                    out string entityScanCircuit))
            {
                await WriteJsonAsync(
                    stream,
                    503,
                    new { error = entityScanCircuit },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            PerformanceEntityScanSnapshot scan;
            try
            {
                scan = await RunOnMainThreadAsync(
                    () => MeasureFeature(
                        "entity-scan",
                        "Entity / Chunk Hotspot Scan",
                        BuildPerformanceEntityScan),
                    "web-performance-entity-scan").ConfigureAwait(false);

                RecordFeatureSuccess(
                    "entity-scan",
                    "Entity / Chunk Hotspot Scan",
                    "Moderate");
            }
            catch (Exception ex)
            {
                RecordFeatureFailure(
                    "entity-scan",
                    ex,
                    "Entity / Chunk Hotspot Scan",
                    "Moderate");

                await WriteJsonAsync(
                    stream,
                    503,
                    new { error = $"{ex.GetType().Name}: {ex.Message}" },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            lastPerformanceEntityScan = scan;
            performanceCenterStore.EntityScans.Insert(0, scan);
            SavePerformanceCenterStore();

            api.Logger.Notification(
                "[WebDashboard] admin {0} manually scanned entity/chunk performance hotspots from {1}: {2} positioned entities.",
                requestActor,
                remote,
                scan.PositionedEntities);

            RecordWebAudit(
                "entity-chunk-scan",
                $"Manual scan: {scan.PositionedEntities:N0} positioned / {scan.TotalLoadedEntities:N0} total entities.",
                "Success",
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(
                stream,
                200,
                new
                {
                    success = true,
                    snapshot = scan,
                    manualOnly = true
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/block-activity")
        {
            var queries = module.Queries;
            if (queries == null)
            {
                await WriteJsonAsync(
                    stream,
                    503,
                    new { error = "BlockLogger query service is unavailable." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            int minutes = Math.Clamp(
                GetQueryInt(request, "minutes", 30),
                1,
                1440);
            int limit = Math.Clamp(
                GetQueryInt(request, "limit", 50),
                1,
                200);

            var rows = await AwaitWithTimeout(
                queries.QueryRecentBlockActivityHotspotsAsync(minutes, limit),
                TimeSpan.FromSeconds(8),
                "BlockLogger activity hotspot query").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                new
                {
                    minutes,
                    count = rows.Count,
                    rows,
                    measurement = "BlockLogger activity count by coordinate; this is not block-entity CPU timing."
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/conflicts")
        {
            var conflicts = await RunOnMainThreadAsync(
                BuildPatchConflictSnapshot,
                "web-performance-conflicts").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                conflicts,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/performance/providers")
        {
            var providers = await RunOnMainThreadAsync(
                BuildOptimizationProviders,
                "web-performance-providers").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                providers,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if(path=="/api/block-migration"&&(request.Method=="GET"||request.Method=="POST"))
        {
            try{
                string operation=request.Method=="GET"?"status":GetJsonString(request.Body,"operation","");
                var result=await RunOnMainThreadAsync<object>(()=>{
                    var repair=module.BlockRepair??throw new InvalidOperationException("Block migration is unavailable.");
                    List<BlockLogger.WorldRepair.RemapChoice> Choices(){using var json=JsonDocument.Parse(request.Body);if(!json.RootElement.TryGetProperty("mappings",out var items)||items.ValueKind!=JsonValueKind.Array||items.GetArrayLength() is <1 or >32)throw new ArgumentException("Select 1–32 mappings.");return items.EnumerateArray().Select(e=>new BlockLogger.WorldRepair.RemapChoice(e.GetProperty("sourceId").GetInt32(),e.GetProperty("destination").GetString()??"")).ToList();}
                    BlockLogger.WorldRepair.MigrationInventoryOptions Inventory()=>BlockLogger.WorldRepair.MigrationInventoryOptions.Parse(GetJsonString(request.Body,"inventoryMode","preserve"),GetJsonString(request.Body,"inventoryBeforeUtc",""),GetJsonBool(request.Body,"inventoryRiskAcknowledged",false),GetJsonBool(request.Body,"inventoryOverwriteAcknowledged",false));
                    List<int> Sources(){using var json=JsonDocument.Parse(request.Body);if(!json.RootElement.TryGetProperty("sources",out var items)||items.ValueKind!=JsonValueKind.Array||items.GetArrayLength() is <1 or >32)throw new ArgumentException("Select 1–32 IDs.");return items.EnumerateArray().Select(e=>e.GetInt32()).ToList();}
                    return operation switch {
                        "status"=>repair.Snapshot((webSession?.IsOwner??false)||LocalAnonymousMode),
                        "search"=>repair.SearchTargets(GetJsonString(request.Body,"query",""),GetJsonInt(request.Body,"sourceId",0) is int searchSource&&searchSource!=0?searchSource:null,GetJsonBool(request.Body,"allowAir",false),GetJsonBool(request.Body,"allowValidSource",false),GetJsonString(request.Body,"inventoryMode","preserve")),
                        "check-destinations"=>repair.CheckDestinations(Choices(),GetJsonBool(request.Body,"allowAir",false),GetJsonBool(request.Body,"allowValidSource",false)),
                        "mapping-advice"=>repair.StartMappingAdvice(Sources()),
                        "loaded-stage"=>repair.StageLoadedMigration(GetJsonString(request.Body,"previewId",""),GetJsonBool(request.Body,"createBackup",true),GetJsonBool(request.Body,"manualBackupAcknowledged",false),Inventory()),
                        "loaded-disable"=>repair.DisableLoadedMigration(),
                        "loaded-resume"=>repair.ResumeConfiguredMigration(),
                        "rule-set"=>repair.ConfigureMigrationRule(GetJsonInt(request.Body,"sourceId",0),GetJsonString(request.Body,"destination",""),Inventory(),GetJsonBool(request.Body,"createBackup",true),GetJsonBool(request.Body,"manualBackupAcknowledged",false),GetJsonBool(request.Body,"allowAir",false)),
                        "rule-remove"=>repair.RemoveMigrationRule(GetJsonInt(request.Body,"sourceId",0)),
                        "rules-clear"=>repair.ClearMigrationRules(),
                        "automatic-preview"=>repair.PreviewAutomatic(GetJsonBool(request.Body,"createBackup",true),GetJsonBool(request.Body,"manualBackupAcknowledged",false),Inventory()),
                        "batch-preview"=>repair.PreviewBatch(Choices(),GetJsonBool(request.Body,"allowAir",false),GetJsonBool(request.Body,"allowValidSource",false),GetJsonBool(request.Body,"createBackup",true),GetJsonBool(request.Body,"manualBackupAcknowledged",false),Inventory()),
                        "scan"=>repair.StartScan(GetJsonInt(request.Body,"sourceId",0) is int focus&&focus!=0?focus:null,false),
                        "cancel"=>repair.Cancel(),
                        _=>throw new ArgumentException("Unsupported block migration operation.")};
                },"web-block-migration").ConfigureAwait(false);
                if(request.Method=="POST")RecordWebAudit("block-migration-"+operation,"Missing block migration workflow", "Requested",remote.ToString(),requestActor);
                await WriteJsonAsync(stream,200,result,cancellationToken).ConfigureAwait(false);
            }catch(Exception e){await WriteJsonAsync(stream,400,new{error=e.Message},cancellationToken).ConfigureAwait(false);}
            return;
        }

        if (path == "/api/optimizations/newfies" && (request.Method == "GET" || request.Method == "POST"))
        {
            string operation = request.Method == "GET" ? "get" : GetJsonString(request.Body, "operation", "set");
            if (operation is not ("get" or "set" or "profile-start" or "profile-stop" or "export"))
            {
                await WriteJsonAsync(stream, 400, new { error = "Unsupported optimizer operation." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await RunOnMainThreadAsync(
                () => OptimizerControlBridge.Request(api, operation, request.Method == "POST" ? ExtractOptimizerSettings(request.Body) : "{}"),
                "web-newfies-optimizer").ConfigureAwait(false);
            if(request.Method == "POST") RecordWebAudit("optimizer-" + operation, "NOS module/capture control requested; inspect returned module states.", "Requested", remote.ToString(), requestActor);
            await WriteJsonAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/optimizations/tungsten")
        {
            var status = await RunOnMainThreadAsync(
                BuildTungstenStatus,
                "web-tungsten-status").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                status,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/optimizations/tungsten/test")
        {
            string code = GetJsonString(request.Body, "code", "").Trim().ToLowerInvariant();
            bool enabled = GetJsonBool(request.Body, "enabled", true);

            if (!TungstenOptimizations.Any(x => x.Code == code))
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    new { error = "Unknown Tungsten optimization code." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var preparation = await RunOnMainThreadAsync(
                () => PrepareOptimizationExperiment("tungsten", code, enabled),
                "web-tungsten-test-baseline").ConfigureAwait(false);

            if (!preparation.Success || preparation.Experiment == null)
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    new { error = preparation.Message },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await ExecuteTungstenCommandAsync(
                $"/tungsten {code} {(enabled ? "on" : "off")}",
                $"web-tungsten-test-{code}").ConfigureAwait(false);

            if (result.Success)
            {
                await RunOnMainThreadAsync(
                    () =>
                    {
                        optimizationExperiments.Pending = preparation.Experiment;
                        SaveOptimizationExperiments();
                        return true;
                    },
                    "web-tungsten-test-save").ConfigureAwait(false);
            }

            api.Logger.Notification(
                "[WebDashboard] admin {0} requested measured Tungsten {1}={2} from {3}. Result={4}.",
                requestActor,
                code,
                enabled,
                remote,
                result.Status);

            RecordWebAudit(
                "optimization-measure",
                $"Tungsten {code} -> {(enabled ? "ON" : "OFF")}",
                result.Status,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(
                stream,
                result.Success ? 200 : 400,
                new
                {
                    success = result.Success,
                    status = result.Status,
                    message = result.Success
                        ? $"{result.Message} Baseline saved. Restart the server, let diagnostics collect for at least 60 seconds, then return to Optimizations to evaluate improvement."
                        : result.Message,
                    command = result.Command,
                    experiment = result.Success ? preparation.Experiment : null
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/optimizations/experiment/evaluate")
        {
            bool allowSameRuntime = GetJsonBool(request.Body, "allowSameRuntime", false);

            var evaluation = await RunOnMainThreadAsync(
                () => EvaluatePendingOptimizationExperiment(allowSameRuntime),
                "web-optimization-evaluate").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                evaluation.Success ? 200 : 400,
                evaluation,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/optimizations/tungsten/toggle")
        {
            string code = GetJsonString(request.Body, "code", "").Trim().ToLowerInvariant();
            bool enabled = GetJsonBool(request.Body, "enabled", true);

            if (!TungstenOptimizations.Any(x => x.Code == code))
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    new { error = "Unknown Tungsten optimization code." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await ExecuteTungstenCommandAsync(
                $"/tungsten {code} {(enabled ? "on" : "off")}",
                $"web-tungsten-{code}").ConfigureAwait(false);

            api.Logger.Notification(
                "[WebDashboard] admin {0} requested Tungsten {1}={2} from {3}. Result={4}.",
                requestActor,
                code,
                enabled,
                remote,
                result.Status);

            RecordWebAudit(
                "optimization-toggle",
                $"Tungsten {code} -> {(enabled ? "ON" : "OFF")}",
                result.Status,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/optimizations/tungsten/all")
        {
            bool enabled = GetJsonBool(request.Body, "enabled", true);
            var result = await ExecuteTungstenCommandAsync(
                $"/tungsten all {(enabled ? "on" : "off")}",
                "web-tungsten-all").ConfigureAwait(false);

            api.Logger.Notification(
                "[WebDashboard] admin {0} requested Tungsten ALL={1} from {2}. Result={3}.",
                requestActor,
                enabled,
                remote,
                result.Status);

            RecordWebAudit(
                "optimization-all",
                $"Tungsten all -> {(enabled ? "ON" : "OFF")}",
                result.Status,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/optimizations/tungsten/action")
        {
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string command;

            switch (action)
            {
                case "status":
                    command = "/tungsten";
                    break;
                case "stats":
                    command = "/tungsten stats";
                    break;
                case "stats-on":
                    command = "/tungsten stats on";
                    break;
                case "stats-off":
                    command = "/tungsten stats off";
                    break;
                case "reload":
                    command = "/tungsten reload";
                    break;
                case "health":
                    command = "/tungsten health";
                    break;
                case "manifest":
                    command = "/tungsten manifest";
                    break;
                case "benchmark-on":
                    command = "/tungsten benchmarkharness on";
                    break;
                case "benchmark-off":
                    command = "/tungsten benchmarkharness off";
                    break;
                case "frameprofiler-on":
                {
                    int thresholdMs = Math.Clamp(
                        GetJsonInt(request.Body, "thresholdMs", 100),
                        10,
                        5000);
                    command = $"/tungsten frameprofiler on {thresholdMs}";
                    break;
                }
                case "frameprofiler-off":
                    command = "/tungsten frameprofiler off";
                    break;
                case "diag-on":
                case "diag-off":
                case "diag-dump":
                case "diag-reset":
                {
                    string module = GetJsonString(request.Body, "module", "all")
                        .Trim()
                        .ToLowerInvariant();

                    if (module != "all" &&
                        !TungstenOptimizations.Any(x => x.Code == module))
                    {
                        await WriteJsonAsync(
                            stream,
                            400,
                            new { error = "Unknown Tungsten diagnostic module." },
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    string verb = action["diag-".Length..];
                    command = $"/tungsten diag {module} {verb}";
                    break;
                }
                default:
                    await WriteJsonAsync(
                        stream,
                        400,
                        new { error = "Unknown Tungsten action." },
                        cancellationToken).ConfigureAwait(false);
                    return;
            }

            var result = await ExecuteTungstenCommandAsync(
                command,
                "web-tungsten-action").ConfigureAwait(false);

            api.Logger.Notification(
                "[WebDashboard] admin {0} ran Tungsten action {1} from {2}. Result={3}.",
                requestActor,
                action,
                remote,
                result.Status);

            RecordWebAudit(
                "optimization-action",
                $"Tungsten action {action}",
                result.Status,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/optimizations/synergy")
        {
            var statusResult = await ExecuteSynergyCommandAsync(
                "/synergy",
                "web-synergy-status").ConfigureAwait(false);

            var status = await RunOnMainThreadAsync(
                () => BuildSynergyStatus(statusResult),
                "web-synergy-status-build").ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                status,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/optimizations/synergy/test")
        {
            string code = GetJsonString(request.Body, "code", "").Trim().ToLowerInvariant();
            bool enabled = GetJsonBool(request.Body, "enabled", true);

            if (!SynergyOptimizations.Any(x => x.Code == code))
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    new { error = "Unknown Synergy optimization code." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var preparation = await RunOnMainThreadAsync(
                () => PrepareOptimizationExperiment("synergy", code, enabled),
                "web-synergy-test-baseline").ConfigureAwait(false);

            if (!preparation.Success || preparation.Experiment == null)
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    new { error = preparation.Message },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await ExecuteSynergyCommandAsync(
                $"/synergy {code} {(enabled ? "on" : "off")}",
                $"web-synergy-test-{code}").ConfigureAwait(false);

            if (result.Success)
            {
                await RunOnMainThreadAsync(
                    () =>
                    {
                        optimizationExperiments.Pending = preparation.Experiment;
                        SaveOptimizationExperiments();
                        return true;
                    },
                    "web-synergy-test-save").ConfigureAwait(false);
            }

            api.Logger.Notification(
                "[WebDashboard] admin {0} requested measured Synergy {1}={2} from {3}. Result={4}.",
                requestActor,
                code,
                enabled,
                remote,
                result.Status);

            RecordWebAudit(
                "optimization-measure",
                $"Synergy {code} -> {(enabled ? "ON" : "OFF")}",
                result.Status,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(
                stream,
                result.Success ? 200 : 400,
                new
                {
                    success = result.Success,
                    status = result.Status,
                    message = result.Success
                        ? $"{result.Message} Baseline saved. Restart the server, let diagnostics collect for at least 60 seconds, then return to Optimizations to evaluate improvement."
                        : result.Message,
                    command = result.Command,
                    experiment = result.Success ? preparation.Experiment : null
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/optimizations/synergy/toggle")
        {
            string code = GetJsonString(request.Body, "code", "").Trim().ToLowerInvariant();
            bool enabled = GetJsonBool(request.Body, "enabled", true);

            if (!SynergyOptimizations.Any(x => x.Code == code))
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    new { error = "Unknown Synergy optimization code." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await ExecuteSynergyCommandAsync(
                $"/synergy {code} {(enabled ? "on" : "off")}",
                $"web-synergy-{code}").ConfigureAwait(false);

            api.Logger.Notification(
                "[WebDashboard] admin {0} requested Synergy {1}={2} from {3}. Result={4}.",
                requestActor,
                code,
                enabled,
                remote,
                result.Status);

            RecordWebAudit(
                "optimization-toggle",
                $"Synergy {code} -> {(enabled ? "ON" : "OFF")}",
                result.Status,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/optimizations/synergy/all")
        {
            bool enabled = GetJsonBool(request.Body, "enabled", true);
            var result = await ExecuteSynergyCommandAsync(
                $"/synergy all {(enabled ? "on" : "off")}",
                "web-synergy-all").ConfigureAwait(false);

            api.Logger.Notification(
                "[WebDashboard] admin {0} requested Synergy ALL={1} from {2}. Result={3}.",
                requestActor,
                enabled,
                remote,
                result.Status);

            RecordWebAudit(
                "optimization-all",
                $"Synergy all -> {(enabled ? "ON" : "OFF")}",
                result.Status,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/optimizations/synergy/action")
        {
            string action = GetJsonString(request.Body, "action", "").Trim().ToLowerInvariant();
            string command;

            switch (action)
            {
                case "status":
                    command = "/synergy";
                    break;
                case "stats":
                    command = "/synergy stats";
                    break;
                case "reload":
                    command = "/synergy reload";
                    break;
                case "diag-on":
                case "diag-off":
                case "diag-dump":
                case "diag-reset":
                {
                    string module = GetJsonString(request.Body, "module", "all")
                        .Trim()
                        .ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(module)) module = "all";
                    string verb = action["diag-".Length..];
                    command = $"/synergy diag {module} {verb}";
                    break;
                }
                default:
                    await WriteJsonAsync(
                        stream,
                        400,
                        new { error = "Unknown Synergy action." },
                        cancellationToken).ConfigureAwait(false);
                    return;
            }

            var result = await ExecuteSynergyCommandAsync(
                command,
                "web-synergy-action").ConfigureAwait(false);

            api.Logger.Notification(
                "[WebDashboard] admin {0} ran Synergy action {1} from {2}. Result={3}.",
                requestActor,
                action,
                remote,
                result.Status);

            RecordWebAudit(
                "optimization-action",
                $"Synergy action {action}",
                result.Status,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(stream, result.Success ? 200 : 400, result, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if(path=="/api/blocklogger/recording" && request.Method is "GET" or "POST")
        {
            try
            {
                var payload=await RunOnMainThreadAsync<object>(()=>request.Method=="POST"?SaveBlockLoggerRecording(request.Body):BlockLoggerRecordingSnapshot(),"web-blocklogger-recording").ConfigureAwait(false);
                if(request.Method=="POST")RecordWebAudit("blocklogger-recording","Recording and recovery settings","Saved",remoteText,requestActor);
                await WriteJsonAsync(stream,200,payload,cancellationToken).ConfigureAwait(false);
            }
            catch(Exception e){await WriteJsonAsync(stream,400,new{error=e.Message},cancellationToken).ConfigureAwait(false);}
            return;
        }

        if (request.Method == "GET" && path == "/api/blocklogger/maintenance")
        {
            var payload = await RunOnMainThreadAsync(
                () =>
                {
                    var storage = module.Storage;
                    var db = storage?.GetDatabaseHealth(module.Config.RetentionDays);
                    return new
                    {
                        available = storage != null,
                        retentionDays = module.Config.RetentionDays,
                        autoBackupIntervalHours = module.Config.AutoDatabaseBackupIntervalHours,
                        backupRetentionCount = module.Config.DatabaseBackupRetentionCount,
                        databaseBytes = db?.DatabaseBytes ?? 0,
                        walBytes = db?.WalBytes ?? 0,
                        backupBytes = db?.BackupBytes ?? 0,
                        backupCount = db?.BackupCount ?? 0,
                        backupInProgress = db?.BackupInProgress ?? false,
                        databasePath = db?.Path ?? "",
                        note = "Automatic retention deletes old rows gradually. Manual purge can target individual data categories; VACUUM is optional because it can be expensive on a large live database."
                    };
                },
                "web-blocklogger-maintenance-status").ConfigureAwait(false);

            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/blocklogger/maintenance/config")
        {
            int retentionDays = Math.Clamp(GetJsonInt(request.Body, "retentionDays", module.Config.RetentionDays), 0, 3650);
            int autoBackupIntervalHours = Math.Clamp(GetJsonInt(request.Body, "autoBackupIntervalHours", module.Config.AutoDatabaseBackupIntervalHours), 0, 24 * 365);
            int backupRetentionCount = Math.Clamp(GetJsonInt(request.Body, "backupRetentionCount", module.Config.DatabaseBackupRetentionCount), 1, 20);

            var payload = await RunOnMainThreadAsync(
                () =>
                {
                    module.Config.RetentionDays = retentionDays;
                    module.Config.AutoDatabaseBackupIntervalHours = autoBackupIntervalHours;
                    module.Config.DatabaseBackupRetentionCount = backupRetentionCount;
                    module.SaveRuntimeConfig();
                    return new
                    {
                        success = true,
                        retentionDays = module.Config.RetentionDays,
                        autoBackupIntervalHours = module.Config.AutoDatabaseBackupIntervalHours,
                        backupRetentionCount = module.Config.DatabaseBackupRetentionCount,
                        message = module.Config.AutoDatabaseBackupIntervalHours <= 0
                            ? $"Saved. Automatic database backups are disabled; {module.Config.DatabaseBackupRetentionCount} existing healthy backup(s) will be retained at most."
                            : $"Saved. Automatic backups are eligible every {module.Config.AutoDatabaseBackupIntervalHours} hour(s), retaining at most {module.Config.DatabaseBackupRetentionCount} healthy backup file(s)."
                    };
                },
                "web-blocklogger-maintenance-config").ConfigureAwait(false);

            RecordWebAudit(
                "blocklogger-storage-policy",
                $"retention={retentionDays}d autoBackup={autoBackupIntervalHours}h backups={backupRetentionCount}",
                "Success",
                remoteText,
                requestActor);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/blocklogger/purge")
        {
            var storage = module.Storage;
            if (storage == null)
            {
                await WriteJsonAsync(stream, 503, new { error = "BlockLogger storage is unavailable." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            int keepDays = Math.Clamp(GetJsonInt(request.Body, "keepDays", 30), 0, 3650);
            bool purgeEvents = GetJsonBool(request.Body, "purgeEvents", true);
            bool purgeSnapshots = GetJsonBool(request.Body, "purgeSnapshots", true);
            bool purgeEntityActivity = GetJsonBool(request.Body, "purgeEntityActivity", true);
            bool compactAfter = GetJsonBool(request.Body, "compactAfter", false);

            var result = await storage.PurgeDataAsync(
                keepDays,
                purgeEvents,
                purgeSnapshots,
                purgeEntityActivity,
                compactAfter).ConfigureAwait(false);

            RecordWebAudit(
                "blocklogger-purge",
                $"keep={keepDays}d events={purgeEvents} snapshots={purgeSnapshots} entityActivity={purgeEntityActivity} compact={compactAfter}",
                result.Success ? $"Removed {result.TotalRemoved:N0}" : result.Message,
                remoteText,
                requestActor);

            if (result.Success)
                await WriteJsonAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
            else
                await WriteJsonAsync(stream, 409, new { error = result.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/blocklogger/player")
        {
            await HandlePlayerQueryAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/blocklogger/nearby")
        {
            await HandleNearbyQueryAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/animals/status")
        {
            var payload = await RunOnMainThreadAsync(
                () =>
                {
                    var limiter = AnimalLimiterSystem.Current;
                    return new
                    {
                        available = limiter != null,
                        lines = limiter?.GetGuiStatusLines() ?? new List<string> { "Animal Limiter is not available." }
                    };
                },
                "web-animal-status").ConfigureAwait(false);

            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/animals/rules")
        {
            var payload = await RunOnMainThreadAsync(
                () => AnimalLimiterSystem.Current?.GetWebRuleSnapshot() ?? new AnimalLimiterRuleSnapshot { Available = false },
                "web-animal-rules").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/animals/rules/set")
        {
            string pattern = GetJsonString(request.Body, "pattern", "").Trim();
            int limit = Math.Clamp(GetJsonInt(request.Body, "limit", 0), 0, 1000000);
            int radius = Math.Clamp(GetJsonInt(request.Body, "radiusChunks", 0), 0, 32);
            string message = await RunOnMainThreadAsync(
                () => AnimalLimiterSystem.Current?.SetRuleFromGui(pattern, limit, radius) ?? "Animal Limiter is not available.",
                "web-animal-rule-set").ConfigureAwait(false);
            bool success = !message.Contains("not available", StringComparison.OrdinalIgnoreCase) &&
                           !message.StartsWith("Enter", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("animal-rule-set", pattern, success ? "Success" : message, remoteText, requestActor);
            await WriteJsonAsync(stream, success ? 200 : 400, new { success, message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/animals/rules/remove")
        {
            string pattern = GetJsonString(request.Body, "pattern", "").Trim();
            string message = await RunOnMainThreadAsync(
                () => AnimalLimiterSystem.Current?.RemoveRuleFromGui(pattern) ?? "Animal Limiter is not available.",
                "web-animal-rule-remove").ConfigureAwait(false);
            bool success = message.StartsWith("Removed rule", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("animal-rule-remove", pattern, success ? "Success" : message, remoteText, requestActor);
            await WriteJsonAsync(stream, success ? 200 : 400, new { success, message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/animals/exclusions/add")
        {
            string pattern = GetJsonString(request.Body, "pattern", "").Trim();
            string message = await RunOnMainThreadAsync(
                () => AnimalLimiterSystem.Current?.AddExclusionFromGui(pattern) ?? "Animal Limiter is not available.",
                "web-animal-exclusion-add").ConfigureAwait(false);
            bool success = message.StartsWith("Excluded ", StringComparison.OrdinalIgnoreCase) ||
                           message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                           message.Contains("already permanently protected", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("animal-exclusion-add", pattern, success ? "Success" : message, remoteText, requestActor);
            await WriteJsonAsync(stream, success ? 200 : 400, new { success, message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/animals/exclusions/remove")
        {
            string pattern = GetJsonString(request.Body, "pattern", "").Trim();
            string message = await RunOnMainThreadAsync(
                () => AnimalLimiterSystem.Current?.RemoveExclusionFromGui(pattern) ?? "Animal Limiter is not available.",
                "web-animal-exclusion-remove").ConfigureAwait(false);
            bool success = message.StartsWith("Removed exclusion", StringComparison.OrdinalIgnoreCase);
            RecordWebAudit("animal-exclusion-remove", pattern, success ? "Success" : message, remoteText, requestActor);
            await WriteJsonAsync(stream, success ? 200 : 400, new { success, message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/animals/recommendations/start")
        {
            try
            {
                var payload = await RunOnMainThreadAsync(
                    () =>
                    {
                        var limiter = AnimalLimiterSystem.Current;
                        if (limiter == null)
                        {
                            return new AnimalPopulationScanState
                            {
                                Completed = true,
                                Error = "Animal Limiter is not available."
                            };
                        }
                        return limiter.BeginRecommendationScan();
                    },
                    "web-animal-recommendation-scan-start").ConfigureAwait(false);

                RecordWebAudit(
                    "animal-recommendation-scan",
                    $"Started Animal Limiter recommendation scan {payload.ScanId}.",
                    string.IsNullOrWhiteSpace(payload.Error) ? "Started" : "Unavailable",
                    remoteText,
                    requestActor);

                await WriteJsonAsync(
                    stream,
                    string.IsNullOrWhiteSpace(payload.Error) ? 200 : 503,
                    payload,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[WebDashboard] Animal Limiter recommendation scan failed safely: {0}: {1}", ex.GetType().Name, ex.Message);
                await WriteJsonAsync(stream, 503, new { error = $"Recommendation scan could not start: {ex.Message}" }, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (request.Method == "GET" && path == "/api/animals/recommendations")
        {
            long scanId = 0;
            if (request.Query.TryGetValue("id", out var recScanIdText)) long.TryParse(recScanIdText, out scanId);
            var payload = await RunOnMainThreadAsync(
                () => AnimalLimiterSystem.Current?.GetWebRecommendations(scanId) ?? new AnimalLimiterRecommendationSnapshot { ScanId = scanId, Error = "Animal Limiter is not available." },
                "web-animal-recommendations").ConfigureAwait(false);
            await WriteJsonAsync(stream, string.IsNullOrWhiteSpace(payload.Error) ? 200 : 409, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/animals/scan")
        {
            try
            {
                var payload = await RunOnMainThreadAsync(
                    () =>
                    {
                        var limiter = AnimalLimiterSystem.Current;
                        if (limiter == null)
                        {
                            return new AnimalPopulationScanState
                            {
                                Completed = true,
                                Error = "Animal Limiter is not available."
                            };
                        }

                        return limiter.BeginPopulationScan();
                    },
                    "web-animal-population-scan-start").ConfigureAwait(false);

                api.Logger.Notification(
                    "[WebDashboard] admin {0} started Animal Limiter population scan {1} from {2}.",
                    requestActor,
                    payload.ScanId,
                    remote);

                RecordWebAudit(
                    "animal-population-scan",
                    $"Started incremental Animal Limiter population scan {payload.ScanId}.",
                    string.IsNullOrWhiteSpace(payload.Error) ? "Started" : "Unavailable",
                    remote.ToString(),
                    requestActor);

                await WriteJsonAsync(
                    stream,
                    string.IsNullOrWhiteSpace(payload.Error) ? 200 : 503,
                    payload,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                api.Logger.Warning(
                    "[WebDashboard] Animal Limiter population scan start failed safely: {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
                await WriteJsonAsync(
                    stream,
                    503,
                    new { error = $"Population scan could not start: {ex.Message}" },
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (request.Method == "GET" && path == "/api/animals/scan-status")
        {
            long scanId = 0;
            if (request.Query.TryGetValue("id", out var scanIdText))
                long.TryParse(scanIdText, out scanId);

            try
            {
                var payload = await RunOnMainThreadAsync(
                    () =>
                    {
                        var limiter = AnimalLimiterSystem.Current;
                        if (limiter == null)
                        {
                            return new AnimalPopulationScanState
                            {
                                ScanId = scanId,
                                Completed = true,
                                Error = "Animal Limiter is not available."
                            };
                        }
                        return limiter.GetPopulationScanState(scanId);
                    },
                    "web-animal-population-scan-status").ConfigureAwait(false);

                await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                api.Logger.Warning(
                    "[WebDashboard] Animal Limiter population scan status failed safely: {0}: {1}",
                    ex.GetType().Name,
                    ex.Message);
                await WriteJsonAsync(
                    stream,
                    503,
                    new { error = $"Population scan status unavailable: {ex.Message}" },
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (request.Method == "GET" && path == "/api/loginprotection/config")
        {
            var payload = await RunOnMainThreadAsync(
                () => BuildLoginProtectionConfigSnapshot(),
                "web-loginprotection-config").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/loginprotection/config")
        {
            string actor = requestActor;
            var result = await RunOnMainThreadAsync(
                () =>
                {
                    CaptureConfigSnapshot(
                        "Automatic backup before Login Protection web edit",
                        actor);
                    return ApplyLoginProtectionConfig(request.Body);
                },
                "web-loginprotection-config-save").ConfigureAwait(false);

            RecordWebAudit(
                "login-protection-config",
                "Login Protection settings updated from the web dashboard.",
                result.Message,
                remote.ToString(),
                actor);

            object responsePayload;
            if (result.Success)
            {
                var savedConfig = await RunOnMainThreadAsync(
                    BuildLoginProtectionConfigSnapshot,
                    "web-loginprotection-config-after-save").ConfigureAwait(false);
                responsePayload = new { success = true, message = result.Message, config = savedConfig };
            }
            else
            {
                responsePayload = new { success = false, error = result.Message };
            }

            await WriteJsonAsync(
                stream,
                result.Success ? 200 : 400,
                responsePayload,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/loginprotection/status")
        {
            var payload = await RunOnMainThreadAsync(
                () =>
                {
                    var login = LoginProtectionSystem.Current;
                    return new
                    {
                        available = login != null,
                        lines = login?.GetGuiStatusLines() ?? new List<string> { "Login Protection is not available." }
                    };
                },
                "web-loginprotection-status").ConfigureAwait(false);

            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/tick-debug")
        {
            var payload = await RunOnMainThreadAsync(
                BuildNativeTickDebugSnapshot,
                "web-tick-debug-status").ConfigureAwait(false);
            await WriteJsonAsync(stream, 200, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/tick-debug/enabled")
        {
            bool enabled = GetJsonBool(request.Body, "enabled", true);
            var result = await RunOnMainThreadAsync(
                () =>
                {
                    config.TickDebuggingEnabled = enabled;
                    SaveConfig();
                    if (!enabled && nativeSlowTickCaptureRunning)
                        StopNativeSlowTickCaptureNow();
                    return new WebActionResult(true, enabled ? "Tick Debugging enabled." : "Tick Debugging disabled.");
                },
                "web-tick-debug-enabled").ConfigureAwait(false);
            RecordWebAudit("tick-debug-enabled", enabled ? "enabled" : "disabled", "Success", remote.ToString(), requestActor);
            await WriteJsonAsync(stream, 200, new { success = result.Success, message = result.Message, enabled }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/tick-debug/slow-start")
        {
            if (!config.TickDebuggingEnabled)
            {
                await WriteJsonAsync(stream, 409, new { success = false, message = "Tick Debugging is disabled. Enable it before starting a capture." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            int thresholdMs = Math.Clamp(GetJsonInt(request.Body, "thresholdMs", 100), 20, 1000);
            int durationSeconds = Math.Clamp(GetJsonInt(request.Body, "durationSeconds", 30), 5, 120);
            var result = await RunOnMainThreadAsync(
                () => StartNativeSlowTickCapture(thresholdMs, durationSeconds, incidentCapture: false),
                "web-tick-debug-slow-start").ConfigureAwait(false);
            RecordWebAudit("native-slow-ticks", $"Started {durationSeconds}s native slow-tick capture at {thresholdMs}ms.", result.Success ? "Success" : result.Message, remote.ToString(), requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 409, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/tick-debug/slow-stop")
        {
            var result = await RunOnMainThreadAsync(
                StopNativeSlowTickCaptureNow,
                "web-tick-debug-slow-stop").ConfigureAwait(false);
            RecordWebAudit("native-slow-ticks", "Stopped native slow-tick capture.", result.Success ? "Success" : result.Message, remote.ToString(), requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 409, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/tick-debug/incident")
        {
            if (!config.TickDebuggingEnabled)
            {
                await WriteJsonAsync(stream, 409, new { success = false, message = "Tick Debugging is disabled. Enable it before starting an incident capture." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            int thresholdMs = Math.Clamp(GetJsonInt(request.Body, "thresholdMs", 100), 20, 1000);
            int durationSeconds = Math.Clamp(GetJsonInt(request.Body, "durationSeconds", 15), 5, 30);
            var result = await RunOnMainThreadAsync(
                () => StartNativeSlowTickCapture(thresholdMs, durationSeconds, incidentCapture: true),
                "web-tick-debug-incident").ConfigureAwait(false);
            RecordWebAudit("native-incident-capture", $"Started {durationSeconds}s incident capture at {thresholdMs}ms.", result.Success ? "Success" : result.Message, remote.ToString(), requestActor);
            await WriteJsonAsync(stream, result.Success ? 200 : 409, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/tick-debug/action")
        {
            if (!config.TickDebuggingEnabled)
            {
                await WriteJsonAsync(stream, 409, new { success = false, message = "Tick Debugging is disabled. Enable it before running native debug actions." }, cancellationToken).ConfigureAwait(false);
                return;
            }
            string action = GetJsonString(request.Body, "action", "tickhandlers").Trim().ToLowerInvariant();
            string command = action switch
            {
                "tickhandlers" => "/debug tickhandlers",
                "mainthreadstate" => "/debug mainthreadstate",
                "dump-gtblock" => "/debug tickhandlers dump gtblock",
                "dump-gtentity" => "/debug tickhandlers dump gtentity",
                "dump-dcblock" => "/debug tickhandlers dump dcblock",
                "dump-sdcblock" => "/debug tickhandlers dump sdcblock",
                "dump-dcentity" => "/debug tickhandlers dump dcentity",
                _ => ""
            };

            if (command.Length == 0)
            {
                await WriteJsonAsync(stream, 400, new { success = false, error = "Unsupported native debug action." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool detailedDump = action.StartsWith("dump-", StringComparison.Ordinal);
            string logPath = detailedDump ? ResolveNativeTickDebugLogPath(debugLog: true) : ResolveNativeTickDebugLogPath();
            long logOffset = SafeFileLength(logPath);
            var commandResult = await ExecuteWebConsoleCommandAsync(command).ConfigureAwait(false);
            string logDelta = ReadNativeTickDebugLogDelta(logPath, logOffset, 64 * 1024);
            lock (tickDebugLock)
            {
                nativeTickDebugLastAction = action;
                nativeTickDebugLastActionUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                nativeTickDebugLastOutput = CombineNativeDebugOutput(commandResult.Message, logDelta);
            }
            RecordWebAudit("native-tick-debug", $"Executed {command}.", commandResult.Success ? "Success" : commandResult.Message, remote.ToString(), requestActor);
            await WriteJsonAsync(stream, commandResult.Success ? 200 : 409, new { success = commandResult.Success, message = commandResult.Message, action }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/tick-debug/clear")
        {
            string target = GetJsonString(request.Body, "target", "all").Trim().ToLowerInvariant();
            var result = await RunOnMainThreadAsync(
                () =>
                {
                    lock (tickDebugLock)
                    {
                        if (target is "action" or "all")
                        {
                            nativeTickDebugLastAction = "";
                            nativeTickDebugLastActionUnixMilliseconds = 0;
                            nativeTickDebugLastOutput = "";
                        }
                        if (target is "slow" or "all")
                        {
                            nativeSlowTickLastExcerpt = "";
                            nativeTickDebugLastIncidentSummary = "";
                        }
                    }
                    return new WebActionResult(true, target == "all" ? "Tick Debugger output cleared." : $"Tick Debugger {target} output cleared.");
                },
                "web-tick-debug-clear").ConfigureAwait(false);
            RecordWebAudit("native-tick-debug-clear", $"Cleared Tick Debugger output target={target}.", "Success", remote.ToString(), requestActor);
            await WriteJsonAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/profiler/clear")
        {
            string target = GetJsonString(request.Body, "target", "all").Trim().ToLowerInvariant();
            var result = await RunOnMainThreadAsync(
                () =>
                {
                    var health = module.ServerHealth;
                    if (health == null) return new { success = false, message = "Server profiler is not available.", cleared = 0 };

                    int cleared = target switch
                    {
                        "mods" => health.ClearModHotspots(),
                        "ticks" => health.ClearProfileSlowTicks(),
                        "all" => health.ClearModHotspots() + health.ClearProfileSlowTicks(),
                        _ => -1
                    };
                    if (cleared < 0) return new { success = false, message = "Unsupported profiler clear target.", cleared = 0 };
                    InvalidateReadOnlySnapshotCaches();
                    return new { success = true, message = target == "all" ? "Retained profiler result lists cleared." : $"Retained profiler {target} cleared.", cleared };
                },
                "web-profiler-clear").ConfigureAwait(false);
            RecordWebAudit("profiler-clear", $"Cleared retained profiler target={target}.", "Success", remote.ToString(), requestActor);
            await WriteJsonAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/performance/lag-history/clear")
        {
            var result = await RunOnMainThreadAsync(
                () =>
                {
                    int liveCleared = module.ServerHealth?.ClearDiagnosticIncidents() ?? 0;
                    int archivedCleared = performanceCenterStore.Incidents?.Count ?? 0;
                    performanceCenterStore.Incidents?.Clear();
                    SavePerformanceCenterStore();
                    InvalidateReadOnlySnapshotCaches();
                    return new
                    {
                        success = true,
                        message = "Retained lag diagnostic history cleared.",
                        liveCleared,
                        archivedCleared
                    };
                },
                "web-performance-lag-history-clear").ConfigureAwait(false);
            RecordWebAudit("lag-history-clear", "Cleared retained Performance/Profiler lag diagnostic history.", "Success", remote.ToString(), requestActor);
            await WriteJsonAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && path == "/api/profiler")
        {
            int windowSeconds = Math.Clamp(
                GetQueryInt(request, "window", 300),
                60,
                1800);
            int graphPoints = Math.Clamp(
                GetQueryInt(request, "points", 240),
                60,
                600);

            var payload = await RunOnMainThreadAsync(
                () => module.ServerHealth?.GetSnapshot(graphPoints, windowSeconds),
                "web-profiler-status").ConfigureAwait(false);

            if (payload != null) payload.AutomaticIncidentProfilingConfigured = config.AutomaticIncidentProfilingEnabled;
            object responsePayload = payload == null
                ? new { available = false }
                : payload;

            await WriteJsonAsync(
                stream,
                200,
                responsePayload,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/profiler/start")
        {
            int seconds = Math.Clamp(GetJsonInt(request.Body, "seconds", 10), 5, 30);

            var result = await RunOnMainThreadAsync(
                () =>
                {
                    var health = module.ServerHealth;
                    if (health == null)
                        return new WebActionResult(false, "Server profiler is not available.");

                    bool success = health.StartManualProfile(seconds, out string message);
                    return new WebActionResult(success, message);
                },
                "web-profiler-start").ConfigureAwait(false);

            api.Logger.Notification(
                "[WebDashboard] admin {0} requested a {1}s engine profile from {2}. Result={3}.",
                requestActor,
                seconds,
                remote,
                result.Success);

            RecordWebAudit(
                "manual-profiler",
                $"Requested {seconds}s engine profile.",
                result.Success ? "Success" : result.Message,
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(stream, result.Success ? 200 : 409, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST" && path == "/api/profiler/enabled")
        {
            bool value = GetJsonBool(request.Body, "enabled", false);

            var result = await RunOnMainThreadAsync(
                () =>
                {
                    module.Config.ProfilerEnabled = value;
                    module.SaveRuntimeConfig();
                    return new WebActionResult(
                        true,
                        value ? "Profiler diagnostics enabled." : "Profiler diagnostics disabled.");
                },
                "web-profiler-enabled").ConfigureAwait(false);

            api.Logger.Notification(
                "[WebDashboard] admin {0} set profiler Enabled={1} from {2}.",
                requestActor,
                value,
                remote);

            RecordWebAudit(
                "diagnostics-toggle",
                $"Profiler diagnostics -> {(value ? "ON" : "OFF")}",
                "Success",
                remote.ToString(),
                requestActor);

            await WriteJsonAsync(stream, 200, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            stream,
            404,
            new { error = "Not found." },
            cancellationToken).ConfigureAwait(false);
    }

    private object BuildNativeTickDebugSnapshot()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long sent = 0;
        long received = 0;
        try
        {
            sent = api.Server.TotalSentBytes;
            received = api.Server.TotalReceivedBytes;
        }
        catch { }

        lock (tickDebugLock)
        {
            if (tickDebugNetworkSampleUnixMilliseconds > 0 && now > tickDebugNetworkSampleUnixMilliseconds)
            {
                double seconds = (now - tickDebugNetworkSampleUnixMilliseconds) / 1000d;
                tickDebugNetworkSendBytesPerSecond = Math.Max(0, sent - tickDebugNetworkSentBytes) / seconds;
                tickDebugNetworkReceiveBytesPerSecond = Math.Max(0, received - tickDebugNetworkReceivedBytes) / seconds;
            }
            tickDebugNetworkSampleUnixMilliseconds = now;
            tickDebugNetworkSentBytes = sent;
            tickDebugNetworkReceivedBytes = received;

            ServerHealthSnapshot? health = null;
            try { health = module.ServerHealth?.GetSnapshot(60, 300); } catch { }
            var current = health?.Current;
            float tickTime = 0;
            try { tickTime = api.Server.Config.TickTime; } catch { }
            var topProfileMod = health?.ModHotspots
                .Where(x => !x.IsCore && !string.IsNullOrWhiteSpace(x.ModId) && x.ModId != "<unattributed>" && x.Confidence != "Low")
                .OrderByDescending(x => x.TotalMs)
                .FirstOrDefault();
            var worstProfileTick = health?.ProfileSlowTicks
                .OrderByDescending(x => x.TickMs)
                .FirstOrDefault();

            return new
            {
                available = true,
                enabled = config.TickDebuggingEnabled,
                safeDiagnosticsMode = config.SafeDiagnosticsMode,
                tickBudgetMs = tickTime,
                currentMainThreadDelayMs = current?.MainThreadDelayMs ?? 0,
                cpuPercent = current?.CpuPercent ?? 0,
                players = current?.Players ?? api.World.AllOnlinePlayers.Length,
                loadedEntities = current?.LoadedEntities ?? 0,
                loadedChunks = current?.LoadedChunks ?? 0,
                totalSentBytes = sent,
                totalReceivedBytes = received,
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
                    remainingSeconds = nativeSlowTickCaptureRunning ? Math.Max(0, (nativeSlowTickEndsUnixMilliseconds - now + 999) / 1000) : 0,
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
                profileSlowTicks = health?.ProfileSlowTicks,
                profileSlowCauses = health?.ProfileSlowCauses,
                modAttribution = new
                {
                    available = topProfileMod != null && (health?.LastProfile.CompletedUnixMilliseconds ?? 0) > 0,
                    modId = topProfileMod?.ModId ?? "",
                    modName = topProfileMod?.ModName ?? "",
                    confidence = topProfileMod?.Confidence ?? "Low",
                    tickSharePercent = topProfileMod?.PercentOfProfileTickTime ?? 0,
                    maxSegmentMs = topProfileMod?.MaxSingleMs ?? 0,
                    slowSegments = topProfileMod?.SlowSegments ?? 0,
                    topMarker = topProfileMod?.TopMarker ?? "",
                    profileCompletedUnixMilliseconds = health?.LastProfile.CompletedUnixMilliseconds ?? 0,
                    profileKind = health?.LastProfile.CaptureKind ?? "",
                    worstProfileTickMs = worstProfileTick?.TickMs ?? 0,
                    worstProfileTickTopCauses = worstProfileTick?.TopCauseSummary ?? "",
                    note = topProfileMod == null
                        ? "No non-core mod has strong attribution in the latest engine profile."
                        : "Profiler attribution identifies where measured tick time was spent; it is not automatic proof of a mod defect."
                },
                profiler = new
                {
                    enabled = health?.Enabled ?? false,
                    running = health?.ProfileRunning ?? false,
                    remainingSeconds = health?.ProfileRemainingSeconds ?? 0,
                    lastProfile = health?.LastProfile,
                    diagnosticSummary = health?.DiagnosticSummary
                }
            };
        }
    }

    private WebActionResult StartNativeSlowTickCapture(int thresholdMs, int durationSeconds, bool incidentCapture)
    {
        lock (tickDebugLock)
        {
            if (nativeSlowTickCaptureRunning)
                return new WebActionResult(false, "A native slow-tick capture is already running.");
        }

        string profilerMessage = "";
        if (incidentCapture)
        {
            var health = module.ServerHealth;
            if (health == null)
                return new WebActionResult(false, "Server profiler is not available for incident capture.");
            if (!health.StartManualProfile(durationSeconds, out profilerMessage))
                return new WebActionResult(false, profilerMessage);
        }

        string logPath = ResolveNativeTickDebugLogPath();
        long logOffset = SafeFileLength(logPath);
        int generation;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (tickDebugLock)
        {
            generation = ++nativeSlowTickCaptureGeneration;
            nativeSlowTickCaptureRunning = true;
            nativeSlowTickIncidentCapture = incidentCapture;
            nativeSlowTickThresholdMs = thresholdMs;
            nativeSlowTickDurationSeconds = durationSeconds;
            nativeSlowTickStartedUnixMilliseconds = now;
            nativeSlowTickEndsUnixMilliseconds = now + durationSeconds * 1000L;
            nativeSlowTickLogPath = logPath;
            nativeSlowTickLogOffset = logOffset;
            nativeTickDebugLastAction = incidentCapture ? "incident-capture" : "slow-tick-capture";
            nativeTickDebugLastActionUnixMilliseconds = now;
            nativeTickDebugLastOutput = $"Starting native slow-tick capture at {thresholdMs}ms for {durationSeconds}s." + (incidentCapture ? $" Engine profiler: {profilerMessage}" : "");
        }

        var nativeProfiler = api.World.FrameProfiler;
        bool tickProfilerWasActive = nativeProfiler.Enabled || nativeProfiler.PrintSlowTicks;
        lock (tickDebugLock)
        {
            nativeSlowTickWasEnabled = nativeProfiler.PrintSlowTicks;
            nativeSlowTickPreviousThreshold = nativeProfiler.PrintSlowTicksThreshold;
        }
        nativeProfiler.PrintSlowTicks = true;
        nativeProfiler.PrintSlowTicksThreshold = thresholdMs;
        if (!tickProfilerWasActive)
            nativeProfiler.Begin("Newfies Suite native slow-tick capture transition");

        api.Event.RegisterCallback(_ => FinishNativeSlowTickCapture(generation), durationSeconds * 1000);

        string message = incidentCapture
            ? $"Incident capture started for {durationSeconds}s: native slow ticks ≥{thresholdMs}ms plus engine profile."
            : $"Native slow-tick capture started for {durationSeconds}s at ≥{thresholdMs}ms.";
        return new WebActionResult(true, message);
    }

    private WebActionResult StopNativeSlowTickCaptureNow()
    {
        int generation;
        lock (tickDebugLock)
        {
            if (!nativeSlowTickCaptureRunning)
                return new WebActionResult(false, "No native slow-tick capture is running.");
            generation = nativeSlowTickCaptureGeneration;
        }
        FinishNativeSlowTickCapture(generation);
        return new WebActionResult(true, "Native slow-tick capture stopped.");
    }

    private void FinishNativeSlowTickCapture(int generation)
    {
        string logPath;
        long logOffset;
        bool incident;
        lock (tickDebugLock)
        {
            if (!nativeSlowTickCaptureRunning || generation != nativeSlowTickCaptureGeneration)
                return;
            logPath = nativeSlowTickLogPath;
            logOffset = nativeSlowTickLogOffset;
            incident = nativeSlowTickIncidentCapture;
            nativeSlowTickCaptureRunning = false;
            nativeSlowTickEndsUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // Restore the exact native FrameProfiler slow-tick state that existed
        // before this Suite capture. This avoids interfering with an admin who
        // had already enabled /debug logticks independently.
        try
        {
            var nativeProfiler = api.World.FrameProfiler;
            bool restoreEnabled;
            int restoreThreshold;
            lock (tickDebugLock)
            {
                restoreEnabled = nativeSlowTickWasEnabled;
                restoreThreshold = nativeSlowTickPreviousThreshold;
            }
            nativeProfiler.PrintSlowTicksThreshold = restoreThreshold;
            nativeProfiler.PrintSlowTicks = restoreEnabled;
        }
        catch { }

        string excerpt = ReadNativeTickDebugLogDelta(logPath, logOffset, 128 * 1024);

        string incidentSummary = "";
        if (incident)
        {
            try
            {
                var health = module.ServerHealth?.GetSnapshot(120, 300);
                var diag = health?.DiagnosticSummary;
                var profile = health?.LastProfile;
                var topMod = health?.ModHotspots
                    .Where(x => !x.IsCore && !string.IsNullOrWhiteSpace(x.ModId) && x.ModId != "<unattributed>" && x.Confidence != "Low")
                    .OrderByDescending(x => x.TotalMs)
                    .FirstOrDefault();
                string modText = topMod == null
                    ? " no strong non-core mod attribution."
                    : $" likely mod: {topMod.ModId} ({topMod.Confidence}, {topMod.PercentOfProfileTickTime:0.0}% tick share, max {topMod.MaxSingleMs:0.0}ms).";
                incidentSummary = $"Native incident capture completed. Worst rolling main-thread delay: {diag?.WorstDelayMs ?? 0:0.0}ms; " +
                                  $"profile ticks: {profile?.TickCount ?? 0}; profiled tick time: {((profile?.AttributedMs ?? 0) + (profile?.UnattributedMs ?? 0)):0.0}ms;" + modText;
            }
            catch (Exception ex)
            {
                incidentSummary = $"Native incident capture completed; profile summary unavailable: {ex.Message}";
            }
        }

        lock (tickDebugLock)
        {
            nativeSlowTickLastExcerpt = string.IsNullOrWhiteSpace(excerpt)
                ? "Capture completed. No new slow-tick breakdown lines were found in the current server log during this window."
                : excerpt;
            nativeTickDebugLastOutput = incident ? incidentSummary : "Native slow-tick capture completed.";
            if (incident) nativeTickDebugLastIncidentSummary = incidentSummary;
            nativeSlowTickIncidentCapture = false;
        }
    }

    private string ResolveNativeTickDebugLogPath(bool debugLog = false)
    {
        if (!debugLog)
        {
            try
            {
                string fromLogIntelligence = logIntelligence?.GetSnapshot().CurrentLogPath ?? "";
                if (!string.IsNullOrWhiteSpace(fromLogIntelligence)) return fromLogIntelligence;
            }
            catch { }
        }

        try
        {
            string root = api.GetOrCreateDataPath("Logs");
            string stem = debugLog ? "server-debug" : "server-main";
            string[] preferred = { stem + ".txt", stem + ".log" };
            foreach (string name in preferred)
            {
                string candidate = Path.Combine(root, name);
                if (File.Exists(candidate)) return candidate;
            }
            return Directory.Exists(root)
                ? Directory.GetFiles(root, stem + "*", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? ""
                : "";
        }
        catch { return ""; }
    }

    private static string ReadNativeTickDebugLogDelta(string path, long offset, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset < 0 || offset > fs.Length) offset = Math.Max(0, fs.Length - maxBytes);
            long available = fs.Length - offset;
            if (available <= 0) return "";
            if (available > maxBytes) offset = fs.Length - maxBytes;
            fs.Seek(offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
            string text = sr.ReadToEnd();
            if (text.Length > 24000) text = text[^24000..];
            return text.Trim();
        }
        catch { return ""; }
    }

    private static string CombineNativeDebugOutput(string commandOutput, string logDelta)
    {
        commandOutput = commandOutput?.Trim() ?? "";
        logDelta = logDelta?.Trim() ?? "";
        if (commandOutput.Length == 0) return logDelta;
        if (logDelta.Length == 0) return commandOutput;
        return commandOutput + "\n\n--- Native log output ---\n" + logDelta;
    }

    private ServerUtilitiesSelfTest BuildServerUtilitiesHealth()
    {
        return MeasureFeature(
            "self-test",
            "Server Essentials Suite Self-Test",
            () =>
            {
                var checks = new List<SelfTestCheck>();
                var features = module.Features;

                void Check(
                    string key,
                    string name,
                    string status,
                    string detail,
                    string safety = "Safe") =>
                    checks.Add(
                        new SelfTestCheck
                        {
                            Key = key,
                            Name = name,
                            Status = status,
                            Detail = detail,
                            Safety = safety
                        });

                var storage = module.Storage;
                if (storage == null)
                {
                    Check(
                        "database",
                        "BlockLogger SQLite",
                        features.BlockLogger ? "Failed" : "OK",
                        features.BlockLogger ? "Storage service is not available." : "Feature master-disabled; no BlockLogger storage worker is expected.");
                }
                else
                {
                    var db = storage.GetDatabaseHealth(
                        module.Config.RetentionDays);

                    string dbStatus =
                        !db.Ready ? "Warning" :
                        !string.IsNullOrWhiteSpace(db.LastError) &&
                        db.LastErrorUnixMilliseconds >
                        DateTimeOffset.UtcNow.AddMinutes(-10)
                            .ToUnixTimeMilliseconds()
                            ? "Warning"
                            : "OK";

                    Check(
                        "database",
                        "BlockLogger SQLite",
                        dbStatus,
                        $"Ready={db.Ready}; DB={FormatBytes(db.DatabaseBytes)}; WAL={FormatBytes(db.WalBytes)}; queue={db.PendingEvents:N0}/{db.QueueCapacity:N0}; last error={(string.IsNullOrWhiteSpace(db.LastError) ? "none" : db.LastError)}");
                }

                var adminDb = module.Admin?.GetStorageHealth();
                if (adminDb == null || !adminDb.Available)
                {
                    Check("admin-database", "Essentials Admin SQLite", features.Administration ? "Warning" : "OK", features.Administration ? "Admin storage service is unavailable." : "Administration master-disabled; no admin database worker is expected.");
                }
                else
                {
                    string adminDbStatus = !string.IsNullOrWhiteSpace(adminDb.LastError) && adminDb.LastErrorUnixMilliseconds > DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds() ? "Warning" : "OK";
                    Check("admin-database", "Essentials Admin SQLite", adminDbStatus, $"DB={FormatBytes(adminDb.DatabaseBytes)}; WAL={FormatBytes(adminDb.WalBytes)}; players={adminDb.Players:N0}; snapshots={adminDb.InventorySnapshots:N0}; last save={adminDb.LastSaveMilliseconds:N0}ms; integrity={adminDb.Integrity}");
                }

                int runningWebWorkers;
                lock (stateLock)
                    runningWebWorkers = webWorkerTasks.Count(x => !x.IsCompleted);
                bool webPoolHealthy = !config.Enabled || !IsRunning || runningWebWorkers == config.WebWorkerCount;
                Check(
                    "web",
                    "Web Server",
                    config.Enabled == IsRunning && webPoolHealthy
                        ? "OK"
                        : config.Enabled
                            ? "Failed"
                            : "OK",
                    config.Enabled
                        ? IsRunning
                            ? $"Listening at {ListenAddress}; workers={runningWebWorkers}/{config.WebWorkerCount}; queue={Math.Max(0, Volatile.Read(ref webQueuedRequests))}/{config.WebRequestQueueCapacity}; max wait={config.WebQueueMaxWaitMs}ms."
                            : $"Enabled but not listening. Last error: {LastError}"
                        : "Disabled by configuration.");

                Check(
                    "diagnostics",
                    "Profiler / Diagnostics",
                    module.ServerHealth != null || !features.PerformanceDiagnostics ? "OK" : "Failed",
                    !features.PerformanceDiagnostics
                        ? "Feature master-disabled; no diagnostics sampler is expected."
                        : module.ServerHealth == null
                            ? "ServerHealthService is unavailable."
                            : module.ServerHealth.Enabled
                                ? "Lightweight diagnostics enabled."
                                : "Available; lightweight diagnostics currently OFF.");

                var playerAnalytics = module.PlayerAnalytics?.GetSnapshot(7);
                Check(
                    "player-analytics",
                    "Player Analytics",
                    playerAnalytics != null || !features.PlayerAnalytics ? "OK" : "Failed",
                    !features.PlayerAnalytics
                        ? "Feature master-disabled; no analytics reconciliation/save work is expected."
                        : playerAnalytics == null
                        ? "PlayerCountAnalyticsService is unavailable."
                        : $"Tracking {playerAnalytics.CurrentPlayers} online player(s); {playerAnalytics.Daily.Count} daily bucket(s); server time zone={playerAnalytics.ServerTimeZone}.");

                var reportStatus = module.Reports?.GetSnapshot("all", 1);
                Check(
                    "reports",
                    "Reports & Tickets",
                    reportStatus != null || !features.Reports ? "OK" : "Failed",
                    !features.Reports
                        ? "Feature master-disabled; no reports timer/state service is expected."
                        : reportStatus == null
                        ? "ReportsService is unavailable."
                        : $"Reports={reportStatus.TotalCount}; open={reportStatus.OpenCount}; investigating={reportStatus.InvestigatingCount}; root /report owner={reportStatus.RootCommandOwner}.");

                var antiCheat = module.AntiCheat?.GetSnapshot();
                Check(
                    "anti-cheat",
                    "Integrated Anti-Cheat Server",
                    antiCheat != null ? (antiCheat.Enabled && antiCheat.Available ? "OK" : "Warning") : (features.AntiCheat ? "Failed" : "OK"),
                    !features.AntiCheat
                        ? "Feature master-disabled; no anti-cheat network channel/watchdog is expected."
                        : antiCheat == null
                        ? "AntiCheatService is unavailable."
                        : antiCheat.Enabled && antiCheat.Available
                            ? $"Protocol={antiCheat.ProtocolVersion}; clients={antiCheat.ClientsConnected}/{antiCheat.OnlinePlayers}; suspicious={antiCheat.Suspicious}; missing/timed out={antiCheat.MissingOrTimedOut}."
                            : antiCheat.Enabled
                                ? string.IsNullOrWhiteSpace(antiCheat.AvailabilityReason)
                                    ? "Configured ON but anti-cheat runtime is unavailable."
                                    : antiCheat.AvailabilityReason
                                : "Integrated anti-cheat server is disabled in NewfiesAntiCheat.json.");

                Check(
                    "blocklogger-config",
                    "BlockLogger Config",
                    module.Config.SchemaVersion >= BlockLoggerSchemaVersion
                        ? "OK"
                        : "Warning",
                    $"Schema {module.Config.SchemaVersion}; expected {BlockLoggerSchemaVersion}.");

                Check(
                    "web-config",
                    "Web Dashboard Config",
                    config.SchemaVersion >= PersistentSchemaVersion
                        ? "OK"
                        : "Warning",
                    $"Schema {config.SchemaVersion}; expected {PersistentSchemaVersion}.");

                if (AnimalLimiterSystem.Current?.Config != null)
                {
                    Check(
                        "animal-config",
                        "Animal Limiter Config",
                        AnimalLimiterSystem.Current.Config.SchemaVersion >= PersistentSchemaVersion
                            ? "OK"
                            : "Warning",
                        $"Schema {AnimalLimiterSystem.Current.Config.SchemaVersion}; expected {PersistentSchemaVersion}.");
                }

                if (LoginProtectionSystem.Current?.Config != null)
                {
                    Check(
                        "login-config",
                        "Login Protection Config",
                        LoginProtectionSystem.Current.Config.SchemaVersion >= PersistentSchemaVersion
                            ? "OK"
                            : "Warning",
                        $"Schema {LoginProtectionSystem.Current.Config.SchemaVersion}; expected {PersistentSchemaVersion}.");
                }

                Check(
                    "performance-store",
                    "Performance Store",
                    !features.PerformanceCenter || performanceCenterStore.SchemaVersion == PersistentSchemaVersion ? "OK" : "Warning",
                    !features.PerformanceCenter ? "Feature master-disabled; store maintenance is not running." : $"Schema {performanceCenterStore.SchemaVersion}; expected {PersistentSchemaVersion}.");

                Check(
                    "operations-store",
                    "Operations Store",
                    !features.OperationsCenter || operationsCenterStore.SchemaVersion == PersistentSchemaVersion ? "OK" : "Warning",
                    !features.OperationsCenter ? "Feature master-disabled; store maintenance is not running." : $"Schema {operationsCenterStore.SchemaVersion}; expected {PersistentSchemaVersion}.");

                Check(
                    "optimization-store",
                    "Optimization Experiment Store",
                    optimizationExperiments.SchemaVersion ==
                    PersistentSchemaVersion
                        ? "OK"
                        : "Warning",
                    $"Schema {optimizationExperiments.SchemaVersion}; expected {PersistentSchemaVersion}.");

                try
                {
                    string dir =
                        api.GetOrCreateDataPath(
                            "NewfiesServerUtilities");
                    string probe = Path.Combine(
                        dir,
                        ".write-test-" +
                        Guid.NewGuid().ToString("N"));
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);

                    Check(
                        "data-path",
                        "Writable Data Directory",
                        "OK",
                        dir);
                }
                catch (Exception ex)
                {
                    Check(
                        "data-path",
                        "Writable Data Directory",
                        "Failed",
                        $"{ex.GetType().Name}: {ex.Message}");
                }

                Check(
                    "animal-limiter",
                    "Animal Limiter",
                    AnimalLimiterSystem.Current != null || !features.AnimalLimiter ? "OK" : "Warning",
                    !features.AnimalLimiter ? "Feature master-disabled; no limiter hooks/timer are expected." : AnimalLimiterSystem.Current != null ? "Subsystem available." : "Subsystem not available.");

                Check(
                    "login-protection",
                    "Login Protection",
                    LoginProtectionSystem.Current != null || !features.LoginProtection ? "OK" : "Warning",
                    !features.LoginProtection ? "Feature master-disabled; no login/respawn hooks are expected." : LoginProtectionSystem.Current != null ? "Subsystem available." : "Subsystem not available.");

                bool tungsten = api.ModLoader.Mods.Any(x =>
                    x?.Info != null &&
                    string.Equals(
                        x.Info.ModID,
                        "tungsten",
                        StringComparison.OrdinalIgnoreCase));

                Check(
                    "tungsten",
                    "Tungsten Provider",
                    "OK",
                    tungsten
                        ? "Optional Tungsten provider detected."
                        : "Not installed; optimization controls remain unavailable.",
                    "External");

                Check(
                    "security",
                    "Web Security",
                    !IsLoopbackBind(config.BindAddress) &&
                    !PasswordConfigured
                        ? "Failed"
                        : "OK",
                    IsLoopbackBind(config.BindAddress)
                        ? "Loopback-only binding."
                        : PasswordConfigured
                            ? "Remote-capable binding with password authentication configured."
                            : "Unsafe remote binding without password.");

                Check(
                    "safe-mode",
                    "Safe Diagnostics Mode",
                    "OK",
                    config.SafeDiagnosticsMode
                        ? "ON: lightweight diagnostics forced on; manual engine profiler and external optimizer changes locked."
                        : "OFF.",
                    "Safe");

                var breakers = SnapshotCircuitBreakers();
                foreach (var breaker in breakers.Where(x => x.Open))
                {
                    Check(
                        "breaker:" + breaker.Key,
                        breaker.Name + " Circuit Breaker",
                        "Warning",
                        breaker.LastError,
                        breaker.Safety);
                }

                string overall =
                    checks.Any(x => x.Status == "Failed")
                        ? "Failed"
                        : checks.Any(x => x.Status == "Warning")
                            ? "Warning"
                            : "OK";

                string version =
                    api.ModLoader.Mods.FirstOrDefault(x =>
                        x?.Info != null &&
                        string.Equals(
                            x.Info.ModID,
                            "newfiesserveressentialssuite",
                            StringComparison.OrdinalIgnoreCase))
                    ?.Info?.Version ?? SuiteRelease.Version;

                var result = new ServerUtilitiesSelfTest
                {
                    BuildId = BuildId,
                    ModVersion = version,
                    TargetGameVersion = TargetGameVersion,
                    UnixMilliseconds =
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Overall = overall,
                    Checks = checks,
                    CircuitBreakers = breakers,
                    Overhead = SnapshotOverhead(),
                    WebPolicies = WebRoutePolicies.ToList()
                };

                lastSelfTest = result;
                return result;
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
                note =
                    "Diagnostic bundles include configuration snapshot metadata only. Authentication secrets and raw snapshot JSON are excluded; audit remote addresses are redacted."
            }
        };
    }

    private static string FormatBytes(long value)
    {
        if (value >= 1024L * 1024L * 1024L)
            return $"{value / (1024d * 1024d * 1024d):0.00} GB";
        if (value >= 1024L * 1024L)
            return $"{value / (1024d * 1024d):0.0} MB";
        if (value >= 1024L)
            return $"{value / 1024d:0.0} KB";
        return $"{value} B";
    }

    private object BuildTerrainExportDashboardFallback()
    {
        return new
        {
            utc = DateTime.UtcNow,
            exportBusy = true,
            playersOnline = 0,
            blockLogger = (object?)null,
            profiler = (object?)null,
            animalLimiter = new { available = false, enabled = false, trackedEntities = 0, trackedRegions = 0 },
            loginProtection = new { available = false, enabled = false, activeProtected = 0 },
            operations = new { activeAlerts = 0, criticalAlerts = 0, openActions = 0, configSnapshots = 0 },
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
            if (snapshotCache.TryGet("live", out var cached))
            {
                Interlocked.Increment(ref webSnapshotCacheHits);
                return Task.FromResult(cached!);
            }
            return Task.FromResult<object>(new
            {
                utc = DateTime.UtcNow,
                exportBusy = true,
                note = "Terrain export is running; a lightweight cached dashboard snapshot will resume when the game thread is free."
            });
        }
        return GetCachedMainThreadReadAsync("live", config.LiveSnapshotMaxAgeMs,
            BuildLiveSnapshot, "web-live-snapshot");
    }

    private Task<object> GetDashboardSnapshotAsync(bool terrainExportBusy)
    {
        if (terrainExportBusy)
        {
            if (snapshotCache.TryGet("dashboard", out var cached))
            {
                Interlocked.Increment(ref webSnapshotCacheHits);
                return Task.FromResult(cached!);
            }
            return Task.FromResult(BuildTerrainExportDashboardFallback());
        }
        return GetCachedMainThreadReadAsync("dashboard", config.DashboardSnapshotMaxAgeMs,
            BuildDashboardSnapshot, "web-dashboard-status");
    }

    private Task<object> GetCachedMainThreadReadAsync(string key, int maxAgeMs, Func<object> builder, string taskCode)
    {
        var task = snapshotCache.GetAsync(key, maxAgeMs, async () =>
        {
            object payload = await RunOnMainThreadAsync(() =>
            {
                if (disposed) throw new ObjectDisposedException(nameof(WebDashboardService));
                if (!config.Enabled) throw new InvalidOperationException("Web panel snapshot refresh is paused.");
                return builder();
            }, taskCode, skipIfTimedOut: true).ConfigureAwait(false);
            Interlocked.Increment(ref webSnapshotRefreshes);
            return payload;
        }, out bool servedCached,
        ex => api.Logger.Debug("[WebDashboard] cached read refresh {0} skipped: {1}: {2}",
            key, ex.GetType().Name, ex.Message),
        allowRefresh: !disposed && config.Enabled);
        if (servedCached) Interlocked.Increment(ref webSnapshotCacheHits);
        else Interlocked.Increment(ref webSnapshotCacheMisses);
        return task;
    }

    private void InvalidateReadOnlySnapshotCaches()
    {
        // Keep the last successful payload available, but prevent older in-flight
        // builders from publishing a fresh result after a mutation. Refresh only
        // requested pages; a write need not schedule unrelated diagnostic work.
        snapshotCache.Invalidate();
    }

    private object BuildDashboardSnapshot()
    {
        var storage = module.Storage;
        var health = module.ServerHealth?.GetSnapshot(120);
        var limiter = AnimalLimiterSystem.Current;
        var login = LoginProtectionSystem.Current;

        return new
        {
            utc = DateTime.UtcNow,
            playersOnline = api.World.AllOnlinePlayers?.Length ?? 0,
            blockLogger = storage == null
                ? null
                : new
                {
                    ready = storage.IsReady,
                    fullFidelity = module.Config.LogEntitySpawn && module.Config.LogEntityDeath && module.Config.LogEntityDespawn &&
                                   !module.Config.AggregateEntitySpawnDespawn && module.Config.BulkEntityPositionSampleIntervalMs == 0,
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
                },
            profiler = health == null
                ? null
                : new
                {
                    enabled = health.Enabled,
                    running = health.ProfileRunning,
                    current = health.Current,
                    diagnostic = health.DiagnosticSummary,
                    smart = health.SmartAnalysis,
                    recentLagEvents = health.DiagnosticIncidents.Take(5).ToList(),
                    recentIncidentGroups = health.SmartAnalysis.IncidentGroups.Take(5).ToList(),
                    lastProfile = health.LastProfile
                },
            animalLimiter = new
            {
                available = limiter != null,
                enabled = limiter?.Config.Enabled == true,
                trackedEntities = limiter?.HealthTrackedEntityCount ?? 0,
                trackedRegions = limiter?.HealthTrackedRegionCount ?? 0
            },
            loginProtection = new
            {
                available = login != null,
                enabled = login?.Config.Enabled == true,
                activeProtected = login?.ActiveProtectedCount ?? 0
            },
            operations = new
            {
                activeAlerts = operationsCenterStore.Alerts.Count(x =>
                    x.Status is "Active" or "Investigating"),
                criticalAlerts = operationsCenterStore.Alerts.Count(x =>
                    (x.Status is "Active" or "Investigating") &&
                    x.Severity == "Critical"),
                openActions = operationsCenterStore.Actions.Count(x =>
                    x.Status is "Open" or "Investigating"),
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

    private async Task HandlePlayerQueryAsync(
        NetworkStream stream,
        WebRequest request,
        CancellationToken cancellationToken)
    {
        var queries = module.Queries;
        if (queries == null)
        {
            await WriteJsonAsync(stream, 503, new { error = "BlockLogger query service is unavailable." }, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string player = GetQuery(request, "player", "").Trim();
        if (player.Length == 0)
        {
            await WriteJsonAsync(stream, 400, new { error = "Enter a player name or UID." }, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (player.Length > 80) player = player[..80];

        int limit = Math.Clamp(
            GetQueryInt(request, "limit", module.Config.DefaultDisplayCount),
            1,
            module.Config.MaxDisplayCount);
        int offset = Math.Clamp(GetQueryInt(request, "offset", 0), 0, 2000);

        string? category = NormalizePlayerCategory(GetQuery(request, "category", "any"));
        var rows = await AwaitWithTimeout(
            queries.QueryPlayerAsync(player, limit, category, offset),
            TimeSpan.FromSeconds(8),
            "BlockLogger player query").ConfigureAwait(false);

        await WriteJsonAsync(
            stream,
            200,
            new
            {
                count = rows.Count,
                offset,
                limit,
                hasPrevious = offset > 0,
                hasNext = rows.Count >= limit && offset + limit < 2000,
                rows = rows.Select(row => FormatWebRecord(row, null)).ToList()
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleNearbyQueryAsync(
        NetworkStream stream,
        WebRequest request,
        CancellationToken cancellationToken)
    {
        var queries = module.Queries;
        if (queries == null)
        {
            await WriteJsonAsync(stream, 503, new { error = "BlockLogger query service is unavailable." }, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string view = GetQuery(request, "view", "blocks").Trim().ToLowerInvariant();
        if (view is not ("blocks" or "containers" or "entities"))
            view = "blocks";

        int x = GetQueryInt(request, "x", 0);
        int y = GetQueryInt(request, "y", 0);
        int z = GetQueryInt(request, "z", 0);
        int dimension = GetQueryInt(request, "dimension", 0);
        int radius = Math.Clamp(GetQueryInt(request, "radius", module.Config.DefaultRadius), 0, module.Config.MaxRadius);
        int limit = Math.Clamp(
            GetQueryInt(request, "limit", module.Config.DefaultDisplayCount),
            1,
            module.Config.MaxDisplayCount);
        int offset = Math.Clamp(GetQueryInt(request, "offset", 0), 0, 2000);

        string search = GetQuery(request, "search", "").Trim();
        if (search.Length > 80) search = search[..80];
        string? filter = string.IsNullOrWhiteSpace(search) ? null : search;

        string? tag = NormalizeWebAction(view, GetQuery(request, "action", "any"));

        Task<List<LogEventRecord>> queryTask = view == "entities"
            ? queries.QueryEntitiesAsync(
                x, y, z, dimension, radius, limit, filter, tag, offset)
            : queries.QueryBlocksAsync(
                x, y, z, dimension, radius, limit, filter, tag, offset);

        List<LogEventRecord> rows = await AwaitWithTimeout(
            queryTask,
            TimeSpan.FromSeconds(8),
            "BlockLogger nearby query").ConfigureAwait(false);

        var target = new WebTarget(x, y, z);

        await WriteJsonAsync(
            stream,
            200,
            new
            {
                view,
                count = rows.Count,
                offset,
                limit,
                hasPrevious = offset > 0,
                hasNext = rows.Count >= limit && offset + limit < 2000,
                radius,
                target,
                rows = rows.Select(row => FormatWebRecord(row, target)).ToList()
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static object FormatWebRecord(LogEventRecord record, WebTarget? target)
    {
        string actor = string.IsNullOrWhiteSpace(record.PlayerName)
            ? "Unknown"
            : record.PlayerName;

        string entityCode = Meta(record, "entityCode") ?? "";
        string code = !string.IsNullOrWhiteSpace(record.BlockCode)
            ? record.BlockCode
            : entityCode.Length > 0 ? entityCode : "unknown";

        string detail = record.Action switch
        {
            LogActionType.ContainerTake => $"took {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} from {code}",
            LogActionType.ContainerPut => $"put {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} into {code}",
            LogActionType.ContainerOpen => $"opened {code}",
            LogActionType.BlockBreak => $"broke {code}",
            LogActionType.BlockPlace => $"placed {code}",
            LogActionType.BlockUse => $"used {code}",
            LogActionType.EntityInteract => $"interacted with {(entityCode.Length > 0 ? entityCode : "entity")}",
            LogActionType.EntitySpawn => $"spawned {(entityCode.Length > 0 ? entityCode : "entity")}{CountSuffix(record)}",
            LogActionType.EntityDeath => $"{(entityCode.Length > 0 ? entityCode : "entity")} died",
            LogActionType.EntityDespawn => $"despawned {(entityCode.Length > 0 ? entityCode : "entity")}{CountSuffix(record)}",
            _ => record.Action.ToString()
        };

        return new
        {
            timestamp = IngameTimeFormatter.FormatNewfoundlandTimestamp(record.TimestampUtc),
            timestampUtc = record.TimestampUtc,
            actor,
            actorUid = record.PlayerUid,
            action = record.Action.ToString(),
            detail,
            x = record.X,
            y = record.Y,
            z = record.Z,
            deltaX = target == null ? 0 : record.X - target.X,
            deltaZ = target == null ? 0 : record.Z - target.Z,
            ingameDate = record.IngameDate,
            blockCode = record.BlockCode,
            entityCode,
            meta = record.Meta
        };
    }

    private static string? Meta(LogEventRecord record, string key) =>
        record.Meta != null && record.Meta.TryGetValue(key, out var value) ? value : null;

    private static int Quantity(LogEventRecord record) =>
        int.TryParse(
            Meta(record, "quantity"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : 0;

    private static string CountSuffix(LogEventRecord record) =>
        long.TryParse(
            Meta(record, "eventCount"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long count) && count > 1
            ? $" x{count:N0}"
            : "";

    private bool TryAuthenticateWebUser(
        string username,
        string password,
        string remoteKey,
        out WebIdentity identity,
        out bool accountLocked)
    {
        identity = new WebIdentity("", "", false);
        accountLocked = false;
        // Bound unauthenticated work before comparisons, password hashing or account I/O.
        if (username == null || username.Length > 256 || password == null || password.Length > 256)
            return false;
        username = username.Trim();
        if (username.Length == 0 || username.Length > 64)
            return false;

        if (PasswordConfigured &&
            ConstantTimeEquals(username, config.AdminUsername) &&
            VerifyPassword(password))
        {
            identity = new WebIdentity(config.AdminUsername, "Owner", true);
            return true;
        }

        lock (webAccountLock)
        {
            var account = FindPanelAccount(username);
            if (account == null || !account.Enabled)
                return false;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (account.LockedUntilUnixMilliseconds > now)
            {
                accountLocked = true;
                return false;
            }

            if (!VerifyAccountPassword(account, password))
            {
                account.FailedLoginCount++;
                if (account.FailedLoginCount >= config.MaxFailedLogins)
                {
                    account.FailedLoginCount = 0;
                    account.LockedUntilUnixMilliseconds =
                        DateTimeOffset.UtcNow.AddMinutes(config.LockoutMinutes).ToUnixTimeMilliseconds();
                    accountLocked = true;
                }
                SaveWebAdminAccounts();
                return false;
            }

            account.FailedLoginCount = 0;
            account.LockedUntilUnixMilliseconds = 0;
            account.LastLoginUnixMilliseconds = now;
            account.LastLoginRemote = remoteKey;
            SaveWebAdminAccounts();
            identity = new WebIdentity(account.Username, account.Role, false);
            return true;
        }
    }

    private async Task HandleLoginAsync(
        NetworkStream stream,
        WebRequest request,
        IPAddress remote,
        CancellationToken cancellationToken)
    {
        string remoteKey = remote.ToString();

        bool currentlyLocked;
        lock (authLock)
        {
            PruneFailedLoginsUnsafe();
            currentlyLocked =
                failedLogins.TryGetValue(remoteKey, out var state) &&
                state.LockedUntilUtc > DateTime.UtcNow;
        }

        if (currentlyLocked)
        {
            RecordWebAudit(
                "login",
                "Login attempted while source was temporarily locked.",
                "Locked",
                remoteKey,
                "unknown");

            await WriteJsonAsync(
                stream,
                429,
                new { error = "Too many failed logins. Try again later." },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string username = GetJsonString(request.Body, "username", "");
        string password = GetJsonString(request.Body, "password", "");

        bool ok = TryAuthenticateWebUser(
            username,
            password,
            remoteKey,
            out var identity,
            out bool accountLocked);

        if (!ok)
        {
            bool lockedNow = accountLocked;

            if (!accountLocked)
            {
                lock (authLock)
                {
                    if (!failedLogins.TryGetValue(remoteKey, out var state))
                    {
                        EnsureFailedLoginCapacityUnsafe();
                        state = new FailedLoginState();
                        failedLogins[remoteKey] = state;
                    }

                    state.Failures++;
                    state.LastFailureUtc = DateTime.UtcNow;

                    if (state.Failures >= config.MaxFailedLogins)
                    {
                        state.LockedUntilUtc = DateTime.UtcNow.AddMinutes(config.LockoutMinutes);
                        state.Failures = 0;
                        lockedNow = true;
                    }
                }
            }

            api.Logger.Warning(
                "[WebDashboard] failed login from {0}{1}.",
                remote,
                lockedNow ? " (temporarily locked)" : "");

            RecordWebAudit(
                "login",
                $"Failed login for username '{username}'.",
                lockedNow ? "Failed / locked" : "Failed",
                remoteKey,
                string.IsNullOrWhiteSpace(username) ? "unknown" : username);

            await WriteJsonAsync(
                stream,
                lockedNow ? 429 : 401,
                new { error = lockedNow ? "Too many failed logins. Try again later." : "Invalid username or password." },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string token = RandomToken(32);
        string csrf = RandomToken(24);
        var expires = DateTime.UtcNow.AddMinutes(config.SessionTimeoutMinutes);

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

        api.Logger.Notification(
            "[WebDashboard] panel login for {0} ({1}) from {2}.",
            identity.Username,
            identity.Role,
            remote);

        RecordWebAudit(
            "login",
            "Administrator authenticated.",
            "Success",
            remoteKey,
            identity.Username);
        string cookie =
            $"nsu_session={token}; Path=/; HttpOnly; SameSite=Strict; Max-Age={config.SessionTimeoutMinutes * 60}" +
            (config.SecureCookies ? "; Secure" : "");

        await WriteJsonAsync(
            stream,
            200,
            new
            {
                success = true,
                username = identity.Username,
                role = identity.Role,
                owner = identity.IsOwner,
                csrfToken = csrf,
                expiresUtc = expires,
                safeDiagnosticsMode = config.SafeDiagnosticsMode
            },
            cancellationToken,
            setCookie: cookie).ConfigureAwait(false);
    }

    private bool TryGetSession(WebRequest request, out WebSession? session)
    {
        session = null;

        if (!TryGetCookie(request, "nsu_session", out string token) ||
            string.IsNullOrWhiteSpace(token))
            return false;

        lock (authLock)
        {
            PruneSessionsUnsafe();

            if (!sessions.TryGetValue(token, out session))
                return false;

            // Session timeout is absolute from login, matching the cookie Max-Age.
            return true;
        }
    }

    private void RemoveSession(WebRequest request)
    {
        if (!TryGetCookie(request, "nsu_session", out string token))
            return;

        lock (authLock)
        {
            sessions.Remove(token);
        }
    }

    private void PruneSessionsUnsafe()
    {
        DateTime now = DateTime.UtcNow;
        foreach (string key in sessions
                     .Where(x => x.Value.ExpiresUtc <= now)
                     .Select(x => x.Key)
                     .ToList())
        {
            sessions.Remove(key);
        }
    }

    private void EnsureSessionCapacityUnsafe()
    {
        if (sessions.Count < MaxWebSessions) return;
        var oldest = sessions
            .OrderBy(x => x.Value.CreatedUtc)
            .Select(x => x.Key)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(oldest)) sessions.Remove(oldest);
    }

    private void EnsureFailedLoginCapacityUnsafe()
    {
        if (failedLogins.Count < MaxFailedLoginSources) return;
        var oldest = failedLogins
            .OrderBy(x => x.Value.LastFailureUtc)
            .Select(x => x.Key)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(oldest)) failedLogins.Remove(oldest);
    }

    private void PruneFailedLoginsUnsafe()
    {
        DateTime cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (string key in failedLogins
                     .Where(x =>
                         x.Value.LockedUntilUtc <= DateTime.UtcNow &&
                         x.Value.LastFailureUtc < cutoff)
                     .Select(x => x.Key)
                     .ToList())
        {
            failedLogins.Remove(key);
        }
    }

    private bool IsLocalAnonymousRequest(IPAddress remote) =>
        LocalAnonymousMode && IPAddress.IsLoopback(remote);

    private bool IsRemoteAllowed(IPAddress remote)
    {
        if (config.AllowedNetworks.Count == 0)
            return true;

        foreach (string entry in config.AllowedNetworks)
        {
            if (IpNetworkContains(entry, remote))
                return true;
        }

        return false;
    }

    private static bool IpNetworkContains(string entry, IPAddress remote)
    {
        entry = (entry ?? "").Trim();
        if (entry.Length == 0) return false;

        string[] parts = entry.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var network))
            return false;

        if (network.AddressFamily == AddressFamily.InterNetwork &&
            remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();

        if (network.AddressFamily != remote.AddressFamily)
            return false;

        byte[] networkBytes = network.GetAddressBytes();
        byte[] remoteBytes = remote.GetAddressBytes();

        if (parts.Length == 1)
            return networkBytes.SequenceEqual(remoteBytes);

        if (!int.TryParse(parts[1], out int prefixBits))
            return false;

        int maxBits = networkBytes.Length * 8;
        if (prefixBits < 0 || prefixBits > maxBits)
            return false;

        int fullBytes = prefixBits / 8;
        int remainingBits = prefixBits % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != remoteBytes[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        int mask = 0xFF << (8 - remainingBits);
        return (networkBytes[fullBytes] & mask) ==
               (remoteBytes[fullBytes] & mask);
    }

    private static bool TryParseBindAddress(string? value, out IPAddress address)
    {
        value = (value ?? "").Trim();

        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("loopback", StringComparison.OrdinalIgnoreCase))
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

        if (IPAddress.TryParse(value, out var parsed))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private static bool IsLoopbackBind(string? bindAddress)
    {
        return TryParseBindAddress(bindAddress, out var address) &&
               IPAddress.IsLoopback(address);
    }

    private static string DisplayBindAddress(string address)
    {
        if (address is "0.0.0.0" or "*")
            return "<server-ip>";

        if (address == "::")
            return "[server-ipv6]";

        if (IPAddress.TryParse(address, out var parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetworkV6)
            return $"[{address}]";

        return address;
    }

    private void RegisterCommands()
    {
#pragma warning disable CS0618
        api.RegisterCommand(
            "serverutils",
            "Configure Newfies Server Essentials Suite",
            "/serverutils web status|on|off|bind|port|publicurl|username|password|sessions|logoutall|reload|securecookies",
            HandleCommand,
            "controlserver");
#pragma warning restore CS0618
    }

    private void HandleCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        string section = (args.PopWord() ?? "").Trim().ToLowerInvariant();

        if (section != "web")
        {
            SendCommand(
                player,
                groupId,
                "Usage: /serverutils web status|on|off|bind <ip>|port <port>|publicurl <url|off>|username <name>|password <password>|sessions|logoutall|reload|securecookies on|off",
                false);
            return;
        }

        string op = (args.PopWord() ?? "status").Trim().ToLowerInvariant();
        string message;
        bool success = true;

        switch (op)
        {
            case "status":
                message = string.Join(" | ", GetGuiStatusLines().Take(10));
                break;

            case "on":
                message = SetEnabled(true);
                success = IsRunning;
                break;

            case "off":
                message = SetEnabled(false);
                break;

            case "bind":
                message = SetBindAddress(args.PopWord());
                success = !message.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase);
                break;

            case "port":
                success = int.TryParse(args.PopWord(), out int port);
                message = success
                    ? SetPort(port)
                    : "Usage: /serverutils web port <1-65535>";
                break;

            case "publicurl":
                message = SetPublicUrl(args.PopWord());
                success = !message.StartsWith("Public URL must", StringComparison.OrdinalIgnoreCase) &&
                          !message.StartsWith("Public URL is too long", StringComparison.OrdinalIgnoreCase);
                break;

            case "username":
                message = SetUsername(args.PopWord());
                success = !message.Contains("cannot be empty", StringComparison.OrdinalIgnoreCase);
                break;

            case "password":
            {
                string password = args.PopWord() ?? "";
                message = SetPassword(password);
                success = message.StartsWith("Web admin password updated", StringComparison.Ordinal);
                break;
            }

            case "sessions":
                message = $"Active web sessions: {ActiveSessionCount}.";
                break;

            case "logoutall":
                LogoutAllSessions();
                message = "All web sessions invalidated.";
                break;

            case "reload":
                ReloadConfig();
                message = IsRunning || !config.Enabled
                    ? "Web config reloaded."
                    : $"Web config reloaded, but listener failed: {LastError}";
                success = IsRunning || !config.Enabled;
                break;

            case "securecookies":
            {
                string value = (args.PopWord() ?? "").Trim().ToLowerInvariant();
                if (value is not ("on" or "off"))
                {
                    success = false;
                    message = "Usage: /serverutils web securecookies on|off";
                    break;
                }

                message = SetSecureCookies(value == "on");
                break;
            }

            default:
                success = false;
                message = "Unknown web command. Use /serverutils web status.";
                break;
        }

        SendCommand(player, groupId, message, success);
    }

    private void SendCommand(IServerPlayer player, int groupId, string message, bool success)
    {
        api.SendMessage(
            player,
            groupId,
            message,
            success ? EnumChatType.CommandSuccess : EnumChatType.CommandError);
    }

    private void MaintainPerformanceCenter()
    {
        if (!module.Features.PerformanceCenter) return;
        long maintenanceNow =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (lastPerformanceMaintenanceUnixMilliseconds > 0 &&
            maintenanceNow - lastPerformanceMaintenanceUnixMilliseconds < 10_000)
            return;

        lastPerformanceMaintenanceUnixMilliseconds = maintenanceNow;

        var healthService = module.ServerHealth;
        if (healthService == null || !healthService.Enabled)
            return;

        // Internal black-box archiving asks for the native 1-second rolling
        // history. Public web graph endpoints still cap requested points at 600.
        var snapshot = healthService.GetSnapshot(1800, 1800);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool changed = false;

        foreach (var group in snapshot.SmartAnalysis.IncidentGroups)
        {
            // Wait for the 15-second post-incident side of the black box before
            // archiving it.
            if (group.EndUnixMilliseconds > now - 15_000)
                continue;

            string id =
                $"{group.StartUnixMilliseconds:x}-{group.EndUnixMilliseconds:x}";

            if (performanceCenterStore.Incidents.Any(x => x.Id == id))
                continue;

            var source = snapshot.Samples;
            var duringRows = source
                .Where(x =>
                    x.UnixMilliseconds >= group.StartUnixMilliseconds &&
                    x.UnixMilliseconds <= group.EndUnixMilliseconds)
                .ToList();
            var databaseSnapshot = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays)
                ?? new DatabaseHealthSnapshot { Available = false, Message = "BlockLogger storage unavailable." };
            var webSnapshot = BuildWebDiagnosticsSnapshot();
            List<WebAuditRecord> recentAdminActions;
            lock (operationsLock)
            {
                recentAdminActions = (operationsCenterStore.Audit ?? new List<WebAuditRecord>())
                    .Where(x =>
                        x.UnixMilliseconds >= group.StartUnixMilliseconds - 120_000L &&
                        x.UnixMilliseconds <= group.EndUnixMilliseconds + 30_000L)
                    .OrderByDescending(x => x.UnixMilliseconds)
                    .Take(12)
                    .ToList();
            }

            performanceCenterStore.Incidents.Add(
                new PerformanceIncidentArchive
                {
                    Id = id,
                    StartUnixMilliseconds = group.StartUnixMilliseconds,
                    EndUnixMilliseconds = group.EndUnixMilliseconds,
                    CapturedUnixMilliseconds = now,
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
                    Before = source
                        .Where(x =>
                            x.UnixMilliseconds >= group.StartUnixMilliseconds - 30_000 &&
                            x.UnixMilliseconds < group.StartUnixMilliseconds)
                        .ToList(),
                    During = duringRows,
                    After = source
                        .Where(x =>
                            x.UnixMilliseconds > group.EndUnixMilliseconds &&
                            x.UnixMilliseconds <= group.EndUnixMilliseconds + 15_000)
                        .ToList(),
                    Database = databaseSnapshot,
                    WebServer = webSnapshot,
                    RecentAdminActions = recentAdminActions,
                    CauseScores = BuildIncidentCauseScores(
                        group,
                        duringRows,
                        webSnapshot,
                        databaseSnapshot)
                });
            changed = true;
        }

        var environment = CaptureEnvironmentSnapshot(snapshot);

        if (performanceCenterStore.CurrentEnvironment == null)
        {
            performanceCenterStore.CurrentEnvironment = environment;
            changed = true;
        }
        else if (!string.Equals(
                     performanceCenterStore.CurrentEnvironment.Fingerprint,
                     environment.Fingerprint,
                     StringComparison.Ordinal))
        {
            var previous = performanceCenterStore.CurrentEnvironment;
            performanceCenterStore.EnvironmentHistory.Insert(
                0,
                new PerformanceEnvironmentChange
                {
                    DetectedUnixMilliseconds = now,
                    BeforeFingerprint = previous.Fingerprint,
                    AfterFingerprint = environment.Fingerprint,
                    ChangedMods = DescribeModChanges(
                        previous.Mods,
                        environment.Mods),
                    Before = previous.Metrics,
                    After = environment.Metrics,
                    Result = "Collecting",
                    Confidence = "Low",
                    Reason =
                        "Mod environment changed. Collecting at least 60 current diagnostic samples before grading the regression."
                });

            performanceCenterStore.CurrentEnvironment = environment;
            changed = true;
        }
        else
        {
            performanceCenterStore.CurrentEnvironment = environment;

            var pending = performanceCenterStore.EnvironmentHistory
                .FirstOrDefault(x =>
                    x.Result == "Collecting" &&
                    string.Equals(
                        x.AfterFingerprint,
                        environment.Fingerprint,
                        StringComparison.Ordinal));

            if (pending != null)
            {
                pending.After = environment.Metrics;
                if (environment.Metrics.SampleCount >= 60)
                {
                    GradeEnvironmentChange(pending);
                    changed = true;
                }
            }
        }

        if (changed)
            SavePerformanceCenterStore();
    }

    private PerformanceEnvironmentSnapshot CaptureEnvironmentSnapshot(
        ServerHealthSnapshot snapshot)
    {
        // A Vintage Story server's loaded mod set is fixed for the life of this process.
        // Cache the sorted identities and fingerprint once instead of rebuilding/sorting/
        // hashing them on each Performance Center maintenance cycle.
        if (environmentModIdentityCache == null)
        {
            environmentModIdentityCache = api.ModLoader.Mods
                .Where(x => x?.Info != null)
                .Select(x => new PerformanceModIdentity
                {
                    ModId = x.Info.ModID ?? "",
                    Name = x.Info.Name ?? x.Info.ModID ?? "",
                    Version = x.Info.Version ?? "",
                    FileName = x.FileName ?? ""
                })
                .OrderBy(x => x.ModId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string fingerprintSource = string.Join(
                "\n",
                environmentModIdentityCache.Select(x =>
                    $"{x.ModId}|{x.Version}|{x.FileName}"));

            environmentModFingerprintCache = Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(fingerprintSource)))
                .ToLowerInvariant();
        }

        return new PerformanceEnvironmentSnapshot
        {
            Fingerprint = environmentModFingerprintCache,
            CapturedUnixMilliseconds =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Mods = environmentModIdentityCache,
            Metrics = CaptureEnvironmentMetrics(snapshot)
        };
    }

    private static PerformanceEnvironmentMetrics CaptureEnvironmentMetrics(
        ServerHealthSnapshot snapshot)
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

    private static List<string> DescribeModChanges(
        List<PerformanceModIdentity> before,
        List<PerformanceModIdentity> after)
    {
        var oldMap = before.ToDictionary(
            x => x.ModId,
            x => x,
            StringComparer.OrdinalIgnoreCase);
        var newMap = after.ToDictionary(
            x => x.ModId,
            x => x,
            StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();

        foreach (var mod in newMap.Values)
        {
            if (!oldMap.TryGetValue(mod.ModId, out var old))
            {
                result.Add($"+ {mod.ModId} {mod.Version}");
            }
            else if (!string.Equals(
                         old.Version,
                         mod.Version,
                         StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(
                         old.FileName,
                         mod.FileName,
                         StringComparison.OrdinalIgnoreCase))
            {
                result.Add(
                    $"~ {mod.ModId} {old.Version} -> {mod.Version}");
            }
        }

        foreach (var mod in oldMap.Values)
        {
            if (!newMap.ContainsKey(mod.ModId))
                result.Add($"- {mod.ModId} {mod.Version}");
        }

        return result
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void GradeEnvironmentChange(
        PerformanceEnvironmentChange change)
    {
        double delay = LowerIsBetterPercent(
            change.Before.DelayMedianMs,
            change.After.DelayMedianMs,
            5);
        double cpu = LowerIsBetterPercent(
            change.Before.CpuMedianPercent,
            change.After.CpuMedianPercent,
            10);
        double writer = LowerIsBetterPercent(
            change.Before.WriterMedianMs,
            change.After.WriterMedianMs,
            5);
        double queue = LowerIsBetterPercent(
            change.Before.QueueMedian,
            change.After.QueueMedian,
            10);
        double p95 = LowerIsBetterPercent(
            change.Before.P95ProfileTickMs,
            change.After.P95ProfileTickMs,
            10);
        double health =
            change.After.HealthScore - change.Before.HealthScore;

        // Positive means improved, negative means regressed.
        double improvement =
            delay * 0.35 +
            cpu * 0.25 +
            writer * 0.10 +
            queue * 0.10 +
            p95 * 0.15 +
            health * 0.05;

        improvement = Math.Clamp(improvement, -100, 100);
        change.RegressionScore = -improvement;

        change.Result =
            improvement >= 8 ? "Improved" :
            improvement <= -8 ? "Regressed" :
            "No meaningful change";

        change.Confidence =
            change.Before.SampleCount >= 60 &&
            change.After.SampleCount >= 60
                ? "Medium"
                : "Low";

        change.Reason = string.Join(
            " | ",
            new[]
            {
                $"Delay {SignedPercent(delay)}",
                $"CPU {SignedPercent(cpu)}",
                $"Writer {SignedPercent(writer)}",
                $"Queue {SignedPercent(queue)}",
                $"Profile P95 {SignedPercent(p95)}",
                $"Health {health:+0;-0;0} points"
            });
    }

    private List<PerformanceEntityGrowthRow> BuildEntityGrowthRows()
    {
        var scans = performanceCenterStore.EntityScans
            .OrderByDescending(x => x.UnixMilliseconds)
            .Take(2)
            .ToList();

        if (scans.Count < 2)
            return new List<PerformanceEntityGrowthRow>();

        var after = scans[0];
        var before = scans[1];

        var beforeMap = before.EntityTypes.ToDictionary(
            x => x.Code,
            x => x.Count,
            StringComparer.OrdinalIgnoreCase);
        var afterMap = after.EntityTypes.ToDictionary(
            x => x.Code,
            x => x.Count,
            StringComparer.OrdinalIgnoreCase);

        return afterMap.Keys
            .Union(beforeMap.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(code =>
            {
                beforeMap.TryGetValue(code, out int oldCount);
                afterMap.TryGetValue(code, out int newCount);
                int delta = newCount - oldCount;
                double percent = oldCount <= 0
                    ? newCount > 0 ? 100 : 0
                    : delta * 100d / oldCount;

                string severity =
                    delta >= 500 && percent >= 100 ? "Critical" :
                    delta >= 250 && percent >= 75 ? "High" :
                    delta >= 100 && percent >= 40 ? "Watch" :
                    "Info";

                return new PerformanceEntityGrowthRow
                {
                    Code = code,
                    Before = oldCount,
                    After = newCount,
                    Delta = delta,
                    PercentChange = percent,
                    Severity = severity,
                    Reason = severity == "Info"
                        ? "No strong scan-to-scan runaway signal."
                        : $"Entity population changed {delta:+#;-#;0} ({percent:+0;-0;0}%) between the last two manual scans."
                };
            })
            .Where(x => x.Delta != 0)
            .OrderByDescending(x =>
                x.Severity == "Critical" ? 4 :
                x.Severity == "High" ? 3 :
                x.Severity == "Watch" ? 2 : 1)
            .ThenByDescending(x => Math.Abs(x.Delta))
            .Take(100)
            .ToList();
    }

    private object BuildIncidentDrilldown(long startUnixMilliseconds)
    {
        MaintainPerformanceCenter();

        var incident = performanceCenterStore.Incidents
            .OrderBy(x =>
                Math.Min(
                    Math.Abs(x.StartUnixMilliseconds - startUnixMilliseconds),
                    startUnixMilliseconds >= x.StartUnixMilliseconds && startUnixMilliseconds <= x.EndUnixMilliseconds
                        ? 0
                        : Math.Abs(x.EndUnixMilliseconds - startUnixMilliseconds)))
            .FirstOrDefault();

        if (incident == null ||
            (startUnixMilliseconds > 0 &&
             startUnixMilliseconds < incident.StartUnixMilliseconds - 120_000L) ||
            (startUnixMilliseconds > 0 &&
             startUnixMilliseconds > incident.EndUnixMilliseconds + 120_000L))
            return new
            {
                found = false,
                message = "No archived black-box incident matched that timestamp."
            };

        object Summary(List<ServerHealthSample> rows)
        {
            if (rows.Count == 0)
                return new
                {
                    samples = 0,
                    delayAvg = 0d,
                    delayMax = 0d,
                    cpuAvg = 0d,
                    cpuMax = 0d,
                    heapAvg = 0d,
                    entitiesAvg = 0d,
                    chunksAvg = 0d,
                    queueMax = 0,
                    writerMax = 0L
                };

            return new
            {
                samples = rows.Count,
                delayAvg = rows.Average(x => x.MainThreadDelayMs),
                delayMax = rows.Max(x => x.MainThreadDelayMs),
                cpuAvg = rows.Average(x => x.CpuPercent),
                cpuMax = rows.Max(x => x.CpuPercent),
                heapAvg = rows.Average(x => x.ManagedHeapMb),
                entitiesAvg = rows.Average(x => x.LoadedEntities),
                chunksAvg = rows.Average(x => x.LoadedChunks),
                queueMax = rows.Max(x => x.BlockLoggerQueue),
                writerMax = rows.Max(x => x.BlockLoggerWriterMs)
            };
        }

        return new
        {
            found = true,
            incident,
            beforeSummary = Summary(incident.Before),
            duringSummary = Summary(incident.During),
            afterSummary = Summary(incident.After),
            entityGrowth = BuildEntityGrowthRows().Take(10).ToList(),
            latestEntityScan = lastPerformanceEntityScan,
            environment = performanceCenterStore.EnvironmentHistory
                .Where(x =>
                    Math.Abs(
                        x.DetectedUnixMilliseconds -
                        incident.StartUnixMilliseconds) <= 30 * 60_000L)
                .OrderBy(x =>
                    Math.Abs(
                        x.DetectedUnixMilliseconds -
                        incident.StartUnixMilliseconds))
                .FirstOrDefault()
        };
    }

    private static string ExtractOptimizerSettings(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("settings", out var settings) ? settings.GetRawText() : "{}";
    }

    private object BuildPerformanceTimeline(int windowSeconds)
    {
        var health = module.ServerHealth?.GetSnapshot(600, windowSeconds);
        if (health == null)
            return new
            {
                available = false,
                samples = Array.Empty<object>()
            };

        long cutoff =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() -
            windowSeconds * 1000L;

        return new
        {
            available = true,
            samples = health.Samples,
            incidents = performanceCenterStore.Incidents
                .Where(x => x.StartUnixMilliseconds >= cutoff)
                .OrderBy(x => x.StartUnixMilliseconds)
                .ToList(),
            environmentChanges = performanceCenterStore.EnvironmentHistory
                .Where(x => x.DetectedUnixMilliseconds >= cutoff)
                .OrderBy(x => x.DetectedUnixMilliseconds)
                .ToList(),
            entityScans = performanceCenterStore.EntityScans
                .Where(x => x.UnixMilliseconds >= cutoff)
                .OrderBy(x => x.UnixMilliseconds)
                .Select(x => new
                {
                    x.UnixMilliseconds,
                    x.TotalLoadedEntities,
                    x.PositionedEntities
                })
                .ToList()
        };
    }

    private object BuildPerformanceWindowSummary(ServerHealthSnapshot health)
    {
        var rows = (health.Samples ?? new List<ServerHealthSample>())
            .OrderBy(x => x.UnixMilliseconds)
            .ToList();

        if (rows.Count == 0)
        {
            return new
            {
                available = false,
                sampleCount = 0
            };
        }

        static double Median(IEnumerable<double> source)
        {
            var values = source.OrderBy(x => x).ToArray();
            if (values.Length == 0) return 0;
            int mid = values.Length / 2;
            return values.Length % 2 == 0
                ? (values[mid - 1] + values[mid]) / 2d
                : values[mid];
        }

        static double Percentile95(IEnumerable<double> source)
        {
            var values = source.OrderBy(x => x).ToArray();
            if (values.Length == 0) return 0;
            int index = (int)Math.Min(values.Length - 1, Math.Ceiling(values.Length * 0.95d) - 1);
            return values[index];
        }

        var first = rows[0];
        var last = rows[^1];
        double durationSeconds = Math.Max(0, (last.UnixMilliseconds - first.UnixMilliseconds) / 1000d);
        long droppedIncrease = Math.Max(0, last.BlockLoggerDropped - first.BlockLoggerDropped);
        int gen2Increase = Math.Max(0, last.Gen2Collections - first.Gen2Collections);

        return new
        {
            available = true,
            sampleCount = rows.Count,
            durationSeconds,
            delayMedianMs = Median(rows.Select(x => x.MainThreadDelayMs)),
            delayP95Ms = Percentile95(rows.Select(x => x.MainThreadDelayMs)),
            delayMaxMs = rows.Max(x => x.MainThreadDelayMs),
            cpuAveragePercent = rows.Average(x => x.CpuPercent),
            cpuP95Percent = Percentile95(rows.Select(x => x.CpuPercent)),
            cpuMaxPercent = rows.Max(x => x.CpuPercent),
            heapDeltaMb = last.ManagedHeapMb - first.ManagedHeapMb,
            privateMemoryDeltaMb = last.PrivateMemoryMb - first.PrivateMemoryMb,
            entityDelta = last.LoadedEntities - first.LoadedEntities,
            maxBlockLoggerQueue = rows.Max(x => x.BlockLoggerQueue),
            maxBlockLoggerWriterMs = rows.Max(x => x.BlockLoggerWriterMs),
            blockLoggerDroppedIncrease = droppedIncrease,
            gen2CollectionsIncrease = gen2Increase,
            delayOver100Count = rows.Count(x => x.MainThreadDelayMs >= 100),
            delayOver250Count = rows.Count(x => x.MainThreadDelayMs >= 250)
        };
    }

    private object BuildPerformanceOverview(int windowSeconds)
    {
        var health = module.ServerHealth?.GetSnapshot(360, windowSeconds);

        if (health == null)
        {
            return new
            {
                available = false,
                message = "Server health diagnostics are unavailable.",
                entityScan = lastPerformanceEntityScan
            };
        }

        MaintainPerformanceCenter();

        var incidents = performanceCenterStore.Incidents
            .Where(x =>
                x.StartUnixMilliseconds >=
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() -
                windowSeconds * 1000L)
            .OrderByDescending(x => x.EndUnixMilliseconds)
            .Take(40)
            .ToList();

        var growth = BuildEntityGrowthRows();
        var suiteTimings = SuiteTimingRegistry.GetSnapshot();
        var windowSummary = BuildPerformanceWindowSummary(health);
        var latestProfileMod = health.ModHotspots
            .Where(x => !x.IsCore && !string.IsNullOrWhiteSpace(x.ModId) && x.ModId != "<unattributed>" && x.Confidence != "Low")
            .OrderByDescending(x => x.TotalMs)
            .FirstOrDefault();

        return new
        {
            available = true,
            diagnosticsEnabled = health.Enabled,
            current = health.Current,
            diagnosticSummary = health.DiagnosticSummary,
            smart = health.SmartAnalysis,
            incidents,
            incidentCount = incidents.Count,
            archivedIncidentCount = performanceCenterStore.Incidents.Count,
            entityScan = lastPerformanceEntityScan,
            entityGrowth = growth.Take(20).ToList(),
            environmentCurrent = performanceCenterStore.CurrentEnvironment,
            environmentHistory = performanceCenterStore.EnvironmentHistory
                .OrderByDescending(x => x.DetectedUnixMilliseconds)
                .Take(5)
                .ToList(),
            webDiagnostics = BuildWebDiagnosticsSnapshot(),
            databaseDiagnostics = module.Storage?.GetDatabaseHealth(module.Config.RetentionDays),
            suiteTimings,
            windowSummary,
            automaticModAttribution = new
            {
                enabled = health.AutomaticIncidentProfilingEnabled,
                configured = config.AutomaticIncidentProfilingEnabled,
                profileDurationSeconds = health.AutomaticIncidentProfileDurationSeconds,
                cooldownSeconds = health.AutomaticIncidentProfileCooldownSeconds,
                lastTriggeredUnixMilliseconds = health.LastAutomaticIncidentTriggerUnixMilliseconds,
                lastTriggerDelayMs = health.LastAutomaticIncidentTriggerDelayMs,
                latestProfileCompletedUnixMilliseconds = health.LastProfile.CompletedUnixMilliseconds,
                latestProfileKind = health.LastProfile.CaptureKind,
                likelyModId = latestProfileMod?.ModId ?? "",
                likelyModName = latestProfileMod?.ModName ?? "",
                confidence = latestProfileMod?.Confidence ?? "Low",
                tickSharePercent = latestProfileMod?.PercentOfProfileTickTime ?? 0,
                maxSegmentMs = latestProfileMod?.MaxSingleMs ?? 0
            },
            entityScanManualOnly = true,
            notes = new[]
            {
                "Automatic lag incidents use lightweight rolling diagnostics; high/critical main-thread stalls without a strong known subsystem correlation can trigger a short cooldown-limited profiler burst for mod attribution.",
                "Entity/chunk hotspot scans are manual-only.",
                "Block/container activity hotspots count recent BlockLogger events and do not represent block-entity CPU timing.",
                "Harmony conflict inspection reports multiple patch owners on the same method; overlap is a warning, not proof of incompatibility."
            }
        };
    }

    private PerformanceEntityScanSnapshot BuildPerformanceEntityScan()
    {
        var typeCounts = new Dictionary<
            string,
            (string RuntimeType, int Count, HashSet<string> Chunks)>(
            StringComparer.OrdinalIgnoreCase);

        var chunks = new Dictionary<
            string,
            (int X, int Z, int Dimension, int Entities, int Players, Dictionary<string, int> Types)>(
            StringComparer.Ordinal);

        int positioned = 0;
        int unpositioned = 0;
        int total = 0;

        foreach (var entity in api.World.LoadedEntities.Values)
        {
            if (entity == null) continue;
            total++;

            string code =
                entity.Code?.ToString() ??
                entity.Properties?.Code?.ToString() ??
                entity.GetType().Name;

            string runtimeType = entity.GetType().Name;

            if (!EntityPositionResolver.TryGetCoordinates(
                    entity,
                    out double x,
                    out _,
                    out double z,
                    out int dimension))
            {
                unpositioned++;

                if (!typeCounts.TryGetValue(code, out var missingType))
                    missingType = (runtimeType, 0, new HashSet<string>());

                missingType.Count++;
                typeCounts[code] = missingType;
                continue;
            }

            positioned++;
            int chunkX = (int)Math.Floor(x / GlobalConstants.ChunkSize);
            int chunkZ = (int)Math.Floor(z / GlobalConstants.ChunkSize);
            string chunkKey = $"{dimension}:{chunkX}:{chunkZ}";

            if (!typeCounts.TryGetValue(code, out var type))
                type = (runtimeType, 0, new HashSet<string>());

            type.Count++;
            type.Chunks.Add(chunkKey);
            typeCounts[code] = type;

            if (!chunks.TryGetValue(chunkKey, out var chunk))
            {
                chunk = (
                    chunkX,
                    chunkZ,
                    dimension,
                    0,
                    0,
                    new Dictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase));
            }

            chunk.Entities++;
            chunk.Types.TryGetValue(code, out int count);
            chunk.Types[code] = count + 1;

            if (entity is Vintagestory.API.Common.EntityPlayer)
                chunk.Players++;

            chunks[chunkKey] = chunk;
        }

        var typeRows = typeCounts
            .Select(pair => new PerformanceEntityTypeHotspot
            {
                Code = pair.Key,
                RuntimeType = pair.Value.RuntimeType,
                Count = pair.Value.Count,
                ChunkCount = pair.Value.Chunks.Count
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();

        var chunkRows = chunks.Values
            .Select(chunk =>
            {
                var topTypes = chunk.Types
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .Select(x => $"{x.Key} ×{x.Value}");

                int pressure =
                    chunk.Entities +
                    chunk.Types.Count * 3 +
                    chunk.Players * 25;

                return new PerformanceChunkHotspot
                {
                    ChunkX = chunk.X,
                    ChunkZ = chunk.Z,
                    Dimension = chunk.Dimension,
                    EntityCount = chunk.Entities,
                    PlayerCount = chunk.Players,
                    UniqueEntityTypes = chunk.Types.Count,
                    TopEntityTypes = string.Join(", ", topTypes),
                    PressureScore = pressure
                };
            })
            .OrderByDescending(x => x.PressureScore)
            .ThenByDescending(x => x.EntityCount)
            .Take(200)
            .ToList();

        return new PerformanceEntityScanSnapshot
        {
            UnixMilliseconds =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TotalLoadedEntities = total,
            PositionedEntities = positioned,
            UnpositionedEntities = unpositioned,
            EntityTypes = typeRows,
            Chunks = chunkRows
        };
    }

    private object BuildLiveSnapshot()
    {
        var health = module.ServerHealth;
        var current = health?.Current ?? new ServerHealthSample();
        var storage = module.Storage;
        var limiter = AnimalLimiterSystem.Current;
        var login = LoginProtectionSystem.Current;

        int activeAlerts;
        int criticalAlerts;
        int openActions;
        lock (operationsLock)
        {
            activeAlerts = operationsCenterStore.Alerts.Count(x =>
                x.Status is "Active" or "Investigating");
            criticalAlerts = operationsCenterStore.Alerts.Count(x =>
                (x.Status is "Active" or "Investigating") &&
                x.Severity == "Critical");
            openActions = operationsCenterStore.Actions.Count(x =>
                x.Status is "Open" or "Investigating");
        }

        return new
        {
            buildId = BuildId,
            unixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            diagnosticsEnabled = health?.Enabled == true,
            current = new
            {
                current.MainThreadDelayMs,
                current.CpuPercent,
                current.WorkingSetMb,
                current.ManagedHeapMb,
                Players = api.World.AllOnlinePlayers?.Length ?? 0,
                current.LoadedEntities,
                current.LoadedChunks,
                current.BlockLoggerQueue,
                current.BlockLoggerWriterMs
            },
            blockLogger = new
            {
                ready = storage?.IsReady == true,
                pending = storage?.PendingEvents ?? 0,
                dropped = storage?.DroppedEvents ?? 0,
                activeContainerSessions = module.ActiveContainerSessions,
                containerSessionLimit = module.Config.MaxTrackedContainerSessions,
                containerSessionsEvicted = module.ContainerSessionsEvicted,
                containerSessionsWithExpiredInventory = module.ContainerSessionsWithExpiredInventory
            },
            animalLimiter = new
            {
                available = limiter != null,
                enabled = limiter?.Config.Enabled == true,
                trackedEntities = limiter?.HealthTrackedEntityCount ?? 0,
                trackedRegions = limiter?.HealthTrackedRegionCount ?? 0
            },
            loginProtection = new
            {
                available = login != null,
                enabled = login?.Config.Enabled == true,
                activeProtected = login?.ActiveProtectedCount ?? 0
            },
            operations = new { activeAlerts, criticalAlerts, openActions },
            web = new { running = IsRunning, activeRequests = Volatile.Read(ref webActiveRequests) }
        };
    }

    private object BuildCollectionAuditSnapshot()
    {
        var measured = SnapshotOverhead();
        var web = BuildWebDiagnosticsSnapshot();
        var collectors = new object[]
        {
            new { name = "Server health core sampler", cadence = "1 second", thread = "Main thread callback", cost = "Low", automatic = true, cached = true, detail = "Scheduler delay, process CPU, memory/GC counters, online player count, loaded-entity dictionary Count, BlockLogger queue/writer state." },
            new { name = "Loaded chunk count", cadence = "30 seconds", thread = "Main thread callback", cost = "Moderate", automatic = true, cached = true, detail = "LoadedChunkIndices is intentionally sampled only every 30 seconds and the last value is reused between samples." },
            new { name = "Smart Diagnosis", cadence = "On snapshot request, cached for 5 seconds", thread = "Main thread / in-memory", cost = "Low-Moderate", automatic = true, cached = true, detail = "Median/MAD, incident grouping, trend and confidence calculations over bounded in-memory history. No world/entity walk." },
            new { name = "Operations maintenance", cadence = "60 seconds", thread = "Main thread callback", cost = "Low", automatic = true, cached = true, detail = "Updates bounded alerts/actions, player-load buckets, world-save/network observations and retention." },
            new { name = "Player-count analytics", cadence = "Join/leave events + 60 second reconciliation", thread = "Main thread callback", cost = "Very Low", automatic = true, cached = true, detail = "Tracks only player UIDs/count transitions and time-weighted daily/hourly buckets. No entity/world scan and independent of Profiler diagnostics." },
            new { name = "Reports persistence", cadence = "On report changes + dirty flush up to every 60 seconds/world save", thread = "Main thread callback", cost = "Very Low", automatic = true, cached = true, detail = "Serializes only the bounded per-world report queue when dirty. No world/entity scan; new-report notifications iterate only currently online players." },
            new { name = "Announcement scheduler", cadence = "10 seconds", thread = "Main thread callback", cost = "Very Low", automatic = true, cached = true, detail = "Checks only the bounded announcement-rule list for due timestamps. No world/entity scan; messages are sent only when a rule becomes due." },
            new { name = "Web console process sampler", cadence = "2 seconds", thread = "Main thread callback", cost = "Very Low", automatic = true, cached = true, detail = "Samples current-process CPU/working-set and online-player count. Log text is read only when the Web Console page requests it." },
            new { name = "Mod update checker", cadence = "Delayed startup + every 6 hours, or manual", thread = "Background HTTP", cost = "Very Low local CPU / external request", automatic = true, cached = true, detail = "Sends installed non-core mod identifiers/versions and the target game version to the official Vintage Story Mod DB compatibility endpoint. It never downloads or replaces mod files." },
            new { name = "Player admin tick", cadence = "250 ms listener; historical expiry/AFK work throttled to 1 second", thread = "Main thread callback", cost = "Low", automatic = true, cached = true, detail = "250 ms cadence is retained only for frozen-player enforcement and shutdown timing. Known-player expiry and movement/AFK sampling run at 1 Hz." },
            new { name = "BlockLogger retention queue", cadence = "1 hour", thread = "Main thread trigger / background storage", cost = "Very Low", automatic = true, cached = true, detail = "Queues retention work hourly; it does not perform a world scan." },
            new { name = "Container session cleanup", cadence = "30 seconds", thread = "Main thread callback", cost = "Very Low", automatic = true, cached = true, detail = "Prunes expired in-memory container tracking sessions only." },
            new { name = "WebDashboard v2 live snapshot", cadence = "Browser requests every 3 seconds while Live is ON", thread = "Main thread read of cached state", cost = "Very Low", automatic = false, cached = true, detail = "Reads the newest health sample and counters only; it does not build the full dashboard or walk entities/chunks." },
            new { name = "Animal population scan", cadence = "Manual only", thread = "Incremental across server ticks", cost = "Bounded", automatic = false, cached = false, detail = "Processes with a per-tick time budget and exposes progress through a lightweight status endpoint." },
            new { name = "Entity/chunk hotspot scan", cadence = "Manual only", thread = "Main thread", cost = "High", automatic = false, cached = true, detail = "Walks loaded entities only after an explicit administrator request. Protected by cooldown/exclusive-operation policy." },
            new { name = "Harmony compatibility deep scan", cadence = "Manual only", thread = "Main thread reflection", cost = "Moderate", automatic = false, cached = false, detail = "Inspects patched methods/owners only when Run Deep Scan is pressed; ordinary Compatibility Center loading does not run it." },
            new { name = "BlockLogger activity hotspot query", cadence = "Manual / support export", thread = "Background SQLite", cost = "Moderate", automatic = false, cached = false, detail = "Bounded recent-coordinate aggregation. Activity evidence only, not CPU timing." },
            new { name = "Bounded Vintage Story engine profile", cadence = "Manual 10s/30s; optional 3s major-stall burst", thread = "Engine profiler", cost = "High / diagnostic / time-limited", automatic = true, cached = false, detail = "Manual profiles provide full diagnosis. Automatic attribution is OFF by default because it adds per-tick overhead. When opted in, it is limited to unexplained 750ms+ stalls, a 3-second capture, a 300-second cooldown, and is disabled by Safe Diagnostics Mode." }
        };

        return new
        {
            version = 2,
            generatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            collectors,
            measuredSuiteOverhead = measured,
            webDiagnostics = web,
            safeguards = new[]
            {
                "No automatic full loaded-entity walk.",
                "No automatic Animal Limiter population scan.",
                "Loaded chunk count is throttled and cached.",
                "Smart Diagnosis is computed from bounded in-memory samples and cached for 5 seconds.",
                "Heavy web routes use cooldowns and/or the exclusive heavy-operation gate.",
                "Feature circuit breakers isolate repeatedly failing nonessential diagnostics."
            }
        };
    }

    private object BuildCompatibilitySnapshot(bool deep)
    {
        var mods = api.ModLoader.Mods
            .Where(x => x?.Info != null)
            .Select(x => new
            {
                modId = x.Info.ModID ?? "",
                name = x.Info.Name ?? "",
                version = x.Info.Version ?? "",
                fileName = Path.GetFileName(x.FileName ?? "")
            })
            .OrderBy(x => x.modId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool Has(string id) => mods.Any(x => string.Equals(x.modId, id, StringComparison.OrdinalIgnoreCase));
        string VersionOf(string id) => mods.FirstOrDefault(x => string.Equals(x.modId, id, StringComparison.OrdinalIgnoreCase))?.version ?? "";

        bool th3 = Has("th3essentials");
        bool stratum = RuntimeCompatibility.IsStratum;
        bool synergy = Has("synergy");
        bool tungsten = Has("tungsten");
        bool legacyBlockLogger = Has("blocklogger") || Has("newfiesblockloggingsuite") || Has("newfiesblocklogger");

        var checks = new List<object>();
        int critical = 0;
        int watch = 0;
        int ignoredCount = 0;
        compatibilityIgnores.Ignores ??= new List<CompatibilityIgnoreRecord>();
        void Add(string severity, string category, string title, string detail, string action = "")
        {
            string fingerprint = CompatibilityFingerprint(category, title, detail);
            var ignore = compatibilityIgnores.Ignores.FirstOrDefault(x => string.Equals(x.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
            bool ignored = ignore != null && !string.Equals(severity, "OK", StringComparison.OrdinalIgnoreCase) && !string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase);
            if (ignored) ignoredCount++;
            if (!ignored && string.Equals(severity, "Critical", StringComparison.OrdinalIgnoreCase)) critical++;
            if (!ignored && string.Equals(severity, "Watch", StringComparison.OrdinalIgnoreCase)) watch++;
            checks.Add(new { severity, category, title, detail, action, fingerprint, ignored, ignoredBy = ignore?.IgnoredBy ?? "", ignoredReason = ignore?.Reason ?? "", ignoredUnixMilliseconds = ignore?.IgnoredUnixMilliseconds ?? 0 });
        }

        Add(
            legacyBlockLogger ? "Critical" : "OK",
            "Suite",
            "Legacy Server Essentials / BlockLogger duplicate",
            legacyBlockLogger
                ? "A legacy BlockLogger/Newfies logging mod is loaded alongside the current suite. This can duplicate commands, writers and web listeners."
                : "No legacy BlockLogger/Newfies logging mod ID is loaded alongside the current suite.",
            legacyBlockLogger ? "Remove the legacy ZIP and keep only the current Newfies Server Essentials Suite build." : "");

        if (th3)
        {
            Add(
                "OK",
                "Commands",
                "Th3Essentials command ownership",
                $"Th3Essentials {VersionOf("th3essentials")} detected. Root /restart, /shutdown, /back, /spawn, /msg, /r, /starterkit, /setstarterkit and starter-kit reset aliases are left to Th3Essentials. NSE fallbacks remain under /nse; warp aliases use independent safe ownership checks, and the Player Commands page independently selects the Starter Kit provider.",
                "No action required unless Th3Essentials reports its own startup error.");
        }
        else
        {
            Add(
                "Info",
                "Commands",
                "Th3Essentials not detected",
                "Newfies Server Essentials may own its normal root shutdown/back/spawn/msg/reply/starter-kit aliases because Th3Essentials is absent. Root /restart is additionally delegated when Stratum or another provider owns it.");
        }

        if (stratum)
        {
            Add(
                "OK",
                "Runtime",
                "Stratum server runtime",
                $"Stratum detected via {RuntimeCompatibility.StratumDetection}. NSE intentionally does not register the root /restart alias so Stratum keeps command ownership. /nse restart remains available.",
                "No action required. Keep only one root /restart provider.");
        }

        if (synergy)
        {
            string sv = VersionOf("synergy");
            bool knownValidationGap = sv == "1.1.25" && TargetGameVersion == "1.22.7";
            Add(
                knownValidationGap ? "Watch" : "OK",
                "Optimizer",
                $"Synergy {sv}",
                knownValidationGap
                    ? "Synergy 1.1.25 is installed on the suite's Vintage Story 1.22.7 target. Its public validation window was through 1.22.6, so treat this as a compatibility watch rather than a proven problem."
                    : "Synergy detected. Server Essentials controls it only through its public command interface and does not embed its implementation.",
                knownValidationGap ? "Use measured tests and the Harmony deep scan if behavior changes after updates." : "");
        }

        if (tungsten)
        {
            Add(
                "OK",
                "Optimizer",
                $"Tungsten {VersionOf("tungsten")}",
                "Tungsten detected. Server Essentials uses its documented command interface and measured before/after experiments.");
        }

        if (synergy && tungsten)
        {
            Add(
                "Info",
                "Optimizer",
                "Multiple optimization providers",
                "Synergy and Tungsten are both loaded. Their coexistence is not automatically a conflict; use the deep Harmony scan and measured tests instead of assuming overlap.");
        }

        string gameVersion = VersionOf("game");
        Add(
            string.Equals(gameVersion, TargetGameVersion, StringComparison.OrdinalIgnoreCase) ? "OK" : "Watch",
            "Game",
            "Vintage Story target",
            $"Loaded game mod version is {gameVersion}; this suite build targets {TargetGameVersion}.",
            string.Equals(gameVersion, TargetGameVersion, StringComparison.OrdinalIgnoreCase) ? "" : "Rebuild/retest the suite against the loaded game version before relying on unsupported APIs.");

        object harmony = deep
            ? BuildPatchConflictSnapshot()
            : new
            {
                available = false,
                skipped = true,
                conflictCount = 0,
                conflicts = Array.Empty<object>(),
                owners = Array.Empty<object>(),
                message = "Deep Harmony inspection is manual-only. Press Run Deep Scan to inspect patch ownership overlaps."
            };

        var reportOwner = module.Reports?.RootCommandOwner ?? "Unknown";
        bool reportSuiteOwner = module.Reports?.RootCommandRegisteredBySuite == true;
        var playerCommandsSnapshot = module.PlayerCommands?.GetSnapshot();
        PlayerCommandStatus? PlayerCommand(string key) => playerCommandsSnapshot?.Commands?.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        string PlayerCommandOwner(string key) => PlayerCommand(key)?.RootProvider ?? (module.PlayerCommands == null ? "Player Commands feature disabled" : "Unregistered");
        string smiteOwner = api.ChatCommands.Get("smite") != null ? "Newfies Server Essentials / existing provider" : "Unregistered";
        string afkOwner = NewfiesAfkModSystem.Current?.RootAfkCommandRegistered == true
            ? "Newfies Server Essentials"
            : api.ChatCommands.Get("afk") != null
                ? "Another registered provider"
                : (NewfiesAfkModSystem.Current == null ? "AFK feature disabled" : "Unregistered");
        var commandOwnership = new[]
        {
            new { command = "/restart", owner = th3 ? "Th3Essentials" : stratum ? "Stratum" : "Newfies Server Essentials / existing provider", suiteFallback = "/nse restart", status = "OK" },
            new { command = "/restarttime", owner = module.AutomatedRestarts?.GetSnapshot().RootRestartTimeOwner ?? (th3 ? "Th3Essentials" : "Unregistered"), suiteFallback = "/nse restarttime", status = "OK" },
            new { command = "/shutdown", owner = th3 ? "Th3Essentials" : "Newfies Server Essentials", suiteFallback = "/nse shutdown", status = "OK" },
            new { command = "/afk", owner = afkOwner, suiteFallback = "/nse afk", status = NewfiesAfkModSystem.Current == null ? "Info" : "OK" },
            new { command = "/afkinfo", owner = api.ChatCommands.Get("afkinfo") != null ? "Newfies Server Essentials / existing provider" : "Unregistered", suiteFallback = "/nse afkinfo", status = "OK" },
            new { command = "/back", owner = PlayerCommandOwner("back"), suiteFallback = "/nse back", status = PlayerCommand("back") == null ? "Info" : "OK" },
            new { command = "/home", owner = PlayerCommandOwner("home"), suiteFallback = "/nse home", status = PlayerCommand("home") == null ? "Info" : "OK" },
            new { command = "/sethome", owner = PlayerCommandOwner("sethome"), suiteFallback = "/nse sethome", status = PlayerCommand("sethome") == null ? "Info" : "OK" },
            new { command = "/delhome", owner = PlayerCommandOwner("delhome"), suiteFallback = "/nse delhome", status = PlayerCommand("delhome") == null ? "Info" : "OK" },
            new { command = "/homes", owner = PlayerCommandOwner("homes"), suiteFallback = "/nse homes", status = PlayerCommand("homes") == null ? "Info" : "OK" },
            new { command = "/t2p", owner = PlayerCommandOwner("t2p"), suiteFallback = "/nse t2p", status = PlayerCommand("t2p") == null ? "Info" : "OK" },
            new { command = "/t2m", owner = PlayerCommandOwner("t2m"), suiteFallback = "/nse t2m", status = PlayerCommand("t2m") == null ? "Info" : "OK" },
            new { command = "/tpaccept", owner = api.ChatCommands.Get("tpaccept") != null ? "Newfies Server Essentials / existing provider" : "Unregistered", suiteFallback = "/nse tpaccept", status = api.ChatCommands.Get("tpaccept") == null ? "Info" : "OK" },
            new { command = "/rtp", owner = PlayerCommandOwner("rtp"), suiteFallback = "/nse rtp", status = PlayerCommand("rtp") == null ? "Info" : "OK" },
            new { command = "/spawn", owner = th3 ? "Th3Essentials" : "Newfies Server Essentials / existing provider", suiteFallback = "/nse spawn", status = "OK" },
            new { command = "/msg", owner = th3 ? "Th3Essentials" : "Newfies Server Essentials / existing provider", suiteFallback = "/nse msg", status = "OK" },
            new { command = "/r", owner = th3 ? "Th3Essentials" : "Newfies Server Essentials / existing provider", suiteFallback = "/nse r", status = "OK" },
            new { command = "/warp", owner = PlayerCommandOwner("warp"), suiteFallback = "/nse warp", status = PlayerCommand("warp") == null ? "Info" : "OK" },
            new { command = "/smite", owner = smiteOwner, suiteFallback = "/nse smite", status = api.ChatCommands.Get("smite") != null ? "OK" : "Info" },
            new { command = "/setjail", owner = api.ChatCommands.Get("setjail") != null ? "Newfies Server Essentials / existing provider" : "Unregistered", suiteFallback = "/nse setjail", status = api.ChatCommands.Get("setjail") != null ? "OK" : "Info" },
            new { command = "/jail", owner = api.ChatCommands.Get("jail") != null ? "Newfies Server Essentials / existing provider" : "Unregistered", suiteFallback = "/nse jail", status = api.ChatCommands.Get("jail") != null ? "OK" : "Info" },
            new { command = "/unjail", owner = api.ChatCommands.Get("unjail") != null ? "Newfies Server Essentials / existing provider" : "Unregistered", suiteFallback = "/nse unjail", status = api.ChatCommands.Get("unjail") != null ? "OK" : "Info" },
            new { command = "/jailinfo", owner = api.ChatCommands.Get("jailinfo") != null ? "Newfies Server Essentials / existing provider" : "Unregistered", suiteFallback = "/nse jailinfo", status = api.ChatCommands.Get("jailinfo") != null ? "OK" : "Info" },
            new { command = "/report", owner = reportSuiteOwner ? "Newfies Server Essentials" : reportOwner, suiteFallback = "/nse report", status = "OK" },
            new { command = "/nse", owner = "Newfies Server Essentials", suiteFallback = "", status = "OK" },
            new { command = "/loginprot", owner = "Newfies Login Protection", suiteFallback = "Web Login Protection page", status = "OK" },
            new { command = "/synergy", owner = synergy ? "Synergy" : "Not loaded", suiteFallback = "Optimizations page", status = synergy ? "OK" : "Info" },
            new { command = "/tungsten", owner = tungsten ? "Tungsten" : "Not loaded", suiteFallback = "Optimizations page", status = tungsten ? "OK" : "Info" }
        };

        return new
        {
            generatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            buildId = BuildId,
            targetGameVersion = TargetGameVersion,
            loadedModCount = mods.Count,
            summary = critical > 0 ? "Conflict" : watch > 0 ? "Watch" : "Healthy",
            criticalCount = critical,
            watchCount = watch,
            ignoredCount,
            checks,
            commandOwnership,
            harmony,
            mods = mods.Take(500).ToList(),
            deepScan = deep
        };
    }

    private object BuildLoginProtectionConfigSnapshot()
    {
        var login = LoginProtectionSystem.Current;
        if (login == null)
            return new { available = false };

        var c = login.Config;
        return new
        {
            available = true,
            activeProtected = login.ActiveProtectedCount,
            pendingProtected = login.PendingProtectedCount,
            config = new
            {
                c.Enabled,
                c.ProtectionSeconds,
                c.ProtectOnJoin,
                c.ProtectOnRespawn,
                c.AnnounceOnJoin,
                c.AnnounceOnEnd,
                c.BlockPvP,
                c.CancelOnAttack,
                c.ProtectFallDamage,
                c.ExtinguishFire,
                c.RecoverSwimmingOnLogin
            }
        };
    }

    private LoginProtectionConfigUpdateResult ApplyLoginProtectionConfig(string body)
    {
        var login = LoginProtectionSystem.Current;
        if (login == null)
            return new LoginProtectionConfigUpdateResult(false, "Login Protection is not available.");

        try
        {
            var c = login.Config;
            bool enabled = GetJsonBool(body, "enabled", c.Enabled);
            if (enabled != c.Enabled)
                login.SetBooleanSettingFromGui("enabled", enabled);

            c.ProtectionSeconds = Math.Clamp(GetJsonInt(body, "protectionSeconds", c.ProtectionSeconds), 1, 3600);
            c.ProtectOnJoin = GetJsonBool(body, "protectOnJoin", c.ProtectOnJoin);
            c.ProtectOnRespawn = GetJsonBool(body, "protectOnRespawn", c.ProtectOnRespawn);
            c.AnnounceOnJoin = GetJsonBool(body, "announceOnJoin", c.AnnounceOnJoin);
            c.AnnounceOnEnd = GetJsonBool(body, "announceOnEnd", c.AnnounceOnEnd);
            c.BlockPvP = GetJsonBool(body, "blockPvP", c.BlockPvP);
            c.CancelOnAttack = GetJsonBool(body, "cancelOnAttack", c.CancelOnAttack);
            c.ProtectFallDamage = GetJsonBool(body, "protectFallDamage", c.ProtectFallDamage);
            c.ExtinguishFire = GetJsonBool(body, "extinguishFire", c.ExtinguishFire);
            c.RecoverSwimmingOnLogin = GetJsonBool(body, "recoverSwimmingOnLogin", c.RecoverSwimmingOnLogin);
            c.Clamp();
            login.SaveConfig();
            return new LoginProtectionConfigUpdateResult(true, "Login Protection configuration saved.");
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[WebDashboard] Login Protection config update failed safely: {0}: {1}", ex.GetType().Name, ex.Message);
            return new LoginProtectionConfigUpdateResult(false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private object BuildOptimizationProviders()
    {
        var mods = api.ModLoader.Mods
            .Where(x => x?.Info != null)
            .ToList();

        object Provider(
            string id,
            string name,
            bool installed,
            string version,
            string role,
            string detail) =>
            new
            {
                id,
                name,
                installed,
                version,
                role,
                detail
            };

        var tungsten = mods.FirstOrDefault(x =>
            string.Equals(
                x.Info.ModID,
                "tungsten",
                StringComparison.OrdinalIgnoreCase));

        var synergy = mods.FirstOrDefault(x =>
            string.Equals(
                x.Info.ModID,
                "synergy",
                StringComparison.OrdinalIgnoreCase));

        return new
        {
            providers = new[]
            {
                Provider(
                    "newfies",
                    "Newfies Server Essentials Suite",
                    true,
                    SuiteRelease.Version,
                    "Administration / diagnostics / advisor / safety / measurement",
                    "Owns BlockLogger, Smart Diagnosis, optimization recommendations, before/after measurements, and performance web UI."),
                Provider(
                    "tungsten",
                    "Tungsten",
                    tungsten != null,
                    tungsten?.Info?.Version ?? "",
                    "Optional optimization engine",
                    tungsten == null
                        ? "Not installed. The 27 Tungsten optimization toggles remain disabled."
                        : "Detected. The Optimizations page can control its documented /tungsten commands."),
                Provider(
                    "synergy",
                    "Synergy",
                    synergy != null,
                    synergy?.Info?.Version ?? "",
                    "External optimizer / compatibility signal",
                    synergy == null
                        ? "Not installed."
                        : "Detected. The Optimizations page can read status and control Synergy through its documented /synergy commands, with Server Essentials recommendations and measured before/after tests.")
            }
        };
    }

    private object BuildPatchConflictSnapshot()
    {
        if (IsFeatureCircuitOpen(
                "harmony-inspector",
                out string circuitMessage))
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
                message = circuitMessage
            };
        }

        var harmonyAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(assembly =>
                assembly.GetType("HarmonyLib.Harmony", false) != null);

        if (harmonyAssembly == null)
        {
            RecordFeatureSuccess(
                "harmony-inspector",
                "Harmony Patch Inspector",
                "Moderate");

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
            Type? harmonyType =
                harmonyAssembly.GetType("HarmonyLib.Harmony", false);

            MethodInfo? getAllPatched =
                harmonyType?.GetMethod(
                    "GetAllPatchedMethods",
                    BindingFlags.Public | BindingFlags.Static);

            MethodInfo? getPatchInfo =
                harmonyType?.GetMethod(
                    "GetPatchInfo",
                    BindingFlags.Public | BindingFlags.Static);

            if (getAllPatched == null || getPatchInfo == null)
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

            var patchedMethods =
                (getAllPatched.Invoke(null, null) as IEnumerable)
                ?.Cast<object>()
                .OfType<MethodBase>()
                .ToList()
                ?? new List<MethodBase>();

            var conflictRows = new List<object>();
            var ownerCounts = new Dictionary<
                string,
                int>(
                StringComparer.OrdinalIgnoreCase);

            foreach (MethodBase method in patchedMethods.Take(5000))
            {
                object? patchInfo =
                    getPatchInfo.Invoke(
                        null,
                        new object?[] { method });

                if (patchInfo == null) continue;

                var ownersProperty =
                    patchInfo.GetType().GetProperty(
                        "Owners",
                        BindingFlags.Instance | BindingFlags.Public);

                var ownerValues =
                    (ownersProperty?.GetValue(patchInfo) as IEnumerable)
                    ?.Cast<object>()
                    .Select(x => x?.ToString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    ?? new List<string>();

                foreach (string owner in ownerValues)
                {
                    ownerCounts.TryGetValue(owner, out int count);
                    ownerCounts[owner] = count + 1;
                }

                if (ownerValues.Count < 2) continue;

                conflictRows.Add(
                    new
                    {
                        declaringType =
                            method.DeclaringType?.FullName ?? "",
                        method = method.Name,
                        signature = method.ToString() ?? method.Name,
                        ownerCount = ownerValues.Count,
                        owners = ownerValues,
                        severity = ownerValues.Count >= 3
                            ? "High"
                            : "Watch",
                        note = "Multiple Harmony owners patch this method. This is overlap evidence, not proof that the patches conflict."
                    });
            }

            var owners = ownerCounts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .Select(x => new
                {
                    owner = x.Key,
                    patchedMethods = x.Value
                })
                .ToList();

            RecordFeatureSuccess(
                "harmony-inspector",
                "Harmony Patch Inspector",
                "Moderate");

            return new
            {
                available = true,
                harmonyDetected = true,
                harmonyAssembly = harmonyAssembly.GetName().Name ?? "Harmony",
                patchedMethodCount = patchedMethods.Count,
                conflictCount = conflictRows.Count,
                conflicts = conflictRows.Take(200).ToList(),
                owners,
                message = "Harmony patch overlap inspection completed."
            };
        }
        catch (Exception ex)
        {
            RecordFeatureFailure(
                "harmony-inspector",
                ex,
                "Harmony Patch Inspector",
                "Moderate");

            return new
            {
                available = false,
                harmonyDetected = true,
                patchedMethodCount = 0,
                conflictCount = 0,
                conflicts = Array.Empty<object>(),
                owners = Array.Empty<object>(),
                message = $"Harmony patch inspection failed safely: {ex.Message}"
            };
        }
    }

    private object BuildTungstenStatus()
    {
        TryAutoEvaluateOptimizationExperiment();

        var mod = api.ModLoader.Mods.FirstOrDefault(x =>
            x?.Info != null &&
            string.Equals(
                x.Info.ModID,
                "tungsten",
                StringComparison.OrdinalIgnoreCase));

        IChatCommand? command = null;
        try
        {
            command = api.ChatCommands.Get("tungsten");
        }
        catch
        {
            // Older/failed Tungsten installs may not have registered commands.
        }

        var registered = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        if (command?.AllSubcommands != null)
        {
            foreach (var key in command.AllSubcommands.Keys)
                registered.Add(key);

            foreach (var sub in command.Subcommands ?? Enumerable.Empty<IChatCommand>())
                registered.Add(sub.Name);
        }

        ServerHealthSnapshot? health = null;
        try
        {
            health = module.ServerHealth?.GetSnapshot(120, 300);
        }
        catch
        {
            // Recommendations are advisory; never make Tungsten controls depend
            // on profiler snapshot availability.
        }

        var recommendations = BuildTungstenRecommendations(health);

        var optimizations = TungstenOptimizations.Select(x =>
        {
            recommendations.TryGetValue(
                x.Code,
                out TungstenOptimizationRecommendation? recommendation);

            return new
            {
                code = x.Code,
                name = x.Name,
                description = x.Description,
                category = x.Category,
                commandAvailable =
                    command != null &&
                    (registered.Count == 0 || registered.Contains(x.Code)),
                restartRecommended = true,
                recommendation = recommendation ?? new TungstenOptimizationRecommendation
                {
                    Code = x.Code,
                    Score = 0,
                    Level = health?.Enabled == true ? "Not indicated" : "Need diagnostics",
                    Reason = health?.Enabled == true
                        ? "Current Smart Diagnosis does not show a strong signal for this optimization."
                        : "Enable BlockLogger Profiler diagnostics for at least a minute to generate server-specific recommendations."
                }
            };
        }).ToList();

        return new
        {
            installed = mod != null,
            commandRegistered = command != null,
            version = mod?.Info?.Version ?? "",
            name = mod?.Info?.Name ?? "Tungsten",
            optimizationCount = optimizations.Count,
            optimizations,
            documentedVersion = "1.3.7",
            requiresTungsten = true,
            safeDiagnosticsMode = config.SafeDiagnosticsMode,
            installMessage = mod == null
                ? "This BlockLogger build does not copy Tungsten's optimization engine. Install the separate Tungsten mod to enable these controls."
                : "",
            recommendationState = health?.Enabled == true
                ? health.SmartAnalysis.Available
                    ? $"Smart Diagnosis active: {health.SmartAnalysis.State}, health {health.SmartAnalysis.HealthScore}/100."
                    : "Diagnostics are enabled but still learning the server baseline."
                : "Profiler diagnostics are OFF. Enable them for at least 60 seconds for server-specific optimization recommendations and before/after testing.",
            pendingExperiment = optimizationExperiments.Pending,
            experimentHistory = optimizationExperiments.History
                .OrderByDescending(x => x.CreatedUnixMilliseconds)
                .Take(10)
                .ToList(),
            note = mod == null
                ? "Tungsten is not installed. Install Tungsten separately to use these controls."
                : "Controls call Tungsten's public /tungsten command interface. Most Tungsten optimization changes require a server restart.",
            supports = new
            {
                status = command != null,
                stats = command != null,
                reload = command != null,
                health = command != null &&
                    (registered.Count == 0 || registered.Contains("health")),
                manifest = command != null &&
                    (registered.Count == 0 || registered.Contains("manifest")),
                diagnostics = command != null &&
                    (registered.Count == 0 || registered.Contains("diag")),
                benchmarkHarness = command != null &&
                    (registered.Count == 0 || registered.Contains("benchmarkharness")),
                frameProfiler = command != null &&
                    (registered.Count == 0 || registered.Contains("frameprofiler"))
            }
        };
    }

    private Dictionary<string, TungstenOptimizationRecommendation>
        BuildTungstenRecommendations(ServerHealthSnapshot? health)
    {
        var score = TungstenOptimizations.ToDictionary(
            x => x.Code,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        var reasons = TungstenOptimizations.ToDictionary(
            x => x.Code,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        void Boost(
            IEnumerable<string> codes,
            int points,
            string reason)
        {
            foreach (string code in codes)
            {
                if (!score.ContainsKey(code)) continue;
                score[code] = Math.Min(100, score[code] + points);
                if (!reasons[code].Contains(reason))
                    reasons[code].Add(reason);
            }
        }

        if (health?.Enabled != true || !health.SmartAnalysis.Available)
            return new Dictionary<string, TungstenOptimizationRecommendation>(
                StringComparer.OrdinalIgnoreCase);

        var smart = health.SmartAnalysis;

        foreach (var signal in smart.Signals)
        {
            string evidence = string.IsNullOrWhiteSpace(signal.Title)
                ? signal.Category
                : signal.Title;

            switch (signal.Category)
            {
                case "Entities":
                    Boost(
                        new[]
                        {
                            "entitylistreuse",
                            "entitysimulationoptimization",
                            "getentitiesaroundoptimization"
                        },
                        28,
                        $"Entity pressure detected: {evidence}");
                    Boost(
                        new[]
                        {
                            "entitydespawnpacketoptimization",
                            "bulkentityattributespacketoptimization"
                        },
                        15,
                        "Entity-related lag makes entity packet/despawn hot paths more relevant.");
                    break;

                case "Chunks":
                    Boost(
                        new[]
                        {
                            "chunkloadingoptimization",
                            "chunkunloadingoptimization"
                        },
                        35,
                        $"Chunk/world activity correlated with lag: {evidence}");
                    Boost(
                        new[]
                        {
                            "genterrazeroallocoptimization",
                            "genterrabitarrayoptimization",
                            "genterranativenoiseoptimization"
                        },
                        20,
                        "Chunk/world activity suggests world-generation optimizations may help during exploration.");
                    break;

                case "GC":
                    Boost(
                        new[]
                        {
                            "entitylistreuse",
                            "blocklistreuse",
                            "getdropslistreuse",
                            "eventmanagerlistreuse",
                            "physicsmanagerlistoptimization",
                            "physicsmanagermethodlistoptimization",
                            "getentitiesaroundoptimization"
                        },
                        24,
                        $"GC/allocation pressure detected: {evidence}");
                    Boost(
                        new[] { "genterrazeroallocoptimization" },
                        18,
                        "Zero-allocation worldgen is relevant when GC pressure rises during terrain generation.");
                    break;

                case "CPU":
                    Boost(
                        new[]
                        {
                            "servermainlinqoptimization",
                            "getplayersaroundoptimization",
                            "getentitiesaroundoptimization"
                        },
                        24,
                        $"CPU saturation correlated with lag: {evidence}");
                    Boost(
                        new[]
                        {
                            "broadcastlinqoptimization",
                            "recipebaselinqoptimization",
                            "wildcardfastmatchoptimization"
                        },
                        12,
                        "These reduce CPU/allocation overhead in frequently-called server paths.");
                    break;

                case "MainThread":
                    Boost(
                        new[]
                        {
                            "servermainlinqoptimization",
                            "eventmanagerlistreuse",
                            "getentitiesaroundoptimization"
                        },
                        16,
                        $"Main-thread scheduling is falling behind: {evidence}");
                    break;

                case "Memory":
                    Boost(
                        new[]
                        {
                            "entitylistreuse",
                            "blocklistreuse",
                            "getdropslistreuse",
                            "eventmanagerlistreuse",
                            "physicsmanagerlistoptimization",
                            "physicsmanagermethodlistoptimization",
                            "genterrazeroallocoptimization"
                        },
                        22,
                        $"Memory/heap growth detected: {evidence}");
                    break;
            }
        }

        if (smart.RecentEntityMedian >= 12_000)
        {
            Boost(
                new[]
                {
                    "entitylistreuse",
                    "entitysimulationoptimization",
                    "getentitiesaroundoptimization"
                },
                22,
                $"Loaded entity median is high ({smart.RecentEntityMedian:N0}).");
        }

        if (health.Current.Players >= 10)
        {
            Boost(
                new[]
                {
                    "getplayersaroundoptimization",
                    "servermainlinqoptimization",
                    "broadcastlinqoptimization",
                    "bulkentityattributespacketoptimization"
                },
                24,
                $"There are {health.Current.Players} players online; player-query/broadcast optimizations become more valuable at higher concurrency.");
        }
        else if (health.Current.Players >= 5)
        {
            Boost(
                new[]
                {
                    "getplayersaroundoptimization",
                    "servermainlinqoptimization",
                    "broadcastlinqoptimization"
                },
                12,
                $"There are {health.Current.Players} players online; player-query overhead is becoming relevant.");
        }

        var result = new Dictionary<string, TungstenOptimizationRecommendation>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in score)
        {
            int value = entry.Value;
            if (value <= 0) continue;

            result[entry.Key] = new TungstenOptimizationRecommendation
            {
                Code = entry.Key,
                Score = value,
                Level = value >= 70
                    ? "High"
                    : value >= 45
                        ? "Medium"
                        : "Low",
                Reason = string.Join(
                    " ",
                    reasons[entry.Key].Take(3))
            };
        }

        return result;
    }

    private object BuildSynergyStatus(TungstenCommandResult statusResult)
    {
        TryAutoEvaluateOptimizationExperiment();

        var mod = api.ModLoader.Mods.FirstOrDefault(x =>
            x?.Info != null &&
            string.Equals(
                x.Info.ModID,
                "synergy",
                StringComparison.OrdinalIgnoreCase));

        IChatCommand? command = null;
        try
        {
            command = api.ChatCommands.Get("synergy");
        }
        catch
        {
            // Failed/unsupported Synergy versions may not register the command.
        }

        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (command?.AllSubcommands != null)
        {
            foreach (var key in command.AllSubcommands.Keys)
                registered.Add(key);
            foreach (var sub in command.Subcommands ?? Enumerable.Empty<IChatCommand>())
                registered.Add(sub.Name);
        }

        ServerHealthSnapshot? health = null;
        try
        {
            health = module.ServerHealth?.GetSnapshot(120, 300);
        }
        catch
        {
            // Recommendations are advisory only.
        }

        var recommendations = BuildSynergyRecommendations(health);
        string statusText = statusResult.Success ? statusResult.Message : "";

        var optimizations = SynergyOptimizations.Select(x =>
        {
            recommendations.TryGetValue(
                x.Code,
                out TungstenOptimizationRecommendation? recommendation);

            bool? enabled = ParseSynergyEnabled(statusText, x.Code);
            return new
            {
                code = x.Code,
                name = x.Name,
                description = x.Description,
                category = x.Category,
                scope = x.Scope,
                caution = x.Caution,
                enabled,
                commandAvailable =
                    command != null &&
                    (registered.Count == 0 || registered.Contains(x.Code)),
                restartRecommended = true,
                recommendation = recommendation ?? new TungstenOptimizationRecommendation
                {
                    Code = x.Code,
                    Score = 0,
                    Level = health?.Enabled == true ? "Not indicated" : "Need diagnostics",
                    Reason = health?.Enabled == true
                        ? "Current Smart Diagnosis does not show a strong server-side signal for this optimization."
                        : "Enable Server Essentials diagnostics for at least a minute to generate workload-specific recommendations."
                }
            };
        }).ToList();

        string version = mod?.Info?.Version ?? "";
        bool knownCompatibilityCaution =
            mod != null &&
            string.Equals(TargetGameVersion, "1.22.7", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(version, "1.1.25", StringComparison.OrdinalIgnoreCase);

        return new
        {
            installed = mod != null,
            commandRegistered = command != null,
            statusCommandSucceeded = statusResult.Success,
            version,
            name = mod?.Info?.Name ?? "Synergy",
            optimizationCount = optimizations.Count,
            optimizations,
            documentedVersion = "1.1.25",
            safeDiagnosticsMode = config.SafeDiagnosticsMode,
            recommendationState = health?.Enabled == true
                ? health.SmartAnalysis.Available
                    ? $"Smart Diagnosis active: {health.SmartAnalysis.State}, health {health.SmartAnalysis.HealthScore}/100."
                    : "Diagnostics are enabled but still learning the server baseline."
                : "Profiler diagnostics are OFF. Enable them for at least 60 seconds for workload-specific recommendations and measured tests.",
            pendingExperiment = optimizationExperiments.Pending,
            experimentHistory = optimizationExperiments.History
                .OrderByDescending(x => x.CreatedUnixMilliseconds)
                .Take(10)
                .ToList(),
            note = mod == null
                ? "Synergy is not installed. Install it separately to enable these controls."
                : "Controls call Synergy's public /synergy command interface. Configuration changes require a server restart.",
            compatibilityWarning = knownCompatibilityCaution
                ? "Synergy 1.1.25 publicly documents validation for Vintage Story 1.22.5-1.22.6. This server targets 1.22.7, so watch Synergy startup logs/circuit-breaker output for skipped patches."
                : "",
            statusText = statusResult.Message,
            statusError = statusResult.Success ? "" : statusResult.Message,
            supports = new
            {
                status = command != null,
                stats = command != null && (registered.Count == 0 || registered.Contains("stats")),
                reload = command != null && (registered.Count == 0 || registered.Contains("reload")),
                diagnostics = command != null && (registered.Count == 0 || registered.Contains("diag")),
                all = command != null && (registered.Count == 0 || registered.Contains("all"))
            }
        };
    }

    private Dictionary<string, TungstenOptimizationRecommendation>
        BuildSynergyRecommendations(ServerHealthSnapshot? health)
    {
        var score = SynergyOptimizations.ToDictionary(
            x => x.Code,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        var reasons = SynergyOptimizations.ToDictionary(
            x => x.Code,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        void Boost(IEnumerable<string> codes, int amount, string reason)
        {
            foreach (string code in codes)
            {
                if (!score.ContainsKey(code)) continue;
                score[code] = Math.Min(100, score[code] + amount);
                if (!reasons[code].Contains(reason, StringComparer.OrdinalIgnoreCase))
                    reasons[code].Add(reason);
            }
        }

        if (health == null || !health.Enabled || !health.SmartAnalysis.Available)
            return new Dictionary<string, TungstenOptimizationRecommendation>(StringComparer.OrdinalIgnoreCase);

        var smart = health.SmartAnalysis;
        var current = health.Current;
        var summary = health.DiagnosticSummary;

        if (smart.RecentEntityMedian >= 10000)
        {
            Boost(new[] { "activationrange", "pathfindingthrottle", "aithrottle" }, 45,
                $"Loaded entity median is very high ({smart.RecentEntityMedian:N0}).");
            Boost(new[] { "collisionfastpath", "repulsethrottle", "pathfindingpool" }, 30,
                "High entity density increases physics, repulsion, and pathfinding work.");
        }
        else if (smart.RecentEntityMedian >= 4000)
        {
            Boost(new[] { "activationrange", "pathfindingthrottle", "aithrottle" }, 28,
                $"Loaded entity median is elevated ({smart.RecentEntityMedian:N0}).");
            Boost(new[] { "collisionfastpath", "repulsethrottle", "pathfindingpool" }, 18,
                "Entity-heavy workloads can benefit from physics/pathfinding allocation reductions.");
        }

        if (smart.RecentCpuMedianPercent >= 80)
        {
            Boost(new[] { "activationrange", "pathfindingthrottle", "aithrottle", "collisionfastpath" }, 28,
                $"Recent CPU median is high ({smart.RecentCpuMedianPercent:0.0}%).");
        }
        else if (smart.RecentCpuMedianPercent >= 60)
        {
            Boost(new[] { "activationrange", "pathfindingthrottle", "aithrottle" }, 14,
                $"Recent CPU median is elevated ({smart.RecentCpuMedianPercent:0.0}%).");
        }

        if (summary.Gen2Collections >= 2)
        {
            Boost(new[] { "pathfindingpool", "blocktickpool" }, 28,
                $"Recent diagnostics recorded {summary.Gen2Collections} Gen2 collections; allocation-reduction optimizations are more relevant.");
        }

        if (current.Players >= 20)
        {
            Boost(new[] { "networkflush", "inventoryscan", "sendfrequency", "attributedelta", "deltaencoding" }, 35,
                $"There are {current.Players} players online; network/inventory scaling optimizations are more valuable.");
            Boost(new[] { "spawnpriority", "hysteresis" }, 15,
                "Higher concurrency increases entity tracking and login/teleport churn.");
        }
        else if (current.Players >= 8)
        {
            Boost(new[] { "networkflush", "sendfrequency", "attributedelta", "deltaencoding" }, 20,
                $"There are {current.Players} players online; network overhead is becoming more relevant.");
            Boost(new[] { "inventoryscan" }, 10,
                "Inventory dirty scans become more relevant as connected player count grows.");
        }

        if (current.LoadedChunks >= 5000)
        {
            Boost(new[] { "blocktickpool" }, 22,
                $"Loaded chunk count is high ({current.LoadedChunks:N0}); random block-tick allocation pressure may be more significant.");
        }

        if (summary.WorstDelayMs >= 500 && current.LoadedEntities >= 2000)
        {
            Boost(new[] { "activationrange", "aithrottle", "pathfindingthrottle", "repulsethrottle" }, 18,
                $"Recent worst scheduler delay reached {summary.WorstDelayMs:0}ms while entity load is substantial.");
        }

        var result = new Dictionary<string, TungstenOptimizationRecommendation>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in score)
        {
            if (entry.Value <= 0) continue;
            int value = entry.Value;
            result[entry.Key] = new TungstenOptimizationRecommendation
            {
                Code = entry.Key,
                Score = value,
                Level = value >= 70 ? "High" : value >= 45 ? "Medium" : "Low",
                Reason = string.Join(" ", reasons[entry.Key].Take(3))
            };
        }
        return result;
    }

    private static bool? ParseSynergyEnabled(string statusText, string code)
    {
        if (string.IsNullOrWhiteSpace(statusText)) return null;
        foreach (string rawLine in statusText.Replace("\r", "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(code + ":", StringComparison.OrdinalIgnoreCase)) continue;
            string value = line[(code.Length + 1)..].Trim();
            if (value.StartsWith("ON", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.StartsWith("OFF", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
    }

    private OptimizationExperimentPreparation PrepareOptimizationExperiment(
        string providerId,
        string code,
        bool enabled)
    {
        if (optimizationExperiments.Pending != null)
        {
            return new OptimizationExperimentPreparation(
                false,
                $"A measured optimization experiment is already pending for {optimizationExperiments.Pending.OptimizationName}. Finish/evaluate it before starting another so the result can be attributed to one change.",
                null);
        }

        string providerName;
        string optimizationName;
        if (string.Equals(providerId, "synergy", StringComparison.OrdinalIgnoreCase))
        {
            var synergyInfo = SynergyOptimizations.FirstOrDefault(x => x.Code == code);
            if (synergyInfo == null)
                return new OptimizationExperimentPreparation(false, "Unknown Synergy optimization.", null);
            providerId = "synergy";
            providerName = "Synergy";
            optimizationName = synergyInfo.Name;
        }
        else
        {
            var tungstenInfo = TungstenOptimizations.FirstOrDefault(x => x.Code == code);
            if (tungstenInfo == null)
                return new OptimizationExperimentPreparation(false, "Unknown Tungsten optimization.", null);
            providerId = "tungsten";
            providerName = "Tungsten";
            optimizationName = tungstenInfo.Name;
        }

        var baseline = CaptureOptimizationMetrics(out string error);
        if (baseline == null)
        {
            return new OptimizationExperimentPreparation(
                false,
                error,
                null);
        }

        var experiment = new OptimizationExperiment
        {
            Id = Guid.NewGuid().ToString("N"),
            ProviderId = providerId,
            ProviderName = providerName,
            OptimizationCode = code,
            OptimizationName = optimizationName,
            TargetEnabled = enabled,
            CreatedUnixMilliseconds =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RuntimeId = runtimeId,
            Status = "AwaitingRestart",
            Baseline = baseline,
            Result = "",
            Confidence = "Pending",
            Reason =
                $"Baseline captured before changing {providerName}. Restart the server because optimizer toggles are applied at startup, then allow diagnostics to collect for at least 60 seconds."
        };

        return new OptimizationExperimentPreparation(
            true,
            "Baseline captured.",
            experiment);
    }

    private OptimizationMetricSnapshot? CaptureOptimizationMetrics(
        out string error)
    {
        error = "";
        var health = module.ServerHealth;
        if (health == null)
        {
            error = "Server diagnostics are unavailable.";
            return null;
        }

        var snapshot = health.GetSnapshot(120, 300);
        if (!snapshot.Enabled)
        {
            error =
                "Enable BlockLogger Profiler diagnostics before starting a measured optimization test. The expensive manual engine profile is not required.";
            return null;
        }

        if (!snapshot.SmartAnalysis.Available ||
            snapshot.Samples.Count < 60)
        {
            error =
                $"Diagnostics need at least 60 seconds of samples before a measured optimization test. Current graph samples: {snapshot.Samples.Count}.";
            return null;
        }

        return new OptimizationMetricSnapshot
        {
            CapturedUnixMilliseconds =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
        var pending = optimizationExperiments.Pending;
        if (pending == null ||
            string.Equals(
                pending.RuntimeId,
                runtimeId,
                StringComparison.Ordinal))
            return;

        var result = EvaluatePendingOptimizationExperiment(false);
        if (!result.Success &&
            optimizationExperiments.Pending != null &&
            optimizationExperiments.Pending.Status != "CollectingAfterRestart")
        {
            optimizationExperiments.Pending.Status = "CollectingAfterRestart";
            optimizationExperiments.Pending.Reason = result.Message;
            SaveOptimizationExperiments();
        }
    }

    private OptimizationExperimentEvaluationResult
        EvaluatePendingOptimizationExperiment(bool allowSameRuntime)
    {
        var pending = optimizationExperiments.Pending;
        if (pending == null)
        {
            return new OptimizationExperimentEvaluationResult(
                false,
                "No measured optimization experiment is pending.",
                null);
        }

        bool restarted = !string.Equals(
            pending.RuntimeId,
            runtimeId,
            StringComparison.Ordinal);

        if (!restarted && !allowSameRuntime)
        {
            pending.Status = "AwaitingRestart";
            pending.Reason =
                $"Restart the server before evaluating. {(string.IsNullOrWhiteSpace(pending.ProviderName) ? "The optimizer" : pending.ProviderName)} applies these optimization changes at startup.";
            SaveOptimizationExperiments();

            return new OptimizationExperimentEvaluationResult(
                false,
                pending.Reason,
                pending);
        }

        var after = CaptureOptimizationMetrics(out string error);
        if (after == null)
        {
            pending.Status = restarted
                ? "CollectingAfterRestart"
                : "CollectingAfterChange";
            pending.Reason = error;
            SaveOptimizationExperiments();

            return new OptimizationExperimentEvaluationResult(
                false,
                error,
                pending);
        }

        pending.After = after;

        double playerChange = PercentDifference(
            after.Players,
            pending.Baseline.Players);
        double entityChange = PercentDifference(
            after.EntityMedian,
            pending.Baseline.EntityMedian);

        double delayImprovement = LowerIsBetterPercent(
            pending.Baseline.DelayMedianMs,
            after.DelayMedianMs,
            5);
        double cpuImprovement = LowerIsBetterPercent(
            pending.Baseline.CpuMedianPercent,
            after.CpuMedianPercent,
            10);
        double writerImprovement = LowerIsBetterPercent(
            pending.Baseline.WriterMedianMs,
            after.WriterMedianMs,
            5);
        double queueImprovement = LowerIsBetterPercent(
            pending.Baseline.QueueMedian,
            after.QueueMedian,
            10);
        double gen2Improvement = LowerIsBetterPercent(
            pending.Baseline.Gen2Collections,
            after.Gen2Collections,
            1);
        double healthImprovement =
            after.HealthScore - pending.Baseline.HealthScore;

        double weighted =
            delayImprovement * 0.35 +
            cpuImprovement * 0.25 +
            writerImprovement * 0.15 +
            queueImprovement * 0.10 +
            gen2Improvement * 0.10 +
            healthImprovement * 0.05;

        weighted = Math.Clamp(weighted, -100, 100);
        pending.ImprovementScore = weighted;

        bool majorLoadDifference =
            Math.Abs(playerChange) > 35 ||
            Math.Abs(entityChange) > 35;

        bool moderateLoadDifference =
            Math.Abs(playerChange) > 20 ||
            Math.Abs(entityChange) > 20;

        pending.Confidence = majorLoadDifference
            ? "Low"
            : moderateLoadDifference
                ? "Medium"
                : "High";

        if (majorLoadDifference)
        {
            pending.Result = "Inconclusive";
        }
        else if (weighted >= 7)
        {
            pending.Result = "Improved";
        }
        else if (weighted <= -7)
        {
            pending.Result = "Regressed";
        }
        else
        {
            pending.Result = "No meaningful change";
        }

        pending.Status = "Completed";
        pending.Reason = string.Join(
            " | ",
            new[]
            {
                $"Delay {SignedPercent(delayImprovement)}",
                $"CPU {SignedPercent(cpuImprovement)}",
                $"Writer {SignedPercent(writerImprovement)}",
                $"Queue {SignedPercent(queueImprovement)}",
                $"Gen2 {SignedPercent(gen2Improvement)}",
                $"Health {healthImprovement:+0;-0;0} points",
                $"Player load {playerChange:+0;-0;0}%",
                $"Entity load {entityChange:+0;-0;0}%"
            });

        optimizationExperiments.History.Insert(0, pending);
        optimizationExperiments.Pending = null;
        SaveOptimizationExperiments();

        return new OptimizationExperimentEvaluationResult(
            true,
            $"{pending.OptimizationName}: {pending.Result} ({pending.ImprovementScore:+0.0;-0.0;0.0}% weighted score, {pending.Confidence} confidence).",
            pending);
    }

    private static double LowerIsBetterPercent(
        double before,
        double after,
        double floor)
    {
        double denominator = Math.Max(Math.Abs(before), floor);
        return Math.Clamp(
            (before - after) * 100d / denominator,
            -200,
            200);
    }

    private static double PercentDifference(
        double current,
        double baseline)
    {
        double denominator = Math.Max(Math.Abs(baseline), 1);
        return (current - baseline) * 100d / denominator;
    }

    private static string SignedPercent(double value) =>
        $"{value:+0.0;-0.0;0.0}%";

    private Task<TungstenCommandResult> ExecuteTungstenCommandAsync(
        string commandLine,
        string taskCode) =>
        ExecuteExternalOptimizerCommandAsync("tungsten", "Tungsten", commandLine, taskCode);

    private Task<TungstenCommandResult> ExecuteSynergyCommandAsync(
        string commandLine,
        string taskCode) =>
        ExecuteExternalOptimizerCommandAsync("synergy", "Synergy", commandLine, taskCode);

    private Task<TungstenCommandResult> ExecuteExternalOptimizerCommandAsync(
        string modId,
        string displayName,
        string commandLine,
        string taskCode)
    {
        var tcs = new TaskCompletionSource<TungstenCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        api.Event.EnqueueMainThreadTask(
            () =>
            {
                try
                {
                    var optimizerMod = api.ModLoader.Mods.FirstOrDefault(x =>
                        x?.Info != null &&
                        string.Equals(
                            x.Info.ModID,
                            modId,
                            StringComparison.OrdinalIgnoreCase));

                    if (optimizerMod == null)
                    {
                        tcs.TrySetResult(new TungstenCommandResult(
                            false,
                            "NotInstalled",
                            $"{displayName} is not installed.",
                            commandLine));
                        return;
                    }

                    var caller = new Caller
                    {
                        Type = EnumCallerType.Console,
                        CallerPrivileges = new[] { "*" },
                        CallerRole = "admin",
                        FromChatGroupId = 0
                    };

                    var callArgs = new TextCommandCallingArgs
                    {
                        Caller = caller,
                        LanguageCode = "en"
                    };

                    api.ChatCommands.ExecuteUnparsed(
                        commandLine,
                        callArgs,
                        result =>
                        {
                            try
                            {
                                string message = FormatCommandResult(result, "Tungsten command");
                                bool success =
                                    result != null &&
                                    (result.Status == EnumCommandStatus.Success ||
                                     result.Status == EnumCommandStatus.UnknownLegacy);

                                tcs.TrySetResult(new TungstenCommandResult(
                                    success,
                                    result?.Status.ToString() ?? "Unknown",
                                    message,
                                    commandLine));
                            }
                            catch (Exception ex)
                            {
                                tcs.TrySetResult(new TungstenCommandResult(
                                    false,
                                    "Error",
                                    ex.Message,
                                    commandLine));
                            }
                        });
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(new TungstenCommandResult(
                        false,
                        "Error",
                        ex.Message,
                        commandLine));
                }
            },
            taskCode);

        return AwaitTungstenCommandWithTimeoutAsync(tcs.Task, commandLine);
    }

    private static async Task<TungstenCommandResult> AwaitTungstenCommandWithTimeoutAsync(
        Task<TungstenCommandResult> commandTask,
        string commandLine)
    {
        Task completed = await Task.WhenAny(
            commandTask,
            Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);

        if (completed == commandTask)
            return await commandTask.ConfigureAwait(false);

        return new TungstenCommandResult(
            false,
            "Timeout",
            "Tungsten command did not complete within 8 seconds. Check the server log; the command may still have executed.",
            commandLine);
    }

    private static string FormatCommandResult(TextCommandResult? result, string commandLabel = "Command")
    {
        if (result == null) return "No command result was returned.";

        string message = result.StatusMessage ?? "";
        if (result.MessageParams != null && result.MessageParams.Length > 0)
        {
            try
            {
                message = string.Format(
                    CultureInfo.InvariantCulture,
                    message,
                    result.MessageParams);
            }
            catch
            {
                // Preserve the raw message if it is a localization key or has
                // formatting syntax that is not standard string.Format syntax.
            }
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            message = result.Status == EnumCommandStatus.Success
                ? $"{commandLabel} completed successfully. Some detailed output may be written to the server log."
                : $"{commandLabel} returned {result.Status}.";
        }

        return message;
    }

    private void CompleteWebRequest(WebRequestMetricContext metric, bool failed)
    {
        double totalMs = Math.Max(
            0,
            (Stopwatch.GetTimestamp() - metric.StartedTimestamp) /
            (double)Stopwatch.Frequency * 1000d);

        int status;
        long responseBytes;
        double queueWait;
        double mainWait;
        double mainWork;
        lock (metric.Sync)
        {
            status = metric.StatusCode;
            responseBytes = metric.ResponseBytes;
            queueWait = metric.QueueWaitMilliseconds;
            mainWait = metric.MainThreadWaitMilliseconds;
            mainWork = metric.MainThreadWorkMilliseconds;
        }

        if (status <= 0)
            status = failed ? 500 : 200;

        SuiteTimingRegistry.Record("Web Dashboard request", totalMs);
        if (queueWait > 0) SuiteTimingRegistry.Record("Web Dashboard worker queue wait", queueWait);
        if (mainWait > 0) SuiteTimingRegistry.Record("Web Dashboard main-thread wait", mainWait);
        if (mainWork > 0) SuiteTimingRegistry.Record("Web Dashboard main-thread work", mainWork);

        var row = new WebRequestDiagnostic
        {
            UnixMilliseconds = metric.UnixMilliseconds,
            Method = metric.Method,
            Path = metric.Path,
            StatusCode = status,
            ResponseBytes = responseBytes,
            TotalMilliseconds = totalMs,
            QueueWaitMilliseconds = queueWait,
            MainThreadWaitMilliseconds = mainWait,
            MainThreadWorkMilliseconds = mainWork,
            Note = failed
                ? "Request handler threw before completing normally."
                : queueWait >= 100
                    ? "Request spent notable time waiting for an available web worker."
                    : mainWait >= 100
                    ? "Most request delay may be waiting for the Vintage Story main thread."
                    : mainWork >= 50
                        ? "Request performed notable work on the Vintage Story main thread."
                        : ""
        };

        lock (webMetricsLock)
        {
            webTotalRequests++;
            webTotalResponseBytes += responseBytes;
            if (status >= 500 || failed) webErrorResponses++;
            if (status == 401 || status == 403) webUnauthorizedResponses++;
            if (status == 429) webRateLimitedResponses++;

            string metricPath = metric.Path;
            string key = metric.Method + " " + metricPath;
            if (!webEndpointMetrics.TryGetValue(key, out var endpoint))
            {
                // Unknown/attacker-controlled paths must not create an unbounded dictionary
                // containing a 120-sample queue for every unique URL. Aggregate excess keys.
                if (webEndpointMetrics.Count >= MaxWebEndpointMetricKeys)
                {
                    metricPath = "<other>";
                    key = metric.Method + " " + metricPath;
                }

                if (!webEndpointMetrics.TryGetValue(key, out endpoint))
                {
                    endpoint = new WebEndpointMetricsState
                    {
                        Method = metric.Method,
                        Path = metricPath
                    };
                    webEndpointMetrics[key] = endpoint;
                }
            }

            endpoint.Requests++;
            endpoint.TotalMilliseconds += totalMs;
            endpoint.MaxMilliseconds = Math.Max(endpoint.MaxMilliseconds, totalMs);
            endpoint.ResponseBytes += responseBytes;
            endpoint.QueueWaitTotalMilliseconds += queueWait;
            endpoint.MainThreadWaitTotalMilliseconds += mainWait;
            endpoint.MainThreadWorkTotalMilliseconds += mainWork;
            if (status >= 400 || failed) endpoint.Errors++;
            endpoint.RecentDurations.Enqueue(totalMs);
            while (endpoint.RecentDurations.Count > 120)
                endpoint.RecentDurations.Dequeue();

            recentWebRequests.Enqueue(row);
            long cutoff = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
            while (recentWebRequests.Count > 0 && recentWebRequests.Peek().UnixMilliseconds < cutoff)
                recentWebRequests.Dequeue();
            while (recentWebRequests.Count > 500)
                recentWebRequests.Dequeue();
        }
    }

    private WebServerDiagnosticsSnapshot BuildWebDiagnosticsSnapshot()
    {
        lock (webMetricsLock)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var allDurations = webEndpointMetrics.Values
                .SelectMany(x => x.RecentDurations)
                .OrderBy(x => x)
                .ToList();
            double Percentile95(List<double> rows) => rows.Count == 0
                ? 0
                : rows[Math.Clamp((int)Math.Ceiling(rows.Count * 0.95) - 1, 0, rows.Count - 1)];
            var queueWaits = recentWebRequests
                .Select(x => x.QueueWaitMilliseconds)
                .Where(x => x > 0)
                .OrderBy(x => x)
                .ToList();

            long totalRequests = Math.Max(0, webTotalRequests);
            double totalDuration = webEndpointMetrics.Values.Sum(x => x.TotalMilliseconds);
            double totalWait = webEndpointMetrics.Values.Sum(x => x.MainThreadWaitTotalMilliseconds);
            double totalWork = webEndpointMetrics.Values.Sum(x => x.MainThreadWorkTotalMilliseconds);

            return new WebServerDiagnosticsSnapshot
            {
                CapturedUnixMilliseconds = now,
                ActiveRequests = Math.Max(0, Volatile.Read(ref webActiveRequests)),
                ConnectionCapacity = config.WebWorkerCount + config.WebRequestQueueCapacity,
                WorkerCount = config.WebWorkerCount,
                ActiveWorkers = Math.Max(0, Volatile.Read(ref webActiveWorkers)),
                QueueDepth = Math.Max(0, Volatile.Read(ref webQueuedRequests)),
                QueueCapacity = config.WebRequestQueueCapacity,
                PeakQueueDepth = Math.Max(0, Volatile.Read(ref webPeakQueuedRequests)),
                RejectedBusyRequests = Math.Max(0, Interlocked.Read(ref webRejectedBusyRequests)),
                AverageQueueWaitMilliseconds = queueWaits.Count == 0 ? 0 : queueWaits.Average(),
                P95QueueWaitMilliseconds = Percentile95(queueWaits),
                SnapshotCacheHits = Math.Max(0, Interlocked.Read(ref webSnapshotCacheHits)),
                SnapshotCacheMisses = Math.Max(0, Interlocked.Read(ref webSnapshotCacheMisses)),
                SnapshotRefreshes = Math.Max(0, Interlocked.Read(ref webSnapshotRefreshes)),
                TotalRequests = totalRequests,
                ErrorResponses = webErrorResponses,
                UnauthorizedResponses = webUnauthorizedResponses,
                RateLimitedResponses = webRateLimitedResponses,
                TotalResponseBytes = webTotalResponseBytes,
                RequestsLastMinute = recentWebRequests.Count(x => x.UnixMilliseconds >= now - 60_000L),
                AverageRequestMilliseconds = totalRequests == 0 ? 0 : totalDuration / totalRequests,
                P95RequestMilliseconds = Percentile95(allDurations),
                MaxRequestMilliseconds = webEndpointMetrics.Values.Count == 0 ? 0 : webEndpointMetrics.Values.Max(x => x.MaxMilliseconds),
                AverageMainThreadWaitMilliseconds = totalRequests == 0 ? 0 : totalWait / totalRequests,
                AverageMainThreadWorkMilliseconds = totalRequests == 0 ? 0 : totalWork / totalRequests,
                Endpoints = webEndpointMetrics.Values
                    .Select(x =>
                    {
                        var ordered = x.RecentDurations.OrderBy(v => v).ToList();
                        return new WebEndpointDiagnostic
                        {
                            Method = x.Method,
                            Path = x.Path,
                            Requests = x.Requests,
                            Errors = x.Errors,
                            ResponseBytes = x.ResponseBytes,
                            AverageMilliseconds = x.Requests == 0 ? 0 : x.TotalMilliseconds / x.Requests,
                            P95Milliseconds = Percentile95(ordered),
                            MaxMilliseconds = x.MaxMilliseconds,
                            AverageMainThreadWaitMilliseconds = x.Requests == 0 ? 0 : x.MainThreadWaitTotalMilliseconds / x.Requests,
                            AverageMainThreadWorkMilliseconds = x.Requests == 0 ? 0 : x.MainThreadWorkTotalMilliseconds / x.Requests,
                            AverageQueueWaitMilliseconds = x.Requests == 0 ? 0 : x.QueueWaitTotalMilliseconds / x.Requests
                        };
                    })
                    .OrderByDescending(x => x.P95Milliseconds)
                    .ThenByDescending(x => x.Requests)
                    .Take(30)
                    .ToList(),
                RecentSlowRequests = recentWebRequests
                    .Where(x => x.TotalMilliseconds >= 100 || x.StatusCode >= 400)
                    .OrderByDescending(x => x.UnixMilliseconds)
                    .Take(30)
                    .ToList()
            };
        }
    }

    private List<IncidentCauseScore> BuildIncidentCauseScores(
        ServerLagIncidentGroup group,
        IReadOnlyList<ServerHealthSample> during,
        WebServerDiagnosticsSnapshot web,
        DatabaseHealthSnapshot database)
    {
        var result = new List<IncidentCauseScore>();

        if (!string.IsNullOrWhiteSpace(group.LikelyModId))
        {
            int modScore = string.Equals(group.ModConfidence, "High", StringComparison.OrdinalIgnoreCase) ? 92 : 58;
            result.Add(new IncidentCauseScore
            {
                Category = $"Profiler attribution: {group.LikelyModId}",
                Score = modScore,
                Confidence = group.ModConfidence,
                Evidence = string.IsNullOrWhiteSpace(group.ModEvidence)
                    ? $"A profile overlapping this incident attributed significant tick work to {group.LikelyModId}."
                    : group.ModEvidence
            });
        }

        int storageScore = 5;
        if (group.BlockLoggerDropped > 0) storageScore = 95;
        else if (group.MaxBlockLoggerQueue >= 1000 || group.MaxBlockLoggerWriterMs >= 250 || database.P95QueryMilliseconds >= 500) storageScore = 75;
        else if (group.MaxBlockLoggerQueue >= 250 || group.MaxBlockLoggerWriterMs >= 100 || database.P95QueryMilliseconds >= 200) storageScore = 45;
        else if (group.MaxBlockLoggerQueue >= 50 || group.MaxBlockLoggerWriterMs >= 40 || database.P95QueryMilliseconds >= 100) storageScore = 20;
        result.Add(new IncidentCauseScore
        {
            Category = "BlockLogger storage",
            Score = storageScore,
            Confidence = during.Count >= 3 ? "High" : "Medium",
            Evidence = storageScore <= 10
                ? $"Queue stayed at or below {group.MaxBlockLoggerQueue:N0}; writer max {group.MaxBlockLoggerWriterMs:N0}ms; query P95 {database.P95QueryMilliseconds:0.0}ms; no event drops. BlockLogger storage is unlikely to be the cause."
                : $"Queue max {group.MaxBlockLoggerQueue:N0}; writer max {group.MaxBlockLoggerWriterMs:N0}ms; query P95 {database.P95QueryMilliseconds:0.0}ms; dropped {group.BlockLoggerDropped:N0}."
        });

        var nearbyWeb = web.RecentSlowRequests
            .Where(x => x.UnixMilliseconds >= group.StartUnixMilliseconds - 15_000L && x.UnixMilliseconds <= group.EndUnixMilliseconds + 15_000L)
            .ToList();
        double webMainWork = nearbyWeb.Count == 0 ? 0 : nearbyWeb.Max(x => x.MainThreadWorkMilliseconds);
        double webMainWait = nearbyWeb.Count == 0 ? 0 : nearbyWeb.Max(x => x.MainThreadWaitMilliseconds);
        double webQueueWait = nearbyWeb.Count == 0 ? 0 : nearbyWeb.Max(x => x.QueueWaitMilliseconds);
        int webScore = webMainWork >= 100 ? 80 : webMainWork >= 50 ? 55 : webMainWork >= 20 ? 30 : webQueueWait >= 500 ? 25 : nearbyWeb.Any(x => x.TotalMilliseconds >= 500) ? 20 : 5;
        result.Add(new IncidentCauseScore
        {
            Category = "Web dashboard",
            Score = webScore,
            Confidence = nearbyWeb.Count > 0 ? "Medium" : "Low",
            Evidence = nearbyWeb.Count == 0
                ? "No slow/error web request was recorded near this incident."
                : $"{nearbyWeb.Count} slow/error web request(s) nearby; max worker-queue wait {webQueueWait:0.0}ms, max main-thread work {webMainWork:0.0}ms, max main-thread wait {webMainWait:0.0}ms."
        });

        int gcScore = group.Gen2Collections > 0 ? Math.Min(90, 45 + group.Gen2Collections * 15) : 5;
        result.Add(new IncidentCauseScore
        {
            Category = "Garbage collection",
            Score = gcScore,
            Confidence = during.Count >= 2 ? "High" : "Medium",
            Evidence = group.Gen2Collections > 0
                ? $"Gen2 collections increased by {group.Gen2Collections} during the grouped incident."
                : "No Gen2 collection was correlated with this incident."
        });

        int entityScore = Math.Abs(group.MaxEntityDelta) >= 500 ? 75 : Math.Abs(group.MaxEntityDelta) >= 200 ? 45 : Math.Abs(group.MaxEntityDelta) >= 75 ? 20 : 5;
        result.Add(new IncidentCauseScore
        {
            Category = "Entity population",
            Score = entityScore,
            Confidence = "Medium",
            Evidence = entityScore <= 10
                ? $"Largest sampled entity-count change was {group.MaxEntityDelta:+#;-#;0}."
                : $"Largest sampled entity-count change was {group.MaxEntityDelta:+#;-#;0}; inspect entity hotspots if this repeats."
        });

        int chunkScore = Math.Abs(group.MaxChunkDelta) >= 300 ? 70 : Math.Abs(group.MaxChunkDelta) >= 100 ? 40 : Math.Abs(group.MaxChunkDelta) >= 30 ? 20 : 5;
        result.Add(new IncidentCauseScore
        {
            Category = "Chunk load/unload",
            Score = chunkScore,
            Confidence = "Low",
            Evidence = $"Largest sampled loaded-chunk change was {group.MaxChunkDelta:+#;-#;0}; chunk count is intentionally refreshed only about every 30 seconds."
        });

        return result.OrderByDescending(x => x.Score).ToList();
    }

    private sealed record WebConsoleCommandResult(bool Success, string Status, string Message);

    private static string QuoteCommandArgument(string value)
    {
        value ??= "";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private async Task<WebConsoleCommandResult> ExecuteWebConsoleCommandAsync(string command)
    {
        var tcs = new TaskCompletionSource<WebConsoleCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Event.EnqueueMainThreadTask(
            () =>
            {
                try
                {
                    var caller = new Caller
                    {
                        Type = EnumCallerType.Console,
                        CallerPrivileges = new[] { "*" },
                        CallerRole = "admin",
                        FromChatGroupId = 0
                    };
                    var callArgs = new TextCommandCallingArgs { Caller = caller, LanguageCode = "en" };
                    api.ChatCommands.ExecuteUnparsed(
                        command,
                        callArgs,
                        result =>
                        {
                            try
                            {
                                bool success = result != null && (result.Status == EnumCommandStatus.Success || result.Status == EnumCommandStatus.UnknownLegacy);
                                tcs.TrySetResult(new WebConsoleCommandResult(success, result?.Status.ToString() ?? "Unknown", FormatCommandResult(result, "Command")));
                            }
                            catch (Exception ex) { tcs.TrySetResult(new WebConsoleCommandResult(false, "Error", ex.Message)); }
                        });
                }
                catch (Exception ex) { tcs.TrySetResult(new WebConsoleCommandResult(false, "Error", ex.Message)); }
            },
            "web-console-command");
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
        if (completed == tcs.Task) return await tcs.Task.ConfigureAwait(false);
        return new WebConsoleCommandResult(false, "Timeout", "Command did not return within 8 seconds. Check the console output; it may still have executed.");
    }

    private async Task<WebConsoleCommandResult> ExecuteWebConsoleMessageAsync(string message, string actor)
    {
        try
        {
            await RunOnMainThreadAsync(
                () =>
                {
                    api.BroadcastMessageToAllGroups(message, EnumChatType.Notification);
                    api.Logger.Notification("[WebConsole] {0} broadcast a server message ({1} chars).", actor, message.Length);
                    return true;
                },
                "web-console-message").ConfigureAwait(false);
            return new WebConsoleCommandResult(true, "Success", "Server message broadcast to online players.");
        }
        catch (Exception ex)
        {
            return new WebConsoleCommandResult(false, "Error", ex.Message);
        }
    }

    private async Task<T> RunOnMainThreadAsync<T>(
        System.Func<T> action,
        string taskCode,
        bool skipIfTimedOut = false)
    {
        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestMetric = requestMetricContext.Value;
        skipIfTimedOut |= requestMetric?.Method == "GET";
        var workLease = requestMetric?.RouteLease;
        workLease?.Retain();
        long queuedTimestamp = Stopwatch.GetTimestamp();
        // Cache reads may be abandoned while waiting behind a blocked game thread.
        // Mutations retain their existing completion behavior after HTTP timeouts.
        int readState = 0; // 0 = queued, 1 = executing, 2 = abandoned
        int responseTimedOut = 0;

        try
        {
        api.Event.EnqueueMainThreadTask(
            () =>
            {
                if (skipIfTimedOut && Interlocked.CompareExchange(ref readState, 1, 0) != 0)
                {
                    tcs.TrySetCanceled();
                    workLease?.Dispose();
                    return;
                }
                long workStarted = Stopwatch.GetTimestamp();
                if (requestMetric != null)
                {
                    lock (requestMetric.Sync)
                    {
                        requestMetric.MainThreadWaitMilliseconds +=
                            (workStarted - queuedTimestamp) / (double)Stopwatch.Frequency * 1000d;
                    }
                }

                var workStopwatch = Stopwatch.StartNew();
                try
                {
                    if (disposed) throw new ObjectDisposedException(nameof(WebDashboardService));
                    T result = action();
                    // A timed-out write can still finish. Refresh affected views when it does,
                    // while the route lease continues to protect the real operation lifetime.
                    if (Volatile.Read(ref responseTimedOut) != 0 && requestMetric is { Method: not "GET" } &&
                        !Equals(result?.GetType().GetProperty("Success", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(result), false))
                        snapshotCache.Invalidate(key => CacheAffectedByMutation(key, requestMetric.Path));
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    workStopwatch.Stop();
                    workLease?.Dispose();
                    if (requestMetric != null)
                    {
                        lock (requestMetric.Sync)
                        {
                            requestMetric.MainThreadWorkMilliseconds += workStopwatch.Elapsed.TotalMilliseconds;
                        }
                    }
                }
            },
            taskCode);
        }
        catch { workLease?.Dispose(); throw; }

        Task completed = await Task.WhenAny(
            tcs.Task,
            Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);

        if (completed != tcs.Task)
        {
            Volatile.Write(ref responseTimedOut, 1);
            if (skipIfTimedOut)
            {
                Interlocked.CompareExchange(ref readState, 2, 0);
                // If a read already started, ignore its eventual result or exception.
                tcs.TrySetCanceled();
            }
            throw new TimeoutException(
                $"Main-thread task '{taskCode}' did not complete within 8 seconds.");
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task<T> AwaitWithTimeout<T>(
        Task<T> task,
        TimeSpan timeout,
        string operation)
    {
        Task completed = await Task.WhenAny(
            task,
            Task.Delay(timeout)).ConfigureAwait(false);

        if (completed != task)
            throw new TimeoutException(
                $"{operation} did not complete within {timeout.TotalSeconds:0} seconds.");

        return await task.ConfigureAwait(false);
    }

    private object InspectBadBlockEntity(string key)
    {
        if (logIntelligence == null)
            return new { success = false, error = "Log Intelligence / bad block-entity tracking is unavailable." };

        if (!logIntelligence.TryGetBadBlockEntity(key, out var record))
            return new { success = false, error = "Tracked block entity was not found." };

        var pos = new BlockPos(record.X, record.Y, record.Z, record.Dimension);
        if (!api.World.BlockAccessor.IsValidPos(pos))
            return new { success = false, error = "The tracked coordinate is outside the valid world range.", record };

        try
        {
            var block = api.World.BlockAccessor.GetBlock(pos);
            var blockEntity = api.World.BlockAccessor.GetBlockEntity(pos);
            string currentBlockCode = block?.Code?.ToString() ?? "game:air";
            string currentBlockEntityType = blockEntity?.GetType().FullName ?? "";
            string currentBlockEntityShortType = blockEntity?.GetType().Name ?? "";
            bool blockMatches = string.Equals(currentBlockCode, record.BlockCode, StringComparison.OrdinalIgnoreCase);
            bool entityMatches = blockEntity != null &&
                (string.Equals(currentBlockEntityShortType, record.BlockEntityType, StringComparison.Ordinal) ||
                 string.Equals(currentBlockEntityType, record.BlockEntityType, StringComparison.Ordinal));

            return new
            {
                success = true,
                record,
                live = new
                {
                    validPosition = true,
                    currentBlockCode,
                    blockId = block?.Id ?? 0,
                    blockClass = block?.GetType().FullName ?? "",
                    blockEntityPresent = blockEntity != null,
                    blockEntityClass = currentBlockEntityType,
                    blockMatchesLogged = blockMatches,
                    blockEntityMatchesLogged = entityMatches
                }
            };
        }
        catch (Exception ex)
        {
            return new
            {
                success = false,
                error = $"Live block inspection failed safely: {ex.GetType().Name}: {ex.Message}",
                record
            };
        }
    }

    private BadBlockEntityActionResult ExecuteBadBlockEntityAction(
        string key,
        string action,
        string value,
        string actor)
    {
        var result = new BadBlockEntityActionResult();
        if (logIntelligence == null)
        {
            result.Message = "Log Intelligence / bad block-entity tracking is unavailable.";
            return result;
        }

        action = (action ?? "").Trim().ToLowerInvariant();
        if (action == "clearall")
        {
            int removed = logIntelligence.ClearAllBadBlockEntities(actor, out string message);
            result.Success = true;
            result.Message = message;
            result.Detail = new { removed, trackerOnly = true };
            result.Snapshot = logIntelligence.GetBadBlockEntitySnapshot();
            api.Logger.Notification($"[NewfiesServerEssentials] Bad Block Entity Tracker cleared by {actor}: {removed} tracked record(s) removed; world data unchanged.");
            return result;
        }

        if (!logIntelligence.TryGetBadBlockEntity(key, out var record))
        {
            result.Message = "Tracked block entity was not found.";
            result.Snapshot = logIntelligence.GetBadBlockEntitySnapshot();
            return result;
        }

        var pos = new BlockPos(record.X, record.Y, record.Z, record.Dimension);

        try
        {
            switch (action)
            {
                case "stop":
                case "resume":
                {
                    bool tracking = action == "resume";
                    result.Success = logIntelligence.SetBadBlockEntityTracking(key, tracking, actor, out string message);
                    result.Message = message;
                    break;
                }

                case "teleport":
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        result.Message = "Enter an online player name or UID to teleport.";
                        break;
                    }

                    var player = api.World.AllOnlinePlayers
                        .OfType<IServerPlayer>()
                        .FirstOrDefault(p =>
                            string.Equals(p.PlayerName, value, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.PlayerUID, value, StringComparison.OrdinalIgnoreCase));
                    if (player == null || player.Entity == null)
                    {
                        result.Message = $"Online player '{value}' was not found.";
                        break;
                    }

                    var teleportPos = new BlockPos(record.X, record.Y + 1, record.Z, record.Dimension);
                    PacketGuardTrustedState.Mark(player, "dashboard-teleport");
                    player.Entity.TeleportTo(teleportPos);
                    result.Success = true;
                    result.Message = $"Teleported {player.PlayerName} above {record.X}, {record.Y}, {record.Z}.";
                    logIntelligence.RecordBadBlockEntityAction(key, actor, "Teleport", player.PlayerName);
                    break;
                }

                case "remove":
                {
                    if (!api.World.BlockAccessor.IsValidPos(pos))
                    {
                        result.Message = "The tracked coordinate is outside the valid world range.";
                        break;
                    }

                    var currentBlock = api.World.BlockAccessor.GetBlock(pos);
                    string currentCode = currentBlock?.Code?.ToString() ?? "game:air";
                    if (!string.Equals(currentCode, record.BlockCode, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Message = $"Safety check stopped removal because the live block is now '{currentCode}', not '{record.BlockCode}'. Refresh/inspect before acting.";
                        break;
                    }

                    var currentBlockEntity = api.World.BlockAccessor.GetBlockEntity(pos);
                    if (currentBlockEntity == null)
                    {
                        result.Message = "No block entity currently exists at that position.";
                        break;
                    }

                    string removedType = currentBlockEntity.GetType().FullName ?? currentBlockEntity.GetType().Name;
                    api.World.BlockAccessor.RemoveBlockEntity(pos);
                    result.Success = true;
                    result.Message = $"Removed block entity {removedType}. The block itself remains in place.";
                    logIntelligence.RecordBadBlockEntityAction(key, actor, "Remove block entity", removedType);
                    api.Logger.Warning(
                        "[BadBlockEntityTracker] {0} removed block entity {1} at {2},{3},{4} (dim {5}); block {6} remains.",
                        actor, removedType, record.X, record.Y, record.Z, record.Dimension, record.BlockCode);
                    break;
                }

                case "replace":
                {
                    if (!api.World.BlockAccessor.IsValidPos(pos))
                    {
                        result.Message = "The tracked coordinate is outside the valid world range.";
                        break;
                    }

                    string targetCode = (value ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(targetCode))
                    {
                        result.Message = "Replacement block code is required.";
                        break;
                    }
                    if (!targetCode.Contains(':')) targetCode = "game:" + targetCode;

                    var currentBlock = api.World.BlockAccessor.GetBlock(pos);
                    string currentCode = currentBlock?.Code?.ToString() ?? "game:air";
                    if (!string.Equals(currentCode, record.BlockCode, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Message = $"Safety check stopped replacement because the live block is now '{currentCode}', not '{record.BlockCode}'. Refresh/inspect before acting.";
                        break;
                    }

                    var targetBlock = api.World.GetBlock(new AssetLocation(targetCode));
                    if (targetBlock == null)
                    {
                        result.Message = $"Block '{targetCode}' could not be resolved.";
                        break;
                    }

                    // Clear first so replacing with the same block code also resets the old block entity/state.
                    api.World.BlockAccessor.SetBlock(0, pos);
                    if (targetBlock.Id != 0)
                        api.World.BlockAccessor.SetBlock(targetBlock.Id, pos);
                    api.World.BlockAccessor.MarkBlockModified(pos);

                    result.Success = true;
                    result.Message = $"Replaced {currentCode} with {targetBlock.Code}. Existing block-entity state was discarded and no drops were created.";
                    logIntelligence.RecordBadBlockEntityAction(key, actor, "Replace block", targetBlock.Code?.ToString() ?? targetCode);
                    api.Logger.Warning(
                        "[BadBlockEntityTracker] {0} replaced block {1} with {2} at {3},{4},{5} (dim {6}); previous block-entity state was discarded.",
                        actor, currentCode, targetBlock.Code, record.X, record.Y, record.Z, record.Dimension);
                    break;
                }

                default:
                    result.Message = "Unknown bad block-entity action.";
                    break;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Action failed safely: {ex.GetType().Name}: {ex.Message}";
        }

        result.Snapshot = logIntelligence.GetBadBlockEntitySnapshot();
        return result;
    }

    private Task<WebRequest?> ReadRequestAsync(NetworkStream stream,CancellationToken cancellationToken) =>
        WebHttpRequestParser.ReadAsync(stream,cancellationToken);

    private static string GetQuery(WebRequest request, string key, string fallback) =>
        request.Query.TryGetValue(key, out string? value)
            ? value
            : fallback;

    private static int GetQueryInt(WebRequest request, string key, int fallback) =>
        int.TryParse(GetQuery(request, key, ""), out int value)
            ? value
            : fallback;

    private static long GetQueryLong(WebRequest request, string key, long fallback) =>
        long.TryParse(
            GetQuery(request, key, ""),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value)
            ? value
            : fallback;

    private static object BuildChatGuardWebPayload(ChatGuardControlSnapshot snapshot)
    {
        JsonElement config = ParseJsonElement(snapshot.ConfigJson, "{}");
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
            config,
            recent,
            mutes,
            warnings,
            ruleStats,
            onlinePlayers,
            test
        };
    }

    private static JsonElement ParseJsonElement(string? raw, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? fallback : raw);
            return doc.RootElement.Clone();
        }
        catch
        {
            using var doc = JsonDocument.Parse(fallback);
            return doc.RootElement.Clone();
        }
    }

    private static string GetJsonString(string json, string name, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Dictionary<string, string> GetJsonStringDictionary(string json, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (!doc.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                result[property.Name] = property.Value.GetString() ?? string.Empty;
                if (result.Count >= 64) break;
            }
        }
        catch
        {
            // Invalid entries are ignored and the config's Clamp() performs final validation.
        }
        return result;
    }

    private static int GetJsonInt(string json, string name, int fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.TryGetProperty(name, out var value) &&
                   value.TryGetInt32(out int result)
                ? result
                : fallback;
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
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.TryGetProperty(name, out var value) &&
                   value.TryGetDouble(out double result)
                ? result
                : fallback;
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
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.TryGetProperty(name, out var value) &&
                   value.TryGetInt64(out long result)
                ? result
                : fallback;
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
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (!doc.RootElement.TryGetProperty(name, out var value))
                return fallback;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static bool TryGetCookie(WebRequest request, string name, out string value)
    {
        value = "";

        if (!request.Headers.TryGetValue("Cookie", out string? cookieHeader))
            return false;

        foreach (string pair in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0) continue;

            string key = pair[..equals].Trim();
            if (!key.Equals(name, StringComparison.Ordinal))
                continue;

            value = pair[(equals + 1)..].Trim();
            return true;
        }

        return false;
    }

    private byte[] BuildDiagnosticBundleBytes(
        object core,
        object blockActivity,
        IReadOnlyDictionary<string, object> extras)
    {
        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(
            memory,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            void AddJson(string name, object value)
            {
                var entry = zip.CreateEntry(
                    name,
                    CompressionLevel.Fastest);

                using var stream = entry.Open();
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(false));

                writer.Write(
                    JsonSerializer.Serialize(
                        value,
                        jsonOptions));
            }

            AddJson("performance-core.json", core);
            AddJson("block-activity.json", blockActivity);
            AddJson(
                "optimization-experiments.json",
                optimizationExperiments);

            foreach (var pair in extras)
                AddJson(pair.Key, pair.Value);
            AddJson(
                "hardening-summary.json",
                new
                {
                    buildId = BuildId,
                    targetGameVersion = TargetGameVersion,
                    retentionAndPrivacy =
                        BuildRetentionAndPrivacyStatus(),
                    circuitBreakers =
                        SnapshotCircuitBreakers(),
                    selfOverhead =
                        SnapshotOverhead(),
                    webRoutePolicies =
                        WebRoutePolicies
                });

            var readme = zip.CreateEntry(
                "README.txt",
                CompressionLevel.Fastest);

            using (var stream = readme.Open())
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false)))
            {
                writer.WriteLine("Newfies Server Essentials Suite diagnostic bundle v2");
                writer.WriteLine($"Generated UTC: {DateTime.UtcNow:O}");
                writer.WriteLine();
                writer.WriteLine("Contents:");
                writer.WriteLine("- performance-core.json: health timeline, archived black-box incidents, entity scans/growth, environment changes, alerts/actions, player-load history, world-save observations, server network samples, providers, Harmony overlaps, loaded mods, and redacted web-audit metadata");
                writer.WriteLine("- block-activity.json: recent BlockLogger coordinate activity (activity evidence, not CPU timing)");
                writer.WriteLine("- optimization-experiments.json: measured Tungsten/Synergy before/after tests");
                writer.WriteLine("- compatibility.json: lightweight known-rule/command ownership compatibility report (Harmony deep scan stays manual)");
                writer.WriteLine("- data-collection-audit.json: automatic/manual collector cadence, cost policy and measured suite/web overhead");
                writer.WriteLine("- login-protection.json: current non-secret Login Protection configuration and active count");
                writer.WriteLine("- suite-self-test.json: subsystem health, circuit breakers and storage checks");
                writer.WriteLine("- web-diagnostics.json: endpoint timing/main-thread wait/work evidence");
                writer.WriteLine("- smart-diagnosis.json: rolling health samples, Smart Diagnosis, trends, incidents and profiler evidence");
                writer.WriteLine("- loaded-mods.json: loaded mod IDs/names/versions/files");
                writer.WriteLine("- hardening-summary.json: build ID, retention/export privacy policy, circuit-breaker state, self-overhead, and web endpoint safety policy; no authentication secrets");
                writer.WriteLine();
                writer.WriteLine("Interpretation note: correlation/activity/population evidence is not automatically mod blame. Mod attribution comes only from bounded Vintage Story engine-profiler evidence and is confidence-labelled.");
            }
        }

        return memory.ToArray();
    }

    private async Task WriteJsonAsync(
        NetworkStream stream,
        int statusCode,
        object value,
        CancellationToken cancellationToken,
        string? setCookie = null,
        bool clearCookie = false)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions);
        var metric = requestMetricContext.Value;
        if (metric != null && metric.Method != "GET")
        {
            using var response = JsonDocument.Parse(body);
            if (response.RootElement.ValueKind == JsonValueKind.Object &&
                ((response.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
                 response.RootElement.TryGetProperty("error", out _))) metric.ResponseRejected = true;
        }

        var extra = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(setCookie))
            extra["Set-Cookie"] = setCookie;
        else if (clearCookie)
            extra["Set-Cookie"] =
                "nsu_session=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0" +
                (config.SecureCookies ? "; Secure" : "");

        await WriteResponseAsync(
            stream,
            statusCode,
            "application/json; charset=utf-8",
            body,
            cancellationToken,
            extra).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraHeaders = null)
    {
        var metric = requestMetricContext.Value;
        if (metric != null)
        {
            lock (metric.Sync)
            {
                metric.StatusCode = statusCode;
                metric.ResponseBytes += body.LongLength;
            }
        }

        string reason = statusCode switch
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
            _ => "OK"
        };

        var header = new StringBuilder();
        header.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
        header.Append($"Content-Type: {contentType}\r\n");
        header.Append($"Content-Length: {body.Length}\r\n");
        header.Append("Connection: close\r\n");
        header.Append("Cache-Control: no-store\r\n");
        header.Append("X-Content-Type-Options: nosniff\r\n");
        header.Append("X-Frame-Options: DENY\r\n");
        header.Append("Referrer-Policy: no-referrer\r\n");
        header.Append("Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n");

        if (extraHeaders != null)
        {
            foreach (var pair in extraHeaders)
                header.Append($"{pair.Key}: {pair.Value}\r\n");
        }

        header.Append("\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string RandomToken(int bytes)
    {
        byte[] value = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    private static bool ConstantTimeEquals(string left, string right)
    {
        byte[] a = Encoding.UTF8.GetBytes(left ?? "");
        byte[] b = Encoding.UTF8.GetBytes(right ?? "");

        if (a.Length != b.Length)
        {
            // Still perform a fixed-time compare against equal-length dummy data
            // to avoid an immediate early return on credential checks.
            byte[] dummy = new byte[a.Length];
            CryptographicOperations.FixedTimeEquals(a, dummy);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string? NormalizePlayerCategory(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "blocks" => "blocks",
            "containers" => "containers",
            "entities" => "entities",
            _ => null
        };

    private static string? NormalizeWebAction(string view, string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.Length == 0 || value == "any")
            return view == "containers" ? "container" : null;

        return view switch
        {
            "containers" => value is "open" or "take" or "put"
                ? value
                : "container",
            "entities" => value is "entityspawn" or "entitydeath" or "entitydespawn" or "entityinteract"
                ? value
                : null,
            _ => value is "break" or "place" or "interact"
                ? value
                : null
        };
    }


    private sealed class CompatibilityIgnoreStore
    {
        public int SchemaVersion { get; set; } = CompatibilityIgnoreSchemaVersion;
        public List<CompatibilityIgnoreRecord> Ignores { get; set; } = new();
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
        public Queue<double> RecentDurations { get; } = new();
    }

    private sealed class WebRequestMetricContext
    {
        public object Sync { get; } = new();
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
        public void Retain() => Interlocked.Increment(ref references);

        public RouteOperationLease(
            WebDashboardService owner,
            string operationKey,
            string displayName,
            bool releaseHeavy)
        {
            this.owner = owner;
            this.operationKey = operationKey;
            this.displayName = displayName;
            this.releaseHeavy = releaseHeavy;
            startedTimestamp = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref references) != 0) return;
            var target = Interlocked.Exchange(ref owner, null);
            target?.FinishRouteOperation(
                operationKey,
                displayName,
                releaseHeavy,
                startedTimestamp);
        }
    }

    private sealed record PerformanceSnapshotRouteResult(
        bool Success,
        string Message,
        PerformanceSnapshotRecord? Snapshot);

    private sealed record ConfigRestoreRouteResult(
        bool Success,
        bool NotFound,
        string Message);

    private sealed record LoginProtectionConfigUpdateResult(
        bool Success,
        string Message);

    private sealed record OptimizationExperimentPreparation(
        bool Success,
        string Message,
        OptimizationExperiment? Experiment);

    private sealed record OptimizationExperimentEvaluationResult(
        bool Success,
        string Message,
        OptimizationExperiment? Experiment);

    private sealed record TungstenOptimizationInfo(
        string Code,
        string Name,
        string Description,
        string Category);

    private sealed record SynergyOptimizationInfo(
        string Code,
        string Name,
        string Description,
        string Category,
        string Scope,
        string Caution);

    private sealed record TungstenCommandResult(
        bool Success,
        string Status,
        string Message,
        string Command);

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
}
