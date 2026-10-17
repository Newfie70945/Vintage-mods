using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class SupporterNicknameService : IDisposable
{
	public const string PrivilegeCode = "supporternickname";

	public const string ConfigFile = "NewfiesSupporterNicknames.json";

	public const int CurrentSchemaVersion = 1;

	private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "admin", "administrator", "moderator", "mod", "staff", "server", "console", "system", "owner" };

	private readonly ICoreServerAPI api;

	private readonly object gate = new object();

	private SupporterNicknameConfig config = new SupporterNicknameConfig();

	private bool started;

	public SupporterNicknameConfig Config
	{
		get
		{
			lock (gate)
			{
				return config.Clone();
			}
		}
	}

	public event Action? NicknameChanged;

	public SupporterNicknameService(ICoreServerAPI api)
	{
		this.api = api;
	}

	public void Start()
	{
		if (!started)
		{
			started = true;
			Load();
			api.Permissions.RegisterPrivilege("supporternickname", "Allows the player to set a supporter nickname with /nick or /nickname.");
			RegisterCommand("nick");
			RegisterCommand("nickname");
			api.Logger.Notification("[ServerEssentials/SupporterNickname] Ready. Privilege={0}, cooldown={1}m, length={2}-{3}.", "supporternickname", config.CooldownMinutes, config.MinLength, config.MaxLength);
		}
	}

	private void RegisterCommand(string command)
	{
		if (api.ChatCommands.Get(command) != null)
		{
			api.Logger.Warning("[ServerEssentials/SupporterNickname] /{0} is already registered by another mod; this alias was not registered.", command);
			return;
		}
		api.ChatCommands.Create(command).WithDescription("Set or reset your supporter nickname.").WithAdditionalInformation("Usage: /" + command + " <nickname> or /" + command + " reset. Nicknames are display-only; your real account name remains authoritative.")
			.WithArgs(api.ChatCommands.Parsers.All("nickname"))
			.RequiresPlayer()
			.RequiresPrivilege("supporternickname")
			.HandleWith(OnNicknameCommand);
	}

	private TextCommandResult OnNicknameCommand(TextCommandCallingArgs args)
	{
		if (!(args.Caller.Player is IServerPlayer serverPlayer))
		{
			return TextCommandResult.Error("This command can only be used by a player.");
		}
		string text = ((args.ArgCount > 0) ? (args[0] as string) : null) ?? string.Empty;
		text = text.Trim();
		if (text.Length == 0)
		{
			return TextCommandResult.Error("Usage: /nick <nickname> or /nick reset");
		}
		bool flag = text.Equals("reset", StringComparison.OrdinalIgnoreCase) || text.Equals("clear", StringComparison.OrdinalIgnoreCase) || text.Equals("off", StringComparison.OrdinalIgnoreCase);
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		lock (gate)
		{
			config.Players.TryGetValue(serverPlayer.PlayerUID, out SupporterNicknameRecord value);
			if (value != null && config.CooldownMinutes > 0)
			{
				long num2 = (long)config.CooldownMinutes * 60000L;
				long num3 = value.LastChangedUnixMilliseconds + num2 - num;
				if (num3 > 0)
				{
					return TextCommandResult.Error("You can change your supporter nickname again in " + FormatRemaining(num3) + ".");
				}
			}
		}
		if (flag)
		{
			lock (gate)
			{
				if (!config.Players.TryGetValue(serverPlayer.PlayerUID, out SupporterNicknameRecord value2) || string.IsNullOrWhiteSpace(value2.Nickname))
				{
					return TextCommandResult.Success("You do not currently have a supporter nickname set.");
				}
				config.Players[serverPlayer.PlayerUID] = new SupporterNicknameRecord
				{
					Nickname = string.Empty,
					LastChangedUnixMilliseconds = num
				};
				SaveUnsafe();
			}
			api.Logger.Notification("[ServerEssentials/SupporterNickname] {0} reset their supporter nickname.", serverPlayer.PlayerName);
			NicknameChanged?.Invoke();
			return TextCommandResult.Success("Supporter nickname cleared. Your real player name will be shown again.");
		}
		string text2 = NormalizeNickname(text);
		string text3 = ValidateNickname(serverPlayer, text2);
		if (text3 != null)
		{
			return TextCommandResult.Error(text3);
		}
		lock (gate)
		{
			if (config.Players.TryGetValue(serverPlayer.PlayerUID, out SupporterNicknameRecord value3) && string.Equals(value3.Nickname, text2, StringComparison.Ordinal))
			{
				return TextCommandResult.Success("Your supporter nickname is already set to " + text2 + ".");
			}
			config.Players[serverPlayer.PlayerUID] = new SupporterNicknameRecord
			{
				Nickname = text2,
				LastChangedUnixMilliseconds = num
			};
			SaveUnsafe();
		}
		api.Logger.Notification("[ServerEssentials/SupporterNickname] {0} set supporter nickname to {1}.", serverPlayer.PlayerName, text2);
		NicknameChanged?.Invoke();
		return TextCommandResult.Success("Supporter nickname set to " + text2 + ". Your real account name remains authoritative for commands and moderation.");
	}

	public string GetSavedNickname(string? playerUid)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return string.Empty;
		}
		lock (gate)
		{
			if (!config.Players.TryGetValue(playerUid, out SupporterNicknameRecord value))
			{
				return string.Empty;
			}
			return (value.Nickname ?? string.Empty).Trim();
		}
	}

	public string ResolveDisplayName(string? playerUid, string? realName)
	{
		string text = realName ?? string.Empty;
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return text;
		}
		if (api.World.PlayerByUid(playerUid) is IServerPlayer serverPlayer && !serverPlayer.HasPrivilege("supporternickname"))
		{
			return text;
		}
		lock (gate)
		{
			if (!config.Players.TryGetValue(playerUid, out SupporterNicknameRecord value))
			{
				return text;
			}
			return string.IsNullOrWhiteSpace(value.Nickname) ? text : value.Nickname;
		}
	}

	private string? ValidateNickname(IServerPlayer player, string nickname)
	{
		SupporterNicknameConfig supporterNicknameConfig;
		lock (gate)
		{
			supporterNicknameConfig = config.Clone();
		}
		if (nickname.Length < supporterNicknameConfig.MinLength || nickname.Length > supporterNicknameConfig.MaxLength)
		{
			return $"Nickname must be {supporterNicknameConfig.MinLength}-{supporterNicknameConfig.MaxLength} characters long.";
		}
		if (ReservedNames.Contains(nickname))
		{
			return "That nickname is reserved for server/staff identification.";
		}
		foreach (char c in nickname)
		{
			if (!char.IsLetterOrDigit(c) && c != ' ' && c != '_' && c != '-' && c != '\'')
			{
				return "Nickname may contain only letters, numbers, spaces, apostrophes, underscores, and hyphens.";
			}
		}
		try
		{
			foreach (KeyValuePair<string, IServerPlayerData> item in api.PlayerData.PlayerDataByUid)
			{
				if (!string.Equals(item.Key, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
				{
					string text = item.Value?.LastKnownPlayername ?? string.Empty;
					if (text.Equals(nickname, StringComparison.OrdinalIgnoreCase))
					{
						return "That nickname matches another player's real account name.";
					}
				}
			}
		}
		catch
		{
		}
		lock (gate)
		{
			foreach (KeyValuePair<string, SupporterNicknameRecord> player2 in config.Players)
			{
				if (string.Equals(player2.Key, player.PlayerUID, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(player2.Value.Nickname) || !player2.Value.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				return "That supporter nickname is already in use.";
			}
		}
		return null;
	}

	private static string NormalizeNickname(string value)
	{
		value = (value ?? string.Empty).Trim();
		StringBuilder stringBuilder = new StringBuilder(value.Length);
		bool flag = false;
		string text = value;
		foreach (char c in text)
		{
			if (char.IsWhiteSpace(c))
			{
				if (!flag && stringBuilder.Length > 0)
				{
					stringBuilder.Append(' ');
				}
				flag = true;
			}
			else
			{
				flag = false;
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString().Trim();
	}

	private static string FormatRemaining(long milliseconds)
	{
		TimeSpan timeSpan = TimeSpan.FromMilliseconds(Math.Max(0L, milliseconds));
		if (!(timeSpan.TotalDays >= 1.0))
		{
			if (!(timeSpan.TotalHours >= 1.0))
			{
				if (!(timeSpan.TotalMinutes >= 1.0))
				{
					return $"{Math.Max(1, (int)Math.Ceiling(timeSpan.TotalSeconds))}s";
				}
				return $"{Math.Max(1, (int)Math.Ceiling(timeSpan.TotalMinutes))}m";
			}
			return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";
		}
		return $"{(int)timeSpan.TotalDays}d {timeSpan.Hours}h";
	}

	private void Load()
	{
		try
		{
			config = SuiteConfigStorage.Load<SupporterNicknameConfig>(api, "NewfiesSupporterNicknames.json") ?? new SupporterNicknameConfig();
			config.Clamp();
			SuiteConfigStorage.Store(api, config, "NewfiesSupporterNicknames.json");
		}
		catch (Exception ex)
		{
			config = new SupporterNicknameConfig();
			api.Logger.Warning("[ServerEssentials/SupporterNickname] Could not load {0}; using defaults. {1}: {2}", "NewfiesSupporterNicknames.json", ex.GetType().Name, ex.Message);
		}
	}

	private void SaveUnsafe()
	{
		config.Clamp();
		SuiteConfigStorage.Store(api, config, "NewfiesSupporterNicknames.json");
	}

	public void Dispose()
	{
		started = false;
		NicknameChanged = null;
	}
}
