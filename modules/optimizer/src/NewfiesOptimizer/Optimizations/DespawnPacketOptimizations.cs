using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Common;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Keep all three output arrays independently owned by their outgoing packet.
// Replace only LINQ projection/iterator overhead; the three stock passes remain.
public static class DespawnPacketOptimizations
{
    private static volatile bool enabled;
    private static long calls, entities;
    public static MethodInfo Target => AccessTools.Method(typeof(ServerPackets), nameof(ServerPackets.GetEntityDespawnPacket), [typeof(List<EntityDespawn>)]);
    public static MethodInfo PrefixMethod => AccessTools.Method(typeof(DespawnPacketOptimizations), nameof(Prefix));
    public static void Configure(bool value) => enabled = value;
    public static (long Calls,long Entities) Counters => (Interlocked.Read(ref calls),Interlocked.Read(ref entities));
    public static bool ValidateContract(out string reason)
    {
        bool valid=Target?.ReturnType==typeof(Packet_Server) && typeof(EntityDespawn).GetField(nameof(EntityDespawn.EntityId))?.FieldType==typeof(long);
        reason=valid?"Official despawn packet projection contract verified.":"Despawn packet projection contract differs.";
        return valid;
    }
    public static bool Prefix(List<EntityDespawn> despawns, ref Packet_Server __result)
    {
        // Preserve stock ArgumentNullException details instead of manufacturing them.
        if (!enabled || despawns is null) return true;
        int count=despawns.Count;
        long[] ids=count==0?Array.Empty<long>():new long[count];
        int index=0;
        foreach(var item in despawns) ids[index++]=item.EntityId;
        int[] reasons=count==0?Array.Empty<int>():new int[count];
        index=0;
        foreach(var item in despawns) reasons[index++]=(int)(item.DespawnData!=null?item.DespawnData.Reason:EnumDespawnReason.Death);
        int[] sources=count==0?Array.Empty<int>():new int[count];
        index=0;
        foreach(var item in despawns) sources[index++]=(int)(item.DespawnData!=null?(item.DespawnData.DamageSourceForDeath!=null?item.DespawnData.DamageSourceForDeath.Source:EnumDamageSource.Unknown):EnumDamageSource.Block);
        var packet=new Packet_EntityDespawn();
        packet.SetEntityId(ids);packet.SetDeathDamageSource(sources);packet.SetDespawnReason(reasons);
        __result=new Packet_Server { Id=36,EntityDespawn=packet };
        Interlocked.Increment(ref calls);Interlocked.Add(ref entities,count);
        return false;
    }
}
