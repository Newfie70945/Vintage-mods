using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BlockLogger.Models;

namespace BlockLogger.Services;

public static class RollbackRecoveryPlanner
{
	private static bool Mutation(LogEventRecord r)
	{
		LogActionType action = r.Action;
		if ((uint)(action - 1) <= 1u || (uint)(action - 5) <= 1u)
		{
			return true;
		}
		return false;
	}

	private static bool BlockMutation(LogEventRecord r)
	{
		LogActionType action = r.Action;
		if ((uint)(action - 1) <= 1u)
		{
			return true;
		}
		return false;
	}

	public static RecoveryBuild Build(RollbackHistory history, DateTime from, DateTime to, bool inventories, bool entities, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		RecoveryBuild recoveryBuild = new RecoveryBuild();
		Dictionary<RecoveryCell, List<ContainerSnapshotRecord>> dictionary = history.Snapshots.GroupBy((ContainerSnapshotRecord s) =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return RecoveryCell.At(s);
		}).ToDictionary((IGrouping<RecoveryCell, ContainerSnapshotRecord> g) => g.Key, (IGrouping<RecoveryCell, ContainerSnapshotRecord> g) => (from s in g
			orderby s.TimestampUtc descending, s.StorageId descending
			select s).ToList());
		foreach (IGrouping<RecoveryCell, LogEventRecord> item in history.Events.Where((LogEventRecord r) =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return r.Action <= LogActionType.ContainerPut;
		}).GroupBy(RecoveryCell.At))
		{
			cancellationToken.ThrowIfCancellationRequested();
			List<LogEventRecord> source = (from r in item
				orderby r.TimestampUtc descending, r.StorageId descending
				select r).ToList();
			List<LogEventRecord> list = source.Where((LogEventRecord r) => (history.AreaMode || r.PlayerUid == history.PlayerUid) && r.TimestampUtc >= from && r.TimestampUtc <= to && Mutation(r)).ToList();
			if (list.Count == 0)
			{
				continue;
			}
			DateTime earliest = list.Min((LogEventRecord r) => r.TimestampUtc);
			long earliestId = list.Where((LogEventRecord r) => r.TimestampUtc == earliest).Min((LogEventRecord r) => r.StorageId);
			List<LogEventRecord> source2 = source.Where((LogEventRecord r) => r.TimestampUtc > earliest || (r.TimestampUtc == earliest && r.StorageId >= earliestId)).ToList();
			if (source2.Any((LogEventRecord r) => r.Meta.ContainsKey("rollbackToken") || (!history.AreaMode && r.PlayerUid != history.PlayerUid) || r.TimestampUtc > to))
			{
				recoveryBuild.Skip("Later activity/other actor/previous rollback at location");
				continue;
			}
			dictionary.TryGetValue(item.Key, out var value);
			if (value == null)
			{
				value = new List<ContainerSnapshotRecord>();
			}
			if (value.Any((ContainerSnapshotRecord s) => s.TimestampUtc >= earliest && (history.AreaMode ? (s.TimestampUtc > to) : (s.PlayerUid != history.PlayerUid))))
			{
				recoveryBuild.Skip("Later/shared container snapshot");
				continue;
			}
			List<LogEventRecord> list2 = list.Where(BlockMutation).ToList();
			string before = "";
			string text = "";
			bool flag = true;
			foreach (LogEventRecord item2 in list2)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string text2 = item2.Meta.GetValueOrDefault("oldBlockCode");
				string text3 = item2.Meta.GetValueOrDefault("newBlockCode");
				if (string.IsNullOrWhiteSpace(text2) && item2.Action == LogActionType.BlockBreak)
				{
					text2 = item2.BlockCode;
				}
				if (string.IsNullOrWhiteSpace(text3))
				{
					text3 = ((item2.Action == LogActionType.BlockBreak) ? "air" : item2.BlockCode);
				}
				if (string.IsNullOrWhiteSpace(text2) || string.IsNullOrWhiteSpace(text3) || text2 == "unknown" || text3 == "unknown")
				{
					flag = false;
					break;
				}
				if (text.Length == 0)
				{
					text = text3;
				}
				else if (Canonical(before) != Canonical(text3))
				{
					flag = false;
					break;
				}
				before = text2;
			}
			if (!flag)
			{
				recoveryBuild.Skip("Missing old block code or discontinuous block history");
				continue;
			}
			List<LogEventRecord> list3 = list.Where((LogEventRecord r) =>
			{
				LogActionType action = r.Action;
				return (uint)(action - 5) <= 1u;
			}).ToList();
			RecoveryOperation recoveryOperation = null;
			if (list2.Count > 0 && Canonical(before) != Canonical(text))
			{
				recoveryOperation = new RecoveryOperation
				{
					Kind = "block",
					Cell = item.Key,
					Before = before,
					After = text
				};
			}
			if (inventories && (list3.Count > 0 || list2.Count > 0))
			{
				DateTime first = list.Min((LogEventRecord r) => r.TimestampUtc);
				ContainerSnapshotRecord baseline = value.FirstOrDefault((ContainerSnapshotRecord s) => s.TimestampUtc < first && (before.Length == 0 || Canonical(s.BlockCode) == Canonical(before)));
				if (baseline != null && ValidSnapshot(baseline))
				{
					List<LogEventRecord> list4 = (from r in source.Where((LogEventRecord r) =>
						{
							bool flag3 = r.TimestampUtc > baseline.TimestampUtc && r.TimestampUtc <= to;
							bool flag4 = flag3;
							if (flag4)
							{
								LogActionType action = r.Action;
								bool flag5 = (uint)(action - 5) <= 1u;
								flag4 = flag5;
							}
							return flag4;
						})
						orderby r.TimestampUtc, r.StorageId
						select r).ToList();
					Dictionary<int, RecoverySlot> dictionary2 = ReadSlots(baseline);
					Dictionary<int, RecoverySlot> dictionary3 = ReadSlots(baseline);
					bool flag2 = true;
					if (!string.IsNullOrWhiteSpace(baseline.InventoryId) && list4.Any((LogEventRecord r) => r.Meta.TryGetValue("inventoryId", out string value2) && !string.IsNullOrWhiteSpace(value2) && value2 != baseline.InventoryId))
					{
						flag2 = false;
					}
					foreach (LogEventRecord item3 in list4)
					{
						cancellationToken.ThrowIfCancellationRequested();
						if (item3.TimestampUtc < earliest || (item3.TimestampUtc == earliest && item3.StorageId < earliestId))
						{
							flag2 &= ApplyDelta(dictionary2, item3, baseline.SlotCount);
						}
						flag2 &= ApplyDelta(dictionary3, item3, baseline.SlotCount);
					}
					if (source.Any((LogEventRecord r) => BlockMutation(r) && r.TimestampUtc > baseline.TimestampUtc && (r.TimestampUtc < earliest || (r.TimestampUtc == earliest && r.StorageId < earliestId))))
					{
						flag2 = false;
					}
					ContainerSnapshotRecord containerSnapshotRecord = value.FirstOrDefault((ContainerSnapshotRecord s) => s.TimestampUtc >= first);
					if (list2.Count == 0 || (before.Length > 0 && Canonical(before) == Canonical(text)))
					{
						DateTime dateTime = list.Max((LogEventRecord r) => r.TimestampUtc);
						if (containerSnapshotRecord == null || containerSnapshotRecord.TimestampUtc <= dateTime || !ValidSnapshot(containerSnapshotRecord) || Canonical(containerSnapshotRecord.BlockCode) != Canonical(baseline.BlockCode) || (!string.IsNullOrWhiteSpace(baseline.InventoryId) && containerSnapshotRecord.InventoryId != baseline.InventoryId) || containerSnapshotRecord.SlotCount != baseline.SlotCount || !Equal(dictionary3, ReadSlots(containerSnapshotRecord)))
						{
							flag2 = false;
						}
					}
					if (flag2)
					{
						if (recoveryOperation == null && (list3.Count > 0 || (list2.Count > 0 && before.Length > 0 && Canonical(before) == Canonical(text))))
						{
							recoveryOperation = new RecoveryOperation
							{
								Kind = "inventory",
								Cell = item.Key,
								Before = baseline.BlockCode,
								After = baseline.BlockCode
							};
						}
						if (recoveryOperation != null)
						{
							recoveryOperation.InventoryBefore = Sorted(dictionary2);
							recoveryOperation.InventoryAfter = Sorted(dictionary3);
							recoveryOperation.SlotCount = baseline.SlotCount;
						}
					}
					else
					{
						recoveryBuild.Skip("Inventory deltas/snapshots disagree or are incomplete");
					}
				}
				else if (list3.Count > 0)
				{
					recoveryBuild.Skip("No valid pre-change container snapshot");
				}
			}
			else if (list3.Count > 0)
			{
				recoveryBuild.Skip("Inventory recovery requires --inventories (saved codes/counts only)");
			}
			if (recoveryOperation != null)
			{
				recoveryOperation.SourceIds = (from r in list
					select r.StorageId into x
					orderby x
					select x).ToList();
				Stamp(recoveryOperation, history.WorldId);
				recoveryBuild.Operations.Add(recoveryOperation);
			}
		}
		IEnumerable<LogEventRecord> enumerable = history.Events.Where((LogEventRecord r) => r.Action == LogActionType.EntityDeath && r.TimestampUtc >= from && r.TimestampUtc <= to && (history.AreaMode || r.PlayerUid == history.PlayerUid || r.Meta.GetValueOrDefault("sourcePlayerUid") == history.PlayerUid));
		HashSet<long> hashSet = new HashSet<long>();
		foreach (LogEventRecord item4 in enumerable)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!entities)
			{
				recoveryBuild.Skip("Entity death: replacement requires --replace-entities");
				continue;
			}
			if (!long.TryParse(item4.Meta.GetValueOrDefault("entityId"), out var result) || result <= 0 || !hashSet.Add(result))
			{
				recoveryBuild.Skip("Missing/duplicate original entity ID");
				continue;
			}
			string valueOrDefault = item4.Meta.GetValueOrDefault("entityCode");
			string text4 = item4.Meta.GetValueOrDefault("entityClass") ?? "";
			if (string.IsNullOrWhiteSpace(valueOrDefault) || text4.Contains("Player", StringComparison.OrdinalIgnoreCase) || Canonical(valueOrDefault) == "game:player")
			{
				recoveryBuild.Skip("Player or unknown entity cannot be replaced");
				continue;
			}
			RecoveryOperation recoveryOperation2 = new RecoveryOperation
			{
				Kind = "entity-replacement",
				Cell = RecoveryCell.At(item4),
				EntityCode = valueOrDefault,
				OriginalEntityId = result,
				SourceIds = new List<long> { item4.StorageId }
			};
			Stamp(recoveryOperation2, history.WorldId);
			recoveryBuild.Operations.Add(recoveryOperation2);
		}
		return recoveryBuild;
	}

	public static string Canonical(string code)
	{
		return code.Contains(':') ? code : ("game:" + code);
	}

	private static void Stamp(RecoveryOperation op, string world)
	{
		string text = string.Join(",", op.SourceIds);
		op.ReceiptKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(world + ":" + op.Kind + ":" + ((op.Kind == "entity-replacement") ? op.OriginalEntityId.ToString(CultureInfo.InvariantCulture) : (op.Cell.Key + ":" + text)))));
		op.Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(op.ReceiptKey + ":" + Canonical(op.Before) + ":" + Canonical(op.After) + ":" + op.SlotCount + ":" + string.Join(";", (op.InventoryBefore ?? new List<RecoverySlot>()).Select((RecoverySlot s) => $"{s.Slot}:{Canonical(s.Code)}:{s.Count}")) + ":" + string.Join(";", (op.InventoryAfter ?? new List<RecoverySlot>()).Select((RecoverySlot s) => $"{s.Slot}:{Canonical(s.Code)}:{s.Count}")) + ":" + op.EntityCode + ":" + op.OriginalEntityId)));
	}

	private static bool ValidSnapshot(ContainerSnapshotRecord s)
	{
		return s.SlotCount > 0 && s.SlotCount <= 4096 && s.Slots.All((ContainerSnapshotSlot x) => x.SlotId >= 0 && x.SlotId < s.SlotCount && x.StackSize > 0 && !string.IsNullOrWhiteSpace(x.ItemCode)) && s.Slots.Select((ContainerSnapshotSlot x) => x.SlotId).Distinct().Count() == s.Slots.Count && s.Slots.Count == s.NonEmptySlotCount && ((IEnumerable<ContainerSnapshotSlot>)s.Slots).Sum((Func<ContainerSnapshotSlot, long>)((ContainerSnapshotSlot x) => x.StackSize)) == s.TotalItemCount;
	}

	private static Dictionary<int, RecoverySlot> ReadSlots(ContainerSnapshotRecord s)
	{
		return s.Slots.ToDictionary((ContainerSnapshotSlot x) => x.SlotId, (ContainerSnapshotSlot x) => new RecoverySlot(x.SlotId, Canonical(x.ItemCode), x.StackSize));
	}

	private static List<RecoverySlot> Sorted(Dictionary<int, RecoverySlot> s)
	{
		return s.Values.OrderBy((RecoverySlot x) => x.Slot).ToList();
	}

	private static bool Equal(Dictionary<int, RecoverySlot> a, Dictionary<int, RecoverySlot> b)
	{
		return a.Count == b.Count && a.All((KeyValuePair<int, RecoverySlot> x) => b.TryGetValue(x.Key, out RecoverySlot value) && value == x.Value);
	}

	private static bool ApplyDelta(Dictionary<int, RecoverySlot> slots, LogEventRecord r, int count)
	{
		if (!int.TryParse(r.Meta.GetValueOrDefault("slotId"), out var result) || result < 0 || result >= count || !int.TryParse(r.Meta.GetValueOrDefault("quantity"), out var result2) || result2 <= 0)
		{
			return false;
		}
		string valueOrDefault = r.Meta.GetValueOrDefault("itemCode");
		if (string.IsNullOrWhiteSpace(valueOrDefault))
		{
			return false;
		}
		valueOrDefault = Canonical(valueOrDefault);
		slots.TryGetValue(result, out RecoverySlot value);
		if (value != null && value.Code != valueOrDefault)
		{
			return false;
		}
		long num = (value?.Count ?? 0) + ((r.Action == LogActionType.ContainerTake) ? (-result2) : result2);
		if (num < 0 || num > int.MaxValue)
		{
			return false;
		}
		if (num == 0)
		{
			slots.Remove(result);
		}
		else
		{
			slots[result] = new RecoverySlot(result, valueOrDefault, (int)num);
		}
		return true;
	}
}
