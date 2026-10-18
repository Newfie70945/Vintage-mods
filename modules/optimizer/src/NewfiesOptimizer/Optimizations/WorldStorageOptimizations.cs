using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

public static class WorldgenRequestOptimizations
{
    private static volatile bool enabled;
    private static Type QueueType=>AccessTools.Field(typeof(ChunkServerThread),"requestedChunkColumns")?.FieldType
        ?? throw new MissingFieldException("Official requestedChunkColumns field is absent.");
    // Resolve private accessors only during guarded validation, not when switching
    // an unsupported engine off or entering safe mode.
    private static class Accessors
    {
    internal static readonly AccessTools.FieldRef<object,ConcurrentDictionary<long,ChunkColumnLoadRequest>> Index=
        AccessTools.FieldRefAccess<object,ConcurrentDictionary<long,ChunkColumnLoadRequest>>(AccessTools.Field(QueueType,"elementsByIndex"));
    internal static readonly AccessTools.FieldRef<ServerMain,ConcurrentDictionary<long,ServerMapChunk>> Maps=AccessTools.FieldRefAccess<ServerMain,ConcurrentDictionary<long,ServerMapChunk>>("loadedMapChunks");
    internal static readonly AccessTools.FieldRef<ChunkColumnLoadRequest,int> Until=AccessTools.FieldRefAccess<ChunkColumnLoadRequest,int>("untilPass");
    internal static readonly AccessTools.FieldRef<ChunkColumnLoadRequest,long> Created=AccessTools.FieldRefAccess<ChunkColumnLoadRequest,long>("creationTime");
    internal static readonly Action<object,ChunkColumnLoadRequest> Enqueue=MakeEnqueue();
    }
    public static MethodInfo Target=>AccessTools.Method(typeof(ChunkServerThread),"EnsureMinimumWorldgenPassAt");
    public static MethodInfo TranspilerMethod=>AccessTools.Method(typeof(WorldgenRequestOptimizations),nameof(Transpiler));
    public static MethodInfo[] Dependencies=>[Target,AccessTools.Method(QueueType,"EnqueueWithoutAddingToIndex"),
        AccessTools.PropertyGetter(typeof(ChunkColumnLoadRequest),"CurrentIncompletePass_AsInt"),
        AccessTools.PropertyGetter(typeof(ServerMapChunk),"CurrentIncompletePass"),AccessTools.PropertyGetter(typeof(ServerMain),"ServerConsoleId")];
    public static bool Enabled=>enabled;
    public static void Configure(bool value)=>enabled=value;
    public static bool ValidateContract(out string reason)
    {
        bool valid=Target?.ReturnType==typeof(bool)&&Dependencies.All(x=>x!=null)&&AccessTools.Field(QueueType,"elementsByIndex").FieldType==typeof(ConcurrentDictionary<long,ChunkColumnLoadRequest>);
        if(valid) _=Accessors.Enqueue;
        reason=valid?"Official chunk request/index contract verified.":"Chunk request/index contract differs.";return valid;
    }
    private static Action<object,ChunkColumnLoadRequest> MakeEnqueue()
    {
        var method=new DynamicMethod("nos_enqueue_generation_request",typeof(void),[typeof(object),typeof(ChunkColumnLoadRequest)],typeof(WorldgenRequestOptimizations),true);
        var il=method.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,QueueType);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Callvirt,AccessTools.Method(QueueType,"EnqueueWithoutAddingToIndex"));il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Action<object,ChunkColumnLoadRequest>>();
    }
    public static bool Ensure(long index2d,int chunkX,int chunkZ,int minPass,long requirorTime,ServerMain server,object queue)
    {
        Accessors.Maps(server).TryGetValue(index2d,out var map);
        if(map!=null&&map.CurrentIncompletePass==EnumWorldGenPass.Done)return true;
        var request=Accessors.Index(queue).GetOrAdd(index2d,static (key,state)=>new ChunkColumnLoadRequest(key,state.X,state.Z,state.Server.ServerConsoleId,-1,state.Server),
            (Server:server,X:chunkX,Z:chunkZ));
        if(request.CurrentIncompletePass_AsInt<minPass)
        {
            if(Accessors.Until(request)<minPass)
            {
                if(Accessors.Until(request)<0)Accessors.Enqueue(queue,request);
                Accessors.Until(request)=minPass;
                if(Accessors.Created(request)<requirorTime)Accessors.Created(request)=requirorTime;
            }
            return false;
        }
        return true;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,ILGenerator generator)
    {
        var original=instructions.ToList();
        if(original.Count(x=>x.operand is MethodInfo m&&m.Name=="GetOrAdd")!=1||original[0].blocks.Count!=0)
            throw new InvalidOperationException("Unexpected generation-request IL; retain stock.");
        var stock=generator.DefineLabel();original[0].labels.Add(stock);
        var code=new List<CodeInstruction>{new(OpCodes.Call,AccessTools.PropertyGetter(typeof(WorldgenRequestOptimizations),nameof(Enabled))),new(OpCodes.Brfalse,stock)};
        for(int arg=1;arg<=5;arg++)code.Add(new(OpCodes.Ldarg,(short)arg));
        code.AddRange([new(OpCodes.Ldarg_0),new(OpCodes.Ldfld,AccessTools.Field(typeof(ChunkServerThread),"server")),
            new(OpCodes.Ldarg_0),new(OpCodes.Ldfld,AccessTools.Field(typeof(ChunkServerThread),"requestedChunkColumns")),
            new(OpCodes.Call,AccessTools.Method(typeof(WorldgenRequestOptimizations),nameof(Ensure))),new(OpCodes.Ret)]);
        code.AddRange(original);return code;
    }
}

