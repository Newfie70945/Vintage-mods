using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace NewfiesOptimizer.Optimizations;

// Native synchronous walkers only. Callback state is borrowed for one call and
// clears every owner/accessor reference on completion; custom walkers stay native.
public static class CollisionGenerationOptimizations
{
    private static readonly bool[] enabled = new bool[2];
    private static readonly int[] epochs = new int[2];
    private static readonly long[] calls = new long[2], hits = new long[2], created = new long[2], fallbacks = new long[2], failures = new long[2];
    private sealed class Contract(bool native) { public readonly bool Native = native; }
    private static readonly ConditionalWeakTable<Type, Contract> contracts = new();
    private static readonly ConditionalWeakTable<Type, Contract>.CreateValueCallback Inspect = type =>
    {
        var map = type.GetInterfaceMap(typeof(IBlockAccessor));
        int index = Array.FindIndex(map.InterfaceMethods, m => m == InterfaceWalk);
        return new(index >= 0 && map.TargetMethods[index].Module == Walk.Module && map.TargetMethods[index].MetadataToken == Walk.MetadataToken);
    };
    [ThreadStatic] private static Context?[]? idle;
    private sealed class Context
    {
        public CollisionTester? Owner; public IBlockAccessor? Accessor;
        public int Kind, Epoch;
        public readonly Action<Block,int,int,int> Visit;
        public Context() => Visit = Add;
        private void Add(Block block,int x,int y,int z)
        {
            var owner = Owner!;
            var boxes = block.GetCollisionBoxes(Accessor!, owner.tmpPos.Set(x,y,z));
            if (boxes != null) owner.CollisionBoxList.Add(boxes,x,y,z,block);
        }
    }
    public static MethodInfo Walk => AccessTools.Method(typeof(BlockAccessorBase), nameof(IBlockAccessor.WalkBlocks));
    private static MethodInfo InterfaceWalk => AccessTools.Method(typeof(IBlockAccessor), nameof(IBlockAccessor.WalkBlocks));
    public static MethodInfo Target(int kind) => AccessTools.DeclaredMethod(kind == 0 ? typeof(CollisionTester) : typeof(CachingCollisionTester), "GenerateCollisionBoxList");
    public static MethodBase[] Dependencies(int kind)
    {
        var ctor = PatchProcessor.GetOriginalInstructions(Target(kind)).Where(c=>c.opcode==OpCodes.Newobj && c.operand is ConstructorInfo ci && ci.DeclaringType?.DeclaringType==Target(kind).DeclaringType).Select(c=>(ConstructorInfo)c.operand).Single();
        return new MethodBase[]{Target(kind),Walk,ctor}.Concat(ctor.DeclaringType!.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)).ToArray();
    }
    public static bool ValidateContract(int kind,out string reason)
    {
        var target=Target(kind);var code=PatchProcessor.GetOriginalInstructions(target);
        bool valid=target.ReturnType==typeof(void)&&target.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(IBlockAccessor),typeof(double),typeof(double),typeof(double),typeof(float),typeof(float),typeof(int)})&&code.Count(c=>c.Calls(InterfaceWalk))==1&&Dependencies(kind).Length>=4;
        reason=valid?"Official collision bounds/callback and synchronous walker verified.":"Collision generation contract differs; retain stock.";return valid;
    }
    public static void Configure(int kind,bool value){Volatile.Write(ref enabled[kind],false);Interlocked.Increment(ref epochs[kind]);if(idle!=null)idle[kind]=null;Volatile.Write(ref enabled[kind],value);}
    public static (long Calls,long Hits,long Created,long Fallbacks,long Failures) Counters(int kind)=>(Interlocked.Read(ref calls[kind]),Interlocked.Read(ref hits[kind]),Interlocked.Read(ref created[kind]),Interlocked.Read(ref fallbacks[kind]),Interlocked.Read(ref failures[kind]));
    public static bool CurrentThreadRetainsInputs=>idle?.Any(c=>c?.Owner!=null||c?.Accessor!=null)??false;
    public static bool NormalPrefix(CollisionTester __instance,IBlockAccessor blockAccessor,double motionX,double motionY,double motionZ,float stepHeight,float yExtra,int dimension)=>Run(0,__instance,blockAccessor,motionX,motionY,motionZ,stepHeight,yExtra);
    public static bool CachedPrefix(CollisionTester __instance,IBlockAccessor blockAccessor,double motionX,double motionY,double motionZ,float stepHeight,float yExtra,int dimension)=>Run(1,__instance,blockAccessor,motionX,motionY,motionZ,stepHeight,yExtra);
    private static bool Run(int kind,CollisionTester owner,IBlockAccessor accessor,double x,double y,double z,float step,float extra)
    {
        if(!Volatile.Read(ref enabled[kind]))return true;
        bool native;
        try{native=accessor!=null&&contracts.GetValue(accessor.GetType(),Inspect).Native;}
        catch{Interlocked.Increment(ref fallbacks[kind]);return true;}
        if(!native){Interlocked.Increment(ref fallbacks[kind]);return true;}
        Interlocked.Increment(ref calls[kind]);Context? context=null;
        try
        {
            var box=owner.entityBox;
            bool min=owner.minPos.SetAndEquals((int)(box.X1+Math.Min(0,x)),(int)(box.Y1+Math.Min(0,y)-extra),(int)(box.Z1+Math.Min(0,z)));
            double top=Math.Max(box.Y1+step,box.Y2);
            bool max=owner.maxPos.SetAndEquals((int)(box.X2+Math.Max(0,x)),(int)(top+Math.Max(0,y)),(int)(box.Z2+Math.Max(0,z)));
            if(min & max){Interlocked.Increment(ref hits[kind]);return false;}
            owner.CollisionBoxList.Clear();
            idle??=new Context?[2];context=idle[kind];idle[kind]=null;int epoch=Volatile.Read(ref epochs[kind]);
            if(context==null||context.Epoch!=epoch){context=new(){Kind=kind,Epoch=epoch};Interlocked.Increment(ref created[kind]);}
            context.Owner=owner;context.Accessor=accessor;
            accessor!.WalkBlocks(owner.minPos,owner.maxPos,context.Visit,true);
            return false;
        }
        catch{Interlocked.Increment(ref failures[kind]);Volatile.Write(ref enabled[kind],false);throw;}
        finally
        {
            if(context!=null){context.Owner=null;context.Accessor=null;if(Volatile.Read(ref enabled[kind])&&context.Epoch==Volatile.Read(ref epochs[kind])&&idle![kind]==null)idle[kind]=context;}
        }
    }
}
