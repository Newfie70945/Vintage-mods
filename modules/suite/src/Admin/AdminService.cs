using System;
using Newfies.ChatProtocol;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using BlockLogger.Afk;
using BlockLogger.ServerHealth;
using BlockLogger.AntiCheat;
using BlockLogger.Models;
using BlockLogger.Services;
using BlockLogger.Reports;
using BlockLogger.PlayerCommands;
using BlockLogger.Restarts;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Packets;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace BlockLogger.Admin;

public sealed class AdminService : IDisposable
{
    private const string DiscordRestartControlEvent = "newfiesse:restart-control";
    private const string DiscordStaffCommandEvent = "newfiesse:discord-staff-command";
    public const string PrivView = "nse.player.view";
    public const string PrivManage = "nse.player.manage";
    public const string PrivWarn = "nse.moderation.warn";
    public const string PrivMute = "nse.moderation.mute";
    public const string PrivBan = "nse.moderation.ban";
    public const string PrivInventory = "nse.inventory.view";
    public const string PrivInventoryEdit = "nse.inventory.edit";
    public const string PrivTeleport = "nse.teleport";
    public const string PrivCleanup = "nse.cleanup";
    public const string PrivDiagnostics = "nse.diagnostics";
    public const string PrivServer = "nse.server";
    public const string PrivReports = "nse.reports.manage";
    public const string PrivSmite = "nse.smite";
    public const string PrivJail = "nse.jail";

    private const string StateFile = "NewfiesServerEssentialsAdmin.json";
    private readonly ICoreServerAPI api;
    private readonly System.Func<ServerHealthService?> getHealth;
    private readonly System.Func<LogQueryService?> getQueries;
    private readonly ReportsService? reports;
    private readonly PlayerCommandsService? playerCommands;
    private readonly Func<AutomatedRestartService?> getAutomatedRestarts;
    private readonly object gate = new();
    private AdminState state = new();
    private readonly Dictionary<string, EntityPos> frozenPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EntityPos> adminBackPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EntityPos> playerBackPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AdminGhostState> ghostStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlayerActivityState> activity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IServerPlayer> onlinePlayersByUid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IServerPlayer> onlinePlayersByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object onlineGate = new();
    private IReadOnlyList<AdminPlayerSummary> cachedOnlineSummaries = Array.Empty<AdminPlayerSummary>();
    private IReadOnlyList<AdminPlayerSummary> cachedAllSummaries = Array.Empty<AdminPlayerSummary>();
    private long cachedOnlineSummariesUnixMilliseconds;
    private long cachedAllSummariesUnixMilliseconds;
    private long cachedOnlineSummariesVersion = -1;
    private long cachedAllSummariesVersion = -1;
    private long playerSummaryVersion;
    private readonly object undoGate = new();
    private readonly Stack<AdminUndoEntry> undoStack = new();
    private bool performingUndo;
    private long tickListenerId = -1;
    private long freezeTickListenerId = -1;
    private long shutdownAtUnixMilliseconds;
    private string shutdownReason = "Server shutdown";
    private long lastShutdownAnnouncementBucket = long.MinValue;
    private bool disposed;
    private long lastTickErrorLogUnixMilliseconds;
    private int suppressedTickErrors;
    private AdminStorageService? storage;
    private JailService? jailService;
    private long nextRecordId = 1;
    private long lastActivitySampleUnixMilliseconds;
    private long lastExpiryScanUnixMilliseconds;

