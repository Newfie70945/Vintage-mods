using NewfiesServerEssentials.Discord.Linking.Models;
using NewfiesServerEssentials.Discord.Linking.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace NewfiesServerEssentials.Discord.Linking;

/// <summary>
/// Account-linking authority embedded in the unified Discord module.
/// Owns one-time codes, PlayerUID <-> Discord mappings, in-game commands,
/// and query/change event contracts used by the rest of Newfie's Server Suite.
/// </summary>
public sealed class DiscordLinkService
{
    public const string LegacyDiscordRequestEvent = "newfiesse:discord-link-request";
    public const string QueryEvent = "newfiesse:discord-link-query";
    public const string ChangedEvent = "newfiesse:discord-link-changed";

    private readonly ICoreServerAPI sapi;
    private readonly Func<DiscordLinkConfig> getConfig;
    private readonly DiscordLinkStore store;
    private readonly LinkCodeService codes;
    private long codeMaintenanceListenerId = -1;
    private bool active;

    public DiscordLinkService(ICoreServerAPI sapi, Func<DiscordLinkConfig> getConfig)
    {
        this.sapi = sapi;
        this.getConfig = getConfig;
        store = new DiscordLinkStore(sapi);
        store.Load();
        codes = new LinkCodeService(getConfig);
    }

    public int LinkedAccountCount => store.GetAll().Count;
    public int PendingCodeCount => codes.PendingCount;
    public bool IsStorageReady => store.IsStorageReady;

    public DiscordLinkRecord? GetByPlayerUid(string playerUid) => store.GetByPlayerUid(playerUid);
    public IReadOnlyList<DiscordLinkRecord> GetAllLinks() => store.GetAll();

    public void Register()
    {
        active = true;
        sapi.Event.RegisterEventBusListener(OnLegacyDiscordRequest, filterByEventName: LegacyDiscordRequestEvent);
        sapi.Event.RegisterEventBusListener(OnQuery, filterByEventName: QueryEvent);
        RegisterCommands();
        codeMaintenanceListenerId = sapi.Event.RegisterGameTickListener(_ => { if (active) codes.PruneExpired(); }, 60_000);
    }

    public void Unregister()
    {
        // Vintage Story 1.22.7 exposes no event-bus unregister method. Ignore late
        // events after disposal; the event bus itself is destroyed with the server.
        active = false;
        if (codeMaintenanceListenerId >= 0)
        {
            sapi.Event.UnregisterGameTickListener(codeMaintenanceListenerId);
            codeMaintenanceListenerId = -1;
        }
        codes.Clear();
    }

    private void RegisterCommands()
    {
#pragma warning disable CS0618
        var playerCommandRegistered = sapi.RegisterCommand(
            "discordlink",
            "Link your Vintage Story account to Discord.",
            "",
            OnPlayerCommand,
            Privilege.chat);

        if (!playerCommandRegistered)
        {
            sapi.Logger.Warning("[NSE Discord] Could not register /discordlink because another mod already owns that command. Remove the legacy standalone Discord Link mod if it is still installed.");
        }

        sapi.RegisterCommand(
            "discordlinkadmin",
            "Admin tools for Newfie's Discord account linking.",
            "stats | status <player> | discord <discordUserId> | unlink <player> confirm",
            OnAdminCommand,
            "controlserver");
#pragma warning restore CS0618
    }

