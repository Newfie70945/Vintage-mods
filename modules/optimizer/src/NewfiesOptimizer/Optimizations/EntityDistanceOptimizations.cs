using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Opt-in scheduling policy. Never skip Entity.OnGameTick or ProcessRunningTasks:
// hunger, breeding, health, physics and currently running AI keep their stock ticks.
public static class EntityDistanceOptimizations
{
    private static volatile bool ai, wander, repulsion, safety;
    private static int radius = 64, aiInterval = 250, wanderInterval = 1500, repulsionInterval = 100;
    private static string[] included = ["game:*"], excluded = ["game:player", "game:trader-*"];
    private static ConditionalWeakTable<Entity, State> states = new();
    private static long aiSkipped, wanderSkipped, repulsionSkipped, failures;
    private sealed class State
    {
        public long AiDue, WanderDue, RepulsionDue, HurtUntil;
        public string? CodeDomain,CodePath; public bool AllowedCode;
        public long DistanceUntil;public bool Far;public int Dimension;public double X,Y,Z;
    }
    public static MethodInfo AiTarget => AccessTools.Method(typeof(AiTaskManager), "StartNewTasks");
    public static MethodInfo HurtTarget => AccessTools.Method(typeof(AiTaskManager), "OnEntityHurt");
    public static MethodInfo WanderTarget => AccessTools.Method(typeof(AiTaskWander), "ShouldExecute");
    public static MethodInfo RepulsionTarget => AccessTools.Method(typeof(EntityBehaviorRepulseAgents), "OnGameTick");
    public static MethodInfo[] Dependencies(MethodInfo target) => [target, HurtTarget,
        AccessTools.Method(typeof(AiTaskManager), "ProcessRunningTasks"), AccessTools.Method(typeof(AiTaskManager), "OnGameTick")];
    public static void Settings(int protectRadius, int aiMs, int wanderMs, int repulseMs, string[] allow, string[] deny)
    {
        radius=protectRadius;aiInterval=aiMs;wanderInterval=wanderMs;repulsionInterval=repulseMs;
        included=(string[])allow.Clone();excluded=(string[])deny.Clone();states=new();
    }
    public static void ConfigureAi(bool value)=>ai=value;
    public static void ConfigureWander(bool value)=>wander=value;
    public static void ConfigureRepulsion(bool value)=>repulsion=value;
    public static void ConfigureSafety(bool value){safety=value;if(!value)states=new();}
    public static (long Ai,long Wander,long Repulsion,long Failures) Counters =>
        (Interlocked.Read(ref aiSkipped),Interlocked.Read(ref wanderSkipped),Interlocked.Read(ref repulsionSkipped),Interlocked.Read(ref failures));
    public static bool ValidateContract(out string reason)
    {
        bool valid=AiTarget?.ReturnType==typeof(void)&&HurtTarget?.ReturnType==typeof(void)&&WanderTarget?.ReturnType==typeof(bool)&&
            RepulsionTarget?.ReturnType==typeof(void)&&AccessTools.Field(typeof(AiTaskManager),"entity")?.FieldType==typeof(Entity);
        reason=valid?"Official task-start, hurt, wander and repulsion contracts verified; full entity ticks retained.":"Entity scheduling contract differs.";return valid;
    }
    public static bool Wildcard(string pattern,string text)
    {
        int p=0,t=0,star=-1,retry=0;
        while(t<text.Length){if(p<pattern.Length&&char.ToLowerInvariant(pattern[p])==char.ToLowerInvariant(text[t])){p++;t++;}
            else if(p<pattern.Length&&pattern[p]=='*'){star=p++;retry=t;}
            else if(star>=0){p=star+1;t=++retry;}else return false;}
        while(p<pattern.Length&&pattern[p]=='*')p++;return p==pattern.Length;
    }
    // Stable stagger spreads work; first use always runs and re-entering the safe
    // radius removes prior delays on the next <=100ms proximity refresh.
    public static bool Defer(ref long due,long now,int interval,long entityId)
    {
        long previous=Volatile.Read(ref due);if(previous>now)return true;
        Interlocked.Exchange(ref due,now+interval+(previous==0?(long)((ulong)entityId%31):0));return false;
    }
    public static void HurtPrefix(Entity ___entity)
    {
        if(safety&&___entity!=null)Interlocked.Exchange(ref states.GetValue(___entity,static _=>new()).HurtUntil,Environment.TickCount64+10000);
    }
    private static bool BasicEligible(Entity entity)
    {
        return safety && entity is EntityAgent agent && entity is not EntityPlayer && entity.Api is ICoreServerAPI &&
            entity.World is ServerMain && entity.Code!=null && entity.Properties?.Server!=null && entity.Alive &&
            entity.IsInteractable && !entity.AlwaysActive && !entity.Swimming && !entity.InLava && !entity.IsOnFire &&
            entity.OnGround && agent.MountedOn==null;
    }
    private static bool Eligible(Entity entity,State state,AiTaskManager? manager)
    {
        if(Volatile.Read(ref state.HurtUntil)>Environment.TickCount64)return false;
        var server=(ServerMain)entity.World;
        // Custom runtime entities/behaviors/tasks opt out automatically. Code filters
        // let owners narrow this further; they cannot override the safety exemptions.
        if(entity.GetType().Assembly!=typeof(EntityAgent).Assembly&&entity.GetType().Assembly!=typeof(AiTaskManager).Assembly)return false;
        if(state.CodeDomain!=entity.Code.Domain||state.CodePath!=entity.Code.Path)
        {
            string code=entity.Code.ToString();bool allowed=false;
            foreach(string pattern in included)if(Wildcard(pattern,code)){allowed=true;break;}
            foreach(string pattern in excluded)if(Wildcard(pattern,code)){allowed=false;break;}
            state.CodeDomain=entity.Code.Domain;state.CodePath=entity.Code.Path;state.AllowedCode=allowed;
        }
        if(!state.AllowedCode)return false;
        var attrs=entity.WatchedAttributes;
        if(attrs==null||attrs.GetInt("generation")>0||attrs.HasAttribute("ownedby")||attrs.HasAttribute("ownerUid")||attrs.HasAttribute("ownerUID")||
            attrs.GetBool("tamed")||attrs.GetBool("domesticated")||attrs.GetTreeAttribute("nametag")?.GetString("name","").Length>0||
            entity.GetBehavior("seatable")!=null||entity.GetBehavior("tethered")!=null)return false;
        foreach(var behavior in entity.Properties.Server.Behaviors)
        {
            var assembly=behavior.GetType().Assembly;
            if(assembly!=typeof(Entity).Assembly&&assembly!=typeof(AiTaskManager).Assembly&&assembly!=typeof(ServerMain).Assembly)return false;
        }
        manager??=entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
        if(manager==null)return false;
        foreach(var task in manager.AllTasks)if(task.GetType().Assembly!=typeof(AiTaskManager).Assembly)return false;
        foreach(var task in manager.ActiveTasksBySlot)
            if(task!=null&&task.GetType().Name is not ("AiTaskIdle" or "AiTaskWander" or "AiTaskLookAround"))return false;
        var pos=entity.Pos;long now=Environment.TickCount64;
        if(now<Volatile.Read(ref state.DistanceUntil)&&state.Dimension==pos.Dimension&&
            Math.Abs(state.X-pos.X)<8&&Math.Abs(state.Y-pos.Y)<8&&Math.Abs(state.Z-pos.Z)<8)return state.Far;
        bool players=false;double protectedSq=(double)(radius+16)*(radius+16);
        state.X=pos.X;state.Y=pos.Y;state.Z=pos.Z;state.Dimension=pos.Dimension;state.Far=false;
        Volatile.Write(ref state.DistanceUntil,now+100);
        foreach(var client in server.Clients.Values)
        {
            if(client.State!=EnumClientState.Playing||client.Player==null)continue;
            var observer=client.Entityplayer;if(observer==null)continue;
            var player=observer.Pos;if(player.Dimension!=pos.Dimension)continue;players=true;
            if(player.SquareDistanceTo(pos)<protectedSq)return false;
        }
        state.Far=players;return players; // no known observer in this dimension => stock, not indefinite sleep
    }
    private static bool Permit(Entity entity,int kind,AiTaskManager? manager=null)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(entity);
            // Most protected entities never need scheduler state. Still clear any
            // earlier delay when a previously eligible entity becomes exempt.
            if(!BasicEligible(entity))
            {
                if(states.TryGetValue(entity,out var previous))previous.AiDue=previous.WanderDue=previous.RepulsionDue=0;
                return true;
            }
            var state=states.GetValue(entity,static _=>new());
            if(!Eligible(entity,state,manager)){state.AiDue=state.WanderDue=state.RepulsionDue=0;return true;}
            if(kind==2&&entity.Pos.Motion.LengthSq()>0.000001)return true;
            bool skip=kind switch {
                0=>Defer(ref state.AiDue,Environment.TickCount64,aiInterval,entity.EntityId),
                1=>Defer(ref state.WanderDue,Environment.TickCount64,wanderInterval,entity.EntityId),
                _=>Defer(ref state.RepulsionDue,Environment.TickCount64,repulsionInterval,entity.EntityId)};
            if(skip){if(kind==0)Interlocked.Increment(ref aiSkipped);else if(kind==1)Interlocked.Increment(ref wanderSkipped);else Interlocked.Increment(ref repulsionSkipped);}
            return !skip;
        }
        catch{Interlocked.Increment(ref failures);return true;} // before gameplay mutation; retain stock
    }
    public static bool AiPrefix(AiTaskManager __instance,Entity ___entity)=>!ai||Permit(___entity,0,__instance);
    public static bool WanderPrefix(EntityAgent ___entity,ref bool __result)
    {if(!wander||Permit(___entity,1))return true;__result=false;return false;}
    public static bool RepulsionPrefix(Entity ___entity)=>!repulsion||Permit(___entity,2);
}
