using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BlockLogger.Config;
using BlockLogger.Models;
using BlockLogger.Services;
using BlockLogger.Tracking;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Hooks;

public sealed class ContainerHooks
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(5);

    private readonly ICoreServerAPI api;
    private readonly BlockLoggerConfig config;
    private readonly LogStorageService queue;
    private readonly LogStorageService snapshotQueue;
    private readonly IngameTimeFormatter timeFormatter;
    private readonly string worldId;
    private readonly Dictionary<string, ContainerSession> sessionsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference<IServerPlayer>> onlinePlayersByUid = new(StringComparer.Ordinal);
    private long cleanupListenerId = -1;
    private long hookFailures;
    private long lastHookWarningTicks;
    private long sessionsEvictedForCapacity;
    private long sessionsDroppedBecauseInventoryExpired;

    public ContainerHooks(ICoreServerAPI api, BlockLoggerConfig config, LogStorageService queue, LogStorageService snapshotQueue, IngameTimeFormatter timeFormatter)
    {
        this.api = api;
        this.config = config;
        this.queue = queue;
        this.snapshotQueue = snapshotQueue;
        this.timeFormatter = timeFormatter;
        worldId = api.World.SavegameIdentifier ?? "unknownworld";
    }

    public int ActiveSessionCount => sessionsByKey.Count;
    public long SessionsEvictedForCapacity => Interlocked.Read(ref sessionsEvictedForCapacity);
    public long SessionsDroppedBecauseInventoryExpired => Interlocked.Read(ref sessionsDroppedBecauseInventoryExpired);

    public void Register()
    {
        api.Event.DidUseBlock += OnDidUseBlock;
        api.Event.PlayerJoin += OnPlayerJoin;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        foreach (var player in api.World.AllOnlinePlayers)
            if (player is IServerPlayer serverPlayer && !string.IsNullOrWhiteSpace(serverPlayer.PlayerUID))
                onlinePlayersByUid[serverPlayer.PlayerUID] = new WeakReference<IServerPlayer>(serverPlayer);
    }

    public void Unregister()
    {
        api.Event.DidUseBlock -= OnDidUseBlock;
        api.Event.PlayerJoin -= OnPlayerJoin;
        api.Event.PlayerDisconnect -= OnPlayerDisconnect;
        if (cleanupListenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(cleanupListenerId);
            cleanupListenerId = -1;
        }

        ClearSessions(queueFinalSnapshots: false);
        onlinePlayersByUid.Clear();
    }

    /// <summary>
    /// Applies live configuration changes that affect container-session lifetime. In particular,
    /// disabling content tracking must detach already-subscribed SlotModified handlers immediately;
    /// waiting for a later slot event or timeout defeats the purpose of turning the feature off.
    /// </summary>
    public void ApplyRuntimeSettings()
    {
        bool trackContents = config.Enabled && (config.LogContainerItemChanges || config.LogContainerSnapshots);
        if (!trackContents)
        {
            ClearSessions(queueFinalSnapshots: false);
            return;
        }

        CleanupExpiredSessions();
        EnforceSessionCapacity(config.MaxTrackedContainerSessions);
        EnsureCleanupListener();
    }

    private void OnDidUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        try { OnDidUseBlockCore(byPlayer, blockSel); }
        catch (Exception ex) { ReportHookFailure("DidUseBlock", ex); }
    }

    private void OnDidUseBlockCore(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        if (!config.Enabled || blockSel?.Position == null || byPlayer == null) return;
        bool logAccess = config.LogContainerAccess;
        bool trackContents = config.LogContainerItemChanges || config.LogContainerSnapshots;
        if (!logAccess && !trackContents) return;

        var blockEntity = api.World.BlockAccessor.GetBlockEntity(blockSel.Position);
        if (blockEntity is not IBlockEntityContainer container || container.Inventory == null) return;

        var block = api.World.BlockAccessor.GetBlock(blockSel.Position);
        var blockCode = block?.Code?.ToShortString() ?? "unknown";
        if (ShouldIgnore(blockCode)) return;

        var inventory = container.Inventory;

        // Access-only logging does not need inventory snapshots, SlotModified handlers,
        // session dictionaries, or the stale-session cleanup listener.
        if (!trackContents)
        {
            QueueContainerRecord(
                LogActionType.ContainerOpen,
                byPlayer,
                blockSel.Position,
                blockCode,
                null,
                null,
                null,
                inventory.InventoryID);
            return;
        }

        var sessionKey = BuildSessionKey(inventory.InventoryID, blockSel.Position);
        var now = DateTime.UtcNow;

        if (!sessionsByKey.TryGetValue(sessionKey, out var session))
        {
            EnsureCapacityForNewSession();

            session = new ContainerSession
            {
                SessionId = sessionKey,
                InventoryId = inventory.InventoryID,
                BlockCode = blockCode,
                Position = blockSel.Position.Copy(),
                InventoryRef = new WeakReference<IInventory>(inventory),
                SlotStates = SnapshotInventory(inventory),
                ExpiresAtUtc = now + SessionTimeout
            };

            session.SetActivePlayer(byPlayer.PlayerUID, byPlayer.PlayerName);
            sessionsByKey[sessionKey] = session;
            AttachSession(session);
            EnsureCleanupListener();

            if (logAccess)
                QueueContainerRecord(LogActionType.ContainerOpen, byPlayer, session.Position, session.BlockCode, null, null, null, session.InventoryId);
            QueueSnapshot(session, byPlayer, "open", null);
        }
        else
        {
            // One listener per inventory, not one listener per player opening it. Only this
            // explicit player interaction extends the lifetime; SlotModified never does.
            session.SetActivePlayer(byPlayer.PlayerUID, byPlayer.PlayerName);
            session.ExpiresAtUtc = now + SessionTimeout;

            // If the old weak target died and Vintage Story has supplied a new inventory object
            // at the same location/id, replace the stale session rather than trying to reuse it.
            if (!session.TryGetInventory(out var existing) || !ReferenceEquals(existing, inventory))
            {
                RemoveSession(sessionKey, queueFinalSnapshot: false);
                EnsureCapacityForNewSession();
                session = new ContainerSession
                {
                    SessionId = sessionKey,
                    InventoryId = inventory.InventoryID,
                    BlockCode = blockCode,
                    Position = blockSel.Position.Copy(),
                    InventoryRef = new WeakReference<IInventory>(inventory),
                    SlotStates = SnapshotInventory(inventory),
                    ExpiresAtUtc = now + SessionTimeout
                };
                session.SetActivePlayer(byPlayer.PlayerUID, byPlayer.PlayerName);
                sessionsByKey[sessionKey] = session;
                AttachSession(session);
                EnsureCleanupListener();
            }
        }
    }

    private void AttachSession(ContainerSession session)
    {
        if (session.TryGetInventory(out var inventory) && inventory is InventoryBase invBase)
        {
            // Capture only the immutable id, not the session object. This avoids creating a
            // handler -> session cycle and makes the event subscription as small as possible.
            string sessionId = session.SessionId;
            Action<int> handler = slotId => OnSlotModified(sessionId, slotId);
            session.SlotModifiedHandler = handler;
            invBase.SlotModified += handler;
        }
    }

    private void DetachSession(ContainerSession session)
    {
        if (session.TryGetInventory(out var inventory) && inventory is InventoryBase invBase && session.SlotModifiedHandler != null)
        {
            invBase.SlotModified -= session.SlotModifiedHandler;
        }
        session.SlotModifiedHandler = null;

        if (session.SnapshotCallbackId.HasValue)
        {
            api.Event.UnregisterCallback(session.SnapshotCallbackId.Value);
            session.SnapshotCallbackId = null;
        }

        session.Detached = true;
    }

    private void OnSlotModified(string sessionId, int slotId)
    {
        try { OnSlotModifiedCore(sessionId, slotId); }
        catch (Exception ex) { ReportHookFailure("SlotModified", ex); }
    }

    private void OnSlotModifiedCore(string sessionId, int slotId)
    {
        if (!sessionsByKey.TryGetValue(sessionId, out var session) || session.Detached) return;

        // Config can change at runtime. This defensive check complements ApplyRuntimeSettings()
        // so a stale callback can never continue content tracking after the feature is disabled.
        if (!config.Enabled || (!config.LogContainerItemChanges && !config.LogContainerSnapshots))
        {
            RemoveSession(sessionId, queueFinalSnapshot: false);
            return;
        }

        // Automated inventory changes do NOT refresh expiry. If no player has touched this
        // container in five minutes, detach before doing any more logging.
        if (DateTime.UtcNow >= session.ExpiresAtUtc)
        {
            RemoveSession(sessionId, queueFinalSnapshot: config.LogContainerSnapshots, finalReason: "timeout");
            return;
        }

        if (!session.TryGetInventory(out var inventory))
        {
            Interlocked.Increment(ref sessionsDroppedBecauseInventoryExpired);
            RemoveSession(sessionId, queueFinalSnapshot: false);
            return;
        }

        var current = ReadSlotState(inventory, slotId);
        if (!session.SlotStates.TryGetValue(slotId, out var previous))
            previous = TrackedSlotState.Empty;

        if (StatesEqual(previous, current)) return;

        if (config.LogContainerItemChanges)
            EmitDeltaLogs(session, previous, current, slotId);

        if (string.IsNullOrEmpty(current.ItemCode) || current.StackSize <= 0)
            session.SlotStates.Remove(slotId);
        else
            session.SlotStates[slotId] = current;

        if (config.LogContainerSnapshots && config.SnapshotOnSlotChange)
            ScheduleSnapshot(session, slotId);
    }

    private void ScheduleSnapshot(ContainerSession session, int changedSlotId)
    {
        // Coalesce many SlotModified events from one inventory operation into one snapshot.
        if (session.SnapshotCallbackId.HasValue) return;

        var sessionId = session.SessionId;
        session.SnapshotCallbackId = api.Event.RegisterCallback(_ =>
        {
            try
            {
                if (!sessionsByKey.TryGetValue(sessionId, out var current) || current.Detached) return;
                current.SnapshotCallbackId = null;
                if (DateTime.UtcNow >= current.ExpiresAtUtc)
                {
                    RemoveSession(sessionId, queueFinalSnapshot: config.LogContainerSnapshots, finalReason: "timeout");
                    return;
                }
                QueueSnapshot(current, ResolvePlayer(current.PlayerUid), "slotmodified", changedSlotId);
            }
            catch (Exception ex) { ReportHookFailure("SnapshotCallback", ex); }
        }, config.SnapshotDebounceMs);
    }

    private void EmitDeltaLogs(ContainerSession session, TrackedSlotState previous, TrackedSlotState current, int slotId)
    {
        var player = ResolvePlayer(session.PlayerUid);

        if (!string.IsNullOrEmpty(previous.ItemCode) && previous.StackSize > 0)
        {
            if (previous.ItemCode == current.ItemCode)
            {
                var delta = current.StackSize - previous.StackSize;
                if (delta < 0)
                    QueueContainerRecord(LogActionType.ContainerTake, player, session.Position, session.BlockCode, previous.ItemCode, -delta, slotId, session.InventoryId);
                else if (delta > 0)
                    QueueContainerRecord(LogActionType.ContainerPut, player, session.Position, session.BlockCode, current.ItemCode, delta, slotId, session.InventoryId);
                return;
            }

            QueueContainerRecord(LogActionType.ContainerTake, player, session.Position, session.BlockCode, previous.ItemCode, previous.StackSize, slotId, session.InventoryId);
        }

        if (!string.IsNullOrEmpty(current.ItemCode) && current.StackSize > 0)
            QueueContainerRecord(LogActionType.ContainerPut, player, session.Position, session.BlockCode, current.ItemCode, current.StackSize, slotId, session.InventoryId);
    }

    private void QueueContainerRecord(LogActionType action, IServerPlayer? byPlayer, BlockPos pos, string blockCode, string? itemCode, int? quantity, int? slotId, string? inventoryId)
    {
        queue.TryEnqueueFast(new QueuedEventPayload(
            DateTime.UtcNow, timeFormatter.GetCurrentStamp(), action,
            byPlayer?.PlayerUID ?? string.Empty, byPlayer?.PlayerName ?? string.Empty,
            worldId, ResolveDimension(pos), pos.X, pos.Y, pos.Z, blockCode,
            itemCode == null ? null : "itemCode", itemCode,
            quantity.HasValue ? "quantity" : null, quantity?.ToString(),
            slotId.HasValue ? "slotId" : null, slotId?.ToString(),
            string.IsNullOrWhiteSpace(inventoryId) ? null : "inventoryId", inventoryId));
    }

    private void QueueSnapshot(ContainerSession session, IServerPlayer? byPlayer, string reason, int? changedSlotId)
    {
        if (!config.Enabled || !config.LogContainerSnapshots) return;
        if (!session.TryGetInventory(out var inventory)) return;

        var slots = SnapshotSlots(inventory);
        var totalItemCount = 0;
        foreach (var slot in slots) totalItemCount += slot.StackSize;

        var snapshot = new ContainerSnapshotRecord
        {
            TimestampUtc = DateTime.UtcNow,
            IngameDate = timeFormatter.GetCurrentStamp(),
            WorldId = worldId,
            Dimension = ResolveDimension(session.Position),
            X = session.Position.X, Y = session.Position.Y, Z = session.Position.Z,
            BlockCode = session.BlockCode,
            PlayerUid = byPlayer?.PlayerUID ?? session.PlayerUid,
            PlayerName = byPlayer?.PlayerName ?? session.PlayerName,
            InventoryId = session.InventoryId,
            Reason = reason,
            ChangedSlotId = changedSlotId,
            SlotCount = inventory.Count,
            NonEmptySlotCount = slots.Count,
            TotalItemCount = totalItemCount,
            Slots = slots
        };

        snapshotQueue.TryEnqueue(snapshot);
    }

    private void CleanupExpiredSessions()
    {
        if (sessionsByKey.Count == 0)
        {
            StopCleanupListenerIfIdle();
            return;
        }

        if (!config.Enabled || (!config.LogContainerItemChanges && !config.LogContainerSnapshots))
        {
            ClearSessions(queueFinalSnapshots: false);
            return;
        }

        var now = DateTime.UtcNow;
        var expired = new List<string>();

        foreach (var pair in sessionsByKey)
        {
            if (pair.Value.Detached || now >= pair.Value.ExpiresAtUtc || !pair.Value.TryGetInventory(out _))
                expired.Add(pair.Key);
        }

        foreach (var key in expired)
        {
            if (!sessionsByKey.TryGetValue(key, out var session)) continue;
            bool inventoryAlive = session.TryGetInventory(out _);
            if (!inventoryAlive) Interlocked.Increment(ref sessionsDroppedBecauseInventoryExpired);
            RemoveSession(key, queueFinalSnapshot: inventoryAlive && config.LogContainerSnapshots, finalReason: "timeout");
        }

        EnforceSessionCapacity(config.MaxTrackedContainerSessions);
        StopCleanupListenerIfIdle();
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        onlinePlayersByUid[player.PlayerUID] = new WeakReference<IServerPlayer>(player);
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        try { OnPlayerDisconnectCore(player); }
        catch (Exception ex) { ReportHookFailure("PlayerDisconnect", ex); }
    }

    private void OnPlayerDisconnectCore(IServerPlayer player)
    {
        if (player == null) return;
        onlinePlayersByUid.Remove(player.PlayerUID);

        var removeKeys = new List<string>();
        foreach (var pair in sessionsByKey)
        {
            if (!pair.Value.PlayerUid.Equals(player.PlayerUID, StringComparison.Ordinal)) continue;
            QueueSnapshot(pair.Value, player, "playerdisconnect", null);
            DetachSession(pair.Value);
            removeKeys.Add(pair.Key);
        }

        foreach (var key in removeKeys) sessionsByKey.Remove(key);
        StopCleanupListenerIfIdle();
    }

    private void EnsureCapacityForNewSession()
    {
        CleanupDeadInventorySessions();
        EnforceSessionCapacity(Math.Max(16, config.MaxTrackedContainerSessions) - 1);
    }

    private void CleanupDeadInventorySessions()
    {
        if (sessionsByKey.Count == 0) return;
        var dead = sessionsByKey
            .Where(pair => pair.Value.Detached || !pair.Value.TryGetInventory(out _))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in dead)
        {
            Interlocked.Increment(ref sessionsDroppedBecauseInventoryExpired);
            RemoveSession(key, queueFinalSnapshot: false);
        }
    }

    private void EnforceSessionCapacity(int capacity)
    {
        capacity = Math.Clamp(capacity, 0, 8192);
        if (sessionsByKey.Count <= capacity) return;

        int removeCount = sessionsByKey.Count - capacity;
        foreach (var key in sessionsByKey
                     .OrderBy(pair => pair.Value.ExpiresAtUtc)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                     .Take(removeCount)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            if (RemoveSession(key, queueFinalSnapshot: false))
                Interlocked.Increment(ref sessionsEvictedForCapacity);
        }
    }

    private bool RemoveSession(string key, bool queueFinalSnapshot, string finalReason = "")
    {
        if (!sessionsByKey.TryGetValue(key, out var session)) return false;

        if (queueFinalSnapshot && !string.IsNullOrWhiteSpace(finalReason))
            QueueSnapshot(session, ResolvePlayer(session.PlayerUid), finalReason, null);

        DetachSession(session);
        sessionsByKey.Remove(key);
        StopCleanupListenerIfIdle();
        return true;
    }

    private void ClearSessions(bool queueFinalSnapshots)
    {
        if (sessionsByKey.Count == 0)
        {
            StopCleanupListenerIfIdle();
            return;
        }

        foreach (var session in sessionsByKey.Values.ToList())
        {
            if (queueFinalSnapshots)
                QueueSnapshot(session, ResolvePlayer(session.PlayerUid), "disabled", null);
            DetachSession(session);
        }
        sessionsByKey.Clear();
        StopCleanupListenerIfIdle();
    }

    private void EnsureCleanupListener()
    {
        if (cleanupListenerId >= 0 || sessionsByKey.Count == 0) return;
        cleanupListenerId = api.Event.RegisterGameTickListener(_ => CleanupExpiredSessionsSafe(), 30_000);
    }

    private void StopCleanupListenerIfIdle()
    {
        if (sessionsByKey.Count != 0 || cleanupListenerId < 0) return;
        api.Event.UnregisterGameTickListener(cleanupListenerId);
        cleanupListenerId = -1;
    }

    private void CleanupExpiredSessionsSafe()
    {
        try { CleanupExpiredSessions(); }
        catch (Exception ex) { ReportHookFailure("CleanupExpiredSessions", ex); }
    }

    private void ReportHookFailure(string hookName, Exception ex)
    {
        Interlocked.Increment(ref hookFailures);
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref lastHookWarningTicks);
        if (now - previous < TimeSpan.FromSeconds(30).Ticks) return;
        if (Interlocked.CompareExchange(ref lastHookWarningTicks, now, previous) != previous) return;
        api.Logger.Warning("[BlockLogger] Container hook {0} failed; record skipped. Failures={1}. {2}: {3}", hookName, Interlocked.Read(ref hookFailures), ex.GetType().Name, ex.Message);
    }

    private IServerPlayer? ResolvePlayer(string playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid) || !onlinePlayersByUid.TryGetValue(playerUid, out var weak))
            return null;

        if (weak.TryGetTarget(out var player) && player != null)
            return player;

        onlinePlayersByUid.Remove(playerUid);
        return null;
    }

    private static Dictionary<int, TrackedSlotState> SnapshotInventory(IInventory inventory)
    {
        // Empty slots are represented implicitly by a missing dictionary entry. This keeps the
        // per-session working set proportional to occupied slots instead of total inventory size.
        var states = new Dictionary<int, TrackedSlotState>();
        for (var i = 0; i < inventory.Count; i++)
        {
            var state = ReadSlotState(inventory, i);
            if (!string.IsNullOrEmpty(state.ItemCode) && state.StackSize > 0) states[i] = state;
        }
        return states;
    }

    private static List<ContainerSnapshotSlot> SnapshotSlots(IInventory inventory)
    {
        var slots = new List<ContainerSnapshotSlot>();
        for (var i = 0; i < inventory.Count; i++)
        {
            var stack = inventory[i]?.Itemstack;
            var code = stack?.Collectible?.Code?.ToShortString();
            var size = stack?.StackSize ?? 0;
            if (!string.IsNullOrWhiteSpace(code) && size > 0)
                slots.Add(new ContainerSnapshotSlot { SlotId = i, ItemCode = code, StackSize = size });
        }
        return slots;
    }

    private static TrackedSlotState ReadSlotState(IInventory inventory, int slotId)
    {
        if (slotId < 0 || slotId >= inventory.Count) return TrackedSlotState.Empty;
        var stack = inventory[slotId]?.Itemstack;
        return new TrackedSlotState(
            stack?.Collectible?.Code?.ToShortString() ?? string.Empty,
            stack?.StackSize ?? 0);
    }

    private bool ShouldIgnore(string blockCode)
    {
        foreach (var prefix in config.IgnoredBlockCodePrefixes)
            if (!string.IsNullOrWhiteSpace(prefix) && blockCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool StatesEqual(TrackedSlotState a, TrackedSlotState b) =>
        a.ItemCode == b.ItemCode && a.StackSize == b.StackSize;

    private static string BuildSessionKey(string inventoryId, BlockPos pos) =>
        $"{inventoryId}|{pos.X}|{pos.Y}|{pos.Z}|{pos.dimension}";

    private static string ResolveDimension(BlockPos pos) => pos.dimension.ToString();
}
