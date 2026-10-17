using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Afk;
using BlockLogger.AntiCheat;
using BlockLogger.Models;
using BlockLogger.PlayerCommands;
using BlockLogger.Reports;
using BlockLogger.Restarts;
using BlockLogger.ServerHealth;
using BlockLogger.Services;
using Newfies.ChatProtocol;
using NewfiesAntiCheat.Shared.Packets;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace BlockLogger.Admin;

public sealed class AdminService : IDisposable
{
	private sealed class AdminGhostState
	{
		public EnumGameMode GameMode;

		public bool FreeMove;

		public bool NoClip;
	}

	private sealed class PlayerActivityState
	{
		public EntityPos Position;

		public long LastActiveUnixMilliseconds;

		public PlayerActivityState(EntityPos position, long lastActiveUnixMilliseconds)
		{
			Position = position;
			LastActiveUnixMilliseconds = lastActiveUnixMilliseconds;
		}
	}

	private sealed record AdminUndoEntry(string Description, System.Func<string, string> Reverse);

	private const string DiscordRestartControlEvent = "newfiesse:restart-control";

	private const string DiscordStaffCommandEvent = "newfiesse:discord-staff-command";

	public const string PrivView = "nse.player.view";

	public const string PrivManage = "nse.player.manage";

	public const string PrivWarn = "nse.moderation.warn";

	public const string PrivMute = "nse.moderation.mute";

	public const string PrivBan = "nse.moderation.ban";

	public const string PrivInventory = "nse.inventory.view";

	public const string PrivInventoryEdit = "nse.inventory.edit";

	public const string PrivTeleport = "nse.teleport";

	public const string PrivCleanup = "nse.cleanup";

	public const string PrivDiagnostics = "nse.diagnostics";

	public const string PrivServer = "nse.server";

	public const string PrivReports = "nse.reports.manage";

	public const string PrivSmite = "nse.smite";

	public const string PrivJail = "nse.jail";

	private const string StateFile = "NewfiesServerEssentialsAdmin.json";

	private readonly ICoreServerAPI api;

	private readonly Func<ServerHealthService?> getHealth;

	private readonly Func<LogQueryService?> getQueries;

	private readonly ReportsService? reports;

	private readonly PlayerCommandsService? playerCommands;

	private readonly Func<AutomatedRestartService?> getAutomatedRestarts;

	private readonly object gate = new object();

	private AdminState state = new AdminState();

	private readonly Dictionary<string, EntityPos> frozenPositions = new Dictionary<string, EntityPos>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, EntityPos> adminBackPositions = new Dictionary<string, EntityPos>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, EntityPos> playerBackPositions = new Dictionary<string, EntityPos>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, AdminGhostState> ghostStates = new Dictionary<string, AdminGhostState>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, PlayerActivityState> activity = new Dictionary<string, PlayerActivityState>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, IServerPlayer> onlinePlayersByUid = new Dictionary<string, IServerPlayer>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, IServerPlayer> onlinePlayersByName = new Dictionary<string, IServerPlayer>(StringComparer.OrdinalIgnoreCase);

	private readonly object onlineGate = new object();

	private IReadOnlyList<AdminPlayerSummary> cachedOnlineSummaries = Array.Empty<AdminPlayerSummary>();

	private IReadOnlyList<AdminPlayerSummary> cachedAllSummaries = Array.Empty<AdminPlayerSummary>();

	private long cachedOnlineSummariesUnixMilliseconds;

	private long cachedAllSummariesUnixMilliseconds;

	private long cachedOnlineSummariesVersion = -1L;

	private long cachedAllSummariesVersion = -1L;

	private long playerSummaryVersion;

	private readonly object undoGate = new object();

	private readonly Stack<AdminUndoEntry> undoStack = new Stack<AdminUndoEntry>();

	private bool performingUndo;

	private long tickListenerId = -1L;

	private long freezeTickListenerId = -1L;

	private long shutdownAtUnixMilliseconds;

	private string shutdownReason = "Server shutdown";

	private long lastShutdownAnnouncementBucket = long.MinValue;

	private bool disposed;

	private long lastTickErrorLogUnixMilliseconds;

	private int suppressedTickErrors;

	private AdminStorageService? storage;

	private JailService? jailService;

	private long nextRecordId = 1L;

	private long lastActivitySampleUnixMilliseconds;

	private long lastExpiryScanUnixMilliseconds;

