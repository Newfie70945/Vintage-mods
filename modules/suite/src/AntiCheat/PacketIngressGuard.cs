using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NewfiesAntiCheat.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;
using Vintagestory.Server.Systems;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Guards the verified 1.22.7 vanilla ingress BEFORE handlers mutate player settings,
/// schedule chunk work, index block faces, or invoke mod block entities. Client scan reports
/// are never used as authority. On other engine versions the missing coverage is logged.
/// </summary>
internal sealed class PacketIngressGuard : IDisposable
{
    private const string HarmonyId = "newfies.serveressentials.packetguard.ingress";
    private static PacketIngressGuard? current;
    private readonly ICoreServerAPI api;
    private readonly AntiCheatConfig config;
    private readonly Func<PacketGuardTpsSnapshot> getTickHealth;
    private readonly Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> reportFinding;
    private readonly ConditionalWeakTable<ConnectedClient, ConnectionState> connections = new();
    private Harmony? harmony;
    private ServerMain? server;
    private bool active;
    private DeniedActionResynchronizer? deniedResync;
    private readonly PacketIngressBudget resyncBudget=new();
    internal bool IsActive => active;
    internal string Status { get; private set; } = "Disabled";
    private static AccessTools.FieldRef<ReceivedClientPacket, ConnectedClient>? receivedClient;
    private static AccessTools.FieldRef<ReceivedClientPacket, Packet_Client>? receivedPacket;
    private static AccessTools.FieldRef<CitoMemoryStream, int>? inputLength;
    [ThreadStatic] private static int parsingLimit;
    [ThreadStatic] private static ConnectedClient? parsingClient;
    internal int InstalledHookCount { get; private set; }

    private readonly record struct ParseContext(int Limit, ConnectedClient? Client);

    private sealed class ConnectionState
    {
        internal IServerPlayer? MiningPlayer;
        internal long MiningEntity;
        internal readonly MiningRequestObservation MiningRequest=new();
        internal readonly PacketIngressBudget CombatActions=new(),InventoryActions=new();
        internal long LastResync,LastResyncError;
        internal readonly BlockNukerWindow Nuker = new();
        internal bool NukerKickPending, NukerKicked;
        internal IServerPlayer? NukerPlayer;
        internal long NukerEntity;
        internal int NukerDimension,NukerLimit,NukerEpoch;
        internal readonly PacketIngressBudget Wire = new();
        internal readonly PacketIngressBudget Blocks = new();
        internal readonly PacketIngressBudget Settings = new();
        internal readonly PacketIngressBudget Positions = new();
        internal readonly PacketFloodConfirmation WireFlood=new(),BlockFlood=new(),SettingsFlood=new(),PositionFlood=new();
        internal long HealthCheckedUtc;
        internal bool TimingReliable;
        internal readonly Dictionary<string, long> ReportTimes = new(StringComparer.Ordinal);
    }

    internal PacketIngressGuard(ICoreServerAPI api, AntiCheatConfig config,
        Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> reportFinding,Func<PacketGuardTpsSnapshot> getTickHealth)
    {
        this.api = api;
        this.config = config;
        this.reportFinding = reportFinding;
        this.getTickHealth=getTickHealth;
    }