    private void OnPlayerCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsStorageReady)
        {
            Reply(player, groupId, "Discord account storage could not be read. Linking is paused to preserve existing accounts; ask an administrator to check the server log.");
            return;
        }
        var cfg = getConfig();
        if (!cfg.Enabled)
        {
            Reply(player, groupId, "Discord account linking is currently disabled.");
            return;
        }

        var op = (args.PopWord() ?? "new").Trim().ToLowerInvariant();
        switch (op)
        {
            case "status":
                SendPlayerStatus(player, groupId);
                break;

            case "new":
            case "code":
            case "link":
                CreateCode(player, groupId);
                break;

            case "cancel":
                Reply(player, groupId, codes.Cancel(player.PlayerUID)
                    ? "Your pending Discord link code was cancelled."
                    : "You do not have a pending Discord link code.");
                break;

            case "unlink":
                if (!string.Equals(args.PopWord(), "confirm", StringComparison.OrdinalIgnoreCase))
                {
                    Reply(player, groupId, "This removes your Discord association. Use /discordlink unlink confirm to continue.");
                    return;
                }
                UnlinkPlayer(player.PlayerUID, player.PlayerName, "game", player, groupId);
                break;

            case "help":
            default:
                Reply(player, groupId, "Usage: /discordlink  |  /discordlink status  |  /discordlink cancel  |  /discordlink unlink confirm");
                break;
        }
    }

    private void CreateCode(IServerPlayer player, int groupId)
    {
        var existing = store.GetByPlayerUid(player.PlayerUID);
        if (existing != null)
        {
            Reply(player, groupId, $"Already linked to Discord user {DisplayDiscord(existing)}. Use /discordlink unlink confirm before linking a different Discord account.");
            return;
        }

        if (!codes.TryCreate(player.PlayerUID, player.PlayerName, out var code, out var error))
        {
            Reply(player, groupId, error);
            return;
        }

        var expiry = Math.Clamp(getConfig().CodeExpirySeconds, 60, 3600);
        Reply(player, groupId, $"Discord link code: {code}");
        Reply(player, groupId, $"In Discord, run /link and enter that code. It expires in {expiry / 60d:0.#} minute(s). Do not share it.");
    }

    private void SendPlayerStatus(IServerPlayer player, int groupId)
    {
        var record = store.GetByPlayerUid(player.PlayerUID);
        if (record == null)
        {
            Reply(player, groupId, "Your Vintage Story account is not linked to Discord. Use /discordlink to generate a code.");
            return;
        }
        Reply(player, groupId, $"Linked to Discord user {DisplayDiscord(record)} since {FormatUtc(record.LinkedAtUnixMilliseconds)}.");
    }

    private void OnAdminCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsStorageReady)
        {
            Reply(player, groupId, "Discord account storage is unavailable. Existing save data is protected from writes; repair the data and restart the server.");
            return;
        }
        var cfg = getConfig();
        var op = (args.PopWord() ?? "stats").Trim().ToLowerInvariant();
        switch (op)
        {
            case "stats":
                Reply(player, groupId, $"Discord account linking: {LinkedAccountCount} persistent link(s), {PendingCodeCount} pending code(s), enabled={cfg.Enabled}.");
                return;

            case "status":
            {
                var targetName = args.PopWord() ?? "";
                var target = FindPlayerData(targetName);
                if (target == null) { Reply(player, groupId, "Player not found in server player data."); return; }
                var record = store.GetByPlayerUid(target.PlayerUID);
                Reply(player, groupId, record == null
                    ? $"{target.LastKnownPlayername} is not linked to Discord."
                    : $"{target.LastKnownPlayername} is linked to {DisplayDiscord(record)} ({record.DiscordUserId}) since {FormatUtc(record.LinkedAtUnixMilliseconds)}.");
                return;
            }

            case "discord":
            {
                var discordId = args.PopWord() ?? "";
                if (string.IsNullOrWhiteSpace(discordId)) { Reply(player, groupId, "Usage: /discordlinkadmin discord <discordUserId>"); return; }
                var records = store.GetByDiscordUserId(discordId);
                Reply(player, groupId, records.Count == 0
                    ? "That Discord user ID has no linked Vintage Story account."
                    : "Linked Vintage Story account(s): " + string.Join(", ", records.Select(x => $"{x.PlayerName} [{x.PlayerUid}]")));
                return;
            }

            case "unlink":
            {
                var targetName = args.PopWord() ?? "";
                var confirm = args.PopWord() ?? "";
                if (!confirm.Equals("confirm", StringComparison.OrdinalIgnoreCase))
                {
                    Reply(player, groupId, "Usage: /discordlinkadmin unlink <player> confirm");
                    return;
                }
                var target = FindPlayerData(targetName);
                if (target == null) { Reply(player, groupId, "Player not found in server player data."); return; }
                var removed = store.RemoveByPlayerUid(target.PlayerUID);
                if (removed == null) { Reply(player, groupId, $"{target.LastKnownPlayername} was not linked."); return; }
                EmitChanged("unlink", removed, "admin");
                LogChange($"Admin {player.PlayerName} unlinked VS player {removed.PlayerName} ({removed.PlayerUid}) from Discord {removed.DiscordUserId}.");
                Reply(player, groupId, $"Unlinked {target.LastKnownPlayername} from Discord.");
                return;
            }

            default:
                Reply(player, groupId, "Usage: /discordlinkadmin stats | status <player> | discord <discordUserId> | unlink <player> confirm");
                return;
        }
    }

    private IServerPlayerData? FindPlayerData(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return sapi.PlayerData.GetPlayerDataByLastKnownName(name);
    }

    /// <summary>
    /// Handles /link, /linkstatus and /unlink from the Discord slash-command service.
    /// Must be called on the Vintage Story main thread because it can access world/player state.
    /// </summary>
    public DiscordLinkOperationResult HandleDiscordOperation(string operation, string discordUserId, string discordUserName, string code = "")
    {
        if (!IsStorageReady)
            return new(false, "Discord account storage is unavailable. Linking is paused to preserve existing accounts; contact a server administrator.");
        var cfg = getConfig();
        if (!cfg.Enabled)
            return new(false, "Discord account linking is disabled on the game server.");

        if (string.IsNullOrWhiteSpace(discordUserId))
            return new(false, "Discord user information was not supplied by the Discord bot.");

        return operation.Trim().ToLowerInvariant() switch
        {
            "link" => HandleDiscordLink(discordUserId.Trim(), discordUserName.Trim(), code),
            "status" => HandleDiscordStatus(discordUserId.Trim()),
            "unlink" => HandleDiscordUnlink(discordUserId.Trim()),
            _ => new DiscordLinkOperationResult(false, "Unknown Discord link operation.")
        };
    }

    private DiscordLinkOperationResult HandleDiscordLink(string discordUserId, string discordUserName, string code)
    {
        if (!codes.TryValidate(code, discordUserId, out var pending, out var error) || pending == null)
            return new(false, error);

        var existingForPlayer = store.GetByPlayerUid(pending.PlayerUid);
        if (existingForPlayer != null)
        {
            if (existingForPlayer.DiscordUserId.Equals(discordUserId, StringComparison.Ordinal))
            {
                codes.Consume(pending.PlayerUid);
                return new(true, $"Your Discord account is already linked to Vintage Story player **{EscapeDiscord(existingForPlayer.PlayerName)}**.", true, existingForPlayer.PlayerUid, existingForPlayer.PlayerName);
            }
            return new(false, "That Vintage Story account is already linked to a different Discord account. Unlink it in-game first.");
        }

        var existingForDiscord = store.GetByDiscordUserId(discordUserId);
        if (!getConfig().AllowOneDiscordAccountToLinkMultiplePlayers && existingForDiscord.Count > 0)
            return new(false, $"Your Discord account is already linked to Vintage Story player **{EscapeDiscord(existingForDiscord[0].PlayerName)}**. Use /unlink in Discord first if you want to change accounts.");

        var record = new DiscordLinkRecord
        {
            PlayerUid = pending.PlayerUid,
            PlayerName = pending.PlayerName,
            DiscordUserId = discordUserId,
            DiscordUserName = discordUserName,
            LinkedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        store.Upsert(record);
        // A failed persistent write leaves the valid one-time code available for retry.
        codes.Consume(pending.PlayerUid);
        EmitChanged("link", record, "discord");
        LogChange($"Linked VS player {record.PlayerName} ({record.PlayerUid}) to Discord {record.DiscordUserName} ({record.DiscordUserId}).");
        NotifyOnlinePlayer(record.PlayerUid, $"Discord link successful. Your account is now linked to {DisplayDiscord(record)}.");
        return new(true, $"Linked Discord account to Vintage Story player **{EscapeDiscord(record.PlayerName)}** successfully.", true, record.PlayerUid, record.PlayerName);
    }

    private DiscordLinkOperationResult HandleDiscordStatus(string discordUserId)
    {
        var records = store.GetByDiscordUserId(discordUserId);
        if (records.Count == 0)
            return new(true, "Your Discord account is not linked to a Vintage Story account. Generate a code in-game with `/discordlink`, then use `/link` here.", false);

        var names = string.Join(", ", records.Select(x => $"**{EscapeDiscord(x.PlayerName)}**"));
        return new(true, $"Linked Vintage Story account(s): {names}.", true, records[0].PlayerUid, records[0].PlayerName);
    }

    private DiscordLinkOperationResult HandleDiscordUnlink(string discordUserId)
    {
        var removed = store.RemoveByDiscordUserId(discordUserId);
        if (removed.Count == 0)
            return new(true, "Your Discord account is not currently linked to a Vintage Story account.", false);

        foreach (var record in removed)
        {
            EmitChanged("unlink", record, "discord");
            LogChange($"Discord user {record.DiscordUserName} ({record.DiscordUserId}) unlinked VS player {record.PlayerName} ({record.PlayerUid}).");
            NotifyOnlinePlayer(record.PlayerUid, "Your Discord account link was removed from Discord.");
        }

        return new(true, removed.Count == 1
            ? $"Unlinked Discord from Vintage Story player **{EscapeDiscord(removed[0].PlayerName)}**."
            : $"Unlinked Discord from {removed.Count} Vintage Story accounts.", false);
    }

    // Compatibility contract for older Discord Integration builds. The unified module no longer uses this internally.
    private void OnLegacyDiscordRequest(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active) return;
        if (data is not ITreeAttribute tree) return;
        tree.SetBool("handled", true);
        var result = HandleDiscordOperation(
            tree.GetString("operation", "link"),
            tree.GetString("discordUserId", ""),
            tree.GetString("discordUserName", ""),
            tree.GetString("code", ""));
        tree.SetBool("success", result.Success);
        tree.SetString("response", result.Response);
        tree.SetBool("linked", result.Linked);
        if (!string.IsNullOrWhiteSpace(result.PlayerUid)) tree.SetString("playerUid", result.PlayerUid);
        if (!string.IsNullOrWhiteSpace(result.PlayerName)) tree.SetString("playerName", result.PlayerName);
    }

    public void PopulateControl(ITreeAttribute tree)
    {
        var cfg = getConfig();
        tree.SetBool("linkAvailable", true);
        tree.SetBool("linkStorageReady", IsStorageReady);
        tree.SetBool("linkEnabled", cfg.Enabled);
        tree.SetInt("linkCodeLength", cfg.CodeLength);
        tree.SetInt("linkCodeExpirySeconds", cfg.CodeExpirySeconds);
        tree.SetInt("linkCodeCooldownSeconds", cfg.CodeCooldownSeconds);
        tree.SetBool("linkAllowMultipleVs", cfg.AllowOneDiscordAccountToLinkMultiplePlayers);
        tree.SetBool("linkLogChanges", cfg.LogLinkChanges);
        tree.SetBool("linkRoleSyncEnabled", cfg.RoleSync?.Enabled == true);
        tree.SetInt("linkRoleSyncIntervalSeconds", cfg.RoleSync == null ? 60 : Math.Clamp(cfg.RoleSync.SyncIntervalSeconds, 30, 3600));
        tree.SetInt("linkRoleSyncMappingCount", cfg.RoleSync?.Mappings?.Count(x => x.Enabled && x.DiscordRoleId != 0 && !string.IsNullOrWhiteSpace(x.GameRoleCode)) ?? 0);
        tree.SetInt("linkedAccountCount", LinkedAccountCount);
        tree.SetInt("pendingLinkCodeCount", PendingCodeCount);
    }

    public bool TryApplySetting(string key, ITreeAttribute tree, out string message)
    {
        var cfg = getConfig();
        switch (key.Trim().ToLowerInvariant())
        {
            case "link.enabled":
                cfg.Enabled = tree.GetBool("boolValue", cfg.Enabled);
                break;
            case "link.codelength":
                cfg.CodeLength = Math.Clamp(tree.GetInt("intValue", cfg.CodeLength), 6, 12);
                break;
            case "link.codeexpiryseconds":
                cfg.CodeExpirySeconds = Math.Clamp(tree.GetInt("intValue", cfg.CodeExpirySeconds), 60, 3600);
                break;
            case "link.codecooldownseconds":
                cfg.CodeCooldownSeconds = Math.Clamp(tree.GetInt("intValue", cfg.CodeCooldownSeconds), 0, 600);
                break;
            case "link.allowmultiplevs":
                cfg.AllowOneDiscordAccountToLinkMultiplePlayers = tree.GetBool("boolValue", cfg.AllowOneDiscordAccountToLinkMultiplePlayers);
                break;
            case "link.logchanges":
                cfg.LogLinkChanges = tree.GetBool("boolValue", cfg.LogLinkChanges);
                break;
            case "link.rolesync.enabled":
                cfg.RoleSync ??= new DiscordRoleSyncConfig();
                cfg.RoleSync.Enabled = tree.GetBool("boolValue", cfg.RoleSync.Enabled);
                break;
            case "link.rolesync.intervalseconds":
                cfg.RoleSync ??= new DiscordRoleSyncConfig();
                cfg.RoleSync.SyncIntervalSeconds = Math.Clamp(tree.GetInt("intValue", cfg.RoleSync.SyncIntervalSeconds), 30, 3600);
                break;
            default:
                message = $"Unknown Discord account-linking setting '{key}'.";
                return false;
        }

        message = "Discord account-linking setting saved.";
        return true;
    }

    private void OnQuery(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active) return;
        if (data is not ITreeAttribute tree) return;
        tree.SetBool("handled", true);

        var playerUid = tree.GetString("playerUid", "").Trim();
        var discordUserId = tree.GetString("discordUserId", "").Trim();
        DiscordLinkRecord? record = null;

        if (!string.IsNullOrWhiteSpace(playerUid)) record = store.GetByPlayerUid(playerUid);
        else if (!string.IsNullOrWhiteSpace(discordUserId)) record = store.GetByDiscordUserId(discordUserId).FirstOrDefault();

        tree.SetBool("linked", record != null);
        if (record == null) return;
        tree.SetString("playerUid", record.PlayerUid);
        tree.SetString("playerName", record.PlayerName);
        tree.SetString("discordUserId", record.DiscordUserId);
        tree.SetString("discordUserName", record.DiscordUserName);
        tree.SetLong("linkedAtUnixMilliseconds", record.LinkedAtUnixMilliseconds);
    }

    private void UnlinkPlayer(string playerUid, string playerName, string source, IServerPlayer player, int groupId)
    {
        var removed = store.RemoveByPlayerUid(playerUid);
        if (removed == null)
        {
            Reply(player, groupId, "Your Vintage Story account is not linked to Discord.");
            return;
        }
        EmitChanged("unlink", removed, source);
        LogChange($"VS player {playerName} ({playerUid}) unlinked Discord {removed.DiscordUserId} from {source}.");
        Reply(player, groupId, "Your Discord association has been removed.");
    }

    private void EmitChanged(string action, DiscordLinkRecord record, string source)
    {
        var evt = new TreeAttribute();
        evt.SetString("action", action);
        evt.SetString("source", source);
        evt.SetString("playerUid", record.PlayerUid);
        evt.SetString("playerName", record.PlayerName);
        evt.SetString("discordUserId", record.DiscordUserId);
        evt.SetString("discordUserName", record.DiscordUserName);
        evt.SetLong("linkedAtUnixMilliseconds", record.LinkedAtUnixMilliseconds);
        sapi.Event.PushEvent(ChangedEvent, evt);
    }

    private void NotifyOnlinePlayer(string playerUid, string message)
    {
        if (sapi.World.PlayerByUid(playerUid) is IServerPlayer player && player.ConnectionState == EnumClientState.Playing)
            player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
    }

    private void LogChange(string message)
    {
        if (getConfig().LogLinkChanges) sapi.Logger.Notification("[NSE Discord] {0}", message);
    }

    private static string DisplayDiscord(DiscordLinkRecord record) => string.IsNullOrWhiteSpace(record.DiscordUserName)
        ? record.DiscordUserId
        : $"{record.DiscordUserName} ({record.DiscordUserId})";

    private static string FormatUtc(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'");
    private static string EscapeDiscord(string value) => value.Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`");

    private static void Reply(IServerPlayer player, int groupId, string message) =>
        player.SendMessage(groupId, message, EnumChatType.Notification);
}

public sealed record DiscordLinkOperationResult(
    bool Success,
    string Response,
    bool Linked = false,
    string PlayerUid = "",
    string PlayerName = "");
