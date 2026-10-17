using System;
using System.Collections.Generic;
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

internal sealed class PacketIngressGuard : IDisposable
{
	private readonly record struct ParseContext(int Limit, ConnectedClient? Client);

	private sealed class ConnectionState
	{
		internal readonly PacketIngressBudget Wire = new PacketIngressBudget();

		internal readonly PacketIngressBudget Blocks = new PacketIngressBudget();

		internal readonly PacketIngressBudget Settings = new PacketIngressBudget();

		internal readonly PacketIngressBudget Positions = new PacketIngressBudget();

		internal readonly Dictionary<string, long> ReportTimes = new Dictionary<string, long>(StringComparer.Ordinal);
	}

	private const string HarmonyId = "newfies.serveressentials.packetguard.ingress";

	private static PacketIngressGuard? current;

	private readonly ICoreServerAPI api;

	private readonly AntiCheatConfig config;

	private readonly Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> reportFinding;

	private readonly ConditionalWeakTable<ConnectedClient, ConnectionState> connections = new ConditionalWeakTable<ConnectedClient, ConnectionState>();

	private Harmony? harmony;

	private ServerMain? server;

	private bool active;

	private static AccessTools.FieldRef<ReceivedClientPacket, ConnectedClient>? receivedClient;

	private static AccessTools.FieldRef<ReceivedClientPacket, Packet_Client>? receivedPacket;

	private static AccessTools.FieldRef<CitoMemoryStream, int>? inputLength;

	[ThreadStatic]
	private static int parsingLimit;

	[ThreadStatic]
	private static ConnectedClient? parsingClient;

	internal bool IsActive => active;

	internal string Status { get; private set; } = "Disabled";

	internal int InstalledHookCount { get; private set; }

	internal PacketIngressGuard(ICoreServerAPI api, AntiCheatConfig config, Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> reportFinding)
	{
		this.api = api;
		this.config = config;
		this.reportFinding = reportFinding;
	}

