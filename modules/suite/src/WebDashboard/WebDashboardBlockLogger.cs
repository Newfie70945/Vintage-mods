using System;
using System.Linq;
using System.Text.Json;
using BlockLogger.Config;

namespace BlockLogger.WebDashboard;

public sealed partial class WebDashboardService
{
    private static readonly string[] RecordingBools = ["Enabled","LogBlockBreak","LogBlockPlace","LogBlockUse","LogContainerAccess","LogContainerItemChanges","LogContainerSnapshots","SnapshotOnSlotChange","LogEntityInteract","LogEntitySpawn","LogEntityDeath","LogEntityDespawn","AggregateEntitySpawnDespawn","AdaptiveEntityAggregation"];
    private static readonly string[] RecordingInts = ["FlushIntervalMs","MaxQueuedEvents","EmergencyOverflowCapacity","SnapshotDebounceMs","MaxTrackedContainerSessions","EntityAggregateWindowMs","BulkEntityPositionSampleIntervalMs","DefaultRadius","DefaultDisplayCount"];
    private object BlockLoggerRecordingSnapshot()
    {
        var storage=module.Storage;var c=module.Config;
        return new {featureEnabled=module.Features.BlockLogger,ready=storage?.IsReady??false,settings=JsonSerializer.SerializeToElement(c,jsonOptions),
            recording=module.Features.BlockLogger&&c.Enabled&&storage?.IsReady==true,
            snapshotRecovery=c.LogContainerItemChanges&&c.LogContainerSnapshots&&c.SnapshotOnSlotChange,
            activeSessions=module.ActiveContainerSessions,pendingEvents=storage?.PendingEvents??0,pendingSnapshots=storage?.PendingSnapshots??0,
            writtenEvents=storage?.WrittenEvents??0,writtenSnapshots=storage?.WrittenSnapshots??0,droppedEvents=storage?.DroppedEvents??0,droppedSnapshots=storage?.DroppedSnapshots??0,
            note="Recording settings apply live to future activity. Open/reopen containers to start a tracked session. Take/put rows and snapshots are separate records; enabling snapshots cannot recreate old contents. Module feature changes require restart."};
    }
    private object SaveBlockLoggerRecording(string body)
    {
        using var input=JsonDocument.Parse(body);if(input.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Recording settings must be an object.");
        var old=JsonSerializer.Deserialize<BlockLoggerConfig>(JsonSerializer.Serialize(module.Config,jsonOptions),jsonOptions)!;
        var next=JsonSerializer.Deserialize<BlockLoggerConfig>(JsonSerializer.Serialize(old,jsonOptions),jsonOptions)!;
        foreach(var name in RecordingBools.Concat(RecordingInts))
        {
            string key=char.ToLowerInvariant(name[0])+name[1..];if(!input.RootElement.TryGetProperty(key,out var value))continue;
            var property=typeof(BlockLoggerConfig).GetProperty(name)!;
            if(property.PropertyType==typeof(bool)){if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException(key+" must be true or false.");property.SetValue(next,value.GetBoolean());}
            else {if(!value.TryGetInt32(out var number))throw new ArgumentException(key+" must be a whole number.");property.SetValue(next,number);}
        }
        foreach(var name in new[]{"IgnoredBlockCodePrefixes","IgnoredEntityCodePrefixes"})
        {
            string key=char.ToLowerInvariant(name[0])+name[1..];if(!input.RootElement.TryGetProperty(key,out var value))continue;
            if(value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()>128)throw new ArgumentException(key+" must contain at most 128 prefixes.");
            var prefixes=value.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String?v.GetString()?.Trim()??"":throw new ArgumentException("Prefixes must be text.")).ToList();
            if(prefixes.Any(v=>v.Length is 0 or >256||v.Any(char.IsControl)))throw new ArgumentException("Prefixes must contain 1–256 printable characters.");
            typeof(BlockLoggerConfig).GetProperty(name)!.SetValue(next,prefixes.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        next.Clamp();module.Config.ApplyFrom(next);
        try{module.SaveRuntimeConfig();}
        catch{module.Config.ApplyFrom(old);try{module.SaveRuntimeConfig();}catch(Exception e){api.Logger.Error("[BlockLogger] Recording settings rollback could not be persisted: {0}",e.Message);}throw;}
        return BlockLoggerRecordingSnapshot();
    }
}
