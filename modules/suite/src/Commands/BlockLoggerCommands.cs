using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BlockLogger.Config;
using BlockLogger.Models;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Commands;

public sealed class BlockLoggerCommands
{
    private const string PrivilegeCode = "blocklogger";
    private readonly ICoreServerAPI api;
    private readonly System.Func<BlockLoggerConfig> getConfig;
    private readonly Action reloadConfig;
    private readonly LogStorageService storage;
    private readonly LogQueryService queryService;
    private readonly ContainerSnapshotQueryService snapshotQueryService;
    private readonly ConcurrentDictionary<string, byte> activeQueries = new();

    private readonly record struct DisplayContext(int ViewerX, int ViewerZ, string Mode, int OriginX, int OriginZ);

    public BlockLoggerCommands(
        ICoreServerAPI api,
        System.Func<BlockLoggerConfig> getConfig,
        Action reloadConfig,
        LogStorageService storage,
        LogQueryService queryService,
        ContainerSnapshotQueryService snapshotQueryService)
    {
        this.api = api;
        this.getConfig = getConfig;
        this.reloadConfig = reloadConfig;
        this.storage = storage;
        this.queryService = queryService;
        this.snapshotQueryService = snapshotQueryService;
    }

    public void Register()
    {
        api.Permissions.RegisterPrivilege(PrivilegeCode, "Use BlockLogger investigation commands", true);
#pragma warning disable CS0618
        api.RegisterCommand("blocklogger", "Manage BlockLogger", "[status|reloadconfig|path|count]", OnAdminCommand, PrivilegeCode);
        api.RegisterCommand("blocklog", "Inspect logged block and container activity", "(help|up|down) (-r R) (-n N) (-s text) (-t tag)", OnBlockLogCommand, PrivilegeCode);
        api.RegisterCommand("entitylog", "Inspect logged entity activity", "(-r R) (-n N) (-s text) (-t tag)", OnEntityLogCommand, PrivilegeCode);
        api.RegisterCommand("blockloge", "Alias for exact entity lookup", "(-n N) (-s text) (-t tag)", OnEntityLogExactAliasCommand, PrivilegeCode);
#pragma warning restore CS0618
    }