	internal void Start()
	{
		if (!config.PacketGuardIngressEnabled || active)
		{
			return;
		}
		try
		{
			string text = (typeof(GameVersion).GetField("ShortGameVersion", BindingFlags.Static | BindingFlags.Public)?.GetRawConstantValue() as string) ?? "unknown";
			if (text != "1.22.7" || !(api.World is ServerMain serverMain))
			{
				throw new NotSupportedException("Ingress hooks require the verified standard 1.22.7 server runtime; found " + text + ".");
			}
			server = serverMain;
			if (current != null && current != this)
			{
				throw new InvalidOperationException("An ingress guard is already installed.");
			}
			receivedClient = AccessTools.FieldRefAccess<ReceivedClientPacket, ConnectedClient>("client");
			receivedPacket = AccessTools.FieldRefAccess<ReceivedClientPacket, Packet_Client>("packet");
			inputLength = AccessTools.FieldRefAccess<CitoMemoryStream, int>("bufferlength");
			harmony = new Harmony("newfies.serveressentials.packetguard.ingress");
			MethodInfo original = AccessTools.DeclaredMethod(typeof(ServerMain), "ParseClientPacket_offthread", new Type[3]
			{
				typeof(ConnectedClient),
				typeof(byte[]),
				typeof(int)
			}) ?? throw new MissingMethodException(typeof(ServerMain).FullName, "ParseClientPacket_offthread");
			harmony.Patch(original, new HarmonyMethod(typeof(PacketIngressGuard), "BeforeParse")
			{
				priority = 800
			}, null, null, new HarmonyMethod(typeof(PacketIngressGuard), "FinishParse")
			{
				priority = 0
			});
			InstalledHookCount++;
			MethodInfo original2 = AccessTools.DeclaredMethod(typeof(Packet_UdpPacketSerializer), "DeserializeBuffer", new Type[3]
			{
				typeof(byte[]),
				typeof(int),
				typeof(Packet_UdpPacket)
			}) ?? throw new MissingMethodException(typeof(Packet_UdpPacketSerializer).FullName, "DeserializeBuffer");
			harmony.Patch(original2, new HarmonyMethod(typeof(PacketIngressGuard), "BeforeUdpParse")
			{
				priority = 800
			}, null, null, new HarmonyMethod(typeof(PacketIngressGuard), "FinishUdpParse")
			{
				priority = 0
			});
			InstalledHookCount++;
			Patch(typeof(ProtocolParser), "ReadBytes", new Type[1] { typeof(CitoStream) }, "BeforeReadBytes");
			Patch(typeof(ServerMain), "HandleClientPacket_mainthread", new Type[1] { typeof(ReceivedClientPacket) }, "BeforeDispatch");
			Patch(typeof(ServerUdpNetwork), "HandlePlayerPosition", new Type[2]
			{
				typeof(Packet_EntityPosition),
				typeof(ServerPlayer)
			}, "BeforePlayerPosition");
			current = this;
			active = true;
			Status = "Active on Vintage Story 1.22.7";
			api.Logger.Notification("[ServerEssentials/AntiCheat] PacketGuard vanilla ingress active (1.22.7): pre-deserialize cap={0} KiB, packet budget={1}/s burst={2}; identification/settings view cap={3} blocks plus server radius; block/decor/BE/hand guards and UDP/TCP position validation active; length-prefixed byte arrays bounded BEFORE allocation; hooks={4}.", config.PacketGuardIngressMaxPacketKiB, config.PacketGuardIngressPacketsPerSecond, config.PacketGuardIngressPacketBurst, config.PacketGuardMaxViewDistanceBlocks, InstalledHookCount);
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
		MethodInfo original = AccessTools.DeclaredMethod(type, method, parameters) ?? throw new MissingMethodException(type.FullName, method);
		harmony.Patch(original, new HarmonyMethod(typeof(PacketIngressGuard), prefix)
		{
			priority = 800
		});
		InstalledHookCount++;
	}

	private static bool BeforeParse(ConnectedClient client, byte[] data, int length, out ParseContext __state)
	{
		__state = new ParseContext(parsingLimit, parsingClient);
		if (current == null)
		{
			return true;
		}
		if (!current.ValidateWire(client, data, length))
		{
			return false;
		}
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
		if (current != null)
		{
			if (__0 == null || __1 <= 0 || __1 > __0.Length || __1 > (long)current.config.PacketGuardIngressMaxPacketKiB * 1024L)
			{
				throw new InvalidDataException("Invalid client UDP frame length.");
			}
			parsingLimit = current.config.PacketGuardIngressMaxPacketKiB * 1024;
			parsingClient = null;
		}
	}

	private static Exception? FinishUdpParse(Packet_UdpPacket __2, ParseContext __state, Exception? __exception)
	{
		FinishParse(__state);
		if (__exception != null || current == null)
		{
			return __exception;
		}
		return ValidUdpShape(__2) ? null : new InvalidDataException("Invalid client UDP packet shape.");
	}

	private static bool ValidCustomMessageId(int id)
	{
		return (uint)id < 256u;
	}

	private static bool ValidUdpShape(Packet_UdpPacket? packet)
	{
		if (packet == null)
		{
			return false;
		}
		int id = packet.Id;
		if (1 == 0)
		{
		}
		bool result = id switch
		{
			1 => packet.ConnectionPacket != null && !string.IsNullOrWhiteSpace(packet.ConnectionPacket.LoginToken), 
			2 => packet.EntityPosition != null, 
			3 => packet.EntityPosition != null && (packet.ChannelPacket == null || (packet.ChannelPacket.Data != null && ValidCustomMessageId(packet.ChannelPacket.MessageId))), 
			6 => packet.ChannelPacket != null && packet.ChannelPacket.Data != null && ValidCustomMessageId(packet.ChannelPacket.MessageId), 
			_ => true, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static bool BeforeReadBytes(CitoStream stream, ref byte[] __result)
	{
		if (current == null || parsingLimit == 0)
		{
			return true;
		}
		int num = ProtocolParser.ReadUInt32(stream);
		int num2 = ((stream is CitoMemoryStream citoMemoryStream) ? (inputLength(citoMemoryStream) - citoMemoryStream.Position()) : (-1));
		if (num < 0 || num > parsingLimit || num > num2)
		{
			if (parsingClient != null)
			{
				current.Report(parsingClient, "PACKET_MALFORMED_PACKET", "Rejected an impossible length-prefixed byte array before allocating it.", $"stage=deserialize;declaredBytes={num};remainingBytes={num2}", AntiCheatSeverity.High);
			}
			throw new InvalidDataException("Invalid client byte-array length.");
		}
		__result = new byte[num];
		int num3;
		for (int i = 0; i < num; i += num3)
		{
			num3 = stream.Read(__result, i, num - i);
			if (num3 <= 0)
			{
				throw new InvalidDataException("Truncated client byte array.");
			}
		}
		return false;
	}

	private static bool BeforeDispatch(ReceivedClientPacket cpk)
	{
		if (current == null || cpk == null)
		{
			return true;
		}
		Packet_Client packet_Client = receivedPacket(cpk);
		return packet_Client == null || current.ValidatePacket(receivedClient(cpk), packet_Client);
	}

	private static bool BeforePlayerPosition(Packet_EntityPosition packet, ServerPlayer player)
	{
		return current == null || current.ValidatePlayerPosition(packet, player);
	}

	private bool ValidateWire(ConnectedClient client, byte[]? data, int length)
	{
		if (!active || client == null)
		{
			return true;
		}
		if (data == null || length <= 0 || length > data.Length || length > (long)config.PacketGuardIngressMaxPacketKiB * 1024L)
		{
			return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected an invalid or oversized vanilla TCP packet before deserialization.", $"stage=pre-deserialize;length={length};capKiB={config.PacketGuardIngressMaxPacketKiB}");
		}
		ConnectionState value = connections.GetValue(client, (ConnectedClient _) => new ConnectionState());
		bool flag;
		lock (value)
		{
			flag = value.Wire.TryConsume(Environment.TickCount64, config.PacketGuardIngressPacketsPerSecond, config.PacketGuardIngressPacketBurst);
		}
		return flag || Reject(client, "PACKET_FLOOD", "Dropped client packets above the bounded ingress budget before parsing/queueing.", "stage=pre-deserialize;budget=wire");
	}

	private bool ValidatePacket(ConnectedClient client, Packet_Client packet)
	{
		if (!active || client == null)
		{
			return true;
		}
		if (packet.Id < 0 || packet.Id >= 255)
		{
			return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected a packet ID outside the server handler table.", $"packetId={packet.Id}");
		}
		IInventory hotbarInventory;
		int slotNumber;
		int num;
		IInventory ownInventory;
		int result;
		switch (packet.Id)
		{
		case 1:
			if (packet.Identification == null)
			{
				return Malformed(client, packet.Id);
			}
			packet.Identification.ViewDistance = NormalizeViewDistance(client, packet.Identification.ViewDistance);
			return SettingsBudget(client);
		case 20:
			if (packet.RequestModeChange == null || client.Player == null)
			{
				return Malformed(client, packet.Id);
			}
			if (string.IsNullOrWhiteSpace(packet.RequestModeChange.PlayerUID) || (packet.RequestModeChange.PlayerUID != client.Player.PlayerUID && !client.Player.HasPrivilege(Privilege.commandplayer)))
			{
				return Reject(client, "PACKET_UNAUTHORIZED_SETTINGS", "Rejected settings targeting another player before the vanilla handler can modify their view distance.", $"packetId={packet.Id};target=other-player");
			}
			if (!SettingsBudget(client))
			{
				return false;
			}
			if (!Enum.IsDefined(typeof(EnumGameMode), packet.RequestModeChange.GameMode))
			{
				return Malformed(client, packet.Id);
			}
			packet.RequestModeChange.ViewDistance = NormalizeViewDistance(client, packet.RequestModeChange.ViewDistance);
			if (!float.IsFinite(CollectibleNet.DeserializeFloat(packet.RequestModeChange.MoveSpeed)) || !float.IsFinite(CollectibleNet.DeserializeFloat(packet.RequestModeChange.PickingRange)))
			{
				return Malformed(client, packet.Id);
			}
			return true;
		case 3:
		{
			if (packet.BlockPlaceOrBreak == null || client.Player == null)
			{
				return Malformed(client, packet.Id);
			}
			if (!BlockBudget(client))
			{
				return false;
			}
			Packet_ClientBlockPlaceOrBreak blockPlaceOrBreak = packet.BlockPlaceOrBreak;
			if (blockPlaceOrBreak.Mode < 0 || blockPlaceOrBreak.Mode > 2 || !PacketIngressPolicy.IsValidFace(blockPlaceOrBreak.OnBlockFace) || !PacketIngressPolicy.IsValidBlockId(blockPlaceOrBreak.BlockType, api.World.Blocks.Count) || !ValidSelection(blockPlaceOrBreak.HitX, blockPlaceOrBreak.HitY, blockPlaceOrBreak.HitZ, blockPlaceOrBreak.SelectionBoxIndex, PacketSelectionIdReader<Packet_ClientBlockPlaceOrBreak>.Read(blockPlaceOrBreak)))
			{
				return Malformed(client, packet.Id);
			}
			if (!ValidateBlockTarget(client, blockPlaceOrBreak.X, blockPlaceOrBreak.Y, blockPlaceOrBreak.Z))
			{
				return false;
			}
			if (blockPlaceOrBreak.Mode == 2)
			{
				api.Logger.Audit("[PacketGuard/Decor] playerUid={0};player={1};request=break-decor;target={2},{3},{4};face={5};result=pending-vanilla-claim-check", client.Player.PlayerUID, Clean(client.Player.PlayerName), blockPlaceOrBreak.X, blockPlaceOrBreak.Y, blockPlaceOrBreak.Z, blockPlaceOrBreak.OnBlockFace);
			}
			return true;
		}
		case 22:
			if (packet.BlockEntityPacket == null || client.Player == null)
			{
				return Malformed(client, packet.Id);
			}
			if (!BlockBudget(client))
			{
				return false;
			}
			return ValidateBlockTarget(client, packet.BlockEntityPacket.X, packet.BlockEntityPacket.Y, packet.BlockEntityPacket.Z);
		case 25:
		{
			if (packet.HandInteraction == null || client.Player == null)
			{
				return Malformed(client, packet.Id);
			}
			if (!BlockBudget(client))
			{
				return false;
			}
			Packet_ClientHandInteraction handInteraction = packet.HandInteraction;
			if ((handInteraction.InventoryId?.Length ?? 0) > 512 || handInteraction.SlotId < 0)
			{
				return Malformed(client, packet.Id);
			}
			if (handInteraction.EnumHandInteract < 4)
			{
				int num3 = handInteraction.SlotId;
				IInventory inventory;
				if (handInteraction.InventoryId == null)
				{
					if (num3 >= 10)
					{
						client.Player.InventoryManager.GetInventory("backpack-" + client.Player.PlayerUID, out var invFound);
						inventory = invFound;
						num3 -= 10;
					}
					else
					{
						inventory = client.Player.InventoryManager.GetHotbarInventory();
					}
				}
				else
				{
					client.Player.InventoryManager.Inventories.TryGetValue(handInteraction.InventoryId, out var value);
					inventory = value;
				}
				if (inventory != null && num3 >= inventory.Count)
				{
					return Malformed(client, packet.Id);
				}
			}
			if (handInteraction.UsingCount < 0 || (long)handInteraction.UsingCount - (long)client.Player.Entity.Controls.UsingCount > 512)
			{
				return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected an excessive hand-use catch-up count before it can trigger an unbounded server loop.", $"packetId={packet.Id};useCount={handInteraction.UsingCount};serverCount={client.Player.Entity.Controls.UsingCount}");
			}
			if (handInteraction.UseType < 0 || handInteraction.UseType > 3 || handInteraction.EnumHandInteract < 0 || handInteraction.EnumHandInteract > 7)
			{
				return Malformed(client, packet.Id);
			}
			if (handInteraction.EnumHandInteract >= 4 || (handInteraction.UseType != 0 && handInteraction.MouseButton == 2))
			{
				if (!PacketIngressPolicy.IsValidFace(handInteraction.OnBlockFace) || handInteraction.SelectionBoxIndex < 0 || handInteraction.SelectionBoxIndex > 8192 || (PacketSelectionIdReader<Packet_ClientHandInteraction>.Read(handInteraction)?.Length ?? 0) > 256 || !PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDoublePrecise(handInteraction.HitX)) || !PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDoublePrecise(handInteraction.HitY)) || !PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDoublePrecise(handInteraction.HitZ)))
				{
					return Malformed(client, packet.Id);
				}
				if (handInteraction.EnumHandInteract >= 4 || handInteraction.UseType == 3)
				{
					return ValidateBlockTarget(client, handInteraction.X, handInteraction.Y, handInteraction.Z);
				}
			}
			return true;
		}
		case 28:
			if (packet.BlockDamage == null || client.Player == null)
			{
				return Malformed(client, packet.Id);
			}
			if (!BlockBudget(client))
			{
				return false;
			}
			if (!PacketIngressPolicy.IsValidFace(packet.BlockDamage.Facing))
			{
				return Malformed(client, packet.Id);
			}
			return ValidateBlockTarget(client, packet.BlockDamage.PosX, packet.BlockDamage.PosY, packet.BlockDamage.PosZ);
		case 10:
			if (packet.CreateItemstack == null || client.Player == null)
			{
				return Malformed(client, packet.Id);
			}
			if (client.Player.WorldData.CurrentGameMode != EnumGameMode.Creative)
			{
				return Reject(client, "PACKET_UNAUTHORIZED_ITEMSTACK", "Rejected a creative inventory creation packet from a non-creative player.", $"packetId={packet.Id}");
			}
			return true;
		case 23:
			return (packet.CustomPacket != null && packet.CustomPacket.Data != null && ValidCustomMessageId(packet.CustomPacket.MessageId)) || Malformed(client, packet.Id);
		case 35:
			return ValidUdpShape(packet.UdpPacket) || Malformed(client, packet.Id);
		case 31:
			return packet.EntityPacket != null || Malformed(client, packet.Id);
		case 17:
			return packet.EntityInteraction != null || Malformed(client, packet.Id);
		case 32:
			return packet.RuntimeSetting != null || Malformed(client, packet.Id);
		case 27:
			if (packet.ToolMode == null || client.Player == null)
			{
				return Malformed(client, packet.Id);
			}
			if (!PacketIngressPolicy.IsValidFace(packet.ToolMode.Face) || !ValidSelection(packet.ToolMode.HitX, packet.ToolMode.HitY, packet.ToolMode.HitZ, packet.ToolMode.SelectionBoxIndex, null))
			{
				return Malformed(client, packet.Id);
			}
			return true;
		case 13:
		{
			if (packet.SelectedHotbarSlot == null)
			{
				return Malformed(client, packet.Id);
			}
			if (client.Player?.InventoryManager == null)
			{
				return false;
			}
			hotbarInventory = client.Player.InventoryManager.GetHotbarInventory();
			if (hotbarInventory == null || hotbarInventory.Count <= 10)
			{
				return false;
			}
			slotNumber = packet.SelectedHotbarSlot.SlotNumber;
			ItemSlot itemSlot = hotbarInventory[10];
			if (itemSlot == null)
			{
				return false;
			}
			num = 10 + ((!itemSlot.Empty) ? 1 : 0);
			ownInventory = client.Player.InventoryManager.GetOwnInventory("backpack");
			if (slotNumber >= 0)
			{
				bool num2;
				if (slotNumber >= num)
				{
					if (ownInventory == null)
					{
						goto IL_0c04;
					}
					num2 = slotNumber - num < ownInventory.Count;
				}
				else
				{
					num2 = slotNumber < hotbarInventory.Count;
				}
				if (num2)
				{
					result = 1;
					goto IL_0cb1;
				}
			}
			goto IL_0c04;
		}
		default:
			{
				return true;
			}
			IL_0c04:
			result = (Reject(client, "PACKET_MALFORMED_PACKET", "Rejected an invalid active hotbar/backpack selection before inventory access.", $"packetId={packet.Id};reason=active-slot-bounds;slot={slotNumber};hotbarCount={hotbarInventory.Count};backpackStart={num};backpackCount={ownInventory?.Count ?? 0}") ? 1 : 0);
			goto IL_0cb1;
			IL_0cb1:
			return (byte)result != 0;
		}
	}