    internal void Start()
    {
        if (!config.PacketGuardIngressEnabled || active) return;
        try
        {
            string version = typeof(GameVersion).GetField(nameof(GameVersion.ShortGameVersion), BindingFlags.Public | BindingFlags.Static)
                ?.GetRawConstantValue() as string ?? "unknown";
            if (version != "1.22.7" || api.World is not ServerMain sm)
                throw new NotSupportedException($"Ingress hooks require the verified standard 1.22.7 server runtime; found {version}.");
            server = sm;
            if (current != null && !ReferenceEquals(current, this)) throw new InvalidOperationException("An ingress guard is already installed.");
            receivedClient = AccessTools.FieldRefAccess<ReceivedClientPacket, ConnectedClient>("client");
            receivedPacket = AccessTools.FieldRefAccess<ReceivedClientPacket, Packet_Client>("packet");
            inputLength = AccessTools.FieldRefAccess<CitoMemoryStream, int>("bufferlength");
            harmony = new Harmony(HarmonyId);
            MethodInfo parse = AccessTools.DeclaredMethod(typeof(ServerMain), "ParseClientPacket_offthread", new[] { typeof(ConnectedClient), typeof(byte[]), typeof(int) })
                ?? throw new MissingMethodException(typeof(ServerMain).FullName, "ParseClientPacket_offthread");
            harmony.Patch(parse, prefix: new HarmonyMethod(typeof(PacketIngressGuard), nameof(BeforeParse)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PacketIngressGuard), nameof(FinishParse)) { priority = Priority.Last });
            InstalledHookCount++;
            // Native UDP parsing happens before endpoint authentication. Scope the same safe
            // byte-array reader to the UDP serializer, including unauthenticated datagrams.
            MethodInfo udpParse = AccessTools.DeclaredMethod(typeof(Packet_UdpPacketSerializer), "DeserializeBuffer", new[] { typeof(byte[]), typeof(int), typeof(Packet_UdpPacket) })
                ?? throw new MissingMethodException(typeof(Packet_UdpPacketSerializer).FullName, "DeserializeBuffer");
            harmony.Patch(udpParse, prefix: new HarmonyMethod(typeof(PacketIngressGuard), nameof(BeforeUdpParse)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(PacketIngressGuard), nameof(FinishUdpParse)) { priority = Priority.Last });
            InstalledHookCount++;
            Patch(typeof(ProtocolParser), "ReadBytes", new[] { typeof(CitoStream) }, nameof(BeforeReadBytes));
            Patch(typeof(ServerMain), "HandleClientPacket_mainthread", new[] { typeof(ReceivedClientPacket) }, nameof(BeforeDispatch));
            // UDP and TCP-fallback movement converge here. A TCP-only hook misses normal player movement.
            Patch(typeof(ServerUdpNetwork), "HandlePlayerPosition", new[] { typeof(Packet_EntityPosition), typeof(ServerPlayer) }, nameof(BeforePlayerPosition));
            current = this;
            active = true;
            Status = "Active on Vintage Story 1.22.7";
            api.Logger.Notification("[ServerEssentials/AntiCheat] PacketGuard vanilla ingress active (1.22.7): pre-deserialize cap={0} KiB, packet budget={1}/s burst={2}; identification/settings view cap={3} blocks plus server radius; block/decor/BE/hand guards and UDP/TCP position validation active; length-prefixed byte arrays bounded BEFORE allocation; hooks={4}.",
                config.PacketGuardIngressMaxPacketKiB, config.PacketGuardIngressPacketsPerSecond,
                config.PacketGuardIngressPacketBurst, config.PacketGuardMaxViewDistanceBlocks, InstalledHookCount);
        }
        catch (Exception ex)
        {
            Dispose();
            Status = "Unavailable: " + ex.Message;
            api.Logger.Error("[ServerEssentials/AntiCheat] PacketGuard ingress protection UNAVAILABLE; vanilla packets are not guarded by this layer. Movement/reach sampling continues. {0}", ex.Message);
        }
    }

    private void Patch(Type type, string method, Type[] parameters, string prefix)
    {
        MethodInfo target = AccessTools.DeclaredMethod(type, method, parameters) ?? throw new MissingMethodException(type.FullName, method);
        harmony!.Patch(target, prefix: new HarmonyMethod(typeof(PacketIngressGuard), prefix) { priority = Priority.First });
        InstalledHookCount++;
    }

    private static bool BeforeParse(ConnectedClient client, byte[] data, int length, out ParseContext __state)
    {
        __state = new ParseContext(parsingLimit, parsingClient);
        if (current == null) return true;
        if (!current.ValidateWire(client, data, length)) return false;
        parsingLimit = current.config.PacketGuardIngressMaxPacketKiB * 1024;
        parsingClient = client;
        return true;
    }

    private static void FinishParse(ParseContext __state)
    {
        parsingLimit = __state.Limit;
        parsingClient = __state.Client;
    }

    private static void BeforeUdpParse(byte[] __0, int __1, out ParseContext __state)
    {
        __state = new ParseContext(parsingLimit, parsingClient);
        if (current == null) return;
        if (__0 == null || __1 <= 0 || __1 > __0.Length || __1 > current.config.PacketGuardIngressMaxPacketKiB * 1024L)
            throw new InvalidDataException("Invalid client UDP frame length.");
        parsingLimit = current.config.PacketGuardIngressMaxPacketKiB * 1024;
        parsingClient = null; // The engine has not authenticated this endpoint yet.
    }

    private static Exception? FinishUdpParse(Packet_UdpPacket __2, ParseContext __state, Exception? __exception)
    {
        FinishParse(__state);
        if (__exception != null || current == null) return __exception;
        return ValidUdpShape(__2) ? null : new InvalidDataException("Invalid client UDP packet shape.");
    }

    private static bool ValidCustomMessageId(int id) => (uint)id < 256; // Native channels have 256 handlers.

    private static bool ValidUdpShape(Packet_UdpPacket? packet)
    {
        if (packet == null) return false;
        return packet.Id switch
        {
            1 => packet.ConnectionPacket != null && !string.IsNullOrWhiteSpace(packet.ConnectionPacket.LoginToken),
            2 => packet.EntityPosition != null,
            3 => packet.EntityPosition != null && (packet.ChannelPacket == null || (packet.ChannelPacket.Data != null && ValidCustomMessageId(packet.ChannelPacket.MessageId))),
            6 => packet.ChannelPacket != null && packet.ChannelPacket.Data != null && ValidCustomMessageId(packet.ChannelPacket.MessageId),
            _ => true // Other native UDP IDs, including ping, have different/optional bodies.
        };
    }

    private static bool BeforeReadBytes(CitoStream stream, ref byte[] __result)
    {
        if (current == null || parsingLimit == 0) return true;
        // The vanilla helper allocates the declared length before checking the stream. A tiny
        // frame can therefore request gigabytes. Match the existing format while validating
        // the allocation against BOTH the configured cap and the real remaining frame bytes.
        int declared = ProtocolParser.ReadUInt32(stream);
        int remaining = stream is CitoMemoryStream memory ? inputLength!(memory) - memory.Position() : -1;
        if (declared < 0 || declared > parsingLimit || declared > remaining)
        {
            if (parsingClient != null)
                current.Report(parsingClient, "PACKET_MALFORMED_PACKET", "Rejected an impossible length-prefixed byte array before allocating it.",
                    $"stage=deserialize;declaredBytes={declared};remainingBytes={remaining}", AntiCheatSeverity.High);
            // ParseClientPacket_offthread catches this and performs its normal invalid-packet
            // disconnect. The corrupt packet never reaches the action handler queue.
            throw new InvalidDataException("Invalid client byte-array length.");
        }
        __result = new byte[declared];
        int offset = 0;
        while (offset < declared)
        {
            int read = stream.Read(__result, offset, declared - offset);
            if (read <= 0) throw new InvalidDataException("Truncated client byte array.");
            offset += read;
        }
        return false;
    }

    private static bool BeforeDispatch(ReceivedClientPacket cpk)
    {
        if (current == null || cpk == null) return true;
        Packet_Client? packet = receivedPacket!(cpk);
        // Connection/disconnection lifecycle envelopes deliberately have no packet.
        return packet == null || current.ValidatePacket(receivedClient!(cpk), packet);
    }

    private static bool BeforePlayerPosition(Packet_EntityPosition packet, ServerPlayer player) =>
        current == null || current.ValidatePlayerPosition(packet, player);

    private bool ValidateWire(ConnectedClient client, byte[]? data, int length)
    {
        if (!active || client == null) return true;
        if (data == null || length <= 0 || length > data.Length || length > (long)config.PacketGuardIngressMaxPacketKiB * 1024)
            return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected an invalid or oversized vanilla TCP packet before deserialization.", $"stage=pre-deserialize;length={length};capKiB={config.PacketGuardIngressMaxPacketKiB}");
        ConnectionState state = connections.GetValue(client, _ => new ConnectionState());
        return ConsumeBudget(client,state,state.Wire,state.WireFlood,config.PacketGuardIngressPacketsPerSecond,config.PacketGuardIngressPacketBurst,"stage=pre-deserialize;budget=wire");
    }

    private bool ValidatePacket(ConnectedClient client, Packet_Client packet)
    {
        if (!active || client == null) return true;
        if (packet.Id < 0 || packet.Id >= 255)
            return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected a packet ID outside the server handler table.", $"packetId={packet.Id}");
        switch (packet.Id)
        {
            case Packet_ClientIdEnum.PlayerIdentification:
                if (packet.Identification == null) return Malformed(client, packet.Id);
                packet.Identification.ViewDistance = NormalizeViewDistance(client, packet.Identification.ViewDistance);
                return SettingsBudget(client);
            case Packet_ClientIdEnum.RequestModeChange:
                if (packet.RequestModeChange == null || client.Player == null) return Malformed(client, packet.Id);
                if (string.IsNullOrWhiteSpace(packet.RequestModeChange.PlayerUID) ||
                    (packet.RequestModeChange.PlayerUID != client.Player.PlayerUID && !client.Player.HasPrivilege(Privilege.commandplayer)))
                    return Reject(client, "PACKET_UNAUTHORIZED_SETTINGS", "Rejected settings targeting another player before the vanilla handler can modify their view distance.", $"packetId={packet.Id};target=other-player");
                if (!SettingsBudget(client)) return false;
                if (!Enum.IsDefined(typeof(EnumGameMode), packet.RequestModeChange.GameMode)) return Malformed(client, packet.Id);
                packet.RequestModeChange.ViewDistance = NormalizeViewDistance(client, packet.RequestModeChange.ViewDistance);
                // Encoded floats must not introduce NaN/Infinity into server-owned movement/reach state.
                if (!float.IsFinite(CollectibleNet.DeserializeFloat(packet.RequestModeChange.MoveSpeed)) ||
                    !float.IsFinite(CollectibleNet.DeserializeFloat(packet.RequestModeChange.PickingRange)))
                    return Malformed(client, packet.Id);
                return true;
            case Packet_ClientIdEnum.BlockPlaceOrBreak:
                if (packet.BlockPlaceOrBreak == null || client.Player == null) return Malformed(client, packet.Id);
                if (!BlockBudget(client)) return false;
                Packet_ClientBlockPlaceOrBreak b = packet.BlockPlaceOrBreak;
                if (b.Mode < 0 || b.Mode > 2 || !PacketIngressPolicy.IsValidFace(b.OnBlockFace) ||
                    !PacketIngressPolicy.IsValidBlockId(b.BlockType, api.World.Blocks.Count) ||
                    !ValidSelection(b.HitX, b.HitY, b.HitZ, b.SelectionBoxIndex, PacketSelectionIdReader<Packet_ClientBlockPlaceOrBreak>.Read(b))) return Malformed(client, packet.Id);
                if (!ValidateBlockTarget(client, b.X, b.Y, b.Z)) return false;
                if (b.Mode == 0 && !ValidateBreakBurst(client,b.X,b.Y,b.Z)) return false;
                if (b.Mode == 2)
                {
                    // Vanilla decor removal bypasses BreakBlock/DidBreakBlock, so retain an
                    // attributed audit record without fabricating a full-block rollback entry.
                    api.Logger.Audit("[PacketGuard/Decor] playerUid={0};player={1};request=break-decor;target={2},{3},{4};face={5};result=pending-vanilla-claim-check",
                        client.Player.PlayerUID, Clean(client.Player.PlayerName), b.X, b.Y, b.Z, b.OnBlockFace);
                }
                return true;
            case Packet_ClientIdEnum.BlockEntityPacket:
                if (packet.BlockEntityPacket == null || client.Player == null) return Malformed(client, packet.Id);
                if (!BlockBudget(client)) return false;
                return ValidateBlockTarget(client, packet.BlockEntityPacket.X, packet.BlockEntityPacket.Y, packet.BlockEntityPacket.Z);
            case Packet_ClientIdEnum.HandInteraction:
                if (packet.HandInteraction == null || client.Player == null) return Malformed(client, packet.Id);
                if (!BlockBudget(client)) return false;
                Packet_ClientHandInteraction hand = packet.HandInteraction;
                if ((hand.InventoryId?.Length ?? 0) > 512 || hand.SlotId < 0) return Malformed(client, packet.Id);
                if (hand.EnumHandInteract < 4)
                {
                    IInventory? inventory;
                    int slot = hand.SlotId;
                    if (hand.InventoryId == null)
                    {
                        if (slot >= 10)
                        {
                            client.Player.InventoryManager.GetInventory("backpack-" + client.Player.PlayerUID, out var backpack);
                            inventory = backpack;
                            slot -= 10;
                        }
                        else inventory = client.Player.InventoryManager.GetHotbarInventory();
                    }
                    else
                    {
                        client.Player.InventoryManager.Inventories.TryGetValue(hand.InventoryId, out var requestedInventory);
                        inventory = requestedInventory;
                    }
                    if (inventory != null && slot >= inventory.Count) return Malformed(client, packet.Id);
                }
                // The block-use handler has an unbounded catch-up loop. Bound the delta, not
                // the absolute use counter, so long legitimate held actions remain compatible.
                if (hand.UsingCount < 0 || (long)hand.UsingCount - client.Player.Entity.Controls.UsingCount > 512)
                    return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected an excessive hand-use catch-up count before it can trigger an unbounded server loop.", $"packetId={packet.Id};useCount={hand.UsingCount};serverCount={client.Player.Entity.Controls.UsingCount}");
                if (hand.UseType < 0 || hand.UseType > (int)EnumHandInteract.BlockInteract || hand.EnumHandInteract < 0 || hand.EnumHandInteract > 7) return Malformed(client, packet.Id);
                // Vanilla routes block-use by EnumHandInteract (4..7), independently of
                // UseType; held-item use can also index its block face when UseType != None.
                // Validate the same routing conditions before either handler is entered.
                if (hand.EnumHandInteract >= 4 || (hand.UseType != 0 && hand.MouseButton == 2))
                {
                    if (!PacketIngressPolicy.IsValidFace(hand.OnBlockFace) || hand.SelectionBoxIndex < 0 || hand.SelectionBoxIndex > 8192 || (PacketSelectionIdReader<Packet_ClientHandInteraction>.Read(hand)?.Length ?? 0) > 256 ||
                        !PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDoublePrecise(hand.HitX)) ||
                        !PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDoublePrecise(hand.HitY)) ||
                        !PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDoublePrecise(hand.HitZ))) return Malformed(client, packet.Id);
                    if (hand.EnumHandInteract >= 4 || hand.UseType == (int)EnumHandInteract.BlockInteract)
                        return ValidateBlockTarget(client, hand.X, hand.Y, hand.Z);
                }
                return true;
            case Packet_ClientIdEnum.BlockDamage:
                if (packet.BlockDamage == null || client.Player == null) return Malformed(client, packet.Id);
                if (!BlockBudget(client)) return false;
                if (!PacketIngressPolicy.IsValidFace(packet.BlockDamage.Facing)) return Malformed(client, packet.Id);
                if(!ValidateBlockTarget(client, packet.BlockDamage.PosX, packet.BlockDamage.PosY, packet.BlockDamage.PosZ))return false;
                ObserveMiningRequest(client,packet.BlockDamage);
                return true;
            // Official protocol IDs verified against ServerSystemInventory. Preserve native
            // tabbed/ground slot semantics and all normal batch quantities.
            case 7: // ActivateInventorySlot
                if (packet.ActivateInventorySlot == null || client.Player == null ||
                    !PacketIngressPolicy.IsValidInventoryId(packet.ActivateInventorySlot.TargetInventoryId)) return Malformed(client, packet.Id);
                return ValidateInventoryAction(client,packet.ActivateInventorySlot.TargetInventoryId,packet.ActivateInventorySlot.TargetSlot);
            case 8: // MoveItemstack
                if (packet.MoveItemstack == null || client.Player == null ||
                    !PacketIngressPolicy.IsValidInventoryId(packet.MoveItemstack.SourceInventoryId) ||
                    !PacketIngressPolicy.IsValidInventoryId(packet.MoveItemstack.TargetInventoryId) ||
                    packet.MoveItemstack.Quantity < 0) return Malformed(client, packet.Id);
                return ValidateInventoryAction(client,packet.MoveItemstack.SourceInventoryId,packet.MoveItemstack.SourceSlot,packet.MoveItemstack.TargetInventoryId,packet.MoveItemstack.TargetSlot);
            case 9: // FlipItemstacks
                if (packet.Flipitemstacks == null || client.Player == null ||
                    !PacketIngressPolicy.IsValidInventoryId(packet.Flipitemstacks.SourceInventoryId) ||
                    !PacketIngressPolicy.IsValidInventoryId(packet.Flipitemstacks.TargetInventoryId)) return Malformed(client, packet.Id);
                return ValidateInventoryAction(client,packet.Flipitemstacks.SourceInventoryId,packet.Flipitemstacks.SourceSlot,packet.Flipitemstacks.TargetInventoryId,packet.Flipitemstacks.TargetSlot);
            case 30: // InvOpenedClosed
                if (packet.InvOpenedClosed == null || client.Player == null ||
                    !PacketIngressPolicy.IsValidInventoryId(packet.InvOpenedClosed.InventoryId)) return Malformed(client, packet.Id);
                return true;
            case Packet_ClientIdEnum.CreateItemstack:
                if (packet.CreateItemstack == null || client.Player == null) return Malformed(client, packet.Id);
                if (!PacketIngressPolicy.IsValidInventoryId(packet.CreateItemstack.TargetInventoryId) ||
                    packet.CreateItemstack.TargetSlot < 0 || packet.CreateItemstack.Itemstack == null) return Malformed(client, packet.Id);
                if (client.Player.WorldData.CurrentGameMode != EnumGameMode.Creative)
                    return Reject(client, "PACKET_UNAUTHORIZED_ITEMSTACK", "Rejected a creative inventory creation packet from a non-creative player.", $"packetId={packet.Id}");
                // Creative mode is not permission to index an absent/stale inventory.
                // Resolve only server-known inventories; do not deserialize the supplied stack here.
                if (client.Player.InventoryManager == null ||
                    !client.Player.InventoryManager.GetInventory(packet.CreateItemstack.TargetInventoryId, out var creationInventory) ||
                    creationInventory == null) return false;
                if (packet.CreateItemstack.TargetSlot >= creationInventory.Count) return false;
                return true;
            case Packet_ClientIdEnum.CustomPacket:
                return packet.CustomPacket != null && packet.CustomPacket.Data != null && ValidCustomMessageId(packet.CustomPacket.MessageId) || Malformed(client, packet.Id);
            case Packet_ClientIdEnum.UdpPacket:
                return ValidUdpShape(packet.UdpPacket) || Malformed(client, packet.Id);
            case Packet_ClientIdEnum.EntityPacket:
                return packet.EntityPacket != null || Malformed(client, packet.Id);
            case Packet_ClientIdEnum.EntityInteraction:
                if (packet.EntityInteraction == null || client.Player == null) return Malformed(client, packet.Id);
                var interaction = packet.EntityInteraction;
                if (!PacketIngressPolicy.IsValidEntityInteraction(interaction.SelectionBoxIndex,
                    CollectibleNet.DeserializeDouble(interaction.HitX),
                    CollectibleNet.DeserializeDouble(interaction.HitY),
                    CollectibleNet.DeserializeDouble(interaction.HitZ))) return Malformed(client, packet.Id);
                return interaction.MouseButton!=0 || ConsumeActionBudget(client,false);
            case Packet_ClientIdEnum.RuntimeSetting:
                return packet.RuntimeSetting != null || Malformed(client, packet.Id);
            case Packet_ClientIdEnum.SetToolMode:
                if (packet.ToolMode == null || client.Player == null) return Malformed(client, packet.Id);
                if (!PacketIngressPolicy.IsValidFace(packet.ToolMode.Face) ||
                    !ValidSelection(packet.ToolMode.HitX, packet.ToolMode.HitY, packet.ToolMode.HitZ, packet.ToolMode.SelectionBoxIndex, null)) return Malformed(client, packet.Id);
                return true;
            case Packet_ClientIdEnum.SelectedHotbarSlot:
                if (packet.SelectedHotbarSlot == null) return Malformed(client, packet.Id);
                // A packet can arrive during inventory initialization. Drop it without risk;
                // the client can resend once the server-owned inventories exist.
                if (client.Player?.InventoryManager == null) return false;
                var hotbar = client.Player.InventoryManager.GetHotbarInventory();
                if (hotbar == null || hotbar.Count <= 10) return false;
                int selected = packet.SelectedHotbarSlot.SlotNumber;
                var offhand = hotbar[10];
                if (offhand == null) return false;
                int backpackStart = 10 + (offhand.Empty ? 0 : 1);
                var selectedBackpack = client.Player.InventoryManager.GetOwnInventory("backpack");
                bool valid = selected >= 0 && (selected < backpackStart
                    ? selected < hotbar.Count
                    : selectedBackpack != null && selected - backpackStart < selectedBackpack.Count);
                return valid || Reject(client, "PACKET_MALFORMED_PACKET",
                    "Rejected an invalid active hotbar/backpack selection before inventory access.",
                    $"packetId={packet.Id};reason=active-slot-bounds;slot={selected};hotbarCount={hotbar.Count};backpackStart={backpackStart};backpackCount={selectedBackpack?.Count ?? 0}");
        }
        return true;
    }

    private void ObserveMiningRequest(ConnectedClient client,Packet_BlockDamage damage)
    {
        try
        {
            if(config.PacketGuardMiningEnabled && client.Player.WorldData.CurrentGameMode==EnumGameMode.Survival)
                {
                    var startPos=new BlockPos(damage.PosX,damage.PosY,damage.PosZ);
                    var startBlock=api.World.BlockAccessor.GetBlock(startPos);
                    var startState=connections.GetValue(client,_=>new ConnectionState());
                    string tool=client.Player.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code?.ToString()??"hand";
                    lock(startState)
                    {
                        if(!ReferenceEquals(startState.MiningPlayer,client.Player)||startState.MiningEntity!=client.Player.Entity.EntityId)startState.MiningRequest.Reset();
                        startState.MiningPlayer=client.Player;startState.MiningEntity=client.Player.Entity.EntityId;
                        if(startBlock!=null)startState.MiningRequest.Start(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),startPos.X,startPos.InternalY,startPos.Z,startBlock.Id,tool);
                    }
                }
        }
        catch(Exception ex){Report(client,"SERVER_ACTION_OBSERVER_UNAVAILABLE","Mining request observation skipped incompatible runtime state.","component=mining-start;exceptionType="+ex.GetType().Name,AntiCheatSeverity.Info);}
    }

    internal long? MiningRequestElapsed(IServerPlayer player,BlockPos pos,int blockId,string tool,long now)
    {
        var client=server?.GetClient(player.ClientId);
        if(client==null||!ReferenceEquals(client.Player,player)||!connections.TryGetValue(client,out var state))return null;
        lock(state)return ReferenceEquals(state.MiningPlayer,player)&&state.MiningEntity==player.Entity.EntityId?state.MiningRequest.Elapsed(now,pos.X,pos.InternalY,pos.Z,blockId,tool):null;
    }

    private bool ExemptNewActionContext(IServerPlayer player)=>player.WorldData.CurrentGameMode!=EnumGameMode.Survival ||
        (config.PacketGuardExemptAdmins&&(player.HasPrivilege(Privilege.root)||player.HasPrivilege(Privilege.controlserver)));

    private bool ConsumeActionBudget(ConnectedClient client,bool inventory)
    {
        var player=client.Player;
        if(player==null||!config.Enabled || ExemptNewActionContext(player) || (inventory?!config.PacketGuardInventoryChecksEnabled:!config.PacketGuardCombatChecksEnabled))return true;
        var scope=inventory?ServerProtectionScope.Inventory:ServerProtectionScope.Combat;
        if(ServerProtectionExemptions.Has(player.PlayerUID,scope,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))return true;
        var state=connections.GetValue(client,_=>new ConnectionState());bool allowed;
        int rate=inventory?config.PacketGuardInventoryMaxRequestsPerSecond:config.PacketGuardCombatMaxRequestsPerSecond;
        lock(state)allowed=(inventory?state.InventoryActions:state.CombatActions).TryConsume(Environment.TickCount64,rate,rate*3);
        if(allowed)return true;
        var health=getTickHealth();bool enforce=!config.PacketGuardNewActionShadowMode&&(!health.Enabled||(health.Ready&&!health.IsDegraded&&!health.IsCritical));
        Report(client,inventory?"SERVER_ACTION_INVENTORY_BUDGET":"SERVER_ACTION_COMBAT_BUDGET","Expensive action budget exceeded; no automatic kick is issued by new action checks.",$"decision={(enforce?"denied":"observed")};shadow={config.PacketGuardNewActionShadowMode};rate={rate};burst={rate*3}",AntiCheatSeverity.Info);
        return !enforce;
    }

    private bool ValidateInventoryAction(ConnectedClient client,string source,int sourceSlot,string? target=null,int targetSlot=0)
    {
        if(!ConsumeActionBudget(client,true)){QueueDeniedResync(client,null,source,sourceSlot,target,targetSlot);return false;}
        var player=client.Player;
        if(player?.InventoryManager==null||ExemptNewActionContext(player)||!config.PacketGuardInventoryChecksEnabled||
            ServerProtectionExemptions.Has(player.PlayerUID,ServerProtectionScope.Inventory,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))return true;
        bool unknown=false;
        bool Known(string id,int slot)
        {
            try
            {

            if(!player.InventoryManager.GetInventory(id,out var inv)||inv==null)return false;
            // Native tabbed/ground/custom inventories keep their own virtual slot semantics.
            bool ordinary=inv.GetType()==typeof(InventoryGeneric)||inv.GetType().FullName is "Vintagestory.Common.InventoryPlayerHotbar" or "Vintagestory.Common.InventoryPlayerBackpacks";
            if(ordinary&&slot>=inv.Count)return false;
            bool own=id.EndsWith("-"+player.PlayerUID,StringComparison.Ordinal);
            return own||inv.HasOpened(player);
            }
            catch(Exception ex){unknown=true;Report(client,"SERVER_ACTION_OBSERVER_UNAVAILABLE","Inventory state observation skipped incompatible custom inventory state.","component=inventory-state;exceptionType="+ex.GetType().Name,AntiCheatSeverity.Info);return true;}
        }
        if(Known(source,sourceSlot)&&(target==null||Known(target,targetSlot)))return true;
        if(unknown)return true;
        bool deny=!config.PacketGuardNewActionShadowMode;
        Report(client,"SERVER_ACTION_INVENTORY_STATE","Inventory request referenced unavailable, unopened or out-of-bounds ordinary state. Review stale UI and mod compatibility.",$"decision={(deny?"denied":"observed")};shadow={config.PacketGuardNewActionShadowMode};source={Clean(source)};sourceSlot={sourceSlot};target={Clean(target)};targetSlot={targetSlot}",AntiCheatSeverity.Info);
        if(deny)QueueDeniedResync(client,null,source,sourceSlot,target,targetSlot);
        return !deny;
    }

    internal void ResyncDeniedBlock(IServerPlayer player,BlockPos pos)
    {
        if(!active||server==null)return;
        var client=server.GetClient(player.ClientId);
        if(client!=null&&ReferenceEquals(client.Player,player))QueueDeniedResync(client,pos.Copy());
    }

    private void QueueDeniedResync(ConnectedClient client,BlockPos? block,string? source=null,int sourceSlot=0,string? target=null,int targetSlot=0)
    {
        if(!config.PacketGuardDeniedActionResyncEnabled)return;
        var player=client.Player;if(player==null)return;
        var state=connections.GetValue(client,_=>new ConnectionState());long now=Environment.TickCount64;
        lock(state){if(now-state.LastResync<1000)return;state.LastResync=now;}
        lock(resyncBudget)if(!resyncBudget.TryConsume(now,16,16))return;
        api.Event.EnqueueMainThreadTask(()=>
        {
            if(!active||!ReferenceEquals(client.Player,player)||Environment.TickCount64-now>2000)return;
            try
            {
                deniedResync??=new DeniedActionResynchronizer(api,config);
                if(block!=null)deniedResync.Block(player,block);
                if(source!=null)deniedResync.Slot(player,source,sourceSlot);
                if(target!=null)deniedResync.Slot(player,target,targetSlot);
            }
            catch(Exception ex)
            {
                bool log;lock(state){long at=Environment.TickCount64;log=at-state.LastResyncError>=30000;if(log)state.LastResyncError=at;}
                if(log)api.Logger.Debug("[PacketGuard] Denied action resync unavailable: {0}",ex.GetType().Name);
            }
        },"packetguard-denied-resync");
    }

    private bool ValidateBreakBurst(ConnectedClient client,int x,int y,int z)
    {
        var player=client.Player;
        if(!config.PacketGuardBlockNukerEnabled || player==null || ServerProtectionExemptions.Has(player.PlayerUID,ServerProtectionScope.BlockNuker,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) || player.WorldData.CurrentGameMode!=EnumGameMode.Survival ||
            (config.PacketGuardExemptAdmins && (player.HasPrivilege(Privilege.root)||player.HasPrivilege(Privilege.controlserver))))return true;
        var health=getTickHealth();
        bool reliable=!health.Enabled || (health.Ready && !health.IsDegraded && !health.IsCritical);
        long now=Environment.TickCount64;
        var state=connections.GetValue(client,_=>new ConnectionState());
        (bool Allow,bool Kick,int Distinct) result;
        int limit=config.PacketGuardBlockNukerLimit;
        int dimension=EntityRuntimeCompat.GetPos(player.Entity)?.Dimension ?? -1;
        int epoch;
        lock(state)
        {
            if(!ReferenceEquals(state.NukerPlayer,player) || state.NukerEntity!=player.Entity.EntityId ||
                state.NukerDimension!=dimension || state.NukerLimit!=limit)
            {
                state.Nuker.Reset();state.NukerPlayer=player;state.NukerEntity=player.Entity.EntityId;
                state.NukerDimension=dimension;state.NukerLimit=limit;state.NukerEpoch++;
                state.NukerKickPending=state.NukerKicked=false;
            }
            epoch=state.NukerEpoch;
            result=state.Nuker.Observe(now,x,y,z,limit,reliable);
        }
        if(result.Allow)return true;
        QueueDeniedResync(client,new BlockPos(x,y,z));
        bool emitReport,scheduleKick;
        lock(state)
        {
            emitReport=!state.ReportTimes.TryGetValue("PACKET_BLOCK_NUKER",out long previous) ||
                now-previous>=config.PacketGuardIngressFindingCooldownSeconds*1000L;
            scheduleKick=result.Kick && config.PacketGuardBlockNukerKickEnabled && !state.NukerKickPending && !state.NukerKicked;
            if(scheduleKick)state.NukerKickPending=true;
        }
        // Saturated traffic must not allocate evidence strings for every denied packet.
        if(!emitReport && !scheduleKick)return false;
        string evidence=$"decision=denied;distinctBreakTargets={result.Distinct};limit={limit};windowMs=1000;repeatedHealthyBurst={result.Kick};{health.Evidence}";
        if(emitReport)Report(client,"PACKET_BLOCK_NUKER", "Denied excessive distinct client block-break requests before the native handler. A single burst is not sufficient for a kick.",evidence,AntiCheatSeverity.Moderate);
        if(!scheduleKick)return false;
        api.Event.EnqueueMainThreadTask(()=>
        {
            try
            {
                lock(state)if(state.NukerEpoch!=epoch)return;
                var currentHealth=getTickHealth();
                if(!active || !config.Enabled || !config.PacketGuardBlockNukerEnabled || !config.PacketGuardBlockNukerKickEnabled ||
                    !ReferenceEquals(client.Player,player) || config.PacketGuardBlockNukerLimit!=limit ||
                    player.Entity==null || player.Entity.EntityId!=state.NukerEntity || (EntityRuntimeCompat.GetPos(player.Entity)?.Dimension ?? -1)!=dimension ||
                    player.WorldData.CurrentGameMode!=EnumGameMode.Survival ||
                    (config.PacketGuardExemptAdmins && (player.HasPrivilege(Privilege.root)||player.HasPrivilege(Privilege.controlserver))) ||
                    ServerProtectionExemptions.Has(player.PlayerUID,ServerProtectionScope.BlockNuker,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ||
                    Environment.TickCount64-now>1000 ||
                    (currentHealth.Enabled && (!currentHealth.Ready||currentHealth.IsDegraded||currentHealth.IsCritical)))return;
                reportFinding(player,"PACKET_BLOCK_NUKER_KICK",AntiCheatSeverity.High,
                    "Kicked after independently spaced excessive client break bursts; secondary server-side mining actions are excluded.",player.PlayerUID,evidence+";decision=kick");
                api.Logger.Audit("[PacketGuard/BlockNuker] playerUid={0};action=kick;{1}",player.PlayerUID,evidence);
                lock(state)state.NukerKicked=true;
                player.Disconnect("Repeated excessive block-breaking requests (Block Nuker protection).");
            }
            finally {lock(state)if(state.NukerEpoch==epoch)state.NukerKickPending=false;}
        },"packetguard-block-nuker-kick");
        return false;
    }

    private int NormalizeViewDistance(ConnectedClient client, int requested)
    {
        int approved = PacketIngressPolicy.BoundViewDistanceBlocks(requested, config.PacketGuardMaxViewDistanceBlocks,
            server!.Config.MaxChunkRadius, GlobalConstants.ChunkSize);
        // A normal client can request more than this server permits. Clamp all requests, but
        // only treat pathological values as evidence; normal preferences are not cheat findings.
        if (requested < 0 || requested > 4096)
            Report(client, "PACKET_VIEW_DISTANCE_ABUSE", "Normalized a pathological view-distance request before chunk work was scheduled.", $"requestedBlocks={requested};approvedBlocks={approved};stage=pre-dispatch", AntiCheatSeverity.High);
        return approved;
    }

    private static bool ValidSelection(long x, long y, long z, int index, string? id) =>
        index >= 0 && index <= 8192 && (id?.Length ?? 0) <= 256 &&
        PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDouble(x)) &&
        PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDouble(y)) &&
        PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDouble(z));

    private bool ValidateBlockTarget(ConnectedClient client, int x, int internalY, int z)
    {
        var player = client.Player;
        var entity = player?.Entity;
        // Some reference sets expose Entity.Pos as a field, others as a property.
        // Use the same runtime-compatible accessor as the movement/reach sampler.
        EntityPos? position = EntityRuntimeCompat.GetPos(entity);
        if (player == null || entity == null || position == null ||
            !PacketIngressPolicy.IsValidCoordinate(position.X) ||
            !PacketIngressPolicy.IsValidCoordinate(position.Y) ||
            !PacketIngressPolicy.IsValidCoordinate(position.Z))
            return false; // Not ready: drop the action without findings or a disconnect.
        BlockPos target = new(x, internalY, z);
        if (x < 0 || z < 0 || x >= api.World.BlockAccessor.MapSizeX || z >= api.World.BlockAccessor.MapSizeZ ||
            target.Y < 0 || target.Y >= api.World.BlockAccessor.MapSizeY)
            return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected an out-of-world block target before world/block-entity access.", $"target={x},{internalY},{z}");
        bool transition = PacketGuardTrustedState.IsActive(player.PlayerUID, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), PacketGuardTrustScope.Reach);
        if (target.dimension != position.Dimension)
        {
            if (transition)
            {
                PacketGuardTrustedState.RecordSuppression(player, "ingress-stale-transition-target", $"Dropped stale dimension target {x},{internalY},{z} during a trusted transition.");
                return false;
            }
            return Reject(client, "PACKET_UNAUTHORIZED_BLOCK", "Rejected a cross-dimension block target before world/block-entity access.", $"target={x},{internalY},{z}");
        }
        // Do not request/load chunks here. Unknown terrain cannot authorize mutation.
        if (api.World.BlockAccessor.GetChunkAtBlockPos(target) == null)
        {
            // Lag, unloads and settling teleports can leave the client displaying stale terrain.
            // Reject the action safely, but this condition alone is not cheat evidence.
            PacketGuardTrustedState.RecordSuppression(player, "ingress-unloaded-target", $"Dropped action on unloaded target {x},{internalY},{z}; no chunk load requested.");
            return false;
        }
        if (player.WorldData.CurrentGameMode == EnumGameMode.Survival &&
            !transition &&
            !(config.PacketGuardExemptAdmins && (player.HasPrivilege(Privilege.root) || player.HasPrivilege(Privilege.controlserver))))
        {
            // This hard ingress bound protects decor/BE paths that omit vanilla reach checks.
            // Existing PacketGuard history/TPS logic handles ordinary near-threshold actions.
            double allowed = Math.Max(16, player.WorldData.PickingRange + config.PacketGuardReachTolerance + 8);
            double dx = Math.Max(0, Math.Abs(position.X - (x + 0.5)) - 0.5);
            double dy = Math.Max(0, Math.Abs(position.Y + entity.LocalEyePos.Y - (target.Y + 0.5)) - 0.5);
            double dz = Math.Max(0, Math.Abs(position.Z - (z + 0.5)) - 0.5);
            if (dx * dx + dy * dy + dz * dz > allowed * allowed)
                return Reject(client, "PACKET_UNAUTHORIZED_BLOCK", "Rejected a block/decor/block-entity action far outside the player's server-owned interaction range.", $"target={x},{internalY},{z};hardReach={allowed:0.0}");
        }
        return true;
    }

    private bool ValidatePlayerPosition(Packet_EntityPosition? packet, ServerPlayer player)
    {
        if (!active || player == null) return true;
        ConnectedClient? client = server!.GetClient(player.ClientId);
        if (client == null || !ReferenceEquals(client.Player, player)) return false;
        if (packet == null || packet.EntityId != player.Entity.EntityId ||
            !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.X)) ||
            !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.Y)) ||
            !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.Z)) ||
            !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.MotionX)) ||
            !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.MotionY)) ||
            !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.MotionZ)))
            return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected an invalid UDP/TCP-fallback player-position packet before applying it.", "stage=player-position;reason=identity-or-coordinate");
        ConnectionState state = connections.GetValue(client, _ => new ConnectionState());
        return ConsumeBudget(client,state,state.Positions,state.PositionFlood,config.PacketGuardIngressPacketsPerSecond,config.PacketGuardIngressPacketBurst,"budget=positions");
    }

    private bool BlockBudget(ConnectedClient client)
    {
        ConnectionState state = connections.GetValue(client, _ => new ConnectionState());
        return ConsumeBudget(client,state,state.Blocks,state.BlockFlood,config.PacketGuardBlockPacketsPerSecond,config.PacketGuardBlockPacketBurst,"budget=block-actions");
    }

    private bool SettingsBudget(ConnectedClient client)
    {
        ConnectionState state = connections.GetValue(client, _ => new ConnectionState());
        return ConsumeBudget(client,state,state.Settings,state.SettingsFlood,config.PacketGuardSettingsPacketsPerSecond,config.PacketGuardSettingsPacketBurst,"budget=settings");
    }

    private bool ConsumeBudget(ConnectedClient client,ConnectionState state,PacketIngressBudget budget,PacketFloodConfirmation confirmation,int rate,int burst,string evidence)
    {
        long now=Environment.TickCount64;bool confirmed;
        lock(state)
        {
            if(budget.TryConsume(now,rate,burst))return true;
            if(now-state.HealthCheckedUtc>=250||state.HealthCheckedUtc==0)
            {var health=getTickHealth();state.TimingReliable=!health.Enabled||(health.Ready&&!health.IsDegraded);state.HealthCheckedUtc=now;}
            confirmed=confirmation.Observe(now,state.TimingReliable);
        }
        Report(client,"PACKET_FLOOD","Dropped traffic above a bounded resource budget; isolated bursts and lag catch-up are review-only.",evidence,AntiCheatSeverity.High,confirmed,budget,confirmation);
        return false;
    }

    private bool Malformed(ConnectedClient client, int packetId) =>
        Reject(client, "PACKET_MALFORMED_PACKET", "Rejected a malformed vanilla action packet before handler execution.", $"packetId={packetId};reason=shape-or-bounds");

    private bool Reject(ConnectedClient client, string code, string summary, string evidence)
    {
        Report(client, code, summary, evidence, AntiCheatSeverity.High);
        return false;
    }

    private void Report(ConnectedClient client, string code, string summary, string evidence, AntiCheatSeverity severity,bool floodCandidate=false,PacketIngressBudget? budget=null,PacketFloodConfirmation? confirmation=null)
    {
        ConnectionState state = connections.GetValue(client, _ => new ConnectionState());
        long now = Environment.TickCount64;
        lock (state)
        {
            if (state.ReportTimes.TryGetValue(code, out long previous) && now - previous < config.PacketGuardIngressFindingCooldownSeconds * 1000L) return;
            state.ReportTimes[code] = now;
            // Snapshot under the same connection lock; format only after the report cooldown,
            // never for every packet dropped during an attack.
            if(budget!=null)evidence+=";"+budget.Snapshot.Evidence;
            if(confirmation!=null){var window=confirmation.Evidence;evidence+=$";observationWindows={window.Windows};confirmationSpanMs={window.SpanMs};minimumObservationWindows=10;observationSpacingMs=100";}

        }
        IServerPlayer? observedPlayer = client.Player;
        // Parser hooks run off-thread. Coalesce before queueing and deliver findings on the
        // main thread, where the anti-cheat session/history/consequence services are owned.
        api.Event.EnqueueMainThreadTask(() =>
        {
            if (!active) return;
            IServerPlayer? player = observedPlayer;
            // A queued finding belongs to the player observed on this connection, not
            // someone authenticated/replaced before the main-thread callback executes.
            if(player!=null&&!ReferenceEquals(client.Player,player))return;
            if(code=="PACKET_FLOOD")
            {
                var health=getTickHealth();long queueDelay=Math.Max(0,Environment.TickCount64-now);
                bool confirmed=floodCandidate&&queueDelay<=1000&&(!health.Enabled||(health.Ready&&!health.IsDegraded));
                evidence+=$";floodConfirmed={confirmed};confirmationMinimumMs=3000;queueDelayMs={queueDelay};{health.Evidence}";
                if(!confirmed)severity=AntiCheatSeverity.Info;
            }
            api.Logger.Warning("[ServerEssentials/AntiCheat/Ingress] connection={0};playerUid={1};player={2};code={3};{4}",
                client.Id, player?.PlayerUID ?? "unauthenticated", Clean(player?.PlayerName), code, evidence);
            if (player != null && player.ClientId == client.Id)
                reportFinding(player, code, severity, summary, player.PlayerUID, evidence);
        }, "packetguard-ingress-finding");
    }

    private static string Clean(string? value)
    {
        string clean = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace(';', '_');
        return clean.Length > 80 ? clean[..80] : clean;
    }

    public void Dispose()
    {
        active = false;
        Status = "Stopped";
        if (ReferenceEquals(current, this)) current = null;
        try { harmony?.UnpatchAll(HarmonyId); }
        catch (Exception ex) { api.Logger.Warning("[ServerEssentials/AntiCheat] Ingress unpatch error: {0}", ex.Message); }
        harmony = null;
    }
}