    // Entity.Pos changed from a public field in some API/reference builds to a property in 1.22.7.
    // Resolve it at runtime so a mod compiled against a stale/mixed reference set cannot JIT-crash the server.
    private static readonly PropertyInfo? RuntimePosProperty = typeof(Entity).GetProperty("Pos", BindingFlags.Instance | BindingFlags.Public);
    private static readonly FieldInfo? RuntimePosField = typeof(Entity).GetField("Pos", BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? RuntimeServerPosProperty = typeof(Entity).GetProperty("ServerPos", BindingFlags.Instance | BindingFlags.Public);
    private static readonly FieldInfo? RuntimeServerPosField = typeof(Entity).GetField("ServerPos", BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? RuntimeSidedPosProperty = typeof(Entity).GetProperty("SidedPos", BindingFlags.Instance | BindingFlags.Public);
    private static readonly FieldInfo? RuntimeSidedPosField = typeof(Entity).GetField("SidedPos", BindingFlags.Instance | BindingFlags.Public);

    public AdminService(ICoreServerAPI api, System.Func<ServerHealthService?> getHealth, System.Func<LogQueryService?> getQueries, ReportsService? reports, PlayerCommandsService? playerCommands, Func<AutomatedRestartService?> getAutomatedRestarts)
    {
        this.api = api;
        this.getHealth = getHealth;
        this.getQueries = getQueries;
        this.reports = reports;
        this.playerCommands = playerCommands;
        this.getAutomatedRestarts = getAutomatedRestarts;
    }

    public void Start()
    {
        storage = new AdminStorageService(api, SnapshotStateForStorage);
        storage.InventorySnapshotsCommitted += ReleasePersistedSnapshotPayloads;
        LoadState();
        InitializeNextRecordId();
        RegisterPrivileges();
        jailService = new JailService(api, Audit);
        jailService.Start();
        RegisterCommands();
        api.Event.PlayerJoin += OnPlayerJoin;
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Event.PlayerChat += OnPlayerChat;
        api.Event.CanPlaceOrBreakBlock += OnCanPlaceOrBreakBlock;
        api.Event.CanUseBlock += OnCanUseBlock;
        api.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
        api.Event.GameWorldSave += OnGameWorldSave;
        tickListenerId = api.Event.RegisterGameTickListener(OnTick, 1000);
        api.Event.RegisterEventBusListener(OnDiscordRestartControl, filterByEventName: DiscordRestartControlEvent);
        api.Event.RegisterEventBusListener(OnDiscordStaffCommand, filterByEventName: DiscordStaffCommandEvent);

        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
            IndexOnlinePlayer(player);
        foreach (var player in OnlinePlayers())
            BeginSession(player);
        RefreshFreezeTickListener();

        api.Logger.Notification("[ServerEssentials/Admin] Player management started. KnownPlayers={0}", state.Players.Count);
    }

    private void OnDiscordStaffCommand(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (disposed || data is not ITreeAttribute tree) return;

        string command = (tree.GetString("command", "") ?? "").Trim().ToLowerInvariant();
        string player = (tree.GetString("player", "") ?? "").Trim();
        string duration = (tree.GetString("duration", "") ?? "").Trim();
        string reason = (tree.GetString("reason", "") ?? "").Trim();
        string discordUser = (tree.GetString("discordUser", "Discord staff") ?? "Discord staff").Trim();
        string discordUserId = (tree.GetString("discordUserId", "") ?? "").Trim();
        string actor = string.IsNullOrWhiteSpace(discordUserId)
            ? $"Discord: {discordUser}"
            : $"Discord: {discordUser} ({discordUserId})";

        tree.SetBool("handled", true);
        try
        {
            string result;
            bool success;
            switch (command)
            {
                case "mute":
                    result = Mute(actor, player, duration, reason, "discord");
                    success = IsSuccessfulActionResult("mute", result);
                    break;
                case "unmute":
                    result = Unmute(actor, player, "discord");
                    success = IsSuccessfulActionResult("unmute", result);
                    break;
                case "ban":
                    result = TempBan(actor, player, duration, reason, "discord");
                    success = IsSuccessfulActionResult("tempban", result);
                    break;
                case "unban":
                    result = Unban(actor, player, "discord");
                    success = IsSuccessfulActionResult("unban", result);
                    break;
                default:
                    tree.SetBool("success", false);
                    tree.SetString("response", $"Unsupported Discord moderation command: {command}.");
                    return;
            }

            tree.SetBool("success", success);
            tree.SetString("response", result ?? "");
        }
        catch (Exception ex)
        {
            api.Logger.Error("[ServerEssentials/Admin] Discord moderation command {0} failed: {1}", command, ex);
            tree.SetBool("success", false);
            tree.SetString("response", "The moderation command failed inside Server Essentials Core. Check the server log.");
        }
    }

    private void OnDiscordRestartControl(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (disposed || data is not ITreeAttribute tree) return;

        tree.SetBool("handled", true);
        string operation = (tree.GetString("operation", "status") ?? "status").Trim().ToLowerInvariant();
        string actor = (tree.GetString("actor", "Discord staff") ?? "Discord staff").Trim();
        string reason = (tree.GetString("reason", "") ?? "").Trim();
        if (reason.Length > 240) reason = reason[..240];

        try
        {
            switch (operation)
            {
                case "status":
                {
                    long now = NowMs();
                    string manual = shutdownAtUnixMilliseconds > now
                        ? $"Manual {(IsRestartShutdown() ? "restart" : "shutdown")} in {FormatDuration(shutdownAtUnixMilliseconds - now)}."
                        : "No manual restart is scheduled.";
                    string automatic = getAutomatedRestarts()?.StatusText() ?? "Automated restart scheduler unavailable.";
                    tree.SetBool("success", true);
                    tree.SetString("response", manual + " " + automatic);
                    return;
                }
                case "schedule":
                {
                    int minutes = tree.GetInt("minutes", 0);
                    if (minutes < 1 || minutes > 1440)
                    {
                        tree.SetBool("success", false);
                        tree.SetString("response", "Restart delay must be between 1 and 1440 minutes.");
                        return;
                    }
                    string response = ScheduleRestart(actor, minutes * 60_000L, reason, "discord");
                    tree.SetBool("success", true);
                    tree.SetString("response", response);
                    return;
                }
                case "now":
                {
                    string response = ScheduleRestart(actor, 0, reason, "discord");
                    tree.SetBool("success", true);
                    tree.SetString("response", response);
                    return;
                }
                case "cancel":
                {
                    if (shutdownAtUnixMilliseconds <= 0)
                    {
                        tree.SetBool("success", false);
                        tree.SetString("response", "No manual restart is scheduled.");
                        return;
                    }
                    if (!IsRestartShutdown())
                    {
                        tree.SetBool("success", false);
                        tree.SetString("response", "A manual shutdown is scheduled, not a restart. Discord /restart will not cancel shutdowns.");
                        return;
                    }
                    tree.SetBool("success", true);
                    tree.SetString("response", CancelShutdown(actor, "discord"));
                    return;
                }
                case "skipnext":
                {
                    var restarts = getAutomatedRestarts();
                    if (restarts == null)
                    {
                        tree.SetBool("success", false);
                        tree.SetString("response", "Automated restart scheduler is unavailable.");
                        return;
                    }
                    tree.SetBool("success", true);
                    tree.SetString("response", restarts.SkipNext(actor));
                    return;
                }
                default:
                    tree.SetBool("success", false);
                    tree.SetString("response", "Unknown restart operation.");
                    return;
            }
        }
        catch (Exception ex)
        {
            api.Logger.Error("[ServerEssentials/Admin] Discord restart control failed: {0}", ex);
            tree.SetBool("success", false);
            tree.SetString("response", "Restart control failed on the server. Check the Server Essentials log.");
        }
    }

    public AdminStorageHealth GetStorageHealth()
    {
        var health = storage?.GetHealth() ?? new AdminStorageHealth { Available = false };
        lock (gate)
        {
            health.Players = state.Players.Count;
            health.AuditRows = state.Audit.Count;
            health.InventorySnapshots = state.InventorySnapshots.Values.Sum(x => x.Count);
        }
        return health;
    }
    public object GetRetentionDiagnostics()
    {
        lock (gate)
        {
            long headers = 0, loaded = 0, sampledSlots = 0, sampledBytes = 0;
            const int limit = 65536;
            foreach (var records in state.InventorySnapshots.Values)
                foreach (var snapshot in records)
                {
                    headers++;
                    if (!snapshot.SlotsPayloadLoaded) continue;
                    loaded++;
                    foreach (var slot in snapshot.Slots)
                    {
                        if (sampledSlots >= limit) break;
                        sampledSlots++; sampledBytes += slot.StackBytes?.LongLength ?? 0;
                    }
                }
            return new { players = state.Players.Count, auditRows = state.Audit.Count, snapshotHeaders = headers,
                loadedPayloads = loaded, sampledSlots, sampledBytes, sampleLimit = limit,
                sampleMayBeTruncated = sampledSlots >= limit,
                cleanup = "Persisted item payloads are released after commit; historical items are fetched on demand." };
        }
    }

    public string CheckStorageIntegrity() => storage?.RunQuickCheck() ?? "Storage unavailable";
    public string BackupStorage(string reason = "manual") => storage?.CreateBackup(reason) ?? "";

    public IReadOnlyList<AdminPlayerSummary> GetPlayers()
    {
        long timingStart = System.Diagnostics.Stopwatch.GetTimestamp();
        long now = NowMs();
        long version = Interlocked.Read(ref playerSummaryVersion);
        lock (gate)
        {
            if (cachedAllSummariesVersion == version &&
                now - cachedAllSummariesUnixMilliseconds < 5_000)
                return cachedAllSummaries;

            var online = OnlinePlayers().ToDictionary(p => p.PlayerUID, StringComparer.OrdinalIgnoreCase);
            var all = new HashSet<string>(state.Players.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var uid in online.Keys) all.Add(uid);

            cachedAllSummaries = all
                .Select(uid => BuildSummaryUnsafe(uid, online.TryGetValue(uid, out var p) ? p : null))
                .OrderByDescending(x => x.Online)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            cachedAllSummariesUnixMilliseconds = now;
            cachedAllSummariesVersion = version;
            SuiteTimingRegistry.Record(
                "Admin all-player summaries",
                (System.Diagnostics.Stopwatch.GetTimestamp() - timingStart) / (double)System.Diagnostics.Stopwatch.Frequency * 1000d);
            return cachedAllSummaries;
        }
    }

    /// <summary>
    /// Lightweight online-only view used by frequently refreshed surfaces such as the Web Map.
    /// A short shared cache lets the dashboard and public map reuse the same expensive player
    /// summary work instead of rebuilding health/ping/position data for every request.
    /// </summary>
    public IReadOnlyList<AdminPlayerSummary> GetOnlinePlayerSummaries()
    {
        long timingStart = System.Diagnostics.Stopwatch.GetTimestamp();
        long now = NowMs();
        long version = Interlocked.Read(ref playerSummaryVersion);
        lock (gate)
        {
            if (cachedOnlineSummariesVersion == version &&
                now - cachedOnlineSummariesUnixMilliseconds < 2_000)
                return cachedOnlineSummaries;

            cachedOnlineSummaries = OnlinePlayers()
                .Select(player => BuildSummaryUnsafe(player.PlayerUID, player))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            cachedOnlineSummariesUnixMilliseconds = now;
            cachedOnlineSummariesVersion = version;
            SuiteTimingRegistry.Record(
                "Admin online-player summaries",
                (System.Diagnostics.Stopwatch.GetTimestamp() - timingStart) / (double)System.Diagnostics.Stopwatch.Frequency * 1000d);
            return cachedOnlineSummaries;
        }
    }

    public AdminPlayerSummary? GetPlayer(string playerOrUid)
    {
        var player = FindOnlinePlayer(playerOrUid);
        string uid = player?.PlayerUID ?? ResolveKnownUid(playerOrUid);
        if (string.IsNullOrWhiteSpace(uid))
            uid = FindPlayer(playerOrUid)?.PlayerUID ?? "";
        if (string.IsNullOrWhiteSpace(uid)) return null;
        lock (gate) return BuildSummaryUnsafe(uid, player);
    }

    public List<AdminWarningRecord> GetWarnings(string playerOrUid)
    {
        string uid = FindPlayer(playerOrUid)?.PlayerUID ?? ResolveKnownUid(playerOrUid);
        lock (gate)
        {
            return state.Players.TryGetValue(uid, out var rec)
                ? rec.Warnings.OrderByDescending(x => x.UnixMilliseconds).Select(CloneWarning).ToList()
                : new();
        }
    }

    public List<AdminNoteRecord> GetNotes(string playerOrUid)
    {
        string uid = FindPlayer(playerOrUid)?.PlayerUID ?? ResolveKnownUid(playerOrUid);
        lock (gate)
        {
            return state.Players.TryGetValue(uid, out var rec)
                ? rec.Notes.Where(x => !x.Removed).OrderByDescending(x => x.UnixMilliseconds).Select(CloneNote).ToList()
                : new();
        }
    }

    public List<AdminAuditRecord> GetHistory(string playerOrUid, int limit = 100)
    {
        string uid = FindPlayer(playerOrUid)?.PlayerUID ?? ResolveKnownUid(playerOrUid);
        lock (gate)
        {
            return state.Audit
                .Where(x => string.Equals(x.TargetUid, uid, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.UnixMilliseconds)
                .Take(Math.Clamp(limit, 1, 500))
                .Select(CloneAudit)
                .ToList();
        }
    }


    public async System.Threading.Tasks.Task<List<LogEventRecord>> GetBlockHistoryAsync(string playerOrUid, int limit = 100)
    {
        var summary = GetPlayer(playerOrUid);
        var queries = getQueries();
        if (summary == null || queries == null) return new();
        try
        {
            return await queries.QueryPlayerAsync(summary.Name, Math.Clamp(limit, 1, 500), null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/Admin] BlockLogger history query failed for {0}: {1}", summary.Name, ex.Message);
            return new();
        }
    }

    public List<AdminAuditRecord> GetAudit(int limit = 200)
    {
        lock (gate)
        {
            return state.Audit.OrderByDescending(x => x.UnixMilliseconds)
                .Take(Math.Clamp(limit, 1, 1000)).Select(CloneAudit).ToList();
        }
    }

    public List<AdminInventorySlotView> GetInventory(string playerOrUid)
    {
        var player = FindOnlinePlayer(playerOrUid);
        return player == null ? new() : InspectInventories(player.InventoryManager.InventoriesOrdered);
    }

    public AdminInventoryInspection GetInventoryInspection(string playerOrUid, SavedPlayerInventoryData? saved = null)
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player != null)
        {
            var slots = InspectInventories(player.InventoryManager.InventoriesOrdered);
            return new AdminInventoryInspection
            {
                Slots = slots,
                Status = new AdminInventoryViewStatus { Available = true, ReadOnly = false, Source = "live", Partial = slots.Count >= SavedPlayerInventoryData.MaxSlots, CapturedUnixMilliseconds = NowMs(), ReadUnixMilliseconds = NowMs(), Message = "Current server inventory." }
            };
        }
        string uid = ResolveKnownUid(playerOrUid);
        AdminOfflineInventoryRecord? captured;
        AdminInventorySnapshotRecord? snapshot;
        lock (gate)
        {
            state.OfflineInventories.TryGetValue(uid, out captured);
            snapshot = state.InventorySnapshots.TryGetValue(uid, out var snapshots)
                ? snapshots.OrderByDescending(x => x.UnixMilliseconds).ThenByDescending(x => x.Id).FirstOrDefault() : null;
        }
        if (captured != null)
        {
            return new AdminInventoryInspection
            {
                Slots = captured.Slots.Select(CloneInventorySlot).ToList(),
                Status = new AdminInventoryViewStatus { Available = true, Source = captured.Source, CapturedUnixMilliseconds = captured.CapturedUnixMilliseconds, ReadUnixMilliseconds = NowMs(), Message = "Saved server inventory. Offline inventories are read only." }
            };
        }
        if (saved?.Inventories != null)
        {
            if (decodedInventoryCache.TryGetValue(uid, out var cached) && cached.Data.TryGetTarget(out var cachedData) && ReferenceEquals(cachedData, saved)) return cached.View;
            var result = new AdminInventoryInspection
            {
                Status = new AdminInventoryViewStatus { Available = true, Source = "world-save", ReadUnixMilliseconds = saved.ReadUnixMilliseconds, Message = "Saved world inventory. The game does not store its per-player save timestamp. Offline inventories are read only." }
            };
            foreach (var entry in saved.Inventories)
            {
                if (entry.Key.IndexOf("creative", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                try
                {
                    var tree = new TreeAttribute();
                    using var stream = new System.IO.MemoryStream(entry.Value, false);
                    using var reader = new System.IO.BinaryReader(stream);
                    tree.FromBytes(reader);
                    int count = tree.GetInt("qslots", 0);
                    if (tree.GetTreeAttribute("slots") == null)
                        throw new InvalidOperationException("This inventory uses an unsupported saved layout.");
                    if (count < 0 || count > SavedPlayerInventoryData.MaxSlots || result.Slots.Count + count > SavedPlayerInventoryData.MaxSlots)
                        throw new InvalidOperationException("Saved inventory slot limit exceeded.");
                    // Detached inventories preserve bags and their contents without initializing
                    // a player entity or putting anything into the live inventory manager.
                    InventoryBase inventory;
                    bool isBackpack = entry.Key.StartsWith("backpack-", StringComparison.OrdinalIgnoreCase);
                    if (isBackpack)
                        inventory = DetachedBackpackInventoryFactory.Create(entry.Key, api);
                    else
                        inventory = new InventoryGeneric(count, entry.Key, api);
                    inventory.FromTreeAttributes(tree);
                    var unresolvedBags = new List<AdminInventorySlotView>();
                    if (isBackpack)
                    {
                        long expandedCount = count;
                        // Before AfterBlocksLoaded expands bag contents, Count/indexer expose
                        // the detached bag slots without binding to engine-specific fields.
                        for (int index = 0; index < inventory.Count; index++)
                        {
                            var bagSlot = inventory[index];
                            var bagStack = bagSlot.Itemstack;
                            if (bagStack == null) continue;
                            if (bagStack.Collectible == null)
                            {
                                unresolvedBags.Add(InspectInventorySlot(entry.Key, index, bagSlot, bagStack, false));
                                bagSlot.Itemstack = null; // Detached copy only; preserve its unresolved display row below.
                                result.Status.Partial = true;
                                continue;
                            }
                            var bag = bagStack.Collectible.GetCollectibleInterface<IHeldBag>();
                            int bagCount = bag?.GetQuantitySlots(bagStack) ?? 0;
                            if (bagCount < 0) throw new InvalidOperationException("Invalid saved bag size.");
                            expandedCount += bagCount;
                            if (expandedCount + result.Slots.Count > SavedPlayerInventoryData.MaxSlots)
                                throw new InvalidOperationException("Saved bag slot limit exceeded.");
                        }
                        inventory.AfterBlocksLoaded(api.World);
                    }
                    if (result.Slots.Count + inventory.Count > SavedPlayerInventoryData.MaxSlots)
                        throw new InvalidOperationException("Saved inventory slot limit exceeded.");
                    var rows = InspectInventories(new[] { inventory }, false);
                    foreach (var bag in unresolvedBags)
                    {
                        int index = rows.FindIndex(x => x.Slot == bag.Slot);
                        if (index >= 0) rows[index] = bag;
                    }
                    if (rows.Any(x => x.Code.StartsWith("unresolved:", StringComparison.Ordinal))) result.Status.Partial = true;
                    result.Slots.AddRange(rows);
                }
                catch (Exception ex)
                {
                    result.Status.Partial = true;
                    api.Logger.Warning("[ServerEssentials/Admin] Could not inspect saved inventory {0} for {1}: {2}", entry.Key, uid, ex.Message);
                }
            }
            if (result.Status.Partial) result.Status.Message += " Some saved inventories could not be decoded; this view is incomplete.";
            while (decodedInventoryCache.Count >= 32 || decodedInventoryCache.Values.Sum(x => x.View.Slots.Count) + result.Slots.Count > 16384)
            {
                if (decodedInventoryCache.Count == 0) break;
                decodedInventoryCache.Remove(decodedInventoryCache.OrderBy(x => x.Value.ReadUnixMilliseconds).First().Key);
            }
            decodedInventoryCache[uid] = (new WeakReference<SavedPlayerInventoryData>(saved), saved.ReadUnixMilliseconds, result);
            return result;
        }
        if (snapshot != null)
        {
            if (!TryGetSnapshotForUse(snapshot, out var loadedSnapshot, out string error))
                return new AdminInventoryInspection { Status = new AdminInventoryViewStatus { Source="snapshot", SnapshotId=snapshot.Id, Partial=true, ReadUnixMilliseconds=NowMs(), Message=error } };
            snapshot = loadedSnapshot!;
            var result = new AdminInventoryInspection
            {
                Status = new AdminInventoryViewStatus { Available = true, Source = "snapshot", CapturedUnixMilliseconds = snapshot.UnixMilliseconds, ReadUnixMilliseconds = NowMs(), SnapshotId = snapshot.Id, Partial = true, Message = "Historical snapshot; shows saved occupied slots only. This may predate the player's last session. Offline inventories are read only." }
            };
            foreach (var row in snapshot.Slots)
            {
                var stack = DeserializeSnapshotStack(row);
                var view = InspectInventorySlot(row.Inventory, row.Slot, null, stack);
                if (stack == null)
                {
                    view.Empty = false;
                    view.Code = row.ItemCode;
                    view.Name = row.ItemCode + " (unresolved saved item)";
                    view.StackSize = row.StackSize;
                }
                result.Slots.Add(view);
            }
            return result;
        }
        return new AdminInventoryInspection { Status = new AdminInventoryViewStatus { ReadUnixMilliseconds = NowMs(), Message = saved?.Error ?? "No saved inventory is available. An inventory will be captured when this player next disconnects or the world saves." } };
    }

    // Call on a worker: this reader has its own read-only SQLite connection, never the
    // server's active connection. Captured records avoid repeated world-save disk reads.
    public SavedPlayerInventoryData? ReadSavedInventory(string worldFilepath, string playerUid)
    {
        lock (gate) if (state.OfflineInventories.ContainsKey(playerUid)) return null;
        return offlineInventoryReader.Read(worldFilepath, playerUid);
    }

    private readonly AdminOfflineInventoryReader offlineInventoryReader = new();
    // Accessed only on the game thread; bound by both players and total decoded slots.
    private readonly Dictionary<string, (WeakReference<SavedPlayerInventoryData> Data, long ReadUnixMilliseconds, AdminInventoryInspection View)> decodedInventoryCache = new(StringComparer.OrdinalIgnoreCase);

    private List<AdminInventorySlotView> InspectInventories(IEnumerable<IInventory> inventories, bool includeStackBytes = true)
    {
        var list = new List<AdminInventorySlotView>();
        foreach (var inv in inventories)
        {
            if (!IsManagedPlayerInventory(inv)) continue;
            for (int i = 0; i < inv.Count; i++)
            {
                if (list.Count >= SavedPlayerInventoryData.MaxSlots) return list;
                var slot = inv[i];
                if (slot == null) continue;
                list.Add(InspectInventorySlot(inv.InventoryID, i, slot, slot.Itemstack, includeStackBytes));
            }
        }
        return list;
    }

    private AdminInventorySlotView InspectInventorySlot(string inventoryId, int index, ItemSlot? slot, ItemStack? stack, bool includeStackBytes = true)
    {
        var view = new AdminInventorySlotView
        {
            Inventory = inventoryId, Section = InventorySection(inventoryId, slot), Slot = index,
            Empty = stack == null, MaxSlotStackSize = slot?.MaxSlotStackSize ?? 0,
            StorageType = slot?.StorageType.ToString() ?? "", Fingerprint = includeStackBytes ? Fingerprint(stack) : "read-only"
        };
        if (stack == null) return view;
        var collectible = stack.Collectible;
        view.Code = collectible?.Code?.ToString() ?? $"unresolved:{stack.Class}:{stack.Id}";
        try { view.Name = stack.GetName() ?? view.Code; } catch { view.Name = view.Code; }
        view.ItemClass = stack.Class.ToString();
        view.StackSize = stack.StackSize;
        view.MaxStackSize = collectible?.MaxStackSize ?? 0;
        if (includeStackBytes) try { view.SerializedStack = Convert.ToBase64String(stack.ToBytes()); } catch { }
        if (collectible != null)
        {
            try { view.MaxDurability = collectible.GetMaxDurability(stack); view.Durability = collectible.GetRemainingDurability(stack); } catch { }
            try { view.Temperature = collectible.GetTemperature(api.World, stack); } catch { }
        }
        return view;
    }

    private static AdminInventorySlotView CloneInventorySlot(AdminInventorySlotView x) => new()
    {
        Inventory=x.Inventory, Section=x.Section, Slot=x.Slot, Empty=x.Empty, Code=x.Code, Name=x.Name,
        ItemClass=x.ItemClass, StackSize=x.StackSize, MaxStackSize=x.MaxStackSize, MaxSlotStackSize=x.MaxSlotStackSize,
        Durability=x.Durability, MaxDurability=x.MaxDurability, Temperature=x.Temperature,
        StorageType=x.StorageType, Fingerprint=x.Fingerprint, SerializedStack=x.SerializedStack
    };

    private void CaptureOfflineInventory(IServerPlayer player, string source)
    {
        try
        {
            if (player.Entity == null) return; // Login rejection must not replace a valid capture.
            var slots = InspectInventories(player.InventoryManager.InventoriesOrdered.Where(x => x is InventoryBasePlayer), false);
            if (slots.Count == 0 || slots.Count >= SavedPlayerInventoryData.MaxSlots) return;
            var capture = new AdminOfflineInventoryRecord { CapturedUnixMilliseconds = NowMs(), Source = source, Slots = slots };
            lock (gate) { state.OfflineInventories[player.PlayerUID] = capture; storage?.RequestSave(); }
            offlineInventoryReader.Invalidate(player.PlayerUID);
            decodedInventoryCache.Remove(player.PlayerUID);
        }
        catch (Exception ex) { api.Logger.Warning("[ServerEssentials/Admin] Could not capture inventory for {0}: {1}", player.PlayerName, ex.Message); }
    }

    private void OnGameWorldSave()
    {
        var profiler=api.World?.FrameProfiler;
        bool tracing=profiler?.Enabled==true;
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        if(tracing)profiler!.Enter("suite-admin-world-save");
        try
        {
            foreach (var player in OnlinePlayers()) CaptureOfflineInventory(player, "server-save-capture");
            if(tracing)profiler!.Mark("suite-admin-save-inventory-views");
            long flushStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            SaveState(); // Keep the original synchronous commit guarantee.
            if(tracing)profiler!.Mark("suite-admin-save-storage-flush");
            double flushMs=System.Diagnostics.Stopwatch.GetElapsedTime(flushStarted).TotalMilliseconds;
            if(flushMs>=1)SuiteTimingRegistry.Record("Admin world-save storage flush (slow >=1ms)",flushMs);
        }
        finally
        {
            if(tracing)profiler!.Leave();
            double elapsed=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if(elapsed>=1)SuiteTimingRegistry.Record("Admin world-save handler (slow >=1ms)",elapsed);
        }
    }

    public List<AdminItemSearchResult> SearchItems(string search, int limit = 50)
    {
        search = (search ?? "").Trim();
        if (search.Length < 2) return new();
        limit = Math.Clamp(limit, 1, 100);
        var results = new List<AdminItemSearchResult>();
        foreach (var collectible in api.World.Collectibles)
        {
            if (collectible?.Code == null || collectible.IsMissing) continue;
            string code = collectible.Code.ToString();
            string name;
            try { name = new ItemStack(collectible, 1).GetName() ?? code; }
            catch { name = code; }
            if (code.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            results.Add(new AdminItemSearchResult
            {
                Code = code,
                Name = name,
                ItemClass = collectible is Block ? EnumItemClass.Block.ToString() : EnumItemClass.Item.ToString(),
                MaxStackSize = collectible.MaxStackSize
            });
            if (results.Count >= limit) break;
        }
        return results.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<AdminInventorySnapshotRecord> GetInventorySnapshots(string playerOrUid, int limit = 12)
    {
        string uid = FindOnlinePlayer(playerOrUid)?.PlayerUID ?? ResolveKnownUid(playerOrUid);
        if (string.IsNullOrWhiteSpace(uid)) return new();
        lock (gate)
        {
            if (!state.InventorySnapshots.TryGetValue(uid, out var snapshots)) return new();
            return snapshots.OrderByDescending(x => x.UnixMilliseconds).Take(Math.Clamp(limit, 1, 50)).Select(CloneSnapshotMetadata).ToList();
        }
    }

    public string CreateInventorySnapshot(string actor, string playerOrUid, string reason = "Manual snapshot", string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player == null) return "Player must be online.";
        var snapshot = CaptureInventorySnapshot(player, actor, reason);
        lock (gate)
        {
            if (!state.InventorySnapshots.TryGetValue(player.PlayerUID, out var list))
                state.InventorySnapshots[player.PlayerUID] = list = new();
            list.Add(snapshot);
            while (list.Count > 12) list.RemoveAt(0);
            AddAuditUnsafe(actor, "inventory-snapshot", player.PlayerUID, player.PlayerName,
                $"Snapshot #{snapshot.Id}: {snapshot.OccupiedSlots} occupied slots ({reason})", source);
            SaveStateUnsafe();
        }
        return $"Inventory snapshot #{snapshot.Id} created for {player.PlayerName} ({snapshot.OccupiedSlots} occupied slots).";
    }

    public string RestoreInventorySnapshot(string actor, string playerOrUid, long snapshotId, string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player == null) return "Player must be online.";
        AdminInventorySnapshotRecord? target;
        lock (gate)
        {
            target = state.InventorySnapshots.TryGetValue(player.PlayerUID, out var list)
                ? list.FirstOrDefault(x => x.Id == snapshotId)
                : null;
        }
        if (target == null) return "Inventory snapshot not found.";
        if (!TryGetSnapshotForUse(target, out var loadedTarget, out string error)) return error;
        target = loadedTarget!;
        if (!TryPrepareSnapshotRestore(player, target, out var prepared, out error)) return error;

        var before = CaptureInventorySnapshot(player, actor, $"Automatic backup before restoring snapshot #{snapshotId}");
        StoreSnapshotUnsafe(player, before, actor, source, audit: false);
        ApplyPreparedSnapshot(player, prepared);
        Audit(actor, "inventory-snapshot-restore", player.PlayerUID, player.PlayerName,
            $"Restored snapshot #{snapshotId}; automatic rollback snapshot #{before.Id} created", source);
        PushUndo($"inventory snapshot restore for {player.PlayerName}", undoActor => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
        return $"Restored inventory snapshot #{snapshotId} for {player.PlayerName}. Rollback snapshot #{before.Id} was created first.";
    }


    public List<AdminInventoryDiffRow> CompareInventorySnapshot(string playerOrUid, long snapshotId)
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player == null) return new();
        AdminInventorySnapshotRecord? snapshot;
        lock (gate)
        {
            snapshot = state.InventorySnapshots.TryGetValue(player.PlayerUID, out var list)
                ? list.FirstOrDefault(x => x.Id == snapshotId)
                : null;
        }
        if (snapshot == null) return new();
        if (!TryGetSnapshotForUse(snapshot, out var loadedSnapshot, out string error)) throw new InvalidOperationException(error);
        snapshot = loadedSnapshot!;

        var saved = snapshot.Slots.ToDictionary(x => $"{x.Inventory}\n{x.Slot}", StringComparer.OrdinalIgnoreCase);
        var rows = new List<AdminInventoryDiffRow>();
        foreach (var inv in player.InventoryManager.InventoriesOrdered)
        {
            if (!IsManagedPlayerInventory(inv)) continue;
            for (int i = 0; i < inv.Count; i++)
            {
                string key = $"{inv.InventoryID}\n{i}";
                saved.TryGetValue(key, out var savedSlot);
                var current = inv[i]?.Itemstack;
                var beforeStack = DeserializeSnapshotStack(savedSlot);
                string beforeFp = Fingerprint(beforeStack);
                string currentFp = Fingerprint(current);
                if (string.Equals(beforeFp, currentFp, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new AdminInventoryDiffRow
                {
                    Inventory = inv.InventoryID,
                    Slot = i,
                    SnapshotItem = DescribeStack(beforeStack),
                    CurrentItem = DescribeStack(current),
                    Change = beforeStack == null ? "Added" : current == null ? "Removed" : "Changed"
                });
            }
        }
        return rows;
    }

    public string RemoveInventorySlot(string actor, string playerOrUid, string inventoryId, int slotIndex, string source = "ingame", string expectedFingerprint = "")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player == null) return "Player must be online.";
        var inventory = FindInventory(player, inventoryId);
        if (inventory == null) return "Inventory not found.";
        if (slotIndex < 0 || slotIndex >= inventory.Count) return "Invalid inventory slot.";
        var slot = inventory[slotIndex];
        var stack = slot?.Itemstack;
        if (slot == null || stack == null) return "That inventory slot is empty.";
        if (!FingerprintMatches(stack, expectedFingerprint)) return "Inventory changed while you were editing. Refresh and try again.";

        var before = CaptureInventorySnapshot(player, actor, $"Automatic backup before removing {inventoryId}[{slotIndex}]");
        StoreSnapshotUnsafe(player, before, actor, source, audit: false);
        var backup = stack.Clone();
        string code = stack.Collectible?.Code?.ToString() ?? "unknown";
        int quantity = stack.StackSize;
        slot.Itemstack = null;
        slot.MarkDirty();
        Audit(actor, "inventory-remove", player.PlayerUID, player.PlayerName,
            $"Removed {quantity}x {code} from {inventory.InventoryID}[{slotIndex}]; snapshot #{before.Id}", source);
        PushUndo($"inventory removal {player.PlayerName} {inventory.InventoryID}[{slotIndex}]",
            undoActor => RestoreInventorySlot(undoActor, player.PlayerUID, inventory.InventoryID, slotIndex, backup));
        return $"Removed {quantity}x {code} from {player.PlayerName} {inventory.InventoryID}[{slotIndex}]. Snapshot #{before.Id} created.";
    }

    public string SetInventoryQuantity(string actor, string playerOrUid, string inventoryId, int slotIndex, int quantity, string expectedFingerprint = "", string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player == null) return "Player must be online.";
        var inventory = FindInventory(player, inventoryId);
        if (inventory == null || slotIndex < 0 || slotIndex >= inventory.Count) return "Inventory slot not found.";
        var slot = inventory[slotIndex];
        var stack = slot?.Itemstack;
        if (slot == null || stack == null) return "That inventory slot is empty.";
        if (!FingerprintMatches(stack, expectedFingerprint)) return "Inventory changed while you were editing. Refresh and try again.";
        int max = Math.Max(1, Math.Min(slot.MaxSlotStackSize, stack.Collectible?.MaxStackSize ?? slot.MaxSlotStackSize));
        quantity = Math.Clamp(quantity, 0, max);
        var before = CaptureInventorySnapshot(player, actor, $"Automatic backup before quantity edit {inventoryId}[{slotIndex}]");
        StoreSnapshotUnsafe(player, before, actor, source, audit: false);
        int old = stack.StackSize;
        string code = stack.Collectible?.Code?.ToString() ?? "unknown";
        if (quantity == 0) slot.Itemstack = null; else stack.StackSize = quantity;
        slot.MarkDirty();
        Audit(actor, "inventory-quantity", player.PlayerUID, player.PlayerName,
            $"Changed {code} in {inventoryId}[{slotIndex}] from {old} to {quantity}; snapshot #{before.Id}", source);
        PushUndo($"inventory quantity edit {player.PlayerName} {inventoryId}[{slotIndex}]",
            undoActor => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
        return $"Changed {code} quantity from {old} to {quantity}. Snapshot #{before.Id} created.";
    }

    public string MoveInventorySlot(string actor, string playerOrUid, string fromInventoryId, int fromSlotIndex, string toInventoryId, int toSlotIndex, string expectedFromFingerprint = "", string expectedToFingerprint = "", string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player == null) return "Player must be online.";
        var fromInventory = FindInventory(player, fromInventoryId);
        var toInventory = FindInventory(player, toInventoryId);
        if (fromInventory == null || toInventory == null) return "Inventory not found.";
        if (fromSlotIndex < 0 || fromSlotIndex >= fromInventory.Count || toSlotIndex < 0 || toSlotIndex >= toInventory.Count) return "Invalid inventory slot.";
        var from = fromInventory[fromSlotIndex];
        var to = toInventory[toSlotIndex];
        if (from?.Itemstack == null || to == null) return "Source slot is empty.";
        if (!FingerprintMatches(from.Itemstack, expectedFromFingerprint) || !FingerprintMatches(to.Itemstack, expectedToFingerprint))
            return "Inventory changed while you were editing. Refresh and try again.";
        if (ReferenceEquals(from, to)) return "Source and destination are the same slot.";
        if (!to.CanHold(from)) return "Destination slot cannot hold that item.";
        if (to.Itemstack != null && !from.CanHold(to)) return "Source slot cannot hold the destination item, so the swap was cancelled.";

        var before = CaptureInventorySnapshot(player, actor, $"Automatic backup before move {fromInventoryId}[{fromSlotIndex}] -> {toInventoryId}[{toSlotIndex}]");
        StoreSnapshotUnsafe(player, before, actor, source, audit: false);
        var a = from.Itemstack;
        var b = to.Itemstack;
        from.Itemstack = b;
        to.Itemstack = a;
        from.MarkDirty();
        to.MarkDirty();
        Audit(actor, "inventory-move", player.PlayerUID, player.PlayerName,
            $"Swapped {fromInventoryId}[{fromSlotIndex}] with {toInventoryId}[{toSlotIndex}]; snapshot #{before.Id}", source);
        PushUndo($"inventory move for {player.PlayerName}", undoActor => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
        return $"Moved/swapped inventory slots. Snapshot #{before.Id} created.";
    }

    public string GiveInventoryItem(string actor, string playerOrUid, string code, int quantity, string inventoryId = "", int slotIndex = -1, string expectedFingerprint = "", string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player == null) return "Player must be online.";
        var collectible = FindCollectible(code);
        if (collectible == null) return "Item/block code not found.";
        quantity = Math.Clamp(quantity, 1, Math.Max(1, collectible.MaxStackSize));
        var stack = new ItemStack(collectible, quantity);
        ItemSlot? targetSlot = null;

        if (!string.IsNullOrWhiteSpace(inventoryId) && slotIndex >= 0)
        {
            var inventory = FindInventory(player, inventoryId);
            if (inventory == null || slotIndex < 0 || slotIndex >= inventory.Count) return "Target inventory slot not found.";
            targetSlot = inventory[slotIndex];
            if (targetSlot == null) return "Target inventory slot not found.";
            if (!FingerprintMatches(targetSlot.Itemstack, expectedFingerprint)) return "Inventory changed while you were editing. Refresh and try again.";
            if (targetSlot.Itemstack != null) return "Target slot is occupied. Use Replace if you intend to overwrite it.";
            if (!targetSlot.CanHold(new DummySlot(stack))) return "Target slot cannot hold that item.";
        }

        var before = CaptureInventorySnapshot(player, actor, $"Automatic backup before giving {quantity}x {code}");

        if (targetSlot != null)
        {
            StoreSnapshotUnsafe(player, before, actor, source, audit: false);
            targetSlot.Itemstack = stack;
            targetSlot.MarkDirty();
        }
        else
        {
            // TryGiveItemStack can mutate the stack while searching/merging. Keep a snapshot only if the give succeeds.
            if (!player.Entity.TryGiveItemStack(stack)) return "No suitable free inventory space was available.";
            StoreSnapshotUnsafe(player, before, actor, source, audit: false);
        }
        Audit(actor, "inventory-give", player.PlayerUID, player.PlayerName,
            $"Gave {quantity}x {code}; snapshot #{before.Id}", source);
        PushUndo($"inventory give to {player.PlayerName}", undoActor => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
        return $"Gave {quantity}x {code} to {player.PlayerName}. Snapshot #{before.Id} created.";
    }

    public string ReplaceInventorySlot(string actor, string playerOrUid, string inventoryId, int slotIndex, string code, int quantity, string expectedFingerprint = "", string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player == null) return "Player must be online.";
        var inventory = FindInventory(player, inventoryId);
        if (inventory == null || slotIndex < 0 || slotIndex >= inventory.Count) return "Target inventory slot not found.";
        var slot = inventory[slotIndex];
        if (slot == null) return "Target inventory slot is unavailable.";
        if (!FingerprintMatches(slot.Itemstack, expectedFingerprint)) return "Inventory changed while you were editing. Refresh and try again.";
        var collectible = FindCollectible(code);
        if (collectible == null) return "Item/block code not found.";
        quantity = Math.Clamp(quantity, 1, Math.Max(1, Math.Min(slot.MaxSlotStackSize, collectible.MaxStackSize)));
        var replacementStack = new ItemStack(collectible, quantity);
        if (!slot.CanHold(new DummySlot(replacementStack))) return "Target slot cannot hold that item.";
        var before = CaptureInventorySnapshot(player, actor, $"Automatic backup before replacing {inventoryId}[{slotIndex}]");
        StoreSnapshotUnsafe(player, before, actor, source, audit: false);
        string old = slot.Itemstack?.Collectible?.Code?.ToString() ?? "empty";
        slot.Itemstack = replacementStack;
        slot.MarkDirty();
        Audit(actor, "inventory-replace", player.PlayerUID, player.PlayerName,
            $"Replaced {old} with {quantity}x {code} in {inventoryId}[{slotIndex}]; snapshot #{before.Id}", source);
        PushUndo($"inventory replace for {player.PlayerName}", undoActor => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
        return $"Replaced {inventoryId}[{slotIndex}] with {quantity}x {code}. Snapshot #{before.Id} created.";
    }

    private AdminInventorySnapshotRecord CaptureInventorySnapshot(IServerPlayer player, string actor, string reason)
    {
        var snapshot = new AdminInventorySnapshotRecord
        {
            Id = NextIdThreadSafe(),
            UnixMilliseconds = NowMs(),
            PlayerUid = player.PlayerUID,
            PlayerName = player.PlayerName,
            CreatedBy = actor,
            Reason = reason
        };
        foreach (var inv in player.InventoryManager.InventoriesOrdered)
        {
            if (!IsManagedPlayerInventory(inv)) continue;
            for (int i = 0; i < inv.Count; i++)
            {
                var stack = inv[i]?.Itemstack;
                if (stack == null) continue;
                snapshot.Slots.Add(new AdminInventorySnapshotSlot
                {
                    Inventory = inv.InventoryID,
                    Slot = i,
                    StackBytes = stack.ToBytes(),
                    ItemCode = stack.Collectible?.Code?.ToString() ?? "",
                    StackSize = stack.StackSize
                });
            }
        }
        snapshot.OccupiedSlots = snapshot.Slots.Count;
        return snapshot;
    }

    private void StoreSnapshotUnsafe(IServerPlayer player, AdminInventorySnapshotRecord snapshot, string actor, string source, bool audit)
    {
        lock (gate)
        {
            if (!state.InventorySnapshots.TryGetValue(player.PlayerUID, out var list))
                state.InventorySnapshots[player.PlayerUID] = list = new();
            list.Add(snapshot);
            while (list.Count > 12) list.RemoveAt(0);
            if (audit) AddAuditUnsafe(actor, "inventory-snapshot", player.PlayerUID, player.PlayerName,
                $"Snapshot #{snapshot.Id}: {snapshot.OccupiedSlots} occupied slots ({snapshot.Reason})", source);
            SaveStateUnsafe();
        }
    }

    private bool TryGetSnapshotForUse(AdminInventorySnapshotRecord header, out AdminInventorySnapshotRecord? snapshot, out string error)
    {
        lock (gate)
        {
            if (header.SlotsPayloadLoaded)
            {
                // Detach the list before a successful background save releases the
                // state's payload. Serialized arrays are immutable after capture.
                snapshot = CloneSnapshotMetadata(header);
                snapshot.Slots = header.Slots.ToList();
                snapshot.SlotsPayloadLoaded = true;
                error = "";
                return true;
            }
        }
        snapshot = null;
        error = "Inventory snapshot storage is unavailable. No inventory was changed.";
        return storage?.TryReadInventorySnapshot(header, out snapshot, out error) ?? false;
    }

    private void ReleasePersistedSnapshotPayloads(long committedHigh)
    {
        lock (gate)
            foreach (var list in state.InventorySnapshots.Values)
                foreach (var snapshot in list)
                    if (snapshot.Id <= committedHigh && snapshot.SlotsPayloadLoaded)
                    {
                        snapshot.Slots = new();
                        snapshot.SlotsPayloadLoaded = false;
                    }
    }

    private bool TryPrepareSnapshotRestore(IServerPlayer player, AdminInventorySnapshotRecord snapshot,
        out List<(IInventory Inventory, int Index, ItemStack Stack)> prepared, out string error)
    {
        prepared = new();
        error = "Inventory snapshot is incomplete or incompatible with the current inventories. No inventory was changed.";
        if (!snapshot.SlotsPayloadLoaded || snapshot.Slots.Count != snapshot.OccupiedSlots) return false;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in snapshot.Slots.OrderBy(x => x.Inventory, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Slot))
        {
            var inventory = FindInventory(player, saved.Inventory);
            if (inventory == null || saved.Slot < 0) return false;
            var stack = DeserializeSnapshotStack(saved);
            if (stack == null || stack.StackSize <= 0 || !targets.Add(saved.Inventory + "\n" + saved.Slot)) return false;
            prepared.Add((inventory, saved.Slot, stack));
        }
        // Bag slots are rebuilt whenever a bag changes. Validate saved capacity
        // against detached saved bags, not today's bags or temporary live slot objects.
        foreach (var group in prepared.GroupBy(x => x.Inventory))
        {
            var inventory = group.Key;
            if (inventory.InventoryID.StartsWith("backpack-", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var detached = DetachedBackpackInventoryFactory.Create(inventory.InventoryID, api);
                    int bagSlots = detached.Count;
                    foreach (var row in group)
                        if (row.Index < bagSlots) detached[row.Index].Itemstack = row.Stack.Clone();
                    detached.AfterBlocksLoaded(api.World);
                    if (group.Any(x => x.Index >= detached.Count || detached[x.Index] == null)) return false;
                }
                catch { return false; }
            }
            else if (group.Any(x => x.Index >= inventory.Count || inventory[x.Index] == null)) return false;
        }
        error = "";
        return true;
    }

