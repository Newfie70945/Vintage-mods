using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Packets;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Owns Ore Shield's narrowly-scoped 1.22.7 network virtualization hooks.
/// Failure is isolated from the rest of Anti-Cheat: incompatible internals disable Ore Shield only.
/// </summary>
internal sealed class OreShieldRuntimeState
{
    public Dictionary<string, Dictionary<OreShieldBlockVirtualizer.ChunkKey, OreShieldDiscoveryCells>> Discoveries { get; } = new(StringComparer.Ordinal);
    public HashSet<string> SyncReadyPlayerUids { get; } = new(StringComparer.Ordinal);
}

internal sealed class OreShieldService : IDisposable
{
    private const string HarmonyId = "newfies.serveressentials.anticheat.oreshield";
    private readonly ICoreServerAPI api;
    private readonly AntiCheatConfig config;
    private readonly object clientSyncReadyLock = new();
    private readonly HashSet<string> clientSyncReadyUids = new(StringComparer.Ordinal);
    private readonly HashSet<string> clientSyncRejectedLoggedUids = new(StringComparer.Ordinal);
    // Stable bridge used by recipient chunk masking. The internal ConnectedClient object does not
    // reliably expose an IServerPlayer on 1.22.7, but its numeric Id is stable for the connection.
    // Learn the public PlayerUID from the OreShield hello/public player list and reuse it on later
    // chunk re-sends so already-discovered ore does not become concealed again after chunk unload.
    private readonly Dictionary<int, string> clientUidById = new();
    private readonly ConditionalWeakTable<Packet_ServerChunk, ChunkPacketMarker> processedChunkPackets = new();
    private readonly object processedChunkPacketsLock = new();
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
    private readonly object recentBreakSync = new();
    private readonly Dictionary<BreakKey, long> recentBreaks = new();
    private readonly Queue<(BreakKey Key, long Stamp)> recentBreakOrder = new();
    // Vintage Story keeps the chunk-load request dimension in an internal field. 1.22.7's
    // mainThreadLoadChunkColumn hook is the authoritative point where a generated/loaded column
    // becomes usable by the server, so OreShield uses the same runtime field accessor as the
    // validated ServerGuard-style hook rather than guessing from chunk coordinates.
    private AccessTools.FieldRef<ChunkColumnLoadRequest, int> getChunkRequestDimension = null!;
    private static OreShieldService? current;
    private readonly record struct BreakKey(string PlayerUid, int X, int InternalY, int Z);
    private sealed record NativeBreakState(IServerPlayer Player, BlockPos Position, int OldBlockId);
    private sealed record PlacementPatchState(BlockPos Position, bool AddedExemption);
    private sealed record ChunkPacketMarker(ConnectedClient Recipient, Packet_ServerChunk Canonical);
    private static readonly System.Func<object, object> ClonePacket = typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<System.Func<object, object>>();

    public OreShieldService(ICoreServerAPI api, AntiCheatConfig config, System.Func<IServerPlayer, bool>? supportsClientSync = null, OreShieldRuntimeState? restoredRuntimeState = null)
    {
        this.api = api;
        this.config = config;
        this.restoredRuntimeState = restoredRuntimeState;
    }


