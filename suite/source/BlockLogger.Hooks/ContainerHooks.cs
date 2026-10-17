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
	private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(5L);

	private readonly ICoreServerAPI api;

	private readonly BlockLoggerConfig config;

	private readonly LogStorageService queue;

	private readonly LogStorageService snapshotQueue;

	private readonly IngameTimeFormatter timeFormatter;

	private readonly string worldId;

	private readonly Dictionary<string, ContainerSession> sessionsByKey = new Dictionary<string, ContainerSession>(StringComparer.Ordinal);

	private readonly Dictionary<string, WeakReference<IServerPlayer>> onlinePlayersByUid = new Dictionary<string, WeakReference<IServerPlayer>>(StringComparer.Ordinal);

	private long cleanupListenerId = -1L;

	private long hookFailures;

	private long lastHookWarningTicks;

	private long sessionsEvictedForCapacity;

	private long sessionsDroppedBecauseInventoryExpired;

	public int ActiveSessionCount => sessionsByKey.Count;

	public long SessionsEvictedForCapacity => Interlocked.Read(in sessionsEvictedForCapacity);

	public long SessionsDroppedBecauseInventoryExpired => Interlocked.Read(in sessionsDroppedBecauseInventoryExpired);

	public ContainerHooks(ICoreServerAPI api, BlockLoggerConfig config, LogStorageService queue, LogStorageService snapshotQueue, IngameTimeFormatter timeFormatter)
	{
		this.api = api;
		this.config = config;
		this.queue = queue;
		this.snapshotQueue = snapshotQueue;
		this.timeFormatter = timeFormatter;
		worldId = api.World.SavegameIdentifier ?? "unknownworld";
	}

	public void Register()
	{
		api.Event.DidUseBlock += OnDidUseBlock;
		api.Event.PlayerJoin += OnPlayerJoin;
		api.Event.PlayerDisconnect += OnPlayerDisconnect;
		IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
		foreach (IPlayer player in allOnlinePlayers)
		{
			if (player is IServerPlayer serverPlayer && !string.IsNullOrWhiteSpace(serverPlayer.PlayerUID))
			{
				onlinePlayersByUid[serverPlayer.PlayerUID] = new WeakReference<IServerPlayer>(serverPlayer);
			}
		}
	}

	public void Unregister()
	{
		api.Event.DidUseBlock -= OnDidUseBlock;
		api.Event.PlayerJoin -= OnPlayerJoin;
		api.Event.PlayerDisconnect -= OnPlayerDisconnect;
		if (cleanupListenerId >= 0)
		{
			api.Event.UnregisterGameTickListener(cleanupListenerId);
			cleanupListenerId = -1L;
		}
		ClearSessions(queueFinalSnapshots: false);
		onlinePlayersByUid.Clear();
	}

	public void ApplyRuntimeSettings()
	{
		if (!config.Enabled || (!config.LogContainerItemChanges && !config.LogContainerSnapshots))
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
		try
		{
			OnDidUseBlockCore(byPlayer, blockSel);
		}
		catch (Exception ex)
		{
			ReportHookFailure("DidUseBlock", ex);
		}
	}

	private void OnDidUseBlockCore(IServerPlayer byPlayer, BlockSelection blockSel)
	{
		if (!config.Enabled || blockSel?.Position == null || byPlayer == null)
		{
			return;
		}
		bool logContainerAccess = config.LogContainerAccess;
		bool flag = config.LogContainerItemChanges || config.LogContainerSnapshots;
		if (!logContainerAccess && !flag)
		{
			return;
		}
		BlockEntity blockEntity = api.World.BlockAccessor.GetBlockEntity(blockSel.Position);
		if (!(blockEntity is IBlockEntityContainer { Inventory: not null } blockEntityContainer))
		{
			return;
		}
		string blockCode = api.World.BlockAccessor.GetBlock(blockSel.Position)?.Code?.ToShortString() ?? "unknown";
		if (ShouldIgnore(blockCode))
		{
			return;
		}
		IInventory inventory = blockEntityContainer.Inventory;
		if (!flag)
		{
			QueueContainerRecord(LogActionType.ContainerOpen, byPlayer, blockSel.Position, blockCode, null, null, null, inventory.InventoryID);
			return;
		}
		string text = BuildSessionKey(inventory.InventoryID, blockSel.Position);
		DateTime utcNow = DateTime.UtcNow;
		if (!sessionsByKey.TryGetValue(text, out ContainerSession value))
		{
			EnsureCapacityForNewSession();
			value = new ContainerSession
			{
				SessionId = text,
				InventoryId = inventory.InventoryID,
				BlockCode = blockCode,
				Position = blockSel.Position.Copy(),
				InventoryRef = new WeakReference<IInventory>(inventory),
				SlotStates = SnapshotInventory(inventory),
				ExpiresAtUtc = utcNow + SessionTimeout
			};
			value.SetActivePlayer(byPlayer.PlayerUID, byPlayer.PlayerName);
			sessionsByKey[text] = value;
			AttachSession(value);
			EnsureCleanupListener();
			if (logContainerAccess)
			{
				QueueContainerRecord(LogActionType.ContainerOpen, byPlayer, value.Position, value.BlockCode, null, null, null, value.InventoryId);
			}
			QueueSnapshot(value, byPlayer, "open", null);
			return;
		}
		value.SetActivePlayer(byPlayer.PlayerUID, byPlayer.PlayerName);
		value.ExpiresAtUtc = utcNow + SessionTimeout;
		if (!value.TryGetInventory(out IInventory inventory2) || inventory2 != inventory)
		{
			RemoveSession(text, queueFinalSnapshot: false);
			EnsureCapacityForNewSession();
			value = new ContainerSession
			{
				SessionId = text,
				InventoryId = inventory.InventoryID,
				BlockCode = blockCode,
				Position = blockSel.Position.Copy(),
				InventoryRef = new WeakReference<IInventory>(inventory),
				SlotStates = SnapshotInventory(inventory),
				ExpiresAtUtc = utcNow + SessionTimeout
			};
			value.SetActivePlayer(byPlayer.PlayerUID, byPlayer.PlayerName);
			sessionsByKey[text] = value;
			AttachSession(value);
			EnsureCleanupListener();
		}
	}

	private void AttachSession(ContainerSession session)
	{
		if (session.TryGetInventory(out IInventory inventory) && inventory is InventoryBase inventoryBase)
		{
			string sessionId = session.SessionId;
			Action<int> value = (session.SlotModifiedHandler = (int slotId) =>
			{
				OnSlotModified(sessionId, slotId);
			});
			inventoryBase.SlotModified += value;
		}
	}

	private void DetachSession(ContainerSession session)
	{
		if (session.TryGetInventory(out IInventory inventory) && inventory is InventoryBase inventoryBase && session.SlotModifiedHandler != null)
		{
			inventoryBase.SlotModified -= session.SlotModifiedHandler;
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
		try
		{
			OnSlotModifiedCore(sessionId, slotId);
		}
		catch (Exception ex)
		{
			ReportHookFailure("SlotModified", ex);
		}
	}

	private void OnSlotModifiedCore(string sessionId, int slotId)
	{
		if (!sessionsByKey.TryGetValue(sessionId, out ContainerSession value) || value.Detached)
		{
			return;
		}
		if (!config.Enabled || (!config.LogContainerItemChanges && !config.LogContainerSnapshots))
		{
			RemoveSession(sessionId, queueFinalSnapshot: false);
			return;
		}
		if (DateTime.UtcNow >= value.ExpiresAtUtc)
		{
			RemoveSession(sessionId, config.LogContainerSnapshots, "timeout");
			return;
		}
		if (!value.TryGetInventory(out IInventory inventory))
		{
			Interlocked.Increment(ref sessionsDroppedBecauseInventoryExpired);
			RemoveSession(sessionId, queueFinalSnapshot: false);
			return;
		}
		TrackedSlotState trackedSlotState = ReadSlotState(inventory, slotId);
		if (!value.SlotStates.TryGetValue(slotId, out var value2))
		{
			value2 = TrackedSlotState.Empty;
		}
		if (!StatesEqual(value2, trackedSlotState))
		{
			if (config.LogContainerItemChanges)
			{
				EmitDeltaLogs(value, value2, trackedSlotState, slotId);
			}
			if (string.IsNullOrEmpty(trackedSlotState.ItemCode) || trackedSlotState.StackSize <= 0)
			{
				value.SlotStates.Remove(slotId);
			}
			else
			{
				value.SlotStates[slotId] = trackedSlotState;
			}
			if (config.LogContainerSnapshots && config.SnapshotOnSlotChange)
			{
				ScheduleSnapshot(value, slotId);
			}
		}
	}

	private void ScheduleSnapshot(ContainerSession session, int changedSlotId)
	{
		if (session.SnapshotCallbackId.HasValue)
		{
			return;
		}
		string sessionId = session.SessionId;
		session.SnapshotCallbackId = api.Event.RegisterCallback((float _) =>
		{
			try
			{
				if (sessionsByKey.TryGetValue(sessionId, out ContainerSession value) && !value.Detached)
				{
					value.SnapshotCallbackId = null;
					if (DateTime.UtcNow >= value.ExpiresAtUtc)
					{
						RemoveSession(sessionId, config.LogContainerSnapshots, "timeout");
					}
					else
					{
						QueueSnapshot(value, ResolvePlayer(value.PlayerUid), "slotmodified", changedSlotId);
					}
				}
			}
			catch (Exception ex)
			{
				ReportHookFailure("SnapshotCallback", ex);
			}
		}, config.SnapshotDebounceMs);
	}

	private void EmitDeltaLogs(ContainerSession session, TrackedSlotState previous, TrackedSlotState current, int slotId)
	{
		IServerPlayer byPlayer = ResolvePlayer(session.PlayerUid);
		if (!string.IsNullOrEmpty(previous.ItemCode) && previous.StackSize > 0)
		{
			if (previous.ItemCode == current.ItemCode)
			{
				int num = current.StackSize - previous.StackSize;
				if (num < 0)
				{
					QueueContainerRecord(LogActionType.ContainerTake, byPlayer, session.Position, session.BlockCode, previous.ItemCode, -num, slotId, session.InventoryId);
				}
				else if (num > 0)
				{
					QueueContainerRecord(LogActionType.ContainerPut, byPlayer, session.Position, session.BlockCode, current.ItemCode, num, slotId, session.InventoryId);
				}
				return;
			}
			QueueContainerRecord(LogActionType.ContainerTake, byPlayer, session.Position, session.BlockCode, previous.ItemCode, previous.StackSize, slotId, session.InventoryId);
		}
		if (!string.IsNullOrEmpty(current.ItemCode) && current.StackSize > 0)
		{
			QueueContainerRecord(LogActionType.ContainerPut, byPlayer, session.Position, session.BlockCode, current.ItemCode, current.StackSize, slotId, session.InventoryId);
		}
	}

	private void QueueContainerRecord(LogActionType action, IServerPlayer? byPlayer, BlockPos pos, string blockCode, string? itemCode, int? quantity, int? slotId, string? inventoryId)
	{
		queue.TryEnqueueFast(new QueuedEventPayload(DateTime.UtcNow, timeFormatter.GetCurrentStamp(), action, byPlayer?.PlayerUID ?? string.Empty, byPlayer?.PlayerName ?? string.Empty, worldId, ResolveDimension(pos), pos.X, pos.Y, pos.Z, blockCode, (itemCode == null) ? null : "itemCode", itemCode, quantity.HasValue ? "quantity" : null, quantity?.ToString(), slotId.HasValue ? "slotId" : null, slotId?.ToString(), string.IsNullOrWhiteSpace(inventoryId) ? null : "inventoryId", inventoryId));
	}

	private void QueueSnapshot(ContainerSession session, IServerPlayer? byPlayer, string reason, int? changedSlotId)
	{
		if (!config.Enabled || !config.LogContainerSnapshots || !session.TryGetInventory(out IInventory inventory))
		{
			return;
		}
		List<ContainerSnapshotSlot> list = SnapshotSlots(inventory);
		int num = 0;
		foreach (ContainerSnapshotSlot item in list)
		{
			num += item.StackSize;
		}
		ContainerSnapshotRecord record = new ContainerSnapshotRecord
		{
			TimestampUtc = DateTime.UtcNow,
			IngameDate = timeFormatter.GetCurrentStamp(),
			WorldId = worldId,
			Dimension = ResolveDimension(session.Position),
			X = session.Position.X,
			Y = session.Position.Y,
			Z = session.Position.Z,
			BlockCode = session.BlockCode,
			PlayerUid = (byPlayer?.PlayerUID ?? session.PlayerUid),
			PlayerName = (byPlayer?.PlayerName ?? session.PlayerName),
			InventoryId = session.InventoryId,
			Reason = reason,
			ChangedSlotId = changedSlotId,
			SlotCount = inventory.Count,
			NonEmptySlotCount = list.Count,
			TotalItemCount = num,
			Slots = list
		};
		snapshotQueue.TryEnqueue(record);
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
		DateTime utcNow = DateTime.UtcNow;
		List<string> list = new List<string>();
		IInventory inventory;
		foreach (KeyValuePair<string, ContainerSession> item in sessionsByKey)
		{
			if (item.Value.Detached || utcNow >= item.Value.ExpiresAtUtc || !item.Value.TryGetInventory(out inventory))
			{
				list.Add(item.Key);
			}
		}
		foreach (string item2 in list)
		{
			if (sessionsByKey.TryGetValue(item2, out ContainerSession value))
			{
				bool flag = value.TryGetInventory(out inventory);
				if (!flag)
				{
					Interlocked.Increment(ref sessionsDroppedBecauseInventoryExpired);
				}
				RemoveSession(item2, flag && config.LogContainerSnapshots, "timeout");
			}
		}
		EnforceSessionCapacity(config.MaxTrackedContainerSessions);
		StopCleanupListenerIfIdle();
	}

	private void OnPlayerJoin(IServerPlayer player)
	{
		if (player != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			onlinePlayersByUid[player.PlayerUID] = new WeakReference<IServerPlayer>(player);
		}
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		try
		{
			OnPlayerDisconnectCore(player);
		}
		catch (Exception ex)
		{
			ReportHookFailure("PlayerDisconnect", ex);
		}
	}

	private void OnPlayerDisconnectCore(IServerPlayer player)
	{
		if (player == null)
		{
			return;
		}
		onlinePlayersByUid.Remove(player.PlayerUID);
		List<string> list = new List<string>();
		foreach (KeyValuePair<string, ContainerSession> item in sessionsByKey)
		{
			if (item.Value.PlayerUid.Equals(player.PlayerUID, StringComparison.Ordinal))
			{
				QueueSnapshot(item.Value, player, "playerdisconnect", null);
				DetachSession(item.Value);
				list.Add(item.Key);
			}
		}
		foreach (string item2 in list)
		{
			sessionsByKey.Remove(item2);
		}
		StopCleanupListenerIfIdle();
	}

	private void EnsureCapacityForNewSession()
	{
		CleanupDeadInventorySessions();
		EnforceSessionCapacity(Math.Max(16, config.MaxTrackedContainerSessions) - 1);
	}

	private void CleanupDeadInventorySessions()
	{
		if (sessionsByKey.Count == 0)
		{
			return;
		}
		List<string> list = (from pair in sessionsByKey
			where pair.Value.Detached || !pair.Value.TryGetInventory(out IInventory _)
			select pair.Key).ToList();
		foreach (string item in list)
		{
			Interlocked.Increment(ref sessionsDroppedBecauseInventoryExpired);
			RemoveSession(item, queueFinalSnapshot: false);
		}
	}

	private void EnforceSessionCapacity(int capacity)
	{
		capacity = Math.Clamp(capacity, 0, 8192);
		if (sessionsByKey.Count <= capacity)
		{
			return;
		}
		int count = sessionsByKey.Count - capacity;
		foreach (string item in (from pair in sessionsByKey.OrderBy((KeyValuePair<string, ContainerSession> pair) => pair.Value.ExpiresAtUtc).ThenBy((KeyValuePair<string, ContainerSession> pair) => pair.Key, StringComparer.Ordinal).Take(count)
			select pair.Key).ToList())
		{
			if (RemoveSession(item, queueFinalSnapshot: false))
			{
				Interlocked.Increment(ref sessionsEvictedForCapacity);
			}
		}
	}

	private bool RemoveSession(string key, bool queueFinalSnapshot, string finalReason = "")
	{
		if (!sessionsByKey.TryGetValue(key, out ContainerSession value))
		{
			return false;
		}
		if (queueFinalSnapshot && !string.IsNullOrWhiteSpace(finalReason))
		{
			QueueSnapshot(value, ResolvePlayer(value.PlayerUid), finalReason, null);
		}
		DetachSession(value);
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
		foreach (ContainerSession item in sessionsByKey.Values.ToList())
		{
			if (queueFinalSnapshots)
			{
				QueueSnapshot(item, ResolvePlayer(item.PlayerUid), "disabled", null);
			}
			DetachSession(item);
		}
		sessionsByKey.Clear();
		StopCleanupListenerIfIdle();
	}

	private void EnsureCleanupListener()
	{
		if (cleanupListenerId < 0 && sessionsByKey.Count != 0)
		{
			cleanupListenerId = api.Event.RegisterGameTickListener((float _) =>
			{
				CleanupExpiredSessionsSafe();
			}, 30000);
		}
	}

	private void StopCleanupListenerIfIdle()
	{
		if (sessionsByKey.Count == 0 && cleanupListenerId >= 0)
		{
			api.Event.UnregisterGameTickListener(cleanupListenerId);
			cleanupListenerId = -1L;
		}
	}

	private void CleanupExpiredSessionsSafe()
	{
		try
		{
			CleanupExpiredSessions();
		}
		catch (Exception ex)
		{
			ReportHookFailure("CleanupExpiredSessions", ex);
		}
	}

	private void ReportHookFailure(string hookName, Exception ex)
	{
		Interlocked.Increment(ref hookFailures);
		long ticks = DateTime.UtcNow.Ticks;
		long num = Interlocked.Read(in lastHookWarningTicks);
		if (ticks - num >= TimeSpan.FromSeconds(30L).Ticks && Interlocked.CompareExchange(ref lastHookWarningTicks, ticks, num) == num)
		{
			api.Logger.Warning("[BlockLogger] Container hook {0} failed; record skipped. Failures={1}. {2}: {3}", hookName, Interlocked.Read(in hookFailures), ex.GetType().Name, ex.Message);
		}
	}

	private IServerPlayer? ResolvePlayer(string playerUid)
	{
		if (string.IsNullOrWhiteSpace(playerUid) || !onlinePlayersByUid.TryGetValue(playerUid, out WeakReference<IServerPlayer> value))
		{
			return null;
		}
		if (value.TryGetTarget(out var target) && target != null)
		{
			return target;
		}
		onlinePlayersByUid.Remove(playerUid);
		return null;
	}

	private static Dictionary<int, TrackedSlotState> SnapshotInventory(IInventory inventory)
	{
		Dictionary<int, TrackedSlotState> dictionary = new Dictionary<int, TrackedSlotState>();
		for (int i = 0; i < inventory.Count; i++)
		{
			TrackedSlotState value = ReadSlotState(inventory, i);
			if (!string.IsNullOrEmpty(value.ItemCode) && value.StackSize > 0)
			{
				dictionary[i] = value;
			}
		}
		return dictionary;
	}

	private static List<ContainerSnapshotSlot> SnapshotSlots(IInventory inventory)
	{
		List<ContainerSnapshotSlot> list = new List<ContainerSnapshotSlot>();
		for (int i = 0; i < inventory.Count; i++)
		{
			ItemStack itemStack = inventory[i]?.Itemstack;
			string text = itemStack?.Collectible?.Code?.ToShortString();
			int num = itemStack?.StackSize ?? 0;
			if (!string.IsNullOrWhiteSpace(text) && num > 0)
			{
				list.Add(new ContainerSnapshotSlot
				{
					SlotId = i,
					ItemCode = text,
					StackSize = num
				});
			}
		}
		return list;
	}

	private static TrackedSlotState ReadSlotState(IInventory inventory, int slotId)
	{
		if (slotId < 0 || slotId >= inventory.Count)
		{
			return TrackedSlotState.Empty;
		}
		ItemStack itemStack = inventory[slotId]?.Itemstack;
		return new TrackedSlotState(itemStack?.Collectible?.Code?.ToShortString() ?? string.Empty, itemStack?.StackSize ?? 0);
	}

	private bool ShouldIgnore(string blockCode)
	{
		foreach (string ignoredBlockCodePrefix in config.IgnoredBlockCodePrefixes)
		{
			if (!string.IsNullOrWhiteSpace(ignoredBlockCodePrefix) && blockCode.StartsWith(ignoredBlockCodePrefix, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static bool StatesEqual(TrackedSlotState a, TrackedSlotState b)
	{
		return a.ItemCode == b.ItemCode && a.StackSize == b.StackSize;
	}

	private static string BuildSessionKey(string inventoryId, BlockPos pos)
	{
		return $"{inventoryId}|{pos.X}|{pos.Y}|{pos.Z}|{pos.dimension}";
	}

	private static string ResolveDimension(BlockPos pos)
	{
		return pos.dimension.ToString();
	}
}