    private void ApplyPreparedSnapshot(IServerPlayer player, List<(IInventory Inventory, int Index, ItemStack Stack)> prepared)
    {
        foreach (var inv in player.InventoryManager.InventoriesOrdered)
        {
            if (!IsManagedPlayerInventory(inv)) continue;
            // Clear bag contents before removing bags so their changing Count
            // cannot skip slots or leave stale references behind.
            for (int i = inv.Count - 1; i >= 0; i--)
            {
                var slot = inv[i];
                if (slot == null) continue;
                slot.Itemstack = null;
                slot.MarkDirty();
            }
        }
        // Prepared rows are ordered by inventory and slot: bags restore before
        // their contents. Resolve each live slot after any preceding bag reload.
        foreach (var (inventory, index, stack) in prepared)
        {
            var slot = inventory[index];
            if (slot == null) throw new InvalidOperationException("Inventory layout changed while restoring the snapshot.");
            slot.Itemstack = stack;
            slot.MarkDirty();
        }
    }

    private ItemStack? DeserializeSnapshotStack(AdminInventorySnapshotSlot? saved)
    {
        if (saved == null) return null;
        try
        {
            byte[] bytes = saved.StackBytes ?? Array.Empty<byte>();
            if (bytes.Length == 0 && !string.IsNullOrWhiteSpace(saved.StackBase64))
                bytes = Convert.FromBase64String(saved.StackBase64);
            if (bytes.Length == 0) return null;
            var stack = new ItemStack(bytes);
            return stack.ResolveBlockOrItem(api.World) ? stack : null;
        }
        catch { return null; }
    }

