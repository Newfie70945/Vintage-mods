using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

/// <summary>
/// Privilege-gated supporter nickname presentation layer. Nicknames never replace the
/// authoritative Vintage Story account/player name; they are used only by Suite-owned
/// presentation surfaces such as Chat Display and the Suite-backed Player List.
/// </summary>
public sealed class SupporterNicknameService : IDisposable
{
    public const string PrivilegeCode = "supporternickname";
    public const string ConfigFile = "NewfiesSupporterNicknames.json";
    public const int CurrentSchemaVersion = 1;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "moderator", "mod", "staff", "server", "console", "system", "owner"
    };

    private readonly ICoreServerAPI api;
    private readonly object gate = new();
    private SupporterNicknameConfig config = new();
    private bool started;

    public event Action? NicknameChanged;

    public SupporterNicknameService(ICoreServerAPI api) => this.api = api;

    public SupporterNicknameConfig Config
    {
        get
        {
            lock (gate) return config.Clone();
        }
    }

    public void Start()
    {
        if (started) return;
        started = true;

        Load();
        api.Permissions.RegisterPrivilege(PrivilegeCode, "Allows the player to set a supporter nickname with /nick or /nickname.");

        RegisterCommand("nick");
        RegisterCommand("nickname");

        api.Logger.Notification(
            "[ServerEssentials/SupporterNickname] Ready. Privilege={0}, cooldown={1}m, length={2}-{3}.",
            PrivilegeCode,
            config.CooldownMinutes,
            config.MinLength,
            config.MaxLength);
    }

    private void RegisterCommand(string command)
    {
        if (api.ChatCommands.Get(command) != null)
        {
            api.Logger.Warning("[ServerEssentials/SupporterNickname] /{0} is already registered by another mod; this alias was not registered.", command);
            return;
        }

        api.ChatCommands.Create(command)
            .WithDescription("Set or reset your supporter nickname.")
            .WithAdditionalInformation("Usage: /" + command + " <nickname> or /" + command + " reset. Nicknames are display-only; your real account name remains authoritative.")
            .WithArgs(api.ChatCommands.Parsers.All("nickname"))
            .RequiresPlayer()
            .RequiresPrivilege(PrivilegeCode)
            .HandleWith(OnNicknameCommand);
    }

    private TextCommandResult OnNicknameCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player == null) return TextCommandResult.Error("This command can only be used by a player.");

        string requested = (args.ArgCount > 0 ? args[0] as string : null) ?? string.Empty;
        requested = requested.Trim();
        if (requested.Length == 0)
            return TextCommandResult.Error("Usage: /nick <nickname> or /nick reset");

        bool reset = requested.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                     requested.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                     requested.Equals("off", StringComparison.OrdinalIgnoreCase);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SupporterNicknameRecord? existing;
        lock (gate)
        {
            config.Players.TryGetValue(player.PlayerUID, out existing);
            if (existing != null && config.CooldownMinutes > 0)
            {
                long cooldownMs = (long)config.CooldownMinutes * 60_000L;
                long remaining = existing.LastChangedUnixMilliseconds + cooldownMs - now;
                if (remaining > 0)
                    return TextCommandResult.Error("You can change your supporter nickname again in " + FormatRemaining(remaining) + ".");
            }
        }

        if (reset)
        {
            bool hadNickname;
            lock (gate)
            {
                hadNickname = config.Players.TryGetValue(player.PlayerUID, out var record) && !string.IsNullOrWhiteSpace(record.Nickname);
                if (!hadNickname) return TextCommandResult.Success("You do not currently have a supporter nickname set.");

                config.Players[player.PlayerUID] = new SupporterNicknameRecord
                {
                    Nickname = string.Empty,
                    LastChangedUnixMilliseconds = now
                };
                SaveUnsafe();
            }

            api.Logger.Notification("[ServerEssentials/SupporterNickname] {0} reset their supporter nickname.", player.PlayerName);
            NicknameChanged?.Invoke();
            return TextCommandResult.Success("Supporter nickname cleared. Your real player name will be shown again.");
        }

        string normalized = NormalizeNickname(requested);
        string? validationError = ValidateNickname(player, normalized);
        if (validationError != null) return TextCommandResult.Error(validationError);

        lock (gate)
        {
            if (config.Players.TryGetValue(player.PlayerUID, out var current) &&
                string.Equals(current.Nickname, normalized, StringComparison.Ordinal))
                return TextCommandResult.Success("Your supporter nickname is already set to " + normalized + ".");

            config.Players[player.PlayerUID] = new SupporterNicknameRecord
            {
                Nickname = normalized,
                LastChangedUnixMilliseconds = now
            };
            SaveUnsafe();
        }

        api.Logger.Notification("[ServerEssentials/SupporterNickname] {0} set supporter nickname to {1}.", player.PlayerName, normalized);
        NicknameChanged?.Invoke();
        return TextCommandResult.Success("Supporter nickname set to " + normalized + ". Your real account name remains authoritative for commands and moderation.");
    }

    /// <summary>Returns the saved supporter nickname for staff/admin presentation surfaces.
    /// This does not replace the authoritative account name and may return a saved nickname
    /// even while the player is offline.
    /// </summary>
    public string GetSavedNickname(string? playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return string.Empty;
        lock (gate)
        {
            if (!config.Players.TryGetValue(playerUid, out var record)) return string.Empty;
            return (record.Nickname ?? string.Empty).Trim();
        }
    }

    public string ResolveDisplayName(string? playerUid, string? realName)
    {
        string fallback = realName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(playerUid)) return fallback;

        // A revoked supporter privilege immediately disables presentation of the nickname,
        // but retains the saved choice in case staff later restores the privilege.
        var online = api.World.PlayerByUid(playerUid) as IServerPlayer;
        if (online != null && !online.HasPrivilege(PrivilegeCode)) return fallback;

        lock (gate)
        {
            if (!config.Players.TryGetValue(playerUid, out var record)) return fallback;
            return string.IsNullOrWhiteSpace(record.Nickname) ? fallback : record.Nickname;
        }
    }

    private string? ValidateNickname(IServerPlayer player, string nickname)
    {
        SupporterNicknameConfig snapshot;
        lock (gate) snapshot = config.Clone();

        if (nickname.Length < snapshot.MinLength || nickname.Length > snapshot.MaxLength)
            return $"Nickname must be {snapshot.MinLength}-{snapshot.MaxLength} characters long.";

        if (ReservedNames.Contains(nickname))
            return "That nickname is reserved for server/staff identification.";

        foreach (char c in nickname)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '\'') continue;
            return "Nickname may contain only letters, numbers, spaces, apostrophes, underscores, and hyphens.";
        }

        // Prevent impersonation of any real account name already known to this server.
        try
        {
            foreach (var pair in api.PlayerData.PlayerDataByUid)
            {
                if (string.Equals(pair.Key, player.PlayerUID, StringComparison.OrdinalIgnoreCase)) continue;
                string knownName = pair.Value?.LastKnownPlayername ?? string.Empty;
                if (knownName.Equals(nickname, StringComparison.OrdinalIgnoreCase))
                    return "That nickname matches another player's real account name.";
            }
        }
        catch
        {
            // If the player-data snapshot is unavailable, keep the nickname system usable;
            // active nickname uniqueness below still applies.
        }

        lock (gate)
        {
            foreach (var pair in config.Players)
            {
                if (string.Equals(pair.Key, player.PlayerUID, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(pair.Value.Nickname) &&
                    pair.Value.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase))
                    return "That supporter nickname is already in use.";
            }
        }

        return null;
    }

    private static string NormalizeNickname(string value)
    {
        value = (value ?? string.Empty).Trim();
        var sb = new StringBuilder(value.Length);
        bool previousWhitespace = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWhitespace && sb.Length > 0) sb.Append(' ');
                previousWhitespace = true;
                continue;
            }
            previousWhitespace = false;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string FormatRemaining(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1)
            return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalSeconds))}s";
    }

    private void Load()
    {
        try
        {
            config = SuiteConfigStorage.Load<SupporterNicknameConfig>(api, ConfigFile) ?? new SupporterNicknameConfig();
            config.Clamp();
            SuiteConfigStorage.Store(api, config, ConfigFile);
        }
        catch (Exception ex)
        {
            config = new SupporterNicknameConfig();
            api.Logger.Warning("[ServerEssentials/SupporterNickname] Could not load {0}; using defaults. {1}: {2}", ConfigFile, ex.GetType().Name, ex.Message);
        }
    }

    private void SaveUnsafe()
    {
        config.Clamp();
        SuiteConfigStorage.Store(api, config, ConfigFile);
    }

    public void Dispose()
    {
        started = false;
        NicknameChanged = null;
    }
}

