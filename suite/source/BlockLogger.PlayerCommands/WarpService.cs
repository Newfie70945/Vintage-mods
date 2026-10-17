using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BlockLogger.AntiCheat;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerCommands;

public sealed class WarpService : IDisposable
{
	private sealed class PendingWarp
	{
		public string PlayerUid { get; init; } = "";

		public string WarpName { get; init; } = "";

		public long DueUtcMs { get; init; }

		public double StartX { get; init; }

		public double StartY { get; init; }

		public double StartZ { get; init; }

		public bool CancelOnMove { get; init; }
	}

	private readonly ICoreServerAPI api;

	private readonly Func<PlayerCommandConfig> getConfig;

	private readonly Action<IServerPlayer, EntityPos?, string>? recordTeleportOrigin;

	private readonly System.Func<string, bool>? isJailed;

	private WarpDatabase database;

	private readonly Dictionary<string, long> lastWarpUtc = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, PendingWarp> pendingByUid = new Dictionary<string, PendingWarp>(StringComparer.OrdinalIgnoreCase);

	private long tickListenerId;

	private bool disposed;

	public WarpService(ICoreServerAPI api, Func<PlayerCommandConfig> getConfig, Action<IServerPlayer, EntityPos?, string>? recordTeleportOrigin = null, System.Func<string, bool>? isJailed = null)
	{
		this.api = api;
		this.getConfig = getConfig;
		this.recordTeleportOrigin = recordTeleportOrigin;
		this.isJailed = isJailed;
		database = WarpDatabase.Load(api);
	}

	public bool HasPending(string playerUid)
	{
		return !string.IsNullOrWhiteSpace(playerUid) && pendingByUid.ContainsKey(playerUid);
	}

	public void Start()
	{
		tickListenerId = api.Event.RegisterGameTickListener((float _) =>
		{
			Tick();
		}, 250, 250);
		api.Event.PlayerDisconnect += OnPlayerDisconnect;
	}