    private static string DescribeStack(ItemStack? stack)
    {
        if (stack == null) return "Empty";
        string code = stack.Collectible?.Code?.ToString() ?? "unknown";
        string name;
        try { name = stack.GetName() ?? code; } catch { name = code; }
        return $"{stack.StackSize}x {name} ({code})";
    }

    private IInventory? FindInventory(IServerPlayer player, string inventoryId) =>
        player.InventoryManager.InventoriesOrdered.FirstOrDefault(inv =>
            IsManagedPlayerInventory(inv) &&
            string.Equals(inv.InventoryID, inventoryId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Creative inventory is a catalog, not player-owned inventory. Including it in the
    /// admin manager can expose many thousands of virtual stacks, produce huge JSON
    /// payloads, and make snapshots/restores dangerously expensive. Keep the inventory
    /// manager limited to real player-owned inventories.
    /// </summary>
    private static bool IsManagedPlayerInventory(IInventory? inventory)
    {
        if (inventory == null) return false;
        string id = inventory.InventoryID ?? "";
        string typeName = inventory.GetType().Name ?? "";
        if (id.IndexOf("creative", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (typeName.IndexOf("creative", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }

    private CollectibleObject? FindCollectible(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (!code.Contains(':')) code = "game:" + code;
        var loc = new AssetLocation(code);
        return (CollectibleObject?)api.World.GetItem(loc) ?? api.World.GetBlock(loc);
    }

    private static string InventorySection(string inventoryId, ItemSlot? slot = null)
    {
        if (slot is ItemSlotCharacter || slot is ItemSlotOffhand) return "Equipment";
        string id = (inventoryId ?? "").ToLowerInvariant();
        if (id.Contains("hotbar")) return "Hotbar";
        if (id.Contains("backpack")) return "Backpack";
        if (id.Contains("character") || id.Contains("gear") || id.Contains("armor")) return "Equipment";
        if (id.Contains("craft")) return "Crafting";
        if (id.Contains("mouse")) return "Cursor";
        return "Other";
    }

    private static string Fingerprint(ItemStack? stack)
    {
        if (stack == null) return "empty";
        try { return Convert.ToHexString(SHA256.HashData(stack.ToBytes())); }
        catch { return $"{stack.Class}:{stack.Id}:{stack.StackSize}"; }
    }

    private static bool FingerprintMatches(ItemStack? stack, string expected) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(Fingerprint(stack), expected, StringComparison.OrdinalIgnoreCase);

    private long NextIdThreadSafe()
    {
        lock (gate) return NextIdUnsafe();
    }

    private static AdminInventorySnapshotRecord CloneSnapshotMetadata(AdminInventorySnapshotRecord x) => new()
    {
        Id = x.Id,
        UnixMilliseconds = x.UnixMilliseconds,
        PlayerUid = x.PlayerUid,
        PlayerName = x.PlayerName,
        CreatedBy = x.CreatedBy,
        Reason = x.Reason,
        OccupiedSlots = x.OccupiedSlots,
        Slots = new(),
        SlotsPayloadLoaded = false
    };

    public string Warn(string actor, string playerOrUid, string reason, string source = "ingame")
    {
        if (!TryResolveTarget(playerOrUid, out var uid, out var name)) return "Player not found.";
        if (string.IsNullOrWhiteSpace(reason)) return "A warning reason is required.";
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            var warning = new AdminWarningRecord
            {
                Id = NextIdUnsafe(), UnixMilliseconds = NowMs(), IssuedBy = actor, Reason = reason.Trim()
            };
            rec.Warnings.Add(warning);
            AddAuditUnsafe(actor, "warn", uid, name, $"Warning #{warning.Id}: {warning.Reason}", source);
            SaveStateUnsafe();
            PushUndo($"warning #{warning.Id} on {name}", undoActor => RemoveWarning(undoActor, uid, warning.Id, "undo"));
            FindOnlinePlayer(uid)?.SendMessage(GlobalConstants.GeneralChatGroup,
                $"You received a staff warning: {warning.Reason}", EnumChatType.Notification);
            return $"Warning #{warning.Id} issued to {name}.";
        }
    }

    public string RemoveWarning(string actor, string playerOrUid, long warningId, string source = "ingame")
    {
        if (!TryResolveTarget(playerOrUid, out var uid, out var name)) return "Player not found.";
        lock (gate)
        {
            if (!state.Players.TryGetValue(uid, out var rec)) return "No warning history.";
            var warning = rec.Warnings.FirstOrDefault(x => x.Id == warningId && !x.Removed);
            if (warning == null) return "Active warning not found.";
            warning.Removed = true;
            warning.RemovedBy = actor;
            warning.RemovedUnixMilliseconds = NowMs();
            AddAuditUnsafe(actor, "unwarn", uid, name, $"Removed warning #{warningId}", source);
            SaveStateUnsafe();
            return $"Warning #{warningId} removed from {name}.";
        }
    }

    public string Mute(string actor, string playerOrUid, string duration, string reason, string source = "ingame")
    {
        if (!TryResolveTarget(playerOrUid, out var uid, out var name)) return "Player not found.";
        if (!TryParseDuration(duration, out long milliseconds, out bool permanent)) return "Invalid duration. Use 30m, 6h, 3d, 2w, or permanent.";
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            bool oldPermanent = rec.PermanentMute;
            long oldUntil = rec.MuteUntilUnixMilliseconds;
            string oldReason = rec.MuteReason;
            string oldBy = rec.MutedBy;
            rec.PermanentMute = permanent;
            rec.MuteUntilUnixMilliseconds = permanent ? 0 : DeadlineAfterMilliseconds(milliseconds);
            rec.MuteReason = string.IsNullOrWhiteSpace(reason) ? "Muted by staff" : reason.Trim();
            rec.MutedBy = actor;
            AddAuditUnsafe(actor, "mute", uid, name, $"{duration}: {rec.MuteReason}", source);
            SaveStateUnsafe();
            PushUndo($"mute on {name}", undoActor => RestoreMute(undoActor, uid, name, oldPermanent, oldUntil, oldReason, oldBy));
            FindOnlinePlayer(uid)?.SendMessage(GlobalConstants.GeneralChatGroup,
                $"You have been muted. Reason: {rec.MuteReason}", EnumChatType.Notification);
            return $"{name} muted for {duration}.";
        }
    }

    public string Unmute(string actor, string playerOrUid, string source = "ingame")
    {
        if (!TryResolveTarget(playerOrUid, out var uid, out var name)) return "Player not found.";
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            rec.PermanentMute = false; rec.MuteUntilUnixMilliseconds = 0; rec.MuteReason = ""; rec.MutedBy = "";
            AddAuditUnsafe(actor, "unmute", uid, name, "Mute cleared", source);
            SaveStateUnsafe();
            return $"{name} unmuted.";
        }
    }

    public string TempBan(string actor, string playerOrUid, string duration, string reason, string source = "ingame")
    {
        if (!TryResolveTarget(playerOrUid, out var uid, out var name)) return "Player not found.";
        if (!TryParseDuration(duration, out long milliseconds, out bool permanent) || permanent)
            return "Temporary bans require a duration such as 30m, 6h, 3d, or 2w.";
        string displayReason = string.IsNullOrWhiteSpace(reason) ? "Temporarily banned by staff" : reason.Trim();
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            long oldUntil = rec.BanUntilUnixMilliseconds;
            string oldReason = rec.BanReason;
            string oldBy = rec.BannedBy;
            rec.BanUntilUnixMilliseconds = DeadlineAfterMilliseconds(milliseconds);
            rec.BanReason = displayReason;
            rec.BannedBy = actor;
            AddAuditUnsafe(actor, "tempban", uid, name, $"{duration}: {rec.BanReason}", source);
            SaveStateUnsafe();
            PushUndo($"temporary ban on {name}", undoActor => RestoreBan(undoActor, uid, name, oldUntil, oldReason, oldBy));
        }
        var online = FindOnlinePlayer(uid);
        if (online != null) DisconnectWithNotice(online, $"You have been temporarily banned for {duration}. Reason: {displayReason}", $"Temporarily banned for {duration}. Reason: {displayReason}");
        return $"{name} temporarily banned for {duration}.";
    }

    public string Unban(string actor, string playerOrUid, string source = "ingame")
    {
        if (!TryResolveTarget(playerOrUid, out var uid, out var name)) return "Player not found.";
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            rec.BanUntilUnixMilliseconds = 0; rec.BanReason = ""; rec.BannedBy = "";
            AddAuditUnsafe(actor, "unban", uid, name, "Temporary ban cleared", source);
            SaveStateUnsafe();
            return $"Temporary ban cleared for {name}.";
        }
    }

    public string AddNote(string actor, string playerOrUid, string text, string source = "ingame")
    {
        if (!TryResolveTarget(playerOrUid, out var uid, out var name)) return "Player not found.";
        if (string.IsNullOrWhiteSpace(text)) return "Note text is required.";
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            var note = new AdminNoteRecord { Id = NextIdUnsafe(), UnixMilliseconds = NowMs(), Author = actor, Text = text.Trim() };
            rec.Notes.Add(note);
            AddAuditUnsafe(actor, "note", uid, name, $"Note #{note.Id} added", source);
            SaveStateUnsafe();
            PushUndo($"staff note #{note.Id} on {name}", undoActor => RemoveNote(undoActor, uid, note.Id, "undo"));
            return $"Staff note #{note.Id} added to {name}.";
        }
    }

    public string RemoveNote(string actor, string playerOrUid, long noteId, string source = "ingame")
    {
        if (!TryResolveTarget(playerOrUid, out var uid, out var name)) return "Player not found.";
        lock (gate)
        {
            if (!state.Players.TryGetValue(uid, out var rec)) return "No notes found.";
            var note = rec.Notes.FirstOrDefault(x => x.Id == noteId && !x.Removed);
            if (note == null) return "Active note not found.";
            note.Removed = true;
            AddAuditUnsafe(actor, "note-remove", uid, name, $"Note #{noteId} removed", source);
            SaveStateUnsafe();
            return $"Staff note #{noteId} removed.";
        }
    }