	private static readonly PropertyInfo? RuntimePosProperty = typeof(Entity).GetProperty("Pos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly FieldInfo? RuntimePosField = typeof(Entity).GetField("Pos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly PropertyInfo? RuntimeServerPosProperty = typeof(Entity).GetProperty("ServerPos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly FieldInfo? RuntimeServerPosField = typeof(Entity).GetField("ServerPos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly PropertyInfo? RuntimeSidedPosProperty = typeof(Entity).GetProperty("SidedPos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly FieldInfo? RuntimeSidedPosField = typeof(Entity).GetField("SidedPos", BindingFlags.Instance | BindingFlags.Public);

	private readonly AdminOfflineInventoryReader offlineInventoryReader = new AdminOfflineInventoryReader();

	private readonly Dictionary<string, (WeakReference<SavedPlayerInventoryData> Data, long ReadUnixMilliseconds, AdminInventoryInspection View)> decodedInventoryCache = new Dictionary<string, (WeakReference<SavedPlayerInventoryData>, long, AdminInventoryInspection)>(StringComparer.OrdinalIgnoreCase);

	public AdminService(ICoreServerAPI api, Func<ServerHealthService?> getHealth, Func<LogQueryService?> getQueries, ReportsService? reports, PlayerCommandsService? playerCommands, Func<AutomatedRestartService?> getAutomatedRestarts)
	{
		this.api = api;
		this.getHealth = getHealth;
		this.getQueries = getQueries;
		this.reports = reports;
		this.playerCommands = playerCommands;
		this.getAutomatedRestarts = getAutomatedRestarts;
	}

	public void Start()
	{
		storage = new AdminStorageService(api, SnapshotStateForStorage);
		storage.InventorySnapshotsCommitted += ReleasePersistedSnapshotPayloads;
		LoadState();
		InitializeNextRecordId();
		RegisterPrivileges();
		jailService = new JailService(api, Audit);
		jailService.Start();
		RegisterCommands();
		api.Event.PlayerJoin += OnPlayerJoin;
		api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
		api.Event.PlayerDisconnect += OnPlayerDisconnect;
		api.Event.PlayerChat += OnPlayerChat;
		api.Event.CanPlaceOrBreakBlock += OnCanPlaceOrBreakBlock;
		api.Event.CanUseBlock += OnCanUseBlock;
		api.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
		api.Event.GameWorldSave += OnGameWorldSave;
		tickListenerId = api.Event.RegisterGameTickListener(OnTick, 1000);
		api.Event.RegisterEventBusListener(OnDiscordRestartControl, 0.5, "newfiesse:restart-control");
		api.Event.RegisterEventBusListener(OnDiscordStaffCommand, 0.5, "newfiesse:discord-staff-command");
		foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
		{
			IndexOnlinePlayer(item);
		}
		foreach (IServerPlayer item2 in OnlinePlayers())
		{
			BeginSession(item2);
		}
		RefreshFreezeTickListener();
		api.Logger.Notification("[ServerEssentials/Admin] Player management started. KnownPlayers={0}", state.Players.Count);
	}

	private void OnDiscordStaffCommand(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (disposed || !(data is ITreeAttribute treeAttribute))
		{
			return;
		}
		string text = (treeAttribute.GetString("command", "") ?? "").Trim().ToLowerInvariant();
		string playerOrUid = (treeAttribute.GetString("player", "") ?? "").Trim();
		string duration = (treeAttribute.GetString("duration", "") ?? "").Trim();
		string reason = (treeAttribute.GetString("reason", "") ?? "").Trim();
		string text2 = (treeAttribute.GetString("discordUser", "Discord staff") ?? "Discord staff").Trim();
		string value = (treeAttribute.GetString("discordUserId", "") ?? "").Trim();
		string actor = (string.IsNullOrWhiteSpace(value) ? ("Discord: " + text2) : $"Discord: {text2} ({value})");
		treeAttribute.SetBool("handled", value: true);
		try
		{
			string text3;
			bool value2;
			switch (text)
			{
			case "mute":
				text3 = Mute(actor, playerOrUid, duration, reason, "discord");
				value2 = IsSuccessfulActionResult("mute", text3);
				break;
			case "unmute":
				text3 = Unmute(actor, playerOrUid, "discord");
				value2 = IsSuccessfulActionResult("unmute", text3);
				break;
			case "ban":
				text3 = TempBan(actor, playerOrUid, duration, reason, "discord");
				value2 = IsSuccessfulActionResult("tempban", text3);
				break;
			case "unban":
				text3 = Unban(actor, playerOrUid, "discord");
				value2 = IsSuccessfulActionResult("unban", text3);
				break;
			default:
				treeAttribute.SetBool("success", value: false);
				treeAttribute.SetString("response", "Unsupported Discord moderation command: " + text + ".");
				return;
			}
			treeAttribute.SetBool("success", value2);
			treeAttribute.SetString("response", text3 ?? "");
		}
		catch (Exception ex)
		{
			api.Logger.Error("[ServerEssentials/Admin] Discord moderation command {0} failed: {1}", text, ex);
			treeAttribute.SetBool("success", value: false);
			treeAttribute.SetString("response", "The moderation command failed inside Server Essentials Core. Check the server log.");
		}
	}

	private void OnDiscordRestartControl(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (disposed || !(data is ITreeAttribute treeAttribute))
		{
			return;
		}
		treeAttribute.SetBool("handled", value: true);
		string text = (treeAttribute.GetString("operation", "status") ?? "status").Trim().ToLowerInvariant();
		string actor = (treeAttribute.GetString("actor", "Discord staff") ?? "Discord staff").Trim();
		string text2 = (treeAttribute.GetString("reason", "") ?? "").Trim();
		if (text2.Length > 240)
		{
			text2 = text2.Substring(0, 240);
		}
		try
		{
			switch (text)
			{
			case "status":
			{
				long num2 = NowMs();
				string text3 = ((shutdownAtUnixMilliseconds > num2) ? $"Manual {(IsRestartShutdown() ? "restart" : "shutdown")} in {FormatDuration(shutdownAtUnixMilliseconds - num2)}." : "No manual restart is scheduled.");
				string text4 = getAutomatedRestarts()?.StatusText() ?? "Automated restart scheduler unavailable.";
				treeAttribute.SetBool("success", value: true);
				treeAttribute.SetString("response", text3 + " " + text4);
				break;
			}
			case "schedule":
			{
				int num = treeAttribute.GetInt("minutes");
				if (num < 1 || num > 1440)
				{
					treeAttribute.SetBool("success", value: false);
					treeAttribute.SetString("response", "Restart delay must be between 1 and 1440 minutes.");
				}
				else
				{
					string value2 = ScheduleRestart(actor, (long)num * 60000L, text2, "discord");
					treeAttribute.SetBool("success", value: true);
					treeAttribute.SetString("response", value2);
				}
				break;
			}
			case "now":
			{
				string value = ScheduleRestart(actor, 0L, text2, "discord");
				treeAttribute.SetBool("success", value: true);
				treeAttribute.SetString("response", value);
				break;
			}
			case "cancel":
				if (shutdownAtUnixMilliseconds <= 0)
				{
					treeAttribute.SetBool("success", value: false);
					treeAttribute.SetString("response", "No manual restart is scheduled.");
				}
				else if (!IsRestartShutdown())
				{
					treeAttribute.SetBool("success", value: false);
					treeAttribute.SetString("response", "A manual shutdown is scheduled, not a restart. Discord /restart will not cancel shutdowns.");
				}
				else
				{
					treeAttribute.SetBool("success", value: true);
					treeAttribute.SetString("response", CancelShutdown(actor, "discord"));
				}
				break;
			case "skipnext":
			{
				AutomatedRestartService automatedRestartService = getAutomatedRestarts();
				if (automatedRestartService == null)
				{
					treeAttribute.SetBool("success", value: false);
					treeAttribute.SetString("response", "Automated restart scheduler is unavailable.");
				}
				else
				{
					treeAttribute.SetBool("success", value: true);
					treeAttribute.SetString("response", automatedRestartService.SkipNext(actor));
				}
				break;
			}
			default:
				treeAttribute.SetBool("success", value: false);
				treeAttribute.SetString("response", "Unknown restart operation.");
				break;
			}
		}
		catch (Exception ex)
		{
			api.Logger.Error("[ServerEssentials/Admin] Discord restart control failed: {0}", ex);
			treeAttribute.SetBool("success", value: false);
			treeAttribute.SetString("response", "Restart control failed on the server. Check the Server Essentials log.");
		}
	}

	public AdminStorageHealth GetStorageHealth()
	{
		AdminStorageHealth adminStorageHealth = storage?.GetHealth() ?? new AdminStorageHealth
		{
			Available = false
		};
		lock (gate)
		{
			adminStorageHealth.Players = state.Players.Count;
			adminStorageHealth.AuditRows = state.Audit.Count;
			adminStorageHealth.InventorySnapshots = state.InventorySnapshots.Values.Sum((List<AdminInventorySnapshotRecord> x) => x.Count);
		}
		return adminStorageHealth;
	}

	public object GetRetentionDiagnostics()
	{
		lock (gate)
		{
			long num = 0L;
			long num2 = 0L;
			long num3 = 0L;
			long num4 = 0L;
			foreach (List<AdminInventorySnapshotRecord> value in state.InventorySnapshots.Values)
			{
				foreach (AdminInventorySnapshotRecord item in value)
				{
					num++;
					if (!item.SlotsPayloadLoaded)
					{
						continue;
					}
					num2++;
					foreach (AdminInventorySnapshotSlot slot in item.Slots)
					{
						if (num3 >= 65536)
						{
							break;
						}
						num3++;
						num4 += slot.StackBytes?.LongLength ?? 0;
					}
				}
			}
			return new
			{
				players = state.Players.Count,
				auditRows = state.Audit.Count,
				snapshotHeaders = num,
				loadedPayloads = num2,
				sampledSlots = num3,
				sampledBytes = num4,
				sampleLimit = 65536,
				sampleMayBeTruncated = (num3 >= 65536),
				cleanup = "Persisted item payloads are released after commit; historical items are fetched on demand."
			};
		}
	}

	public string CheckStorageIntegrity()
	{
		return storage?.RunQuickCheck() ?? "Storage unavailable";
	}

	public string BackupStorage(string reason = "manual")
	{
		return storage?.CreateBackup(reason) ?? "";
	}

	public IReadOnlyList<AdminPlayerSummary> GetPlayers()
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = NowMs();
		long num2 = Interlocked.Read(in playerSummaryVersion);
		lock (gate)
		{
			if (cachedAllSummariesVersion == num2 && num - cachedAllSummariesUnixMilliseconds < 5000)
			{
				return cachedAllSummaries;
			}
			Dictionary<string, IServerPlayer> online = OnlinePlayers().ToDictionary((IServerPlayer p) => p.PlayerUID, StringComparer.OrdinalIgnoreCase);
			HashSet<string> hashSet = new HashSet<string>(state.Players.Keys, StringComparer.OrdinalIgnoreCase);
			foreach (string key in online.Keys)
			{
				hashSet.Add(key);
			}
			cachedAllSummaries = (from uid in hashSet
				select BuildSummaryUnsafe(uid, online.TryGetValue(uid, out var value) ? value : null) into x
				orderby x.Online descending
				select x).ThenBy((AdminPlayerSummary x) => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
			cachedAllSummariesUnixMilliseconds = num;
			cachedAllSummariesVersion = num2;
			SuiteTimingRegistry.Record("Admin all-player summaries", (double)(Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency * 1000.0);
			return cachedAllSummaries;
		}
	}

	public IReadOnlyList<AdminPlayerSummary> GetOnlinePlayerSummaries()
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = NowMs();
		long num2 = Interlocked.Read(in playerSummaryVersion);
		lock (gate)
		{
			if (cachedOnlineSummariesVersion == num2 && num - cachedOnlineSummariesUnixMilliseconds < 2000)
			{
				return cachedOnlineSummaries;
			}
			cachedOnlineSummaries = (from player in OnlinePlayers()
				select BuildSummaryUnsafe(player.PlayerUID, player)).OrderBy((AdminPlayerSummary x) => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
			cachedOnlineSummariesUnixMilliseconds = num;
			cachedOnlineSummariesVersion = num2;
			SuiteTimingRegistry.Record("Admin online-player summaries", (double)(Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency * 1000.0);
			return cachedOnlineSummaries;
		}
	}

	public AdminPlayerSummary? GetPlayer(string playerOrUid)
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
		string text = serverPlayer?.PlayerUID ?? ResolveKnownUid(playerOrUid);
		if (string.IsNullOrWhiteSpace(text))
		{
			text = FindPlayer(playerOrUid)?.PlayerUID ?? "";
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		lock (gate)
		{
			return BuildSummaryUnsafe(text, serverPlayer);
		}
	}

	public List<AdminWarningRecord> GetWarnings(string playerOrUid)
	{
		string key = FindPlayer(playerOrUid)?.PlayerUID ?? ResolveKnownUid(playerOrUid);
		lock (gate)
		{
			PlayerAdminRecord value;
			return state.Players.TryGetValue(key, out value) ? value.Warnings.OrderByDescending((AdminWarningRecord x) => x.UnixMilliseconds).Select(CloneWarning).ToList() : new List<AdminWarningRecord>();
		}
	}

	public List<AdminNoteRecord> GetNotes(string playerOrUid)
	{
		string key = FindPlayer(playerOrUid)?.PlayerUID ?? ResolveKnownUid(playerOrUid);
		lock (gate)
		{
			PlayerAdminRecord value;
			return state.Players.TryGetValue(key, out value) ? (from x in value.Notes
				where !x.Removed
				orderby x.UnixMilliseconds descending
				select x).Select(CloneNote).ToList() : new List<AdminNoteRecord>();
		}
	}

	public List<AdminAuditRecord> GetHistory(string playerOrUid, int limit = 100)
	{
		string uid = FindPlayer(playerOrUid)?.PlayerUID ?? ResolveKnownUid(playerOrUid);
		lock (gate)
		{
			return (from x in state.Audit
				where string.Equals(x.TargetUid, uid, StringComparison.OrdinalIgnoreCase)
				orderby x.UnixMilliseconds descending
				select x).Take(Math.Clamp(limit, 1, 500)).Select(CloneAudit).ToList();
		}
	}

	public async Task<List<LogEventRecord>> GetBlockHistoryAsync(string playerOrUid, int limit = 100)
	{
		AdminPlayerSummary summary = GetPlayer(playerOrUid);
		LogQueryService queries = getQueries();
		if (summary == null || queries == null)
		{
			return new List<LogEventRecord>();
		}
		try
		{
			return await queries.QueryPlayerAsync(summary.Name, Math.Clamp(limit, 1, 500), null).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Admin] BlockLogger history query failed for {0}: {1}", summary.Name, ex.Message);
			return new List<LogEventRecord>();
		}
	}

	public List<AdminAuditRecord> GetAudit(int limit = 200)
	{
		lock (gate)
		{
			return state.Audit.OrderByDescending((AdminAuditRecord x) => x.UnixMilliseconds).Take(Math.Clamp(limit, 1, 1000)).Select(CloneAudit)
				.ToList();
		}
	}

	public List<AdminInventorySlotView> GetInventory(string playerOrUid)
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
		return (serverPlayer == null) ? new List<AdminInventorySlotView>() : InspectInventories(serverPlayer.InventoryManager.InventoriesOrdered);
	}

	public AdminInventoryInspection GetInventoryInspection(string playerOrUid, SavedPlayerInventoryData? saved = null)
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
		if (serverPlayer != null)
		{
			List<AdminInventorySlotView> list = InspectInventories(serverPlayer.InventoryManager.InventoriesOrdered);
			return new AdminInventoryInspection
			{
				Slots = list,
				Status = new AdminInventoryViewStatus
				{
					Available = true,
					ReadOnly = false,
					Source = "live",
					Partial = (list.Count >= 8192),
					CapturedUnixMilliseconds = NowMs(),
					ReadUnixMilliseconds = NowMs(),
					Message = "Current server inventory."
				}
			};
		}
		string text = ResolveKnownUid(playerOrUid);
		AdminOfflineInventoryRecord value;
		AdminInventorySnapshotRecord adminInventorySnapshotRecord;
		lock (gate)
		{
			state.OfflineInventories.TryGetValue(text, out value);
			adminInventorySnapshotRecord = (state.InventorySnapshots.TryGetValue(text, out List<AdminInventorySnapshotRecord> value2) ? (from x in value2
				orderby x.UnixMilliseconds descending, x.Id descending
				select x).FirstOrDefault() : null);
		}
		if (value != null)
		{
			AdminInventoryInspection adminInventoryInspection = new AdminInventoryInspection();
			adminInventoryInspection.Slots = value.Slots.Select(CloneInventorySlot).ToList();
			adminInventoryInspection.Status = new AdminInventoryViewStatus
			{
				Available = true,
				Source = value.Source,
				CapturedUnixMilliseconds = value.CapturedUnixMilliseconds,
				ReadUnixMilliseconds = NowMs(),
				Message = "Saved server inventory. Offline inventories are read only."
			};
			return adminInventoryInspection;
		}
		if (saved?.Inventories != null)
		{
			if (decodedInventoryCache.TryGetValue(text, out (WeakReference<SavedPlayerInventoryData>, long, AdminInventoryInspection) value3) && value3.Item1.TryGetTarget(out var target) && target == saved)
			{
				return value3.Item3;
			}
			AdminInventoryInspection adminInventoryInspection2 = new AdminInventoryInspection
			{
				Status = new AdminInventoryViewStatus
				{
					Available = true,
					Source = "world-save",
					ReadUnixMilliseconds = saved.ReadUnixMilliseconds,
					Message = "Saved world inventory. The game does not store its per-player save timestamp. Offline inventories are read only."
				}
			};
			foreach (KeyValuePair<string, byte[]> inventory in saved.Inventories)
			{
				if (inventory.Key.IndexOf("creative", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					continue;
				}
				try
				{
					TreeAttribute treeAttribute = new TreeAttribute();
					using MemoryStream input = new MemoryStream(inventory.Value, writable: false);
					using BinaryReader stream = new BinaryReader(input);
					treeAttribute.FromBytes(stream);
					int num = treeAttribute.GetInt("qslots");
					if (treeAttribute.GetTreeAttribute("slots") == null)
					{
						throw new InvalidOperationException("This inventory uses an unsupported saved layout.");
					}
					if (num < 0 || num > 8192 || adminInventoryInspection2.Slots.Count + num > 8192)
					{
						throw new InvalidOperationException("Saved inventory slot limit exceeded.");
					}
					bool flag = inventory.Key.StartsWith("backpack-", StringComparison.OrdinalIgnoreCase);
					InventoryBase inventoryBase = ((!flag) ? new InventoryGeneric(num, inventory.Key, api) : DetachedBackpackInventoryFactory.Create(inventory.Key, api));
					inventoryBase.FromTreeAttributes(treeAttribute);
					List<AdminInventorySlotView> list2 = new List<AdminInventorySlotView>();
					if (flag)
					{
						long num2 = num;
						for (int num3 = 0; num3 < inventoryBase.Count; num3++)
						{
							ItemSlot itemSlot = inventoryBase[num3];
							ItemStack itemstack = itemSlot.Itemstack;
							if (itemstack == null)
							{
								continue;
							}
							if (itemstack.Collectible == null)
							{
								list2.Add(InspectInventorySlot(inventory.Key, num3, itemSlot, itemstack, includeStackBytes: false));
								itemSlot.Itemstack = null;
								adminInventoryInspection2.Status.Partial = true;
								continue;
							}
							int num4 = itemstack.Collectible.GetCollectibleInterface<IHeldBag>()?.GetQuantitySlots(itemstack) ?? 0;
							if (num4 < 0)
							{
								throw new InvalidOperationException("Invalid saved bag size.");
							}
							num2 += num4;
							if (num2 + adminInventoryInspection2.Slots.Count > 8192)
							{
								throw new InvalidOperationException("Saved bag slot limit exceeded.");
							}
						}
						inventoryBase.AfterBlocksLoaded(api.World);
					}
					if (adminInventoryInspection2.Slots.Count + inventoryBase.Count > 8192)
					{
						throw new InvalidOperationException("Saved inventory slot limit exceeded.");
					}
					List<AdminInventorySlotView> list3 = InspectInventories(new InventoryBase[1] { inventoryBase }, includeStackBytes: false);
					foreach (AdminInventorySlotView bag in list2)
					{
						int num5 = list3.FindIndex((AdminInventorySlotView x) => x.Slot == bag.Slot);
						if (num5 >= 0)
						{
							list3[num5] = bag;
						}
					}
					if (list3.Any((AdminInventorySlotView x) => x.Code.StartsWith("unresolved:", StringComparison.Ordinal)))
					{
						adminInventoryInspection2.Status.Partial = true;
					}
					adminInventoryInspection2.Slots.AddRange(list3);
				}
				catch (Exception ex)
				{
					adminInventoryInspection2.Status.Partial = true;
					api.Logger.Warning("[ServerEssentials/Admin] Could not inspect saved inventory {0} for {1}: {2}", inventory.Key, text, ex.Message);
				}
			}
			if (adminInventoryInspection2.Status.Partial)
			{
				adminInventoryInspection2.Status.Message += " Some saved inventories could not be decoded; this view is incomplete.";
			}
			while ((decodedInventoryCache.Count >= 32 || decodedInventoryCache.Values.Sum(((WeakReference<SavedPlayerInventoryData> Data, long ReadUnixMilliseconds, AdminInventoryInspection View) x) => x.View.Slots.Count) + adminInventoryInspection2.Slots.Count > 16384) && decodedInventoryCache.Count != 0)
			{
				decodedInventoryCache.Remove(decodedInventoryCache.OrderBy((KeyValuePair<string, (WeakReference<SavedPlayerInventoryData> Data, long ReadUnixMilliseconds, AdminInventoryInspection View)> x) => x.Value.ReadUnixMilliseconds).First().Key);
			}
			decodedInventoryCache[text] = (new WeakReference<SavedPlayerInventoryData>(saved), saved.ReadUnixMilliseconds, adminInventoryInspection2);
			return adminInventoryInspection2;
		}
		if (adminInventorySnapshotRecord != null)
		{
			if (!TryGetSnapshotForUse(adminInventorySnapshotRecord, out AdminInventorySnapshotRecord snapshot, out string error))
			{
				return new AdminInventoryInspection
				{
					Status = new AdminInventoryViewStatus
					{
						Source = "snapshot",
						SnapshotId = adminInventorySnapshotRecord.Id,
						Partial = true,
						ReadUnixMilliseconds = NowMs(),
						Message = error
					}
				};
			}
			adminInventorySnapshotRecord = snapshot;
			AdminInventoryInspection adminInventoryInspection3 = new AdminInventoryInspection
			{
				Status = new AdminInventoryViewStatus
				{
					Available = true,
					Source = "snapshot",
					CapturedUnixMilliseconds = adminInventorySnapshotRecord.UnixMilliseconds,
					ReadUnixMilliseconds = NowMs(),
					SnapshotId = adminInventorySnapshotRecord.Id,
					Partial = true,
					Message = "Historical snapshot; shows saved occupied slots only. This may predate the player's last session. Offline inventories are read only."
				}
			};
			foreach (AdminInventorySnapshotSlot slot in adminInventorySnapshotRecord.Slots)
			{
				ItemStack itemStack = DeserializeSnapshotStack(slot);
				AdminInventorySlotView adminInventorySlotView = InspectInventorySlot(slot.Inventory, slot.Slot, null, itemStack);
				if (itemStack == null)
				{
					adminInventorySlotView.Empty = false;
					adminInventorySlotView.Code = slot.ItemCode;
					adminInventorySlotView.Name = slot.ItemCode + " (unresolved saved item)";
					adminInventorySlotView.StackSize = slot.StackSize;
				}
				adminInventoryInspection3.Slots.Add(adminInventorySlotView);
			}
			return adminInventoryInspection3;
		}
		return new AdminInventoryInspection
		{
			Status = new AdminInventoryViewStatus
			{
				ReadUnixMilliseconds = NowMs(),
				Message = (saved?.Error ?? "No saved inventory is available. An inventory will be captured when this player next disconnects or the world saves.")
			}
		};
	}

	public SavedPlayerInventoryData? ReadSavedInventory(string worldFilepath, string playerUid)
	{
		lock (gate)
		{
			if (state.OfflineInventories.ContainsKey(playerUid))
			{
				return null;
			}
		}
		return offlineInventoryReader.Read(worldFilepath, playerUid);
	}

	private List<AdminInventorySlotView> InspectInventories(IEnumerable<IInventory> inventories, bool includeStackBytes = true)
	{
		List<AdminInventorySlotView> list = new List<AdminInventorySlotView>();
		foreach (IInventory inventory in inventories)
		{
			if (!IsManagedPlayerInventory(inventory))
			{
				continue;
			}
			for (int i = 0; i < inventory.Count; i++)
			{
				if (list.Count >= 8192)
				{
					return list;
				}
				ItemSlot itemSlot = inventory[i];
				if (itemSlot != null)
				{
					list.Add(InspectInventorySlot(inventory.InventoryID, i, itemSlot, itemSlot.Itemstack, includeStackBytes));
				}
			}
		}
		return list;
	}

	private AdminInventorySlotView InspectInventorySlot(string inventoryId, int index, ItemSlot? slot, ItemStack? stack, bool includeStackBytes = true)
	{
		AdminInventorySlotView adminInventorySlotView = new AdminInventorySlotView
		{
			Inventory = inventoryId,
			Section = InventorySection(inventoryId, slot),
			Slot = index,
			Empty = (stack == null),
			MaxSlotStackSize = (slot?.MaxSlotStackSize ?? 0),
			StorageType = (slot?.StorageType.ToString() ?? ""),
			Fingerprint = (includeStackBytes ? Fingerprint(stack) : "read-only")
		};
		if (stack == null)
		{
			return adminInventorySlotView;
		}
		CollectibleObject collectible = stack.Collectible;
		adminInventorySlotView.Code = collectible?.Code?.ToString() ?? $"unresolved:{stack.Class}:{stack.Id}";
		try
		{
			adminInventorySlotView.Name = stack.GetName() ?? adminInventorySlotView.Code;
		}
		catch
		{
			adminInventorySlotView.Name = adminInventorySlotView.Code;
		}
		adminInventorySlotView.ItemClass = stack.Class.ToString();
		adminInventorySlotView.StackSize = stack.StackSize;
		adminInventorySlotView.MaxStackSize = collectible?.MaxStackSize ?? 0;
		if (includeStackBytes)
		{
			try
			{
				adminInventorySlotView.SerializedStack = Convert.ToBase64String(stack.ToBytes());
			}
			catch
			{
			}
		}
		if (collectible != null)
		{
			try
			{
				adminInventorySlotView.MaxDurability = collectible.GetMaxDurability(stack);
				adminInventorySlotView.Durability = collectible.GetRemainingDurability(stack);
			}
			catch
			{
			}
			try
			{
				adminInventorySlotView.Temperature = collectible.GetTemperature(api.World, stack);
			}
			catch
			{
			}
		}
		return adminInventorySlotView;
	}

	private static AdminInventorySlotView CloneInventorySlot(AdminInventorySlotView x)
	{
		return new AdminInventorySlotView
		{
			Inventory = x.Inventory,
			Section = x.Section,
			Slot = x.Slot,
			Empty = x.Empty,
			Code = x.Code,
			Name = x.Name,
			ItemClass = x.ItemClass,
			StackSize = x.StackSize,
			MaxStackSize = x.MaxStackSize,
			MaxSlotStackSize = x.MaxSlotStackSize,
			Durability = x.Durability,
			MaxDurability = x.MaxDurability,
			Temperature = x.Temperature,
			StorageType = x.StorageType,
			Fingerprint = x.Fingerprint,
			SerializedStack = x.SerializedStack
		};
	}

	private void CaptureOfflineInventory(IServerPlayer player, string source)
	{
		try
		{
			if (player.Entity == null)
			{
				return;
			}
			List<AdminInventorySlotView> list = InspectInventories(player.InventoryManager.InventoriesOrdered.Where((InventoryBase x) => x is InventoryBasePlayer), includeStackBytes: false);
			if (list.Count != 0 && list.Count < 8192)
			{
				AdminOfflineInventoryRecord value = new AdminOfflineInventoryRecord
				{
					CapturedUnixMilliseconds = NowMs(),
					Source = source,
					Slots = list
				};
				lock (gate)
				{
					state.OfflineInventories[player.PlayerUID] = value;
					storage?.RequestSave();
				}
				offlineInventoryReader.Invalidate(player.PlayerUID);
				decodedInventoryCache.Remove(player.PlayerUID);
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Admin] Could not capture inventory for {0}: {1}", player.PlayerName, ex.Message);
		}
	}

	private void OnGameWorldSave()
	{
		foreach (IServerPlayer item in OnlinePlayers())
		{
			CaptureOfflineInventory(item, "server-save-capture");
		}
		SaveState();
	}

	public List<AdminItemSearchResult> SearchItems(string search, int limit = 50)
	{
		search = (search ?? "").Trim();
		if (search.Length < 2)
		{
			return new List<AdminItemSearchResult>();
		}
		limit = Math.Clamp(limit, 1, 100);
		List<AdminItemSearchResult> list = new List<AdminItemSearchResult>();
		foreach (CollectibleObject collectible in api.World.Collectibles)
		{
			if (collectible?.Code == null || collectible.IsMissing)
			{
				continue;
			}
			string text = collectible.Code.ToString();
			string text2;
			try
			{
				text2 = new ItemStack(collectible).GetName() ?? text;
			}
			catch
			{
				text2 = text;
			}
			if (text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				list.Add(new AdminItemSearchResult
				{
					Code = text,
					Name = text2,
					ItemClass = ((collectible is Block) ? EnumItemClass.Block.ToString() : EnumItemClass.Item.ToString()),
					MaxStackSize = collectible.MaxStackSize
				});
				if (list.Count >= limit)
				{
					break;
				}
			}
		}
		return list.OrderBy((AdminItemSearchResult x) => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy((AdminItemSearchResult x) => x.Code, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public List<AdminInventorySnapshotRecord> GetInventorySnapshots(string playerOrUid, int limit = 12)
	{
		string text = FindOnlinePlayer(playerOrUid)?.PlayerUID ?? ResolveKnownUid(playerOrUid);
		if (string.IsNullOrWhiteSpace(text))
		{
			return new List<AdminInventorySnapshotRecord>();
		}
		lock (gate)
		{
			if (!state.InventorySnapshots.TryGetValue(text, out List<AdminInventorySnapshotRecord> value))
			{
				return new List<AdminInventorySnapshotRecord>();
			}
			return value.OrderByDescending((AdminInventorySnapshotRecord x) => x.UnixMilliseconds).Take(Math.Clamp(limit, 1, 50)).Select(CloneSnapshotMetadata)
				.ToList();
		}
	}

	public string CreateInventorySnapshot(string actor, string playerOrUid, string reason = "Manual snapshot", string source = "ingame")
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
		if (serverPlayer == null)
		{
			return "Player must be online.";
		}
		AdminInventorySnapshotRecord adminInventorySnapshotRecord = CaptureInventorySnapshot(serverPlayer, actor, reason);
		lock (gate)
		{
			if (!state.InventorySnapshots.TryGetValue(serverPlayer.PlayerUID, out List<AdminInventorySnapshotRecord> value))
			{
				value = (state.InventorySnapshots[serverPlayer.PlayerUID] = new List<AdminInventorySnapshotRecord>());
			}
			value.Add(adminInventorySnapshotRecord);
			while (value.Count > 12)
			{
				value.RemoveAt(0);
			}
			AddAuditUnsafe(actor, "inventory-snapshot", serverPlayer.PlayerUID, serverPlayer.PlayerName, $"Snapshot #{adminInventorySnapshotRecord.Id}: {adminInventorySnapshotRecord.OccupiedSlots} occupied slots ({reason})", source);
			SaveStateUnsafe();
		}
		return $"Inventory snapshot #{adminInventorySnapshotRecord.Id} created for {serverPlayer.PlayerName} ({adminInventorySnapshotRecord.OccupiedSlots} occupied slots).";
	}

	public string RestoreInventorySnapshot(string actor, string playerOrUid, long snapshotId, string source = "ingame")
	{
		IServerPlayer player = FindOnlinePlayer(playerOrUid);
		if (player == null)
		{
			return "Player must be online.";
		}
		AdminInventorySnapshotRecord adminInventorySnapshotRecord;
		lock (gate)
		{
			adminInventorySnapshotRecord = (state.InventorySnapshots.TryGetValue(player.PlayerUID, out List<AdminInventorySnapshotRecord> value) ? value.FirstOrDefault((AdminInventorySnapshotRecord x) => x.Id == snapshotId) : null);
		}
		if (adminInventorySnapshotRecord == null)
		{
			return "Inventory snapshot not found.";
		}
		if (!TryGetSnapshotForUse(adminInventorySnapshotRecord, out AdminInventorySnapshotRecord snapshot, out string error))
		{
			return error;
		}
		adminInventorySnapshotRecord = snapshot;
		if (!TryPrepareSnapshotRestore(player, adminInventorySnapshotRecord, out List<(IInventory, int, ItemStack)> prepared, out error))
		{
			return error;
		}
		AdminInventorySnapshotRecord before = CaptureInventorySnapshot(player, actor, $"Automatic backup before restoring snapshot #{snapshotId}");
		StoreSnapshotUnsafe(player, before, actor, source, audit: false);
		ApplyPreparedSnapshot(player, prepared);
		Audit(actor, "inventory-snapshot-restore", player.PlayerUID, player.PlayerName, $"Restored snapshot #{snapshotId}; automatic rollback snapshot #{before.Id} created", source);
		PushUndo("inventory snapshot restore for " + player.PlayerName, (string undoActor) => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
		return $"Restored inventory snapshot #{snapshotId} for {player.PlayerName}. Rollback snapshot #{before.Id} was created first.";
	}

	public List<AdminInventoryDiffRow> CompareInventorySnapshot(string playerOrUid, long snapshotId)
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
		if (serverPlayer == null)
		{
			return new List<AdminInventoryDiffRow>();
		}
		AdminInventorySnapshotRecord adminInventorySnapshotRecord;
		lock (gate)
		{
			adminInventorySnapshotRecord = (state.InventorySnapshots.TryGetValue(serverPlayer.PlayerUID, out List<AdminInventorySnapshotRecord> value) ? value.FirstOrDefault((AdminInventorySnapshotRecord x) => x.Id == snapshotId) : null);
		}
		if (adminInventorySnapshotRecord == null)
		{
			return new List<AdminInventoryDiffRow>();
		}
		if (!TryGetSnapshotForUse(adminInventorySnapshotRecord, out AdminInventorySnapshotRecord snapshot, out string error))
		{
			throw new InvalidOperationException(error);
		}
		adminInventorySnapshotRecord = snapshot;
		Dictionary<string, AdminInventorySnapshotSlot> dictionary = adminInventorySnapshotRecord.Slots.ToDictionary((AdminInventorySnapshotSlot x) => $"{x.Inventory}\n{x.Slot}", StringComparer.OrdinalIgnoreCase);
		List<AdminInventoryDiffRow> list = new List<AdminInventoryDiffRow>();
		foreach (InventoryBase item in serverPlayer.InventoryManager.InventoriesOrdered)
		{
			if (!IsManagedPlayerInventory(item))
			{
				continue;
			}
			for (int num = 0; num < item.Count; num++)
			{
				string key = $"{item.InventoryID}\n{num}";
				dictionary.TryGetValue(key, out var value2);
				ItemStack itemStack = item[num]?.Itemstack;
				ItemStack itemStack2 = DeserializeSnapshotStack(value2);
				string a = Fingerprint(itemStack2);
				string b = Fingerprint(itemStack);
				if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
				{
					list.Add(new AdminInventoryDiffRow
					{
						Inventory = item.InventoryID,
						Slot = num,
						SnapshotItem = DescribeStack(itemStack2),
						CurrentItem = DescribeStack(itemStack),
						Change = ((itemStack2 == null) ? "Added" : ((itemStack == null) ? "Removed" : "Changed"))
					});
				}
			}
		}
		return list;
	}

	public string RemoveInventorySlot(string actor, string playerOrUid, string inventoryId, int slotIndex, string source = "ingame", string expectedFingerprint = "")
	{
		IServerPlayer player = FindOnlinePlayer(playerOrUid);
		if (player == null)
		{
			return "Player must be online.";
		}
		IInventory inventory = FindInventory(player, inventoryId);
		if (inventory == null)
		{
			return "Inventory not found.";
		}
		if (slotIndex < 0 || slotIndex >= inventory.Count)
		{
			return "Invalid inventory slot.";
		}
		ItemSlot itemSlot = inventory[slotIndex];
		ItemStack itemStack = itemSlot?.Itemstack;
		if (itemSlot == null || itemStack == null)
		{
			return "That inventory slot is empty.";
		}
		if (!FingerprintMatches(itemStack, expectedFingerprint))
		{
			return "Inventory changed while you were editing. Refresh and try again.";
		}
		AdminInventorySnapshotRecord adminInventorySnapshotRecord = CaptureInventorySnapshot(player, actor, $"Automatic backup before removing {inventoryId}[{slotIndex}]");
		StoreSnapshotUnsafe(player, adminInventorySnapshotRecord, actor, source, audit: false);
		ItemStack backup = itemStack.Clone();
		string value = itemStack.Collectible?.Code?.ToString() ?? "unknown";
		int stackSize = itemStack.StackSize;
		itemSlot.Itemstack = null;
		itemSlot.MarkDirty();
		Audit(actor, "inventory-remove", player.PlayerUID, player.PlayerName, $"Removed {stackSize}x {value} from {inventory.InventoryID}[{slotIndex}]; snapshot #{adminInventorySnapshotRecord.Id}", source);
		PushUndo($"inventory removal {player.PlayerName} {inventory.InventoryID}[{slotIndex}]", (string undoActor) => RestoreInventorySlot(undoActor, player.PlayerUID, inventory.InventoryID, slotIndex, backup));
		return $"Removed {stackSize}x {value} from {player.PlayerName} {inventory.InventoryID}[{slotIndex}]. Snapshot #{adminInventorySnapshotRecord.Id} created.";
	}

	public string SetInventoryQuantity(string actor, string playerOrUid, string inventoryId, int slotIndex, int quantity, string expectedFingerprint = "", string source = "ingame")
	{
		IServerPlayer player = FindOnlinePlayer(playerOrUid);
		if (player == null)
		{
			return "Player must be online.";
		}
		IInventory inventory = FindInventory(player, inventoryId);
		if (inventory == null || slotIndex < 0 || slotIndex >= inventory.Count)
		{
			return "Inventory slot not found.";
		}
		ItemSlot itemSlot = inventory[slotIndex];
		ItemStack itemStack = itemSlot?.Itemstack;
		if (itemSlot == null || itemStack == null)
		{
			return "That inventory slot is empty.";
		}
		if (!FingerprintMatches(itemStack, expectedFingerprint))
		{
			return "Inventory changed while you were editing. Refresh and try again.";
		}
		int max = Math.Max(1, Math.Min(itemSlot.MaxSlotStackSize, itemStack.Collectible?.MaxStackSize ?? itemSlot.MaxSlotStackSize));
		quantity = Math.Clamp(quantity, 0, max);
		AdminInventorySnapshotRecord before = CaptureInventorySnapshot(player, actor, $"Automatic backup before quantity edit {inventoryId}[{slotIndex}]");
		StoreSnapshotUnsafe(player, before, actor, source, audit: false);
		int stackSize = itemStack.StackSize;
		string value = itemStack.Collectible?.Code?.ToString() ?? "unknown";
		if (quantity == 0)
		{
			itemSlot.Itemstack = null;
		}
		else
		{
			itemStack.StackSize = quantity;
		}
		itemSlot.MarkDirty();
		Audit(actor, "inventory-quantity", player.PlayerUID, player.PlayerName, $"Changed {value} in {inventoryId}[{slotIndex}] from {stackSize} to {quantity}; snapshot #{before.Id}", source);
		PushUndo($"inventory quantity edit {player.PlayerName} {inventoryId}[{slotIndex}]", (string undoActor) => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
		return $"Changed {value} quantity from {stackSize} to {quantity}. Snapshot #{before.Id} created.";
	}

	public string MoveInventorySlot(string actor, string playerOrUid, string fromInventoryId, int fromSlotIndex, string toInventoryId, int toSlotIndex, string expectedFromFingerprint = "", string expectedToFingerprint = "", string source = "ingame")
	{
		IServerPlayer player = FindOnlinePlayer(playerOrUid);
		if (player == null)
		{
			return "Player must be online.";
		}
		IInventory inventory = FindInventory(player, fromInventoryId);
		IInventory inventory2 = FindInventory(player, toInventoryId);
		if (inventory == null || inventory2 == null)
		{
			return "Inventory not found.";
		}
		if (fromSlotIndex < 0 || fromSlotIndex >= inventory.Count || toSlotIndex < 0 || toSlotIndex >= inventory2.Count)
		{
			return "Invalid inventory slot.";
		}
		ItemSlot itemSlot = inventory[fromSlotIndex];
		ItemSlot itemSlot2 = inventory2[toSlotIndex];
		if (itemSlot?.Itemstack == null || itemSlot2 == null)
		{
			return "Source slot is empty.";
		}
		if (!FingerprintMatches(itemSlot.Itemstack, expectedFromFingerprint) || !FingerprintMatches(itemSlot2.Itemstack, expectedToFingerprint))
		{
			return "Inventory changed while you were editing. Refresh and try again.";
		}
		if (itemSlot == itemSlot2)
		{
			return "Source and destination are the same slot.";
		}
		if (!itemSlot2.CanHold(itemSlot))
		{
			return "Destination slot cannot hold that item.";
		}
		if (itemSlot2.Itemstack != null && !itemSlot.CanHold(itemSlot2))
		{
			return "Source slot cannot hold the destination item, so the swap was cancelled.";
		}
		AdminInventorySnapshotRecord before = CaptureInventorySnapshot(player, actor, $"Automatic backup before move {fromInventoryId}[{fromSlotIndex}] -> {toInventoryId}[{toSlotIndex}]");
		StoreSnapshotUnsafe(player, before, actor, source, audit: false);
		ItemStack itemstack = itemSlot.Itemstack;
		ItemStack itemstack2 = itemSlot2.Itemstack;
		itemSlot.Itemstack = itemstack2;
		itemSlot2.Itemstack = itemstack;
		itemSlot.MarkDirty();
		itemSlot2.MarkDirty();
		Audit(actor, "inventory-move", player.PlayerUID, player.PlayerName, $"Swapped {fromInventoryId}[{fromSlotIndex}] with {toInventoryId}[{toSlotIndex}]; snapshot #{before.Id}", source);
		PushUndo("inventory move for " + player.PlayerName, (string undoActor) => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
		return $"Moved/swapped inventory slots. Snapshot #{before.Id} created.";
	}

	public string GiveInventoryItem(string actor, string playerOrUid, string code, int quantity, string inventoryId = "", int slotIndex = -1, string expectedFingerprint = "", string source = "ingame")
	{
		IServerPlayer player = FindOnlinePlayer(playerOrUid);
		if (player == null)
		{
			return "Player must be online.";
		}
		CollectibleObject collectibleObject = FindCollectible(code);
		if (collectibleObject == null)
		{
			return "Item/block code not found.";
		}
		quantity = Math.Clamp(quantity, 1, Math.Max(1, collectibleObject.MaxStackSize));
		ItemStack itemStack = new ItemStack(collectibleObject, quantity);
		ItemSlot itemSlot = null;
		if (!string.IsNullOrWhiteSpace(inventoryId) && slotIndex >= 0)
		{
			IInventory inventory = FindInventory(player, inventoryId);
			if (inventory == null || slotIndex < 0 || slotIndex >= inventory.Count)
			{
				return "Target inventory slot not found.";
			}
			itemSlot = inventory[slotIndex];
			if (itemSlot == null)
			{
				return "Target inventory slot not found.";
			}
			if (!FingerprintMatches(itemSlot.Itemstack, expectedFingerprint))
			{
				return "Inventory changed while you were editing. Refresh and try again.";
			}
			if (itemSlot.Itemstack != null)
			{
				return "Target slot is occupied. Use Replace if you intend to overwrite it.";
			}
			if (!itemSlot.CanHold(new DummySlot(itemStack)))
			{
				return "Target slot cannot hold that item.";
			}
		}
		AdminInventorySnapshotRecord before = CaptureInventorySnapshot(player, actor, $"Automatic backup before giving {quantity}x {code}");
		if (itemSlot != null)
		{
			StoreSnapshotUnsafe(player, before, actor, source, audit: false);
			itemSlot.Itemstack = itemStack;
			itemSlot.MarkDirty();
		}
		else
		{
			if (!player.Entity.TryGiveItemStack(itemStack))
			{
				return "No suitable free inventory space was available.";
			}
			StoreSnapshotUnsafe(player, before, actor, source, audit: false);
		}
		Audit(actor, "inventory-give", player.PlayerUID, player.PlayerName, $"Gave {quantity}x {code}; snapshot #{before.Id}", source);
		PushUndo("inventory give to " + player.PlayerName, (string undoActor) => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
		return $"Gave {quantity}x {code} to {player.PlayerName}. Snapshot #{before.Id} created.";
	}

	public string ReplaceInventorySlot(string actor, string playerOrUid, string inventoryId, int slotIndex, string code, int quantity, string expectedFingerprint = "", string source = "ingame")
	{
		IServerPlayer player = FindOnlinePlayer(playerOrUid);
		if (player == null)
		{
			return "Player must be online.";
		}
		IInventory inventory = FindInventory(player, inventoryId);
		if (inventory == null || slotIndex < 0 || slotIndex >= inventory.Count)
		{
			return "Target inventory slot not found.";
		}
		ItemSlot itemSlot = inventory[slotIndex];
		if (itemSlot == null)
		{
			return "Target inventory slot is unavailable.";
		}
		if (!FingerprintMatches(itemSlot.Itemstack, expectedFingerprint))
		{
			return "Inventory changed while you were editing. Refresh and try again.";
		}
		CollectibleObject collectibleObject = FindCollectible(code);
		if (collectibleObject == null)
		{
			return "Item/block code not found.";
		}
		quantity = Math.Clamp(quantity, 1, Math.Max(1, Math.Min(itemSlot.MaxSlotStackSize, collectibleObject.MaxStackSize)));
		ItemStack itemStack = new ItemStack(collectibleObject, quantity);
		if (!itemSlot.CanHold(new DummySlot(itemStack)))
		{
			return "Target slot cannot hold that item.";
		}
		AdminInventorySnapshotRecord before = CaptureInventorySnapshot(player, actor, $"Automatic backup before replacing {inventoryId}[{slotIndex}]");
		StoreSnapshotUnsafe(player, before, actor, source, audit: false);
		string value = itemSlot.Itemstack?.Collectible?.Code?.ToString() ?? "empty";
		itemSlot.Itemstack = itemStack;
		itemSlot.MarkDirty();
		Audit(actor, "inventory-replace", player.PlayerUID, player.PlayerName, $"Replaced {value} with {quantity}x {code} in {inventoryId}[{slotIndex}]; snapshot #{before.Id}", source);
		PushUndo("inventory replace for " + player.PlayerName, (string undoActor) => RestoreInventorySnapshot(undoActor, player.PlayerUID, before.Id, "undo"));
		return $"Replaced {inventoryId}[{slotIndex}] with {quantity}x {code}. Snapshot #{before.Id} created.";
	}

	private AdminInventorySnapshotRecord CaptureInventorySnapshot(IServerPlayer player, string actor, string reason)
	{
		AdminInventorySnapshotRecord adminInventorySnapshotRecord = new AdminInventorySnapshotRecord
		{
			Id = NextIdThreadSafe(),
			UnixMilliseconds = NowMs(),
			PlayerUid = player.PlayerUID,
			PlayerName = player.PlayerName,
			CreatedBy = actor,
			Reason = reason
		};
		foreach (InventoryBase item in player.InventoryManager.InventoriesOrdered)
		{
			if (!IsManagedPlayerInventory(item))
			{
				continue;
			}
			for (int i = 0; i < item.Count; i++)
			{
				ItemStack itemStack = item[i]?.Itemstack;
				if (itemStack != null)
				{
					adminInventorySnapshotRecord.Slots.Add(new AdminInventorySnapshotSlot
					{
						Inventory = item.InventoryID,
						Slot = i,
						StackBytes = itemStack.ToBytes(),
						ItemCode = (itemStack.Collectible?.Code?.ToString() ?? ""),
						StackSize = itemStack.StackSize
					});
				}
			}
		}
		adminInventorySnapshotRecord.OccupiedSlots = adminInventorySnapshotRecord.Slots.Count;
		return adminInventorySnapshotRecord;
	}

	private void StoreSnapshotUnsafe(IServerPlayer player, AdminInventorySnapshotRecord snapshot, string actor, string source, bool audit)
	{
		lock (gate)
		{
			if (!state.InventorySnapshots.TryGetValue(player.PlayerUID, out List<AdminInventorySnapshotRecord> value))
			{
				value = (state.InventorySnapshots[player.PlayerUID] = new List<AdminInventorySnapshotRecord>());
			}
			value.Add(snapshot);
			while (value.Count > 12)
			{
				value.RemoveAt(0);
			}
			if (audit)
			{
				AddAuditUnsafe(actor, "inventory-snapshot", player.PlayerUID, player.PlayerName, $"Snapshot #{snapshot.Id}: {snapshot.OccupiedSlots} occupied slots ({snapshot.Reason})", source);
			}
			SaveStateUnsafe();
		}
	}

	private bool TryGetSnapshotForUse(AdminInventorySnapshotRecord header, out AdminInventorySnapshotRecord? snapshot, out string error)
	{
		lock (gate)
		{
			if (header.SlotsPayloadLoaded)
			{
				snapshot = CloneSnapshotMetadata(header);
				snapshot.Slots = header.Slots.ToList();
				snapshot.SlotsPayloadLoaded = true;
				error = "";
				return true;
			}
		}
		snapshot = null;
		error = "Inventory snapshot storage is unavailable. No inventory was changed.";
		return storage?.TryReadInventorySnapshot(header, out snapshot, out error) ?? false;
	}

	private void ReleasePersistedSnapshotPayloads(long committedHigh)
	{
		lock (gate)
		{
			foreach (List<AdminInventorySnapshotRecord> value in state.InventorySnapshots.Values)
			{
				foreach (AdminInventorySnapshotRecord item in value)
				{
					if (item.Id <= committedHigh && item.SlotsPayloadLoaded)
					{
						item.Slots = new List<AdminInventorySnapshotSlot>();
						item.SlotsPayloadLoaded = false;
					}
				}
			}
		}
	}

	private bool TryPrepareSnapshotRestore(IServerPlayer player, AdminInventorySnapshotRecord snapshot, out List<(IInventory Inventory, int Index, ItemStack Stack)> prepared, out string error)
	{
		prepared = new List<(IInventory, int, ItemStack)>();
		error = "Inventory snapshot is incomplete or incompatible with the current inventories. No inventory was changed.";
		if (!snapshot.SlotsPayloadLoaded || snapshot.Slots.Count != snapshot.OccupiedSlots)
		{
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (AdminInventorySnapshotSlot item in snapshot.Slots.OrderBy((AdminInventorySnapshotSlot x) => x.Inventory, StringComparer.OrdinalIgnoreCase).ThenBy((AdminInventorySnapshotSlot x) => x.Slot))
		{
			IInventory inventory = FindInventory(player, item.Inventory);
			if (inventory == null || item.Slot < 0)
			{
				return false;
			}
			ItemStack itemStack = DeserializeSnapshotStack(item);
			if (itemStack == null || itemStack.StackSize <= 0 || !hashSet.Add(item.Inventory + "\n" + item.Slot))
			{
				return false;
			}
			prepared.Add((inventory, item.Slot, itemStack));
		}
		foreach (IGrouping<IInventory, (IInventory, int, ItemStack)> item2 in from x in prepared
			group x by x.Inventory)
		{
			IInventory inventory2 = item2.Key;
			if (inventory2.InventoryID.StartsWith("backpack-", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					InventoryBase detached = DetachedBackpackInventoryFactory.Create(inventory2.InventoryID, api);
					int count = detached.Count;
					foreach (var item3 in item2)
					{
						if (item3.Item2 < count)
						{
							detached[item3.Item2].Itemstack = item3.Item3.Clone();
						}
					}
					detached.AfterBlocksLoaded(api.World);
					if (item2.Any(((IInventory Inventory, int Index, ItemStack Stack) x) => x.Index >= detached.Count || detached[x.Index] == null))
					{
						return false;
					}
				}
				catch
				{
					return false;
				}
			}
			else if (item2.Any(((IInventory Inventory, int Index, ItemStack Stack) x) => x.Index >= inventory2.Count || inventory2[x.Index] == null))
			{
				return false;
			}
		}
		error = "";
		return true;
	}

	private void ApplyPreparedSnapshot(IServerPlayer player, List<(IInventory Inventory, int Index, ItemStack Stack)> prepared)
	{
		foreach (InventoryBase item4 in player.InventoryManager.InventoriesOrdered)
		{
			if (!IsManagedPlayerInventory(item4))
			{
				continue;
			}
			for (int num = item4.Count - 1; num >= 0; num--)
			{
				ItemSlot itemSlot = item4[num];
				if (itemSlot != null)
				{
					itemSlot.Itemstack = null;
					itemSlot.MarkDirty();
				}
			}
		}
		foreach (var item5 in prepared)
		{
			IInventory item = item5.Inventory;
			int item2 = item5.Index;
			ItemStack item3 = item5.Stack;
			ItemSlot itemSlot2 = item[item2];
			if (itemSlot2 == null)
			{
				throw new InvalidOperationException("Inventory layout changed while restoring the snapshot.");
			}
			itemSlot2.Itemstack = item3;
			itemSlot2.MarkDirty();
		}
	}

	private ItemStack? DeserializeSnapshotStack(AdminInventorySnapshotSlot? saved)
	{
		if (saved == null)
		{
			return null;
		}
		try
		{
			byte[] array = saved.StackBytes ?? Array.Empty<byte>();
			if (array.Length == 0 && !string.IsNullOrWhiteSpace(saved.StackBase64))
			{
				array = Convert.FromBase64String(saved.StackBase64);
			}
			if (array.Length == 0)
			{
				return null;
			}
			ItemStack itemStack = new ItemStack(array);
			return itemStack.ResolveBlockOrItem(api.World) ? itemStack : null;
		}
		catch
		{
			return null;
		}
	}

	private static string DescribeStack(ItemStack? stack)
	{
		if (stack == null)
		{
			return "Empty";
		}
		string text = stack.Collectible?.Code?.ToString() ?? "unknown";
		string value;
		try
		{
			value = stack.GetName() ?? text;
		}
		catch
		{
			value = text;
		}
		return $"{stack.StackSize}x {value} ({text})";
	}

	private IInventory? FindInventory(IServerPlayer player, string inventoryId)
	{
		return player.InventoryManager.InventoriesOrdered.FirstOrDefault((InventoryBase inv) => IsManagedPlayerInventory(inv) && string.Equals(inv.InventoryID, inventoryId, StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsManagedPlayerInventory(IInventory? inventory)
	{
		if (inventory == null)
		{
			return false;
		}
		string text = inventory.InventoryID ?? "";
		string text2 = inventory.GetType().Name ?? "";
		if (text.IndexOf("creative", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		if (text2.IndexOf("creative", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return false;
		}
		return true;
	}

	private CollectibleObject? FindCollectible(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return null;
		}
		if (!code.Contains(':'))
		{
			code = "game:" + code;
		}
		AssetLocation assetLocation = new AssetLocation(code);
		return (CollectibleObject?)(((object)api.World.GetItem(assetLocation)) ?? ((object)api.World.GetBlock(assetLocation)));
	}

	private static string InventorySection(string inventoryId, ItemSlot? slot = null)
	{
		if (slot is ItemSlotCharacter || slot is ItemSlotOffhand)
		{
			return "Equipment";
		}
		string text = (inventoryId ?? "").ToLowerInvariant();
		if (text.Contains("hotbar"))
		{
			return "Hotbar";
		}
		if (text.Contains("backpack"))
		{
			return "Backpack";
		}
		if (text.Contains("character") || text.Contains("gear") || text.Contains("armor"))
		{
			return "Equipment";
		}
		if (text.Contains("craft"))
		{
			return "Crafting";
		}
		if (text.Contains("mouse"))
		{
			return "Cursor";
		}
		return "Other";
	}

	private static string Fingerprint(ItemStack? stack)
	{
		if (stack == null)
		{
			return "empty";
		}
		try
		{
			return Convert.ToHexString(SHA256.HashData(stack.ToBytes()));
		}
		catch
		{
			return $"{stack.Class}:{stack.Id}:{stack.StackSize}";
		}
	}

	private static bool FingerprintMatches(ItemStack? stack, string expected)
	{
		return string.IsNullOrWhiteSpace(expected) || string.Equals(Fingerprint(stack), expected, StringComparison.OrdinalIgnoreCase);
	}

	private long NextIdThreadSafe()
	{
		lock (gate)
		{
			return NextIdUnsafe();
		}
	}

	private static AdminInventorySnapshotRecord CloneSnapshotMetadata(AdminInventorySnapshotRecord x)
	{
		return new AdminInventorySnapshotRecord
		{
			Id = x.Id,
			UnixMilliseconds = x.UnixMilliseconds,
			PlayerUid = x.PlayerUid,
			PlayerName = x.PlayerName,
			CreatedBy = x.CreatedBy,
			Reason = x.Reason,
			OccupiedSlots = x.OccupiedSlots,
			Slots = new List<AdminInventorySnapshotSlot>(),
			SlotsPayloadLoaded = false
		};
	}

	public string Warn(string actor, string playerOrUid, string reason, string source = "ingame")
	{
		if (!TryResolveTarget(playerOrUid, out string uid, out string name))
		{
			return "Player not found.";
		}
		if (string.IsNullOrWhiteSpace(reason))
		{
			return "A warning reason is required.";
		}
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			AdminWarningRecord warning = new AdminWarningRecord
			{
				Id = NextIdUnsafe(),
				UnixMilliseconds = NowMs(),
				IssuedBy = actor,
				Reason = reason.Trim()
			};
			orCreateUnsafe.Warnings.Add(warning);
			AddAuditUnsafe(actor, "warn", uid, name, $"Warning #{warning.Id}: {warning.Reason}", source);
			SaveStateUnsafe();
			PushUndo($"warning #{warning.Id} on {name}", (string undoActor) => RemoveWarning(undoActor, uid, warning.Id, "undo"));
			FindOnlinePlayer(uid)?.SendMessage(GlobalConstants.GeneralChatGroup, "You received a staff warning: " + warning.Reason, EnumChatType.Notification);
			return $"Warning #{warning.Id} issued to {name}.";
		}
	}

	public string RemoveWarning(string actor, string playerOrUid, long warningId, string source = "ingame")
	{
		if (!TryResolveTarget(playerOrUid, out string uid, out string name))
		{
			return "Player not found.";
		}
		lock (gate)
		{
			if (!state.Players.TryGetValue(uid, out PlayerAdminRecord value))
			{
				return "No warning history.";
			}
			AdminWarningRecord adminWarningRecord = value.Warnings.FirstOrDefault((AdminWarningRecord x) => x.Id == warningId && !x.Removed);
			if (adminWarningRecord == null)
			{
				return "Active warning not found.";
			}
			adminWarningRecord.Removed = true;
			adminWarningRecord.RemovedBy = actor;
			adminWarningRecord.RemovedUnixMilliseconds = NowMs();
			AddAuditUnsafe(actor, "unwarn", uid, name, $"Removed warning #{warningId}", source);
			SaveStateUnsafe();
			return $"Warning #{warningId} removed from {name}.";
		}
	}

	public string Mute(string actor, string playerOrUid, string duration, string reason, string source = "ingame")
	{
		if (!TryResolveTarget(playerOrUid, out string uid, out string name))
		{
			return "Player not found.";
		}
		if (!TryParseDuration(duration, out var milliseconds, out var permanent))
		{
			return "Invalid duration. Use 30m, 6h, 3d, 2w, or permanent.";
		}
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			bool oldPermanent = orCreateUnsafe.PermanentMute;
			long oldUntil = orCreateUnsafe.MuteUntilUnixMilliseconds;
			string oldReason = orCreateUnsafe.MuteReason;
			string oldBy = orCreateUnsafe.MutedBy;
			orCreateUnsafe.PermanentMute = permanent;
			orCreateUnsafe.MuteUntilUnixMilliseconds = (permanent ? 0 : DeadlineAfterMilliseconds(milliseconds));
			orCreateUnsafe.MuteReason = (string.IsNullOrWhiteSpace(reason) ? "Muted by staff" : reason.Trim());
			orCreateUnsafe.MutedBy = actor;
			AddAuditUnsafe(actor, "mute", uid, name, duration + ": " + orCreateUnsafe.MuteReason, source);
			SaveStateUnsafe();
			PushUndo("mute on " + name, (string undoActor) => RestoreMute(undoActor, uid, name, oldPermanent, oldUntil, oldReason, oldBy));
			FindOnlinePlayer(uid)?.SendMessage(GlobalConstants.GeneralChatGroup, "You have been muted. Reason: " + orCreateUnsafe.MuteReason, EnumChatType.Notification);
			return name + " muted for " + duration + ".";
		}
	}

	public string Unmute(string actor, string playerOrUid, string source = "ingame")
	{
		if (!TryResolveTarget(playerOrUid, out string uid, out string name))
		{
			return "Player not found.";
		}
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			orCreateUnsafe.PermanentMute = false;
			orCreateUnsafe.MuteUntilUnixMilliseconds = 0L;
			orCreateUnsafe.MuteReason = "";
			orCreateUnsafe.MutedBy = "";
			AddAuditUnsafe(actor, "unmute", uid, name, "Mute cleared", source);
			SaveStateUnsafe();
			return name + " unmuted.";
		}
	}

	public string TempBan(string actor, string playerOrUid, string duration, string reason, string source = "ingame")
	{
		if (!TryResolveTarget(playerOrUid, out string uid, out string name))
		{
			return "Player not found.";
		}
		if (!TryParseDuration(duration, out var milliseconds, out var permanent) | permanent)
		{
			return "Temporary bans require a duration such as 30m, 6h, 3d, or 2w.";
		}
		string text = (string.IsNullOrWhiteSpace(reason) ? "Temporarily banned by staff" : reason.Trim());
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			long oldUntil = orCreateUnsafe.BanUntilUnixMilliseconds;
			string oldReason = orCreateUnsafe.BanReason;
			string oldBy = orCreateUnsafe.BannedBy;
			orCreateUnsafe.BanUntilUnixMilliseconds = DeadlineAfterMilliseconds(milliseconds);
			orCreateUnsafe.BanReason = text;
			orCreateUnsafe.BannedBy = actor;
			AddAuditUnsafe(actor, "tempban", uid, name, duration + ": " + orCreateUnsafe.BanReason, source);
			SaveStateUnsafe();
			PushUndo("temporary ban on " + name, (string undoActor) => RestoreBan(undoActor, uid, name, oldUntil, oldReason, oldBy));
		}
		IServerPlayer serverPlayer = FindOnlinePlayer(uid);
		if (serverPlayer != null)
		{
			DisconnectWithNotice(serverPlayer, "You have been temporarily banned for " + duration + ". Reason: " + text, "Temporarily banned for " + duration + ". Reason: " + text);
		}
		return name + " temporarily banned for " + duration + ".";
	}

	public string Unban(string actor, string playerOrUid, string source = "ingame")
	{
		if (!TryResolveTarget(playerOrUid, out string uid, out string name))
		{
			return "Player not found.";
		}
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			orCreateUnsafe.BanUntilUnixMilliseconds = 0L;
			orCreateUnsafe.BanReason = "";
			orCreateUnsafe.BannedBy = "";
			AddAuditUnsafe(actor, "unban", uid, name, "Temporary ban cleared", source);
			SaveStateUnsafe();
			return "Temporary ban cleared for " + name + ".";
		}
	}

	public string AddNote(string actor, string playerOrUid, string text, string source = "ingame")
	{
		if (!TryResolveTarget(playerOrUid, out string uid, out string name))
		{
			return "Player not found.";
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			return "Note text is required.";
		}
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			AdminNoteRecord note = new AdminNoteRecord
			{
				Id = NextIdUnsafe(),
				UnixMilliseconds = NowMs(),
				Author = actor,
				Text = text.Trim()
			};
			orCreateUnsafe.Notes.Add(note);
			AddAuditUnsafe(actor, "note", uid, name, $"Note #{note.Id} added", source);
			SaveStateUnsafe();
			PushUndo($"staff note #{note.Id} on {name}", (string undoActor) => RemoveNote(undoActor, uid, note.Id, "undo"));
			return $"Staff note #{note.Id} added to {name}.";
		}
	}

	public string RemoveNote(string actor, string playerOrUid, long noteId, string source = "ingame")
	{
		if (!TryResolveTarget(playerOrUid, out string uid, out string name))
		{
			return "Player not found.";
		}
		lock (gate)
		{
			if (!state.Players.TryGetValue(uid, out PlayerAdminRecord value))
			{
				return "No notes found.";
			}
			AdminNoteRecord adminNoteRecord = value.Notes.FirstOrDefault((AdminNoteRecord x) => x.Id == noteId && !x.Removed);
			if (adminNoteRecord == null)
			{
				return "Active note not found.";
			}
			adminNoteRecord.Removed = true;
			AddAuditUnsafe(actor, "note-remove", uid, name, $"Note #{noteId} removed", source);
			SaveStateUnsafe();
			return $"Staff note #{noteId} removed.";
		}
	}

	public string Freeze(string actor, string playerOrUid, string duration, string reason, string source = "ingame")
	{
		IServerPlayer player = FindOnlinePlayer(playerOrUid);
		if (player?.Entity == null)
		{
			return "Player must be online to freeze.";
		}
		long milliseconds = 0L;
		bool permanent = true;
		if (!string.IsNullOrWhiteSpace(duration) && duration != "permanent" && !TryParseDuration(duration, out milliseconds, out permanent))
		{
			return "Invalid duration.";
		}
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(player.Entity);
		if (runtimeEntityPos == null)
		{
			return "Unable to read player position.";
		}
		string text = (string.IsNullOrWhiteSpace(reason) ? "Frozen by staff" : reason.Trim());
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(player.PlayerUID, player.PlayerName);
			bool oldFrozen = orCreateUnsafe.Frozen;
			long oldUntil = orCreateUnsafe.FreezeUntilUnixMilliseconds;
			string oldBy = orCreateUnsafe.FrozenBy;
			string oldReason = orCreateUnsafe.FreezeReason;
			EntityPos oldPos = (frozenPositions.TryGetValue(player.PlayerUID, out EntityPos value) ? value.Copy() : null);
			orCreateUnsafe.Frozen = true;
			orCreateUnsafe.FreezeUntilUnixMilliseconds = (permanent ? 0 : DeadlineAfterMilliseconds(milliseconds));
			orCreateUnsafe.FrozenBy = actor;
			orCreateUnsafe.FreezeReason = text;
			frozenPositions[player.PlayerUID] = runtimeEntityPos.Copy();
			AddAuditUnsafe(actor, "freeze", player.PlayerUID, player.PlayerName, duration + ": " + orCreateUnsafe.FreezeReason, source);
			SaveStateUnsafe();
			PushUndo("freeze on " + player.PlayerName, (string undoActor) => RestoreFreeze(undoActor, player.PlayerUID, player.PlayerName, oldFrozen, oldUntil, oldBy, oldReason, oldPos));
		}
		RefreshFreezeTickListener();
		string text2 = (permanent ? "until released" : duration);
		player.SendMessage(GlobalConstants.GeneralChatGroup, "You have been frozen for " + text2 + ". Reason: " + text, EnumChatType.Notification);
		return player.PlayerName + " frozen for " + text2 + ".";
	}

	public string Unfreeze(string actor, string playerOrUid, string source = "ingame")
	{
		if (!TryResolveTarget(playerOrUid, out string uid, out string name))
		{
			return "Player not found.";
		}
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			orCreateUnsafe.Frozen = false;
			orCreateUnsafe.FreezeUntilUnixMilliseconds = 0L;
			orCreateUnsafe.FrozenBy = "";
			orCreateUnsafe.FreezeReason = "";
			frozenPositions.Remove(uid);
			AddAuditUnsafe(actor, "unfreeze", uid, name, "Freeze cleared", source);
			SaveStateUnsafe();
		}
		RefreshFreezeTickListener();
		return name + " unfrozen.";
	}

	public string Kick(string actor, string playerOrUid, string reason, string source = "web")
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
		if (serverPlayer == null)
		{
			return "Player must be online.";
		}
		string text = (string.IsNullOrWhiteSpace(reason) ? "Kicked by staff" : reason.Trim());
		Audit(actor, "kick", serverPlayer.PlayerUID, serverPlayer.PlayerName, text, source);
		DisconnectWithNotice(serverPlayer, "You are being kicked from the server. Reason: " + text, text);
		return serverPlayer.PlayerName + " kick scheduled with player-facing reason.";
	}

	private void DisconnectWithNotice(IServerPlayer player, string notice, string disconnectReason, int delayMs = 2200)
	{
		string uid = player.PlayerUID;
		try
		{
			player.SendIngameError("newfies-server-disconnect", notice);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Admin] Could not send pre-disconnect ingame error to {0}: {1}", player.PlayerName, ex.Message);
		}
		try
		{
			player.SendMessage(GlobalConstants.GeneralChatGroup, notice, EnumChatType.Notification);
		}
		catch (Exception ex2)
		{
			api.Logger.Warning("[ServerEssentials/Admin] Could not send pre-disconnect notice to {0}: {1}", player.PlayerName, ex2.Message);
		}
		api.Event.RegisterCallback((float _) =>
		{
			try
			{
				IServerPlayer serverPlayer = FindOnlinePlayer(uid);
				if (serverPlayer != null && !PlayerDisconnectCompat.DisconnectWithReason(serverPlayer, disconnectReason, notice, out string error))
				{
					api.Logger.Warning("[ServerEssentials/Admin] Correct player-facing disconnect path unavailable for {0}: {1}. Falling back to API disconnect after sending the UI reason.", uid, error);
					serverPlayer.Disconnect(disconnectReason);
				}
			}
			catch (Exception ex3)
			{
				api.Logger.Warning("[ServerEssentials/Admin] Delayed disconnect failed for {0}: {1}", uid, ex3.Message);
			}
		}, Math.Clamp(delayMs, 250, 3000));
	}

	public string Heal(string actor, string playerOrUid, string source = "ingame")
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
		if (serverPlayer?.Entity == null)
		{
			return "Player must be online.";
		}
		EntityBehaviorHealth behavior = serverPlayer.Entity.GetBehavior<EntityBehaviorHealth>();
		if (behavior == null)
		{
			return "Player has no health behavior.";
		}
		behavior.Health = behavior.MaxHealth;
		behavior.MarkDirty();
		Audit(actor, "heal", serverPlayer.PlayerUID, serverPlayer.PlayerName, "Restored to full health", source);
		return serverPlayer.PlayerName + " healed to full health.";
	}

	public string Feed(string actor, string playerOrUid, string source = "ingame")
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
		if (serverPlayer?.Entity == null)
		{
			return "Player must be online.";
		}
		EntityBehaviorHunger behavior = serverPlayer.Entity.GetBehavior<EntityBehaviorHunger>();
		if (behavior == null)
		{
			return "Player has no hunger behavior.";
		}
		serverPlayer.Entity.ReceiveSaturation(Math.Max(0f, behavior.MaxSaturation - behavior.Saturation), EnumFoodCategory.Unknown, 10f, 0f);
		Audit(actor, "feed", serverPlayer.PlayerUID, serverPlayer.PlayerName, "Restored satiety", source);
		return serverPlayer.PlayerName + " fed.";
	}

	public string Revive(string actor, string playerOrUid, string source = "ingame")
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
		if (serverPlayer?.Entity == null)
		{
			return "Player must be online.";
		}
		serverPlayer.Entity.Revive();
		Audit(actor, "revive", serverPlayer.PlayerUID, serverPlayer.PlayerName, "Revived player", source);
		return serverPlayer.PlayerName + " revived.";
	}

	public string ToggleGhost(IServerPlayer player, string source = "ingame")
	{
		if (player?.WorldData == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return "Ghost mode is unavailable for this player.";
		}
		if (ghostStates.TryGetValue(player.PlayerUID, out AdminGhostState value))
		{
			player.WorldData.CurrentGameMode = value.GameMode;
			player.WorldData.FreeMove = value.FreeMove;
			player.WorldData.NoClip = value.NoClip;
			player.BroadcastPlayerData();
			ghostStates.Remove(player.PlayerUID);
			BroadcastGhostVisibility(player, isGhosted: false);
			PacketGuardTrustedState.Mark(player, "admin-ghost-exit");
			Audit(player.PlayerName, "ghost-off", player.PlayerUID, player.PlayerName, $"Restored {value.GameMode} mode", source);
			return $"Ghost mode disabled. Restored {value.GameMode}.";
		}
		if (player.WorldData.CurrentGameMode == EnumGameMode.Spectator)
		{
			return "You are already in spectator mode outside the suite. Ghost mode was not changed.";
		}
		ghostStates[player.PlayerUID] = new AdminGhostState
		{
			GameMode = player.WorldData.CurrentGameMode,
			FreeMove = player.WorldData.FreeMove,
			NoClip = player.WorldData.NoClip
		};
		player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
		player.WorldData.FreeMove = true;
		player.WorldData.NoClip = true;
		player.BroadcastPlayerData();
		BroadcastGhostVisibility(player, isGhosted: true);
		PacketGuardTrustedState.Mark(player, "admin-ghost-enter");
		Audit(player.PlayerName, "ghost-on", player.PlayerUID, player.PlayerName, "Entered spectator/free-move/no-clip ghost mode", source);
		return "Ghost mode enabled. Use /ghost again or the Ghost button to restore your previous game mode.";
	}

	private void BroadcastGhostVisibility(IServerPlayer ghost, bool isGhosted, IServerPlayer? onlyTo = null)
	{
		if (ghost?.Entity == null || string.IsNullOrWhiteSpace(ghost.PlayerUID))
		{
			return;
		}
		IServerNetworkChannel channel = api.Network.GetChannel("newfiesghostvisibility");
		if (channel == null)
		{
			return;
		}
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(ghost.Entity);
		GhostVisibilityPacket message = new GhostVisibilityPacket
		{
			EntityId = ghost.Entity.EntityId,
			PlayerUid = ghost.PlayerUID,
			IsGhosted = isGhosted,
			X = (runtimeEntityPos?.X ?? 0.0),
			Y = (runtimeEntityPos?.Y ?? 0.0),
			Z = (runtimeEntityPos?.Z ?? 0.0),
			Dimension = (runtimeEntityPos?.Dimension ?? 0),
			Yaw = (runtimeEntityPos?.Yaw ?? 0f),
			Pitch = (runtimeEntityPos?.Pitch ?? 0f)
		};
		IReadOnlyList<IServerPlayer> readOnlyList;
		if (onlyTo == null)
		{
			readOnlyList = OnlinePlayers();
		}
		else
		{
			IReadOnlyList<IServerPlayer> readOnlyList2 = new IServerPlayer[1] { onlyTo };
			readOnlyList = readOnlyList2;
		}
		IEnumerable<IServerPlayer> enumerable = readOnlyList;
		foreach (IServerPlayer item in enumerable)
		{
			if (item == null || string.IsNullOrWhiteSpace(item.PlayerUID) || string.Equals(item.PlayerUID, ghost.PlayerUID, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (!GhostVisibilityCompatibilityRegistry.IsSafe(item.PlayerUID))
			{
				api.Logger.Debug("[ServerEssentials/Admin] Ghost visibility suppressed for {0}: client has not advertised the safe ghost capability.", item.PlayerName);
				continue;
			}
			try
			{
				channel.SendPacket(message, item);
			}
			catch (Exception ex)
			{
				api.Logger.Debug("[ServerEssentials/Admin] Ghost visibility packet to {0} was not ready: {1}", item.PlayerName, ex.Message);
			}
		}
	}

	private void SendExistingGhostsTo(IServerPlayer recipient)
	{
		if (recipient == null || string.IsNullOrWhiteSpace(recipient.PlayerUID) || ghostStates.Count == 0)
		{
			return;
		}
		string[] array = ghostStates.Keys.ToArray();
		foreach (string playerOrUid in array)
		{
			IServerPlayer serverPlayer = FindOnlinePlayer(playerOrUid);
			if (serverPlayer?.Entity != null)
			{
				BroadcastGhostVisibility(serverPlayer, isGhosted: true, recipient);
			}
		}
	}

	private void RestoreGhostOnDisconnect(IServerPlayer player)
	{
		if (player?.WorldData != null && !string.IsNullOrWhiteSpace(player.PlayerUID) && ghostStates.TryGetValue(player.PlayerUID, out AdminGhostState value))
		{
			player.WorldData.CurrentGameMode = value.GameMode;
			player.WorldData.FreeMove = value.FreeMove;
			player.WorldData.NoClip = value.NoClip;
			ghostStates.Remove(player.PlayerUID);
		}
	}

	public string MapTeleport(IServerPlayer actor, BlockPos target, string source = "worldmap")
	{
		if (actor.Entity == null)
		{
			return "Player must be online.";
		}
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(actor.Entity);
		if (runtimeEntityPos == null)
		{
			return "Unable to read player position.";
		}
		adminBackPositions[actor.PlayerUID] = runtimeEntityPos.Copy();
		playerCommands?.Teleports.RecordTeleportOrigin(actor, runtimeEntityPos.Copy(), "admin map teleport");
		PacketGuardTrustedState.Mark(actor, "suite-map-teleport");
		actor.Entity.TeleportTo(target.Copy());
		Audit(actor.PlayerName, "mapteleport", actor.PlayerUID, actor.PlayerName, $"Teleported from world map to {target.X}, {target.Y}, {target.Z} (dim {target.dimension})", source);
		return $"Teleported to map position {target.X}, {target.Y}, {target.Z}. Use /back to return.";
	}

	public string Goto(IServerPlayer actor, string targetName)
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(targetName);
		if (actor.Entity == null || serverPlayer?.Entity == null)
		{
			return "Both players must be online.";
		}
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(actor.Entity);
		EntityPos runtimeEntityPos2 = GetRuntimeEntityPos(serverPlayer.Entity);
		if (runtimeEntityPos == null || runtimeEntityPos2 == null)
		{
			return "Unable to read player position.";
		}
		adminBackPositions[actor.PlayerUID] = runtimeEntityPos.Copy();
		playerCommands?.Teleports.RecordTeleportOrigin(actor, runtimeEntityPos.Copy(), "admin teleport");
		PacketGuardTrustedState.Mark(actor, "suite-admin-teleport");
		actor.Entity.TeleportTo(runtimeEntityPos2.Copy());
		Audit(actor.PlayerName, "goto", serverPlayer.PlayerUID, serverPlayer.PlayerName, "Teleported to player", "ingame");
		return "Teleported to " + serverPlayer.PlayerName + ".";
	}

	public string Bring(IServerPlayer actor, string targetName)
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(targetName);
		if (actor.Entity == null || serverPlayer?.Entity == null)
		{
			return "Both players must be online.";
		}
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(actor.Entity);
		EntityPos runtimeEntityPos2 = GetRuntimeEntityPos(serverPlayer.Entity);
		if (runtimeEntityPos == null || runtimeEntityPos2 == null)
		{
			return "Unable to read player position.";
		}
		playerBackPositions[serverPlayer.PlayerUID] = runtimeEntityPos2.Copy();
		playerCommands?.Teleports.RecordTeleportOrigin(serverPlayer, runtimeEntityPos2.Copy(), "admin bring");
		PacketGuardTrustedState.Mark(serverPlayer, "suite-admin-teleport");
		serverPlayer.Entity.TeleportTo(runtimeEntityPos.Copy());
		Audit(actor.PlayerName, "bring", serverPlayer.PlayerUID, serverPlayer.PlayerName, "Brought player to admin", "ingame");
		return "Brought " + serverPlayer.PlayerName + " to you.";
	}

	public string Back(IServerPlayer actor)
	{
		if (actor.Entity == null || !adminBackPositions.TryGetValue(actor.PlayerUID, out EntityPos value))
		{
			return "No saved admin teleport position.";
		}
		EntityPos entityPos = GetRuntimeEntityPos(actor.Entity)?.Copy();
		PacketGuardTrustedState.Mark(actor, "suite-admin-teleport");
		actor.Entity.TeleportTo(value.Copy());
		if (entityPos != null)
		{
			playerCommands?.Teleports.RecordTeleportOrigin(actor, entityPos, "admin back");
		}
		adminBackPositions.Remove(actor.PlayerUID);
		Audit(actor.PlayerName, "back", actor.PlayerUID, actor.PlayerName, "Returned to saved admin teleport position", "ingame");
		return "Returned to your previous position.";
	}

	public string SendBack(string actor, string targetName, string source = "ingame")
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(targetName);
		if (serverPlayer?.Entity == null || !playerBackPositions.TryGetValue(serverPlayer.PlayerUID, out EntityPos value))
		{
			return "No saved position for that player.";
		}
		EntityPos entityPos = GetRuntimeEntityPos(serverPlayer.Entity)?.Copy();
		if (entityPos != null)
		{
			playerCommands?.Teleports.RecordTeleportOrigin(serverPlayer, entityPos, "admin sendback");
		}
		PacketGuardTrustedState.Mark(serverPlayer, "suite-admin-teleport");
		serverPlayer.Entity.TeleportTo(value.Copy());
		playerBackPositions.Remove(serverPlayer.PlayerUID);
		Audit(actor, "sendback", serverPlayer.PlayerUID, serverPlayer.PlayerName, "Returned player to saved position", source);
		return "Returned " + serverPlayer.PlayerName + ".";
	}

