using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Server;
using Vintagestory.Server;
namespace NewfiesOptimizer.Optimizations;

// Empty and null skip lists both exclude nobody. Stock short-circuits its LINQ
// predicate when null; normalize only the method-local argument and then run the
// entire original broadcast body. No array mutation or replacement send loop.
public static class BroadcastOptimizations
{
    private static volatile bool raw,packet,udp;
    private static long rawCalls,packetCalls,udpCalls;
    public static MethodInfo RawTarget=>AccessTools.Method(typeof(ServerMain),"BroadcastArbitraryPacket",[typeof(byte[]),typeof(IServerPlayer[])]);
    public static MethodInfo PacketTarget=>AccessTools.Method(typeof(ServerMain),"BroadcastArbitraryPacket",[typeof(Packet_Server),typeof(IServerPlayer[])]);
    public static MethodInfo UdpTarget=>AccessTools.Method(typeof(ServerMain),"BroadcastArbitraryUdpPacket",[typeof(Packet_UdpPacket),typeof(IServerPlayer[])]);
    public static MethodInfo RawSend=>AccessTools.Method(typeof(ServerMain),"SendPacket",[typeof(IServerPlayer),typeof(byte[])]);
    public static MethodInfo PreparedSend=>AccessTools.Method(typeof(ServerMain),"SendPreparedPacket",[typeof(ConnectedClient),typeof(byte[]),typeof(bool)]);
    public static MethodInfo UdpSend=>AccessTools.Method(typeof(ServerMain),"SendPacket",[typeof(ConnectedClient),typeof(Packet_UdpPacket)]);
    public static MethodInfo[] Dependencies(int kind)=>[kind==0?RawTarget:kind==1?PacketTarget:UdpTarget];
    public static bool ValidateContract(out string reason)
    {
        bool valid=new[]{RawTarget,PacketTarget,UdpTarget}.All(m=>m?.ReturnType==typeof(void)&&m.GetParameters().Length==2&&m.GetParameters()[1].ParameterType==typeof(IServerPlayer[]));
        reason=valid?"Official empty/null exclusion contracts verified; complete stock send bodies retained.":"Broadcast contract differs.";return valid;
    }
    public static void ConfigureRaw(bool value)=>raw=value;
    public static void ConfigurePacket(bool value)=>packet=value;
    public static void ConfigureUdp(bool value)=>udp=value;
    public static (long Raw,long Packet,long Udp) Counters=>(Interlocked.Read(ref rawCalls),Interlocked.Read(ref packetCalls),Interlocked.Read(ref udpCalls));
    public static void RawPrefix(ref IServerPlayer[]? skipPlayers)
    {if(raw&&skipPlayers is {Length:0}){skipPlayers=null;Interlocked.Increment(ref rawCalls);}}
    public static void PacketPrefix(ref IServerPlayer[]? skipPlayers)
    {if(packet&&skipPlayers is {Length:0}){skipPlayers=null;Interlocked.Increment(ref packetCalls);}}
    public static void UdpPrefix(ref IServerPlayer[]? skipPlayers)
    {if(udp&&skipPlayers is {Length:0}){skipPlayers=null;Interlocked.Increment(ref udpCalls);}}
}