    /// <summary>
    /// Reads the version from the runtime-loaded Vintage Story assembly.
    /// Do not use GameVersion.ShortGameVersion directly here: it is a const string and
    /// C# will inline the version from whatever VintagestoryAPI.dll was referenced at build time.
    /// </summary>
    private static string ResolveRuntimeGameVersion()
    {
        try
        {
            FieldInfo? shortVersionField = typeof(GameVersion).GetField(
                nameof(GameVersion.ShortGameVersion),
                BindingFlags.Public | BindingFlags.Static);

            if (shortVersionField?.IsLiteral == true &&
                shortVersionField.GetRawConstantValue() is string runtimeVersion &&
                !string.IsNullOrWhiteSpace(runtimeVersion))
            {
                return runtimeVersion.Trim();
            }
        }
        catch
        {
            // Fall through to assembly-version detection.
        }

        try
        {
            Version? assemblyVersion = typeof(ServerMain).Assembly.GetName().Version;
            if (assemblyVersion != null && assemblyVersion.Major >= 0 && assemblyVersion.Minor >= 0 && assemblyVersion.Build >= 0)
            {
                return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
            }
        }
        catch
        {
            // Fall through to unknown.
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
            string gameVersion = ResolveRuntimeGameVersion();
            if (!string.Equals(gameVersion, "1.22.7", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Ore Shield packet hooks are validated for Vintage Story 1.22.7; runtime reports '{gameVersion}'.");

            if (api.World is not ServerMain sm)
                throw new NotSupportedException("Ore Shield requires the standard Vintage Story server runtime.");
            if (current != null && !ReferenceEquals(current, this))
                throw new InvalidOperationException("Another Newfie's Ore Shield instance is already active.");

            server = sm;

            syncChannel = api.Network.GetChannel(OreShieldSyncProtocol.ChannelName);
            if (syncChannel == null)
            {
                syncChannel = api.Network.RegisterChannel(OreShieldSyncProtocol.ChannelName)
                    .RegisterMessageType<OreShieldSyncBatchPacket>()
                    .RegisterMessageType<OreShieldSyncAckPacket>()
                    .RegisterMessageType<OreShieldChunkSyncRequestPacket>()
                    .RegisterMessageType<OreShieldSyncHelloPacket>();
            }
            syncChannel
                .SetMessageHandler<OreShieldSyncAckPacket>(OnClientSyncAck)
                .SetMessageHandler<OreShieldChunkSyncRequestPacket>(OnClientSyncRequest)
                .SetMessageHandler<OreShieldSyncHelloPacket>(OnClientSyncHello);

            palette = new OreShieldPalette(api.World, config);
            if (palette.ProtectedOreVariants == 0)
                throw new InvalidOperationException("No safe host-rock ore variants were resolved from the loaded block registry.");

            virtualizer = new OreShieldBlockVirtualizer(api, sm, config, palette, syncChannel, SupportsClientSync);
            RestoreRuntimeState();
            harmony = new Harmony(HarmonyId);
            getChunkRequestDimension = AccessTools.FieldRefAccess<ChunkColumnLoadRequest, int>("dimension");
            // V3 masks at the recipient-aware chunk-send stage instead of ServerChunk.ToPacket.
            // This is the first point where both the authoritative chunk and the receiving client
            // are known, allowing one-way per-player discovery without leaking cave ore globally.
            Type sendChunksType = typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemSendChunks", throwOnError: true)!;
            List<string> recipientChunkHooks = PatchRecipientChunkSendPaths(sendChunksType);
            if (recipientChunkHooks.Count <= 0)
                throw new MissingMethodException(sendChunksType.FullName, "recipient-aware Packet_ServerChunk send path");
            api.Logger.Notification("[ServerEssentials/AntiCheat/OreShield] Installed recipient chunk masking on {0} compatible send path(s).", recipientChunkHooks.Count);
            foreach (string signature in recipientChunkHooks)
                api.Logger.Notification("[ServerEssentials/AntiCheat/OreShield] Chunk mask hook: {0}", signature);

            Patch(typeof(ServerMain), "SendSetBlocksPacket",
                new[] { typeof(List<BlockPos>), typeof(int) },
                nameof(BeforeSetBlocksPacket), prefix: true, Priority.First);

            Patch(typeof(ServerMain), "SendSetBlock",
                new[] { typeof(IServerPlayer), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool) },
                nameof(BeforeSetBlock), prefix: true, Priority.First);

            // Mod perks such as XSkills call IBlockAccessor.BreakBlock directly. The native
            // accessor does not raise the player DidBreakBlock event for those recursive breaks.
            // Observe canonical server mutations rather than bypassing ingress/mining policy.
            PatchNativeBreak(server.BlockAccessor.GetType());

            // Register player-placed protected ore before the authoritative SetBlock path can emit
            // any outgoing block packet. DidPlaceBlock fires too late for that first packet on some
            // engine paths, so a prefix/postfix around the actual placement operation removes the
            // mask-then-correct race entirely.
            Type? blockSimulationType = typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemBlockSimulation", throwOnError: false);
            MethodInfo? playerPlacementMethod = blockSimulationType == null ? null : AccessTools.DeclaredMethod(
                blockSimulationType, "TryModifyBlockInWorld", new[] { typeof(ServerPlayer), typeof(Packet_ClientBlockPlaceOrBreak) });
            if (playerPlacementMethod != null)
            {
                harmony.Patch(playerPlacementMethod, prefix: new HarmonyMethod(typeof(OreShieldService), nameof(BeforeTryModifyBlockInWorld)) { priority = Priority.First });
                harmony.Patch(playerPlacementMethod, postfix: new HarmonyMethod(typeof(OreShieldService), nameof(AfterTryModifyBlockInWorld)) { priority = Priority.Last });
            }
            else
            {
                api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] Player-placement pre-exemption hook unavailable; retaining DidPlaceBlock correction fallback.");
            }

            // Hook the 1.22.7 column install completion so v3.2.9 can refresh exposed-candidate
            // indexes on touching chunk boundaries as soon as neighboring geometry becomes authoritative.
            Type supplyChunksType = typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemSupplyChunks", throwOnError: true)!;
            Patch(supplyChunksType, "mainThreadLoadChunkColumn",
                new[] { typeof(ChunkColumnLoadRequest) },
                nameof(AfterColumnLoaded), prefix: false, Priority.Last);

            current = this;
            active = true;
            api.Event.BreakBlock += OnBreakBlock;
            api.Event.DidBreakBlock += OnDidBreakBlock;
            api.Event.DidPlaceBlock += OnDidPlaceBlock;
            api.Event.PlayerDisconnect += OnPlayerDisconnect;
            // Ore Shield v3.2.9 uses bounded LOS discovery, but movement/view gating and the exposed-candidate
            // index prevent the old full-radius repair workload. Break/place events update only the
            // changed cell and six neighbors; whole-vein reveal remains optional and strict by family.
            status = $"Active v3.3.0 Perf4 ScopedMining1 RecipientFix1: {palette.ProtectedOreVariants} ore variants, {palette.HostVariants} host-rock variants; bounded server LOS discovery, Client Integrity sync v{OreShieldSyncProtocol.Version}, runtime-persistent discoveries, and fail-closed recipient masking; isolated recipient packets, compact discovery records, targeted invalidation, bounded revision epochs, deferred exposure geometry, coalesced mining updates and compact placed-ore save checkpoints.";
            message = status;
            api.Logger.Notification("[ServerEssentials/AntiCheat/OreShield] {0}", status);
            if (palette.SkippedOreCandidates > 0 && palette.SkippedOreCodes.Count > 0)
            {
                api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] {0} ore candidate(s) could not be safely virtualized. Sample: {1}",
                    palette.SkippedOreCandidates, string.Join(", ", palette.SkippedOreCodes));
            }
            return true;
        }
        catch (Exception ex)
        {
            status = $"Unavailable: {ex.GetType().Name}: {ex.Message}";
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
            OreShieldWebSnapshot snapshot = virtualizer.Snapshot(active, status);
            snapshot.HookErrors = hookErrors;
            lock (clientSyncReadyLock) snapshot.ClientSyncReadyClients = clientSyncReadyUids.Count;
            snapshot.ClientSyncHelloPackets = clientSyncHelloPackets;
            snapshot.ClientSyncRejectedHellos = clientSyncRejectedHellos;
            return snapshot;
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
            AdditionalOrePatterns = new List<string>(config.OreShieldAdditionalOrePatterns ?? new()),
            ExcludedBlockPatterns = new List<string>(config.OreShieldExcludedBlockPatterns ?? new()),
            ProtectedOreVariants = palette?.ProtectedOreVariants ?? 0,
            HostVariants = palette?.HostVariants ?? 0,
            SkippedOreCandidates = palette?.SkippedOreCandidates ?? 0,
            SyncProtocolVersion = OreShieldSyncProtocol.Version,
            SyncCapability = OreShieldSyncProtocol.Capability,
            SyncBuildMarker = OreShieldSyncProtocol.BuildMarker
        };
    }

    private void Patch(Type owner, string methodName, Type[] parameters, string handlerName, bool prefix, int priority)
    {
        MethodInfo original = AccessTools.DeclaredMethod(owner, methodName, parameters)
            ?? throw new MissingMethodException(owner.FullName, methodName);
        var hook = new HarmonyMethod(typeof(OreShieldService), handlerName) { priority = priority };
        harmony!.Patch(original, prefix: prefix ? hook : null, postfix: prefix ? null : hook);
    }

    private void PatchNativeBreak(Type accessorType)
    {
        Type[] parameters = { typeof(BlockPos), typeof(IPlayer), typeof(float) };
        MethodInfo? reflected = accessorType.GetMethod("BreakBlock", parameters);
        // Strict/relaxed accessors inherit this implementation. Harmony requires the
        // declared MethodInfo, not the inherited view reflected from the subclass.
        MethodInfo? implemented = reflected?.DeclaringType == null ? null :
            AccessTools.DeclaredMethod(reflected.DeclaringType, "BreakBlock", parameters);
        if (implemented == null || implemented.IsAbstract)
            throw new MissingMethodException(accessorType.FullName, "Implemented BreakBlock hook");
        harmony!.Patch(implemented,
            prefix: new HarmonyMethod(typeof(OreShieldService), nameof(BeforeNativeBreak)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(OreShieldService), nameof(AfterNativeBreak)) { priority = Priority.Last });
    }

    private List<string> PatchRecipientChunkSendPaths(Type sendChunksType)
    {
        MethodInfo postfix = AccessTools.DeclaredMethod(typeof(OreShieldService), nameof(AfterRecipientChunkDynamic))
            ?? throw new MissingMethodException(typeof(OreShieldService).FullName, nameof(AfterRecipientChunkDynamic));

        var patched = new List<string>();
        IEnumerable<MethodInfo> methods = sendChunksType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => !method.IsAbstract && !method.ContainsGenericParameters)
            .Where(method => typeof(Packet_ServerChunk).IsAssignableFrom(method.ReturnType))
            .Where(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Any(x => typeof(ServerChunk).IsAssignableFrom(x.ParameterType)) &&
                       parameters.Any(x => typeof(ConnectedClient).IsAssignableFrom(x.ParameterType));
            })
            .GroupBy(method => method.Module.ModuleVersionId.ToString("N") + ":" + method.MetadataToken)
            .Select(group => group.First());

        foreach (MethodInfo method in methods)
        {
            harmony!.Patch(method, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            patched.Add(FormatMethodSignature(method));
        }

        return patched;
    }

    private static string FormatMethodSignature(MethodInfo method)
    {
        string args = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
        return $"{method.DeclaringType?.FullName}.{method.Name}({args}) -> {method.ReturnType.Name}";
    }

    private Packet_ServerChunk GetRecipientChunkPacket(Packet_ServerChunk packet, ConnectedClient recipient, out bool alreadyProcessed)
    {
        lock (processedChunkPacketsLock)
        {
            if (processedChunkPackets.TryGetValue(packet, out ChunkPacketMarker? marker))
            {
                if (ReferenceEquals(marker.Recipient, recipient))
                {
                    alreadyProcessed = true;
                    return packet;
                }
                packet = marker.Canonical;
            }
            // collectChunk caches the vanilla packet for every recipient in this tick. Never
            // mutate that shared object or skip a different recipient's masking. Payload arrays
            // are immutable here: SetBlocks replaces the reference on this shallow copy only.
            var result = (Packet_ServerChunk)ClonePacket(packet);
            processedChunkPackets.Add(result, new ChunkPacketMarker(recipient, packet));
            alreadyProcessed = false;
            return result;
        }
    }

    private static void AfterRecipientChunkDynamic(object[] __args, ref Packet_ServerChunk? __result)
    {
        OreShieldService? service = current;
        if (service?.active != true || service.virtualizer == null || __args == null || __result == null) return;
        Packet_ServerChunk packet = __result;
        service.virtualizer.RecordChunkHookInvocation();

        ServerChunk? chunk = null;
        ConnectedClient? client = null;
        foreach (object? arg in __args)
        {
            if (chunk == null && arg is ServerChunk sc) chunk = sc;
            else if (client == null && arg is ConnectedClient cc) client = cc;
        }

        // Do not consume the packet marker until a compatible hook invocation actually provides
        // both authoritative chunk and recipient context. A wrapper with null/missing arguments
        // must not prevent a later valid wrapper from masking the same packet instance.
        if (chunk == null || client == null) return;
        try
        {
            packet = service.GetRecipientChunkPacket(packet, client, out bool alreadyProcessed);
            __result = packet;
            if (alreadyProcessed) return;
            service.virtualizer.RecordUniqueChunkPacket();
            string? playerUid = service.ResolvePlayerUidForChunkClient(client);
            service.virtualizer.MaskChunkForPlayerUid(playerUid, chunk, packet);
        }
        catch (Exception ex)
        {
            // Never allow a masking exception to fall through to the original ore-bearing payload.
            // The fail-closed packet is intentionally visually degraded, but contains no block IDs.
            try { service.virtualizer.FailClosedChunkPacket(service.ResolvePlayerUidForChunkClient(client), chunk, packet, "mask exception"); }
            catch (Exception failClosedEx)
            {
                // Last-resort security guard: if even the authoritative fail-closed rebuild cannot
                // complete, mark the packet empty rather than returning the original ore-bearing data.
                try
                {
                    packet.Empty = 1;
                    packet.Compver = 2;
                    packet.SetBlocks(Array.Empty<byte>());
                }
                catch { }
                service.ReportHookError("recipient chunk fail-closed substitution", failClosedEx);
            }
            service.ReportHookError("recipient chunk packet", ex);
        }
    }

    private static void AfterColumnLoaded(ChunkColumnLoadRequest chunkRequest)
    {
        OreShieldService? service = current;
        if (service?.active != true || service.virtualizer == null || chunkRequest == null) return;

        try
        {
            int dimension = service.getChunkRequestDimension(chunkRequest);

            // This hook refreshes boundary exposure-index state; it does not mutate world blocks or
            // resend whole chunks. Any legitimate discovery still requires the normal per-player LOS rule.
            service.virtualizer.OnColumnLoaded(chunkRequest, dimension);
        }
        catch (Exception ex)
        {
            service.ReportHookError("chunk-load reveal refresh", ex);
        }
    }

    private static bool BeforeSetBlocksPacket(List<BlockPos> positions, int packetId)
    {
        OreShieldService? service = current;
        if (service?.active != true || service.virtualizer == null || !service.config.OreShieldEnabled) return true;
        try
        {
            // Keep ordinary changed blocks entirely on Vintage Story's native SetBlocks path.
            // OreShield removes only coordinates that are themselves maskable natural ore; those
            // protected coordinates are delivered recipient-by-recipient over the dedicated Client
            // Integrity sync channel, while the remaining positions serialize normally.
            return service.virtualizer.FilterBulkProtectedUpdates(positions, packetId);
        }
        catch (Exception ex)
        {
            service.ReportHookError("bulk block update", ex);
            try
            {
                // Strip protected natural ore from the damaged bulk packet. Ordinary coordinates
                // can still use the vanilla path; if this safety pass itself fails, drop the packet.
                return service.virtualizer.FailClosedBulkProtectedUpdates(positions);
            }
            catch (Exception safetyEx)
            {
                service.ReportHookError("bulk block update fail-closed filter", safetyEx);
                return false;
            }
        }
    }

    private static BlockPos ResolveHookBlockPos(IServerPlayer? player, int x, int y, int z)
    {
        // BlockPos(x,y,z) is dimension-aware when y already contains InternalY. Some engine
        // hooks can expose local-Y coordinates, so use the recipient/player dimension only as a
        // fallback when no dimension was embedded in y. This keeps overworld behavior unchanged
        // while making the hook safe in non-zero dimensions.
        var pos = new BlockPos(x, y, z);
        if (pos.dimension == 0 && y >= 0 && y < BlockPos.DimensionBoundary && player?.Entity != null)
        {
            EntityPos? entityPos = EntityRuntimeCompat.GetServerPos(player.Entity) ?? EntityRuntimeCompat.GetPos(player.Entity);
            if (entityPos != null && entityPos.Dimension != 0)
                pos = new BlockPos(x, y, z, entityPos.Dimension);
        }
        return pos;
    }

    private static void BeforeSetBlock(IServerPlayer player, ref int blockId, int posX, int posY, int posZ)
    {
        OreShieldService? service = current;
        if (service?.active != true || service.virtualizer == null || !service.config.OreShieldEnabled) return;
        try
        {
            // Do not touch normal placements/updates unless they are ore themselves or directly
            // affect the visibility of protected ore. This is both cheaper and avoids client
            // desync risk for inventories, backpacks and other block-entity-heavy content.
            BlockPos pos = ResolveHookBlockPos(player, posX, posY, posZ);
            if (!service.virtualizer.RequiresSingleVirtualization(blockId, pos)) return;
            blockId = service.virtualizer.MaskSingle(player, blockId, pos);
        }
        catch (Exception ex)
        {
            try
            {
                BlockPos pos = ResolveHookBlockPos(player, posX, posY, posZ);
                if (service.virtualizer.FailClosedSingleIfProtected(blockId, pos)) blockId = 0;
            }
            catch
            {
                if (service.virtualizer.IsProtectedOreIdForSafety(blockId)) blockId = 0;
            }
            service.ReportHookError("single block update", ex);
        }
    }

    private static void BeforeTryModifyBlockInWorld(ServerPlayer player, Packet_ClientBlockPlaceOrBreak cmd, out PlacementPatchState? __state)
    {
        __state = null;
        OreShieldService? service = current;
        if (service?.active != true || service.virtualizer == null || !service.config.OreShieldEnabled ||
            player == null || cmd == null || cmd.Mode != 1) return;

        try
        {
            ItemStack? stack = player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (stack?.Collectible is not Block block) return;
            BlockPos pos = ResolveHookBlockPos(player, cmd.X, cmd.Y, cmd.Z);
            service.ClearBreakHandledAt(pos);
            bool added = service.virtualizer.BeginPlayerOrePlacement(pos, block.Id);
            if (added || service.virtualizer.IsPlayerPlacedOreExempt(pos))
                __state = new PlacementPatchState(pos, added);
        }
        catch (Exception ex) { service.ReportHookError("pre-place ore exemption", ex); }
    }

    private static void AfterTryModifyBlockInWorld(bool __result, PlacementPatchState? __state)
    {
        OreShieldService? service = current;
        if (service?.active != true || service.virtualizer == null || __state == null) return;
        try { service.virtualizer.CompletePlayerOrePlacement(__state.Position, __state.AddedExemption, __result); }
        catch (Exception ex) { service.ReportHookError("post-place ore exemption", ex); }
    }

    private BreakKey BreakIdentity(string uid, BlockPos pos)
        => new(uid, pos.X, pos.InternalY, pos.Z);

    private void MarkBreakHandled(string uid, BlockPos pos)
    {
        long now = Environment.TickCount64;
        lock (recentBreakSync)
        {
            BreakKey key = BreakIdentity(uid, pos);
            recentBreaks[key] = now; recentBreakOrder.Enqueue((key, now));
            while (recentBreakOrder.Count > 4096 || recentBreakOrder.Count > 0 && now - recentBreakOrder.Peek().Stamp > 2000)
            {
                var oldest = recentBreakOrder.Dequeue();
                if (recentBreaks.TryGetValue(oldest.Key, out long stamp) && stamp == oldest.Stamp) recentBreaks.Remove(oldest.Key);
            }
        }
    }
    private void ClearBreakHandledAt(BlockPos pos)
    {
        lock (recentBreakSync)
            foreach (var key in recentBreaks.Keys.Where(k => k.X == pos.X && k.InternalY == pos.InternalY && k.Z == pos.Z).ToArray())
                recentBreaks.Remove(key);
    }

    private bool WasBreakHandledRecently(string uid, BlockPos pos)
    {
        long now = Environment.TickCount64;
        lock (recentBreakSync)
        {
            BreakKey key = BreakIdentity(uid, pos);
            if (!recentBreaks.TryGetValue(key, out long handledAt)) return false;
            if (now - handledAt <= 1_000) return true;
            recentBreaks.Remove(key);
            return false;
        }
    }

    private static void BeforeNativeBreak(BlockPos __0, IPlayer __1, out NativeBreakState? __state)
    {
        __state = null;
        var service = current;
        if (service?.active != true || service.virtualizer == null || service.server == null ||
            __1 is not IServerPlayer player || __0 == null) return;
        try
        {
            int before = service.server.BlockAccessor.GetBlock(__0, BlockLayersAccess.Solid).Id;
            if (service.palette != null && (uint)before < service.palette.ProtectedOre.Length && service.palette.ProtectedOre[before])
                __state = new NativeBreakState(player, __0.Copy(), before);
        }
        catch (Exception ex) { service.ReportHookError("native ore-break capture", ex); }
    }
    private static Exception? AfterNativeBreak(NativeBreakState? __state, Exception? __exception)
    {
        var service = current;
        if (service?.active != true || service.virtualizer == null || service.server == null || __state == null) return __exception;
        try
        {
            string uid = __state.Player.PlayerUID ?? "";
            if (service.WasBreakHandledRecently(uid, __state.Position) ||
                service.server.BlockAccessor.GetBlock(__state.Position, BlockLayersAccess.Solid).Id == __state.OldBlockId) return __exception;
            service.virtualizer.RevealAfterPlayerBreak(__state.Player, __state.Position, __state.OldBlockId);
            service.MarkBreakHandled(uid, __state.Position);
        }
        catch (Exception ex) { service.ReportHookError("native ore-break completion", ex); }
        return __exception;
    }

    private void OnBreakBlock(IServerPlayer byPlayer, BlockSelection blockSel, ref float dropQuantityMultiplier, ref EnumHandling handling)
    {
        if (!active || virtualizer == null || !config.OreShieldEnabled ||
            byPlayer == null || blockSel?.Position == null || server == null) return;

        // DidBreakBlock is the primary path, but some modded block behaviors can bypass or reorder
        // the normal post-break event. Capture the authoritative target here and perform one delayed
        // verification. If the block did not actually change, the fallback does nothing.
        string uid = byPlayer.PlayerUID ?? "";
        if (uid.Length == 0) return;

        BlockPos pos = blockSel.Position.Copy();
        int beforeId;
        try { beforeId = server.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid).Id; }
        catch { return; }

        api.Event.RegisterCallback(_ =>
        {
            if (!active || virtualizer == null || server == null) return;
            try
            {
                int afterId = server.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid).Id;
                if (afterId == beforeId || WasBreakHandledRecently(uid, pos)) return;
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
            catch (Exception ex) { ReportHookError("pre-break fallback sync", ex); }
        }, 120);
    }

    private void OnDidBreakBlock(IServerPlayer byPlayer, int oldBlockId, BlockSelection blockSel)
    {
        if (!active || virtualizer == null || !config.OreShieldEnabled ||
            byPlayer == null || blockSel?.Position == null) return;

        try
        {
            // The public API guarantees DidBreakBlock runs after the block is already broken, so
            // exposure tests here see authoritative post-break world state.
            BlockPos pos = blockSel.Position.Copy();
            if (WasBreakHandledRecently(byPlayer.PlayerUID ?? "", pos)) return;
            virtualizer.RevealAfterPlayerBreak(byPlayer, pos, oldBlockId);
            MarkBreakHandled(byPlayer.PlayerUID ?? string.Empty, pos);
        }
        catch (Exception ex) { ReportHookError("post-break exposure sync", ex); }
    }

    private void OnDidPlaceBlock(IServerPlayer byPlayer, int oldBlockId, BlockSelection blockSel, ItemStack withItemStack)
    {
        if (!active || virtualizer == null || !config.OreShieldEnabled ||
            blockSel?.Position == null) return;

        try { ClearBreakHandledAt(blockSel.Position); virtualizer.RevealAfterPlayerPlace(byPlayer, blockSel.Position.Copy()); }
        catch (Exception ex) { ReportHookError("post-place exposure sync", ex); }
    }

    private void RememberClientUid(IServerPlayer? player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || player.ClientId < 0) return;
        lock (clientSyncReadyLock) clientUidById[player.ClientId] = player.PlayerUID;
    }

    public OreShieldRuntimeState ExportRuntimeState()
    {
        var state = new OreShieldRuntimeState();
        if (virtualizer != null)
        {
            foreach (var (uid, chunks) in virtualizer.ExportCompactDiscoveryState())
                state.Discoveries[uid] = chunks;
        }
        else if (restoredRuntimeState != null)
        {
            // A failed runtime restart must not erase the discoveries we were trying to restore.
            foreach (var (uid, chunks) in restoredRuntimeState.Discoveries)
                state.Discoveries[uid] = chunks.ToDictionary(c => c.Key, c => c.Value.Clone());
            state.SyncReadyPlayerUids.UnionWith(restoredRuntimeState.SyncReadyPlayerUids);
        }

        lock (clientSyncReadyLock)
        {
            foreach (string uid in clientSyncReadyUids)
                state.SyncReadyPlayerUids.Add(uid);
        }
        return state;
    }

    private void RestoreRuntimeState()
    {
        if (restoredRuntimeState == null || virtualizer == null) return;
        virtualizer.ImportCompactDiscoveryState(restoredRuntimeState.Discoveries);
        restoredRuntimeState.Discoveries.Clear(); // release the transfer copy after successful ownership cloning

        lock (clientSyncReadyLock)
        {
            foreach (IPlayer raw in api.World.AllOnlinePlayers)
            {
                if (raw is not IServerPlayer online || string.IsNullOrWhiteSpace(online.PlayerUID)) continue;
                if (!restoredRuntimeState.SyncReadyPlayerUids.Contains(online.PlayerUID)) continue;
                clientSyncReadyUids.Add(online.PlayerUID);
                if (online.ClientId >= 0) clientUidById[online.ClientId] = online.PlayerUID;
            }
        }
        restoredRuntimeState.SyncReadyPlayerUids.Clear();
    }

    private string? ResolvePlayerUidForChunkClient(ConnectedClient client)
    {
        if (client == null) return null;

        string entityUid = client.Entityplayer?.PlayerUID ?? string.Empty;
        if (entityUid.Length > 0)
        {
            lock (clientSyncReadyLock) clientUidById[client.Id] = entityUid;
            return entityUid;
        }

        foreach (IPlayer raw in api.World.AllOnlinePlayers)
        {
            if (raw is not IServerPlayer online || online.ClientId != client.Id || string.IsNullOrWhiteSpace(online.PlayerUID)) continue;
            lock (clientSyncReadyLock) clientUidById[client.Id] = online.PlayerUID;
            return online.PlayerUID;
        }

        lock (clientSyncReadyLock)
            return clientUidById.TryGetValue(client.Id, out string? rememberedUid) && !string.IsNullOrWhiteSpace(rememberedUid)
                ? rememberedUid
                : null;
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        // Preserve legitimate discoveries for the lifetime of this OreShield runtime. A reconnect
        // will announce sync readiness again and ResyncKnownDiscoveries() will restore the view.
        try { virtualizer?.RemovePlayerConnectionState(player?.PlayerUID); }
        catch { }
        string uid = player?.PlayerUID ?? string.Empty;
        int clientId = player?.ClientId ?? -1;
        if (uid.Length > 0 || clientId >= 0)
        {
            lock (clientSyncReadyLock)
            {
                if (uid.Length > 0)
                {
                    clientSyncReadyUids.Remove(uid);
                    clientSyncRejectedLoggedUids.Remove(uid);
                    foreach (int id in clientUidById.Where(kv => string.Equals(kv.Value, uid, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToArray())
                        clientUidById.Remove(id);
                }
                if (clientId >= 0) clientUidById.Remove(clientId);
            }
        }
    }

    private bool SupportsClientSync(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return false;
        lock (clientSyncReadyLock)
        {
            return clientSyncReadyUids.Contains(player.PlayerUID);
        }
    }

    private void OnClientSyncHello(IServerPlayer player, OreShieldSyncHelloPacket packet)
    {
        if (!active || virtualizer == null || player == null || packet == null ||
            string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        RememberClientUid(player);

        bool compatible = packet.ProtocolVersion == OreShieldSyncProtocol.Version &&
            string.Equals(packet.Capability, OreShieldSyncProtocol.Capability, StringComparison.Ordinal) &&
            string.Equals(packet.BuildMarker, OreShieldSyncProtocol.BuildMarker, StringComparison.Ordinal);

        bool newlyReady = false;
        bool shouldLogReject = false;
        lock (clientSyncReadyLock)
        {
            clientSyncHelloPackets++;
            if (!compatible)
            {
                clientSyncRejectedHellos++;
                clientSyncReadyUids.Remove(player.PlayerUID);
                shouldLogReject = clientSyncRejectedLoggedUids.Add(player.PlayerUID);
            }
            else
            {
                clientSyncRejectedLoggedUids.Remove(player.PlayerUID);
                newlyReady = clientSyncReadyUids.Add(player.PlayerUID);
            }
        }

        if (!compatible)
        {
            if (shouldLogReject)
            {
                api.Logger.Warning(
                    "[ServerEssentials/AntiCheat/OreShield] Rejected stale/incompatible sync hello from {0}: protocol={1}, capability='{2}', build='{3}'. Required protocol={4}, capability='{5}', build='{6}'.",
                    player.PlayerUID, packet.ProtocolVersion, packet.Capability ?? string.Empty, packet.BuildMarker ?? string.Empty,
                    OreShieldSyncProtocol.Version, OreShieldSyncProtocol.Capability, OreShieldSyncProtocol.BuildMarker);
            }
            return;
        }

        if (!newlyReady) return;
        try
        {
            // Recover discoveries made before the dedicated sync channel announced readiness, and
            // recover automatically after an OreShield runtime/server-side reload.
            virtualizer.ResyncKnownDiscoveries(player);
        }
        catch (Exception ex) { ReportHookError("client sync ready resync", ex); }
    }

    private void OnClientSyncAck(IServerPlayer player, OreShieldSyncAckPacket packet)
    {
        if (!active || virtualizer == null || player == null || packet == null ||
            packet.ProtocolVersion != OreShieldSyncProtocol.Version || !SupportsClientSync(player)) return;
        try { virtualizer.HandleClientSyncAck(player, packet); }
        catch (Exception ex) { ReportHookError("client sync ack", ex); }
    }

    private void OnClientSyncRequest(IServerPlayer player, OreShieldChunkSyncRequestPacket packet)
    {
        if (!active || virtualizer == null || player == null || packet == null ||
            packet.ProtocolVersion != OreShieldSyncProtocol.Version || !SupportsClientSync(player)) return;
        try { virtualizer.HandleClientSyncRequest(player, packet); }
        catch (Exception ex) { ReportHookError("client sync resync request", ex); }
    }

    private void ReportHookError(string stage, Exception ex)
    {
        hookErrors++;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        status = $"Active with a recent {stage} degraded event: {ex.GetType().Name}.";
        if (now - lastHookErrorLogUtc < 30_000) return;
        lastHookErrorLogUtc = now;
        api.Logger.Error("[ServerEssentials/AntiCheat/OreShield] {0} failed; Ore Shield kept the operation degraded/fail-closed where applicable. {1}: {2}", stage, ex.GetType().Name, ex.Message);
    }

    private void StopInternal()
    {
        if (ReferenceEquals(current, this)) current = null;
        active = false;

        try { api.Event.BreakBlock -= OnBreakBlock; } catch { }
        try { api.Event.DidBreakBlock -= OnDidBreakBlock; } catch { }
        try { api.Event.DidPlaceBlock -= OnDidPlaceBlock; } catch { }
        try { api.Event.PlayerDisconnect -= OnPlayerDisconnect; } catch { }
        lock (recentBreakSync) { recentBreaks.Clear(); recentBreakOrder.Clear(); }
        lock (clientSyncReadyLock)
        {
            clientSyncReadyUids.Clear();
            clientSyncRejectedLoggedUids.Clear();
            clientUidById.Clear();
        }

        try
        {
            harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/AntiCheat/OreShield] Failed to remove one or more Harmony hooks: {0}", ex.Message);
        }

        try { virtualizer?.Dispose(); } catch { }
        virtualizer = null;
        getChunkRequestDimension = null!;
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
