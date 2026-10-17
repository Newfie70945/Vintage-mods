using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using BlockLogger.Config;
using BlockLogger.Models;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Commands;

public sealed class BlockLoggerCommands
{
	private readonly record struct DisplayContext(int ViewerX, int ViewerZ, string Mode, int OriginX, int OriginZ);

	private sealed class ParsedArgs
	{
		public bool ShowHelp { get; set; }

		public int VerticalOffset { get; set; }

		public int Radius { get; set; }

		public int Count { get; set; }

		public string? SearchText { get; set; }

		public string? Tag { get; set; }
	}

	private const string PrivilegeCode = "blocklogger";

	private readonly ICoreServerAPI api;

	private readonly Func<BlockLoggerConfig> getConfig;

	private readonly Action reloadConfig;

	private readonly LogStorageService storage;

	private readonly LogQueryService queryService;

	private readonly ContainerSnapshotQueryService snapshotQueryService;

	private readonly ConcurrentDictionary<string, byte> activeQueries = new ConcurrentDictionary<string, byte>();

	public BlockLoggerCommands(ICoreServerAPI api, Func<BlockLoggerConfig> getConfig, Action reloadConfig, LogStorageService storage, LogQueryService queryService, ContainerSnapshotQueryService snapshotQueryService)
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
		api.Permissions.RegisterPrivilege("blocklogger", "Use BlockLogger investigation commands");
		api.RegisterCommand("blocklogger", "Manage BlockLogger", "[status|reloadconfig|path|count]", OnAdminCommand, "blocklogger");
		api.RegisterCommand("blocklog", "Inspect logged block and container activity", "(help|up|down) (-r R) (-n N) (-s text) (-t tag)", OnBlockLogCommand, "blocklogger");
		api.RegisterCommand("entitylog", "Inspect logged entity activity", "(-r R) (-n N) (-s text) (-t tag)", OnEntityLogCommand, "blocklogger");
		api.RegisterCommand("blockloge", "Alias for exact entity lookup", "(-n N) (-s text) (-t tag)", OnEntityLogExactAliasCommand, "blocklogger");
	}

	private void OnAdminCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		switch (args.PopWord()?.ToLowerInvariant())
		{
		case "reload":
		case "reloadconfig":
			reloadConfig();
			player.SendMessage(groupId, "BlockLogger config reloaded.", EnumChatType.CommandSuccess);
			break;
		case "status":
		{
			BlockLoggerConfig blockLoggerConfig = getConfig();
			player.SendMessage(groupId, $"Enabled={blockLoggerConfig.Enabled}, Storage=SQLite/WAL, Indexed={storage.IsReady}, Pending={storage.PendingEvents} (critical={storage.PendingCriticalEvents}, entity={storage.PendingBulkEntityEvents}, overflow={storage.PendingOverflowEvents}/{storage.EmergencyOverflowCapacity}), PeakPending={storage.PeakPendingEvents}, OverflowAccepted={storage.OverflowAcceptedEvents}, OverflowRejected={storage.OverflowRejectedEvents}, Dropped={storage.DroppedEvents} (entity={storage.DroppedEntityEvents}), Written={storage.WrittenEvents:N0} (exactEntity={storage.WrittenEntityEvents:N0}, aggregates={storage.WrittenEntityAggregates:N0}, aggregatePending={storage.PendingEntityAggregates}, aggregatePeak={storage.PeakPendingEntityAggregates}, aggregateDropped={storage.DroppedEntityAggregates}), LastBatch={storage.LastBatchRows} rows (entity={storage.LastBatchEntityRows})/{storage.LastBatchElapsedMs}ms, SnapshotsPending={storage.PendingSnapshots}, RetentionDays={blockLoggerConfig.RetentionDays}, EntityAggregate={blockLoggerConfig.AggregateEntitySpawnDespawn}/{blockLoggerConfig.EntityAggregateWindowMs}-{blockLoggerConfig.EntityAggregateMaxWindowMs}ms adaptive={blockLoggerConfig.AdaptiveEntityAggregation} region={blockLoggerConfig.EntityAggregateRegionSize}, CoordMode={CoordinateDisplayService.GetModeLabel(blockLoggerConfig.CoordinateDisplayMode)}", EnumChatType.CommandSuccess);
			break;
		}
		case "path":
			player.SendMessage(groupId, "Database: " + storage.DatabasePath, EnumChatType.CommandSuccess);
			player.SendMessage(groupId, "Legacy event logs: " + queryService.GetLogDirectoryPath(), EnumChatType.CommandSuccess);
			player.SendMessage(groupId, "Legacy snapshots: " + snapshotQueryService.GetLogDirectoryPath(), EnumChatType.CommandSuccess);
			break;
		case "count":
			SendCountsAsync(player, groupId);
			break;
		default:
			player.SendMessage(groupId, "/blocklogger [status|reloadconfig|path|count]", EnumChatType.CommandError);
			break;
		}
	}

	private async Task SendCountsAsync(IServerPlayer player, int groupId)
	{
		try
		{
			player.SendMessage(groupId, "BlockLogger count query queued...", EnumChatType.CommandSuccess);
			int events = await queryService.CountAsync().ConfigureAwait(continueOnCapturedContext: false);
			int snapshots = await snapshotQueryService.CountAsync().ConfigureAwait(continueOnCapturedContext: false);
			api.Event.EnqueueMainThreadTask(() =>
			{
				player.SendMessage(groupId, $"Indexed event records: {events:N0}", EnumChatType.CommandSuccess);
				player.SendMessage(groupId, $"Indexed container snapshots: {snapshots:N0}", EnumChatType.CommandSuccess);
			}, "blocklogger-count-result");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			api.Logger.Warning("[BlockLogger] Count query failed: {0}", ex2.Message);
			api.Event.EnqueueMainThreadTask(() =>
			{
				player.SendMessage(groupId, "Count query failed. Check the server log.", EnumChatType.CommandError);
			}, "blocklogger-count-error");
		}
	}

	private void OnBlockLogCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		BlockLoggerConfig blockLoggerConfig = getConfig();
		ParsedArgs parsedArgs = ParseCommonArgs(args, blockLoggerConfig.DefaultRadius, blockLoggerConfig.DefaultDisplayCount);
		if (parsedArgs.ShowHelp)
		{
			SendBlockHelp(player, groupId);
			return;
		}
		BlockSelection currentBlockSelection = player.CurrentBlockSelection;
		if (currentBlockSelection?.Position == null)
		{
			player.SendMessage(groupId, "Look at a block first.", EnumChatType.CommandError);
			return;
		}
		BlockPos blockPos = currentBlockSelection.Position.Copy();
		blockPos.Y += parsedArgs.VerticalOffset;
		parsedArgs.Radius = Math.Clamp(parsedArgs.Radius, 0, blockLoggerConfig.MaxRadius);
		parsedArgs.Count = Math.Clamp(parsedArgs.Count, 1, Math.Min(blockLoggerConfig.MaxDisplayCount, 200));
		if (TryStartQuery(player, groupId, out string key))
		{
			player.SendMessage(groupId, $"BlockLogger search queued (radius={parsedArgs.Radius}, max={parsedArgs.Count}).", EnumChatType.CommandSuccess);
			RunBlockQuery(player, groupId, key, blockPos.X, blockPos.Y, blockPos.Z, blockPos.dimension, parsedArgs, CreateDisplayContext(player));
		}
	}

	private void OnEntityLogCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		BlockLoggerConfig blockLoggerConfig = getConfig();
		ParsedArgs parsedArgs = ParseCommonArgs(args, blockLoggerConfig.EntityRadius, blockLoggerConfig.DefaultEntityDisplayCount);
		if (parsedArgs.ShowHelp)
		{
			SendEntityHelp(player, groupId);
			return;
		}
		Entity entity = player.CurrentEntitySelection?.Entity ?? player.Entity;
		if (!EntityPositionResolver.TryGetBlockPos(entity, out BlockPos pos))
		{
			player.SendMessage(groupId, "Unable to resolve entity position on this server build.", EnumChatType.CommandError);
			return;
		}
		parsedArgs.Radius = Math.Clamp(parsedArgs.Radius, 0, blockLoggerConfig.MaxRadius);
		parsedArgs.Count = Math.Clamp(parsedArgs.Count, 1, Math.Min(blockLoggerConfig.MaxDisplayCount, 200));
		if (TryStartQuery(player, groupId, out string key))
		{
			player.SendMessage(groupId, $"EntityLogger search queued (radius={parsedArgs.Radius}, max={parsedArgs.Count}).", EnumChatType.CommandSuccess);
			RunEntityQuery(player, groupId, key, pos.X, pos.Y, pos.Z, pos.dimension, parsedArgs, CreateDisplayContext(player));
		}
	}

	private void OnEntityLogExactAliasCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		BlockLoggerConfig blockLoggerConfig = getConfig();
		ParsedArgs parsedArgs = ParseCommonArgs(args, 0, blockLoggerConfig.DefaultEntityDisplayCount);
		EntitySelection currentEntitySelection = player.CurrentEntitySelection;
		if (currentEntitySelection?.Entity == null)
		{
			player.SendMessage(groupId, "Look at an entity first.", EnumChatType.CommandError);
			return;
		}
		parsedArgs.Count = Math.Clamp(parsedArgs.Count, 1, Math.Min(blockLoggerConfig.MaxDisplayCount, 200));
		string key;
		if (!EntityPositionResolver.TryGetBlockPos(currentEntitySelection.Entity, out BlockPos pos))
		{
			player.SendMessage(groupId, "Unable to resolve entity position on this server build.", EnumChatType.CommandError);
		}
		else if (TryStartQuery(player, groupId, out key))
		{
			player.SendMessage(groupId, "EntityLogger exact search queued.", EnumChatType.CommandSuccess);
			RunEntityQuery(player, groupId, key, pos.X, pos.Y, pos.Z, pos.dimension, parsedArgs, CreateDisplayContext(player));
		}
	}

	private async Task RunBlockQuery(IServerPlayer player, int groupId, string key, int x, int y, int z, int dimension, ParsedArgs parsed, DisplayContext display)
	{
		try
		{
			string languageCode = player.LanguageCode;
			List<LogEventRecord> results = await queryService.QueryBlocksAsync(x, y, z, dimension, parsed.Radius, parsed.Count, parsed.SearchText, parsed.Tag).ConfigureAwait(continueOnCapturedContext: false);
			List<string> formatted = results.ConvertAll((LogEventRecord r) => FormatRecordForChat(display, r, languageCode));
			api.Event.EnqueueMainThreadTask(() =>
			{
				if (results.Count == 0)
				{
					player.SendMessage(groupId, "No records found at " + FormatConfigured(new BlockPos(x, y, z)) + ".", EnumChatType.CommandSuccess);
				}
				else
				{
					player.SendMessage(groupId, $"BlockLogger: {results.Count} result(s) at radius={parsed.Radius}", EnumChatType.CommandSuccess);
					SendResults(player, groupId, formatted);
				}
			}, "blocklogger-block-results");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			api.Logger.Warning("[BlockLogger] Block query failed: {0}", ex2.Message);
			api.Event.EnqueueMainThreadTask(() =>
			{
				player.SendMessage(groupId, "BlockLogger search failed. Check the server log.", EnumChatType.CommandError);
			}, "blocklogger-block-error");
		}
		finally
		{
			activeQueries.TryRemove(key, out var _);
		}
	}

	private async Task RunEntityQuery(IServerPlayer player, int groupId, string key, int x, int y, int z, int dimension, ParsedArgs parsed, DisplayContext display)
	{
		try
		{
			string languageCode = player.LanguageCode;
			List<LogEventRecord> results = await queryService.QueryEntitiesAsync(x, y, z, dimension, parsed.Radius, parsed.Count, parsed.SearchText, parsed.Tag).ConfigureAwait(continueOnCapturedContext: false);
			List<string> formatted = results.ConvertAll((LogEventRecord r) => FormatRecordForChat(display, r, languageCode));
			api.Event.EnqueueMainThreadTask(() =>
			{
				if (results.Count == 0)
				{
					player.SendMessage(groupId, "No entity records found near " + FormatConfigured(new BlockPos(x, y, z)) + ".", EnumChatType.CommandSuccess);
				}
				else
				{
					player.SendMessage(groupId, $"EntityLogger: {results.Count} result(s) near radius={parsed.Radius}", EnumChatType.CommandSuccess);
					SendResults(player, groupId, formatted);
				}
			}, "blocklogger-entity-results");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			api.Logger.Warning("[BlockLogger] Entity query failed: {0}", ex2.Message);
			api.Event.EnqueueMainThreadTask(() =>
			{
				player.SendMessage(groupId, "EntityLogger search failed. Check the server log.", EnumChatType.CommandError);
			}, "blocklogger-entity-error");
		}
		finally
		{
			activeQueries.TryRemove(key, out var _);
		}
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
		{
			player.SendMessage(groupId, "The BlockLogger database is still indexing legacy logs. Your search will run when indexing finishes.", EnumChatType.CommandSuccess);
		}
		return true;
	}

	private ParsedArgs ParseCommonArgs(CmdArgs args, int defaultRadius, int defaultCount)
	{
		ParsedArgs parsedArgs = new ParsedArgs
		{
			Radius = defaultRadius,
			Count = defaultCount
		};
		while (args.Length > 0)
		{
			string text = args.PopWord();
			if (string.IsNullOrWhiteSpace(text))
			{
				break;
			}
			switch (text.ToLowerInvariant())
			{
			case "help":
			case "-h":
			case "--help":
				parsedArgs.ShowHelp = true;
				return parsedArgs;
			case "up":
				parsedArgs.VerticalOffset++;
				continue;
			case "down":
				parsedArgs.VerticalOffset--;
				continue;
			case "-r":
			case "--radius":
			{
				if (int.TryParse(args.PopWord(), out var result))
				{
					parsedArgs.Radius = result;
				}
				continue;
			}
			case "-n":
			case "--count":
			{
				if (int.TryParse(args.PopWord(), out var result2))
				{
					parsedArgs.Count = result2;
				}
				continue;
			}
			case "-s":
			case "--search":
			case "-p":
			case "--player":
				parsedArgs.SearchText = args.PopWord();
				continue;
			case "-t":
			case "--tag":
			case "-a":
			case "--action":
				parsedArgs.Tag = args.PopWord();
				continue;
			}
			if (parsedArgs.SearchText == null)
			{
				parsedArgs.SearchText = text;
				continue;
			}
			ParsedArgs parsedArgs2 = parsedArgs;
			if (parsedArgs2.Tag == null)
			{
				string text2 = (parsedArgs2.Tag = text);
			}
		}
		return parsedArgs;
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
		int index = 0;
		api.Event.RegisterCallback(SendBatch, 0);
		void SendBatch(float _)
		{
			try
			{
				for (int num = Math.Min(index + 10, results.Count); index < num; index++)
				{
					player.SendMessage(groupId, results[index], EnumChatType.CommandSuccess);
				}
				if (index < results.Count)
				{
					api.Event.RegisterCallback(SendBatch, 50);
				}
			}
			catch (Exception ex)
			{
				api.Logger.Warning("[BlockLogger] Result display callback stopped safely: {0}: {1}", ex.GetType().Name, ex.Message);
			}
		}
	}

	private string FormatRecordForChat(DisplayContext display, LogEventRecord record, string? languageCode)
	{
		string value = BuildTags(record);
		string value2 = (string.IsNullOrWhiteSpace(record.PlayerName) ? "Unknown" : EscapeVtml(record.PlayerName));
		int value3 = record.X - display.ViewerX;
		int value4 = record.Z - display.ViewerZ;
		string text = $"ΔX/Z {value3:+#;-#;0}/{value4:+#;-#;0}";
		LogActionType action = record.Action;
		if (1 == 0)
		{
		}
		string text2 = action switch
		{
			LogActionType.BlockPlace => "placed " + ColorCode(record.BlockCode, "#dcdcaa"), 
			LogActionType.BlockBreak => "broke " + ColorCode(record.BlockCode, "#dcdcaa"), 
			LogActionType.BlockUse => "used " + ColorCode(record.BlockCode, "#dcdcaa"), 
			LogActionType.ContainerOpen => "opened " + ColorCode(record.BlockCode, "#dcdcaa"), 
			LogActionType.ContainerTake => "took " + ColorCode($"{GetQuantity(record)}x {GetMeta(record, "itemCode") ?? "unknown"}", "#ce9178") + " from " + ColorCode(record.BlockCode, "#dcdcaa"), 
			LogActionType.ContainerPut => "put " + ColorCode($"{GetQuantity(record)}x {GetMeta(record, "itemCode") ?? "unknown"}", "#ce9178") + " into " + ColorCode(record.BlockCode, "#dcdcaa"), 
			LogActionType.EntityInteract => "interacted with " + ColorCode(GetMeta(record, "entityCode") ?? "entity", "#4ec9b0"), 
			LogActionType.EntitySpawn => "spawned " + ColorCode(GetMeta(record, "entityCode") ?? "entity", "#4ec9b0") + GetEventCountSuffix(record), 
			LogActionType.EntityDeath => ColorCode(GetMeta(record, "entityCode") ?? "entity", "#4ec9b0") + " died", 
			LogActionType.EntityDespawn => "despawned " + ColorCode(GetMeta(record, "entityCode") ?? "entity", "#4ec9b0") + GetEventCountSuffix(record), 
			_ => EscapeVtml(record.Action.ToString()), 
		};
		if (1 == 0)
		{
		}
		string value5 = text2;
		return $"{value} <strong>{value2}</strong> {value5} <font color=\"#808080\">{EscapeVtml(text)}</font> {ColorCode(FormatConfigured(display, record.X, record.Y, record.Z), "#9cdcfe", bold: true)} {ColorCode(IngameTimeFormatter.FormatNewfoundlandTimestamp(record.TimestampUtc), "#b5cea8", bold: true)} {ColorCode(IngameTimeFormatter.LocalizeStoredStamp(record.IngameDate, languageCode), "#c586c0")}";
	}

	private static string BuildTags(LogEventRecord record)
	{
		List<string> list = new List<string>();
		switch (record.Action)
		{
		case LogActionType.BlockBreak:
		case LogActionType.BlockPlace:
		case LogActionType.BlockUse:
			list.Add(ColorTag("BLOCK", "#569cd6"));
			break;
		case LogActionType.ContainerOpen:
		case LogActionType.ContainerTake:
		case LogActionType.ContainerPut:
			list.Add(ColorTag("CONTAINER", "#d7ba7d"));
			break;
		case LogActionType.EntityInteract:
		case LogActionType.EntitySpawn:
		case LogActionType.EntityDeath:
		case LogActionType.EntityDespawn:
			list.Add(ColorTag("ENTITY", "#4ec9b0"));
			break;
		}
		if (record.Meta.ContainsKey("itemCode") || record.Meta.ContainsKey("heldItem"))
		{
			list.Add(ColorTag("ITEM", "#ce9178"));
		}
		return string.Join(" ", list);
	}

	private DisplayContext CreateDisplayContext(IServerPlayer player)
	{
		int viewerX = 0;
		int viewerZ = 0;
		if (EntityPositionResolver.TryGetBlockPos(player.Entity, out BlockPos pos))
		{
			viewerX = pos.X;
			viewerZ = pos.Z;
		}
		string modeLabel = CoordinateDisplayService.GetModeLabel(getConfig().CoordinateDisplayMode);
		int originX = 0;
		int originZ = 0;
		if (modeLabel == "RelativeToWorldCenter")
		{
			originX = api.WorldManager.MapSizeX / 2;
			originZ = api.WorldManager.MapSizeZ / 2;
		}
		else if (modeLabel == "RelativeToDefaultSpawn")
		{
			Vec3i defaultSpawnOrWorldCenter = CoordinateDisplayService.GetDefaultSpawnOrWorldCenter(api);
			originX = defaultSpawnOrWorldCenter.X;
			originZ = defaultSpawnOrWorldCenter.Z;
		}
		return new DisplayContext(viewerX, viewerZ, modeLabel, originX, originZ);
	}

	private static string FormatConfigured(DisplayContext display, int x, int y, int z)
	{
		if (!(display.Mode == "Absolute"))
		{
			string value = ((display.Mode == "RelativeToWorldCenter") ? "Center" : "Spawn");
			int value2 = x - display.OriginX;
			int value3 = z - display.OriginZ;
			return $"{value} [{value2:+#;-#;0},{y},{value3:+#;-#;0}]";
		}
		return $"Abs [{x},{y},{z}]";
	}

	private string FormatConfigured(BlockPos pos)
	{
		return CoordinateDisplayService.FormatConfigured(api, getConfig().CoordinateDisplayMode, pos.X, pos.Y, pos.Z);
	}

	private string FormatConfigured(LogEventRecord record)
	{
		return CoordinateDisplayService.FormatConfigured(api, getConfig().CoordinateDisplayMode, record.X, record.Y, record.Z);
	}

	private static string ColorTag(string text, string color)
	{
		return $"<font color=\"{color}\"><strong>[{EscapeVtml(text)}]</strong></font>";
	}

	private static string ColorCode(string text, string color, bool bold = false)
	{
		return bold ? $"<font color=\"{color}\"><strong>{EscapeVtml(text)}</strong></font>" : $"<font color=\"{color}\">{EscapeVtml(text)}</font>";
	}

	private static string EscapeVtml(string? text)
	{
		return string.IsNullOrEmpty(text) ? string.Empty : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
			.Replace("\"", "&quot;");
	}

	private static string? GetMeta(LogEventRecord record, string key)
	{
		string value;
		return record.Meta.TryGetValue(key, out value) ? value : null;
	}

	private static int GetQuantity(LogEventRecord record)
	{
		int result;
		return int.TryParse(GetMeta(record, "quantity"), out result) ? result : 0;
	}

	private static string GetEventCountSuffix(LogEventRecord record)
	{
		int result;
		return (int.TryParse(GetMeta(record, "eventCount"), out result) && result > 1) ? $" ×{result}" : string.Empty;
	}
}
