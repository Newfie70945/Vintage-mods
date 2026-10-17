using NewfiesServerEssentials.Discord.Linking.Models;
using NewfiesServerEssentials.Discord.Linking.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace NewfiesServerEssentials.Discord.Linking;

/// <summary>
/// Applies explicitly configured Discord-role -> Vintage Story role mappings for linked,
/// online players. Ownership is tracked persistently so automatic synchronization can
/// restore only roles it assigned itself and can stand down permanently when an admin
/// changes a player's role manually.
/// </summary>
public sealed class DiscordRoleSyncService
{
    private const int SchedulerTickMs = 10_000;

    private readonly ICoreServerAPI sapi;
    private readonly Func<DiscordRoleSyncConfig> getConfig;
    private readonly DiscordLinkService linkService;
    private readonly DiscordRoleSyncStore store;
    private readonly HashSet<string> invalidRoleWarnings = new(StringComparer.OrdinalIgnoreCase);

    private System.Func<string, string, Task>? requestSyncAsync;
    private long schedulerListenerId = -1;
    private long nextPeriodicSyncAtMs;
    private bool active;

    public DiscordRoleSyncService(
        ICoreServerAPI sapi,
        Func<DiscordRoleSyncConfig> getConfig,
        DiscordLinkService linkService)
    {
        this.sapi = sapi;
        this.getConfig = getConfig;
        this.linkService = linkService;
        store = new DiscordRoleSyncStore(sapi);
        store.Load();
    }

    public int ManagedPlayerCount => store.GetAll().Count(x => !x.SuspendedByManualOverride);
    public int SuspendedPlayerCount => store.GetAll().Count(x => x.SuspendedByManualOverride);
    public bool IsStorageReady => store.IsStorageReady && linkService.IsStorageReady;

    public void SetSyncRequester(System.Func<string, string, Task> requester)
    {
        requestSyncAsync = requester;
    }

    public void Register()
    {
        active = true;
        nextPeriodicSyncAtMs = Environment.TickCount64 + 15_000;
        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        sapi.Event.RegisterEventBusListener(OnLinkChanged, filterByEventName: DiscordLinkService.ChangedEvent);
        schedulerListenerId = sapi.Event.RegisterGameTickListener(_ => SchedulerTick(), SchedulerTickMs, 5_000);
        RegisterCommands();
    }

    public void Unregister()
    {
        active = false;
        sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
        if (schedulerListenerId >= 0)
        {
            try { sapi.Event.UnregisterGameTickListener(schedulerListenerId); } catch { }
            schedulerListenerId = -1;
        }
    }