// Release only after the stock worker has stored savegame metadata and reset its
// pending flag, while still inside its existing savingLock. Exceptions retain the
// snapshot for stock retry behavior. No dirty flags, data or commit order change.
public static class SaveSnapshotOptimizations
{
    private static volatile bool enabled;
    private static long released,completions;
    public static Type SystemType=>typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemLoadAndSaveGame",true)!;
    public static MethodInfo Target=>AccessTools.Method(SystemType,"OnSeparateThreadTick");
    public static MethodInfo TranspilerMethod=>AccessTools.Method(typeof(SaveSnapshotOptimizations),nameof(Transpiler));
    public static MethodInfo[] Dependencies=>[Target,AccessTools.Method(SystemType,"PopulateChunksCopy"),AccessTools.Method(SystemType,"SaveGameWorld"),AccessTools.Method(SystemType,"SaveAllDirtyLoadedChunks"),AccessTools.Method(SystemType,"SaveAllDirtyGeneratingChunks"),AccessTools.Method(SystemType,"SaveAllDirtyMapChunks")];
    public static void Configure(bool value)=>enabled=value;
    public static (long ReferencesReleased,long Completions) Counters=>(Interlocked.Read(ref released),Interlocked.Read(ref completions));
    public static bool ValidateContract(out string reason)
    {
        bool valid=Dependencies.All(x=>x!=null)&&AccessTools.Field(SystemType,"chunksCopy")?.FieldType==typeof(Dictionary<long,ServerChunk>)&&AccessTools.Field(SystemType,"savingLock")?.FieldType==typeof(object);
        reason=valid?"Official save snapshot and worker lock contract verified.":"Save snapshot/worker lock contract differs.";return valid;
    }
    public static void Release(Dictionary<long,ServerChunk> snapshot)
    {
        if(!enabled)return;
        int count=snapshot.Count;snapshot.Clear();Interlocked.Add(ref released,count);Interlocked.Increment(ref completions);
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();var flag=AccessTools.Field(typeof(ChunkServerThread),"runOffThreadSaveNow");int matches=0;
        for(int i=1;i<code.Count;i++)
        {
            if(code[i].opcode!=OpCodes.Stfld||!Equals(code[i].operand,flag))continue;
            if(code[i-1].opcode!=OpCodes.Ldc_I4_0)throw new InvalidOperationException("Unexpected save completion store.");
            code.InsertRange(i+1,[new(OpCodes.Ldarg_0),new(OpCodes.Ldfld,AccessTools.Field(SystemType,"chunksCopy")),new(OpCodes.Call,AccessTools.Method(typeof(SaveSnapshotOptimizations),nameof(Release)))]);
            matches++;i+=3;
        }
        if(matches!=1)throw new InvalidOperationException("Expected one stock save completion store; retain stock.");return code;
    }
}
