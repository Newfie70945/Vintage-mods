using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlockLogger.PlayerCommands;
using BlockLogger.PlayerList;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace BlockLogger.Roles;

public sealed class RoleManagementService
{
	private readonly ICoreServerAPI api;

	private readonly Func<PlayerCommandConfig?> policyConfig;

	private readonly Func<PublicPlayerListServer?> playerList;

	public static readonly string[] Commands = new string[7] { "spawn", "home", "back", "warp", "t2p", "t2m", "rtp" };

	public RoleManagementService(ICoreServerAPI api, Func<PlayerCommandConfig?> policyConfig, Func<PublicPlayerListServer?> playerList)
	{
		this.api = api;
		this.policyConfig = policyConfig;
		this.playerList = playerList;
	}

	public RoleEditorSnapshot GetSnapshot()
	{
		IServerConfig config = api.Server.Config;
		PlayerCommandConfig playerCommandConfig = policyConfig();
		PublicPlayerListConfig publicPlayerListConfig = playerList()?.Config;
		Dictionary<string, int> dictionary = (api.PlayerData.PlayerDataByUid ?? new Dictionary<string, IServerPlayerData>()).Values.GroupBy((IServerPlayerData x) => x.RoleCode ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, IServerPlayerData> x) => x.Key, (IGrouping<string, IServerPlayerData> x) => x.Count(), StringComparer.OrdinalIgnoreCase);
		RoleEditorSnapshot roleEditorSnapshot = new RoleEditorSnapshot();
		roleEditorSnapshot.Available = config is ServerConfig;
		roleEditorSnapshot.PoliciesAvailable = playerCommandConfig != null;
		roleEditorSnapshot.ColorsAvailable = publicPlayerListConfig != null;
		roleEditorSnapshot.UseRoleNameColors = publicPlayerListConfig?.UseRoleNameColors ?? false;
		roleEditorSnapshot.GlobalHomeLimit = playerCommandConfig?.HomeGlobalLimit ?? 0;
		roleEditorSnapshot.KnownPrivileges = (from x in Privilege.AllCodes()
			orderby x
			select x).ToList();
		roleEditorSnapshot.GameModes = Enum.GetNames<EnumGameMode>().ToList();
		RoleEditorSnapshot roleEditorSnapshot2 = roleEditorSnapshot;
		if (playerCommandConfig != null)
		{
			roleEditorSnapshot2.DefaultCooldowns = new Dictionary<string, int>
			{
				{ "spawn", playerCommandConfig.SpawnCooldownSeconds },
				{ "home", playerCommandConfig.HomeCooldownSeconds },
				{ "back", playerCommandConfig.BackCooldownSeconds },
				{ "warp", playerCommandConfig.WarpDefaultCooldownSeconds },
				{ "t2p", playerCommandConfig.T2pCooldownSeconds },
				{ "t2m", playerCommandConfig.T2mCooldownSeconds },
				{ "rtp", playerCommandConfig.RtpCooldownSeconds }
			};
		}
		string[] commands = Commands;
		foreach (string key in commands)
		{
			TeleportRolePolicy teleportRolePolicy = playerCommandConfig?.TeleportRolePolicies.GetValueOrDefault(key)?.GetValueOrDefault("*");
			if (teleportRolePolicy != null)
			{
				roleEditorSnapshot2.WildcardPolicies[key] = new TeleportRolePolicy
				{
					Allowed = teleportRolePolicy.Allowed,
					CooldownSeconds = teleportRolePolicy.CooldownSeconds
				};
			}
		}
		foreach (IPlayerRole item in config.Roles.OrderBy((IPlayerRole x) => x.Code, StringComparer.OrdinalIgnoreCase))
		{
			RoleEditorRow roleEditorRow = new RoleEditorRow
			{
				Code = item.Code,
				Name = item.Name,
				Description = (item.Description ?? ""),
				PrivilegeLevel = item.PrivilegeLevel,
				ColorHex = $"#{item.Color.R:X2}{item.Color.G:X2}{item.Color.B:X2}",
				AutoGrant = item.AutoGrant,
				DefaultGameMode = item.DefaultGameMode.ToString(),
				LandClaimAllowance = item.LandClaimAllowance,
				LandClaimMaxAreas = item.LandClaimMaxAreas,
				ClaimMinX = (item.LandClaimMinSize?.X ?? 5),
				ClaimMinY = (item.LandClaimMinSize?.Y ?? 5),
				ClaimMinZ = (item.LandClaimMinSize?.Z ?? 5),
				Privileges = (item.Privileges ?? new List<string>()).OrderBy((string x) => x).ToList(),
				RuntimePrivileges = (item.RuntimePrivileges ?? new HashSet<string>()).OrderBy((string x) => x).ToList(),
				IsDefault = string.Equals(config.DefaultRoleCode, item.Code, StringComparison.OrdinalIgnoreCase),
				AssignedPlayers = dictionary.GetValueOrDefault(item.Code)
			};
			if (publicPlayerListConfig != null && publicPlayerListConfig.RoleNameColors.TryGetValue(item.Code, out string value))
			{
				roleEditorRow.NameColorHex = value;
			}
			if (playerCommandConfig != null && playerCommandConfig.HomeRoleLimits.TryGetValue(item.Code, out var value2))
			{
				roleEditorRow.HomeLimit = value2;
			}
			string[] commands2 = Commands;
			foreach (string key2 in commands2)
			{
				Dictionary<string, TeleportRolePolicy> dictionary2 = playerCommandConfig?.TeleportRolePolicies.GetValueOrDefault(key2);
				roleEditorRow.Policies[key2] = ((dictionary2 != null && dictionary2.TryGetValue(item.Code, out var value3)) ? new TeleportRolePolicy
				{
					Allowed = value3.Allowed,
					CooldownSeconds = value3.CooldownSeconds
				} : null);
			}
			roleEditorSnapshot2.Roles.Add(roleEditorRow);
		}
		roleEditorSnapshot2.Revision = Hash(JsonSerializer.Serialize(new
		{
			roles = roleEditorSnapshot2.Roles.Select((RoleEditorRow r) => new
			{
				r.Code, r.Name, r.Description, r.PrivilegeLevel, r.ColorHex, r.NameColorHex, r.AutoGrant, r.DefaultGameMode, r.LandClaimAllowance, r.LandClaimMaxAreas,
				r.ClaimMinX, r.ClaimMinY, r.ClaimMinZ, r.Privileges, r.RuntimePrivileges, r.HomeLimit, r.Policies
			}),
			UseRoleNameColors = roleEditorSnapshot2.UseRoleNameColors,
			policyRevision = ((playerCommandConfig == null) ? "" : PolicyRevision(playerCommandConfig))
		}));
		return roleEditorSnapshot2;
	}

	public static string PolicyRevision(PlayerCommandConfig config)
	{
		return Hash(JsonSerializer.Serialize(new
		{
			HomeGlobalLimit = config.HomeGlobalLimit,
			limits = config.HomeRoleLimits.OrderBy((KeyValuePair<string, int> x) => x.Key),
			policies = from x in config.TeleportRolePolicies
				orderby x.Key
				select new
				{
					command = x.Key,
					roles = x.Value.OrderBy((KeyValuePair<string, TeleportRolePolicy> v) => v.Key)
				},
			SpawnCooldownSeconds = config.SpawnCooldownSeconds,
			HomeCooldownSeconds = config.HomeCooldownSeconds,
			BackCooldownSeconds = config.BackCooldownSeconds,
			WarpDefaultCooldownSeconds = config.WarpDefaultCooldownSeconds,
			T2pCooldownSeconds = config.T2pCooldownSeconds,
			T2mCooldownSeconds = config.T2mCooldownSeconds,
			RtpCooldownSeconds = config.RtpCooldownSeconds
		}));
	}

	public static string ColorRevision(PublicPlayerListConfig? config)
	{
		return Hash(JsonSerializer.Serialize(new
		{
			enabled = (config?.UseRoleNameColors ?? false),
			colors = config?.RoleNameColors.OrderBy((KeyValuePair<string, string> x) => x.Key)
		}));
	}

	private static string Hash(string text)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
	}

	public RoleEditorResult Change(RoleEditorRequest? request)
	{
		RoleEditorSnapshot snapshot = GetSnapshot();
		if (!snapshot.Available || !(api.Server.Config is ServerConfig serverConfig))
		{
			return Fail("Role definitions require the supported Vintage Story server configuration.");
		}
		if (request == null || request.Role == null)
		{
			return Fail("Role request is required.");
		}
		if (request.Revision != snapshot.Revision)
		{
			return Fail("Roles or shared policies changed elsewhere. Reload before saving; your draft has been kept.", conflict: true);
		}
		RoleEditorRow role = request.Role;
		string code = (role.Code ?? "").Trim().ToLowerInvariant();
		PlayerRole existing = serverConfig.Roles.FirstOrDefault((PlayerRole r) => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
		bool flag = request.Action == "create";
		bool flag2 = request.Action == "delete";
		bool flag3;
		switch (request.Action)
		{
		case "create":
		case "save":
		case "delete":
			flag3 = true;
			break;
		default:
			flag3 = false;
			break;
		}
		if (!flag3)
		{
			return Fail("Unknown role action.");
		}
		if (flag && existing != null)
		{
			return Fail("A role with this code already exists.");
		}
		if (!flag && existing == null)
		{
			return Fail("The role no longer exists.");
		}
		if (flag && !Regex.IsMatch(code, "^[a-z0-9][a-z0-9_-]{0,63}$"))
		{
			return Fail("New role codes use 1–64 letters, numbers, underscores or hyphens.");
		}
		if (flag && serverConfig.Roles.Count >= 1024)
		{
			return Fail("Role limit reached (1024).");
		}
		if (flag2)
		{
			if (string.Equals(serverConfig.DefaultRoleCode, code, StringComparison.OrdinalIgnoreCase))
			{
				return Fail("The server default role cannot be removed.");
			}
			if (snapshot.Roles.First((RoleEditorRow r) => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).AssignedPlayers > 0)
			{
				return Fail("Reassign this role's online and offline players before removing it.");
			}
			if (IsAdmin(existing) && !serverConfig.Roles.Any((PlayerRole r) => r != existing && IsAdmin(r)))
			{
				return Fail("Keep at least one administrative game role.");
			}
			goto IL_0672;
		}
		string text = ValidateDefinitionFields(role);
		if (text != null)
		{
			return Fail(text);
		}
		if (string.IsNullOrWhiteSpace(role.Name) || role.Name.Length > 80 || (role.Description ?? "").Length > 500)
		{
			return Fail("Set a role name up to 80 characters and description up to 500.");
		}
		if (!Regex.IsMatch(role.ColorHex ?? "", "^#[0-9a-fA-F]{6}$") || (!string.IsNullOrEmpty(role.NameColorHex) && !Regex.IsMatch(role.NameColorHex, "^#[0-9a-fA-F]{6}$")))
		{
			return Fail("Colors must use #RRGGBB.");
		}
		if (!Enum.TryParse<EnumGameMode>(role.DefaultGameMode, ignoreCase: true, out var result) || !Enum.IsDefined(result))
		{
			return Fail("Unknown default game mode.");
		}
		if (role.PrivilegeLevel < 0 || role.PrivilegeLevel > 100000 || role.LandClaimAllowance < 0 || role.LandClaimMaxAreas < 0 || role.LandClaimMaxAreas > 10000 || role.ClaimMinX < 1 || role.ClaimMinY < 1 || role.ClaimMinZ < 1 || role.ClaimMinX > 10000 || role.ClaimMinY > 10000 || role.ClaimMinZ > 10000)
		{
			return Fail("Role attributes are outside their supported bounds.");
		}
		role.Privileges = (from x in role.Privileges ?? new List<string>()
			select (x ?? "").Trim() into x
			where x.Length > 0
			select x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (role.Privileges.Count > 1024 || role.Privileges.Any((string x) => x.Length > 128 || x.Any(char.IsWhiteSpace)))
		{
			return Fail("Use privilege codes without whitespace (maximum 1024).");
		}
		if (role.AutoGrant)
		{
			role.Privileges = role.Privileges.Union(Privilege.AllCodes(), StringComparer.OrdinalIgnoreCase).ToList();
		}
		if (existing != null && IsAdmin(existing) && !role.AutoGrant && !role.Privileges.Any(IsAdminPrivilege) && !serverConfig.Roles.Any((PlayerRole r) => r != existing && IsAdmin(r)))
		{
			return Fail("Keep at least one role with administrative privileges.");
		}
		int? homeLimit = role.HomeLimit;
		if (homeLimit.HasValue)
		{
			int valueOrDefault = homeLimit.GetValueOrDefault();
			if (valueOrDefault < 0 || valueOrDefault > 100)
			{
				flag3 = true;
				goto IL_05f9;
			}
		}
		flag3 = false;
		goto IL_05f9;
		IL_05f9:
		if (flag3)
		{
			return Fail("Home limit must be 0–100, or empty to inherit.");
		}
		RoleEditorRow roleEditorRow = role;
		if (roleEditorRow.Policies == null)
		{
			Dictionary<string, TeleportRolePolicy> dictionary = (roleEditorRow.Policies = new Dictionary<string, TeleportRolePolicy>());
		}
		if (role.Policies.Any((KeyValuePair<string, TeleportRolePolicy> x) =>
		{
			bool flag4 = !Commands.Contains(x.Key);
			bool flag5 = flag4;
			bool flag6;
			if (!flag5)
			{
				int? num2 = x.Value?.CooldownSeconds;
				if (num2.HasValue)
				{
					int valueOrDefault3 = num2.GetValueOrDefault();
					if (valueOrDefault3 < -1 || valueOrDefault3 > 604800)
					{
						flag6 = true;
						goto IL_0068;
					}
				}
				flag6 = false;
				goto IL_0068;
			}
			goto IL_006b;
			IL_0068:
			flag5 = flag6;
			goto IL_006b;
			IL_006b:
			return flag5;
		}))
		{
			return Fail("Invalid teleport policy or cooldown (−1 to inherit, 0–604800 otherwise).");
		}
		goto IL_0672;
		IL_0672:
		PlayerCommandConfig playerCommandConfig = policyConfig();
		PublicPlayerListServer publicPlayerListServer = playerList();
		Dictionary<string, int> homeRoleLimits = playerCommandConfig?.HomeRoleLimits;
		Dictionary<string, Dictionary<string, TeleportRolePolicy>> teleportRolePolicies = playerCommandConfig?.TeleportRolePolicies;
		PublicPlayerListConfig publicPlayerListConfig = publicPlayerListServer?.Config;
		try
		{
			if (playerCommandConfig != null)
			{
				Dictionary<string, int> dictionary3 = new Dictionary<string, int>(playerCommandConfig.HomeRoleLimits, StringComparer.OrdinalIgnoreCase);
				Dictionary<string, Dictionary<string, TeleportRolePolicy>> dictionary4 = playerCommandConfig.TeleportRolePolicies.ToDictionary((KeyValuePair<string, Dictionary<string, TeleportRolePolicy>> x) => x.Key, (KeyValuePair<string, Dictionary<string, TeleportRolePolicy>> x) => new Dictionary<string, TeleportRolePolicy>(x.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
				dictionary3.Remove(code);
				if (!flag2 && role.HomeLimit.HasValue)
				{
					dictionary3[code] = role.HomeLimit.Value;
				}
				string[] commands = Commands;
				foreach (string key in commands)
				{
					dictionary4.TryGetValue(key, out var value);
					if (value == null)
					{
						value = new Dictionary<string, TeleportRolePolicy>(StringComparer.OrdinalIgnoreCase);
					}
					value.Remove(code);
					if (!flag2)
					{
						TeleportRolePolicy valueOrDefault2 = role.Policies.GetValueOrDefault(key);
						if (valueOrDefault2 != null)
						{
							value[code] = new TeleportRolePolicy
							{
								Allowed = valueOrDefault2.Allowed,
								CooldownSeconds = valueOrDefault2.CooldownSeconds
							};
						}
					}
					if (value.Count > 0)
					{
						dictionary4[key] = value;
					}
					else
					{
						dictionary4.Remove(key);
					}
				}
				playerCommandConfig.HomeRoleLimits = dictionary3;
				playerCommandConfig.TeleportRolePolicies = dictionary4;
				SuiteConfigStorage.Store(api, playerCommandConfig, "NewfiesPlayerCommands.json");
			}
			if (publicPlayerListServer != null)
			{
				Dictionary<string, string> dictionary5 = new Dictionary<string, string>(publicPlayerListServer.Config.RoleNameColors, StringComparer.OrdinalIgnoreCase);
				dictionary5.Remove(code);
				if (!flag2 && !string.IsNullOrEmpty(role.NameColorHex))
				{
					dictionary5[code] = role.NameColorHex.ToUpperInvariant();
				}
				publicPlayerListServer.ApplyRoleNameColors(request.UseRoleNameColors ?? publicPlayerListServer.Config.UseRoleNameColors, dictionary5);
			}
		}
		catch (Exception ex)
		{
			if (playerCommandConfig != null)
			{
				playerCommandConfig.HomeRoleLimits = homeRoleLimits;
				playerCommandConfig.TeleportRolePolicies = teleportRolePolicies;
				try
				{
					SuiteConfigStorage.Store(api, playerCommandConfig, "NewfiesPlayerCommands.json");
				}
				catch
				{
				}
			}
			if (publicPlayerListServer != null && publicPlayerListConfig != null)
			{
				try
				{
					publicPlayerListServer.ApplyConfig(publicPlayerListConfig);
				}
				catch
				{
				}
			}
			return Fail("Role settings could not be saved. Role definitions were left unchanged. " + ex.Message);
		}
		if (flag2)
		{
			serverConfig.Roles.Remove(existing);
			serverConfig.RolesByCode.Remove(existing.Code);
		}
		else
		{
			PlayerRole playerRole = existing ?? new PlayerRole
			{
				Code = code
			};
			playerRole.Name = role.Name.Trim();
			playerRole.Description = (role.Description ?? "").Trim();
			playerRole.PrivilegeLevel = role.PrivilegeLevel;
			playerRole.Color = Color.FromArgb(Convert.ToInt32(role.ColorHex.Substring(1, 2), 16), Convert.ToInt32(role.ColorHex.Substring(3, 2), 16), Convert.ToInt32(role.ColorHex.Substring(5, 2), 16));
			playerRole.AutoGrant = role.AutoGrant;
			playerRole.DefaultGameMode = Enum.Parse<EnumGameMode>(role.DefaultGameMode, ignoreCase: true);
			playerRole.LandClaimAllowance = role.LandClaimAllowance;
			playerRole.LandClaimMaxAreas = role.LandClaimMaxAreas;
			playerRole.LandClaimMinSize = new Vec3i(role.ClaimMinX, role.ClaimMinY, role.ClaimMinZ);
			playerRole.Privileges = role.Privileges;
			if (flag)
			{
				serverConfig.Roles.Add(playerRole);
				serverConfig.RolesByCode[code] = playerRole;
			}
		}
		api.Server.MarkConfigDirty();
		try
		{
			(api.World as ServerMain)?.BroadcastRoles();
			foreach (IServerPlayer item in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
			{
				IPlayerRole role2 = item.Role;
				if (role2 != null && role2.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
				{
					item.BroadcastPlayerData();
				}
			}
		}
		catch (Exception ex2)
		{
			api.Logger.Warning("[Suite/Roles] Saved role but client display refresh failed: {0}", ex2.Message);
		}
		return new RoleEditorResult
		{
			Success = true,
			Message = (flag2 ? "Role removed. Shared Suite role overrides were cleared." : "Role and shared policies applied. Vintage Story will persist serverconfig.json through its normal save loop."),
			Snapshot = GetSnapshot()
		};
		static RoleEditorResult Fail(string message, bool conflict = false)
		{
			return new RoleEditorResult
			{
				Message = message,
				Conflict = conflict
			};
		}
	}

	private static bool IsAdminPrivilege(string code)
	{
		return code.Equals("controlserver", StringComparison.OrdinalIgnoreCase) || code.Equals("root", StringComparison.OrdinalIgnoreCase) || code == "*";
	}

	private static bool IsAdmin(PlayerRole role)
	{
		return role.AutoGrant || (role.Privileges ?? new List<string>()).Any(IsAdminPrivilege);
	}

	public object GetPlayers(string query, int limit)
	{
		query = (query ?? "").Trim();
		limit = Math.Clamp(limit, 1, 100);
		HashSet<string> online = new HashSet<string>(api.World.AllOnlinePlayers.Select((IPlayer x) => x.PlayerUID), StringComparer.OrdinalIgnoreCase);
		List<IServerPlayerData> list = (from p in api.PlayerData.PlayerDataByUid.Values
			where query.Length == 0 || p.LastKnownPlayername.Contains(query, StringComparison.OrdinalIgnoreCase) || p.PlayerUID.Contains(query, StringComparison.OrdinalIgnoreCase) || p.RoleCode.Contains(query, StringComparison.OrdinalIgnoreCase)
			orderby online.Contains(p.PlayerUID) descending, p.LastKnownPlayername
			select p).ToList();
		return new
		{
			total = list.Count,
			players = from p in list.Take(limit)
				select new
				{
					playerUid = p.PlayerUID,
					playerName = p.LastKnownPlayername,
					roleCode = p.RoleCode,
					online = online.Contains(p.PlayerUID)
				}
		};
	}

	public RoleEditorResult SetPlayerRole(RolePlayerRequest? request)
	{
		if (request == null || string.IsNullOrWhiteSpace(request.PlayerUid) || string.IsNullOrWhiteSpace(request.RoleCode))
		{
			return new RoleEditorResult
			{
				Message = "Choose a known player."
			};
		}
		IPlayerRole role = api.Permissions.GetRole(request.RoleCode);
		IServerPlayerData playerDataByUid = api.PlayerData.GetPlayerDataByUid(request.PlayerUid);
		if (role == null || playerDataByUid == null)
		{
			return new RoleEditorResult
			{
				Message = "Player or role no longer exists."
			};
		}
		if (playerDataByUid.RoleCode != request.ExpectedRoleCode)
		{
			return new RoleEditorResult
			{
				Conflict = true,
				Message = "This player's role changed elsewhere. Refresh the player list."
			};
		}
		IServerPlayer serverPlayer = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer x) => x.PlayerUID == request.PlayerUid);
		if (serverPlayer != null)
		{
			api.Permissions.SetRole(serverPlayer, role);
		}
		else
		{
			if (!(playerDataByUid is ServerPlayerData serverPlayerData) || !(api.PlayerData is PlayerDataManager playerDataManager))
			{
				return new RoleEditorResult
				{
					Message = "Offline role updates are unavailable in this server implementation."
				};
			}
			serverPlayerData.RoleCode = role.Code;
			playerDataManager.playerDataDirty = true;
		}
		return new RoleEditorResult
		{
			Success = true,
			Message = $"Assigned {playerDataByUid.LastKnownPlayername} to {role.Name}. Existing direct grants/denials were retained."
		};
	}

	public static object DescribePolicy(RoleEditorSnapshot snapshot, RoleEditorRow row, IEnumerable<string> grants, IEnumerable<string> denials, int? playerHomeLimit = null, int extraClaimAllowance = 0, int extraClaimAreas = 0, int? namedRoleHomeLimit = null)
	{
		HashSet<string> denied = new HashSet<string>(denials, StringComparer.OrdinalIgnoreCase);
		string[] array = (from p in (row.AutoGrant ? snapshot.KnownPrivileges : row.Privileges).Concat(row.RuntimePrivileges).Concat(grants).Distinct(StringComparer.OrdinalIgnoreCase)
			where !denied.Contains(p)
			orderby p
			select p).ToArray();
		bool administrative = array.Any((string p) =>
		{
			switch (p)
			{
			case "root":
			case "controlserver":
			case "*":
				return true;
			default:
				return false;
			}
		});
		var commands = Commands.Select((string command) =>
		{
			row.Policies.TryGetValue(command, out TeleportRolePolicy value);
			snapshot.WildcardPolicies.TryGetValue(command, out TeleportRolePolicy value2);
			TeleportRolePolicy teleportRolePolicy = value ?? value2;
			string source = ((value != null) ? "Role override" : ((value2 != null) ? "Wildcard override" : "Command default"));
			TeleportRolePolicy teleportRolePolicy2 = teleportRolePolicy;
			bool flag;
			if (teleportRolePolicy2 != null)
			{
				flag = teleportRolePolicy2.Allowed;
			}
			else
			{
				bool flag2 = ((command == "t2p" || command == "t2m") ? true : false);
				flag = !flag2 | administrative;
			}
			bool allowed = flag;
			int cooldownSeconds = ((teleportRolePolicy != null && teleportRolePolicy.CooldownSeconds >= 0) ? teleportRolePolicy.CooldownSeconds : snapshot.DefaultCooldowns.GetValueOrDefault(command));
			return new { command, allowed, cooldownSeconds, source };
		}).ToArray();
		string code = row.Code;
		string name = row.Name;
		string[] privileges = array;
		string[] deniedPrivileges = denied.OrderBy((string x) => x).ToArray();
		int homeLimit = playerHomeLimit ?? row.HomeLimit ?? namedRoleHomeLimit ?? snapshot.GlobalHomeLimit;
		string homeSource;
		if (playerHomeLimit.HasValue)
		{
			homeSource = "Player override";
		}
		else
		{
			homeSource = ((row.HomeLimit.HasValue || namedRoleHomeLimit.HasValue) ? "Role override" : "Global default");
		}
		return new
		{
			Code = code,
			Name = name,
			privileges = privileges,
			deniedPrivileges = deniedPrivileges,
			homeLimit = homeLimit,
			homeSource = homeSource,
			commands = commands,
			claimAllowance = (int)Math.Clamp((long)row.LandClaimAllowance + (long)extraClaimAllowance, 0L, 2147483647L),
			claimAreas = (int)Math.Clamp((long)row.LandClaimMaxAreas + (long)extraClaimAreas, 0L, 2147483647L),
			note = "Preview preserves direct grants/denials. Runtime grants can change when another mod changes them; teleport warmups and command feature gates remain separate."
		};
	}

	public object PreviewPlayer(string uid, string targetRole)
	{
		return PreviewPlayer(uid, targetRole, GetSnapshot());
	}

	private object PreviewPlayer(string uid, string targetRole, RoleEditorSnapshot snapshot)
	{
		IServerPlayerData data = api.PlayerData.GetPlayerDataByUid(uid);
		if (data == null)
		{
			return new
			{
				available = false,
				message = "Player not found."
			};
		}
		RoleEditorRow row = snapshot.Roles.FirstOrDefault((RoleEditorRow r) => string.Equals(r.Code, string.IsNullOrWhiteSpace(targetRole) ? data.RoleCode : targetRole, StringComparison.OrdinalIgnoreCase));
		if (row == null)
		{
			return new
			{
				available = false,
				message = "Role not found."
			};
		}
		PlayerCommandConfig playerCommandConfig = policyConfig();
		int? playerHomeLimit = null;
		int? namedRoleHomeLimit = null;
		if (playerCommandConfig != null)
		{
			if (playerCommandConfig.HomePlayerLimits.TryGetValue(uid, out var value) || playerCommandConfig.HomePlayerLimits.TryGetValue(data.LastKnownPlayername, out value))
			{
				playerHomeLimit = Math.Max(0, value);
			}
			if (!row.HomeLimit.HasValue && playerCommandConfig.HomeRoleLimits.TryGetValue(row.Name, out var value2))
			{
				namedRoleHomeLimit = Math.Max(0, value2);
			}
		}
		IServerPlayerData serverPlayerData = data;
		string playerUid = uid;
		string lastKnownPlayername = data.LastKnownPlayername;
		string roleCode = data.RoleCode;
		string revision = snapshot.Revision;
		RoleEditorRow row2 = row;
		IEnumerable<string> enumerable = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => p.PlayerUID == uid && row.Code == data.RoleCode)?.Privileges;
		return new
		{
			available = true,
			playerUid = playerUid,
			playerName = lastKnownPlayername,
			currentRole = roleCode,
			revision = revision,
			effective = DescribePolicy(snapshot, row2, enumerable ?? serverPlayerData?.PermaPrivileges ?? new HashSet<string>(), serverPlayerData?.DeniedPrivileges ?? new HashSet<string>(), playerHomeLimit, serverPlayerData?.ExtraLandClaimAllowance ?? 0, serverPlayerData?.ExtraLandClaimAreas ?? 0, namedRoleHomeLimit)
		};
	}

	public object PreviewDefinition(RoleEditorRequest? request)
	{
		RoleEditorSnapshot snapshot = GetSnapshot();
		if (request?.Role == null)
		{
			return new
			{
				success = false,
				message = "Invalid role preview."
			};
		}
		if (!snapshot.Available)
		{
			return new
			{
				success = false,
				message = "Role definitions require the supported Vintage Story server configuration."
			};
		}
		if (request.Revision != snapshot.Revision)
		{
			return new
			{
				success = false,
				conflict = true,
				message = "Role definitions changed; reload before previewing."
			};
		}
		RoleEditorRow row = request.Role;
		RoleEditorRow roleEditorRow = row;
		if (roleEditorRow.Privileges == null)
		{
			List<string> list = (roleEditorRow.Privileges = new List<string>());
		}
		roleEditorRow = row;
		if (roleEditorRow.Policies == null)
		{
			Dictionary<string, TeleportRolePolicy> dictionary = (roleEditorRow.Policies = new Dictionary<string, TeleportRolePolicy>());
		}
		string text = ValidateDefinitionFields(row);
		if (text != null)
		{
			return new
			{
				success = false,
				message = text
			};
		}
		string code = (row.Code ?? "").Trim().ToLowerInvariant();
		RoleEditorRow existing = snapshot.Roles.FirstOrDefault((RoleEditorRow r) => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
		string action = request.Action;
		if ((!(action == "create") && !(action == "save")) || 1 == 0)
		{
			return new
			{
				success = false,
				message = "Preview a role creation or edit."
			};
		}
		if (request.Action == "create")
		{
			if (existing != null)
			{
				return new
				{
					success = false,
					message = "A role with this code already exists."
				};
			}
			if (!Regex.IsMatch(code, "^[a-z0-9][a-z0-9_-]{0,63}$"))
			{
				return new
				{
					success = false,
					message = "New role codes use 1–64 letters, numbers, underscores or hyphens."
				};
			}
			if (snapshot.Roles.Count >= 1024)
			{
				return new
				{
					success = false,
					message = "Role limit reached (1024)."
				};
			}
		}
		else if (existing == null)
		{
			return new
			{
				success = false,
				message = "The role no longer exists."
			};
		}
		row.Privileges = (from p in row.Privileges
			select (p ?? "").Trim() into p
			where p.Length > 0
			select p).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (existing != null && Administrative(existing) && !Administrative(row) && !snapshot.Roles.Any((RoleEditorRow r) => r != existing && Administrative(r)))
		{
			return new
			{
				success = false,
				message = "Keep at least one role with administrative privileges."
			};
		}
		row.Code = code;
		row.RuntimePrivileges = snapshot.Roles.FirstOrDefault((RoleEditorRow r) => r.Code == row.Code)?.RuntimePrivileges ?? new List<string>();
		return new
		{
			success = true,
			revision = snapshot.Revision,
			effective = DescribePolicy(snapshot, row, Array.Empty<string>(), Array.Empty<string>()),
			affectedPlayers = (snapshot.Roles.FirstOrDefault((RoleEditorRow r) => r.Code == row.Code)?.AssignedPlayers ?? 0)
		};
		static bool Administrative(RoleEditorRow r)
		{
			return r.AutoGrant || r.Privileges.Any(IsAdminPrivilege);
		}
	}

	private static string? ValidateDefinitionFields(RoleEditorRow row)
	{
		if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 80 || (row.Description?.Length ?? 0) > 500)
		{
			return "Set a role name up to 80 characters and description up to 500.";
		}
		if (!Regex.IsMatch(row.ColorHex ?? "", "^#[0-9a-fA-F]{6}$") || (!string.IsNullOrEmpty(row.NameColorHex) && !Regex.IsMatch(row.NameColorHex, "^#[0-9a-fA-F]{6}$")))
		{
			return "Colors must use #RRGGBB.";
		}
		if (!Enum.TryParse<EnumGameMode>(row.DefaultGameMode, ignoreCase: true, out var result) || !Enum.IsDefined(result))
		{
			return "Unknown default game mode.";
		}
		int privilegeLevel = row.PrivilegeLevel;
		bool flag = ((privilegeLevel < 0 || privilegeLevel > 100000) ? true : false);
		bool flag2 = flag || row.LandClaimAllowance < 0;
		bool flag3 = flag2;
		if (!flag3)
		{
			int landClaimMaxAreas = row.LandClaimMaxAreas;
			bool flag4 = ((landClaimMaxAreas < 0 || landClaimMaxAreas > 10000) ? true : false);
			flag3 = flag4;
		}
		bool flag5 = flag3;
		if (!flag5)
		{
			int landClaimMaxAreas = row.ClaimMinX;
			bool flag4 = ((landClaimMaxAreas < 1 || landClaimMaxAreas > 10000) ? true : false);
			flag5 = flag4;
		}
		bool flag6 = flag5;
		if (!flag6)
		{
			int landClaimMaxAreas = row.ClaimMinY;
			bool flag4 = ((landClaimMaxAreas < 1 || landClaimMaxAreas > 10000) ? true : false);
			flag6 = flag4;
		}
		bool flag7 = flag6;
		if (!flag7)
		{
			int landClaimMaxAreas = row.ClaimMinZ;
			bool flag4 = ((landClaimMaxAreas < 1 || landClaimMaxAreas > 10000) ? true : false);
			flag7 = flag4;
		}
		if (flag7)
		{
			return "Role attributes are outside their supported bounds.";
		}
		string[] array = (from p in row.Privileges ?? new List<string>()
			select (p ?? "").Trim() into p
			where p.Length > 0
			select p).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length > 1024 || array.Any((string p) => p.Length > 128 || p.Any(char.IsWhiteSpace)))
		{
			return "Use privilege codes without whitespace (maximum 1024).";
		}
		int? homeLimit = row.HomeLimit;
		if (homeLimit.HasValue)
		{
			privilegeLevel = homeLimit.GetValueOrDefault();
			if (privilegeLevel < 0 || privilegeLevel > 100)
			{
				flag = true;
				goto IL_0295;
			}
		}
		flag = false;
		goto IL_0295;
		IL_0295:
		if (flag)
		{
			return "Home limit must be 0–100, or empty to inherit.";
		}
		if ((row.Policies ?? new Dictionary<string, TeleportRolePolicy>()).Any((KeyValuePair<string, TeleportRolePolicy> p) =>
		{
			bool flag8 = !Commands.Contains(p.Key);
			bool flag9 = flag8;
			bool flag10;
			if (!flag9)
			{
				int? num = p.Value?.CooldownSeconds;
				if (num.HasValue)
				{
					int valueOrDefault = num.GetValueOrDefault();
					if (valueOrDefault < -1 || valueOrDefault > 604800)
					{
						flag10 = true;
						goto IL_0068;
					}
				}
				flag10 = false;
				goto IL_0068;
			}
			goto IL_006b;
			IL_0068:
			flag9 = flag10;
			goto IL_006b;
			IL_006b:
			return flag9;
		}))
		{
			return "Invalid teleport policy or cooldown (−1 to inherit, 0–604800 otherwise).";
		}
		return null;
	}

	public object ChangeBatch(RoleBatchRequest? request)
	{
		bool flag = request?.Players == null;
		bool flag2 = flag;
		if (!flag2)
		{
			int count = request.Players.Count;
			bool flag3 = ((count < 1 || count > 50) ? true : false);
			flag2 = flag3;
		}
		if (flag2 || request.Players.Any((RolePlayerRequest p) => p == null || string.IsNullOrWhiteSpace(p.PlayerUid) || p.PlayerUid.Length > 128 || (p.ExpectedRoleCode?.Length ?? 0) > 64))
		{
			return new
			{
				success = false,
				message = "Select 1–50 players."
			};
		}
		RoleEditorSnapshot snapshot = GetSnapshot();
		if (request.Revision != snapshot.Revision)
		{
			return new
			{
				success = false,
				conflict = true,
				message = "Role definitions changed. Preview again."
			};
		}
		if (api.Permissions.GetRole(request.RoleCode) == null)
		{
			return new
			{
				success = false,
				message = "Target role not found."
			};
		}
		if (request.Players.Select((RolePlayerRequest p) => p.PlayerUid).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Players.Count)
		{
			return new
			{
				success = false,
				message = "Duplicate selected players."
			};
		}
		foreach (RolePlayerRequest player in request.Players)
		{
			IServerPlayerData playerDataByUid = api.PlayerData.GetPlayerDataByUid(player.PlayerUid);
			if (playerDataByUid == null || playerDataByUid.RoleCode != player.ExpectedRoleCode)
			{
				return new
				{
					success = false,
					conflict = true,
					message = "A selected player's role changed. Search and preview again; nothing was assigned."
				};
			}
		}
		if (request.Preview)
		{
			return new
			{
				success = true,
				preview = true,
				revision = snapshot.Revision,
				players = request.Players.Select((RolePlayerRequest p) => PreviewPlayer(p.PlayerUid, request.RoleCode, snapshot)).ToArray()
			};
		}
		List<object> list = new List<object>();
		int num = 0;
		foreach (RolePlayerRequest player2 in request.Players)
		{
			RoleEditorResult roleEditorResult;
			try
			{
				roleEditorResult = SetPlayerRole(new RolePlayerRequest
				{
					PlayerUid = player2.PlayerUid,
					ExpectedRoleCode = player2.ExpectedRoleCode,
					RoleCode = request.RoleCode
				});
			}
			catch
			{
				roleEditorResult = new RoleEditorResult
				{
					Message = "Assignment failed; earlier successful assignments remain applied. Refresh before retrying."
				};
			}
			list.Add(new { player2.PlayerUid, roleEditorResult.Success, roleEditorResult.Message });
			if (roleEditorResult.Success)
			{
				num++;
				continue;
			}
			break;
		}
		return new
		{
			success = (num == request.Players.Count),
			preview = false,
			applied = num,
			requested = request.Players.Count,
			results = list,
			message = $"Assigned {num} of {request.Players.Count} players. Direct grants and denials were retained."
		};
	}
}