    /// <summary>
    /// Called by the Discord transport after it has fetched the member's current guild-role IDs.
    /// This method must execute on Vintage Story's main thread.
    /// </summary>
    public void ReconcileFromDiscord(
        string playerUid,
        string discordUserId,
        IReadOnlyCollection<ulong> discordRoleIds,
        string source)
    {
        if (!active || !IsStorageReady) return;
        var cfg = getConfig();
        if (!cfg.Enabled) return;

        var player = FindLivePlayer(playerUid);
        if (player == null) return;

        var link = linkService.GetByPlayerUid(playerUid);
        if (link == null || !string.Equals(link.DiscordUserId, discordUserId, StringComparison.Ordinal))
        {
            RestoreIfNoLongerLinked(player, source);
            return;
        }

        string currentRole = CurrentRoleCode(player);
        if (string.IsNullOrWhiteSpace(currentRole)) return;

        var state = store.Get(playerUid);
        if (state != null && state.SuspendedByManualOverride)
        {
            // Manual override is intentionally sticky. Discord cannot regain ownership until
            // an administrator explicitly runs /discordrolesync resume <player>.
            return;
        }

        if (state != null &&
            !string.IsNullOrWhiteSpace(state.ManagedRoleCode) &&
            !currentRole.Equals(state.ManagedRoleCode, StringComparison.OrdinalIgnoreCase))
        {
            state.SuspendedByManualOverride = true;
            state.ManualRoleCode = currentRole;
            state.PlayerName = player.PlayerName;
            state.LastCheckedUnixMilliseconds = NowUnixMs();
            store.Upsert(state);
            LogChange($"Manual override detected for {player.PlayerName}: Discord previously managed '{state.ManagedRoleCode}', current role is '{currentRole}'. Automatic role sync is suspended for this player.");
            return;
        }

        var target = ResolveTargetMapping(cfg, discordRoleIds);
        if (target == null)
        {
            if (state != null) RestoreManagedRole(player, state, source);
            return;
        }

        string targetRole = (target.GameRoleCode ?? "").Trim();
        if (!RoleExists(targetRole))
        {
            WarnInvalidGameRole(targetRole, target.DiscordRoleId);
            return;
        }

        if (state == null)
        {
            // If an administrator had already assigned the exact target role before Discord
            // sync ever touched this player, treat it as manual. Never claim ownership merely
            // because the current value happens to match a mapping.
            if (currentRole.Equals(targetRole, StringComparison.OrdinalIgnoreCase)) return;

            if (!IsEligibleBaseRole(cfg, currentRole))
            {
                // Conservative first-ownership rule: custom/admin/supporter roles outside the
                // explicitly allowed base list are presumed manual and left untouched.
                return;
            }

            if (!TrySetRole(player, targetRole, out string error))
            {
                sapi.Logger.Warning("[NSE Discord] Role sync could not assign '{0}' to {1}: {2}", targetRole, player.PlayerName, error);
                return;
            }

            state = new DiscordRoleSyncState
            {
                PlayerUid = player.PlayerUID,
                PlayerName = player.PlayerName,
                DiscordUserId = discordUserId,
                PreviousRoleCode = currentRole,
                ManagedRoleCode = targetRole,
                DiscordRoleId = target.DiscordRoleId,
                LastCheckedUnixMilliseconds = NowUnixMs(),
                LastAppliedUnixMilliseconds = NowUnixMs()
            };
            store.Upsert(state);
            NotifyPlayer(player, cfg, $"Your in-game role is now '{targetRole}' from your linked Discord role.");
            LogChange($"Assigned {player.PlayerName} role '{targetRole}' from Discord role {target.DiscordRoleId}; previous role '{currentRole}' is preserved for safe restore.");
            return;
        }

        // Existing managed assignment. If the player's highest-priority mapped Discord role
        // changes, Discord may move only between roles it already owns for this player.
        if (currentRole.Equals(targetRole, StringComparison.OrdinalIgnoreCase))
        {
            if (state.DiscordRoleId != target.DiscordRoleId || !string.Equals(state.DiscordUserId, discordUserId, StringComparison.Ordinal))
            {
                state.DiscordRoleId = target.DiscordRoleId;
                state.DiscordUserId = discordUserId;
                state.PlayerName = player.PlayerName;
                state.LastCheckedUnixMilliseconds = NowUnixMs();
                store.Upsert(state);
            }
            return;
        }

        if (!TrySetRole(player, targetRole, out string changeError))
        {
            sapi.Logger.Warning("[NSE Discord] Role sync could not change {0} from '{1}' to '{2}': {3}", player.PlayerName, currentRole, targetRole, changeError);
            return;
        }

        string oldManaged = state.ManagedRoleCode;
        state.ManagedRoleCode = targetRole;
        state.DiscordRoleId = target.DiscordRoleId;
        state.DiscordUserId = discordUserId;
        state.PlayerName = player.PlayerName;
        state.LastCheckedUnixMilliseconds = NowUnixMs();
        state.LastAppliedUnixMilliseconds = NowUnixMs();
        store.Upsert(state);
        NotifyPlayer(player, cfg, $"Your linked Discord roles changed your in-game role to '{targetRole}'.");
        LogChange($"Changed Discord-managed role for {player.PlayerName}: '{oldManaged}' -> '{targetRole}'. Original pre-sync role remains '{state.PreviousRoleCode}'.");
    }

    /// <summary>Queues a role check for every linked online player. Safe to call from Ready.</summary>
    public void RequestSyncForOnline(string source)
    {
        if (!active || !IsStorageReady || !getConfig().Enabled || requestSyncAsync == null) return;

        foreach (var player in LivePlayers())
        {
            if (linkService.GetByPlayerUid(player.PlayerUID) == null)
            {
                if (store.Get(player.PlayerUID) != null) RestoreIfNoLongerLinked(player, source);
                continue;
            }
            QueueSync(player.PlayerUID, source);
        }
    }

    private void SchedulerTick()
    {
        if (!active) return;
        var cfg = getConfig();
        if (!cfg.Enabled) return;

        long now = Environment.TickCount64;
        if (now < nextPeriodicSyncAtMs) return;
        int intervalSeconds = Math.Clamp(cfg.SyncIntervalSeconds, 30, 3600);
        nextPeriodicSyncAtMs = now + intervalSeconds * 1000L;
        RequestSyncForOnline("periodic");
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (!active || !IsStorageReady || !getConfig().Enabled || player == null) return;
        if (linkService.GetByPlayerUid(player.PlayerUID) == null)
        {
            RestoreIfNoLongerLinked(player, "player-join");
            return;
        }
        QueueSync(player.PlayerUID, "player-join");
    }

