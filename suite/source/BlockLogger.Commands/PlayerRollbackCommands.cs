using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Models;
using BlockLogger.Services;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Commands;

public sealed class PlayerRollbackCommands : IDisposable
{
	private sealed record Prepared(RecoveryOperation Operation, string LiveHash);

	private sealed record Plan(string Token, string Uid, bool Area, DateTime From, DateTime To, DateTime Expires, bool Inventories, bool Entities, bool IncludeClaims, RecoveryCell? Center, int Radius, List<Prepared> Changes);

	private readonly ICoreServerAPI api;

	private readonly LogStorageService storage;

	private readonly Action<int, int, int>? dirty;

	private readonly Dictionary<string, Plan> plans = new Dictionary<string, Plan>();

	private readonly HashSet<string> changed = new HashSet<string>();

	private readonly HashSet<string> trackedCells = new HashSet<string>();

	private bool trackAllDuringRead;

	private long maintenanceTick;

	private readonly HashSet<string> receipts = new HashSet<string>();

	private readonly List<(InventoryBase Inventory, Action<int> Handler)> watchers = new List<(InventoryBase, Action<int>)>();

	private readonly string receiptPath;

	private bool busy;

	private bool receiptReady;

	private string receiptFailure = "Receipt index is initializing; try again shortly.";

	private readonly RecoveryReceiptIndex receiptIndex;

	private volatile bool disposed;

	private volatile bool cancelRequested;

	private CancellationTokenSource? operationCancellation;

	private readonly CancellationTokenSource lifetime = new CancellationTokenSource();

	private string activeAdmin = "";

	public PlayerRollbackCommands(ICoreServerAPI api, LogStorageService storage, Action<int, int, int>? dirty)
	{
		this.api = api;
		this.storage = storage;
		this.dirty = dirty;
		receiptPath = storage.DatabasePath + ".recovery-receipts.jsonl";
		receiptIndex = new RecoveryReceiptIndex(receiptPath);
	}

	public void Register()
	{
		InitializeReceiptsAsync();
		maintenanceTick = api.Event.RegisterGameTickListener((float _) =>
		{
			PruneExpiredPlans();
		}, 30000);
		api.Permissions.RegisterPrivilege("newfies.rollback", "Preview and execute historical recovery");
		api.RegisterCommand("rollback", "Recover available historical player or area changes", "preview player <name|uid> <time> [radius N] OR preview area <radius> <time>; optional --inventories --replace-entities; execute <token>; cancel", Command, "newfies.rollback");
		api.Event.DidBreakBlock += Break;
		api.Event.DidPlaceBlock += Place;
		api.Event.DidUseBlock += Use;
	}

	private void Reply(IServerPlayer p, int g, string text, bool error = false)
	{
		p.SendMessage(g, text, error ? EnumChatType.CommandError : EnumChatType.CommandSuccess);
	}

	private void Touch(BlockSelection s)
	{
		if (s?.Position == null || (!busy && plans.Count == 0))
		{
			return;
		}
		BlockPos position = s.Position;
		string key = new RecoveryCell(position.dimension.ToString(CultureInfo.InvariantCulture), position.X, position.Y, position.Z).Key;
		if (trackAllDuringRead || trackedCells.Contains(key))
		{
			if (changed.Count >= 131072 && !changed.Contains(key))
			{
				cancelRequested = true;
				operationCancellation?.Cancel();
			}
			else
			{
				changed.Add(key);
			}
		}
	}

	private void Break(IServerPlayer p, int id, BlockSelection s)
	{
		Touch(s);
	}

	private void Place(IServerPlayer p, int id, BlockSelection s, ItemStack stack)
	{
		Touch(s);
	}

	private void Use(IServerPlayer p, BlockSelection s)
	{
		Touch(s);
	}

	private void PruneExpiredPlans()
	{
		if (!busy)
		{
			string[] array = (from p in plans
				where p.Value.Expires <= DateTime.UtcNow
				select p.Key).ToArray();
			foreach (string key in array)
			{
				plans.Remove(key);
			}
			if (plans.Count == 0)
			{
				ClearWatchers();
				changed.Clear();
				trackedCells.Clear();
				receipts.Clear();
			}
		}
	}

	private void ClearWatchers()
	{
		foreach (var (inventoryBase, value) in watchers)
		{
			inventoryBase.SlotModified -= value;
		}
		watchers.Clear();
	}

	private void Watch(RecoveryOperation op)
	{
		BlockPos blockPos = Position(op.Cell);
		if (blockPos != null && api.World.BlockAccessor.GetBlockEntity(blockPos) is IBlockEntityContainer { Inventory: InventoryBase inventory })
		{
			string key = op.Cell.Key;
			Action<int> action = (int _) =>
			{
				changed.Add(key);
			};
			inventory.SlotModified += action;
			watchers.Add((inventory, action));
		}
	}

