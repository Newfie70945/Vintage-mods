using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NewfiesAntiCheat.Shared.Packets;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace BlockLogger.AntiCheat;

internal sealed class OreShieldService : IDisposable
{
	private readonly record struct BreakKey(string PlayerUid, int X, int InternalY, int Z);

	private sealed record NativeBreakState(IServerPlayer Player, BlockPos Position, int OldBlockId);

	private sealed record PlacementPatchState(BlockPos Position, bool AddedExemption);

	private sealed record ChunkPacketMarker(ConnectedClient Recipient, Packet_ServerChunk Canonical);

	private const string HarmonyId = "newfies.serveressentials.anticheat.oreshield";

	private readonly ICoreServerAPI api;

	private readonly AntiCheatConfig config;

	private readonly object clientSyncReadyLock = new object();

	private readonly HashSet<string> clientSyncReadyUids = new HashSet<string>(StringComparer.Ordinal);

	private readonly HashSet<string> clientSyncRejectedLoggedUids = new HashSet<string>(StringComparer.Ordinal);

	private readonly Dictionary<int, string> clientUidById = new Dictionary<int, string>();

	private readonly ConditionalWeakTable<Packet_ServerChunk, ChunkPacketMarker> processedChunkPackets = new ConditionalWeakTable<Packet_ServerChunk, ChunkPacketMarker>();

	private readonly object processedChunkPacketsLock = new object();

	private readonly OreShieldRuntimeState? restoredRuntimeState;

	private long clientSyncHelloPackets;

	private long clientSyncRejectedHellos;

	private ServerMain? server;

	private OreShieldPalette? palette;

	private OreShieldBlockVirtualizer? virtualizer;

	private Harmony? harmony;

	private IServerNetworkChannel? syncChannel;

	private bool active;

	private string status = "Not started.";

	private long hookErrors;

	private long lastHookErrorLogUtc;

	private readonly object recentBreakSync = new object();

	private readonly Dictionary<BreakKey, long> recentBreaks = new Dictionary<BreakKey, long>();

	private readonly Queue<(BreakKey Key, long Stamp)> recentBreakOrder = new Queue<(BreakKey, long)>();

	private AccessTools.FieldRef<ChunkColumnLoadRequest, int> getChunkRequestDimension = null;

	private static OreShieldService? current;

	private static readonly System.Func<object, object> ClonePacket = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate<System.Func<object, object>>();

	public OreShieldService(ICoreServerAPI api, AntiCheatConfig config, System.Func<IServerPlayer, bool>? supportsClientSync = null, OreShieldRuntimeState? restoredRuntimeState = null)
	{
		this.api = api;
		this.config = config;
		this.restoredRuntimeState = restoredRuntimeState;
	}

	private static string ResolveRuntimeGameVersion()
	{
		try
		{
			FieldInfo field = typeof(GameVersion).GetField("ShortGameVersion", BindingFlags.Static | BindingFlags.Public);
			if ((object)field != null && field.IsLiteral && field.GetRawConstantValue() is string text && !string.IsNullOrWhiteSpace(text))
			{
				return text.Trim();
			}
		}
		catch
		{
		}
		try
		{
			Version version = typeof(ServerMain).Assembly.GetName().Version;
			if (version != null && version.Major >= 0 && version.Minor >= 0 && version.Build >= 0)
			{
				return $"{version.Major}.{version.Minor}.{version.Build}";
			}
		}
		catch
		{
		}
		return "unknown";
	}

