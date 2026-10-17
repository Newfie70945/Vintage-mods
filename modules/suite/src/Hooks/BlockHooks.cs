using System;
using System.Collections.Generic;
using System.Threading;
using BlockLogger.Config;
using BlockLogger.Models;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Hooks;

public sealed class BlockHooks
{
    private readonly ICoreServerAPI api;
    private readonly BlockLoggerConfig config;
    private readonly LogStorageService queue;
    private readonly IngameTimeFormatter timeFormatter;
    private readonly Action<int, int, int>? terrainDirtyMarker;
    private readonly string worldId;
    private readonly Dictionary<int, string> blockCodeCache = new();
    private long hookFailures;
    private long lastHookWarningTicks;

    public BlockHooks(
        ICoreServerAPI api,
        BlockLoggerConfig config,
        LogStorageService queue,
        IngameTimeFormatter timeFormatter,
        Action<int, int, int>? terrainDirtyMarker = null)
    {
        this.api = api;
        this.config = config;
        this.queue = queue;
        this.timeFormatter = timeFormatter;
        this.terrainDirtyMarker = terrainDirtyMarker;
        worldId = api.World.SavegameIdentifier ?? "unknownworld";
    }

    public void Register()
    {
        api.Event.DidBreakBlock += OnDidBreakBlock;
        api.Event.DidPlaceBlock += OnDidPlaceBlock;
        api.Event.DidUseBlock += OnDidUseBlock;
    }

    public void Unregister()
    {
        api.Event.DidBreakBlock -= OnDidBreakBlock;
        api.Event.DidPlaceBlock -= OnDidPlaceBlock;
        api.Event.DidUseBlock -= OnDidUseBlock;
    }

    private void OnDidBreakBlock(IServerPlayer byPlayer, int oldBlockId, BlockSelection blockSel)
    {
        try
        {
            if (!config.Enabled || !config.LogBlockBreak || blockSel?.Position == null) return;
            var oldCode = GetBlockCode(oldBlockId);
            Queue(LogActionType.BlockBreak, byPlayer, blockSel.Position, oldCode, null, oldCode, GetBlockCode(0));
            MarkTerrainDirty(blockSel.Position);
        }
        catch (Exception ex) { ReportHookFailure("DidBreakBlock", ex); }
    }

    private void OnDidPlaceBlock(IServerPlayer byPlayer, int oldBlockId, BlockSelection blockSel, ItemStack withItemStack)
    {
        try
        {
            if (!config.Enabled || !config.LogBlockPlace || blockSel?.Position == null) return;
            var block = api.World.BlockAccessor.GetBlock(blockSel.Position);
            var newCode = RememberBlockCode(block);
            Queue(LogActionType.BlockPlace, byPlayer, blockSel.Position, newCode, withItemStack, GetBlockCode(oldBlockId), newCode);
            MarkTerrainDirty(blockSel.Position);
        }
        catch (Exception ex) { ReportHookFailure("DidPlaceBlock", ex); }
    }

    private void OnDidUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        try
        {
            if (!config.Enabled || !config.LogBlockUse || blockSel?.Position == null) return;
            var block = api.World.BlockAccessor.GetBlock(blockSel.Position);
            var code = RememberBlockCode(block);
            Queue(LogActionType.BlockUse, byPlayer, blockSel.Position, code, byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack, code, code);
        }
        catch (Exception ex) { ReportHookFailure("DidUseBlock", ex); }
    }

    private void ReportHookFailure(string hookName, Exception ex)
    {
        Interlocked.Increment(ref hookFailures);
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref lastHookWarningTicks);
        if (now - previous < TimeSpan.FromSeconds(30).Ticks) return;
        if (Interlocked.CompareExchange(ref lastHookWarningTicks, now, previous) != previous) return;
        api.Logger.Warning("[BlockLogger] Block hook {0} failed; record skipped. Failures={1}. {2}: {3}", hookName, Interlocked.Read(ref hookFailures), ex.GetType().Name, ex.Message);
    }

    private void Queue(LogActionType action, IServerPlayer? byPlayer, BlockPos pos, string blockCode, ItemStack? itemstack, string oldCode, string newCode)
    {
        if (ShouldIgnore(blockCode)) return;

        var heldItem = itemstack?.Collectible?.Code?.ToShortString();
        var heldSize = itemstack?.StackSize.ToString();

        queue.TryEnqueueFast(new QueuedEventPayload(
            DateTime.UtcNow, timeFormatter.GetCurrentStamp(), action,
            byPlayer?.PlayerUID ?? string.Empty, byPlayer?.PlayerName ?? string.Empty,
            worldId, ResolveDimension(pos), pos.X, pos.Y, pos.Z, blockCode,
            heldItem == null ? null : "heldItem", heldItem,
            heldSize == null ? null : "heldStackSize", heldSize,
            "oldBlockCode", oldCode,
            "newBlockCode", newCode));
    }


    private void MarkTerrainDirty(BlockPos pos)
    {
        if (terrainDirtyMarker == null) return;
        try { terrainDirtyMarker(pos.dimension, pos.X, pos.Z); }
        catch (Exception ex) { ReportHookFailure("WebMapDirtyJournal", ex); }
    }

    private string RememberBlockCode(Block? block)
    {
        if (block == null) return "unknown";
        var code = block.Code?.ToShortString() ?? "unknown";
        if (block.BlockId >= 0) blockCodeCache[block.BlockId] = code;
        return code;
    }

    private string GetBlockCode(int blockId)
    {
        if (blockCodeCache.TryGetValue(blockId, out var cached)) return cached;
        var block = api.World.BlockAccessor.GetBlock(blockId);
        return RememberBlockCode(block);
    }

    private bool ShouldIgnore(string blockCode)
    {
        foreach (var prefix in config.IgnoredBlockCodePrefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix) && blockCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveDimension(BlockPos pos) => pos.dimension.ToString();
}