public sealed class SupporterNicknameConfig
{
    public int SchemaVersion { get; set; } = SupporterNicknameService.CurrentSchemaVersion;
    public int CooldownMinutes { get; set; } = 1440;
    public int MinLength { get; set; } = 3;
    public int MaxLength { get; set; } = 20;
    public Dictionary<string, SupporterNicknameRecord> Players { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Clamp()
    {
        SchemaVersion = Math.Max(SupporterNicknameService.CurrentSchemaVersion, SchemaVersion);
        CooldownMinutes = Math.Clamp(CooldownMinutes, 0, 10080);
        MinLength = Math.Clamp(MinLength, 1, 20);
        MaxLength = Math.Clamp(MaxLength, Math.Max(MinLength, 3), 32);
        Players ??= new Dictionary<string, SupporterNicknameRecord>(StringComparer.OrdinalIgnoreCase);

        var cleaned = new Dictionary<string, SupporterNicknameRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Players.Take(10_000))
        {
            string uid = (pair.Key ?? string.Empty).Trim();
            if (uid.Length == 0 || pair.Value == null) continue;
            string nickname = (pair.Value.Nickname ?? string.Empty).Trim();
            if (nickname.Length > MaxLength) nickname = nickname[..MaxLength];
            cleaned[uid] = new SupporterNicknameRecord
            {
                Nickname = nickname,
                LastChangedUnixMilliseconds = Math.Max(0, pair.Value.LastChangedUnixMilliseconds)
            };
        }
        Players = cleaned;
    }

    public SupporterNicknameConfig Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        CooldownMinutes = CooldownMinutes,
        MinLength = MinLength,
        MaxLength = MaxLength,
        Players = Players.ToDictionary(
            pair => pair.Key,
            pair => new SupporterNicknameRecord
            {
                Nickname = pair.Value.Nickname,
                LastChangedUnixMilliseconds = pair.Value.LastChangedUnixMilliseconds
            },
            StringComparer.OrdinalIgnoreCase)
    };
}

public sealed class SupporterNicknameRecord
{
    public string Nickname { get; set; } = string.Empty;
    public long LastChangedUnixMilliseconds { get; set; }
}