	private int NormalizeViewDistance(ConnectedClient client, int requested)
	{
		int num = PacketIngressPolicy.BoundViewDistanceBlocks(requested, config.PacketGuardMaxViewDistanceBlocks, server.Config.MaxChunkRadius, 32);
		if (requested < 0 || requested > 4096)
		{
			Report(client, "PACKET_VIEW_DISTANCE_ABUSE", "Normalized a pathological view-distance request before chunk work was scheduled.", $"requestedBlocks={requested};approvedBlocks={num};stage=pre-dispatch", AntiCheatSeverity.High);
		}
		return num;
	}

	private static bool ValidSelection(long x, long y, long z, int index, string? id)
	{
		return index >= 0 && index <= 8192 && (id?.Length ?? 0) <= 256 && PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDouble(x)) && PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDouble(y)) && PacketIngressPolicy.IsValidHit(CollectibleNet.DeserializeDouble(z));
	}

	private bool ValidateBlockTarget(ConnectedClient client, int x, int internalY, int z)
	{
		ServerPlayer player = client.Player;
		EntityPlayer entityPlayer = player?.Entity;
		EntityPos pos = EntityRuntimeCompat.GetPos(entityPlayer);
		if (player == null || entityPlayer == null || pos == null || !PacketIngressPolicy.IsValidCoordinate(pos.X) || !PacketIngressPolicy.IsValidCoordinate(pos.Y) || !PacketIngressPolicy.IsValidCoordinate(pos.Z))
		{
			return false;
		}
		BlockPos blockPos = new BlockPos(x, internalY, z);
		if (x < 0 || z < 0 || x >= api.World.BlockAccessor.MapSizeX || z >= api.World.BlockAccessor.MapSizeZ || blockPos.Y < 0 || blockPos.Y >= api.World.BlockAccessor.MapSizeY)
		{
			return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected an out-of-world block target before world/block-entity access.", $"target={x},{internalY},{z}");
		}
		bool flag = PacketGuardTrustedState.IsActive(player.PlayerUID, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), PacketGuardTrustScope.Reach);
		if (blockPos.dimension != pos.Dimension)
		{
			if (flag)
			{
				PacketGuardTrustedState.RecordSuppression(player, "ingress-stale-transition-target", $"Dropped stale dimension target {x},{internalY},{z} during a trusted transition.", 750, 0L);
				return false;
			}
			return Reject(client, "PACKET_UNAUTHORIZED_BLOCK", "Rejected a cross-dimension block target before world/block-entity access.", $"target={x},{internalY},{z}");
		}
		if (api.World.BlockAccessor.GetChunkAtBlockPos(blockPos) == null)
		{
			PacketGuardTrustedState.RecordSuppression(player, "ingress-unloaded-target", $"Dropped action on unloaded target {x},{internalY},{z}; no chunk load requested.", 750, 0L);
			return false;
		}
		if (player.WorldData.CurrentGameMode == EnumGameMode.Survival && !flag && (!config.PacketGuardExemptAdmins || (!player.HasPrivilege(Privilege.root) && !player.HasPrivilege(Privilege.controlserver))))
		{
			double num = Math.Max(16.0, (double)player.WorldData.PickingRange + config.PacketGuardReachTolerance + 8.0);
			double num2 = Math.Max(0.0, Math.Abs(pos.X - ((double)x + 0.5)) - 0.5);
			double num3 = Math.Max(0.0, Math.Abs(pos.Y + entityPlayer.LocalEyePos.Y - ((double)blockPos.Y + 0.5)) - 0.5);
			double num4 = Math.Max(0.0, Math.Abs(pos.Z - ((double)z + 0.5)) - 0.5);
			if (num2 * num2 + num3 * num3 + num4 * num4 > num * num)
			{
				return Reject(client, "PACKET_UNAUTHORIZED_BLOCK", "Rejected a block/decor/block-entity action far outside the player's server-owned interaction range.", $"target={x},{internalY},{z};hardReach={num:0.0}");
			}
		}
		return true;
	}

	private bool ValidatePlayerPosition(Packet_EntityPosition? packet, ServerPlayer player)
	{
		if (!active || player == null)
		{
			return true;
		}
		ConnectedClient client = server.GetClient(player.ClientId);
		if (client == null || client.Player != player)
		{
			return false;
		}
		if (packet == null || packet.EntityId != player.Entity.EntityId || !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.X)) || !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.Y)) || !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.Z)) || !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.MotionX)) || !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.MotionY)) || !PacketIngressPolicy.IsValidCoordinate(CollectibleNet.DeserializeDoublePrecise(packet.MotionZ)))
		{
			return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected an invalid UDP/TCP-fallback player-position packet before applying it.", "stage=player-position;reason=identity-or-coordinate");
		}
		ConnectionState value = connections.GetValue(client, (ConnectedClient _) => new ConnectionState());
		if (!value.Positions.TryConsume(Environment.TickCount64, config.PacketGuardIngressPacketsPerSecond, config.PacketGuardIngressPacketBurst))
		{
			return Reject(client, "PACKET_FLOOD", "Dropped excessive player-position updates before position application/broadcast.", "budget=positions");
		}
		return true;
	}

	private bool BlockBudget(ConnectedClient client)
	{
		ConnectionState value = connections.GetValue(client, (ConnectedClient _) => new ConnectionState());
		return value.Blocks.TryConsume(Environment.TickCount64, config.PacketGuardBlockPacketsPerSecond, config.PacketGuardBlockPacketBurst) || Reject(client, "PACKET_FLOOD", "Dropped excessive block/action packets before world mutation.", "budget=block-actions");
	}

	private bool SettingsBudget(ConnectedClient client)
	{
		ConnectionState value = connections.GetValue(client, (ConnectedClient _) => new ConnectionState());
		return value.Settings.TryConsume(Environment.TickCount64, config.PacketGuardSettingsPacketsPerSecond, config.PacketGuardSettingsPacketBurst) || Reject(client, "PACKET_FLOOD", "Dropped excessive identification/settings packets before repeated chunk/settings work.", "budget=settings");
	}

	private bool Malformed(ConnectedClient client, int packetId)
	{
		return Reject(client, "PACKET_MALFORMED_PACKET", "Rejected a malformed vanilla action packet before handler execution.", $"packetId={packetId};reason=shape-or-bounds");
	}

	private bool Reject(ConnectedClient client, string code, string summary, string evidence)
	{
		Report(client, code, summary, evidence, AntiCheatSeverity.High);
		return false;
	}

	private void Report(ConnectedClient client, string code, string summary, string evidence, AntiCheatSeverity severity)
	{
		ConnectionState value = connections.GetValue(client, (ConnectedClient _) => new ConnectionState());
		long tickCount = Environment.TickCount64;
		lock (value)
		{
			if (value.ReportTimes.TryGetValue(code, out var value2) && tickCount - value2 < (long)config.PacketGuardIngressFindingCooldownSeconds * 1000L)
			{
				return;
			}
			value.ReportTimes[code] = tickCount;
		}
		api.Event.EnqueueMainThreadTask(() =>
		{
			if (active)
			{
				IServerPlayer player = client.Player;
				api.Logger.Warning("[ServerEssentials/AntiCheat/Ingress] connection={0};playerUid={1};player={2};code={3};{4}", client.Id, player?.PlayerUID ?? "unauthenticated", Clean(player?.PlayerName), code, evidence);
				if (player != null && player.ClientId == client.Id)
				{
					reportFinding(player, code, severity, summary, player.PlayerUID, evidence);
				}
			}
		}, "packetguard-ingress-finding");
	}

	private static string Clean(string? value)
	{
		string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace(';', '_');
		return (text.Length > 80) ? text.Substring(0, 80) : text;
	}

	public void Dispose()
	{
		active = false;
		Status = "Stopped";
		if (current == this)
		{
			current = null;
		}
		try
		{
			harmony?.UnpatchAll("newfies.serveressentials.packetguard.ingress");
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Ingress unpatch error: {0}", ex.Message);
		}
		harmony = null;
	}
}