	private void Command(IServerPlayer p, int g, CmdArgs args)
	{
		if (disposed)
		{
			return;
		}
		if (!receiptReady)
		{
			Reply(p, g, receiptFailure, error: true);
			return;
		}
		string text = args.PopWord()?.ToLowerInvariant();
		if (text == "cancel")
		{
			if (busy && activeAdmin != p.PlayerUID)
			{
				Reply(p, g, "Another administrator owns the active operation.", error: true);
			}
			else if (busy)
			{
				cancelRequested = true;
				operationCancellation?.Cancel();
				Reply(p, g, "Cancellation requested; already applied changes remain journaled.");
			}
			else
			{
				plans.Clear();
				ClearWatchers();
				changed.Clear();
				Reply(p, g, "Preview cleared.");
			}
			return;
		}
		if (busy)
		{
			Reply(p, g, "Recovery operation is running; its owner may /rollback cancel.", error: true);
			return;
		}
		if (text == "execute")
		{
			string text2 = args.PopWord();
			if (args.Length != 0 || !plans.TryGetValue(p.PlayerUID, out Plan value) || text2 != value.Token || value.Expires < DateTime.UtcNow)
			{
				Reply(p, g, "No matching unexpired preview. Request another preview.", error: true);
				return;
			}
			if (Occupied(value, p.PlayerUID))
			{
				Reply(p, g, value.Area ? "Other players must leave the selected area before execution." : "Target must be offline.", error: true);
				return;
			}
			plans.Clear();
			busy = true;
			cancelRequested = false;
			activeAdmin = p.PlayerUID;
			operationCancellation = new CancellationTokenSource();
			Execute(p, g, value);
			return;
		}
		string text3 = ((!(text == "preview")) ? null : args.PopWord()?.ToLowerInvariant());
		bool flag = text3 == "area";
		bool flag2 = text != "preview";
		bool flag3 = flag2;
		if (!flag3)
		{
			bool flag4 = ((text3 == "player" || text3 == "area") ? true : false);
			flag3 = !flag4;
		}
		if (flag3)
		{
			Reply(p, g, "/rollback preview player <name|uid> <time> [radius N]; /rollback preview area <radius> <time> [--inventories] [--replace-entities]; /rollback execute <token>", error: true);
			return;
		}
		string text4 = null;
		int result = 0;
		RecoveryCell center = null;
		if (flag)
		{
			if (!int.TryParse(args.PopWord(), out result) || result < 1 || result > 4096 || !EntityPositionResolver.TryGetBlockPos(p.Entity, out BlockPos pos))
			{
				Reply(p, g, "Area radius must be 1..4096; stand at the desired center.", error: true);
				return;
			}
			center = new RecoveryCell(pos.dimension.ToString(CultureInfo.InvariantCulture), pos.X, pos.Y, pos.Z);
		}
		else
		{
			text4 = args.PopWord();
		}
		string text5 = args.PopWord();
		DateTime utc = DateTime.UtcNow;
		DateTime utc2;
		if (text5 == "from")
		{
			string s = args.PopWord();
			string text6 = args.PopWord();
			string s2 = args.PopWord();
			if (!Utc(s, out utc2) || text6 != "to" || !Utc(s2, out utc) || utc2 >= utc || utc > DateTime.UtcNow || utc - utc2 > TimeSpan.FromDays(7))
			{
				Reply(p, g, "Use ordered UTC timestamps YYYY-MM-DDTHH:mm:ssZ, maximum 7 days apart.", error: true);
				return;
			}
		}
		else
		{
			if (!Duration(text5, out var span))
			{
				Reply(p, g, "Duration: 1s through 7d; units s/m/h/d.", error: true);
				return;
			}
			utc2 = utc - span;
		}
		bool inventories = false;
		bool entities = false;
		bool includeClaims = false;
		while (args.Length > 0)
		{
			string text7 = args.PopWord()?.ToLowerInvariant();
			switch (text7)
			{
			case "--include-claims":
				includeClaims = true;
				continue;
			case "--inventories":
				inventories = true;
				continue;
			case "--replace-entities":
				entities = true;
				continue;
			}
			if (!flag && text7 == "radius" && int.TryParse(args.PopWord(), out result) && result >= 1 && result <= 4096 && EntityPositionResolver.TryGetBlockPos(p.Entity, out BlockPos pos2))
			{
				center = new RecoveryCell(pos2.dimension.ToString(CultureInfo.InvariantCulture), pos2.X, pos2.Y, pos2.Z);
				continue;
			}
			Reply(p, g, "Unknown option. Use --inventories, --replace-entities, --include-claims, or radius 1..4096.", error: true);
			return;
		}
		if (!flag && string.IsNullOrWhiteSpace(text4))
		{
			Reply(p, g, "Specify an exact name or UID.", error: true);
			return;
		}
		plans.Clear();
		ClearWatchers();
		changed.Clear();
		trackedCells.Clear();
		trackAllDuringRead = true;
		busy = true;
		cancelRequested = false;
		activeAdmin = p.PlayerUID;
		operationCancellation = new CancellationTokenSource();
		Preview(p, g, text4 ?? "", flag, utc2, utc, inventories, entities, includeClaims, center, result);
		Reply(p, g, "Reading existing event rows and container snapshots; no new history is required.");
	}