	public bool Start(out string message)
	{
		if (active)
		{
			message = status;
			return true;
		}
		if (!config.OreShieldEnabled)
		{
			status = "Disabled by configuration.";
			message = status;
			return false;
		}
		try
		{
			string text = ResolveRuntimeGameVersion();
			if (!string.Equals(text, "1.22.7", StringComparison.OrdinalIgnoreCase))
			{
				throw new NotSupportedException("Ore Shield packet hooks are validated for Vintage Story 1.22.7; runtime reports '" + text + "'.");
			}
			if (!(api.World is ServerMain serverMain))
			{
				throw new NotSupportedException("Ore Shield requires the standard Vintage Story server runtime.");
			}
			if (current != null && current != this)
			{
				throw new InvalidOperationException("Another Newfie's Ore Shield instance is already active.");
			}
			server = serverMain;
			syncChannel = api.Network.GetChannel("newfiesoreshieldsync");
			if (syncChannel == null)
			{
				syncChannel = api.Network.RegisterChannel("newfiesoreshieldsync").RegisterMessageType<OreShieldSyncBatchPacket>().RegisterMessageType<OreShieldSyncAckPacket>()
					.RegisterMessageType<OreShieldChunkSyncRequestPacket>()
					.RegisterMessageType<OreShieldSyncHelloPacket>();
			}
			syncChannel.SetMessageHandler<OreShieldSyncAckPacket>(OnClientSyncAck).SetMessageHandler<OreShieldChunkSyncRequestPacket>(OnClientSyncRequest).SetMessageHandler<OreShieldSyncHelloPacket>(OnClientSyncHello);
			palette = new OreShieldPalette(api.World, config);
			if (palette.ProtectedOreVariants == 0)
			{
				throw new InvalidOperationException("No safe host-rock ore variants were resolved from the loaded block registry.");
			}
			virtualizer = new OreShieldBlockVirtualizer(api, serverMain, config, palette, syncChannel, SupportsClientSync);
			RestoreRuntimeState();
			harmony = new Harmony("newfies.serveressentials.anticheat.oreshield");
			getChunkRequestDimension = AccessTools.FieldRefAccess<ChunkColumnLoadRequest, int>("dimension");
			Type type = typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemSendChunks", throwOnError: true);
			List<string> list = PatchRecipientChunkSendPaths(type);
			if (list.Count <= 0)
			{
				throw new MissingMethodException(type.FullName, "recipient-aware Packet_ServerChunk send path");
			}
			api.Logger.Notification("[ServerEssentials/AntiCheat/OreShield] Installed recipient chunk masking on {0} compatible send path(s).", list.Count);
			foreach (string item in list)
			{
				api.Logger.Notification("[ServerEssentials/AntiCheat/OreShield] Chunk mask hook: {0}", item);
			}
			Patch(typeof(ServerMain), "SendSetBlocksPacket", new Type[2]
			{
				typeof(List<BlockPos>),
				typeof(int)
			}, "BeforeSetBlocksPacket", prefix: true, 800);
			Patch(typeof(ServerMain), "SendSetBlock", new Type[6]
			{
				typeof(IServerPlayer),
				typeof(int),
				typeof(int),
				typeof(int),
				typeof(int),
				typeof(bool)
			}, "BeforeSetBlock", prefix: true, 800);
			PatchNativeBreak(server.BlockAccessor.GetType());
			Type type2 = typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemBlockSimulation", throwOnError: false);
			MethodInfo methodInfo = ((type2 == null) ? null : AccessTools.DeclaredMethod(type2, "TryModifyBlockInWorld", new Type[2]
			{
				typeof(ServerPlayer),
				typeof(Packet_ClientBlockPlaceOrBreak)
			}));
			if (methodInfo != null)
			{
				harmony.Patch(methodInfo, new HarmonyMethod(typeof(OreShieldService), "BeforeTryModifyBlockInWorld")
				{
					priority = 800
				});
				harmony.Patch(methodInfo, null, new HarmonyMethod(typeof(OreShieldService), "AfterTryModifyBlockInWorld")
				{
					priority = 0
				});
			}
			else
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] Player-placement pre-exemption hook unavailable; retaining DidPlaceBlock correction fallback.");
			}
			Type type3 = typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemSupplyChunks", throwOnError: true);
			Patch(type3, "mainThreadLoadChunkColumn", new Type[1] { typeof(ChunkColumnLoadRequest) }, "AfterColumnLoaded", prefix: false, 0);
			current = this;
			active = true;
			api.Event.BreakBlock += OnBreakBlock;
			api.Event.DidBreakBlock += OnDidBreakBlock;
			api.Event.DidPlaceBlock += OnDidPlaceBlock;
			api.Event.PlayerDisconnect += OnPlayerDisconnect;
			status = $"Active v3.3.0 Perf4 ScopedMining1 RecipientFix1: {palette.ProtectedOreVariants} ore variants, {palette.HostVariants} host-rock variants; bounded server LOS discovery, Client Integrity sync v{3}, runtime-persistent discoveries, and fail-closed recipient masking; isolated recipient packets, compact discovery records, targeted invalidation, bounded revision epochs, deferred exposure geometry, coalesced mining updates and compact placed-ore save checkpoints.";
			message = status;
			api.Logger.Notification("[ServerEssentials/AntiCheat/OreShield] {0}", status);
			if (palette.SkippedOreCandidates > 0 && palette.SkippedOreCodes.Count > 0)
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] {0} ore candidate(s) could not be safely virtualized. Sample: {1}", palette.SkippedOreCandidates, string.Join(", ", palette.SkippedOreCodes));
			}
			return true;
		}
		catch (Exception ex)
		{
			status = "Unavailable: " + ex.GetType().Name + ": " + ex.Message;
			message = status;
			api.Logger.Error("[ServerEssentials/AntiCheat/OreShield] {0}", status);
			StopInternal();
			return false;
		}
	}

	public OreShieldWebSnapshot GetSnapshot()
	{
		if (virtualizer != null)
		{
			OreShieldWebSnapshot oreShieldWebSnapshot = virtualizer.Snapshot(active, status);
			oreShieldWebSnapshot.HookErrors = hookErrors;
			lock (clientSyncReadyLock)
			{
				oreShieldWebSnapshot.ClientSyncReadyClients = clientSyncReadyUids.Count;
			}
			oreShieldWebSnapshot.ClientSyncHelloPackets = clientSyncHelloPackets;
			oreShieldWebSnapshot.ClientSyncRejectedHellos = clientSyncRejectedHellos;
			return oreShieldWebSnapshot;
		}
		return new OreShieldWebSnapshot
		{
			Enabled = config.OreShieldEnabled,
			Active = false,
			Status = status,
			ProtectModdedOres = config.OreShieldProtectModdedOres,
			RevealOnUnknownNeighbor = config.OreShieldRevealOnUnknownNeighbor,
			StrictOreMatching = config.OreShieldStrictOreMatching,
			RevealRadiusBlocks = config.OreShieldRevealRadiusBlocks,
			RevealWholeVeinOnMine = config.OreShieldRevealWholeVeinOnMine,
			MaxVeinRevealBlocks = config.OreShieldMaxVeinRevealBlocks,
			DiscoveryRangeBlocks = config.OreShieldDiscoveryRangeBlocks,
			VisibilityScanIntervalMs = config.OreShieldVisibilityScanIntervalMs,
			MaxLosChecksPerPass = config.OreShieldMaxLosChecksPerPass,
			ViewConeDegrees = config.OreShieldViewConeDegrees,
			IdleRescanMs = config.OreShieldIdleRescanMs,
			LeakAuditEnabled = config.OreShieldLeakAuditEnabled,
			ChunkCacheEnabled = config.OreShieldChunkCacheEnabled,
			ChunkCacheMiB = config.OreShieldChunkCacheMiB,
			MaxCachedChunks = config.OreShieldMaxCachedChunks,
			AdditionalOrePatterns = new List<string>(config.OreShieldAdditionalOrePatterns ?? new List<string>()),
			ExcludedBlockPatterns = new List<string>(config.OreShieldExcludedBlockPatterns ?? new List<string>()),
			ProtectedOreVariants = (palette?.ProtectedOreVariants ?? 0),
			HostVariants = (palette?.HostVariants ?? 0),
			SkippedOreCandidates = (palette?.SkippedOreCandidates ?? 0),
			SyncProtocolVersion = 3,
			SyncCapability = "sync-remesh-v3",
			SyncBuildMarker = "ac1127-oreshield-v329-perf1"
		};
	}

	private void Patch(Type owner, string methodName, Type[] parameters, string handlerName, bool prefix, int priority)
	{
		MethodInfo original = AccessTools.DeclaredMethod(owner, methodName, parameters) ?? throw new MissingMethodException(owner.FullName, methodName);
		HarmonyMethod harmonyMethod = new HarmonyMethod(typeof(OreShieldService), handlerName)
		{
			priority = priority
		};
		harmony.Patch(original, prefix ? harmonyMethod : null, prefix ? null : harmonyMethod);
	}

	private void PatchNativeBreak(Type accessorType)
	{
		Type[] array = new Type[3]
		{
			typeof(BlockPos),
			typeof(IPlayer),
			typeof(float)
		};
		MethodInfo method = accessorType.GetMethod("BreakBlock", array);
		MethodInfo methodInfo = ((method?.DeclaringType == null) ? null : AccessTools.DeclaredMethod(method.DeclaringType, "BreakBlock", array));
		if (methodInfo == null || methodInfo.IsAbstract)
		{
			throw new MissingMethodException(accessorType.FullName, "Implemented BreakBlock hook");
		}
		harmony.Patch(methodInfo, new HarmonyMethod(typeof(OreShieldService), "BeforeNativeBreak")
		{
			priority = 800
		}, null, null, new HarmonyMethod(typeof(OreShieldService), "AfterNativeBreak")
		{
			priority = 0
		});
	}

	private List<string> PatchRecipientChunkSendPaths(Type sendChunksType)
	{
		MethodInfo method = AccessTools.DeclaredMethod(typeof(OreShieldService), "AfterRecipientChunkDynamic") ?? throw new MissingMethodException(typeof(OreShieldService).FullName, "AfterRecipientChunkDynamic");
		List<string> list = new List<string>();
		IEnumerable<MethodInfo> enumerable = from methodInfo2 in (from methodInfo2 in sendChunksType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				where !methodInfo2.IsAbstract && !methodInfo2.ContainsGenericParameters
				where typeof(Packet_ServerChunk).IsAssignableFrom(methodInfo2.ReturnType)
				select methodInfo2).Where((MethodInfo methodInfo2) =>
			{
				ParameterInfo[] parameters = methodInfo2.GetParameters();
				return parameters.Any((ParameterInfo x) => typeof(ServerChunk).IsAssignableFrom(x.ParameterType)) && parameters.Any((ParameterInfo x) => typeof(ConnectedClient).IsAssignableFrom(x.ParameterType));
			})
			group methodInfo2 by methodInfo2.Module.ModuleVersionId.ToString("N") + ":" + methodInfo2.MetadataToken into @group
			select @group.First();
		foreach (MethodInfo item in enumerable)
		{
			harmony.Patch(item, null, new HarmonyMethod(method)
			{
				priority = 0
			});
			list.Add(FormatMethodSignature(item));
		}
		return list;
	}

	private static string FormatMethodSignature(MethodInfo method)
	{
		string value = string.Join(", ", from p in method.GetParameters()
			select p.ParameterType.Name + " " + p.Name);
		return $"{method.DeclaringType?.FullName}.{method.Name}({value}) -> {method.ReturnType.Name}";
	}

	private Packet_ServerChunk GetRecipientChunkPacket(Packet_ServerChunk packet, ConnectedClient recipient, out bool alreadyProcessed)
	{
		lock (processedChunkPacketsLock)
		{
			if (processedChunkPackets.TryGetValue(packet, out ChunkPacketMarker value))
			{
				if (value.Recipient == recipient)
				{
					alreadyProcessed = true;
					return packet;
				}
				packet = value.Canonical;
			}
			Packet_ServerChunk packet_ServerChunk = (Packet_ServerChunk)ClonePacket(packet);
			processedChunkPackets.Add(packet_ServerChunk, new ChunkPacketMarker(recipient, packet));
			alreadyProcessed = false;
			return packet_ServerChunk;
		}
	}

	private static void AfterRecipientChunkDynamic(object[] __args, ref Packet_ServerChunk? __result)
	{
		OreShieldService oreShieldService = current;
		if (oreShieldService == null || !oreShieldService.active || oreShieldService.virtualizer == null || __args == null || __result == null)
		{
			return;
		}
		Packet_ServerChunk packet_ServerChunk = __result;
		oreShieldService.virtualizer.RecordChunkHookInvocation();
		ServerChunk serverChunk = null;
		ConnectedClient connectedClient = null;
		foreach (object obj in __args)
		{
			if (serverChunk == null && obj is ServerChunk serverChunk2)
			{
				serverChunk = serverChunk2;
			}
			else if (connectedClient == null && obj is ConnectedClient connectedClient2)
			{
				connectedClient = connectedClient2;
			}
		}
		if (serverChunk == null || connectedClient == null)
		{
			return;
		}
		try
		{
			packet_ServerChunk = (__result = oreShieldService.GetRecipientChunkPacket(packet_ServerChunk, connectedClient, out var alreadyProcessed));
			if (!alreadyProcessed)
			{
				oreShieldService.virtualizer.RecordUniqueChunkPacket();
				string playerUid = oreShieldService.ResolvePlayerUidForChunkClient(connectedClient);
				oreShieldService.virtualizer.MaskChunkForPlayerUid(playerUid, serverChunk, packet_ServerChunk);
			}
		}
		catch (Exception ex)
		{
			try
			{
				oreShieldService.virtualizer.FailClosedChunkPacket(oreShieldService.ResolvePlayerUidForChunkClient(connectedClient), serverChunk, packet_ServerChunk, "mask exception");
			}
			catch (Exception ex2)
			{
				try
				{
					packet_ServerChunk.Empty = 1;
					packet_ServerChunk.Compver = 2;
					packet_ServerChunk.SetBlocks(Array.Empty<byte>());
				}
				catch
				{
				}
				oreShieldService.ReportHookError("recipient chunk fail-closed substitution", ex2);
			}
			oreShieldService.ReportHookError("recipient chunk packet", ex);
		}
	}

	private static void AfterColumnLoaded(ChunkColumnLoadRequest chunkRequest)
	{
		OreShieldService oreShieldService = current;
		if (oreShieldService == null || !oreShieldService.active || oreShieldService.virtualizer == null || chunkRequest == null)
		{
			return;
		}
		try
		{
			int dimension = oreShieldService.getChunkRequestDimension(chunkRequest);
			oreShieldService.virtualizer.OnColumnLoaded(chunkRequest, dimension);
		}
		catch (Exception ex)
		{
			oreShieldService.ReportHookError("chunk-load reveal refresh", ex);
		}
	}

	private static bool BeforeSetBlocksPacket(List<BlockPos> positions, int packetId)
	{
		OreShieldService oreShieldService = current;
		if (oreShieldService == null || !oreShieldService.active || oreShieldService.virtualizer == null || !oreShieldService.config.OreShieldEnabled)
		{
			return true;
		}
		try
		{
			return oreShieldService.virtualizer.FilterBulkProtectedUpdates(positions, packetId);
		}
		catch (Exception ex)
		{
			oreShieldService.ReportHookError("bulk block update", ex);
			try
			{
				return oreShieldService.virtualizer.FailClosedBulkProtectedUpdates(positions);
			}
			catch (Exception ex2)
			{
				oreShieldService.ReportHookError("bulk block update fail-closed filter", ex2);
				return false;
			}
		}
	}

	private static BlockPos ResolveHookBlockPos(IServerPlayer? player, int x, int y, int z)
	{
		BlockPos blockPos = new BlockPos(x, y, z);
		if (blockPos.dimension == 0 && y >= 0 && y < 32768 && player?.Entity != null)
		{
			EntityPos entityPos = EntityRuntimeCompat.GetServerPos(player.Entity) ?? EntityRuntimeCompat.GetPos(player.Entity);
			if (entityPos != null && entityPos.Dimension != 0)
			{
				blockPos = new BlockPos(x, y, z, entityPos.Dimension);
			}
		}
		return blockPos;
	}

	private static void BeforeSetBlock(IServerPlayer player, ref int blockId, int posX, int posY, int posZ)
	{
		OreShieldService oreShieldService = current;
		if (oreShieldService == null || !oreShieldService.active || oreShieldService.virtualizer == null || !oreShieldService.config.OreShieldEnabled)
		{
			return;
		}
		try
		{
			BlockPos changed = ResolveHookBlockPos(player, posX, posY, posZ);
			if (oreShieldService.virtualizer.RequiresSingleVirtualization(blockId, changed))
			{
				blockId = oreShieldService.virtualizer.MaskSingle(player, blockId, changed);
			}
		}
		catch (Exception ex)
		{
			try
			{
				BlockPos position = ResolveHookBlockPos(player, posX, posY, posZ);
				if (oreShieldService.virtualizer.FailClosedSingleIfProtected(blockId, position))
				{
					blockId = 0;
				}
			}
			catch
			{
				if (oreShieldService.virtualizer.IsProtectedOreIdForSafety(blockId))
				{
					blockId = 0;
				}
			}
			oreShieldService.ReportHookError("single block update", ex);
		}
	}

	private static void BeforeTryModifyBlockInWorld(ServerPlayer player, Packet_ClientBlockPlaceOrBreak cmd, out PlacementPatchState? __state)
	{
		__state = null;
		OreShieldService oreShieldService = current;
		if (oreShieldService == null || !oreShieldService.active || oreShieldService.virtualizer == null || !oreShieldService.config.OreShieldEnabled || player == null || cmd == null || cmd.Mode != 1)
		{
			return;
		}
		try
		{
			if ((player.InventoryManager?.ActiveHotbarSlot?.Itemstack)?.Collectible is Block block)
			{
				BlockPos blockPos = ResolveHookBlockPos(player, cmd.X, cmd.Y, cmd.Z);
				oreShieldService.ClearBreakHandledAt(blockPos);
				bool flag = oreShieldService.virtualizer.BeginPlayerOrePlacement(blockPos, block.Id);
				if (flag || oreShieldService.virtualizer.IsPlayerPlacedOreExempt(blockPos))
				{
					__state = new PlacementPatchState(blockPos, flag);
				}
			}
		}
		catch (Exception ex)
		{
			oreShieldService.ReportHookError("pre-place ore exemption", ex);
		}
	}

	private static void AfterTryModifyBlockInWorld(bool __result, PlacementPatchState? __state)
	{
		OreShieldService oreShieldService = current;
		if (oreShieldService == null || !oreShieldService.active || oreShieldService.virtualizer == null || __state == null)
		{
			return;
		}
		try
		{
			oreShieldService.virtualizer.CompletePlayerOrePlacement(__state.Position, __state.AddedExemption, __result);
		}
		catch (Exception ex)
		{
			oreShieldService.ReportHookError("post-place ore exemption", ex);
		}
	}

	private BreakKey BreakIdentity(string uid, BlockPos pos)
	{
		return new BreakKey(uid, pos.X, pos.InternalY, pos.Z);
	}

	private void MarkBreakHandled(string uid, BlockPos pos)
	{
		long tickCount = Environment.TickCount64;
		lock (recentBreakSync)
		{
			BreakKey breakKey = BreakIdentity(uid, pos);
			recentBreaks[breakKey] = tickCount;
			recentBreakOrder.Enqueue((breakKey, tickCount));
			while (recentBreakOrder.Count > 4096 || (recentBreakOrder.Count > 0 && tickCount - recentBreakOrder.Peek().Stamp > 2000))
			{
				(BreakKey, long) tuple = recentBreakOrder.Dequeue();
				if (recentBreaks.TryGetValue(tuple.Item1, out var value) && value == tuple.Item2)
				{
					recentBreaks.Remove(tuple.Item1);
				}
			}
		}
	}

	private void ClearBreakHandledAt(BlockPos pos)
	{
		lock (recentBreakSync)
		{
			BreakKey[] array = recentBreaks.Keys.Where((BreakKey k) => k.X == pos.X && k.InternalY == pos.InternalY && k.Z == pos.Z).ToArray();
			foreach (BreakKey key in array)
			{
				recentBreaks.Remove(key);
			}
		}
	}

	private bool WasBreakHandledRecently(string uid, BlockPos pos)
	{
		long tickCount = Environment.TickCount64;
		lock (recentBreakSync)
		{
			BreakKey key = BreakIdentity(uid, pos);
			if (!recentBreaks.TryGetValue(key, out var value))
			{
				return false;
			}
			if (tickCount - value <= 1000)
			{
				return true;
			}
			recentBreaks.Remove(key);
			return false;
		}
	}

	private static void BeforeNativeBreak(BlockPos __0, IPlayer __1, out NativeBreakState? __state)
	{
		__state = null;
		OreShieldService oreShieldService = current;
		if (oreShieldService == null || !oreShieldService.active || oreShieldService.virtualizer == null || oreShieldService.server == null || !(__1 is IServerPlayer player) || __0 == null)
		{
			return;
		}
		try
		{
			int id = oreShieldService.server.BlockAccessor.GetBlock(__0, 1).Id;
			if (oreShieldService.palette != null && (uint)id < oreShieldService.palette.ProtectedOre.Length && oreShieldService.palette.ProtectedOre[id])
			{
				__state = new NativeBreakState(player, __0.Copy(), id);
			}
		}
		catch (Exception ex)
		{
			oreShieldService.ReportHookError("native ore-break capture", ex);
		}
	}

	private static Exception? AfterNativeBreak(NativeBreakState? __state, Exception? __exception)
	{
		OreShieldService oreShieldService = current;
		if (oreShieldService == null || !oreShieldService.active || oreShieldService.virtualizer == null || oreShieldService.server == null || __state == null)
		{
			return __exception;
		}
		try
		{
			string uid = __state.Player.PlayerUID ?? "";
			if (oreShieldService.WasBreakHandledRecently(uid, __state.Position) || oreShieldService.server.BlockAccessor.GetBlock(__state.Position, 1).Id == __state.OldBlockId)
			{
				return __exception;
			}
			oreShieldService.virtualizer.RevealAfterPlayerBreak(__state.Player, __state.Position, __state.OldBlockId);
			oreShieldService.MarkBreakHandled(uid, __state.Position);
		}
		catch (Exception ex)
		{
			oreShieldService.ReportHookError("native ore-break completion", ex);
		}
		return __exception;
	}

	private void OnBreakBlock(IServerPlayer byPlayer, BlockSelection blockSel, ref float dropQuantityMultiplier, ref EnumHandling handling)
	{
		if (!active || virtualizer == null || !config.OreShieldEnabled || byPlayer == null || blockSel?.Position == null || server == null)
		{
			return;
		}
		string uid = byPlayer.PlayerUID ?? "";
		if (uid.Length == 0)
		{
			return;
		}
		BlockPos pos = blockSel.Position.Copy();
		int beforeId;
		try
		{
			beforeId = server.BlockAccessor.GetBlock(pos, 1).Id;
		}
		catch
		{
			return;
		}
		api.Event.RegisterCallback((float _) =>
		{
			if (!active || virtualizer == null || server == null)
			{
				return;
			}
			try
			{
				int id = server.BlockAccessor.GetBlock(pos, 1).Id;
				if (id != beforeId && !WasBreakHandledRecently(uid, pos))
				{
					if (api.World.PlayerByUid(uid) is IServerPlayer player)
					{
						virtualizer.RevealAfterPlayerBreak(player, pos, beforeId);
						MarkBreakHandled(uid, pos);
					}
					else
					{
						virtualizer.SyncExposureAfterWorldChange(pos);
					}
				}
			}
			catch (Exception ex)
			{
				ReportHookError("pre-break fallback sync", ex);
			}
		}, 120);
	}

	private void OnDidBreakBlock(IServerPlayer byPlayer, int oldBlockId, BlockSelection blockSel)
	{
		if (!active || virtualizer == null || !config.OreShieldEnabled || byPlayer == null || blockSel?.Position == null)
		{
			return;
		}
		try
		{
			BlockPos blockPos = blockSel.Position.Copy();
			if (!WasBreakHandledRecently(byPlayer.PlayerUID ?? "", blockPos))
			{
				virtualizer.RevealAfterPlayerBreak(byPlayer, blockPos, oldBlockId);
				MarkBreakHandled(byPlayer.PlayerUID ?? string.Empty, blockPos);
			}
		}
		catch (Exception ex)
		{
			ReportHookError("post-break exposure sync", ex);
		}
	}

	private void OnDidPlaceBlock(IServerPlayer byPlayer, int oldBlockId, BlockSelection blockSel, ItemStack withItemStack)
	{
		if (!active || virtualizer == null || !config.OreShieldEnabled || blockSel?.Position == null)
		{
			return;
		}
		try
		{
			ClearBreakHandledAt(blockSel.Position);
			virtualizer.RevealAfterPlayerPlace(byPlayer, blockSel.Position.Copy());
		}
		catch (Exception ex)
		{
			ReportHookError("post-place exposure sync", ex);
		}
	}

	private void RememberClientUid(IServerPlayer? player)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || player.ClientId < 0)
		{
			return;
		}
		lock (clientSyncReadyLock)
		{
			clientUidById[player.ClientId] = player.PlayerUID;
		}
	}

	public OreShieldRuntimeState ExportRuntimeState()
	{
		OreShieldRuntimeState oreShieldRuntimeState = new OreShieldRuntimeState();
		string key;
		Dictionary<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells> value;
		if (virtualizer != null)
		{
			foreach (KeyValuePair<string, Dictionary<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells>> item2 in virtualizer.ExportCompactDiscoveryState())
			{
				item2.Deconstruct(out key, out value);
				string key2 = key;
				Dictionary<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells> value2 = value;
				oreShieldRuntimeState.Discoveries[key2] = value2;
			}
		}
		else if (restoredRuntimeState != null)
		{
			foreach (KeyValuePair<string, Dictionary<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells>> discovery in restoredRuntimeState.Discoveries)
			{
				discovery.Deconstruct(out key, out value);
				string key3 = key;
				Dictionary<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells> source = value;
				oreShieldRuntimeState.Discoveries[key3] = source.ToDictionary((KeyValuePair<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells> c) => c.Key, (KeyValuePair<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells> c) => c.Value.Clone());
			}
			oreShieldRuntimeState.SyncReadyPlayerUids.UnionWith(restoredRuntimeState.SyncReadyPlayerUids);
		}
		lock (clientSyncReadyLock)
		{
			foreach (string clientSyncReadyUid in clientSyncReadyUids)
			{
				oreShieldRuntimeState.SyncReadyPlayerUids.Add(clientSyncReadyUid);
			}
		}
		return oreShieldRuntimeState;
	}

	private void RestoreRuntimeState()
	{
		if (restoredRuntimeState == null || virtualizer == null)
		{
			return;
		}
		virtualizer.ImportCompactDiscoveryState(restoredRuntimeState.Discoveries);
		restoredRuntimeState.Discoveries.Clear();
		lock (clientSyncReadyLock)
		{
			IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
			foreach (IPlayer player in allOnlinePlayers)
			{
				if (player is IServerPlayer serverPlayer && !string.IsNullOrWhiteSpace(serverPlayer.PlayerUID) && restoredRuntimeState.SyncReadyPlayerUids.Contains(serverPlayer.PlayerUID))
				{
					clientSyncReadyUids.Add(serverPlayer.PlayerUID);
					if (serverPlayer.ClientId >= 0)
					{
						clientUidById[serverPlayer.ClientId] = serverPlayer.PlayerUID;
					}
				}
			}
		}
		restoredRuntimeState.SyncReadyPlayerUids.Clear();
	}

	private string? ResolvePlayerUidForChunkClient(ConnectedClient client)
	{
		if (client == null)
		{
			return null;
		}
		string text = client.Entityplayer?.PlayerUID ?? string.Empty;
		if (text.Length > 0)
		{
			lock (clientSyncReadyLock)
			{
				clientUidById[client.Id] = text;
			}
			return text;
		}
		IPlayer[] allOnlinePlayers = api.World.AllOnlinePlayers;
		foreach (IPlayer player in allOnlinePlayers)
		{
			if (player is IServerPlayer serverPlayer && serverPlayer.ClientId == client.Id && !string.IsNullOrWhiteSpace(serverPlayer.PlayerUID))
			{
				lock (clientSyncReadyLock)
				{
					clientUidById[client.Id] = serverPlayer.PlayerUID;
				}
				return serverPlayer.PlayerUID;
			}
		}
		lock (clientSyncReadyLock)
		{
			string value;
			return (clientUidById.TryGetValue(client.Id, out value) && !string.IsNullOrWhiteSpace(value)) ? value : null;
		}
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		try
		{
			virtualizer?.RemovePlayerConnectionState(player?.PlayerUID);
		}
		catch
		{
		}
		string uid = player?.PlayerUID ?? string.Empty;
		int num = player?.ClientId ?? (-1);
		if (uid.Length <= 0 && num < 0)
		{
			return;
		}
		lock (clientSyncReadyLock)
		{
			if (uid.Length > 0)
			{
				clientSyncReadyUids.Remove(uid);
				clientSyncRejectedLoggedUids.Remove(uid);
				int[] array = (from kv in clientUidById
					where string.Equals(kv.Value, uid, StringComparison.OrdinalIgnoreCase)
					select kv.Key).ToArray();
				foreach (int key in array)
				{
					clientUidById.Remove(key);
				}
			}
			if (num >= 0)
			{
				clientUidById.Remove(num);
			}
		}
	}

	private bool SupportsClientSync(IServerPlayer player)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return false;
		}
		lock (clientSyncReadyLock)
		{
			return clientSyncReadyUids.Contains(player.PlayerUID);
		}
	}

	private void OnClientSyncHello(IServerPlayer player, OreShieldSyncHelloPacket packet)
	{
		if (!active || virtualizer == null || player == null || packet == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		RememberClientUid(player);
		bool flag = packet.ProtocolVersion == 3 && string.Equals(packet.Capability, "sync-remesh-v3", StringComparison.Ordinal) && string.Equals(packet.BuildMarker, "ac1127-oreshield-v329-perf1", StringComparison.Ordinal);
		bool flag2 = false;
		bool flag3 = false;
		lock (clientSyncReadyLock)
		{
			clientSyncHelloPackets++;
			if (!flag)
			{
				clientSyncRejectedHellos++;
				clientSyncReadyUids.Remove(player.PlayerUID);
				flag3 = clientSyncRejectedLoggedUids.Add(player.PlayerUID);
			}
			else
			{
				clientSyncRejectedLoggedUids.Remove(player.PlayerUID);
				flag2 = clientSyncReadyUids.Add(player.PlayerUID);
			}
		}
		if (!flag)
		{
			if (flag3)
			{
				api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] Rejected stale/incompatible sync hello from {0}: protocol={1}, capability='{2}', build='{3}'. Required protocol={4}, capability='{5}', build='{6}'.", player.PlayerUID, packet.ProtocolVersion, packet.Capability ?? string.Empty, packet.BuildMarker ?? string.Empty, 3, "sync-remesh-v3", "ac1127-oreshield-v329-perf1");
			}
		}
		else if (flag2)
		{
			try
			{
				virtualizer.ResyncKnownDiscoveries(player);
			}
			catch (Exception ex)
			{
				ReportHookError("client sync ready resync", ex);
			}
		}
	}

	private void OnClientSyncAck(IServerPlayer player, OreShieldSyncAckPacket packet)
	{
		if (!active || virtualizer == null || player == null || packet == null || packet.ProtocolVersion != 3 || !SupportsClientSync(player))
		{
			return;
		}
		try
		{
			virtualizer.HandleClientSyncAck(player, packet);
		}
		catch (Exception ex)
		{
			ReportHookError("client sync ack", ex);
		}
	}

	private void OnClientSyncRequest(IServerPlayer player, OreShieldChunkSyncRequestPacket packet)
	{
		if (!active || virtualizer == null || player == null || packet == null || packet.ProtocolVersion != 3 || !SupportsClientSync(player))
		{
			return;
		}
		try
		{
			virtualizer.HandleClientSyncRequest(player, packet);
		}
		catch (Exception ex)
		{
			ReportHookError("client sync resync request", ex);
		}
	}

	private void ReportHookError(string stage, Exception ex)
	{
		hookErrors++;
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		status = $"Active with a recent {stage} degraded event: {ex.GetType().Name}.";
		if (num - lastHookErrorLogUtc >= 30000)
		{
			lastHookErrorLogUtc = num;
			api.Logger.Error("[ServerEssentials/AntiCheat/OreShield] {0} failed; Ore Shield kept the operation degraded/fail-closed where applicable. {1}: {2}", stage, ex.GetType().Name, ex.Message);
		}
	}

	private void StopInternal()
	{
		if (current == this)
		{
			current = null;
		}
		active = false;
		try
		{
			api.Event.BreakBlock -= OnBreakBlock;
		}
		catch
		{
		}
		try
		{
			api.Event.DidBreakBlock -= OnDidBreakBlock;
		}
		catch
		{
		}
		try
		{
			api.Event.DidPlaceBlock -= OnDidPlaceBlock;
		}
		catch
		{
		}
		try
		{
			api.Event.PlayerDisconnect -= OnPlayerDisconnect;
		}
		catch
		{
		}
		lock (recentBreakSync)
		{
			recentBreaks.Clear();
			recentBreakOrder.Clear();
		}
		lock (clientSyncReadyLock)
		{
			clientSyncReadyUids.Clear();
			clientSyncRejectedLoggedUids.Clear();
			clientUidById.Clear();
		}
		try
		{
			harmony?.UnpatchAll("newfies.serveressentials.anticheat.oreshield");
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] Failed to remove one or more Harmony hooks: {0}", ex.Message);
		}
		try
		{
			virtualizer?.Dispose();
		}
		catch
		{
		}
		virtualizer = null;
		getChunkRequestDimension = null;
		harmony = null;
		syncChannel = null;
		server = null;
	}

	public void Dispose()
	{
		status = "Stopped.";
		StopInternal();
	}
}
