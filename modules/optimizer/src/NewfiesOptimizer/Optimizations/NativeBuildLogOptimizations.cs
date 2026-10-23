using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Guard only native build-log formatting; native events, storage and log calls remain.
public static class NativeBuildLogOptimizations
{
    private static volatile bool enabled;
    private static long missingCodes, systemPlayers, failures;
    public static MethodInfo Target => AccessTools.Method(typeof(ServerMain).Assembly.GetType("VintagestoryLib.Server.Systems.ServerSystemBlockLogger",true)!,"DidBreakBock");
    private static MethodInfo GetBlock => AccessTools.Method(typeof(IWorldAccessor),nameof(IWorldAccessor.GetBlock),[typeof(int)]);
    private static MethodInfo GetName => AccessTools.PropertyGetter(typeof(IPlayer),nameof(IPlayer.PlayerName));
    public static MethodBase[] Dependencies => [Target,GetBlock,GetName];
    public static void Configure(bool value)=>enabled=value;
    public static (long MissingCodes,long SystemPlayers,long Failures) Counters => (Interlocked.Read(ref missingCodes),Interlocked.Read(ref systemPlayers),Interlocked.Read(ref failures));
    public static string BlockCode(IWorldAccessor world,int id)
    {
        var block=world.GetBlock(id);
        if(!enabled)return block.Code.ToString();
        if(block?.Code is { } code)return code.ToString();
        Interlocked.Increment(ref missingCodes);
        return "Unknown block ID "+id.ToString(CultureInfo.InvariantCulture);
    }
    public static string PlayerName(IPlayer? player)
    {
        if(!enabled)return player!.PlayerName;
        if(player!=null)return player.PlayerName;
        Interlocked.Increment(ref systemPlayers);return "<system>";
    }
    public static void Prefix(out bool __state)=>__state=enabled;
    public static Exception? Finalizer(Exception? __exception,bool __state)
    {if(__exception!=null&&__state){Interlocked.Increment(ref failures);enabled=false;}return __exception;}
    public static bool ValidateContract(out string reason)
    {
        try{Rewrite(PatchProcessor.GetOriginalInstructions(Target));reason="Official native block-break log formatting verified; log/event calls retained.";return true;}
        catch{reason="Native build-log layout differs; retain stock.";return false;}
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)=>Rewrite(source);
    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> source)
    {
        var code=source.Select(c=>new CodeInstruction(c)).ToList();int blocks=0,names=0;
        var codeField=AccessTools.Field(typeof(RegistryObject),nameof(RegistryObject.Code));
        var toString=AccessTools.Method(typeof(object),nameof(object.ToString),Type.EmptyTypes);
        for(int i=0;i<code.Count;i++)
        {
            if(code[i].Calls(GetBlock))
            {
                if(i+2>=code.Count||!code[i+1].LoadsField(codeField)||!code[i+2].Calls(toString)||code.Skip(i+1).Take(2).Any(c=>c.labels.Count!=0||c.blocks.Count!=0))throw new InvalidOperationException("Unexpected native block-code log expression.");
                code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(NativeBuildLogOptimizations),nameof(BlockCode));code.RemoveRange(i+1,2);blocks++;
            }
            else if(code[i].Calls(GetName)){code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(NativeBuildLogOptimizations),nameof(PlayerName));names++;}
        }
        if(blocks!=1||names!=1)throw new InvalidOperationException("Expected one native block-code and player-name expression.");
        return code;
    }
}