	public WarpSnapshot GetSnapshot()
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		WarpSnapshot warpSnapshot = new WarpSnapshot();
		warpSnapshot.Available = true;
		warpSnapshot.Enabled = playerCommandConfig.WarpsEnabled;
		warpSnapshot.DefaultCooldownSeconds = playerCommandConfig.WarpDefaultCooldownSeconds;
		warpSnapshot.DefaultWarmupSeconds = playerCommandConfig.WarpDefaultWarmupSeconds;
		warpSnapshot.DefaultCancelOnMove = playerCommandConfig.WarpDefaultCancelOnMove;
		warpSnapshot.Warps = database.Warps.Select(Clone).ToList();
		warpSnapshot.AvailableRoles = GetAvailableRoles();
		warpSnapshot.AvailablePrivileges = GetAvailablePrivileges();
		return warpSnapshot;
	}

	public WarpMutationResult SaveDefaults(WarpSettingsRequest? request)
	{
		if (request == null)
		{
			return Result(success: false, "Warp settings are required.");
		}
		PlayerCommandConfig playerCommandConfig = getConfig();
		playerCommandConfig.WarpsEnabled = request.Enabled;
		playerCommandConfig.WarpDefaultCooldownSeconds = request.DefaultCooldownSeconds;
		playerCommandConfig.WarpDefaultWarmupSeconds = request.DefaultWarmupSeconds;
		playerCommandConfig.WarpDefaultCancelOnMove = request.DefaultCancelOnMove;
		playerCommandConfig.Clamp();
		SuiteConfigStorage.Store(api, playerCommandConfig, "NewfiesPlayerCommands.json");
		return Result(success: true, "Warp defaults saved and applied live.");
	}

	public WarpMutationResult Upsert(WarpMutationRequest? request)
	{
		if (request == null)
		{
			return Result(success: false, "Warp data is required.");
		}
		string name = NormalizeWarpName(request.Name);
		if (string.IsNullOrWhiteSpace(name))
		{
			return Result(success: false, "Warp name must contain letters, numbers, '-' or '_'.");
		}
		WarpDefinition warpDefinition = database.Warps.FirstOrDefault((WarpDefinition x) => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
		if (warpDefinition == null)
		{
			warpDefinition = new WarpDefinition
			{
				Name = name
			};
			database.Warps.Add(warpDefinition);
		}
		warpDefinition.DisplayName = request.DisplayName;
		warpDefinition.Enabled = request.Enabled;
		warpDefinition.X = request.X;
		warpDefinition.Y = request.Y;
		warpDefinition.Z = request.Z;
		warpDefinition.CooldownSeconds = request.CooldownSeconds;
		warpDefinition.WarmupSeconds = request.WarmupSeconds;
		warpDefinition.CancelOnMove = request.CancelOnMove;
		warpDefinition.CostEnabled = request.CostEnabled;
		warpDefinition.CostItemCode = request.CostItemCode;
		warpDefinition.CostQuantity = request.CostQuantity;
		warpDefinition.AccessMode = request.AccessMode;
		warpDefinition.AllowedRoles = request.AllowedRoles ?? new List<string>();
		warpDefinition.RequiredPrivileges = request.RequiredPrivileges ?? new List<string>();
		warpDefinition.Clamp();
		SaveDatabase();
		return Result(success: true, "Warp '" + warpDefinition.Name + "' saved.");
	}

	public WarpMutationResult Delete(string? name)
	{
		name = NormalizeWarpName(name);
		if (database.Warps.RemoveAll((WarpDefinition x) => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0)
		{
			return Result(success: false, "Warp '" + name + "' was not found.");
		}
		SaveDatabase();
		return Result(success: true, "Warp '" + name + "' deleted.");
	}

	public WarpMutationResult Reload()
	{
		database = WarpDatabase.Load(api);
		return Result(success: true, $"Reloaded {database.Warps.Count} warp(s) from {"NewfiesWarps.json"}.");
	}

	public void HandleWarp(IServerPlayer player, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		System.Func<string, bool>? func = isJailed;
		if (func != null && func(player.PlayerUID))
		{
			Reply(player, groupId, "You cannot use /warp while jailed.", success: false);
			return;
		}
		if (!playerCommandConfig.WarpsEnabled)
		{
			Reply(player, groupId, "Warps are disabled by Server Essentials.", success: false);
			return;
		}
		if (!playerCommandConfig.WarpCommandEnabled)
		{
			Reply(player, groupId, "The Server Essentials /warp command is disabled.", success: false);
			return;
		}
		TeleportRolePolicyResolution teleportRolePolicyResolution = playerCommandConfig.ResolveTeleportRolePolicy("warp", player.ServerData?.RoleCode ?? player.Role?.Code ?? "", playerCommandConfig.WarpDefaultCooldownSeconds);
		if (!teleportRolePolicyResolution.Allowed)
		{
			string text = (string.IsNullOrWhiteSpace(teleportRolePolicyResolution.RoleCode) ? "your role" : ("role '" + teleportRolePolicyResolution.RoleCode + "'"));
			Reply(player, groupId, "/warp is not available to " + text + ".", success: false);
			return;
		}
		string text2 = NormalizeWarpName(args.PopWord());
		if (string.IsNullOrWhiteSpace(text2))
		{
			HandleWarps(player, groupId, args);
			return;
		}
		WarpDefinition warpDefinition = Find(text2);
		if (warpDefinition == null || !warpDefinition.Enabled)
		{
			Reply(player, groupId, "Warp '" + text2 + "' is not available.", success: false);
			return;
		}
		if (!CanUseWarp(player, warpDefinition, out string reason))
		{
			Reply(player, groupId, "You do not have access to warp " + Display(warpDefinition) + ". " + reason, success: false);
			return;
		}
		TeleportRolePolicyResolution teleportRolePolicyResolution2 = playerCommandConfig.ResolveTeleportRolePolicy("warp", player.ServerData?.RoleCode ?? player.Role?.Code ?? "", EffectiveCooldown(warpDefinition, playerCommandConfig));
		if (!teleportRolePolicyResolution2.Allowed)
		{
			string text3 = (string.IsNullOrWhiteSpace(teleportRolePolicyResolution2.RoleCode) ? "your role" : ("role '" + teleportRolePolicyResolution2.RoleCode + "'"));
			Reply(player, groupId, "/warp is not available to " + text3 + ".", success: false);
			return;
		}
		if (pendingByUid.ContainsKey(player.PlayerUID))
		{
			Reply(player, groupId, "You already have a warp pending. Move to cancel it or wait for it to complete.", success: false);
			return;
		}
		int cooldownSeconds = teleportRolePolicyResolution2.CooldownSeconds;
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string key = CooldownKey(player.PlayerUID, warpDefinition.Name);
		if (cooldownSeconds > 0 && lastWarpUtc.TryGetValue(key, out var value))
		{
			long num2 = value + (long)cooldownSeconds * 1000L - num;
			if (num2 > 0)
			{
				Reply(player, groupId, $"You can use warp {Display(warpDefinition)} again in {(int)Math.Ceiling((double)num2 / 1000.0)} second(s).", success: false);
				return;
			}
		}
		if (!HasCost(player, warpDefinition, out var count))
		{
			Reply(player, groupId, $"Warp {Display(warpDefinition)} requires {warpDefinition.CostQuantity} × {warpDefinition.CostItemCode}. You currently have {count}.", success: false);
			return;
		}
		int num3 = EffectiveWarmup(warpDefinition, playerCommandConfig);
		bool flag = warpDefinition.CancelOnMove ?? playerCommandConfig.WarpDefaultCancelOnMove;
		if (num3 <= 0)
		{
			CompleteWarp(player, warpDefinition, groupId);
			return;
		}
		EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
		if (pos == null)
		{
			Reply(player, groupId, "Warp could not read your current position. Try again in a moment.", success: false);
			return;
		}
		pendingByUid[player.PlayerUID] = new PendingWarp
		{
			PlayerUid = player.PlayerUID,
			WarpName = warpDefinition.Name,
			DueUtcMs = num + (long)num3 * 1000L,
			StartX = pos.X,
			StartY = pos.Y,
			StartZ = pos.Z,
			CancelOnMove = flag
		};
		string value2 = (warpDefinition.CostEnabled ? $" Cost: {warpDefinition.CostQuantity} × {warpDefinition.CostItemCode}." : "");
		string value3 = (flag ? " Do not move." : "");
		Reply(player, groupId, $"Teleporting to {Display(warpDefinition)} in {num3} second(s).{value2}{value3}", success: true);
	}

	public void HandleWarps(IServerPlayer player, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (!playerCommandConfig.WarpsEnabled)
		{
			Reply(player, groupId, "Warps are disabled by Server Essentials.", success: false);
			return;
		}
		List<WarpDefinition> list = database.Warps.Where((WarpDefinition x) => x.Enabled && CanUseWarp(player, x, out string _)).OrderBy((WarpDefinition x) => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			Reply(player, groupId, "No warps are currently available to you.", success: true);
			return;
		}
		string text = string.Join(", ", list.Select((WarpDefinition x) => x.CostEnabled ? $"{Display(x)} ({x.CostQuantity}× {x.CostItemCode})" : Display(x)));
		Reply(player, groupId, "Available warps: " + text, success: true);
	}

	public void HandleWarpInfo(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = NormalizeWarpName(args.PopWord());
		if (string.IsNullOrWhiteSpace(text))
		{
			Reply(player, groupId, "Usage: /warpinfo <name>", success: false);
			return;
		}
		WarpDefinition warpDefinition = Find(text);
		if (warpDefinition == null)
		{
			Reply(player, groupId, "Warp '" + text + "' was not found.", success: false);
			return;
		}
		if (!CanUseWarp(player, warpDefinition, out string reason))
		{
			Reply(player, groupId, "You do not have access to warp " + Display(warpDefinition) + ". " + reason, success: false);
			return;
		}
		PlayerCommandConfig playerCommandConfig = getConfig();
		int defaultCooldownSeconds = EffectiveCooldown(warpDefinition, playerCommandConfig);
		TeleportRolePolicyResolution teleportRolePolicyResolution = playerCommandConfig.ResolveTeleportRolePolicy("warp", player.ServerData?.RoleCode ?? player.Role?.Code ?? "", defaultCooldownSeconds);
		int cooldownSeconds = teleportRolePolicyResolution.CooldownSeconds;
		int value = EffectiveWarmup(warpDefinition, playerCommandConfig);
		bool flag = warpDefinition.CancelOnMove ?? playerCommandConfig.WarpDefaultCancelOnMove;
		string value2 = (warpDefinition.CostEnabled ? $"{warpDefinition.CostQuantity} × {warpDefinition.CostItemCode}" : "Free");
		string value3 = AccessSummary(warpDefinition);
		Reply(player, groupId, $"Warp {Display(warpDefinition)} [{warpDefinition.Name}] — {(warpDefinition.Enabled ? "Enabled" : "Disabled")} — Position {warpDefinition.X:0.##}, {warpDefinition.Y:0.##}, {warpDefinition.Z:0.##} — Cooldown {cooldownSeconds}s — Warm-up {value}s — Cancel on move {(flag ? "ON" : "OFF")} — Cost {value2} — Access {value3}.", success: true);
	}

	public void HandleSetWarp(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = NormalizeWarpName(args.PopWord());
		if (string.IsNullOrWhiteSpace(text))
		{
			Reply(player, groupId, "Usage: /setwarp <name> [itemcode] [quantity]", success: false);
			return;
		}
		WarpDefinition warpDefinition = Find(text);
		string text2 = (args.PopWord() ?? "").Trim();
		int result = 1;
		string text3 = (args.PopWord() ?? "").Trim();
		if (!string.IsNullOrWhiteSpace(text3) && (!int.TryParse(text3, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result < 1))
		{
			Reply(player, groupId, "Warp cost quantity must be a positive whole number.", success: false);
			return;
		}
		EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
		if (pos == null)
		{
			Reply(player, groupId, "Unable to read your current position, so the warp was not saved.", success: false);
			return;
		}
		WarpMutationRequest warpMutationRequest = new WarpMutationRequest
		{
			Name = text,
			DisplayName = (warpDefinition?.DisplayName ?? text),
			Enabled = (warpDefinition?.Enabled ?? true),
			X = pos.X,
			Y = pos.Y,
			Z = pos.Z,
			CooldownSeconds = (warpDefinition?.CooldownSeconds ?? (-1)),
			WarmupSeconds = (warpDefinition?.WarmupSeconds ?? (-1)),
			CancelOnMove = warpDefinition?.CancelOnMove,
			CostEnabled = (warpDefinition?.CostEnabled ?? false),
			CostItemCode = (warpDefinition?.CostItemCode ?? ""),
			CostQuantity = (warpDefinition?.CostQuantity ?? 1),
			AccessMode = (warpDefinition?.AccessMode ?? "any"),
			AllowedRoles = (warpDefinition?.AllowedRoles?.ToList() ?? new List<string>()),
			RequiredPrivileges = (warpDefinition?.RequiredPrivileges?.ToList() ?? new List<string>())
		};
		if (!string.IsNullOrWhiteSpace(text2))
		{
			warpMutationRequest.CostEnabled = !text2.Equals("none", StringComparison.OrdinalIgnoreCase);
			warpMutationRequest.CostItemCode = (warpMutationRequest.CostEnabled ? text2 : "");
			warpMutationRequest.CostQuantity = result;
		}
		WarpMutationResult warpMutationResult = Upsert(warpMutationRequest);
		Reply(player, groupId, warpMutationResult.Success ? $"Warp '{text}' saved at {pos.X:0.##}, {pos.Y:0.##}, {pos.Z:0.##}." : warpMutationResult.Message, warpMutationResult.Success);
	}

	public void HandleDeleteWarp(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = NormalizeWarpName(args.PopWord());
		if (string.IsNullOrWhiteSpace(text))
		{
			Reply(player, groupId, "Usage: /delwarp <name>", success: false);
			return;
		}
		WarpMutationResult warpMutationResult = Delete(text);
		Reply(player, groupId, warpMutationResult.Message, warpMutationResult.Success);
	}

	public void HandleSetWarpCost(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = NormalizeWarpName(args.PopWord());
		string text2 = (args.PopWord() ?? "").Trim();
		string s = (args.PopWord() ?? "1").Trim();
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
		{
			Reply(player, groupId, "Usage: /setwarpcost <warp> <itemcode|none> [quantity]", success: false);
			return;
		}
		WarpDefinition warpDefinition = Find(text);
		if (warpDefinition == null)
		{
			Reply(player, groupId, "Warp '" + text + "' was not found.", success: false);
			return;
		}
		if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < 1)
		{
			Reply(player, groupId, "Quantity must be a positive whole number.", success: false);
			return;
		}
		warpDefinition.CostEnabled = !text2.Equals("none", StringComparison.OrdinalIgnoreCase);
		warpDefinition.CostItemCode = (warpDefinition.CostEnabled ? text2 : "");
		warpDefinition.CostQuantity = result;
		warpDefinition.Clamp();
		SaveDatabase();
		Reply(player, groupId, warpDefinition.CostEnabled ? $"Warp '{text}' now costs {warpDefinition.CostQuantity} × {warpDefinition.CostItemCode}." : ("Warp '" + text + "' is now free."), success: true);
	}

	public void HandleSetWarpCooldown(IServerPlayer player, int groupId, CmdArgs args)
	{
		HandlePerWarpInt(player, groupId, args, "cooldown", 86400, (WarpDefinition w, int v) =>
		{
			w.CooldownSeconds = v;
		});
	}

	public void HandleSetWarpWarmup(IServerPlayer player, int groupId, CmdArgs args)
	{
		HandlePerWarpInt(player, groupId, args, "warmup", 300, (WarpDefinition w, int v) =>
		{
			w.WarmupSeconds = v;
		});
	}

	public void HandleSetWarpMovement(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = NormalizeWarpName(args.PopWord());
		string text2 = (args.PopWord() ?? "").Trim().ToLowerInvariant();
		WarpDefinition warpDefinition = Find(text);
		if (warpDefinition == null)
		{
			Reply(player, groupId, "Warp '" + text + "' was not found.", success: false);
			return;
		}
		WarpDefinition warpDefinition2 = warpDefinition;
		if (1 == 0)
		{
		}
		bool? cancelOnMove;
		switch (text2)
		{
		case "on":
		case "true":
		case "yes":
			cancelOnMove = true;
			break;
		case "off":
		case "false":
		case "no":
			cancelOnMove = false;
			break;
		case "default":
		case "inherit":
			cancelOnMove = null;
			break;
		default:
			cancelOnMove = warpDefinition.CancelOnMove;
			break;
		}
		if (1 == 0)
		{
		}
		warpDefinition2.CancelOnMove = cancelOnMove;
		bool flag;
		switch (text2)
		{
		case "on":
		case "true":
		case "yes":
		case "off":
		case "false":
		case "no":
		case "default":
		case "inherit":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			Reply(player, groupId, "Usage: /setwarpmove <warp> <on|off|default>", success: false);
			return;
		}
		SaveDatabase();
		Reply(player, groupId, $"Warp '{text}' movement cancellation set to {((!warpDefinition.CancelOnMove.HasValue) ? "DEFAULT" : (warpDefinition.CancelOnMove.Value ? "ON" : "OFF"))}.", success: true);
	}

	public void HandleReloadWarps(IServerPlayer player, int groupId, CmdArgs args)
	{
		WarpMutationResult warpMutationResult = Reload();
		Reply(player, groupId, warpMutationResult.Message, warpMutationResult.Success);
	}

	private void HandlePerWarpInt(IServerPlayer player, int groupId, CmdArgs args, string label, int max, Action<WarpDefinition, int> setter)
	{
		string text = NormalizeWarpName(args.PopWord());
		string text2 = (args.PopWord() ?? "").Trim();
		WarpDefinition warpDefinition = Find(text);
		if (warpDefinition == null)
		{
			Reply(player, groupId, "Warp '" + text + "' was not found.", success: false);
			return;
		}
		int result;
		if (text2.Equals("default", StringComparison.OrdinalIgnoreCase) || text2.Equals("inherit", StringComparison.OrdinalIgnoreCase))
		{
			result = -1;
		}
		else if (!int.TryParse(text2, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result < 0 || result > max)
		{
			Reply(player, groupId, $"Usage: /setwarp{label} <warp> <0-{max}|default>", success: false);
			return;
		}
		setter(warpDefinition, result);
		SaveDatabase();
		Reply(player, groupId, $"Warp '{text}' {label} set to {((result < 0) ? "DEFAULT" : (result + " second(s)"))}.", success: true);
	}

	private void Tick()
	{
		if (pendingByUid.Count == 0)
		{
			return;
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		PendingWarp[] array = pendingByUid.Values.ToArray();
		foreach (PendingWarp pendingWarp in array)
		{
			IServerPlayer serverPlayer = OnlineByUid(pendingWarp.PlayerUid);
			if (serverPlayer == null)
			{
				pendingByUid.Remove(pendingWarp.PlayerUid);
				continue;
			}
			System.Func<string, bool>? func = isJailed;
			if (func != null && func(serverPlayer.PlayerUID))
			{
				pendingByUid.Remove(pendingWarp.PlayerUid);
				Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Warp cancelled because you were jailed.", success: false);
				continue;
			}
			if (pendingWarp.CancelOnMove)
			{
				EntityPos pos = EntityRuntimeCompat.GetPos(serverPlayer.Entity);
				if (pos == null)
				{
					pendingByUid.Remove(pendingWarp.PlayerUid);
					Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Warp cancelled because your position could not be read safely.", success: false);
					continue;
				}
				double num2 = pos.X - pendingWarp.StartX;
				double num3 = pos.Y - pendingWarp.StartY;
				double num4 = pos.Z - pendingWarp.StartZ;
				if (num2 * num2 + num3 * num3 + num4 * num4 > 0.16)
				{
					pendingByUid.Remove(pendingWarp.PlayerUid);
					Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Warp cancelled because you moved.", success: false);
					continue;
				}
			}
			if (num >= pendingWarp.DueUtcMs)
			{
				pendingByUid.Remove(pendingWarp.PlayerUid);
				WarpDefinition warpDefinition = Find(pendingWarp.WarpName);
				string reason;
				if (warpDefinition == null || !warpDefinition.Enabled)
				{
					Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Warp cancelled because the destination is no longer available.", success: false);
				}
				else if (!CanUseWarp(serverPlayer, warpDefinition, out reason))
				{
					Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Warp cancelled because you no longer have access to that destination.", success: false);
				}
				else
				{
					CompleteWarp(serverPlayer, warpDefinition, GlobalConstants.GeneralChatGroup);
				}
			}
		}
	}

	private void CompleteWarp(IServerPlayer player, WarpDefinition warp, int groupId)
	{
		if (!CanUseWarp(player, warp, out string reason))
		{
			Reply(player, groupId, "Warp cancelled because you no longer have access. " + reason, success: false);
			return;
		}
		System.Func<string, bool>? func = isJailed;
		if (func != null && func(player.PlayerUID))
		{
			Reply(player, groupId, "Warp cancelled because you are jailed.", success: false);
			return;
		}
		EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
		if (pos == null)
		{
			Reply(player, groupId, "The warp could not read your current position, so nothing was consumed.", success: false);
			return;
		}
		if (!HasCost(player, warp, out var count))
		{
			Reply(player, groupId, $"Warp {Display(warp)} requires {warp.CostQuantity} × {warp.CostItemCode}. You currently have {count}.", success: false);
			return;
		}
		ItemStack refund = null;
		if (warp.CostEnabled && !TryRemoveCost(player, warp, out refund))
		{
			Reply(player, groupId, "Your inventory changed before the warp completed, so no items were consumed.", success: false);
			return;
		}
		try
		{
			EntityPos entityPos = pos.Copy();
			entityPos.X = warp.X;
			entityPos.Y = warp.Y;
			entityPos.Z = warp.Z;
			PacketGuardTrustedState.Mark(player, "warp", 5000);
			player.Entity.TeleportTo(entityPos);
			recordTeleportOrigin?.Invoke(player, pos.Copy(), "warp:" + warp.Name);
			lastWarpUtc[CooldownKey(player.PlayerUID, warp.Name)] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			Reply(player, groupId, warp.CostEnabled ? $"Teleported to {Display(warp)}. Consumed {warp.CostQuantity} × {warp.CostItemCode}." : ("Teleported to " + Display(warp) + "."), success: true);
		}
		catch (Exception ex)
		{
			if (refund != null)
			{
				try
				{
					player.Entity.TryGiveItemStack(refund);
				}
				catch
				{
				}
			}
			api.Logger.Error("[ServerEssentials/Warps] Teleport to {0} failed for {1}: {2}", warp.Name, player.PlayerName, ex);
			Reply(player, groupId, "The warp failed. Any warp cost that was removed was refunded where inventory space allowed.", success: false);
		}
	}

	private bool HasCost(IServerPlayer player, WarpDefinition warp, out int count)
	{
		count = 0;
		if (!warp.CostEnabled || string.IsNullOrWhiteSpace(warp.CostItemCode) || warp.CostQuantity <= 0)
		{
			return true;
		}
		foreach (InventoryBase item in player.InventoryManager.InventoriesOrdered)
		{
			if (item == null)
			{
				continue;
			}
			for (int i = 0; i < item.Count; i++)
			{
				ItemStack itemStack = item[i]?.Itemstack;
				if (!(itemStack?.Collectible?.Code == null) && itemStack.Collectible.Code.ToString().Equals(warp.CostItemCode, StringComparison.OrdinalIgnoreCase))
				{
					count += Math.Max(0, itemStack.StackSize);
					if (count >= warp.CostQuantity)
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	private bool TryRemoveCost(IServerPlayer player, WarpDefinition warp, out ItemStack? refund)
	{
		refund = null;
		if (!warp.CostEnabled)
		{
			return true;
		}
		if (!HasCost(player, warp, out var _))
		{
			return false;
		}
		int num = warp.CostQuantity;
		CollectibleObject collectibleObject = null;
		foreach (InventoryBase item in player.InventoryManager.InventoriesOrdered)
		{
			if (item == null)
			{
				continue;
			}
			for (int i = 0; i < item.Count; i++)
			{
				if (num <= 0)
				{
					break;
				}
				ItemSlot itemSlot = item[i];
				ItemStack itemStack = itemSlot?.Itemstack;
				if (itemSlot != null && !(itemStack?.Collectible?.Code == null) && itemStack.Collectible.Code.ToString().Equals(warp.CostItemCode, StringComparison.OrdinalIgnoreCase))
				{
					if (collectibleObject == null)
					{
						collectibleObject = itemStack.Collectible;
					}
					int num2 = Math.Min(num, itemStack.StackSize);
					itemStack.StackSize -= num2;
					num -= num2;
					if (itemStack.StackSize <= 0)
					{
						itemSlot.Itemstack = null;
					}
					itemSlot.MarkDirty();
				}
			}
			if (num > 0)
			{
				continue;
			}
			break;
		}
		if (num > 0 || collectibleObject == null)
		{
			return false;
		}
		refund = new ItemStack(collectibleObject, warp.CostQuantity);
		return true;
	}

	private bool CanUseWarp(IServerPlayer player, WarpDefinition warp, out string reason)
	{
		reason = "";
		if (player.HasPrivilege(Privilege.root) || player.HasPrivilege(Privilege.controlserver))
		{
			return true;
		}
		List<string> list = warp.AllowedRoles ?? new List<string>();
		List<string> list2 = warp.RequiredPrivileges ?? new List<string>();
		if (list.Count == 0 && list2.Count == 0)
		{
			return true;
		}
		string roleCode = player.ServerData?.RoleCode ?? player.Role?.Code ?? "";
		bool flag = list.Any((string r) => r.Equals(roleCode, StringComparison.OrdinalIgnoreCase));
		bool flag2 = list2.Any((string p) => player.HasPrivilege(p));
		bool flag3 = list2.Count == 0 || list2.All((string p) => player.HasPrivilege(p));
		bool flag4 = (string.Equals(warp.AccessMode, "all", StringComparison.OrdinalIgnoreCase) ? (((list.Count == 0) | flag) & flag3) : (flag | flag2));
		if (!flag4)
		{
			reason = AccessSummary(warp);
		}
		return flag4;
	}

	private static string AccessSummary(WarpDefinition warp)
	{
		List<string> list = warp.AllowedRoles ?? new List<string>();
		List<string> list2 = warp.RequiredPrivileges ?? new List<string>();
		if (list.Count == 0 && list2.Count == 0)
		{
			return "Public";
		}
		string text = ((list.Count == 0) ? "" : ("role: " + string.Join(", ", list)));
		string text2 = ((list2.Count == 0) ? "" : ("privilege: " + string.Join(", ", list2)));
		string text3 = string.Join(string.Equals(warp.AccessMode, "all", StringComparison.OrdinalIgnoreCase) ? " AND " : " OR ", new string[2] { text, text2 }.Where((string x) => x.Length > 0));
		return (string.Equals(warp.AccessMode, "all", StringComparison.OrdinalIgnoreCase) ? "All requirements — " : "Any match — ") + text3;
	}

	private List<string> GetAvailableRoles()
	{
		return (from r in api.Server.Config.Roles ?? new List<IPlayerRole>()
			where r != null && !string.IsNullOrWhiteSpace(r.Code)
			select r.Code).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string r) => r, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private List<string> GetAvailablePrivileges()
	{
		List<IPlayerRole> source = api.Server.Config.Roles ?? new List<IPlayerRole>();
		return (from p in Privilege.AllCodes().Concat(source.Where((IPlayerRole r) => r != null).SelectMany((IPlayerRole r) => (r.Privileges ?? new List<string>()).Concat(r.RuntimePrivileges ?? new HashSet<string>())))
			where !string.IsNullOrWhiteSpace(p)
			select p).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string p) => p, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private WarpDefinition? Find(string name)
	{
		return database.Warps.FirstOrDefault((WarpDefinition x) => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
	}

	private IServerPlayer? OnlineByUid(string uid)
	{
		return api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerUID.Equals(uid, StringComparison.OrdinalIgnoreCase));
	}

	private static string CooldownKey(string uid, string warp)
	{
		return uid + "\n" + warp;
	}

	private static int EffectiveCooldown(WarpDefinition warp, PlayerCommandConfig cfg)
	{
		return (warp.CooldownSeconds >= 0) ? warp.CooldownSeconds : cfg.WarpDefaultCooldownSeconds;
	}

	private static int EffectiveWarmup(WarpDefinition warp, PlayerCommandConfig cfg)
	{
		return (warp.WarmupSeconds >= 0) ? warp.WarmupSeconds : cfg.WarpDefaultWarmupSeconds;
	}

	private static string Display(WarpDefinition warp)
	{
		return string.IsNullOrWhiteSpace(warp.DisplayName) ? warp.Name : warp.DisplayName;
	}

	private void SaveDatabase()
	{
		database.Clamp();
		SuiteConfigStorage.Store(api, database, "NewfiesWarps.json");
	}

	private WarpMutationResult Result(bool success, string message)
	{
		return new WarpMutationResult
		{
			Success = success,
			Message = message,
			Snapshot = GetSnapshot()
		};
	}

	private static WarpDefinition Clone(WarpDefinition x)
	{
		return new WarpDefinition
		{
			Name = x.Name,
			DisplayName = x.DisplayName,
			Enabled = x.Enabled,
			X = x.X,
			Y = x.Y,
			Z = x.Z,
			CooldownSeconds = x.CooldownSeconds,
			WarmupSeconds = x.WarmupSeconds,
			CancelOnMove = x.CancelOnMove,
			CostEnabled = x.CostEnabled,
			CostItemCode = x.CostItemCode,
			CostQuantity = x.CostQuantity,
			AccessMode = x.AccessMode,
			AllowedRoles = (x.AllowedRoles?.ToList() ?? new List<string>()),
			RequiredPrivileges = (x.RequiredPrivileges?.ToList() ?? new List<string>())
		};
	}

	public static string NormalizeWarpName(string? value)
	{
		string text = (value ?? "").Trim().ToLowerInvariant();
		if (text.Length > 32)
		{
			text = text.Substring(0, 32);
		}
		return new string(text.Where((char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		pendingByUid.Remove(player.PlayerUID);
	}

	private static void Reply(IServerPlayer player, int groupId, string message, bool success)
	{
		player.SendMessage(groupId, message, (!success) ? EnumChatType.CommandError : EnumChatType.CommandSuccess);
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			if (tickListenerId != 0)
			{
				api.Event.UnregisterGameTickListener(tickListenerId);
			}
			api.Event.PlayerDisconnect -= OnPlayerDisconnect;
			pendingByUid.Clear();
			lastWarpUtc.Clear();
		}
	}
}
