using System.Reflection;
using System.Runtime.InteropServices;
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
    // Replacing LINQ also bypasses patches on its closed generic operators.
    // Let the engine's compatibility monitor fall back if another mod owns those.
    public static MethodBase[] Dependencies => new MethodBase[] { Target,
        AccessTools.Method(typeof(CollectionsMarshal), nameof(CollectionsMarshal.AsSpan))!.MakeGenericMethod(typeof(EntityDespawn)) }
        .Concat(PatchProcessor.GetOriginalInstructions(Target).Select(i => i.operand).OfType<MethodInfo>()
            .Where(m => m.DeclaringType == typeof(Enumerable))).Distinct().ToArray();
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
        // Borrow only for this synchronous pass. No shared packet arrays, retained
        // spans or input writes. Keep the native three-pass read/allocation order.
        var items=CollectionsMarshal.AsSpan(despawns);
        int count=items.Length;
        long[] ids=count==0?Array.Empty<long>():new long[count];
        for(int i=0;i<items.Length;i++) ids[i]=items[i].EntityId;
        items=CollectionsMarshal.AsSpan(despawns);
        int[] reasons=items.Length==0?Array.Empty<int>():new int[items.Length];
        for(int i=0;i<items.Length;i++)
        {
            var data=items[i].DespawnData;
            reasons[i]=(int)(data!=null?data.Reason:EnumDespawnReason.Death);
        }
        items=CollectionsMarshal.AsSpan(despawns);
        int[] sources=items.Length==0?Array.Empty<int>():new int[items.Length];
        for(int i=0;i<items.Length;i++)
        {
            var data=items[i].DespawnData;
            sources[i]=(int)(data!=null?(data.DamageSourceForDeath!=null?data.DamageSourceForDeath.Source:EnumDamageSource.Unknown):EnumDamageSource.Block);
        }
        var packet=new Packet_EntityDespawn();
        packet.SetEntityId(ids);packet.SetDeathDamageSource(sources);packet.SetDespawnReason(reasons);
        __result=new Packet_Server { Id=36,EntityDespawn=packet };
        Interlocked.Increment(ref calls);Interlocked.Add(ref entities,count);
        return false;
    }
}