	public bool IsJailed(string? playerUid)
	{
		return jailService?.IsJailed(playerUid) ?? false;
	}

	public string SetJailPosition(IServerPlayer actor, string source = "ingame")
	{
		return jailService?.SetJailPosition(actor, source) ?? "Jail service is unavailable.";
	}

	public string Jail(string actor, string targetName, string duration, string reason = "", string source = "ingame")
	{
		return jailService?.JailPlayer(actor, targetName, duration, reason, source) ?? "Jail service is unavailable.";
	}

	public string Unjail(string actor, string targetName, string reason = "", string source = "ingame")
	{
		return jailService?.UnjailPlayer(actor, targetName, reason, source) ?? "Jail service is unavailable.";
	}

	public string JailInfo(string targetName)
	{
		return jailService?.GetInfo(targetName) ?? "Jail service is unavailable.";
	}

	public string Smite(string actor, string targetName, string reason = "", string source = "ingame")
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(targetName);
		if (serverPlayer?.Entity == null)
		{
			return "Player must be online.";
		}
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(serverPlayer.Entity);
		if (runtimeEntityPos == null)
		{
			return "Unable to read player position.";
		}
		try
		{
			WeatherSystemServer modSystem = api.ModLoader.GetModSystem<WeatherSystemServer>();
			if (modSystem == null)
			{
				return "Vintage Story weather system is unavailable.";
			}
			modSystem.SpawnLightningFlash(runtimeEntityPos.XYZ);
			string detail = (string.IsNullOrWhiteSpace(reason) ? "Struck player with lightning" : ("Struck player with lightning. Reason: " + reason.Trim()));
			Audit(actor, "smite", serverPlayer.PlayerUID, serverPlayer.PlayerName, detail, source);
			return "Smote " + serverPlayer.PlayerName + " with lightning.";
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Admin] Smite failed for {0}: {1}", serverPlayer.PlayerName, ex);
			return "Smite failed: " + ex.Message;
		}
	}

	public LagHereReport ScanLagHere(IServerPlayer player, int radiusChunks)
	{
		radiusChunks = Math.Clamp(radiusChunks, 1, 12);
		double radius = radiusChunks * 32;
		Vec3d center = GetRuntimeEntityPos(player.Entity)?.XYZ ?? new Vec3d();
		List<Entity> list = api.World.LoadedEntities.Values.Where((Entity e) =>
		{
			EntityPos runtimeEntityPos = GetRuntimeEntityPos(e);
			return runtimeEntityPos != null && (double)runtimeEntityPos.XYZ.SquareDistanceTo(center) <= radius * radius;
		}).ToList();
		ServerHealthSample serverHealthSample = getHealth()?.Current;
		return new LagHereReport
		{
			RadiusChunks = radiusChunks,
			Entities = list.Count,
			DroppedItems = list.Count((Entity e) => e is EntityItem),
			Players = list.Count((Entity e) => e is EntityPlayer),
			LoadedChunks = (serverHealthSample?.LoadedChunks ?? 0),
			MainThreadDelayMs = (serverHealthSample?.MainThreadDelayMs ?? 0.0),
			CpuPercent = (serverHealthSample?.CpuPercent ?? 0.0),
			BlockLoggerQueue = (serverHealthSample?.BlockLoggerQueue ?? 0),
			TopEntityTypes = (from e in list
				group e by e.Code?.ToString() ?? e.GetType().Name into g
				orderby g.Count() descending
				select g).Take(10).ToDictionary((IGrouping<string, Entity> g) => g.Key, (IGrouping<string, Entity> g) => g.Count(), StringComparer.OrdinalIgnoreCase)
		};
	}

	public CleanupReport ScanCleanup()
	{
		List<Entity> list = api.World.LoadedEntities.Values.ToList();
		return new CleanupReport
		{
			LoadedEntities = list.Count,
			Creatures = list.Count((Entity e) => e.IsCreature),
			InanimateEntities = list.Count((Entity e) => !e.IsCreature),
			DeadOrDespawning = list.Count((Entity e) => !e.Alive || e.ShouldDespawn),
			DroppedItems = list.Count((Entity e) => e is EntityItem),
			Removed = 0,
			DryRun = true
		};
	}

	public CleanupReport CleanupDrops(bool dryRun, int maxRemove = int.MaxValue)
	{
		CleanupReport cleanupReport = ScanCleanup();
		List<EntityItem> list = api.World.LoadedEntities.Values.OfType<EntityItem>().Take(Math.Max(0, maxRemove)).ToList();
		if (!dryRun)
		{
			foreach (EntityItem item in list)
			{
				item.Die(EnumDespawnReason.Removed);
			}
		}
		cleanupReport.DroppedItems = list.Count;
		cleanupReport.Removed = ((!dryRun) ? list.Count : 0);
		cleanupReport.DryRun = dryRun;
		return cleanupReport;
	}

	public string SetMaintenance(string actor, bool enabled, string reason, long durationMilliseconds = 0L, string source = "ingame")
	{
		lock (gate)
		{
			bool oldEnabled = state.MaintenanceMode;
			string oldReason = state.MaintenanceReason;
			long oldUntil = state.MaintenanceUntilUnixMilliseconds;
			state.MaintenanceMode = enabled;
			state.MaintenanceReason = (string.IsNullOrWhiteSpace(reason) ? "Maintenance" : reason.Trim());
			state.MaintenanceUntilUnixMilliseconds = ((enabled && durationMilliseconds > 0) ? DeadlineAfterMilliseconds(durationMilliseconds) : 0);
			AddAuditUnsafe(actor, enabled ? "maintenance-on" : "maintenance-off", "", "server", state.MaintenanceReason, source);
			SaveStateUnsafe();
			PushUndo("maintenance mode change", (string undoActor) => RestoreMaintenance(undoActor, oldEnabled, oldReason, oldUntil));
		}
		api.BroadcastMessageToAllGroups(enabled ? ("Server maintenance mode enabled: " + state.MaintenanceReason) : "Server maintenance mode disabled.", EnumChatType.Notification);
		return enabled ? "Maintenance mode enabled." : "Maintenance mode disabled.";
	}

	public (bool Enabled, string Reason, long Until) GetMaintenance()
	{
		lock (gate)
		{
			return (Enabled: state.MaintenanceMode, Reason: state.MaintenanceReason, Until: state.MaintenanceUntilUnixMilliseconds);
		}
	}

	public string ScheduleShutdown(string actor, long delayMilliseconds, string reason, string source = "ingame")
	{
		shutdownAtUnixMilliseconds = DeadlineAfterMilliseconds(delayMilliseconds);
		shutdownReason = (string.IsNullOrWhiteSpace(reason) ? "Scheduled shutdown" : reason.Trim());
		lastShutdownAnnouncementBucket = long.MinValue;
		Audit(actor, "shutdown-scheduled", "", "server", "In " + FormatDuration(delayMilliseconds) + ": " + shutdownReason, source);
		return "Shutdown scheduled in " + FormatDuration(delayMilliseconds) + ".";
	}

	public string ScheduleRestart(string actor, long delayMilliseconds, string reason, string source = "ingame")
	{
		string text = (string.IsNullOrWhiteSpace(reason) ? "Restart requested" : reason.Trim());
		string text2 = ScheduleShutdown(actor, delayMilliseconds, "Restart requested: " + text, source);
		Audit(actor, "restart-requested", "", "server", "Clean shutdown scheduled; an external service/supervisor must restart the server process.", source);
		return text2 + " An external service/supervisor must restart the server process after shutdown.";
	}

	public string CancelShutdown(string actor, string source = "ingame")
	{
		shutdownAtUnixMilliseconds = 0L;
		Audit(actor, "shutdown-cancelled", "", "server", "Scheduled shutdown cancelled", source);
		api.BroadcastMessageToAllGroups("Scheduled server shutdown cancelled.", EnumChatType.Notification);
		return "Scheduled shutdown cancelled.";
	}

	public (long At, string Reason) GetShutdown()
	{
		return (At: shutdownAtUnixMilliseconds, Reason: shutdownReason);
	}

	public bool IsMuted(string uid, out PlayerAdminRecord? record)
	{
		lock (gate)
		{
			if (!state.Players.TryGetValue(uid, out record))
			{
				return false;
			}
			if (record.PermanentMute)
			{
				return true;
			}
			if (record.MuteUntilUnixMilliseconds > NowMs())
			{
				return true;
			}
			if (record.MuteUntilUnixMilliseconds > 0)
			{
				record.MuteUntilUnixMilliseconds = 0L;
				record.MuteReason = "";
				record.MutedBy = "";
				SaveStateUnsafe();
			}
			return false;
		}
	}

	public static bool IsSuccessfulActionResult(string action, string? result)
	{
		action = (action ?? "").Trim().ToLowerInvariant();
		result = (result ?? "").Trim();
		if (result.Length == 0)
		{
			return false;
		}
		if (1 == 0)
		{
		}
		bool result2 = action switch
		{
			"warn" => result.StartsWith("Warning #", StringComparison.OrdinalIgnoreCase) && result.Contains(" issued to ", StringComparison.OrdinalIgnoreCase), 
			"unwarn" => result.StartsWith("Warning #", StringComparison.OrdinalIgnoreCase) && result.Contains(" removed from ", StringComparison.OrdinalIgnoreCase), 
			"mute" => result.Contains(" muted for ", StringComparison.OrdinalIgnoreCase), 
			"unmute" => result.EndsWith(" unmuted.", StringComparison.OrdinalIgnoreCase), 
			"tempban" => result.Contains(" temporarily banned for ", StringComparison.OrdinalIgnoreCase), 
			"unban" => result.StartsWith("Temporary ban cleared for ", StringComparison.OrdinalIgnoreCase), 
			"freeze" => result.Contains(" frozen for ", StringComparison.OrdinalIgnoreCase), 
			"unfreeze" => result.EndsWith(" unfrozen.", StringComparison.OrdinalIgnoreCase), 
			"kick" => result.Contains(" kick scheduled with player-facing reason.", StringComparison.OrdinalIgnoreCase), 
			"heal" => result.EndsWith(" healed to full health.", StringComparison.OrdinalIgnoreCase), 
			"feed" => result.EndsWith(" fed.", StringComparison.OrdinalIgnoreCase), 
			"revive" => result.EndsWith(" revived.", StringComparison.OrdinalIgnoreCase), 
			"ghost" => result.StartsWith("Ghost mode enabled.", StringComparison.OrdinalIgnoreCase) || result.StartsWith("Ghost mode disabled.", StringComparison.OrdinalIgnoreCase), 
			"goto" => result.StartsWith("Teleported to ", StringComparison.OrdinalIgnoreCase), 
			"bring" => result.StartsWith("Brought ", StringComparison.OrdinalIgnoreCase), 
			"sendback" => result.StartsWith("Returned ", StringComparison.OrdinalIgnoreCase), 
			"smite" => result.StartsWith("Smote ", StringComparison.OrdinalIgnoreCase), 
			"setjail" => result.StartsWith("Jail position set ", StringComparison.OrdinalIgnoreCase), 
			"jail" => result.StartsWith("Jailed ", StringComparison.OrdinalIgnoreCase), 
			"unjail" => result.StartsWith("Released ", StringComparison.OrdinalIgnoreCase), 
			"note" => result.StartsWith("Staff note #", StringComparison.OrdinalIgnoreCase) && result.Contains(" added to ", StringComparison.OrdinalIgnoreCase), 
			"note-remove" => result.StartsWith("Staff note #", StringComparison.OrdinalIgnoreCase) && result.EndsWith(" removed.", StringComparison.OrdinalIgnoreCase), 
			"inventory-snapshot" => result.StartsWith("Inventory snapshot #", StringComparison.OrdinalIgnoreCase), 
			"inventory-restore" => result.StartsWith("Restored inventory snapshot #", StringComparison.OrdinalIgnoreCase), 
			"inventory-remove" => result.StartsWith("Removed ", StringComparison.OrdinalIgnoreCase) && result.Contains("Snapshot #", StringComparison.OrdinalIgnoreCase), 
			"inventory-quantity" => result.StartsWith("Changed ", StringComparison.OrdinalIgnoreCase) && result.Contains("Snapshot #", StringComparison.OrdinalIgnoreCase), 
			"inventory-move" => result.StartsWith("Moved/swapped inventory slots.", StringComparison.OrdinalIgnoreCase), 
			"inventory-give" => result.StartsWith("Gave ", StringComparison.OrdinalIgnoreCase) && result.Contains("Snapshot #", StringComparison.OrdinalIgnoreCase), 
			"inventory-replace" => result.StartsWith("Replaced ", StringComparison.OrdinalIgnoreCase) && result.Contains("Snapshot #", StringComparison.OrdinalIgnoreCase), 
			"maintenance" => result.StartsWith("Maintenance mode enabled.", StringComparison.OrdinalIgnoreCase) || result.StartsWith("Maintenance mode disabled.", StringComparison.OrdinalIgnoreCase), 
			"shutdown" => result.StartsWith("Shutdown scheduled in ", StringComparison.OrdinalIgnoreCase), 
			"restart" => result.StartsWith("Shutdown scheduled in ", StringComparison.OrdinalIgnoreCase), 
			"shutdown-cancel" => result.StartsWith("Scheduled shutdown cancelled.", StringComparison.OrdinalIgnoreCase), 
			"undo" => result.StartsWith("Undid ", StringComparison.OrdinalIgnoreCase), 
			_ => false, 
		};
		if (1 == 0)
		{
		}
		return result2;
	}

	public string ExecuteWebAction(string actor, string action, string player, string duration, string reason, long id = 0L, string note = "")
	{
		action = (action ?? "").Trim().ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result = action switch
		{
			"warn" => Warn(actor, player, reason, "web"), 
			"unwarn" => RemoveWarning(actor, player, id, "web"), 
			"mute" => Mute(actor, player, duration, reason, "web"), 
			"unmute" => Unmute(actor, player, "web"), 
			"tempban" => TempBan(actor, player, duration, reason, "web"), 
			"unban" => Unban(actor, player, "web"), 
			"freeze" => Freeze(actor, player, duration, reason, "web"), 
			"unfreeze" => Unfreeze(actor, player, "web"), 
			"kick" => Kick(actor, player, reason), 
			"heal" => Heal(actor, player, "web"), 
			"feed" => Feed(actor, player, "web"), 
			"revive" => Revive(actor, player, "web"), 
			"smite" => Smite(actor, player, reason, "web"), 
			"jail" => Jail(actor, player, duration, reason, "web"), 
			"unjail" => Unjail(actor, player, reason, "web"), 
			"note" => AddNote(actor, player, note, "web"), 
			"note-remove" => RemoveNote(actor, player, id, "web"), 
			"inventory-remove" => RemoveInventorySlot(actor, player, note, (int)id, "web"), 
			"sendback" => SendBack(actor, player, "web"), 
			"undo" => Undo(actor, "web"), 
			_ => "Unknown admin action.", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	public string Undo(string actor, string source = "ingame")
	{
		AdminUndoEntry adminUndoEntry;
		lock (undoGate)
		{
			if (undoStack.Count == 0)
			{
				return "Nothing reversible to undo.";
			}
			adminUndoEntry = undoStack.Pop();
		}
		performingUndo = true;
		try
		{
			string text = adminUndoEntry.Reverse(actor);
			Audit(actor, "undo", "", "server", "Undid " + adminUndoEntry.Description + ": " + text, source);
			return "Undid " + adminUndoEntry.Description + ". " + text;
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Admin] Undo failed: {0}", ex.Message);
			return "Undo failed: " + ex.Message;
		}
		finally
		{
			performingUndo = false;
		}
	}

	public string GetUndoDescription()
	{
		lock (undoGate)
		{
			return (undoStack.Count == 0) ? "" : undoStack.Peek().Description;
		}
	}

	private void PushUndo(string description, System.Func<string, string> reverse)
	{
		if (performingUndo)
		{
			return;
		}
		lock (undoGate)
		{
			undoStack.Push(new AdminUndoEntry(description, reverse));
			while (undoStack.Count > 50)
			{
				AdminUndoEntry[] array = undoStack.Take(50).Reverse().ToArray();
				undoStack.Clear();
				AdminUndoEntry[] array2 = array;
				foreach (AdminUndoEntry item in array2)
				{
					undoStack.Push(item);
				}
			}
		}
	}

	private string RestoreMute(string actor, string uid, string name, bool permanent, long until, string reason, string by)
	{
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			orCreateUnsafe.PermanentMute = permanent;
			orCreateUnsafe.MuteUntilUnixMilliseconds = until;
			orCreateUnsafe.MuteReason = reason;
			orCreateUnsafe.MutedBy = by;
			AddAuditUnsafe(actor, "mute-restore", uid, name, "Restored previous mute state", "undo");
			SaveStateUnsafe();
		}
		return "Previous mute state restored for " + name + ".";
	}

	private string RestoreBan(string actor, string uid, string name, long until, string reason, string by)
	{
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			orCreateUnsafe.BanUntilUnixMilliseconds = until;
			orCreateUnsafe.BanReason = reason;
			orCreateUnsafe.BannedBy = by;
			AddAuditUnsafe(actor, "ban-restore", uid, name, "Restored previous temporary-ban state", "undo");
			SaveStateUnsafe();
		}
		return "Previous temporary-ban state restored for " + name + ".";
	}

	private string RestoreFreeze(string actor, string uid, string name, bool frozen, long until, string by, string reason, EntityPos? pos)
	{
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, name);
			orCreateUnsafe.Frozen = frozen;
			orCreateUnsafe.FreezeUntilUnixMilliseconds = until;
			orCreateUnsafe.FrozenBy = by;
			orCreateUnsafe.FreezeReason = reason;
			if (frozen && pos != null)
			{
				frozenPositions[uid] = pos.Copy();
			}
			else
			{
				frozenPositions.Remove(uid);
			}
			AddAuditUnsafe(actor, "freeze-restore", uid, name, "Restored previous freeze state", "undo");
			SaveStateUnsafe();
		}
		RefreshFreezeTickListener();
		return "Previous freeze state restored for " + name + ".";
	}

	private string RestoreInventorySlot(string actor, string uid, string inventoryId, int slotIndex, ItemStack backup)
	{
		IServerPlayer serverPlayer = FindOnlinePlayer(uid);
		if (serverPlayer == null)
		{
			return "Player is offline; inventory undo could not be applied.";
		}
		InventoryBase inventoryBase = serverPlayer.InventoryManager.InventoriesOrdered.FirstOrDefault((InventoryBase inv) => IsManagedPlayerInventory(inv) && string.Equals(inv.InventoryID, inventoryId, StringComparison.OrdinalIgnoreCase));
		if (inventoryBase == null || slotIndex < 0 || slotIndex >= inventoryBase.Count)
		{
			return "Inventory slot is no longer available.";
		}
		ItemSlot itemSlot = inventoryBase[slotIndex];
		if (itemSlot.Itemstack != null)
		{
			return "Inventory slot is no longer empty; undo refused to avoid overwriting an item.";
		}
		itemSlot.Itemstack = backup.Clone();
		itemSlot.MarkDirty();
		Audit(actor, "inventory-restore", serverPlayer.PlayerUID, serverPlayer.PlayerName, $"Restored {itemSlot.Itemstack.StackSize}x {itemSlot.Itemstack.Collectible?.Code} to {inventoryId}[{slotIndex}]", "undo");
		return "Inventory slot restored for " + serverPlayer.PlayerName + ".";
	}

	private string RestoreMaintenance(string actor, bool enabled, string reason, long until)
	{
		lock (gate)
		{
			state.MaintenanceMode = enabled;
			state.MaintenanceReason = reason;
			state.MaintenanceUntilUnixMilliseconds = until;
			AddAuditUnsafe(actor, "maintenance-restore", "", "server", "Restored previous maintenance state", "undo");
			SaveStateUnsafe();
		}
		return "Previous maintenance state restored.";
	}

	public void Audit(string actor, string action, string uid, string name, string detail, string source)
	{
		lock (gate)
		{
			AddAuditUnsafe(actor, action, uid, name, detail, source);
			SaveStateUnsafe();
		}
	}

	private void RegisterPrivileges()
	{
		api.Permissions.RegisterPrivilege("nse.player.view", "View Newfies Server Essentials player administration data");
		api.Permissions.RegisterPrivilege("nse.player.manage", "Manage players with Newfies Server Essentials");
		api.Permissions.RegisterPrivilege("nse.moderation.warn", "Issue and remove staff warnings");
		api.Permissions.RegisterPrivilege("nse.moderation.mute", "Mute and unmute players");
		api.Permissions.RegisterPrivilege("nse.moderation.ban", "Temporarily ban and unban players");
		api.Permissions.RegisterPrivilege("nse.inventory.view", "View player inventories");
		api.Permissions.RegisterPrivilege("nse.inventory.edit", "Remove items from player inventories with explicit auditing");
		api.Permissions.RegisterPrivilege("nse.teleport", "Use Newfies Server Essentials teleport helpers");
		api.Permissions.RegisterPrivilege("nse.cleanup", "Run Newfies Server Essentials cleanup actions");
		api.Permissions.RegisterPrivilege("nse.diagnostics", "Run Newfies Server Essentials local diagnostics");
		api.Permissions.RegisterPrivilege("nse.server", "Manage maintenance and scheduled shutdowns");
		api.Permissions.RegisterPrivilege("nse.reports.manage", "Manage issue reports and tickets");
		api.Permissions.RegisterPrivilege("nse.smite", "Strike players with lightning");
		api.Permissions.RegisterPrivilege("nse.jail", "Set the jail cell and jail/release players");
	}

	private void RegisterCommands()
	{
		new AdminCommands(api, this, reports, playerCommands, getAutomatedRestarts).Register();
	}

	private void OnPlayerJoin(IServerPlayer player)
	{
		IndexOnlinePlayer(player);
		TouchPlayer(player);
		string error;
		if (TryGetActiveBan(player.PlayerUID, out PlayerAdminRecord rec))
		{
			long milliseconds = Math.Max(0L, rec.BanUntilUnixMilliseconds - NowMs());
			string text = "Temporarily banned. Remaining: " + FormatDuration(milliseconds) + ". Reason: " + rec.BanReason;
			if (!PlayerDisconnectCompat.DisconnectWithReason(player, player.PlayerName + " was disconnected (temporary ban).", text, out error))
			{
				player.Disconnect(text);
			}
		}
		else if (GetMaintenance().Enabled && !IsStaff(player))
		{
			string text2 = "Server is in maintenance mode: " + GetMaintenance().Reason;
			if (!PlayerDisconnectCompat.DisconnectWithReason(player, player.PlayerName + " was disconnected (maintenance).", text2, out error))
			{
				player.Disconnect(text2);
			}
		}
	}

	private void OnPlayerNowPlaying(IServerPlayer player)
	{
		offlineInventoryReader.Invalidate(player.PlayerUID);
		decodedInventoryCache.Remove(player.PlayerUID);
		IndexOnlinePlayer(player);
		TouchPlayer(player);
		BeginSession(player);
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(player.Entity);
		if (runtimeEntityPos != null)
		{
			lock (gate)
			{
				activity[player.PlayerUID] = new PlayerActivityState(runtimeEntityPos.Copy(), NowMs());
			}
			if (IsFrozen(player.PlayerUID))
			{
				frozenPositions[player.PlayerUID] = runtimeEntityPos.Copy();
			}
		}
		RefreshFreezeTickListener();
		SendExistingGhostsTo(player);
		api.Event.RegisterCallback((float _) =>
		{
			SendExistingGhostsTo(player);
		}, 500);
		api.Event.RegisterCallback((float _) =>
		{
			SendExistingGhostsTo(player);
		}, 1500);
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		CaptureOfflineInventory(player, "disconnect-capture");
		RestoreGhostOnDisconnect(player);
		RemoveOnlinePlayer(player);
		lock (gate)
		{
			activity.Remove(player.PlayerUID);
		}
		frozenPositions.Remove(player.PlayerUID);
		adminBackPositions.Remove(player.PlayerUID);
		playerBackPositions.Remove(player.PlayerUID);
		RefreshFreezeTickListener();
		EndSession(player);
	}

	private string ResolveChatDisplayName(IServerPlayer player)
	{
		TreeAttribute treeAttribute = new TreeAttribute();
		treeAttribute.SetString("playerUid", player.PlayerUID);
		try
		{
			api.Event.PushEvent("newfiesse:chat-sender-query", treeAttribute);
		}
		catch
		{
			return player.PlayerName;
		}
		return treeAttribute.GetBool("available") ? treeAttribute.GetString("displayName", player.PlayerName) : player.PlayerName;
	}

	private void OnPlayerChat(IServerPlayer player, int channelId, ref string message, ref string data, BoolRef consumed)
	{
		if (player != null && !consumed.value)
		{
			MarkActivity(player);
			if (!ChatMessageBoundary.Parse(message, player.PlayerName, ResolveChatDisplayName(player), player.Role?.Name).IsCommand && IsMuted(player.PlayerUID, out PlayerAdminRecord record))
			{
				consumed.SetValue(value_: true);
				string text = (record.PermanentMute ? "permanent" : FormatDuration(Math.Max(0L, record.MuteUntilUnixMilliseconds - NowMs())));
				player.SendMessage(channelId, "You are muted (" + text + "). Reason: " + record.MuteReason, EnumChatType.CommandError);
			}
		}
	}

	private bool OnCanPlaceOrBreakBlock(IServerPlayer player, BlockSelection blockSel, out string claimant)
	{
		claimant = "";
		JailService? jailService = this.jailService;
		if (jailService != null && jailService.IsJailed(player.PlayerUID))
		{
			claimant = "custommessage-nse-jailed";
			return false;
		}
		if (!IsFrozen(player.PlayerUID))
		{
			return true;
		}
		claimant = "custommessage-nse-frozen";
		return false;
	}

	private bool OnCanUseBlock(IServerPlayer player, BlockSelection blockSel)
	{
		return !IsFrozen(player.PlayerUID) && !IsJailed(player.PlayerUID);
	}

	private void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
	{
		if (byPlayer is IServerPlayer serverPlayer && (IsFrozen(serverPlayer.PlayerUID) || IsJailed(serverPlayer.PlayerUID)))
		{
			handling = EnumHandling.PreventDefault;
		}
	}

	private bool IsFrozen(string uid)
	{
		lock (gate)
		{
			PlayerAdminRecord value;
			return state.Players.TryGetValue(uid, out value) && value.Frozen;
		}
	}

	private void OnTick(float dt)
	{
		if (disposed)
		{
			return;
		}
		try
		{
			OnTickCore(dt);
			suppressedTickErrors = 0;
		}
		catch (Exception ex)
		{
			long num = NowMs();
			suppressedTickErrors++;
			if (num - lastTickErrorLogUnixMilliseconds >= 5000)
			{
				int num2 = Math.Max(0, suppressedTickErrors - 1);
				api.Logger.Error("[ServerEssentials/Admin] Tick failure contained (suppressed since last log={0}): {1}", num2, ex);
				lastTickErrorLogUnixMilliseconds = num;
				suppressedTickErrors = 0;
			}
		}
	}

	private void OnTickCore(float dt)
	{
		long num = NowMs();
		if (num - lastExpiryScanUnixMilliseconds >= 5000)
		{
			lastExpiryScanUnixMilliseconds = num;
			List<string> list = new List<string>();
			lock (gate)
			{
				foreach (KeyValuePair<string, PlayerAdminRecord> player in state.Players)
				{
					if (player.Value.Frozen && player.Value.FreezeUntilUnixMilliseconds > 0 && player.Value.FreezeUntilUnixMilliseconds <= num)
					{
						list.Add(player.Key);
					}
				}
				if (state.MaintenanceMode && state.MaintenanceUntilUnixMilliseconds > 0 && state.MaintenanceUntilUnixMilliseconds <= num)
				{
					state.MaintenanceMode = false;
					state.MaintenanceUntilUnixMilliseconds = 0L;
					AddAuditUnsafe("system", "maintenance-off", "", "server", "Timed maintenance expired", "system");
					SaveStateUnsafe();
				}
			}
			foreach (string item in list)
			{
				Unfreeze("system", item, "system");
			}
		}
		if (num - lastActivitySampleUnixMilliseconds >= 1000)
		{
			lastActivitySampleUnixMilliseconds = num;
			foreach (IServerPlayer item2 in OnlinePlayers())
			{
				UpdateMovementActivity(item2, num);
			}
		}
		TickShutdown(num);
	}

	private void RefreshFreezeTickListener()
	{
		if (!disposed)
		{
			bool flag = frozenPositions.Count > 0;
			if (flag && freezeTickListenerId < 0)
			{
				freezeTickListenerId = api.Event.RegisterGameTickListener(OnFreezeTick, 250);
			}
			else if (!flag && freezeTickListenerId >= 0)
			{
				api.Event.UnregisterGameTickListener(freezeTickListenerId);
				freezeTickListenerId = -1L;
			}
		}
	}

	private void OnFreezeTick(float dt)
	{
		if (disposed || frozenPositions.Count == 0)
		{
			RefreshFreezeTickListener();
			return;
		}
		try
		{
			KeyValuePair<string, EntityPos>[] array = frozenPositions.ToArray();
			for (int i = 0; i < array.Length; i++)
			{
				KeyValuePair<string, EntityPos> keyValuePair = array[i];
				IServerPlayer serverPlayer = FindOnlinePlayer(keyValuePair.Key);
				if (serverPlayer?.Entity == null)
				{
					continue;
				}
				EntityPos runtimeEntityPos = GetRuntimeEntityPos(serverPlayer.Entity);
				if (runtimeEntityPos != null)
				{
					if ((double)runtimeEntityPos.SquareDistanceTo(keyValuePair.Value) > 0.01)
					{
						PacketGuardTrustedState.Mark(serverPlayer, "suite-admin-teleport");
						serverPlayer.Entity.TeleportTo(keyValuePair.Value.Copy());
					}
					runtimeEntityPos.Motion.Set(0.0, 0.0, 0.0);
				}
			}
		}
		catch (Exception ex)
		{
			long num = NowMs();
			if (num - lastTickErrorLogUnixMilliseconds >= 5000)
			{
				api.Logger.Error("[ServerEssentials/Admin] Freeze enforcement failure contained: {0}", ex);
				lastTickErrorLogUnixMilliseconds = num;
			}
		}
	}

	private void TickShutdown(long now)
	{
		if (shutdownAtUnixMilliseconds <= 0)
		{
			return;
		}
		long num = shutdownAtUnixMilliseconds - now;
		if (num <= 0)
		{
			shutdownAtUnixMilliseconds = 0L;
			bool flag = IsRestartShutdown();
			string text = (flag ? "Server is restarting now" : ("Server shutting down: " + shutdownReason));
			api.BroadcastMessageToAllGroups(flag ? FormatRestartChatNotice(text) : text, EnumChatType.Notification);
			api.Server.ShutDown();
			return;
		}
		if (1 == 0)
		{
		}
		long num2;
		if (num <= 300000)
		{
			if (num <= 10000)
			{
				num2 = num / 1000;
			}
			else
			{
				num2 = ((num > 60000) ? (num / 60000) : (num / 10000));
			}
		}
		else
		{
			num2 = ((num > 600000) ? long.MaxValue : (num / 300000));
		}
		if (1 == 0)
		{
		}
		long num3 = num2;
		if (num3 != lastShutdownAnnouncementBucket)
		{
			lastShutdownAnnouncementBucket = num3;
			if (num <= 600000)
			{
				bool flag2 = IsRestartShutdown();
				string text2 = (flag2 ? BuildRestartCountdownText(num) : ("Server shutdown in " + FormatDuration(num) + ": " + shutdownReason));
				api.BroadcastMessageToAllGroups(flag2 ? FormatRestartChatNotice(text2) : text2, EnumChatType.Notification);
			}
		}
	}

	private bool IsRestartShutdown()
	{
		return shutdownReason.StartsWith("Restart requested:", StringComparison.OrdinalIgnoreCase);
	}

	private static string BuildRestartCountdownText(long remainingMilliseconds)
	{
		if (remainingMilliseconds >= 60000)
		{
			int num = Math.Max(1, (int)Math.Ceiling((double)remainingMilliseconds / 60000.0));
			return $"Server is restarting in {num} minute{((num == 1) ? "" : "s")}";
		}
		int num2 = Math.Max(1, (int)Math.Ceiling((double)remainingMilliseconds / 1000.0));
		return $"Server is restarting in {num2} second{((num2 == 1) ? "" : "s")}";
	}

	private static string FormatRestartChatNotice(string message)
	{
		return "<font color=\"#ffd166\"><strong>" + WebUtility.HtmlEncode(message ?? string.Empty) + "</strong></font>";
	}

	private bool TryGetActiveBan(string uid, out PlayerAdminRecord? rec)
	{
		lock (gate)
		{
			if (!state.Players.TryGetValue(uid, out rec))
			{
				return false;
			}
			if (rec.BanUntilUnixMilliseconds > NowMs())
			{
				return true;
			}
			if (rec.BanUntilUnixMilliseconds > 0)
			{
				rec.BanUntilUnixMilliseconds = 0L;
				rec.BanReason = "";
				rec.BannedBy = "";
				SaveStateUnsafe();
			}
			return false;
		}
	}

	private void BeginSession(IServerPlayer player)
	{
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(player.PlayerUID, player.PlayerName);
			if (orCreateUnsafe.SessionStartedUnixMilliseconds == 0)
			{
				orCreateUnsafe.SessionStartedUnixMilliseconds = NowMs();
				AddAuditUnsafe("system", "login", player.PlayerUID, player.PlayerName, "Player session started", "system");
				SaveStateUnsafe();
			}
		}
	}

	private void EndSession(IServerPlayer player)
	{
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(player.PlayerUID, player.PlayerName);
			long num = NowMs();
			if (orCreateUnsafe.SessionStartedUnixMilliseconds > 0)
			{
				orCreateUnsafe.TotalPlaySeconds += Math.Max(0L, (num - orCreateUnsafe.SessionStartedUnixMilliseconds) / 1000);
			}
			orCreateUnsafe.SessionStartedUnixMilliseconds = 0L;
			orCreateUnsafe.LastSeenUnixMilliseconds = num;
			AddAuditUnsafe("system", "logout", player.PlayerUID, player.PlayerName, "Player disconnected", "system");
			SaveStateUnsafe();
		}
	}

	private void MarkActivity(IServerPlayer player)
	{
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(player.Entity);
		if (runtimeEntityPos == null)
		{
			return;
		}
		lock (gate)
		{
			activity[player.PlayerUID] = new PlayerActivityState(runtimeEntityPos.Copy(), NowMs());
		}
	}

	private void UpdateMovementActivity(IServerPlayer player, long now)
	{
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(player.Entity);
		if (runtimeEntityPos == null)
		{
			return;
		}
		lock (gate)
		{
			if (!activity.TryGetValue(player.PlayerUID, out PlayerActivityState value))
			{
				activity[player.PlayerUID] = new PlayerActivityState(runtimeEntityPos.Copy(), now);
			}
			else if ((double)value.Position.SquareDistanceTo(runtimeEntityPos) > 0.01)
			{
				value.Position = runtimeEntityPos.Copy();
				value.LastActiveUnixMilliseconds = now;
			}
		}
	}

	private void TouchPlayer(IServerPlayer player)
	{
		lock (gate)
		{
			PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(player.PlayerUID, player.PlayerName);
			orCreateUnsafe.LastKnownName = player.PlayerName;
			orCreateUnsafe.LastSeenUnixMilliseconds = NowMs();
			SaveStateUnsafe();
		}
	}

	private AdminPlayerSummary BuildSummaryUnsafe(string uid, IServerPlayer? player)
	{
		PlayerAdminRecord orCreateUnsafe = GetOrCreateUnsafe(uid, player?.PlayerName ?? state.Players.GetValueOrDefault(uid)?.LastKnownName ?? uid);
		long num = NowMs();
		long num2 = ((orCreateUnsafe.SessionStartedUnixMilliseconds > 0) ? Math.Max(0L, (num - orCreateUnsafe.SessionStartedUnixMilliseconds) / 1000) : 0);
		float health = 0f;
		float maxHealth = 0f;
		long num3 = 0L;
		if (player != null && activity.TryGetValue(uid, out PlayerActivityState value))
		{
			num3 = Math.Max(0L, (num - value.LastActiveUnixMilliseconds) / 1000);
		}
		if (player?.Entity != null)
		{
			EntityBehaviorHealth behavior = player.Entity.GetBehavior<EntityBehaviorHealth>();
			if (behavior != null)
			{
				health = behavior.Health;
				maxHealth = behavior.MaxHealth;
			}
		}
		EntityPos runtimeEntityPos = GetRuntimeEntityPos(player?.Entity);
		AdminPlayerSummary adminPlayerSummary = new AdminPlayerSummary
		{
			PlayerUid = uid,
			Name = (player?.PlayerName ?? orCreateUnsafe.LastKnownName),
			Online = (player != null),
			Role = (player?.Role?.Code ?? "offline"),
			GameMode = (player?.WorldData?.CurrentGameMode.ToString() ?? "offline"),
			PingMilliseconds = ((player != null && !float.IsNaN(player.Ping)) ? ((int)Math.Round(player.Ping * 1000f)) : 0),
			FirstSeenUnixMilliseconds = orCreateUnsafe.FirstSeenUnixMilliseconds,
			LastSeenUnixMilliseconds = orCreateUnsafe.LastSeenUnixMilliseconds,
			SessionSeconds = num2,
			TotalPlaySeconds = orCreateUnsafe.TotalPlaySeconds + num2,
			ActiveWarnings = orCreateUnsafe.Warnings.Count((AdminWarningRecord x) => !x.Removed),
			Muted = (orCreateUnsafe.PermanentMute || orCreateUnsafe.MuteUntilUnixMilliseconds > num),
			MuteUntilUnixMilliseconds = orCreateUnsafe.MuteUntilUnixMilliseconds,
			Banned = (orCreateUnsafe.BanUntilUnixMilliseconds > num),
			BanUntilUnixMilliseconds = orCreateUnsafe.BanUntilUnixMilliseconds,
			Frozen = orCreateUnsafe.Frozen,
			Jailed = (jailService?.IsJailed(uid) ?? false),
			JailUntilUnixMilliseconds = (jailService?.GetReleaseAt(uid) ?? 0)
		};
		int afk;
		if (player != null)
		{
			NewfiesAfkModSystem? current = NewfiesAfkModSystem.Current;
			afk = (((current != null && current.IsAfk(uid)) || num3 >= 300) ? 1 : 0);
		}
		else
		{
			afk = 0;
		}
		adminPlayerSummary.Afk = (byte)afk != 0;
		adminPlayerSummary.IdleSeconds = num3;
		adminPlayerSummary.Position = ((runtimeEntityPos == null) ? "" : $"{runtimeEntityPos.X:0},{runtimeEntityPos.Y:0},{runtimeEntityPos.Z:0}");
		adminPlayerSummary.Health = health;
		adminPlayerSummary.MaxHealth = maxHealth;
		return adminPlayerSummary;
	}

	private static EntityPos? GetRuntimeEntityPos(Entity? entity)
	{
		if (entity == null)
		{
			return null;
		}
		try
		{
			object obj = RuntimePosProperty?.GetValue(entity) ?? RuntimePosField?.GetValue(entity) ?? RuntimeServerPosProperty?.GetValue(entity) ?? RuntimeServerPosField?.GetValue(entity) ?? RuntimeSidedPosProperty?.GetValue(entity) ?? RuntimeSidedPosField?.GetValue(entity);
			return obj as EntityPos;
		}
		catch
		{
			return null;
		}
	}

	public IServerPlayer? FindOnlinePlayer(string playerOrUid)
	{
		if (string.IsNullOrWhiteSpace(playerOrUid))
		{
			return null;
		}
		lock (onlineGate)
		{
			if (onlinePlayersByUid.TryGetValue(playerOrUid, out IServerPlayer value))
			{
				return value;
			}
			IServerPlayer value2;
			return onlinePlayersByName.TryGetValue(playerOrUid, out value2) ? value2 : null;
		}
	}

	private void IndexOnlinePlayer(IServerPlayer player)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		lock (onlineGate)
		{
			if (onlinePlayersByUid.TryGetValue(player.PlayerUID, out IServerPlayer value) && !string.Equals(value.PlayerName, player.PlayerName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value.PlayerName))
			{
				onlinePlayersByName.Remove(value.PlayerName);
			}
			onlinePlayersByUid[player.PlayerUID] = player;
			if (!string.IsNullOrWhiteSpace(player.PlayerName))
			{
				onlinePlayersByName[player.PlayerName] = player;
			}
		}
		InvalidatePlayerSummaryCaches();
	}

	private void RemoveOnlinePlayer(IServerPlayer player)
	{
		lock (onlineGate)
		{
			onlinePlayersByUid.Remove(player.PlayerUID);
			if (!string.IsNullOrWhiteSpace(player.PlayerName) && onlinePlayersByName.TryGetValue(player.PlayerName, out IServerPlayer value) && string.Equals(value.PlayerUID, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
			{
				onlinePlayersByName.Remove(player.PlayerName);
			}
		}
		InvalidatePlayerSummaryCaches();
	}

	private IServerPlayer? FindPlayer(string playerOrUid)
	{
		return api.Server.Players.FirstOrDefault((IServerPlayer p) => string.Equals(p.PlayerUID, playerOrUid, StringComparison.OrdinalIgnoreCase) || string.Equals(p.PlayerName, playerOrUid, StringComparison.OrdinalIgnoreCase));
	}

	private IReadOnlyList<IServerPlayer> OnlinePlayers()
	{
		lock (onlineGate)
		{
			return onlinePlayersByUid.Values.ToArray();
		}
	}

	private void InvalidatePlayerSummaryCaches()
	{
		Interlocked.Increment(ref playerSummaryVersion);
	}

	private bool TryResolveTarget(string playerOrUid, out string uid, out string name)
	{
		IServerPlayer serverPlayer = FindPlayer(playerOrUid);
		if (serverPlayer != null)
		{
			uid = serverPlayer.PlayerUID;
			name = serverPlayer.PlayerName;
			return true;
		}
		uid = ResolveKnownUid(playerOrUid);
		lock (gate)
		{
			if (!string.IsNullOrWhiteSpace(uid) && state.Players.TryGetValue(uid, out PlayerAdminRecord value))
			{
				name = value.LastKnownName;
				return true;
			}
		}
		name = "";
		return false;
	}

	private string ResolveKnownUid(string playerOrUid)
	{
		if (string.IsNullOrWhiteSpace(playerOrUid))
		{
			return "";
		}
		lock (gate)
		{
			if (state.Players.TryGetValue(playerOrUid, out PlayerAdminRecord value))
			{
				return value.PlayerUid;
			}
			return state.Players.FirstOrDefault((KeyValuePair<string, PlayerAdminRecord> x) => string.Equals(x.Value.LastKnownName, playerOrUid, StringComparison.OrdinalIgnoreCase)).Key ?? "";
		}
	}

	private PlayerAdminRecord GetOrCreateUnsafe(string uid, string name)
	{
		if (!state.Players.TryGetValue(uid, out PlayerAdminRecord value))
		{
			value = new PlayerAdminRecord
			{
				PlayerUid = uid,
				LastKnownName = name,
				FirstSeenUnixMilliseconds = NowMs(),
				LastSeenUnixMilliseconds = NowMs()
			};
			state.Players[uid] = value;
		}
		if (!string.IsNullOrWhiteSpace(name))
		{
			value.LastKnownName = name;
		}
		return value;
	}

	private void AddAuditUnsafe(string actor, string action, string uid, string name, string detail, string source)
	{
		state.Audit.Add(new AdminAuditRecord
		{
			Id = NextIdUnsafe(),
			UnixMilliseconds = NowMs(),
			Actor = actor,
			Action = action,
			TargetUid = uid,
			TargetName = name,
			Detail = detail,
			Source = source
		});
		if (state.Audit.Count > 5000)
		{
			state.Audit.RemoveRange(0, state.Audit.Count - 5000);
		}
		api.Logger.Audit($"[ServerEssentials/Admin] {actor} {action} {name}/{uid}: {detail}");
	}

	private void InitializeNextRecordId()
	{
		lock (gate)
		{
			long num = ((state.Audit.Count == 0) ? 0 : state.Audit.Max((AdminAuditRecord x) => x.Id));
			foreach (PlayerAdminRecord value in state.Players.Values)
			{
				if (value.Warnings.Count > 0)
				{
					num = Math.Max(num, value.Warnings.Max((AdminWarningRecord x) => x.Id));
				}
				if (value.Notes.Count > 0)
				{
					num = Math.Max(num, value.Notes.Max((AdminNoteRecord x) => x.Id));
				}
			}
			foreach (List<AdminInventorySnapshotRecord> value2 in state.InventorySnapshots.Values)
			{
				if (value2.Count > 0)
				{
					num = Math.Max(num, value2.Max((AdminInventorySnapshotRecord x) => x.Id));
				}
			}
			nextRecordId = Math.Max(1L, num + 1);
		}
	}

	private long NextIdUnsafe()
	{
		return nextRecordId++;
	}

	private bool IsStaff(IServerPlayer player)
	{
		return player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege(Privilege.root) || player.HasPrivilege("nse.server") || player.HasPrivilege("nse.player.view") || player.HasPrivilege("nse.player.manage") || player.HasPrivilege("nse.moderation.warn") || player.HasPrivilege("nse.moderation.mute") || player.HasPrivilege("nse.moderation.ban") || player.HasPrivilege("nse.inventory.edit") || player.HasPrivilege("nse.reports.manage");
	}

	private void LoadState()
	{
		if (storage == null)
		{
			state = new AdminState();
			NormalizeStateUnsafe();
			return;
		}
		if (storage.HasState())
		{
			state = storage.LoadState();
			NormalizeStateUnsafe();
			storage.RequestSave();
			api.Logger.Notification("[ServerEssentials/Storage] Loaded admin state from {0}", storage.DatabasePath);
			return;
		}
		AdminState adminState = ModConfigRecovery.LoadOrNull<AdminState>(api, "NewfiesServerEssentialsAdmin.json", "ServerEssentials/Storage");
		state = adminState ?? new AdminState();
		NormalizeStateUnsafe();
		if (adminState != null)
		{
			storage.BackupLegacyJson(state);
			storage.ImportLegacy(SnapshotStateForStorage());
			api.Logger.Notification("[ServerEssentials/Storage] Migrated legacy {0} into essentials.db. The JSON is now read-only legacy data.", "NewfiesServerEssentialsAdmin.json");
		}
		else
		{
			storage.RequestSave();
			storage.Flush();
		}
	}

	private void NormalizeStateUnsafe()
	{
		state.Players = new Dictionary<string, PlayerAdminRecord>(state.Players ?? new Dictionary<string, PlayerAdminRecord>(), StringComparer.OrdinalIgnoreCase);
		foreach (PlayerAdminRecord value in state.Players.Values)
		{
			PlayerAdminRecord playerAdminRecord = value;
			if (playerAdminRecord.Warnings == null)
			{
				List<AdminWarningRecord> list = (playerAdminRecord.Warnings = new List<AdminWarningRecord>());
			}
			playerAdminRecord = value;
			if (playerAdminRecord.Notes == null)
			{
				List<AdminNoteRecord> list3 = (playerAdminRecord.Notes = new List<AdminNoteRecord>());
			}
			playerAdminRecord = value;
			if (playerAdminRecord.LastKnownName == null)
			{
				PlayerAdminRecord playerAdminRecord2 = playerAdminRecord;
				string obj = value.PlayerUid ?? "";
				string text = obj;
				playerAdminRecord2.LastKnownName = obj;
			}
			playerAdminRecord = value;
			if (playerAdminRecord.MuteReason == null)
			{
				string text = (playerAdminRecord.MuteReason = "");
			}
			playerAdminRecord = value;
			if (playerAdminRecord.MutedBy == null)
			{
				string text = (playerAdminRecord.MutedBy = "");
			}
			playerAdminRecord = value;
			if (playerAdminRecord.BanReason == null)
			{
				string text = (playerAdminRecord.BanReason = "");
			}
			playerAdminRecord = value;
			if (playerAdminRecord.BannedBy == null)
			{
				string text = (playerAdminRecord.BannedBy = "");
			}
			playerAdminRecord = value;
			if (playerAdminRecord.FreezeReason == null)
			{
				string text = (playerAdminRecord.FreezeReason = "");
			}
			playerAdminRecord = value;
			if (playerAdminRecord.FrozenBy == null)
			{
				string text = (playerAdminRecord.FrozenBy = "");
			}
		}
		AdminState adminState = state;
		if (adminState.Audit == null)
		{
			List<AdminAuditRecord> list5 = (adminState.Audit = new List<AdminAuditRecord>());
		}
		state.OfflineInventories = new Dictionary<string, AdminOfflineInventoryRecord>(state.OfflineInventories ?? new Dictionary<string, AdminOfflineInventoryRecord>(), StringComparer.OrdinalIgnoreCase);
		state.InventorySnapshots = new Dictionary<string, List<AdminInventorySnapshotRecord>>(state.InventorySnapshots ?? new Dictionary<string, List<AdminInventorySnapshotRecord>>(), StringComparer.OrdinalIgnoreCase);
		foreach (List<AdminInventorySnapshotRecord> value2 in state.InventorySnapshots.Values)
		{
			foreach (AdminInventorySnapshotRecord item in value2)
			{
				AdminInventorySnapshotRecord adminInventorySnapshotRecord = item;
				if (adminInventorySnapshotRecord.Slots == null)
				{
					List<AdminInventorySnapshotSlot> list7 = (adminInventorySnapshotRecord.Slots = new List<AdminInventorySnapshotSlot>());
				}
				foreach (AdminInventorySnapshotSlot slot in item.Slots)
				{
					AdminInventorySnapshotSlot adminInventorySnapshotSlot = slot;
					if (adminInventorySnapshotSlot.StackBytes == null)
					{
						byte[] array = (adminInventorySnapshotSlot.StackBytes = Array.Empty<byte>());
					}
					if (slot.StackBytes.Length == 0 && !string.IsNullOrWhiteSpace(slot.StackBase64))
					{
						try
						{
							slot.StackBytes = Convert.FromBase64String(slot.StackBase64);
						}
						catch
						{
							slot.StackBytes = Array.Empty<byte>();
						}
					}
					if ((string.IsNullOrWhiteSpace(slot.ItemCode) || slot.StackSize <= 0) && slot.StackBytes.Length != 0)
					{
						ItemStack itemStack = DeserializeSnapshotStack(slot);
						if (itemStack != null)
						{
							slot.ItemCode = itemStack.Collectible?.Code?.ToString() ?? "";
							slot.StackSize = itemStack.StackSize;
						}
					}
					slot.StackBase64 = "";
				}
			}
			while (value2.Count > 12)
			{
				value2.RemoveAt(0);
			}
		}
		state.SchemaVersion = Math.Max(state.SchemaVersion, 3);
	}

	private AdminState SnapshotStateForStorage()
	{
		lock (gate)
		{
			AdminState adminState = new AdminState();
			adminState.SchemaVersion = state.SchemaVersion;
			adminState.MaintenanceMode = state.MaintenanceMode;
			adminState.MaintenanceReason = state.MaintenanceReason;
			adminState.MaintenanceUntilUnixMilliseconds = state.MaintenanceUntilUnixMilliseconds;
			adminState.Audit = state.Audit.Select(CloneAudit).ToList();
			adminState.Players = new Dictionary<string, PlayerAdminRecord>(StringComparer.OrdinalIgnoreCase);
			adminState.InventorySnapshots = new Dictionary<string, List<AdminInventorySnapshotRecord>>(StringComparer.OrdinalIgnoreCase);
			adminState.OfflineInventories = new Dictionary<string, AdminOfflineInventoryRecord>(StringComparer.OrdinalIgnoreCase);
			AdminState adminState2 = adminState;
			foreach (KeyValuePair<string, PlayerAdminRecord> player in state.Players)
			{
				PlayerAdminRecord value = player.Value;
				adminState2.Players[player.Key] = new PlayerAdminRecord
				{
					PlayerUid = value.PlayerUid,
					LastKnownName = value.LastKnownName,
					FirstSeenUnixMilliseconds = value.FirstSeenUnixMilliseconds,
					LastSeenUnixMilliseconds = value.LastSeenUnixMilliseconds,
					TotalPlaySeconds = value.TotalPlaySeconds,
					SessionStartedUnixMilliseconds = value.SessionStartedUnixMilliseconds,
					Warnings = value.Warnings.Select(CloneWarning).ToList(),
					Notes = value.Notes.Select(CloneNote).ToList(),
					MuteUntilUnixMilliseconds = value.MuteUntilUnixMilliseconds,
					PermanentMute = value.PermanentMute,
					MuteReason = value.MuteReason,
					MutedBy = value.MutedBy,
					BanUntilUnixMilliseconds = value.BanUntilUnixMilliseconds,
					BanReason = value.BanReason,
					BannedBy = value.BannedBy,
					Frozen = value.Frozen,
					FreezeUntilUnixMilliseconds = value.FreezeUntilUnixMilliseconds,
					FrozenBy = value.FrozenBy,
					FreezeReason = value.FreezeReason
				};
			}
			foreach (KeyValuePair<string, List<AdminInventorySnapshotRecord>> inventorySnapshot in state.InventorySnapshots)
			{
				adminState2.InventorySnapshots[inventorySnapshot.Key] = inventorySnapshot.Value.Select((AdminInventorySnapshotRecord s) => new AdminInventorySnapshotRecord
				{
					Id = s.Id,
					UnixMilliseconds = s.UnixMilliseconds,
					PlayerUid = s.PlayerUid,
					PlayerName = s.PlayerName,
					CreatedBy = s.CreatedBy,
					Reason = s.Reason,
					OccupiedSlots = s.OccupiedSlots,
					SlotsPayloadLoaded = s.SlotsPayloadLoaded,
					Slots = s.Slots.Select((AdminInventorySnapshotSlot x) => new AdminInventorySnapshotSlot
					{
						Inventory = x.Inventory,
						Slot = x.Slot,
						StackBytes = (x.StackBytes ?? Array.Empty<byte>()),
						ItemCode = x.ItemCode,
						StackSize = x.StackSize
					}).ToList()
				}).ToList();
			}
			foreach (KeyValuePair<string, AdminOfflineInventoryRecord> offlineInventory in state.OfflineInventories)
			{
				adminState2.OfflineInventories[offlineInventory.Key] = offlineInventory.Value;
			}
			return adminState2;
		}
	}

	private void SaveState()
	{
		try
		{
			storage?.Flush();
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Storage] Failed to flush admin state: {0}", ex.Message);
		}
	}

	private void SaveStateUnsafe()
	{
		InvalidatePlayerSummaryCaches();
		storage?.RequestSave();
	}

	public static bool TryParseDuration(string? text, out long milliseconds, out bool permanent)
	{
		milliseconds = 0L;
		permanent = false;
		text = (text ?? "").Trim().ToLowerInvariant();
		if ((text == "permanent" || text == "perm") ? true : false)
		{
			permanent = true;
			return true;
		}
		if (text.Length >= 2)
		{
			string text2 = text;
			if (double.TryParse(text2.Substring(0, text2.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) && !(result < 0.0))
			{
				string? text3 = text;
				char c = text3[text3.Length - 1];
				if (1 == 0)
				{
				}
				int num = c switch
				{
					's' => 1000, 
					'm' => 60000, 
					'h' => 3600000, 
					'd' => 86400000, 
					'w' => 604800000, 
					_ => -1, 
				};
				if (1 == 0)
				{
				}
				double num2 = num;
				if (num2 < 0.0)
				{
					return false;
				}
				double num3 = result * num2;
				if (!double.IsFinite(num3) || num3 > 922337203685477.0)
				{
					return false;
				}
				milliseconds = (long)num3;
				return true;
			}
		}
		return false;
	}

	public static string FormatDuration(long milliseconds)
	{
		TimeSpan timeSpan = TimeSpan.FromTicks(Math.Min(922337203685477L, Math.Max(0L, milliseconds)) * 10000);
		if (!(timeSpan.TotalDays >= 1.0))
		{
			if (!(timeSpan.TotalHours >= 1.0))
			{
				if (!(timeSpan.TotalMinutes >= 1.0))
				{
					return $"{Math.Max(1.0, timeSpan.TotalSeconds):0}s";
				}
				return $"{timeSpan.TotalMinutes:0.#}m";
			}
			return $"{timeSpan.TotalHours:0.#}h";
		}
		return $"{timeSpan.TotalDays:0.#}d";
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}

	private static long DeadlineAfterMilliseconds(long delay)
	{
		long num = NowMs();
		return num + Math.Min(long.MaxValue - num, Math.Max(0L, delay));
	}

	private static AdminWarningRecord CloneWarning(AdminWarningRecord x)
	{
		return new AdminWarningRecord
		{
			Id = x.Id,
			UnixMilliseconds = x.UnixMilliseconds,
			IssuedBy = x.IssuedBy,
			Reason = x.Reason,
			Removed = x.Removed,
			RemovedBy = x.RemovedBy,
			RemovedUnixMilliseconds = x.RemovedUnixMilliseconds
		};
	}

	private static AdminNoteRecord CloneNote(AdminNoteRecord x)
	{
		return new AdminNoteRecord
		{
			Id = x.Id,
			UnixMilliseconds = x.UnixMilliseconds,
			Author = x.Author,
			Text = x.Text,
			Removed = x.Removed
		};
	}

	private static AdminAuditRecord CloneAudit(AdminAuditRecord x)
	{
		return new AdminAuditRecord
		{
			Id = x.Id,
			UnixMilliseconds = x.UnixMilliseconds,
			Actor = x.Actor,
			Action = x.Action,
			TargetUid = x.TargetUid,
			TargetName = x.TargetName,
			Detail = x.Detail,
			Source = x.Source
		};
	}

	public void Dispose()
	{
		if (!disposed)
		{
			OnGameWorldSave();
			disposed = true;
			SaveState();
			api.Event.PlayerJoin -= OnPlayerJoin;
			api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
			api.Event.PlayerDisconnect -= OnPlayerDisconnect;
			api.Event.PlayerChat -= OnPlayerChat;
			api.Event.CanPlaceOrBreakBlock -= OnCanPlaceOrBreakBlock;
			api.Event.CanUseBlock -= OnCanUseBlock;
			api.Event.OnPlayerInteractEntity -= OnPlayerInteractEntity;
			api.Event.GameWorldSave -= OnGameWorldSave;
			if (tickListenerId >= 0)
			{
				api.Event.UnregisterGameTickListener(tickListenerId);
			}
			tickListenerId = -1L;
			if (freezeTickListenerId >= 0)
			{
				api.Event.UnregisterGameTickListener(freezeTickListenerId);
			}
			freezeTickListenerId = -1L;
			IServerPlayer[] array = OnlinePlayers().ToArray();
			foreach (IServerPlayer player in array)
			{
				RestoreGhostOnDisconnect(player);
			}
			ghostStates.Clear();
			lock (onlineGate)
			{
				onlinePlayersByUid.Clear();
				onlinePlayersByName.Clear();
			}
			jailService?.Dispose();
			jailService = null;
			storage?.Dispose();
			storage = null;
		}
	}
}