    private void OnAdminCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cmd = args.PopWord()?.ToLowerInvariant();
        switch (cmd)
        {
            case "reload":
            case "reloadconfig":
                reloadConfig();
                player.SendMessage(groupId, "BlockLogger config reloaded.", EnumChatType.CommandSuccess);
                return;

            case "status":
                var c = getConfig();
                player.SendMessage(groupId,
                    $"Enabled={c.Enabled}, Storage=SQLite/WAL, Indexed={storage.IsReady}, " +
                    $"Pending={storage.PendingEvents} (critical={storage.PendingCriticalEvents}, entity={storage.PendingBulkEntityEvents}, overflow={storage.PendingOverflowEvents}/{storage.EmergencyOverflowCapacity}), " +
                    $"PeakPending={storage.PeakPendingEvents}, OverflowAccepted={storage.OverflowAcceptedEvents}, OverflowRejected={storage.OverflowRejectedEvents}, Dropped={storage.DroppedEvents} (entity={storage.DroppedEntityEvents}), " +
                    $"Written={storage.WrittenEvents:N0} (exactEntity={storage.WrittenEntityEvents:N0}, aggregates={storage.WrittenEntityAggregates:N0}, aggregatePending={storage.PendingEntityAggregates}, aggregatePeak={storage.PeakPendingEntityAggregates}, aggregateDropped={storage.DroppedEntityAggregates}), " +
                    $"LastBatch={storage.LastBatchRows} rows (entity={storage.LastBatchEntityRows})/{storage.LastBatchElapsedMs}ms, SnapshotsPending={storage.PendingSnapshots}, " +
                    $"RetentionDays={c.RetentionDays}, EntityAggregate={c.AggregateEntitySpawnDespawn}/{c.EntityAggregateWindowMs}-{c.EntityAggregateMaxWindowMs}ms adaptive={c.AdaptiveEntityAggregation} region={c.EntityAggregateRegionSize}, CoordMode={CoordinateDisplayService.GetModeLabel(c.CoordinateDisplayMode)}",
                    EnumChatType.CommandSuccess);
                return;

            case "path":
                player.SendMessage(groupId, $"Database: {storage.DatabasePath}", EnumChatType.CommandSuccess);
                player.SendMessage(groupId, $"Legacy event logs: {queryService.GetLogDirectoryPath()}", EnumChatType.CommandSuccess);
                player.SendMessage(groupId, $"Legacy snapshots: {snapshotQueryService.GetLogDirectoryPath()}", EnumChatType.CommandSuccess);
                return;

            case "count":
                _ = SendCountsAsync(player, groupId);
                return;

            default:
                player.SendMessage(groupId, "/blocklogger [status|reloadconfig|path|count]", EnumChatType.CommandError);
                return;
        }
    }

    private async System.Threading.Tasks.Task SendCountsAsync(IServerPlayer player, int groupId)
    {
        try
        {
            player.SendMessage(groupId, "BlockLogger count query queued...", EnumChatType.CommandSuccess);
            var events = await queryService.CountAsync().ConfigureAwait(false);
            var snapshots = await snapshotQueryService.CountAsync().ConfigureAwait(false);
            api.Event.EnqueueMainThreadTask(() =>
            {
                player.SendMessage(groupId, $"Indexed event records: {events:N0}", EnumChatType.CommandSuccess);
                player.SendMessage(groupId, $"Indexed container snapshots: {snapshots:N0}", EnumChatType.CommandSuccess);
            }, "blocklogger-count-result");
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[BlockLogger] Count query failed: {0}", ex.Message);
            api.Event.EnqueueMainThreadTask(() =>
                player.SendMessage(groupId, "Count query failed. Check the server log.", EnumChatType.CommandError),
                "blocklogger-count-error");
        }
    }

    private void OnBlockLogCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        var config = getConfig();
        var parsed = ParseCommonArgs(args, config.DefaultRadius, config.DefaultDisplayCount);
        if (parsed.ShowHelp) { SendBlockHelp(player, groupId); return; }

        var sel = player.CurrentBlockSelection;
        if (sel?.Position == null)
        {
            player.SendMessage(groupId, "Look at a block first.", EnumChatType.CommandError);
            return;
        }

        var target = sel.Position.Copy();
        target.Y += parsed.VerticalOffset;
        parsed.Radius = Math.Clamp(parsed.Radius, 0, config.MaxRadius);
        parsed.Count = Math.Clamp(parsed.Count, 1, Math.Min(config.MaxDisplayCount, 200));

        if (!TryStartQuery(player, groupId, out var key)) return;

        player.SendMessage(groupId, $"BlockLogger search queued (radius={parsed.Radius}, max={parsed.Count}).", EnumChatType.CommandSuccess);
        _ = RunBlockQuery(player, groupId, key, target.X, target.Y, target.Z, target.dimension, parsed, CreateDisplayContext(player));
    }

    private void OnEntityLogCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        var config = getConfig();
        var parsed = ParseCommonArgs(args, config.EntityRadius, config.DefaultEntityDisplayCount);
        if (parsed.ShowHelp) { SendEntityHelp(player, groupId); return; }

        var entitySel = player.CurrentEntitySelection;
        var targetEntity = entitySel?.Entity ?? player.Entity;
        if (!EntityPositionResolver.TryGetBlockPos(targetEntity, out var pos))
        {
            player.SendMessage(groupId, "Unable to resolve entity position on this server build.", EnumChatType.CommandError);
            return;
        }

        parsed.Radius = Math.Clamp(parsed.Radius, 0, config.MaxRadius);
        parsed.Count = Math.Clamp(parsed.Count, 1, Math.Min(config.MaxDisplayCount, 200));

        if (!TryStartQuery(player, groupId, out var key)) return;

        player.SendMessage(groupId, $"EntityLogger search queued (radius={parsed.Radius}, max={parsed.Count}).", EnumChatType.CommandSuccess);
        _ = RunEntityQuery(player, groupId, key, pos.X, pos.Y, pos.Z, pos.dimension, parsed, CreateDisplayContext(player));
    }

    private void OnEntityLogExactAliasCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        var config = getConfig();
        var parsed = ParseCommonArgs(args, 0, config.DefaultEntityDisplayCount);
        var entitySel = player.CurrentEntitySelection;
        if (entitySel?.Entity == null)
        {
            player.SendMessage(groupId, "Look at an entity first.", EnumChatType.CommandError);
            return;
        }

        parsed.Count = Math.Clamp(parsed.Count, 1, Math.Min(config.MaxDisplayCount, 200));
        if (!EntityPositionResolver.TryGetBlockPos(entitySel.Entity, out var pos))
        {
            player.SendMessage(groupId, "Unable to resolve entity position on this server build.", EnumChatType.CommandError);
            return;
        }

        if (!TryStartQuery(player, groupId, out var key)) return;

        player.SendMessage(groupId, "EntityLogger exact search queued.", EnumChatType.CommandSuccess);
        _ = RunEntityQuery(player, groupId, key, pos.X, pos.Y, pos.Z, pos.dimension, parsed, CreateDisplayContext(player));
    }

    private async System.Threading.Tasks.Task RunBlockQuery(IServerPlayer player, int groupId, string key, int x, int y, int z, int dimension, ParsedArgs parsed, DisplayContext display)
    {
        try
        {
            var languageCode = player.LanguageCode;
            var results = await queryService.QueryBlocksAsync(x, y, z, dimension, parsed.Radius, parsed.Count, parsed.SearchText, parsed.Tag).ConfigureAwait(false);
            var formatted = results.ConvertAll(r => FormatRecordForChat(display, r, languageCode));
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (results.Count == 0)
                {
                    player.SendMessage(groupId, $"No records found at {FormatConfigured(new Vintagestory.API.MathTools.BlockPos(x, y, z))}.", EnumChatType.CommandSuccess);
                    return;
                }

                player.SendMessage(groupId, $"BlockLogger: {results.Count} result(s) at radius={parsed.Radius}", EnumChatType.CommandSuccess);
                SendResults(player, groupId, formatted);
            }, "blocklogger-block-results");
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[BlockLogger] Block query failed: {0}", ex.Message);
            api.Event.EnqueueMainThreadTask(() =>
                player.SendMessage(groupId, "BlockLogger search failed. Check the server log.", EnumChatType.CommandError),
                "blocklogger-block-error");
        }
        finally { activeQueries.TryRemove(key, out _); }
    }

    private async System.Threading.Tasks.Task RunEntityQuery(IServerPlayer player, int groupId, string key, int x, int y, int z, int dimension, ParsedArgs parsed, DisplayContext display)
    {
        try
        {
            var languageCode = player.LanguageCode;
            var results = await queryService.QueryEntitiesAsync(x, y, z, dimension, parsed.Radius, parsed.Count, parsed.SearchText, parsed.Tag).ConfigureAwait(false);
            var formatted = results.ConvertAll(r => FormatRecordForChat(display, r, languageCode));
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (results.Count == 0)
                {
                    player.SendMessage(groupId, $"No entity records found near {FormatConfigured(new Vintagestory.API.MathTools.BlockPos(x, y, z))}.", EnumChatType.CommandSuccess);
                    return;
                }

                player.SendMessage(groupId, $"EntityLogger: {results.Count} result(s) near radius={parsed.Radius}", EnumChatType.CommandSuccess);
                SendResults(player, groupId, formatted);
            }, "blocklogger-entity-results");
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[BlockLogger] Entity query failed: {0}", ex.Message);
            api.Event.EnqueueMainThreadTask(() =>
                player.SendMessage(groupId, "EntityLogger search failed. Check the server log.", EnumChatType.CommandError),
                "blocklogger-entity-error");
        }
        finally { activeQueries.TryRemove(key, out _); }
    }

    private bool TryStartQuery(IServerPlayer player, int groupId, out string key)
    {
        key = player.PlayerUID;
        if (!activeQueries.TryAdd(key, 0))
        {
            player.SendMessage(groupId, "A BlockLogger search is already running for you. Wait for it to finish.", EnumChatType.CommandError);
            return false;
        }

        if (!storage.IsReady)
            player.SendMessage(groupId, "The BlockLogger database is still indexing legacy logs. Your search will run when indexing finishes.", EnumChatType.CommandSuccess);

        return true;
    }

    private ParsedArgs ParseCommonArgs(CmdArgs args, int defaultRadius, int defaultCount)
    {
        var parsed = new ParsedArgs { Radius = defaultRadius, Count = defaultCount };
        while (args.Length > 0)
        {
            var word = args.PopWord();
            if (string.IsNullOrWhiteSpace(word)) break;
            switch (word.ToLowerInvariant())
            {
                case "help":
                case "-h":
                case "--help": parsed.ShowHelp = true; return parsed;
                case "up": parsed.VerticalOffset += 1; break;
                case "down": parsed.VerticalOffset -= 1; break;
                case "-r":
                case "--radius":
                    if (int.TryParse(args.PopWord(), out var radius)) parsed.Radius = radius;
                    break;
                case "-n":
                case "--count":
                    if (int.TryParse(args.PopWord(), out var count)) parsed.Count = count;
                    break;
                case "-s":
                case "--search":
                case "-p":
                case "--player": parsed.SearchText = args.PopWord(); break;
                case "-t":
                case "--tag":
                case "-a":
                case "--action": parsed.Tag = args.PopWord(); break;
                default:
                    if (parsed.SearchText == null) parsed.SearchText = word;
                    else parsed.Tag ??= word;
                    break;
            }
        }
        return parsed;
    }

    private void SendBlockHelp(IServerPlayer player, int groupId)
    {
        player.SendMessage(groupId, "/blocklog [up|down] [-r radius] [-n count] [-s text|-p player] [-t tag|-a action]", EnumChatType.CommandSuccess);
        player.SendMessage(groupId, "Actions/tags: break, place, interact, open, take, put", EnumChatType.CommandSuccess);
    }

    private void SendEntityHelp(IServerPlayer player, int groupId)
    {
        player.SendMessage(groupId, "/entitylog [-r radius] [-n count] [-s text|-p player] [-t tag|-a action]", EnumChatType.CommandSuccess);
        player.SendMessage(groupId, "Actions/tags: entityinteract, entityspawn, entitydeath, entitydespawn", EnumChatType.CommandSuccess);
    }

    private void SendResults(IServerPlayer player, int groupId, List<string> results)
    {
        const int batchSize = 10;
        var index = 0;

        void SendBatch(float _)
        {
            try
            {
                var end = Math.Min(index + batchSize, results.Count);
                for (; index < end; index++)
                    player.SendMessage(groupId, results[index], EnumChatType.CommandSuccess);

                if (index < results.Count)
                    api.Event.RegisterCallback(SendBatch, 50);
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[BlockLogger] Result display callback stopped safely: {0}: {1}", ex.GetType().Name, ex.Message);
            }
        }

        api.Event.RegisterCallback(SendBatch, 0);
    }

    private string FormatRecordForChat(DisplayContext display, LogEventRecord record, string? languageCode)
    {
        var tags = BuildTags(record);
        var actor = string.IsNullOrWhiteSpace(record.PlayerName) ? "Unknown" : EscapeVtml(record.PlayerName);
        var dx = record.X - display.ViewerX;
        var dz = record.Z - display.ViewerZ;
        var rel = $"ΔX/Z {dx:+#;-#;0}/{dz:+#;-#;0}";

        var actionText = record.Action switch
        {
            LogActionType.BlockPlace => $"placed {ColorCode(record.BlockCode, "#dcdcaa")}",
            LogActionType.BlockBreak => $"broke {ColorCode(record.BlockCode, "#dcdcaa")}",
            LogActionType.BlockUse => $"used {ColorCode(record.BlockCode, "#dcdcaa")}",
            LogActionType.ContainerOpen => $"opened {ColorCode(record.BlockCode, "#dcdcaa")}",
            LogActionType.ContainerTake => $"took {ColorCode($"{GetQuantity(record)}x {GetMeta(record, "itemCode") ?? "unknown"}", "#ce9178")} from {ColorCode(record.BlockCode, "#dcdcaa")}",
            LogActionType.ContainerPut => $"put {ColorCode($"{GetQuantity(record)}x {GetMeta(record, "itemCode") ?? "unknown"}", "#ce9178")} into {ColorCode(record.BlockCode, "#dcdcaa")}",
            LogActionType.EntityInteract => $"interacted with {ColorCode(GetMeta(record, "entityCode") ?? "entity", "#4ec9b0")}",
            LogActionType.EntitySpawn => $"spawned {ColorCode(GetMeta(record, "entityCode") ?? "entity", "#4ec9b0")}{GetEventCountSuffix(record)}",
            LogActionType.EntityDeath => $"{ColorCode(GetMeta(record, "entityCode") ?? "entity", "#4ec9b0")} died",
            LogActionType.EntityDespawn => $"despawned {ColorCode(GetMeta(record, "entityCode") ?? "entity", "#4ec9b0")}{GetEventCountSuffix(record)}",
            _ => EscapeVtml(record.Action.ToString())
        };

        return $"{tags} <strong>{actor}</strong> {actionText} <font color=\"#808080\">{EscapeVtml(rel)}</font> {ColorCode(FormatConfigured(display, record.X, record.Y, record.Z), "#9cdcfe", true)} {ColorCode(IngameTimeFormatter.FormatNewfoundlandTimestamp(record.TimestampUtc), "#b5cea8", true)} {ColorCode(IngameTimeFormatter.LocalizeStoredStamp(record.IngameDate, languageCode), "#c586c0")}";
    }

    private static string BuildTags(LogEventRecord record)
    {
        var tags = new List<string>();
        switch (record.Action)
        {
            case LogActionType.BlockPlace:
            case LogActionType.BlockBreak:
            case LogActionType.BlockUse: tags.Add(ColorTag("BLOCK", "#569cd6")); break;
            case LogActionType.ContainerOpen:
            case LogActionType.ContainerTake:
            case LogActionType.ContainerPut: tags.Add(ColorTag("CONTAINER", "#d7ba7d")); break;
            case LogActionType.EntityInteract:
            case LogActionType.EntitySpawn:
            case LogActionType.EntityDeath:
            case LogActionType.EntityDespawn: tags.Add(ColorTag("ENTITY", "#4ec9b0")); break;
        }
        if (record.Meta.ContainsKey("itemCode") || record.Meta.ContainsKey("heldItem")) tags.Add(ColorTag("ITEM", "#ce9178"));
        return string.Join(" ", tags);
    }

    private DisplayContext CreateDisplayContext(IServerPlayer player)
    {
        var vx = 0; var vz = 0;
        if (EntityPositionResolver.TryGetBlockPos(player.Entity, out var viewerPos)) { vx = viewerPos.X; vz = viewerPos.Z; }
        var mode = CoordinateDisplayService.GetModeLabel(getConfig().CoordinateDisplayMode);
        var originX = 0; var originZ = 0;
        if (mode == "RelativeToWorldCenter")
        {
            originX = api.WorldManager.MapSizeX / 2; originZ = api.WorldManager.MapSizeZ / 2;
        }
        else if (mode == "RelativeToDefaultSpawn")
        {
            var spawn = CoordinateDisplayService.GetDefaultSpawnOrWorldCenter(api);
            originX = spawn.X;
            originZ = spawn.Z;
        }
        return new DisplayContext(vx, vz, mode, originX, originZ);
    }

    private static string FormatConfigured(DisplayContext display, int x, int y, int z)
    {
        if (display.Mode == "Absolute") return $"Abs [{x},{y},{z}]";
        var label = display.Mode == "RelativeToWorldCenter" ? "Center" : "Spawn";
        var rx = x - display.OriginX; var rz = z - display.OriginZ;
        return $"{label} [{rx:+#;-#;0},{y},{rz:+#;-#;0}]";
    }

    private string FormatConfigured(Vintagestory.API.MathTools.BlockPos pos) =>
        CoordinateDisplayService.FormatConfigured(api, getConfig().CoordinateDisplayMode, pos.X, pos.Y, pos.Z);

    private string FormatConfigured(LogEventRecord record) =>
        CoordinateDisplayService.FormatConfigured(api, getConfig().CoordinateDisplayMode, record.X, record.Y, record.Z);

    private static string ColorTag(string text, string color) => $"<font color=\"{color}\"><strong>[{EscapeVtml(text)}]</strong></font>";
    private static string ColorCode(string text, string color, bool bold = false) => bold ? $"<font color=\"{color}\"><strong>{EscapeVtml(text)}</strong></font>" : $"<font color=\"{color}\">{EscapeVtml(text)}</font>";
    private static string EscapeVtml(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    private static string? GetMeta(LogEventRecord record, string key) => record.Meta.TryGetValue(key, out var value) ? value : null;
    private static int GetQuantity(LogEventRecord record) => int.TryParse(GetMeta(record, "quantity"), out var qty) ? qty : 0;
    private static string GetEventCountSuffix(LogEventRecord record) => int.TryParse(GetMeta(record, "eventCount"), out var n) && n > 1 ? $" ×{n}" : string.Empty;

    private sealed class ParsedArgs
    {
        public bool ShowHelp { get; set; }
        public int VerticalOffset { get; set; }
        public int Radius { get; set; }
        public int Count { get; set; }
        public string? SearchText { get; set; }
        public string? Tag { get; set; }
    }
}