	private static bool Utc(string? s, out DateTime utc)
	{
		return DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
	}

	private static bool Duration(string? s, out TimeSpan span)
	{
		span = default;
		if (s != null && s.Length >= 2)
		{
			if (double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result))
			{
				char c = s[s.Length - 1];
				if (1 == 0)
				{
				}
				int num = c switch
				{
					's' => 1, 
					'm' => 60, 
					'h' => 3600, 
					'd' => 86400, 
					_ => 0, 
				};
				if (1 == 0)
				{
				}
				double num2 = num;
				double num3 = result * num2;
				if (!double.IsFinite(num3) || num3 < 1.0 || num3 > 604800.0)
				{
					return false;
				}
				span = TimeSpan.FromSeconds(num3);
				return true;
			}
		}
		return false;
	}

	private static bool InRadius(RecoveryOperation op, RecoveryCell? center, int radius)
	{
		if (center == null)
		{
			return true;
		}
		if (op.Cell.Dimension != center.Dimension)
		{
			return false;
		}
		double num = (double)op.Cell.X - (double)center.X;
		double num2 = (double)op.Cell.Y - (double)center.Y;
		double num3 = (double)op.Cell.Z - (double)center.Z;
		return num * num + num2 * num2 + num3 * num3 <= (double)radius * (double)radius;
	}

	private bool Occupied(Plan plan, string adminUid)
	{
		if (!plan.Area)
		{
			return api.World.AllOnlinePlayers.Any((IPlayer x) => x.PlayerUID == plan.Uid);
		}
		return api.World.AllOnlinePlayers.Any((IPlayer x) => x.PlayerUID != adminUid && EntityPositionResolver.TryGetBlockPos(x.Entity, out BlockPos pos) && InRadius(new RecoveryOperation
		{
			Cell = new RecoveryCell(pos.dimension.ToString(CultureInfo.InvariantCulture), pos.X, pos.Y, pos.Z)
		}, plan.Center, plan.Radius));
	}

	private async Task Preview(IServerPlayer p, int g, string target, bool area, DateTime from, DateTime to, bool inventories, bool entities, bool includeClaims, RecoveryCell? center, int radius)
	{
		try
		{
			CancellationToken cancellation = operationCancellation.Token;
			RollbackHistory history = await storage.QueryRollbackRecoveryAsync(target, from, to, area ? center : null, area ? radius : 0, cancellation).ConfigureAwait(continueOnCapturedContext: false);
			RecoveryBuild build = await Task.Run(() => RollbackRecoveryPlanner.Build(history, from, to, inventories, entities, cancellation), cancellation).ConfigureAwait(continueOnCapturedContext: false);
			HashSet<string> relevantReceipts = await receiptIndex.FindAsync(build.Operations.Select((RecoveryOperation op) => op.ReceiptKey), cancellation).ConfigureAwait(continueOnCapturedContext: false);
			api.Event.EnqueueMainThreadTask(() =>
			{
				receipts.Clear();
				receipts.UnionWith(relevantReceipts);
				trackedCells.Clear();
				trackedCells.UnionWith(build.Operations.Select((RecoveryOperation op) => op.Cell.Key));
				changed.IntersectWith(trackedCells);
				trackAllDuringRead = false;
				List<Prepared> list = new List<Prepared>();
				int index = 0;
				int entityCount = 0;
				Batch(0f);
				void Batch(float dt)
				{
					try
					{
						string token;
						Plan plan;
						string report;
						if (disposed)
						{
							Finish();
						}
						else if (cancelRequested)
						{
							Finish();
							Reply(p, g, "Preview cancelled.");
						}
						else
						{
							Stopwatch stopwatch = Stopwatch.StartNew();
							for (int num = Math.Min(index + 64, build.Operations.Count); index < num && (index <= 0 || stopwatch.ElapsedMilliseconds < 4); index++)
							{
								RecoveryOperation recoveryOperation = build.Operations[index];
								if (!InRadius(recoveryOperation, center, radius))
								{
									build.Skip("Outside selected radius/dimension");
								}
								else if (receipts.Contains(recoveryOperation.ReceiptKey))
								{
									build.Skip("Already recovered or uncertain prior attempt; receipt retained");
								}
								else if (changed.Contains(recoveryOperation.Cell.Key))
								{
									build.Skip("Live changes during preview");
								}
								else if (recoveryOperation.Kind == "entity-replacement" && entityCount >= 500)
								{
									build.Skip("Entity replacement cap (500)");
								}
								else
								{
									string text = Validate(recoveryOperation, out string hash, p, includeClaims);
									if (text != null)
									{
										build.Skip(text);
									}
									else
									{
										list.Add(new Prepared(recoveryOperation, hash));
										Watch(recoveryOperation);
										if (recoveryOperation.Kind == "entity-replacement")
										{
											entityCount++;
										}
									}
								}
							}
							if (index < build.Operations.Count)
							{
								api.Event.RegisterCallback(Batch, 10);
							}
							else
							{
								token = Guid.NewGuid().ToString("N").Substring(0, 12);
								plan = new Plan(token, history.PlayerUid, area, from, to, DateTime.UtcNow.AddMinutes(10.0), inventories, entities, includeClaims, center, radius, list);
								report = storage.DatabasePath + ".recovery-preview-" + token + ".json";
								PublishPreviewAsync();
							}
						}
						async Task PublishPreviewAsync()
						{
							try
							{
								await Task.Run(() =>
								{
									File.WriteAllText(report, JsonConvert.SerializeObject(new
									{
										token = token,
										mode = (plan.Area ? "area" : "player"),
										uid = plan.Uid,
										center = plan.Center,
										radius = plan.Radius,
										from = from,
										to = to,
										expires = plan.Expires,
										includeClaims = plan.IncludeClaims,
										affectedChunks = list.Select((Prepared x) => new
										{
											Dimension = x.Operation.Cell.Dimension,
											x = x.Operation.Cell.X >> 5,
											y = x.Operation.Cell.Y >> 5,
											z = x.Operation.Cell.Z >> 5
										}).Distinct().Count(),
										inventoryFidelity = "codes/counts only; old item attributes not logged",
										entityFidelity = "replacement creatures; old attributes not logged",
										eligible = list.Select((Prepared x) => x.Operation).ToArray(),
										reasons = build.Reasons
									}, Formatting.Indented));
								}).ConfigureAwait(continueOnCapturedContext: false);
								await OnGameThreadAsync(() =>
								{
									if (cancelRequested)
									{
										Finish();
										Reply(p, g, "Preview cancelled.");
									}
									else
									{
										plans[p.PlayerUID] = plan;
										busy = false;
										activeAdmin = "";
										operationCancellation?.Dispose();
										operationCancellation = null;
										Reply(p, g, $"{(plan.Area ? $"AREA: all actors, center={plan.Center.Key}, radius={plan.Radius}" : ("UID=" + plan.Uid))}; UTC {from:O} through {to:O}. Eligible: {list.Count((Prepared x) => x.Operation.Kind == "block")} block locations, {list.Count((Prepared x) => x.Operation.Kind == "inventory")} existing inventories, {entityCount} replacement creatures.");
										Reply(p, g, plan.IncludeClaims ? "Claimed land included explicitly; current game access permissions are still checked." : "Claimed land excluded. Use --include-claims for an intentional claimed-area recovery.");
										foreach (KeyValuePair<string, int> item in build.Reasons.Take(10))
										{
											Reply(p, g, $"Skipped: {item.Value} — {item.Key}");
										}
										if (inventories)
										{
											Reply(p, g, "Inventory recovery restores saved item codes/counts; original durability, freshness and other attributes were not logged. Taken items/drops are not confiscated.");
										}
										if (entities)
										{
											Reply(p, g, "Entity replacements have default attributes, not the original generation, ownership, names or training.");
										}
										if (plan.Area)
										{
											Reply(p, g, "Area mode includes everyone’s logged changes in the selected window. Clear other players from the area; later changes are excluded. Creature replacements may include natural deaths.");
										}
										Reply(p, g, $"Full report: {Path.GetFileName(report)}. {(plan.Area ? "Clear area" : "Keep target offline")}; backup first. /rollback execute {token} (10-minute expiry).");
									}
								}).ConfigureAwait(continueOnCapturedContext: false);
							}
							catch (Exception ex5)
							{
								Exception ex6 = ex5;
								if (!disposed)
								{
									await OnGameThreadAsync(() =>
									{
										Finish();
										Reply(p, g, "Preview failed: " + ex6.Message, error: true);
									}).ConfigureAwait(continueOnCapturedContext: false);
								}
							}
						}
					}
					catch (Exception ex4)
					{
						Finish();
						Reply(p, g, "Preview failed: " + ex4.Message, error: true);
					}
				}
			}, "recovery-preview");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!disposed)
			{
				api.Event.EnqueueMainThreadTask(() =>
				{
					Finish();
					Reply(p, g, cancelRequested ? "Preview cancelled." : ("Preview failed: " + ex3.Message), error: true);
				}, "recovery-preview-error");
			}
		}
	}

	private BlockPos? Position(RecoveryCell c)
	{
		int result;
		return (int.TryParse(c.Dimension, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result >= 0) ? new BlockPos(c.X, c.Y, c.Z, result) : null;
	}

	private static bool Micro(Block b)
	{
		AssetLocation code = b.Code;
		int result;
		if ((object)code == null || !code.Path.Contains("chisel", StringComparison.OrdinalIgnoreCase))
		{
			AssetLocation code2 = b.Code;
			if ((object)code2 == null || !code2.Path.Contains("microblock", StringComparison.OrdinalIgnoreCase))
			{
				result = ((b.EntityClass?.Contains("Micro", StringComparison.OrdinalIgnoreCase) ?? false) ? 1 : 0);
				goto IL_0059;
			}
		}
		result = 1;
		goto IL_0059;
		IL_0059:
		return (byte)result != 0;
	}

	private static bool Multi(Block b)
	{
		return b.GetType().Name.Contains("Multiblock", StringComparison.OrdinalIgnoreCase) || (b.Code?.Path.Contains("multiblock", StringComparison.OrdinalIgnoreCase) ?? false);
	}

	private static bool BasicContainer(Block b)
	{
		return b.EntityClass != null && (b.EntityClass.Contains("GenericContainer", StringComparison.OrdinalIgnoreCase) || b.EntityClass.Contains("Chest", StringComparison.OrdinalIgnoreCase) || b.EntityClass.Contains("Basket", StringComparison.OrdinalIgnoreCase) || b.EntityClass.Contains("Trunk", StringComparison.OrdinalIgnoreCase));
	}

	private string? Validate(RecoveryOperation op, out string hash, IServerPlayer? actor = null, bool includeClaims = false)
	{
		hash = "";
		BlockPos blockPos = Position(op.Cell);
		if (blockPos == null)
		{
			return "Unrecognized dimension";
		}
		if (!includeClaims)
		{
			ILandClaimAPI claims = api.World.Claims;
			if (claims != null && claims.Get(blockPos)?.Any() == true)
			{
				return "Protected claimed land (use --include-claims explicitly)";
			}
		}
		if (actor != null && api.World.Claims != null && api.World.Claims.TestAccess(actor, blockPos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted)
		{
			return "Current land access denied";
		}
		IBlockAccessor blockAccessor = api.World.BlockAccessor;
		if (!blockAccessor.IsValidPos(blockPos) || blockAccessor.GetChunkAtBlockPos(blockPos) == null)
		{
			return "Unloaded/out-of-world location (visit area and preview again)";
		}
		if (op.Kind == "entity-replacement")
		{
			if (blockPos.dimension != 0)
			{
				return "Entity replacement supports overworld only";
			}
			if (blockAccessor.GetBlock(blockPos, 1).Replaceable < 6000)
			{
				return "Logged entity position is obstructed; restore terrain then preview again";
			}
			if (api.World.GetEntityById(op.OriginalEntityId) != null)
			{
				return "Original entity/corpse still loaded";
			}
			EntityProperties entityType = api.World.GetEntityType(new AssetLocation(op.EntityCode));
			if (entityType == null)
			{
				return "Missing entity type";
			}
			Entity entity = api.World.ClassRegistry.CreateEntity(entityType);
			if (!(entity is EntityAgent) || entity is EntityPlayer)
			{
				return "Entity is not a recoverable creature";
			}
			hash = "absent:" + op.OriginalEntityId;
			return null;
		}
		Block block = api.World.GetBlock(new AssetLocation(op.Before));
		Block block2 = api.World.GetBlock(new AssetLocation(op.After));
		if (block == null || block2 == null)
		{
			return "Missing block type";
		}
		if (block.ForFluidsLayer || block2.ForFluidsLayer || blockAccessor.GetBlock(blockPos, 2).BlockId != 0)
		{
			return "Fluid state not captured by this logger";
		}
		if (Multi(block) || Multi(block2))
		{
			return "Multiblock structure requires unavailable linked state";
		}
		if (blockAccessor.GetBlock(blockPos, 1).BlockId != block2.BlockId)
		{
			return "Current block differs from recorded final state";
		}
		Block[] decors = blockAccessor.GetDecors(blockPos);
		if (decors != null && decors.Any((Block x) => x != null && x.BlockId != 0))
		{
			return "Live decor state would be affected";
		}
		if (op.Kind == "block" && !string.IsNullOrWhiteSpace(block.EntityClass))
		{
			if (Micro(block))
			{
				return "Original chisel voxel/material data absent from existing DB";
			}
			if (op.InventoryBefore == null || !BasicContainer(block))
			{
				return "Original block-entity data unavailable (sign/door/machine/etc.)";
			}
		}
		BlockEntity blockEntity = blockAccessor.GetBlockEntity(blockPos);
		if (!string.IsNullOrWhiteSpace(block2.EntityClass) && blockEntity == null)
		{
			return "Current block entity missing";
		}
		if (op.Kind == "inventory")
		{
			if (!(blockEntity is IBlockEntityContainer { Inventory: not null } blockEntityContainer))
			{
				return "Live inventory missing";
			}
			if (blockEntityContainer.Inventory.Count != op.SlotCount || !SlotsEqual(blockEntityContainer.Inventory, op.InventoryAfter))
			{
				return "Live container differs from reconstructed final inventory";
			}
		}
		if (op.InventoryBefore != null)
		{
			foreach (RecoverySlot item in op.InventoryBefore)
			{
				if (ResolveStack(item.Code, item.Count) == null)
				{
					return "Historical item code missing or invalid stack count";
				}
			}
			if (op.Kind == "block" && blockEntity is IBlockEntityContainer blockEntityContainer2 && (blockEntityContainer2.Inventory.Count != op.SlotCount || !SlotsEqual(blockEntityContainer2.Inventory, op.InventoryAfter)))
			{
				return "Live container inventory changed";
			}
		}
		hash = LiveHash(blockPos);
		return null;
	}

	private static bool SlotsEqual(IInventory inventory, List<RecoverySlot>? saved)
	{
		if (saved == null)
		{
			return false;
		}
		Dictionary<int, RecoverySlot> dictionary = saved.ToDictionary((RecoverySlot s) => s.Slot);
		for (int num = 0; num < inventory.Count; num++)
		{
			ItemStack itemStack = inventory[num]?.Itemstack;
			if (!dictionary.TryGetValue(num, out var value))
			{
				if (itemStack != null && itemStack.StackSize > 0)
				{
					return false;
				}
			}
			else if (itemStack == null || itemStack.StackSize != value.Count || RollbackRecoveryPlanner.Canonical(itemStack.Collectible.Code.ToShortString()) != RollbackRecoveryPlanner.Canonical(value.Code))
			{
				return false;
			}
		}
		return true;
	}

	private ItemStack? ResolveStack(string code, int count)
	{
		if (count <= 0)
		{
			return null;
		}
		AssetLocation assetLocation = new AssetLocation(code);
		Item item = api.World.GetItem(assetLocation);
		Block block = api.World.GetBlock(assetLocation);
		if (item != null && block != null && block.BlockId != 0)
		{
			return null;
		}
		ItemStack itemStack;
		if (item != null)
		{
			itemStack = new ItemStack(item, count);
		}
		else
		{
			itemStack = ((block != null && block.BlockId != 0) ? new ItemStack(block, count) : null);
		}
		return (itemStack != null && count <= itemStack.Collectible.MaxStackSize) ? itemStack : null;
	}

	private string LiveHash(BlockPos pos)
	{
		TreeAttribute treeAttribute = new TreeAttribute();
		api.World.BlockAccessor.GetBlockEntity(pos)?.ToTreeAttributes(treeAttribute);
		using MemoryStream memoryStream = new MemoryStream();
		using (BinaryWriter stream = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
		{
			treeAttribute.ToBytes(stream);
		}
		return Convert.ToHexString(SHA256.HashData(memoryStream.ToArray()));
	}

	private object Capture(BlockPos pos)
	{
		TreeAttribute treeAttribute = new TreeAttribute();
		api.World.BlockAccessor.GetBlockEntity(pos)?.ToTreeAttributes(treeAttribute);
		using MemoryStream memoryStream = new MemoryStream();
		using (BinaryWriter stream = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
		{
			treeAttribute.ToBytes(stream);
		}
		return new
		{
			block = api.World.BlockAccessor.GetBlock(pos, 1).Code.ToShortString(),
			blockEntityBase64 = Convert.ToBase64String(memoryStream.ToArray())
		};
	}

	private void RestoreSlots(IInventory inv, List<RecoverySlot> desired)
	{
		Dictionary<int, RecoverySlot> dictionary = desired.ToDictionary((RecoverySlot x) => x.Slot);
		ItemStack[] array = new ItemStack[inv.Count];
		for (int num = 0; num < inv.Count; num++)
		{
			if (dictionary.TryGetValue(num, out var value))
			{
				ItemStack itemStack = inv[num]?.Itemstack;
				if (itemStack != null && RollbackRecoveryPlanner.Canonical(itemStack.Collectible.Code.ToShortString()) == RollbackRecoveryPlanner.Canonical(value.Code))
				{
					array[num] = itemStack.Clone();
					array[num].StackSize = value.Count;
				}
				else
				{
					array[num] = ResolveStack(value.Code, value.Count) ?? throw new InvalidOperationException("Missing historical item type.");
				}
			}
		}
		for (int num2 = 0; num2 < inv.Count; num2++)
		{
			if (inv[num2] == null)
			{
				throw new InvalidOperationException("Inventory slot is unavailable; rollback was not applied.");
			}
		}
		for (int num3 = 0; num3 < inv.Count; num3++)
		{
			ItemSlot itemSlot = inv[num3];
			itemSlot.Itemstack = array[num3];
			itemSlot.MarkDirty();
		}
	}

	private async Task InitializeReceiptsAsync()
	{
		try
		{
			await receiptIndex.InitializeAsync(lifetime.Token).ConfigureAwait(continueOnCapturedContext: false);
			await OnGameThreadAsync(() =>
			{
				receiptReady = true;
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!disposed)
			{
				api.Event.EnqueueMainThreadTask(() =>
				{
					receiptFailure = "Recovery disabled: receipt journal/index could not be read. Preserve the journal and check the server error log.";
					api.Logger.Error("[Recovery] Receipt initialization failed: {0}", ex3.Message);
				}, "recovery-receipt-index-failed");
			}
		}
	}

	private Task OnGameThreadAsync(Action action)
	{
		TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationTokenRegistration registration = lifetime.Token.Register(() =>
		{
			completion.TrySetCanceled(lifetime.Token);
		});
		api.Event.EnqueueMainThreadTask(() =>
		{
			if (!completion.Task.IsCompleted && !disposed)
			{
				try
				{
					action();
					completion.TrySetResult();
				}
				catch (Exception exception)
				{
					completion.TrySetException(exception);
				}
			}
			registration.Dispose();
		}, "recovery-step");
		return completion.Task;
	}

	private async Task Execute(IServerPlayer p, int g, Plan plan)
	{
		string administratorUid = p.PlayerUID;
		string administratorName = p.PlayerName;
		int blocks = 0;
		int inventories = 0;
		int entities = 0;
		int skipped = 0;
		int index = 0;
		string audit = storage.DatabasePath + ".recovery-" + plan.Token + ".jsonl";
		try
		{
			await using RecoveryJournal journal = new RecoveryJournal(audit, receiptPath);
			CancellationToken cancellation = operationCancellation.Token;
			RollbackHistory history = await storage.QueryRollbackRecoveryAsync(plan.Uid, plan.From, plan.To, plan.Area ? plan.Center : null, plan.Area ? plan.Radius : 0, cancellation).ConfigureAwait(continueOnCapturedContext: false);
			Dictionary<string, RecoveryOperation> fresh = (await Task.Run(() => RollbackRecoveryPlanner.Build(history, plan.From, plan.To, plan.Inventories, plan.Entities, cancellation), cancellation).ConfigureAwait(continueOnCapturedContext: false)).Operations.ToDictionary((RecoveryOperation x) => x.ReceiptKey);
			foreach (Prepared prepared in plan.Changes)
			{
				int num = index;
				index = num + 1;
				RecoveryOperation op = prepared.Operation;
				string reason = null;
				object current = null;
				await OnGameThreadAsync(() =>
				{
					if (cancelRequested)
					{
						throw new OperationCanceledException();
					}
					CheckOccupancy();
					if (receipts.Contains(op.ReceiptKey) || changed.Contains(op.Cell.Key) || !fresh.TryGetValue(op.ReceiptKey, out var value) || value.Fingerprint != op.Fingerprint)
					{
						reason = "Receipt, live change or changed historical plan";
					}
					else
					{
						reason = Validate(op, out string hash, p, plan.IncludeClaims);
						if (reason == null && hash != prepared.LiveHash)
						{
							reason = "Live block-entity state changed since preview";
						}
					}
					if (reason == null && op.Kind != "entity-replacement")
					{
						current = Capture(Position(op.Cell));
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				if (reason != null)
				{
					num = skipped;
					skipped = num + 1;
					await journal.AppendAsync(new
					{
						phase = "skipped",
						operation = op,
						reason = reason,
						utc = DateTime.UtcNow
					}).ConfigureAwait(continueOnCapturedContext: false);
					if (index % 256 == 0)
					{
						await OnGameThreadAsync(() =>
						{
							Reply(p, g, $"Recovery progress: {index}/{plan.Changes.Count}, skipped={skipped}.");
						}).ConfigureAwait(continueOnCapturedContext: false);
					}
					continue;
				}
				await journal.AppendAsync(new
				{
					phase = "intent",
					utc = DateTime.UtcNow,
					admin = administratorUid,
					mode = (plan.Area ? "area" : "player"),
					target = plan.Uid,
					center = plan.Center,
					radius = plan.Radius,
					from = plan.From,
					to = plan.To,
					operation = op,
					current = current
				}, new
				{
					key = op.ReceiptKey,
					phase = "intent",
					token = plan.Token,
					utc = DateTime.UtcNow
				}).ConfigureAwait(continueOnCapturedContext: false);
				object applied = null;
				await OnGameThreadAsync(() =>
				{
					receipts.Add(op.ReceiptKey);
					if (cancelRequested)
					{
						throw new OperationCanceledException();
					}
					CheckOccupancy();
					reason = Validate(op, out string hash, p, plan.IncludeClaims);
					if (changed.Contains(op.Cell.Key) || reason != null || hash != prepared.LiveHash)
					{
						if (reason == null)
						{
							reason = "Live state changed while writing durable intent";
						}
					}
					else
					{
						BlockPos blockPos = Position(op.Cell);
						if (op.Kind == "entity-replacement")
						{
							EntityProperties entityType = api.World.GetEntityType(new AssetLocation(op.EntityCode));
							Entity entity = api.World.ClassRegistry.CreateEntity(entityType);
							if (!EntityPositionResolver.TrySetPose(entity, (double)blockPos.X + 0.5, blockPos.Y, (double)blockPos.Z + 0.5, 0f, 0f))
							{
								throw new InvalidOperationException("Entity pose setter unavailable.");
							}
							api.World.SpawnEntity(entity);
							int num2 = entities;
							entities = num2 + 1;
							applied = new
							{
								phase = "applied",
								operation = op,
								createdEntityId = entity.EntityId
							};
						}
						else
						{
							if (op.Kind == "block")
							{
								api.World.BlockAccessor.SetBlock(api.World.GetBlock(new AssetLocation(op.Before)).BlockId, blockPos, 1);
								int num2 = blocks;
								blocks = num2 + 1;
							}
							if (op.InventoryBefore != null)
							{
								if (!(api.World.BlockAccessor.GetBlockEntity(blockPos) is IBlockEntityContainer blockEntityContainer) || blockEntityContainer.Inventory.Count != op.SlotCount)
								{
									throw new InvalidOperationException("Container initialization/slot layout differs; block may have been restored but inventory was not.");
								}
								RestoreSlots(blockEntityContainer.Inventory, op.InventoryBefore);
								int num2 = inventories;
								inventories = num2 + 1;
								api.World.BlockAccessor.GetBlockEntity(blockPos)?.MarkDirty(redrawOnClient: true);
							}
							api.World.BlockAccessor.MarkBlockDirty(blockPos);
							dirty?.Invoke(blockPos.dimension, blockPos.X, blockPos.Z);
							if (!storage.TryEnqueue(new LogEventRecord
							{
								TimestampUtc = DateTime.UtcNow,
								Action = LogActionType.BlockUse,
								PlayerUid = administratorUid,
								PlayerName = administratorName,
								WorldId = (api.World.SavegameIdentifier ?? "unknownworld"),
								Dimension = op.Cell.Dimension,
								X = blockPos.X,
								Y = blockPos.Y,
								Z = blockPos.Z,
								BlockCode = op.Before,
								Meta = new Dictionary<string, string>
								{
									["rollbackToken"] = plan.Token,
									["oldBlockCode"] = op.After,
									["newBlockCode"] = op.Before
								}
							}))
							{
								throw new InvalidOperationException("Audit queue rejected recovery barrier; stopped.");
							}
							applied = new
							{
								phase = "applied",
								operation = op
							};
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				if (applied != null)
				{
					await journal.AppendAsync(applied, new
					{
						key = op.ReceiptKey,
						phase = "applied",
						token = plan.Token,
						utc = DateTime.UtcNow
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					num = skipped;
					skipped = num + 1;
					await journal.AppendAsync(new
					{
						phase = "skipped",
						operation = op,
						reason = reason,
						utc = DateTime.UtcNow
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				if (index % 256 == 0)
				{
					await OnGameThreadAsync(() =>
					{
						Reply(p, g, $"Recovery progress: {index}/{plan.Changes.Count}, skipped={skipped}.");
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				await Task.Delay(10, cancellation).ConfigureAwait(continueOnCapturedContext: false);
			}
			await journal.AppendAsync(new
			{
				phase = "complete",
				utc = DateTime.UtcNow,
				blocks = blocks,
				inventories = inventories,
				entities = entities,
				skipped = skipped
			}).ConfigureAwait(continueOnCapturedContext: false);
			await journal.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
			await OnGameThreadAsync(() =>
			{
				Reply(p, g, $"Recovery complete: {blocks} block locations, {inventories} inventories, {entities} replacement creatures; {skipped} conflicts/changes skipped. Journal: {Path.GetFileName(audit)}");
				Finish();
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			try
			{
				await using RecoveryJournal failureJournal = new RecoveryJournal(audit, receiptPath);
				await failureJournal.AppendAsync(new
				{
					phase = ((cancelRequested || disposed) ? "cancelled" : "stopped"),
					utc = DateTime.UtcNow,
					blocks = blocks,
					inventories = inventories,
					entities = entities,
					skipped = skipped,
					error = ex2.Message
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch
			{
			}
			if (disposed)
			{
				return;
			}
			await OnGameThreadAsync(() =>
			{
				Reply(p, g, $"Recovery {(cancelRequested ? "cancelled" : "stopped")}: {blocks} blocks, {inventories} inventories, {entities} creatures applied. {ex2.Message} Inspect journal; uncertain receipts prevent repeat duplication.", error: true);
				if (!cancelRequested)
				{
					api.Logger.Error("[Recovery] {0}", ex2);
				}
				Finish();
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		void CheckOccupancy()
		{
			if (Occupied(plan, administratorUid))
			{
				throw new InvalidOperationException(plan.Area ? "Another player entered the selected area; stopped." : "Target came online; stopped.");
			}
		}
	}

	private void Finish()
	{
		busy = false;
		activeAdmin = "";
		plans.Clear();
		receipts.Clear();
		ClearWatchers();
		changed.Clear();
		trackedCells.Clear();
		trackAllDuringRead = false;
		operationCancellation?.Dispose();
		operationCancellation = null;
	}

	public void Dispose()
	{
		disposed = true;
		if (maintenanceTick != 0)
		{
			api.Event.UnregisterGameTickListener(maintenanceTick);
		}
		operationCancellation?.Cancel();
		lifetime.Cancel();
		Finish();
		api.Event.DidBreakBlock -= Break;
		api.Event.DidPlaceBlock -= Place;
		api.Event.DidUseBlock -= Use;
	}
}