    private void OnLinkChanged(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!active || !IsStorageReady || data is not ITreeAttribute tree) return;
        string uid = tree.GetString("playerUid", "").Trim();
        if (string.IsNullOrWhiteSpace(uid)) return;

        string action = tree.GetString("action", "").Trim();
        if (action.Equals("unlink", StringComparison.OrdinalIgnoreCase))
        {
            var player = FindLivePlayer(uid);
            if (player != null) RestoreIfNoLongerLinked(player, "unlink");
            return;
        }

        if (action.Equals("link", StringComparison.OrdinalIgnoreCase) && getConfig().Enabled)
            QueueSync(uid, "account-link");
    }

    private void RestoreIfNoLongerLinked(IServerPlayer player, string source)
    {
        if (!IsStorageReady) return;
        var state = store.Get(player.PlayerUID);
        if (state == null) return;

        // A manual override belongs to the administrator, not to Discord. Once the account is
        // unlinked, simply forget our historical ownership record and leave that manual role.
        if (state.SuspendedByManualOverride)
        {
            store.Remove(player.PlayerUID);
            return;
        }

        string currentRole = CurrentRoleCode(player);
        if (!currentRole.Equals(state.ManagedRoleCode, StringComparison.OrdinalIgnoreCase))
        {
            // This can happen if a manual role change occurred just before unlink. Never restore
            // over it; forget ownership instead.
            store.Remove(player.PlayerUID);
            return;
        }

        RestoreManagedRole(player, state, source);
    }

    private void RestoreManagedRole(IServerPlayer player, DiscordRoleSyncState state, string source)
    {
        string currentRole = CurrentRoleCode(player);
        if (!currentRole.Equals(state.ManagedRoleCode, StringComparison.OrdinalIgnoreCase))
        {
            state.SuspendedByManualOverride = true;
            state.ManualRoleCode = currentRole;
            state.PlayerName = player.PlayerName;
            store.Upsert(state);
            LogChange($"Did not restore Discord-managed role for {player.PlayerName}: current role '{currentRole}' differs from managed role '{state.ManagedRoleCode}', so it is treated as a manual override.");
            return;
        }

        string restoreRole = (state.PreviousRoleCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(restoreRole) || !RoleExists(restoreRole))
        {
            sapi.Logger.Warning("[NSE Discord] Cannot safely restore {0}: previous role '{1}' no longer exists. Leaving current role '{2}' unchanged.", player.PlayerName, restoreRole, currentRole);
            state.SuspendedByManualOverride = true;
            state.ManualRoleCode = currentRole;
            store.Upsert(state);
            return;
        }

        if (!TrySetRole(player, restoreRole, out string error))
        {
            sapi.Logger.Warning("[NSE Discord] Could not restore previous role '{0}' for {1}: {2}", restoreRole, player.PlayerName, error);
            return;
        }

        store.Remove(player.PlayerUID);
        var cfg = getConfig();
        NotifyPlayer(player, cfg, $"Your Discord-managed in-game role was removed; your previous role '{restoreRole}' was restored.");
        LogChange($"Restored {player.PlayerName} from Discord-managed role '{state.ManagedRoleCode}' to previous role '{restoreRole}' ({source}).");
    }

    private DiscordRoleMapping? ResolveTargetMapping(DiscordRoleSyncConfig cfg, IReadOnlyCollection<ulong> roleIds)
    {
        if (roleIds == null || roleIds.Count == 0 || cfg.Mappings == null || cfg.Mappings.Count == 0) return null;
        var present = roleIds as HashSet<ulong> ?? new HashSet<ulong>(roleIds);

        DiscordRoleMapping? best = null;
        int bestPriority = int.MinValue;
        for (int i = 0; i < cfg.Mappings.Count; i++)
        {
            var mapping = cfg.Mappings[i];
            if (mapping == null || !mapping.Enabled || mapping.DiscordRoleId == 0 || string.IsNullOrWhiteSpace(mapping.GameRoleCode)) continue;
            if (!present.Contains(mapping.DiscordRoleId)) continue;

            if (best == null || mapping.Priority > bestPriority)
            {
                best = mapping;
                bestPriority = mapping.Priority;
            }
        }
        return best;
    }

    private bool IsEligibleBaseRole(DiscordRoleSyncConfig cfg, string roleCode)
    {
        var roles = cfg.EligibleBaseRoleCodes ?? Array.Empty<string>();
        return roles.Any(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Equals(roleCode, StringComparison.OrdinalIgnoreCase));
    }

    private bool RoleExists(string roleCode)
    {
        if (string.IsNullOrWhiteSpace(roleCode)) return false;
        try
        {
            return (sapi.Server.Config.Roles ?? new List<IPlayerRole>())
                .Any(r => r != null && string.Equals(r.Code, roleCode, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private void WarnInvalidGameRole(string roleCode, ulong discordRoleId)
    {
        string key = $"{discordRoleId}:{roleCode}";
        if (!invalidRoleWarnings.Add(key)) return;
        sapi.Logger.Warning("[NSE Discord] Discord role-sync mapping {0} targets unknown Vintage Story role code '{1}'. The mapping is ignored until corrected.", discordRoleId, roleCode);
    }

    private static string CurrentRoleCode(IServerPlayer player) =>
        (player.ServerData?.RoleCode ?? player.Role?.Code ?? "").Trim();

    private bool TrySetRole(IServerPlayer player, string roleCode, out string error)
    {
        error = "";
        try
        {
            player.SetRole(roleCode);
            player.BroadcastPlayerData();
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private IServerPlayer? FindLivePlayer(string playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return null;
        try
        {
            var player = (sapi.Server.Players ?? Array.Empty<IServerPlayer>())
                .FirstOrDefault(p => p != null && p.PlayerUID == playerUid && IsLive(p));
            if (player != null) return player;
        }
        catch { }

        try
        {
            return sapi.World.PlayerByUid(playerUid) is IServerPlayer p && IsLive(p) ? p : null;
        }
        catch { return null; }
    }

    private IEnumerable<IServerPlayer> LivePlayers()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IServerPlayer[] players;
        try { players = sapi.Server.Players ?? Array.Empty<IServerPlayer>(); }
        catch { players = Array.Empty<IServerPlayer>(); }

        foreach (var player in players)
        {
            if (player == null || !IsLive(player) || string.IsNullOrWhiteSpace(player.PlayerUID) || !seen.Add(player.PlayerUID)) continue;
            yield return player;
        }
    }

    private static bool IsLive(IServerPlayer player) =>
        player.ConnectionState == EnumClientState.Connected || player.ConnectionState == EnumClientState.Playing;

    private void QueueSync(string playerUid, string source)
    {
        var requester = requestSyncAsync;
        if (requester == null || string.IsNullOrWhiteSpace(playerUid)) return;
        _ = SafeRequestAsync(requester, playerUid, source);
    }

    private async Task SafeRequestAsync(System.Func<string, string, Task> requester, string playerUid, string source)
    {
        try { await requester(playerUid, source); }
        catch (Exception e) { sapi.Logger.Warning("[NSE Discord] Role sync request failed for player UID {0}: {1}", playerUid, e.Message); }
    }

    private void NotifyPlayer(IServerPlayer player, DiscordRoleSyncConfig cfg, string message)
    {
        if (!cfg.NotifyPlayers) return;
        try { player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification); } catch { }
    }

    private void LogChange(string message)
    {
        if (getConfig().LogChanges) sapi.Logger.Notification("[NSE Discord] Role Sync: {0}", message);
    }

    private void RegisterCommands()
    {
#pragma warning disable CS0618
        sapi.RegisterCommand(
            "discordrolesync",
            "Admin tools for Discord -> Vintage Story role synchronization.",
            "status [player] | sync <player> | resume <player> | release <player>",
            OnAdminCommand,
            "controlserver");
#pragma warning restore CS0618
    }

    private void OnAdminCommand(IServerPlayer caller, int groupId, CmdArgs args)
    {
        if (!IsStorageReady)
        {
            Reply(caller, groupId, "Discord role ownership storage is unavailable. Role changes are paused to preserve the existing roles; repair the data and restart the server.");
            return;
        }
        string op = (args.PopWord() ?? "status").Trim().ToLowerInvariant();
        switch (op)
        {
            case "status":
            {
                string name = args.PopWord() ?? "";
                if (string.IsNullOrWhiteSpace(name))
                {
                    var cfg = getConfig();
                    Reply(caller, groupId,
                        $"Discord role sync: enabled={cfg.Enabled}; mappings={cfg.Mappings?.Count(x => x.Enabled && x.DiscordRoleId != 0 && !string.IsNullOrWhiteSpace(x.GameRoleCode)) ?? 0}; managed={ManagedPlayerCount}; manual overrides={SuspendedPlayerCount}; interval={Math.Clamp(cfg.SyncIntervalSeconds, 30, 3600)}s.");
                    return;
                }

                var data = sapi.PlayerData.GetPlayerDataByLastKnownName(name);
                if (data == null) { Reply(caller, groupId, "Player not found in server player data."); return; }
                var state = store.Get(data.PlayerUID);
                var link = linkService.GetByPlayerUid(data.PlayerUID);
                string current = FindLivePlayer(data.PlayerUID) is IServerPlayer live ? CurrentRoleCode(live) : data.RoleCode;
                if (state == null)
                {
                    Reply(caller, groupId, $"{data.LastKnownPlayername}: current role='{current}', linked={(link != null)}, Discord role sync has not claimed this player's role.");
                    return;
                }

                Reply(caller, groupId,
                    $"{data.LastKnownPlayername}: current='{current}', managed='{state.ManagedRoleCode}', previous='{state.PreviousRoleCode}', DiscordRoleId={state.DiscordRoleId}, suspended={state.SuspendedByManualOverride}, manual='{state.ManualRoleCode}'.");
                return;
            }

            case "sync":
            {
                string name = args.PopWord() ?? "";
                var data = string.IsNullOrWhiteSpace(name) ? null : sapi.PlayerData.GetPlayerDataByLastKnownName(name);
                if (data == null) { Reply(caller, groupId, "Usage: /discordrolesync sync <player>"); return; }
                if (linkService.GetByPlayerUid(data.PlayerUID) == null) { Reply(caller, groupId, "That player is not linked to Discord."); return; }
                QueueSync(data.PlayerUID, "admin-force");
                Reply(caller, groupId, $"Queued Discord role sync for {data.LastKnownPlayername}.");
                return;
            }

            case "resume":
            {
                string name = args.PopWord() ?? "";
                var data = string.IsNullOrWhiteSpace(name) ? null : sapi.PlayerData.GetPlayerDataByLastKnownName(name);
                if (data == null) { Reply(caller, groupId, "Usage: /discordrolesync resume <player>"); return; }
                var state = store.Get(data.PlayerUID);
                if (state == null || !state.SuspendedByManualOverride)
                {
                    Reply(caller, groupId, $"{data.LastKnownPlayername} does not have a suspended manual override.");
                    return;
                }

                // Removing the ownership record is intentionally safer than trying to reuse its
                // old baseline. The next sync may take ownership only if the player's CURRENT
                // role is an explicitly eligible base role.
                store.Remove(data.PlayerUID);
                QueueSync(data.PlayerUID, "admin-resume");
                Reply(caller, groupId, $"Cleared the manual-override suspension for {data.LastKnownPlayername}. Sync will resume only if their current role is eligible for automatic management.");
                return;
            }

            case "release":
            {
                string name = args.PopWord() ?? "";
                var data = string.IsNullOrWhiteSpace(name) ? null : sapi.PlayerData.GetPlayerDataByLastKnownName(name);
                if (data == null) { Reply(caller, groupId, "Usage: /discordrolesync release <player>"); return; }
                var state = store.Get(data.PlayerUID);
                if (state == null)
                {
                    Reply(caller, groupId, $"{data.LastKnownPlayername} is not currently managed by Discord role sync.");
                    return;
                }

                // Explicitly relinquish Discord ownership without changing the live role. This is
                // useful when an administrator wants the current role to become manual even if it
                // happens to be identical to the role Discord previously assigned. Because a new
                // unmanaged state whose current role already equals the mapped target is never
                // claimed automatically, the role remains manual until an admin changes it back
                // to an eligible base role (or otherwise deliberately resumes management).
                store.Remove(data.PlayerUID);
                Reply(caller, groupId, $"Released Discord ownership of {data.LastKnownPlayername}'s role. Their current role was left unchanged and is now treated as manual.");
                LogChange($"Administrator released Discord role ownership for {data.LastKnownPlayername}; current role left unchanged.");
                return;
            }

            default:
                Reply(caller, groupId, "Usage: /discordrolesync status [player] | sync <player> | resume <player> | release <player>");
                return;
        }
    }

    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static void Reply(IServerPlayer player, int groupId, string message) =>
        player.SendMessage(groupId, message, EnumChatType.Notification);
}
