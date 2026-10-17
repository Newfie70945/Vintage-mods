using System;
using System.Collections.Generic;

namespace BlockLogger.Models;

public sealed class ContainerSnapshotRecord
{
    [Newtonsoft.Json.JsonIgnore] public long StorageId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string IngameDate { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string Dimension { get; set; } = "overworld";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public string BlockCode { get; set; } = string.Empty;
    public string PlayerUid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string InventoryId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int? ChangedSlotId { get; set; }
    public int SlotCount { get; set; }
    public int NonEmptySlotCount { get; set; }
    public int TotalItemCount { get; set; }
    public List<ContainerSnapshotSlot> Slots { get; set; } = new();
}