    public string Freeze(string actor, string playerOrUid, string duration, string reason, string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player?.Entity == null) return "Player must be online to freeze.";
        long milliseconds = 0; bool permanent = true;
        if (!string.IsNullOrWhiteSpace(duration) && duration != "permanent" && !TryParseDuration(duration, out milliseconds, out permanent))
            return "Invalid duration.";
        var currentPos = GetRuntimeEntityPos(player.Entity);
        if (currentPos == null) return "Unable to read player position.";
        string detail = string.IsNullOrWhiteSpace(reason) ? "Frozen by staff" : reason.Trim();
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(player.PlayerUID, player.PlayerName);
            bool oldFrozen = rec.Frozen;
            long oldUntil = rec.FreezeUntilUnixMilliseconds;
            string oldBy = rec.FrozenBy;
            string oldReason = rec.FreezeReason;
            EntityPos? oldPos = frozenPositions.TryGetValue(player.PlayerUID, out var previousFrozenPos) ? previousFrozenPos.Copy() : null;
            rec.Frozen = true;
            rec.FreezeUntilUnixMilliseconds = permanent ? 0 : DeadlineAfterMilliseconds(milliseconds);
            rec.FrozenBy = actor;
            rec.FreezeReason = detail;
            frozenPositions[player.PlayerUID] = currentPos.Copy();
            AddAuditUnsafe(actor, "freeze", player.PlayerUID, player.PlayerName, $"{duration}: {rec.FreezeReason}", source);
            SaveStateUnsafe();
            PushUndo($"freeze on {player.PlayerName}", undoActor => RestoreFreeze(undoActor, player.PlayerUID, player.PlayerName, oldFrozen, oldUntil, oldBy, oldReason, oldPos));
        }
        RefreshFreezeTickListener();
        string freezeDuration = permanent ? "until released" : duration;
        player.SendMessage(GlobalConstants.GeneralChatGroup, $"You have been frozen for {freezeDuration}. Reason: {detail}", EnumChatType.Notification);
        return $"{player.PlayerName} frozen for {freezeDuration}.";
    }

    public string Unfreeze(string actor, string playerOrUid, string source = "ingame")
    {
        if (!TryResolveTarget(playerOrUid, out var uid, out var name)) return "Player not found.";
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            rec.Frozen = false; rec.FreezeUntilUnixMilliseconds = 0; rec.FrozenBy = ""; rec.FreezeReason = "";
            frozenPositions.Remove(uid);
            AddAuditUnsafe(actor, "unfreeze", uid, name, "Freeze cleared", source);
            SaveStateUnsafe();
        }
        RefreshFreezeTickListener();
        return $"{name} unfrozen.";
    }

    public string Kick(string actor, string playerOrUid, string reason, string source = "web")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player == null) return "Player must be online.";
        string detail = string.IsNullOrWhiteSpace(reason) ? "Kicked by staff" : reason.Trim();
        Audit(actor, "kick", player.PlayerUID, player.PlayerName, detail, source);
        DisconnectWithNotice(player, $"You are being kicked from the server. Reason: {detail}", detail);
        return $"{player.PlayerName} kick scheduled with player-facing reason.";
    }

    private void DisconnectWithNotice(IServerPlayer player, string notice, string disconnectReason, int delayMs = 2200)
    {
        string uid = player.PlayerUID;
        // Send the reason through two independent player-facing paths before the disconnect.
        // SendIngameError is a dedicated UI packet and remains visible even when chat is hidden;
        // the notification chat packet is retained as a second chance for clients that render it.
        try
        {
            player.SendIngameError("newfies-server-disconnect", notice);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/Admin] Could not send pre-disconnect ingame error to {0}: {1}", player.PlayerName, ex.Message);
        }

        try
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, notice, EnumChatType.Notification);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/Admin] Could not send pre-disconnect notice to {0}: {1}", player.PlayerName, ex.Message);
        }

        // Vintage Story currently has cases where Disconnect(reason) reaches the kicked client as
        // "Unknown". Give the normal notification packet a short head start, then still pass the
        // same reason to Disconnect for clients/game versions where the API reason works correctly.
        api.Event.RegisterCallback(_ =>
        {
            try
            {
                var online = FindOnlinePlayer(uid);
                if (online == null) return;

                if (!PlayerDisconnectCompat.DisconnectWithReason(online, disconnectReason, notice, out string compatError))
                {
                    api.Logger.Warning("[ServerEssentials/Admin] Correct player-facing disconnect path unavailable for {0}: {1}. Falling back to API disconnect after sending the UI reason.", uid, compatError);
                    online.Disconnect(disconnectReason);
                }
            }
            catch (Exception ex) { api.Logger.Warning("[ServerEssentials/Admin] Delayed disconnect failed for {0}: {1}", uid, ex.Message); }
        }, Math.Clamp(delayMs, 250, 3000));
    }

    public string Heal(string actor, string playerOrUid, string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player?.Entity == null) return "Player must be online.";
        var health = player.Entity.GetBehavior<EntityBehaviorHealth>();
        if (health == null) return "Player has no health behavior.";
        health.Health = health.MaxHealth;
        health.MarkDirty();
        Audit(actor, "heal", player.PlayerUID, player.PlayerName, "Restored to full health", source);
        return $"{player.PlayerName} healed to full health.";
    }

    public string Feed(string actor, string playerOrUid, string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player?.Entity == null) return "Player must be online.";
        var hunger = player.Entity.GetBehavior<EntityBehaviorHunger>();
        if (hunger == null) return "Player has no hunger behavior.";
        player.Entity.ReceiveSaturation(Math.Max(0, hunger.MaxSaturation - hunger.Saturation), EnumFoodCategory.Unknown, 10, 0);
        Audit(actor, "feed", player.PlayerUID, player.PlayerName, "Restored satiety", source);
        return $"{player.PlayerName} fed.";
    }

    public string Revive(string actor, string playerOrUid, string source = "ingame")
    {
        var player = FindOnlinePlayer(playerOrUid);
        if (player?.Entity == null) return "Player must be online.";
        player.Entity.Revive();
        Audit(actor, "revive", player.PlayerUID, player.PlayerName, "Revived player", source);
        return $"{player.PlayerName} revived.";
    }

    public string ToggleGhost(IServerPlayer player, string source = "ingame")
    {
        if (player?.WorldData == null || string.IsNullOrWhiteSpace(player.PlayerUID))
            return "Ghost mode is unavailable for this player.";

        if (ghostStates.TryGetValue(player.PlayerUID, out AdminGhostState? previous))
        {
            player.WorldData.CurrentGameMode = previous.GameMode;
            player.WorldData.FreeMove = previous.FreeMove;
            player.WorldData.NoClip = previous.NoClip;
            player.BroadcastPlayerData(false);
            ghostStates.Remove(player.PlayerUID);
            BroadcastGhostVisibility(player, false);
            PacketGuardTrustedState.Mark(player, "admin-ghost-exit", 2500, PacketGuardTrustScope.All);
            Audit(player.PlayerName, "ghost-off", player.PlayerUID, player.PlayerName, $"Restored {previous.GameMode} mode", source);
            return $"Ghost mode disabled. Restored {previous.GameMode}.";
        }

        if (player.WorldData.CurrentGameMode == EnumGameMode.Spectator)
            return "You are already in spectator mode outside the suite. Ghost mode was not changed.";

        ghostStates[player.PlayerUID] = new AdminGhostState
        {
            GameMode = player.WorldData.CurrentGameMode,
            FreeMove = player.WorldData.FreeMove,
            NoClip = player.WorldData.NoClip
        };

        player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
        player.WorldData.FreeMove = true;
        player.WorldData.NoClip = true;
        player.BroadcastPlayerData(false);
        BroadcastGhostVisibility(player, true);
        PacketGuardTrustedState.Mark(player, "admin-ghost-enter", 2500, PacketGuardTrustScope.All);
        Audit(player.PlayerName, "ghost-on", player.PlayerUID, player.PlayerName, "Entered spectator/free-move/no-clip ghost mode", source);
        return "Ghost mode enabled. Use /ghost again or the Ghost button to restore your previous game mode.";
    }

    private void BroadcastGhostVisibility(IServerPlayer ghost, bool isGhosted, IServerPlayer? onlyTo = null)
    {
        if (ghost?.Entity == null || string.IsNullOrWhiteSpace(ghost.PlayerUID)) return;

        var channel = api.Network.GetChannel(GhostVisibilityProtocol.ChannelName);
        if (channel == null) return;

        EntityPos? pos = GetRuntimeEntityPos(ghost.Entity);
        var packet = new GhostVisibilityPacket
        {
            EntityId = ghost.Entity.EntityId,
            PlayerUid = ghost.PlayerUID,
            IsGhosted = isGhosted,
            X = pos?.X ?? 0,
            Y = pos?.Y ?? 0,
            Z = pos?.Z ?? 0,
            Dimension = pos?.Dimension ?? 0,
            Yaw = pos?.Yaw ?? 0,
            Pitch = pos?.Pitch ?? 0
        };

        IEnumerable<IServerPlayer> recipients = onlyTo != null ? new[] { onlyTo } : OnlinePlayers();
        foreach (IServerPlayer recipient in recipients)
        {
            if (recipient == null || string.IsNullOrWhiteSpace(recipient.PlayerUID)) continue;
            if (string.Equals(recipient.PlayerUID, ghost.PlayerUID, StringComparison.OrdinalIgnoreCase)) continue;

            // Client Integrity 1.1.26 bound directly to Entity.Pos in RestoreGhostEntity. On VS 1.22.7
            // that can JIT-fail with MissingFieldException and crash every recipient simultaneously.
            // Send the visibility packet only after the client advertises the runtime-safe capability.
            // Legacy clients merely continue rendering the ghosted admin; they are never crashed.
            if (!GhostVisibilityCompatibilityRegistry.IsSafe(recipient.PlayerUID))
            {
                api.Logger.Debug("[ServerEssentials/Admin] Ghost visibility suppressed for {0}: client has not advertised the safe ghost capability.", recipient.PlayerName);
                continue;
            }

            try { channel.SendPacket(packet, recipient); }
            catch (Exception ex)
            {
                api.Logger.Debug("[ServerEssentials/Admin] Ghost visibility packet to {0} was not ready: {1}", recipient.PlayerName, ex.Message);
            }
        }
    }

    private void SendExistingGhostsTo(IServerPlayer recipient)
    {
        if (recipient == null || string.IsNullOrWhiteSpace(recipient.PlayerUID) || ghostStates.Count == 0) return;
        foreach (string ghostUid in ghostStates.Keys.ToArray())
        {
            IServerPlayer? ghost = FindOnlinePlayer(ghostUid);
            if (ghost?.Entity == null) continue;
            BroadcastGhostVisibility(ghost, true, recipient);
        }
    }

    private void RestoreGhostOnDisconnect(IServerPlayer player)
    {
        if (player?.WorldData == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        if (!ghostStates.TryGetValue(player.PlayerUID, out AdminGhostState? previous)) return;

        player.WorldData.CurrentGameMode = previous.GameMode;
        player.WorldData.FreeMove = previous.FreeMove;
        player.WorldData.NoClip = previous.NoClip;
        ghostStates.Remove(player.PlayerUID);
    }

    public string MapTeleport(IServerPlayer actor, BlockPos target, string source = "worldmap")
    {
        if (actor.Entity == null) return "Player must be online.";
        var actorPos = GetRuntimeEntityPos(actor.Entity);
        if (actorPos == null) return "Unable to read player position.";

        adminBackPositions[actor.PlayerUID] = actorPos.Copy();
        playerCommands?.Teleports.RecordTeleportOrigin(actor, actorPos.Copy(), "admin map teleport");
        PacketGuardTrustedState.Mark(actor, "suite-map-teleport");
        actor.Entity.TeleportTo(target.Copy());
        Audit(actor.PlayerName, "mapteleport", actor.PlayerUID, actor.PlayerName,
            $"Teleported from world map to {target.X}, {target.Y}, {target.Z} (dim {target.dimension})", source);
        return $"Teleported to map position {target.X}, {target.Y}, {target.Z}. Use /back to return.";
    }

    public string Goto(IServerPlayer actor, string targetName)
    {
        var target = FindOnlinePlayer(targetName);
        if (actor.Entity == null || target?.Entity == null) return "Both players must be online.";
        var actorPos = GetRuntimeEntityPos(actor.Entity);
        var targetPos = GetRuntimeEntityPos(target.Entity);
        if (actorPos == null || targetPos == null) return "Unable to read player position.";
        adminBackPositions[actor.PlayerUID] = actorPos.Copy();
        playerCommands?.Teleports.RecordTeleportOrigin(actor, actorPos.Copy(), "admin teleport");
        PacketGuardTrustedState.Mark(actor, "suite-admin-teleport");
        actor.Entity.TeleportTo(targetPos.Copy());
        Audit(actor.PlayerName, "goto", target.PlayerUID, target.PlayerName, "Teleported to player", "ingame");
        return $"Teleported to {target.PlayerName}.";
    }

    public string Bring(IServerPlayer actor, string targetName)
    {
        var target = FindOnlinePlayer(targetName);
        if (actor.Entity == null || target?.Entity == null) return "Both players must be online.";
        var actorPos = GetRuntimeEntityPos(actor.Entity);
        var targetPos = GetRuntimeEntityPos(target.Entity);
        if (actorPos == null || targetPos == null) return "Unable to read player position.";
        playerBackPositions[target.PlayerUID] = targetPos.Copy();
        playerCommands?.Teleports.RecordTeleportOrigin(target, targetPos.Copy(), "admin bring");
        PacketGuardTrustedState.Mark(target, "suite-admin-teleport");
        target.Entity.TeleportTo(actorPos.Copy());
        Audit(actor.PlayerName, "bring", target.PlayerUID, target.PlayerName, "Brought player to admin", "ingame");
        return $"Brought {target.PlayerName} to you.";
    }

    public string Back(IServerPlayer actor)
    {
        if (actor.Entity == null || !adminBackPositions.TryGetValue(actor.PlayerUID, out var pos)) return "No saved admin teleport position.";
        var currentPos = GetRuntimeEntityPos(actor.Entity)?.Copy();
        PacketGuardTrustedState.Mark(actor, "suite-admin-teleport");
        actor.Entity.TeleportTo(pos.Copy());
        if (currentPos != null) playerCommands?.Teleports.RecordTeleportOrigin(actor, currentPos, "admin back");
        adminBackPositions.Remove(actor.PlayerUID);
        Audit(actor.PlayerName, "back", actor.PlayerUID, actor.PlayerName, "Returned to saved admin teleport position", "ingame");
        return "Returned to your previous position.";
    }

    public string SendBack(string actor, string targetName, string source = "ingame")
    {
        var target = FindOnlinePlayer(targetName);
        if (target?.Entity == null || !playerBackPositions.TryGetValue(target.PlayerUID, out var pos)) return "No saved position for that player.";
        var currentPos = GetRuntimeEntityPos(target.Entity)?.Copy();
        if (currentPos != null) playerCommands?.Teleports.RecordTeleportOrigin(target, currentPos, "admin sendback");
        PacketGuardTrustedState.Mark(target, "suite-admin-teleport");
        target.Entity.TeleportTo(pos.Copy());
        playerBackPositions.Remove(target.PlayerUID);
        Audit(actor, "sendback", target.PlayerUID, target.PlayerName, "Returned player to saved position", source);
        return $"Returned {target.PlayerName}.";
    }

    public bool IsJailed(string? playerUid) => jailService?.IsJailed(playerUid) == true;

    public string SetJailPosition(IServerPlayer actor, string source = "ingame")
        => jailService?.SetJailPosition(actor, source) ?? "Jail service is unavailable.";

    public string Jail(string actor, string targetName, string duration, string reason = "", string source = "ingame")
        => jailService?.JailPlayer(actor, targetName, duration, reason, source) ?? "Jail service is unavailable.";

    public string Unjail(string actor, string targetName, string reason = "", string source = "ingame")
        => jailService?.UnjailPlayer(actor, targetName, reason, source) ?? "Jail service is unavailable.";

    public string JailInfo(string targetName)
        => jailService?.GetInfo(targetName) ?? "Jail service is unavailable.";

    public string Smite(string actor, string targetName, string reason = "", string source = "ingame")
    {
        var target = FindOnlinePlayer(targetName);
        if (target?.Entity == null) return "Player must be online.";
        var pos = GetRuntimeEntityPos(target.Entity);
        if (pos == null) return "Unable to read player position.";

        try
        {
            var weather = api.ModLoader.GetModSystem<WeatherSystemServer>();
            if (weather == null) return "Vintage Story weather system is unavailable.";
            weather.SpawnLightningFlash(pos.XYZ);
            string detail = string.IsNullOrWhiteSpace(reason)
                ? "Struck player with lightning"
                : $"Struck player with lightning. Reason: {reason.Trim()}";
            Audit(actor, "smite", target.PlayerUID, target.PlayerName, detail, source);
            return $"Smote {target.PlayerName} with lightning.";
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/Admin] Smite failed for {0}: {1}", target.PlayerName, ex);
            return $"Smite failed: {ex.Message}";
        }
    }

    public LagHereReport ScanLagHere(IServerPlayer player, int radiusChunks)
    {
        radiusChunks = Math.Clamp(radiusChunks, 1, 12);
        double radius = radiusChunks * GlobalConstants.ChunkSize;
        var center = GetRuntimeEntityPos(player.Entity)?.XYZ ?? new Vec3d();
        var entities = api.World.LoadedEntities.Values
            .Where(e =>
            {
                var pos = GetRuntimeEntityPos(e);
                return pos != null && pos.XYZ.SquareDistanceTo(center) <= radius * radius;
            })
            .ToList();
        var health = getHealth()?.Current;
        return new LagHereReport
        {
            RadiusChunks = radiusChunks,
            Entities = entities.Count,
            DroppedItems = entities.Count(e => e is EntityItem),
            Players = entities.Count(e => e is EntityPlayer),
            LoadedChunks = health?.LoadedChunks ?? 0,
            MainThreadDelayMs = health?.MainThreadDelayMs ?? 0,
            CpuPercent = health?.CpuPercent ?? 0,
            BlockLoggerQueue = health?.BlockLoggerQueue ?? 0,
            TopEntityTypes = entities.GroupBy(e => e.Code?.ToString() ?? e.GetType().Name)
                .OrderByDescending(g => g.Count()).Take(10)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public CleanupReport ScanCleanup()
    {
        var entities = api.World.LoadedEntities.Values.ToList();
        return new CleanupReport
        {
            LoadedEntities = entities.Count,
            Creatures = entities.Count(e => e.IsCreature),
            InanimateEntities = entities.Count(e => !e.IsCreature),
            DeadOrDespawning = entities.Count(e => !e.Alive || e.ShouldDespawn),
            DroppedItems = entities.Count(e => e is EntityItem),
            Removed = 0,
            DryRun = true
        };
    }

    public CleanupReport CleanupDrops(bool dryRun, int maxRemove = int.MaxValue)
    {
        var report = ScanCleanup();
        var items = api.World.LoadedEntities.Values.OfType<EntityItem>().Take(Math.Max(0, maxRemove)).ToList();
        if (!dryRun)
        {
            foreach (var entity in items) entity.Die(EnumDespawnReason.Removed);
        }
        report.DroppedItems = items.Count;
        report.Removed = dryRun ? 0 : items.Count;
        report.DryRun = dryRun;
        return report;
    }

    public string SetMaintenance(string actor, bool enabled, string reason, long durationMilliseconds = 0, string source = "ingame")
    {
        lock (gate)
        {
            bool oldEnabled = state.MaintenanceMode;
            string oldReason = state.MaintenanceReason;
            long oldUntil = state.MaintenanceUntilUnixMilliseconds;
            state.MaintenanceMode = enabled;
            state.MaintenanceReason = string.IsNullOrWhiteSpace(reason) ? "Maintenance" : reason.Trim();
            state.MaintenanceUntilUnixMilliseconds = enabled && durationMilliseconds > 0 ? DeadlineAfterMilliseconds(durationMilliseconds) : 0;
            AddAuditUnsafe(actor, enabled ? "maintenance-on" : "maintenance-off", "", "server", state.MaintenanceReason, source);
            SaveStateUnsafe();
            PushUndo("maintenance mode change", undoActor => RestoreMaintenance(undoActor, oldEnabled, oldReason, oldUntil));
        }
        api.BroadcastMessageToAllGroups(enabled ? $"Server maintenance mode enabled: {state.MaintenanceReason}" : "Server maintenance mode disabled.", EnumChatType.Notification);
        return enabled ? "Maintenance mode enabled." : "Maintenance mode disabled.";
    }

    public (bool Enabled, string Reason, long Until) GetMaintenance()
    {
        lock (gate) return (state.MaintenanceMode, state.MaintenanceReason, state.MaintenanceUntilUnixMilliseconds);
    }

    public string ScheduleShutdown(string actor, long delayMilliseconds, string reason, string source = "ingame")
    {
        shutdownAtUnixMilliseconds = DeadlineAfterMilliseconds(delayMilliseconds);
        shutdownReason = string.IsNullOrWhiteSpace(reason) ? "Scheduled shutdown" : reason.Trim();
        lastShutdownAnnouncementBucket = long.MinValue;
        Audit(actor, "shutdown-scheduled", "", "server", $"In {FormatDuration(delayMilliseconds)}: {shutdownReason}", source);
        return $"Shutdown scheduled in {FormatDuration(delayMilliseconds)}.";
    }

    public string ScheduleRestart(string actor, long delayMilliseconds, string reason, string source = "ingame")
    {
        string detail = string.IsNullOrWhiteSpace(reason) ? "Restart requested" : reason.Trim();
        string result = ScheduleShutdown(actor, delayMilliseconds, "Restart requested: " + detail, source);
        Audit(actor, "restart-requested", "", "server",
            "Clean shutdown scheduled; an external service/supervisor must restart the server process.", source);
        return result + " An external service/supervisor must restart the server process after shutdown.";
    }

    public string CancelShutdown(string actor, string source = "ingame")
    {
        shutdownAtUnixMilliseconds = 0;
        Audit(actor, "shutdown-cancelled", "", "server", "Scheduled shutdown cancelled", source);
        api.BroadcastMessageToAllGroups("Scheduled server shutdown cancelled.", EnumChatType.Notification);
        return "Scheduled shutdown cancelled.";
    }

    public (long At, string Reason) GetShutdown() => (shutdownAtUnixMilliseconds, shutdownReason);

    public bool IsMuted(string uid, out PlayerAdminRecord? record)
    {
        lock (gate)
        {
            if (!state.Players.TryGetValue(uid, out record)) return false;
            if (record.PermanentMute) return true;
            if (record.MuteUntilUnixMilliseconds > NowMs()) return true;
            if (record.MuteUntilUnixMilliseconds > 0)
            {
                record.MuteUntilUnixMilliseconds = 0; record.MuteReason = ""; record.MutedBy = "";
                SaveStateUnsafe();
            }
            return false;
        }
    }

    public static bool IsSuccessfulActionResult(string action, string? result)
    {
        action = (action ?? "").Trim().ToLowerInvariant();
        result = (result ?? "").Trim();
        if (result.Length == 0) return false;

        return action switch
        {
            "warn" => result.StartsWith("Warning #", StringComparison.OrdinalIgnoreCase) && result.Contains(" issued to ", StringComparison.OrdinalIgnoreCase),
            "unwarn" => result.StartsWith("Warning #", StringComparison.OrdinalIgnoreCase) && result.Contains(" removed from ", StringComparison.OrdinalIgnoreCase),
            "mute" => result.Contains(" muted for ", StringComparison.OrdinalIgnoreCase),
            "unmute" => result.EndsWith(" unmuted.", StringComparison.OrdinalIgnoreCase),
            "tempban" => result.Contains(" temporarily banned for ", StringComparison.OrdinalIgnoreCase),
            "unban" => result.StartsWith("Temporary ban cleared for ", StringComparison.OrdinalIgnoreCase),
            "freeze" => result.Contains(" frozen for ", StringComparison.OrdinalIgnoreCase),
            "unfreeze" => result.EndsWith(" unfrozen.", StringComparison.OrdinalIgnoreCase),
            "kick" => result.Contains(" kick scheduled with player-facing reason.", StringComparison.OrdinalIgnoreCase),
            "heal" => result.EndsWith(" healed to full health.", StringComparison.OrdinalIgnoreCase),
            "feed" => result.EndsWith(" fed.", StringComparison.OrdinalIgnoreCase),
            "revive" => result.EndsWith(" revived.", StringComparison.OrdinalIgnoreCase),
            "ghost" => result.StartsWith("Ghost mode enabled.", StringComparison.OrdinalIgnoreCase) || result.StartsWith("Ghost mode disabled.", StringComparison.OrdinalIgnoreCase),
            "goto" => result.StartsWith("Teleported to ", StringComparison.OrdinalIgnoreCase),
            "bring" => result.StartsWith("Brought ", StringComparison.OrdinalIgnoreCase),
            "sendback" => result.StartsWith("Returned ", StringComparison.OrdinalIgnoreCase),
            "smite" => result.StartsWith("Smote ", StringComparison.OrdinalIgnoreCase),
            "setjail" => result.StartsWith("Jail position set ", StringComparison.OrdinalIgnoreCase),
            "jail" => result.StartsWith("Jailed ", StringComparison.OrdinalIgnoreCase),
            "unjail" => result.StartsWith("Released ", StringComparison.OrdinalIgnoreCase),
            "note" => result.StartsWith("Staff note #", StringComparison.OrdinalIgnoreCase) && result.Contains(" added to ", StringComparison.OrdinalIgnoreCase),
            "note-remove" => result.StartsWith("Staff note #", StringComparison.OrdinalIgnoreCase) && result.EndsWith(" removed.", StringComparison.OrdinalIgnoreCase),
            "inventory-snapshot" => result.StartsWith("Inventory snapshot #", StringComparison.OrdinalIgnoreCase),
            "inventory-restore" => result.StartsWith("Restored inventory snapshot #", StringComparison.OrdinalIgnoreCase),
            "inventory-remove" => result.StartsWith("Removed ", StringComparison.OrdinalIgnoreCase) && result.Contains("Snapshot #", StringComparison.OrdinalIgnoreCase),
            "inventory-quantity" => result.StartsWith("Changed ", StringComparison.OrdinalIgnoreCase) && result.Contains("Snapshot #", StringComparison.OrdinalIgnoreCase),
            "inventory-move" => result.StartsWith("Moved/swapped inventory slots.", StringComparison.OrdinalIgnoreCase),
            "inventory-give" => result.StartsWith("Gave ", StringComparison.OrdinalIgnoreCase) && result.Contains("Snapshot #", StringComparison.OrdinalIgnoreCase),
            "inventory-replace" => result.StartsWith("Replaced ", StringComparison.OrdinalIgnoreCase) && result.Contains("Snapshot #", StringComparison.OrdinalIgnoreCase),
            "maintenance" => result.StartsWith("Maintenance mode enabled.", StringComparison.OrdinalIgnoreCase) || result.StartsWith("Maintenance mode disabled.", StringComparison.OrdinalIgnoreCase),
            "shutdown" => result.StartsWith("Shutdown scheduled in ", StringComparison.OrdinalIgnoreCase),
            "restart" => result.StartsWith("Shutdown scheduled in ", StringComparison.OrdinalIgnoreCase),
            "shutdown-cancel" => result.StartsWith("Scheduled shutdown cancelled.", StringComparison.OrdinalIgnoreCase),
            "undo" => result.StartsWith("Undid ", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public string ExecuteWebAction(string actor, string action, string player, string duration, string reason, long id = 0, string note = "")
    {
        action = (action ?? "").Trim().ToLowerInvariant();
        return action switch
        {
            "warn" => Warn(actor, player, reason, "web"),
            "unwarn" => RemoveWarning(actor, player, id, "web"),
            "mute" => Mute(actor, player, duration, reason, "web"),
            "unmute" => Unmute(actor, player, "web"),
            "tempban" => TempBan(actor, player, duration, reason, "web"),
            "unban" => Unban(actor, player, "web"),
            "freeze" => Freeze(actor, player, duration, reason, "web"),
            "unfreeze" => Unfreeze(actor, player, "web"),
            "kick" => Kick(actor, player, reason, "web"),
            "heal" => Heal(actor, player, "web"),
            "feed" => Feed(actor, player, "web"),
            "revive" => Revive(actor, player, "web"),
            "smite" => Smite(actor, player, reason, "web"),
            "jail" => Jail(actor, player, duration, reason, "web"),
            "unjail" => Unjail(actor, player, reason, "web"),
            "note" => AddNote(actor, player, note, "web"),
            "note-remove" => RemoveNote(actor, player, id, "web"),
            "inventory-remove" => RemoveInventorySlot(actor, player, note, (int)id, "web"),
            "sendback" => SendBack(actor, player, "web"),
            "undo" => Undo(actor, "web"),
            _ => "Unknown admin action."
        };
    }

    public string Undo(string actor, string source = "ingame")
    {
        AdminUndoEntry? entry;
        lock (undoGate)
        {
            if (undoStack.Count == 0) return "Nothing reversible to undo.";
            entry = undoStack.Pop();
        }
        performingUndo = true;
        try
        {
            string result = entry.Reverse(actor);
            Audit(actor, "undo", "", "server", $"Undid {entry.Description}: {result}", source);
            return $"Undid {entry.Description}. {result}";
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/Admin] Undo failed: {0}", ex.Message);
            return $"Undo failed: {ex.Message}";
        }
        finally { performingUndo = false; }
    }

    public string GetUndoDescription()
    {
        lock (undoGate) return undoStack.Count == 0 ? "" : undoStack.Peek().Description;
    }

    private void PushUndo(string description, System.Func<string, string> reverse)
    {
        if (performingUndo) return;
        lock (undoGate)
        {
            undoStack.Push(new AdminUndoEntry(description, reverse));
            while (undoStack.Count > 50)
            {
                var keep = undoStack.Take(50).Reverse().ToArray();
                undoStack.Clear();
                foreach (var item in keep) undoStack.Push(item);
            }
        }
    }

    private string RestoreMute(string actor, string uid, string name, bool permanent, long until, string reason, string by)
    {
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            rec.PermanentMute = permanent; rec.MuteUntilUnixMilliseconds = until; rec.MuteReason = reason; rec.MutedBy = by;
            AddAuditUnsafe(actor, "mute-restore", uid, name, "Restored previous mute state", "undo");
            SaveStateUnsafe();
        }
        return $"Previous mute state restored for {name}.";
    }

    private string RestoreBan(string actor, string uid, string name, long until, string reason, string by)
    {
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            rec.BanUntilUnixMilliseconds = until; rec.BanReason = reason; rec.BannedBy = by;
            AddAuditUnsafe(actor, "ban-restore", uid, name, "Restored previous temporary-ban state", "undo");
            SaveStateUnsafe();
        }
        return $"Previous temporary-ban state restored for {name}.";
    }

    private string RestoreFreeze(string actor, string uid, string name, bool frozen, long until, string by, string reason, EntityPos? pos)
    {
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(uid, name);
            rec.Frozen = frozen; rec.FreezeUntilUnixMilliseconds = until; rec.FrozenBy = by; rec.FreezeReason = reason;
            if (frozen && pos != null) frozenPositions[uid] = pos.Copy(); else frozenPositions.Remove(uid);
            AddAuditUnsafe(actor, "freeze-restore", uid, name, "Restored previous freeze state", "undo");
            SaveStateUnsafe();
        }
        RefreshFreezeTickListener();
        return $"Previous freeze state restored for {name}.";
    }

    private string RestoreInventorySlot(string actor, string uid, string inventoryId, int slotIndex, ItemStack backup)
    {
        var player = FindOnlinePlayer(uid);
        if (player == null) return "Player is offline; inventory undo could not be applied.";
        var inventory = player.InventoryManager.InventoriesOrdered.FirstOrDefault(inv =>
            IsManagedPlayerInventory(inv) &&
            string.Equals(inv.InventoryID, inventoryId, StringComparison.OrdinalIgnoreCase));
        if (inventory == null || slotIndex < 0 || slotIndex >= inventory.Count) return "Inventory slot is no longer available.";
        var slot = inventory[slotIndex];
        if (slot.Itemstack != null) return "Inventory slot is no longer empty; undo refused to avoid overwriting an item.";
        slot.Itemstack = backup.Clone();
        slot.MarkDirty();
        Audit(actor, "inventory-restore", player.PlayerUID, player.PlayerName,
            $"Restored {slot.Itemstack.StackSize}x {slot.Itemstack.Collectible?.Code} to {inventoryId}[{slotIndex}]", "undo");
        return $"Inventory slot restored for {player.PlayerName}.";
    }

    private string RestoreMaintenance(string actor, bool enabled, string reason, long until)
    {
        lock (gate)
        {
            state.MaintenanceMode = enabled; state.MaintenanceReason = reason; state.MaintenanceUntilUnixMilliseconds = until;
            AddAuditUnsafe(actor, "maintenance-restore", "", "server", "Restored previous maintenance state", "undo");
            SaveStateUnsafe();
        }
        return "Previous maintenance state restored.";
    }

    public void Audit(string actor, string action, string uid, string name, string detail, string source)
    {
        lock (gate)
        {
            AddAuditUnsafe(actor, action, uid, name, detail, source);
            SaveStateUnsafe();
        }
    }

    private void RegisterPrivileges()
    {
        api.Permissions.RegisterPrivilege(PrivView, "View Newfies Server Essentials player administration data", true);
        api.Permissions.RegisterPrivilege(PrivManage, "Manage players with Newfies Server Essentials", true);
        api.Permissions.RegisterPrivilege(PrivWarn, "Issue and remove staff warnings", true);
        api.Permissions.RegisterPrivilege(PrivMute, "Mute and unmute players", true);
        api.Permissions.RegisterPrivilege(PrivBan, "Temporarily ban and unban players", true);
        api.Permissions.RegisterPrivilege(PrivInventory, "View player inventories", true);
        api.Permissions.RegisterPrivilege(PrivInventoryEdit, "Remove items from player inventories with explicit auditing", true);
        api.Permissions.RegisterPrivilege(PrivTeleport, "Use Newfies Server Essentials teleport helpers", true);
        api.Permissions.RegisterPrivilege(PrivCleanup, "Run Newfies Server Essentials cleanup actions", true);
        api.Permissions.RegisterPrivilege(PrivDiagnostics, "Run Newfies Server Essentials local diagnostics", true);
        api.Permissions.RegisterPrivilege(PrivServer, "Manage maintenance and scheduled shutdowns", true);
        api.Permissions.RegisterPrivilege(PrivReports, "Manage issue reports and tickets", true);
        api.Permissions.RegisterPrivilege(PrivSmite, "Strike players with lightning", true);
        api.Permissions.RegisterPrivilege(PrivJail, "Set the jail cell and jail/release players", true);
    }

    private void RegisterCommands() => new AdminCommands(api, this, reports, playerCommands, getAutomatedRestarts).Register();

    private void OnPlayerJoin(IServerPlayer player)
    {
        IndexOnlinePlayer(player);
        TouchPlayer(player);
        if (TryGetActiveBan(player.PlayerUID, out var rec))
        {
            long remaining = Math.Max(0, rec!.BanUntilUnixMilliseconds - NowMs());
            string banMessage = $"Temporarily banned. Remaining: {FormatDuration(remaining)}. Reason: {rec.BanReason}";
            if (!PlayerDisconnectCompat.DisconnectWithReason(player, $"{player.PlayerName} was disconnected (temporary ban).", banMessage, out _))
                player.Disconnect(banMessage);
            return;
        }
        if (GetMaintenance().Enabled && !IsStaff(player))
        {
            string maintenanceMessage = $"Server is in maintenance mode: {GetMaintenance().Reason}";
            if (!PlayerDisconnectCompat.DisconnectWithReason(player, $"{player.PlayerName} was disconnected (maintenance).", maintenanceMessage, out _))
                player.Disconnect(maintenanceMessage);
        }
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        offlineInventoryReader.Invalidate(player.PlayerUID);
        decodedInventoryCache.Remove(player.PlayerUID);
        IndexOnlinePlayer(player);
        TouchPlayer(player);
        BeginSession(player);
        var pos = GetRuntimeEntityPos(player.Entity);
        if (pos != null)
        {
            lock (gate)
                activity[player.PlayerUID] = new PlayerActivityState(pos.Copy(), NowMs());
            if (IsFrozen(player.PlayerUID)) frozenPositions[player.PlayerUID] = pos.Copy();
        }
        RefreshFreezeTickListener();

        // New clients may join while one or more admins are already ghosted. Send the current
        // visibility set immediately and retry briefly while custom channels finish negotiating.
        SendExistingGhostsTo(player);
        api.Event.RegisterCallback(_ => SendExistingGhostsTo(player), 500);
        api.Event.RegisterCallback(_ => SendExistingGhostsTo(player), 1500);
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        CaptureOfflineInventory(player, "disconnect-capture");
        RestoreGhostOnDisconnect(player);
        RemoveOnlinePlayer(player);
        lock (gate) activity.Remove(player.PlayerUID);
        frozenPositions.Remove(player.PlayerUID);
        // Back/return positions are session-scoped. Keeping them after disconnect both allows
        // stale teleports after a later reconnect and makes these UID-keyed dictionaries grow
        // with every distinct player who ever used teleport tools.
        adminBackPositions.Remove(player.PlayerUID);
        playerBackPositions.Remove(player.PlayerUID);
        RefreshFreezeTickListener();
        EndSession(player);
    }

    private string ResolveChatDisplayName(IServerPlayer player)
    {
        var tree=new TreeAttribute();tree.SetString("playerUid",player.PlayerUID);
        try {api.Event.PushEvent("newfiesse:chat-sender-query",tree);}
        catch {return player.PlayerName;}
        return tree.GetBool("available",false)?tree.GetString("displayName",player.PlayerName):player.PlayerName;
    }

    private void OnPlayerChat(IServerPlayer player, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if(player==null || consumed.value)return;
        MarkActivity(player);
        if(ChatMessageBoundary.Parse(message,player.PlayerName,ResolveChatDisplayName(player),player.Role?.Name).IsCommand)return;
        if (!IsMuted(player.PlayerUID, out var rec)) return;
        consumed.SetValue(true);
        string remain = rec!.PermanentMute ? "permanent" : FormatDuration(Math.Max(0, rec.MuteUntilUnixMilliseconds - NowMs()));
        player.SendMessage(channelId, $"You are muted ({remain}). Reason: {rec.MuteReason}", EnumChatType.CommandError);
    }

    private bool OnCanPlaceOrBreakBlock(IServerPlayer player, BlockSelection blockSel, out string claimant)
    {
        claimant = "";
        if (jailService?.IsJailed(player.PlayerUID) == true)
        {
            claimant = "custommessage-nse-jailed";
            return false;
        }
        if (!IsFrozen(player.PlayerUID)) return true;
        claimant = "custommessage-nse-frozen";
        return false;
    }

    private bool OnCanUseBlock(IServerPlayer player, BlockSelection blockSel)
        => !IsFrozen(player.PlayerUID) && !IsJailed(player.PlayerUID);


    private void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
    {
        if (byPlayer is IServerPlayer serverPlayer && (IsFrozen(serverPlayer.PlayerUID) || IsJailed(serverPlayer.PlayerUID)))
            handling = EnumHandling.PreventDefault;
    }

    private bool IsFrozen(string uid)
    {
        lock (gate) return state.Players.TryGetValue(uid, out var rec) && rec.Frozen;
    }

    private void OnTick(float dt)
    {
        if (disposed) return;
        try
        {
            OnTickCore(dt);
            suppressedTickErrors = 0;
        }
        catch (Exception ex)
        {
            // Never allow an admin utility tick failure to become a server-wide exception storm.
            long now = NowMs();
            suppressedTickErrors++;
            if (now - lastTickErrorLogUnixMilliseconds >= 5000)
            {
                int suppressed = Math.Max(0, suppressedTickErrors - 1);
                api.Logger.Error("[ServerEssentials/Admin] Tick failure contained (suppressed since last log={0}): {1}", suppressed, ex);
                lastTickErrorLogUnixMilliseconds = now;
                suppressedTickErrors = 0;
            }
        }
    }

    private void OnTickCore(float dt)
    {
        long now = NowMs();

        // Timed moderation/maintenance expiry does not need a 1 Hz scan. Freeze enforcement
        // has a separate 250 ms listener that exists only while somebody is actually frozen.
        if (now - lastExpiryScanUnixMilliseconds >= 5000)
        {
            lastExpiryScanUnixMilliseconds = now;
            List<string> unfreeze = new();
            lock (gate)
            {
                foreach (var kv in state.Players)
                    if (kv.Value.Frozen && kv.Value.FreezeUntilUnixMilliseconds > 0 && kv.Value.FreezeUntilUnixMilliseconds <= now)
                        unfreeze.Add(kv.Key);

                if (state.MaintenanceMode && state.MaintenanceUntilUnixMilliseconds > 0 && state.MaintenanceUntilUnixMilliseconds <= now)
                {
                    state.MaintenanceMode = false;
                    state.MaintenanceUntilUnixMilliseconds = 0;
                    AddAuditUnsafe("system", "maintenance-off", "", "server", "Timed maintenance expired", "system");
                    SaveStateUnsafe();
                }
            }
            foreach (var uid in unfreeze) Unfreeze("system", uid, "system");
        }

        // AFK/movement activity only needs one sample per second. Frozen players are enforced
        // by the temporary fast listener, not this maintenance pass.
        if (now - lastActivitySampleUnixMilliseconds >= 1000)
        {
            lastActivitySampleUnixMilliseconds = now;
            foreach (var player in OnlinePlayers()) UpdateMovementActivity(player, now);
        }

        TickShutdown(now);
    }

    private void RefreshFreezeTickListener()
    {
        if (disposed) return;
        bool needsFastTick = frozenPositions.Count > 0;
        if (needsFastTick && freezeTickListenerId < 0)
        {
            freezeTickListenerId = api.Event.RegisterGameTickListener(OnFreezeTick, 250);
        }
        else if (!needsFastTick && freezeTickListenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(freezeTickListenerId);
            freezeTickListenerId = -1;
        }
    }

    private void OnFreezeTick(float dt)
    {
        if (disposed || frozenPositions.Count == 0)
        {
            RefreshFreezeTickListener();
            return;
        }

        try
        {
            foreach (var entry in frozenPositions.ToArray())
            {
                var player = FindOnlinePlayer(entry.Key);
                if (player?.Entity == null) continue;
                var currentPos = GetRuntimeEntityPos(player.Entity);
                if (currentPos == null) continue;
                if (currentPos.SquareDistanceTo(entry.Value) > 0.01)
                {
                    PacketGuardTrustedState.Mark(player, "suite-admin-teleport");
                    player.Entity.TeleportTo(entry.Value.Copy());
                }
                currentPos.Motion.Set(0, 0, 0);
            }
        }
        catch (Exception ex)
        {
            long now = NowMs();
            if (now - lastTickErrorLogUnixMilliseconds >= 5000)
            {
                api.Logger.Error("[ServerEssentials/Admin] Freeze enforcement failure contained: {0}", ex);
                lastTickErrorLogUnixMilliseconds = now;
            }
        }
    }

    private void TickShutdown(long now)
    {
        if (shutdownAtUnixMilliseconds <= 0) return;
        long remaining = shutdownAtUnixMilliseconds - now;
        if (remaining <= 0)
        {
            shutdownAtUnixMilliseconds = 0;
            bool restart = IsRestartShutdown();
            string finalShutdownMessage = restart ? "Server is restarting now" : $"Server shutting down: {shutdownReason}";
            api.BroadcastMessageToAllGroups(restart ? FormatRestartChatNotice(finalShutdownMessage) : finalShutdownMessage, EnumChatType.Notification);
            api.Server.ShutDown();
            return;
        }

        long bucket = remaining switch
        {
            <= 10_000 => remaining / 1_000,
            <= 60_000 => remaining / 10_000,
            <= 5 * 60_000 => remaining / 60_000,
            <= 10 * 60_000 => remaining / (5 * 60_000),
            _ => long.MaxValue
        };
        if (bucket == lastShutdownAnnouncementBucket) return;
        lastShutdownAnnouncementBucket = bucket;
        if (remaining <= 10 * 60_000)
        {
            bool restart = IsRestartShutdown();
            string countdownMessage = restart
                ? BuildRestartCountdownText(remaining)
                : $"Server shutdown in {FormatDuration(remaining)}: {shutdownReason}";
            api.BroadcastMessageToAllGroups(restart ? FormatRestartChatNotice(countdownMessage) : countdownMessage, EnumChatType.Notification);
        }
    }

    private bool IsRestartShutdown() =>
        shutdownReason.StartsWith("Restart requested:", StringComparison.OrdinalIgnoreCase);

    private static string BuildRestartCountdownText(long remainingMilliseconds)
    {
        if (remainingMilliseconds >= 60_000)
        {
            int minutes = Math.Max(1, (int)Math.Ceiling(remainingMilliseconds / 60_000d));
            return $"Server is restarting in {minutes} minute{(minutes == 1 ? "" : "s")}";
        }

        int seconds = Math.Max(1, (int)Math.Ceiling(remainingMilliseconds / 1000d));
        return $"Server is restarting in {seconds} second{(seconds == 1 ? "" : "s")}";
    }

    private static string FormatRestartChatNotice(string message) =>
        $"<font color=\"#ffd166\"><strong>{WebUtility.HtmlEncode(message ?? string.Empty)}</strong></font>";

    private bool TryGetActiveBan(string uid, out PlayerAdminRecord? rec)
    {
        lock (gate)
        {
            if (!state.Players.TryGetValue(uid, out rec)) return false;
            if (rec.BanUntilUnixMilliseconds > NowMs()) return true;
            if (rec.BanUntilUnixMilliseconds > 0)
            {
                rec.BanUntilUnixMilliseconds = 0; rec.BanReason = ""; rec.BannedBy = "";
                SaveStateUnsafe();
            }
            return false;
        }
    }

    private void BeginSession(IServerPlayer player)
    {
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(player.PlayerUID, player.PlayerName);
            if (rec.SessionStartedUnixMilliseconds == 0)
            {
                rec.SessionStartedUnixMilliseconds = NowMs();
                AddAuditUnsafe("system", "login", player.PlayerUID, player.PlayerName, "Player session started", "system");
                SaveStateUnsafe();
            }
        }
    }

    private void EndSession(IServerPlayer player)
    {
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(player.PlayerUID, player.PlayerName);
            long now = NowMs();
            if (rec.SessionStartedUnixMilliseconds > 0)
                rec.TotalPlaySeconds += Math.Max(0, (now - rec.SessionStartedUnixMilliseconds) / 1000);
            rec.SessionStartedUnixMilliseconds = 0;
            rec.LastSeenUnixMilliseconds = now;
            AddAuditUnsafe("system", "logout", player.PlayerUID, player.PlayerName, "Player disconnected", "system");
            SaveStateUnsafe();
        }
    }

    private void MarkActivity(IServerPlayer player)
    {
        var pos = GetRuntimeEntityPos(player.Entity);
        if (pos == null) return;
        lock (gate) activity[player.PlayerUID] = new PlayerActivityState(pos.Copy(), NowMs());
    }

    private void UpdateMovementActivity(IServerPlayer player, long now)
    {
        var pos = GetRuntimeEntityPos(player.Entity);
        if (pos == null) return;

        lock (gate)
        {
            if (!activity.TryGetValue(player.PlayerUID, out var tracked))
            {
                activity[player.PlayerUID] = new PlayerActivityState(pos.Copy(), now);
                return;
            }

            if (tracked.Position.SquareDistanceTo(pos) > 0.01)
            {
                tracked.Position = pos.Copy();
                tracked.LastActiveUnixMilliseconds = now;
            }
        }
    }

    private void TouchPlayer(IServerPlayer player)
    {
        lock (gate)
        {
            var rec = GetOrCreateUnsafe(player.PlayerUID, player.PlayerName);
            rec.LastKnownName = player.PlayerName;
            rec.LastSeenUnixMilliseconds = NowMs();
            SaveStateUnsafe();
        }
    }

    private AdminPlayerSummary BuildSummaryUnsafe(string uid, IServerPlayer? player)
    {
        var rec = GetOrCreateUnsafe(uid, player?.PlayerName ?? state.Players.GetValueOrDefault(uid)?.LastKnownName ?? uid);
        long now = NowMs();
        long session = rec.SessionStartedUnixMilliseconds > 0 ? Math.Max(0, (now - rec.SessionStartedUnixMilliseconds) / 1000) : 0;
        float health = 0, maxHealth = 0;
        long idleSeconds = 0;
        if (player != null && activity.TryGetValue(uid, out var activityState))
            idleSeconds = Math.Max(0, (now - activityState.LastActiveUnixMilliseconds) / 1000);
        if (player?.Entity != null)
        {
            var hb = player.Entity.GetBehavior<EntityBehaviorHealth>();
            if (hb != null) { health = hb.Health; maxHealth = hb.MaxHealth; }
        }
        var summaryPos = GetRuntimeEntityPos(player?.Entity);
        return new AdminPlayerSummary
        {
            PlayerUid = uid,
            Name = player?.PlayerName ?? rec.LastKnownName,
            Online = player != null,
            Role = player?.Role?.Code ?? "offline",
            GameMode = player?.WorldData?.CurrentGameMode.ToString() ?? "offline",
            PingMilliseconds = player == null || float.IsNaN(player.Ping) ? 0 : (int)Math.Round(player.Ping * 1000),
            FirstSeenUnixMilliseconds = rec.FirstSeenUnixMilliseconds,
            LastSeenUnixMilliseconds = rec.LastSeenUnixMilliseconds,
            SessionSeconds = session,
            TotalPlaySeconds = rec.TotalPlaySeconds + session,
            ActiveWarnings = rec.Warnings.Count(x => !x.Removed),
            Muted = rec.PermanentMute || rec.MuteUntilUnixMilliseconds > now,
            MuteUntilUnixMilliseconds = rec.MuteUntilUnixMilliseconds,
            Banned = rec.BanUntilUnixMilliseconds > now,
            BanUntilUnixMilliseconds = rec.BanUntilUnixMilliseconds,
            Frozen = rec.Frozen,
            Jailed = jailService?.IsJailed(uid) == true,
            JailUntilUnixMilliseconds = jailService?.GetReleaseAt(uid) ?? 0,
            Afk = player != null && (NewfiesAfkModSystem.Current?.IsAfk(uid) == true || idleSeconds >= 300),
            IdleSeconds = idleSeconds,
            Position = summaryPos == null ? "" : $"{summaryPos.X:0},{summaryPos.Y:0},{summaryPos.Z:0}",
            Health = health,
            MaxHealth = maxHealth
        };
    }

    private static EntityPos? GetRuntimeEntityPos(Entity? entity)
    {
        if (entity == null) return null;
        try
        {
            object? value = RuntimePosProperty?.GetValue(entity)
                ?? RuntimePosField?.GetValue(entity)
                ?? RuntimeServerPosProperty?.GetValue(entity)
                ?? RuntimeServerPosField?.GetValue(entity)
                ?? RuntimeSidedPosProperty?.GetValue(entity)
                ?? RuntimeSidedPosField?.GetValue(entity);
            return value as EntityPos;
        }
        catch
        {
            return null;
        }
    }

    public IServerPlayer? FindOnlinePlayer(string playerOrUid)
    {
        if (string.IsNullOrWhiteSpace(playerOrUid)) return null;
        lock (onlineGate)
        {
            if (onlinePlayersByUid.TryGetValue(playerOrUid, out var byUid)) return byUid;
            return onlinePlayersByName.TryGetValue(playerOrUid, out var byName) ? byName : null;
        }
    }

    private void IndexOnlinePlayer(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        lock (onlineGate)
        {
            if (onlinePlayersByUid.TryGetValue(player.PlayerUID, out var previous) &&
                !string.Equals(previous.PlayerName, player.PlayerName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(previous.PlayerName))
            {
                onlinePlayersByName.Remove(previous.PlayerName);
            }

            onlinePlayersByUid[player.PlayerUID] = player;
            if (!string.IsNullOrWhiteSpace(player.PlayerName))
                onlinePlayersByName[player.PlayerName] = player;
        }
        InvalidatePlayerSummaryCaches();
    }

    private void RemoveOnlinePlayer(IServerPlayer player)
    {
        lock (onlineGate)
        {
            onlinePlayersByUid.Remove(player.PlayerUID);
            if (!string.IsNullOrWhiteSpace(player.PlayerName) &&
                onlinePlayersByName.TryGetValue(player.PlayerName, out var indexed) &&
                string.Equals(indexed.PlayerUID, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
            {
                onlinePlayersByName.Remove(player.PlayerName);
            }
        }
        InvalidatePlayerSummaryCaches();
    }

    private IServerPlayer? FindPlayer(string playerOrUid) => api.Server.Players.FirstOrDefault(p =>
        string.Equals(p.PlayerUID, playerOrUid, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(p.PlayerName, playerOrUid, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<IServerPlayer> OnlinePlayers()
    {
        lock (onlineGate) return onlinePlayersByUid.Values.ToArray();
    }

    private void InvalidatePlayerSummaryCaches() => Interlocked.Increment(ref playerSummaryVersion);

    private bool TryResolveTarget(string playerOrUid, out string uid, out string name)
    {
        var player = FindPlayer(playerOrUid);
        if (player != null) { uid = player.PlayerUID; name = player.PlayerName; return true; }
        uid = ResolveKnownUid(playerOrUid);
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(uid) && state.Players.TryGetValue(uid, out var rec))
            { name = rec.LastKnownName; return true; }
        }
        name = ""; return false;
    }

    private string ResolveKnownUid(string playerOrUid)
    {
        if (string.IsNullOrWhiteSpace(playerOrUid)) return "";
        lock (gate)
        {
            if (state.Players.TryGetValue(playerOrUid, out var known)) return known.PlayerUid;
            var kv = state.Players.FirstOrDefault(x => string.Equals(x.Value.LastKnownName, playerOrUid, StringComparison.OrdinalIgnoreCase));
            return kv.Key ?? "";
        }
    }

    private PlayerAdminRecord GetOrCreateUnsafe(string uid, string name)
    {
        if (!state.Players.TryGetValue(uid, out var rec))
        {
            rec = new PlayerAdminRecord { PlayerUid = uid, LastKnownName = name, FirstSeenUnixMilliseconds = NowMs(), LastSeenUnixMilliseconds = NowMs() };
            state.Players[uid] = rec;
        }
        if (!string.IsNullOrWhiteSpace(name)) rec.LastKnownName = name;
        return rec;
    }

    private void AddAuditUnsafe(string actor, string action, string uid, string name, string detail, string source)
    {
        state.Audit.Add(new AdminAuditRecord
        {
            Id = NextIdUnsafe(), UnixMilliseconds = NowMs(), Actor = actor, Action = action,
            TargetUid = uid, TargetName = name, Detail = detail, Source = source
        });
        if (state.Audit.Count > 5000) state.Audit.RemoveRange(0, state.Audit.Count - 5000);
        api.Logger.Audit($"[ServerEssentials/Admin] {actor} {action} {name}/{uid}: {detail}");
    }

    private void InitializeNextRecordId()
    {
        lock (gate)
        {
            long max = state.Audit.Count == 0 ? 0 : state.Audit.Max(x => x.Id);
            foreach (var p in state.Players.Values)
            {
                if (p.Warnings.Count > 0) max = Math.Max(max, p.Warnings.Max(x => x.Id));
                if (p.Notes.Count > 0) max = Math.Max(max, p.Notes.Max(x => x.Id));
            }
            foreach (var snapshots in state.InventorySnapshots.Values)
                if (snapshots.Count > 0) max = Math.Max(max, snapshots.Max(x => x.Id));
            nextRecordId = Math.Max(1, max + 1);
        }
    }

    private long NextIdUnsafe() => nextRecordId++;

    private bool IsStaff(IServerPlayer player) =>
        player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege(Privilege.root) ||
        player.HasPrivilege(PrivServer) || player.HasPrivilege(PrivView) || player.HasPrivilege(PrivManage) ||
        player.HasPrivilege(PrivWarn) || player.HasPrivilege(PrivMute) || player.HasPrivilege(PrivBan) ||
        player.HasPrivilege(PrivInventoryEdit) || player.HasPrivilege(PrivReports);

    private void LoadState()
    {
        if (storage == null)
        {
            state = new AdminState();
            NormalizeStateUnsafe();
            return;
        }

        if (storage.HasState())
        {
            state = storage.LoadState();
            NormalizeStateUnsafe();
            storage.RequestSave();
            api.Logger.Notification("[ServerEssentials/Storage] Loaded admin state from {0}", storage.DatabasePath);
            return;
        }

        AdminState? legacy = ModConfigRecovery.LoadOrNull<AdminState>(api, StateFile, "ServerEssentials/Storage");

        state = legacy ?? new AdminState();
        NormalizeStateUnsafe();
        if (legacy != null)
        {
            storage.BackupLegacyJson(state);
            storage.ImportLegacy(SnapshotStateForStorage());
            api.Logger.Notification("[ServerEssentials/Storage] Migrated legacy {0} into essentials.db. The JSON is now read-only legacy data.", StateFile);
        }
        else
        {
            storage.RequestSave();
            storage.Flush();
        }
    }

    private void NormalizeStateUnsafe()
    {
        state.Players = new Dictionary<string, PlayerAdminRecord>(state.Players ?? new Dictionary<string, PlayerAdminRecord>(), StringComparer.OrdinalIgnoreCase);
        foreach (var rec in state.Players.Values)
        {
            rec.Warnings ??= new();
            rec.Notes ??= new();
            rec.LastKnownName ??= rec.PlayerUid ?? "";
            rec.MuteReason ??= ""; rec.MutedBy ??= "";
            rec.BanReason ??= ""; rec.BannedBy ??= "";
            rec.FreezeReason ??= ""; rec.FrozenBy ??= "";
        }
        state.Audit ??= new();
        state.OfflineInventories = new Dictionary<string, AdminOfflineInventoryRecord>(state.OfflineInventories ?? new(), StringComparer.OrdinalIgnoreCase);
        state.InventorySnapshots = new Dictionary<string, List<AdminInventorySnapshotRecord>>(state.InventorySnapshots ?? new Dictionary<string, List<AdminInventorySnapshotRecord>>(), StringComparer.OrdinalIgnoreCase);
        foreach (var list in state.InventorySnapshots.Values)
        {
            foreach (var snap in list)
            {
                snap.Slots ??= new();
                foreach (var slot in snap.Slots)
                {
                    slot.StackBytes ??= Array.Empty<byte>();
                    if (slot.StackBytes.Length == 0 && !string.IsNullOrWhiteSpace(slot.StackBase64))
                    {
                        try { slot.StackBytes = Convert.FromBase64String(slot.StackBase64); } catch { slot.StackBytes = Array.Empty<byte>(); }
                    }
                    if ((string.IsNullOrWhiteSpace(slot.ItemCode) || slot.StackSize <= 0) && slot.StackBytes.Length > 0)
                    {
                        var stack = DeserializeSnapshotStack(slot);
                        if (stack != null)
                        {
                            slot.ItemCode = stack.Collectible?.Code?.ToString() ?? "";
                            slot.StackSize = stack.StackSize;
                        }
                    }
                    slot.StackBase64 = "";
                }
            }
            while (list.Count > 12) list.RemoveAt(0);
        }
        state.SchemaVersion = Math.Max(state.SchemaVersion, 3);
    }

    private AdminState SnapshotStateForStorage()
    {
        lock (gate)
        {
            var copy = new AdminState
            {
                SchemaVersion = state.SchemaVersion,
                MaintenanceMode = state.MaintenanceMode,
                MaintenanceReason = state.MaintenanceReason,
                MaintenanceUntilUnixMilliseconds = state.MaintenanceUntilUnixMilliseconds,
                Audit = state.Audit.Select(CloneAudit).ToList(),
                Players = new Dictionary<string, PlayerAdminRecord>(StringComparer.OrdinalIgnoreCase),
                InventorySnapshots = new Dictionary<string, List<AdminInventorySnapshotRecord>>(StringComparer.OrdinalIgnoreCase),
                OfflineInventories = new Dictionary<string, AdminOfflineInventoryRecord>(StringComparer.OrdinalIgnoreCase)
            };
            foreach (var kv in state.Players)
            {
                var r = kv.Value;
                copy.Players[kv.Key] = new PlayerAdminRecord
                {
                    PlayerUid=r.PlayerUid, LastKnownName=r.LastKnownName, FirstSeenUnixMilliseconds=r.FirstSeenUnixMilliseconds, LastSeenUnixMilliseconds=r.LastSeenUnixMilliseconds, TotalPlaySeconds=r.TotalPlaySeconds, SessionStartedUnixMilliseconds=r.SessionStartedUnixMilliseconds,
                    Warnings=r.Warnings.Select(CloneWarning).ToList(), Notes=r.Notes.Select(CloneNote).ToList(),
                    MuteUntilUnixMilliseconds=r.MuteUntilUnixMilliseconds, PermanentMute=r.PermanentMute, MuteReason=r.MuteReason, MutedBy=r.MutedBy,
                    BanUntilUnixMilliseconds=r.BanUntilUnixMilliseconds, BanReason=r.BanReason, BannedBy=r.BannedBy, Frozen=r.Frozen, FreezeUntilUnixMilliseconds=r.FreezeUntilUnixMilliseconds, FrozenBy=r.FrozenBy, FreezeReason=r.FreezeReason
                };
            }
            foreach (var kv in state.InventorySnapshots)
            {
                copy.InventorySnapshots[kv.Key] = kv.Value.Select(s => new AdminInventorySnapshotRecord
                {
                    Id=s.Id, UnixMilliseconds=s.UnixMilliseconds, PlayerUid=s.PlayerUid, PlayerName=s.PlayerName, CreatedBy=s.CreatedBy, Reason=s.Reason, OccupiedSlots=s.OccupiedSlots, SlotsPayloadLoaded=s.SlotsPayloadLoaded,
                    Slots=s.Slots.Select(x => new AdminInventorySnapshotSlot { Inventory=x.Inventory, Slot=x.Slot, StackBytes=x.StackBytes ?? Array.Empty<byte>(), ItemCode=x.ItemCode, StackSize=x.StackSize }).ToList()
                }).ToList();
            }
            foreach (var kv in state.OfflineInventories)
                copy.OfflineInventories[kv.Key] = kv.Value; // Captures are immutable after publication; avoid cloning all historical slots on every background save.
            return copy;
        }
    }

    private void SaveState()
    {
        try { storage?.Flush(); }
        catch (Exception ex) { api.Logger.Warning("[ServerEssentials/Storage] Failed to flush admin state: {0}", ex.Message); }
    }

    private void SaveStateUnsafe()
    {
        InvalidatePlayerSummaryCaches();
        storage?.RequestSave();
    }

    public static bool TryParseDuration(string? text, out long milliseconds, out bool permanent)
    {
        milliseconds = 0; permanent = false;
        text = (text ?? "").Trim().ToLowerInvariant();
        if (text is "permanent" or "perm") { permanent = true; return true; }
        if (text.Length < 2 || !double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < 0) return false;
        double factor = text[^1] switch { 's' => 1000, 'm' => 60_000, 'h' => 3_600_000, 'd' => 86_400_000, 'w' => 604_800_000, _ => -1 };
        if (factor < 0) return false;
        double total = value * factor;
        if (!double.IsFinite(total) || total > long.MaxValue / TimeSpan.TicksPerMillisecond) return false;
        milliseconds = (long)total;
        return true;
    }

    public static string FormatDuration(long milliseconds)
    {
        var span = TimeSpan.FromTicks(Math.Min(long.MaxValue / TimeSpan.TicksPerMillisecond, Math.Max(0, milliseconds)) * TimeSpan.TicksPerMillisecond);
        if (span.TotalDays >= 1) return $"{span.TotalDays:0.#}d";
        if (span.TotalHours >= 1) return $"{span.TotalHours:0.#}h";
        if (span.TotalMinutes >= 1) return $"{span.TotalMinutes:0.#}m";
        return $"{Math.Max(1, span.TotalSeconds):0}s";
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static long DeadlineAfterMilliseconds(long delay)
    {
        long now = NowMs();
        return now + Math.Min(long.MaxValue - now, Math.Max(0, delay));
    }
    private static AdminWarningRecord CloneWarning(AdminWarningRecord x) => new() { Id=x.Id, UnixMilliseconds=x.UnixMilliseconds, IssuedBy=x.IssuedBy, Reason=x.Reason, Removed=x.Removed, RemovedBy=x.RemovedBy, RemovedUnixMilliseconds=x.RemovedUnixMilliseconds };
    private static AdminNoteRecord CloneNote(AdminNoteRecord x) => new() { Id=x.Id, UnixMilliseconds=x.UnixMilliseconds, Author=x.Author, Text=x.Text, Removed=x.Removed };
    private static AdminAuditRecord CloneAudit(AdminAuditRecord x) => new() { Id=x.Id, UnixMilliseconds=x.UnixMilliseconds, Actor=x.Actor, Action=x.Action, TargetUid=x.TargetUid, TargetName=x.TargetName, Detail=x.Detail, Source=x.Source };

    private sealed class AdminGhostState
    {
        public EnumGameMode GameMode;
        public bool FreeMove;
        public bool NoClip;
    }

    private sealed class PlayerActivityState
    {
        public EntityPos Position;
        public long LastActiveUnixMilliseconds;
        public PlayerActivityState(EntityPos position, long lastActiveUnixMilliseconds) { Position = position; LastActiveUnixMilliseconds = lastActiveUnixMilliseconds; }
    }

    private sealed record AdminUndoEntry(string Description, System.Func<string, string> Reverse);

    public void Dispose()
    {
        if (disposed) return;
        OnGameWorldSave();
        disposed = true;
        SaveState();
        api.Event.PlayerJoin -= OnPlayerJoin;
        api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
        api.Event.PlayerDisconnect -= OnPlayerDisconnect;
        api.Event.PlayerChat -= OnPlayerChat;
        api.Event.CanPlaceOrBreakBlock -= OnCanPlaceOrBreakBlock;
        api.Event.CanUseBlock -= OnCanUseBlock;
        api.Event.OnPlayerInteractEntity -= OnPlayerInteractEntity;
        api.Event.GameWorldSave -= OnGameWorldSave;
        if (tickListenerId >= 0) api.Event.UnregisterGameTickListener(tickListenerId);
        tickListenerId = -1;
        if (freezeTickListenerId >= 0) api.Event.UnregisterGameTickListener(freezeTickListenerId);
        freezeTickListenerId = -1;
        foreach (var player in OnlinePlayers().ToArray()) RestoreGhostOnDisconnect(player);
        ghostStates.Clear();
        lock (onlineGate)
        {
            onlinePlayersByUid.Clear();
            onlinePlayersByName.Clear();
        }
        jailService?.Dispose();
        jailService = null;
        storage?.Dispose();
        storage = null;
    }
}
