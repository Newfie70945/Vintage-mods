using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using BlockLogger.AntiCheat;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerCommands;

public sealed class PlayerTeleportPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Dimension { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }

    public static PlayerTeleportPosition From(EntityPos pos) => new()
    {
        X = pos.X,
        Y = pos.Y,
        Z = pos.Z,
        Dimension = pos.Dimension,
        Yaw = pos.Yaw,
        Pitch = pos.Pitch
    };

    public EntityPos ToEntityPos()
    {
        var pos = new EntityPos(X, Y, Z, Yaw, Pitch);
        pos.Dimension = Dimension;
        return pos;
    }
}

public sealed class PlayerHomeDefinition
{
    public string Name { get; set; } = "home";
    public PlayerTeleportPosition Position { get; set; } = new();
    public long UpdatedAtUnixMilliseconds { get; set; }
}

public sealed class PlayerTeleportProfile
{
    public string PlayerUid { get; set; } = "";
    public string LastKnownName { get; set; } = "";
    public PlayerTeleportPosition? Back { get; set; }
    public string BackReason { get; set; } = "";
    public long BackUpdatedAtUnixMilliseconds { get; set; }
    public List<PlayerHomeDefinition> Homes { get; set; } = new();
}

public sealed class PlayerTeleportDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public List<PlayerTeleportProfile> Players { get; set; } = new();
}

/// <summary>
/// Persistent player teleport helpers: /back, homes and safe random teleport.
/// All actual entity/world mutations stay on the Vintage Story server thread.
/// </summary>
public sealed class PlayerTeleportService : IDisposable
{
    private sealed class PendingTeleport
    {
        public string PlayerUid { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Label { get; init; } = "";
        public long DueUtcMs { get; init; }
        public double StartX { get; init; }
        public double StartY { get; init; }
        public double StartZ { get; init; }
        public bool CancelOnMove { get; init; }
        public PlayerTeleportPosition Destination { get; init; } = new();
        public bool CostEnabled { get; init; }
        public string CostItemCode { get; init; } = "";
        public int CostQuantity { get; init; }
        public string CooldownKey { get; init; } = "";
        public int CooldownSeconds { get; init; }
        public bool SwapBackAfterTeleport { get; init; }
    }

    private sealed class PendingConsentTeleportRequest
    {
        public string Kind { get; init; } = "";
        public string RequesterUid { get; init; } = "";
        public string RequesterName { get; init; } = "";
        public string TargetUid { get; init; } = "";
        public string TargetName { get; init; } = "";
        public long ExpiresUtcMs { get; init; }
        public bool Accepted { get; set; }
        public long DueUtcMs { get; set; }
    }

    private const int ConsentRequestTimeoutSeconds = 60;
    private const int ConsentAcceptWarmupSeconds = 10;

    private const string SaveKey = "newfiesserveressentials:player-teleports-v1";
    private readonly ICoreServerAPI api;
    private readonly Func<PlayerCommandConfig> getConfig;
    private readonly System.Func<string, bool>? isJailed;
    private readonly Dictionary<string, PlayerTeleportProfile> profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingTeleport> pendingByUid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingConsentTeleportRequest> consentIncomingByTargetUid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingConsentTeleportRequest> consentOutgoingByRequesterUid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> cooldownUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> rtpSearching = new(StringComparer.OrdinalIgnoreCase);
    private long tickListenerId;
    private bool disposed;
    private bool storageReady;
    private bool dirty;
    private long lastFlushUtcMs;
    private long lastCooldownPruneUtcMs;
    public bool StorageReady => storageReady;
    public string StorageError { get; private set; } = "";

    public PlayerTeleportService(ICoreServerAPI api, Func<PlayerCommandConfig> getConfig, System.Func<string, bool>? isJailed = null)
    {
        this.api = api;
        this.getConfig = getConfig;
        this.isJailed = isJailed;
    }

    public bool HasPending(string playerUid) => !string.IsNullOrWhiteSpace(playerUid) &&
        (pendingByUid.ContainsKey(playerUid) || rtpSearching.Contains(playerUid) || HasAcceptedConsentPlayer(playerUid));

    public void Start()
    {
        Load();
        api.Event.PlayerDeath += OnPlayerDeath;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Event.GameWorldSave += OnWorldSave;
        tickListenerId = api.Event.RegisterGameTickListener(_ => Tick(), 250);
    }

    public void RecordTeleportOrigin(IServerPlayer player, EntityPos? origin, string reason)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || origin == null) return;
        var cfg = getConfig();
        if (!cfg.BackEnabled || !cfg.BackTrackTeleports) return;
        SetBack(player, PlayerTeleportPosition.From(origin), reason);
    }

    public void RecordTeleportOrigin(IServerPlayer player, string reason)
        => RecordTeleportOrigin(player, EntityRuntimeCompat.GetPos(player.Entity)?.Copy(), reason);

    public void HandleSpawn(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (IsJailed(player))
        {
            Reply(player, groupId, "You cannot use /spawn while jailed.", false);
            return;
        }
        if (!TryResolveTeleportPolicy(player, "spawn", cfg.SpawnCooldownSeconds, groupId, out int spawnCooldown))
            return;
        if (!CheckCooldown(player, "spawn", spawnCooldown, out int remaining))
        {
            Reply(player, groupId, $"You can use /spawn again in {remaining} second(s).", false);
            return;
        }
        var spawn = api.World.DefaultSpawnPosition?.Copy();
        if (spawn == null)
        {
            Reply(player, groupId, "The server did not provide a global world spawn position.", false);
            return;
        }
        BeginTeleport(player, groupId, new PendingTeleport
        {
            PlayerUid = player.PlayerUID,
            Kind = "spawn",
            Label = "world spawn",
            Destination = PlayerTeleportPosition.From(spawn),
            CancelOnMove = cfg.SpawnCancelOnMove,
            DueUtcMs = NowMs() + cfg.SpawnWarmupSeconds * 1000L,
            CooldownKey = "spawn",
            CooldownSeconds = spawnCooldown
        }, cfg.SpawnWarmupSeconds);
    }

    public void HandleBack(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (IsJailed(player)) { Reply(player, groupId, "You cannot use /back while jailed.", false); return; }
        if (!cfg.BackEnabled)
        {
            Reply(player, groupId, "The Server Essentials /back command is disabled.", false);
            return;
        }

        if (!TryResolveTeleportPolicy(player, "back", cfg.BackCooldownSeconds, groupId, out int backCooldown))
            return;

        var profile = Profile(player);
        if (profile.Back == null)
        {
            Reply(player, groupId, "You do not have a saved death or teleport position to return to.", false);
            return;
        }

        if (!CheckCooldown(player, "back", backCooldown, out int remaining))
        {
            Reply(player, groupId, $"You can use /back again in {remaining} second(s).", false);
            return;
        }

        BeginTeleport(player, groupId, new PendingTeleport
        {
            PlayerUid = player.PlayerUID,
            Kind = "back",
            Label = string.IsNullOrWhiteSpace(profile.BackReason) ? "previous location" : profile.BackReason,
            Destination = Clone(profile.Back),
            CancelOnMove = cfg.BackCancelOnMove,
            DueUtcMs = NowMs() + cfg.BackWarmupSeconds * 1000L,
            CooldownKey = "back",
            CooldownSeconds = backCooldown,
            SwapBackAfterTeleport = true
        }, cfg.BackWarmupSeconds);
    }

    public void HandleSetHome(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!EnsureWritable(player, groupId)) return;
        var cfg = getConfig();
        if (!cfg.HomesEnabled)
        {
            Reply(player, groupId, "Homes are disabled by Server Essentials.", false);
            return;
        }

        string rawName = (args.PopWord() ?? "home").Trim();
        string name = NormalizeHomeName(rawName);
        if (string.IsNullOrWhiteSpace(name))
        {
            Reply(player, groupId, "Home names may contain letters, numbers, '-' and '_' and must be 1-32 characters.", false);
            return;
        }

        var pos = EntityRuntimeCompat.GetPos(player.Entity);
        if (pos == null)
        {
            Reply(player, groupId, "Your current position could not be read safely.", false);
            return;
        }

        var profile = Profile(player);
        var existing = profile.Homes.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        int limit = GetHomeLimit(player);
        if (existing == null && profile.Homes.Count >= limit)
        {
            Reply(player, groupId, $"You have reached your home limit ({limit}). Delete a home before creating another.", false);
            return;
        }

        var previousHomes = profile.Homes.Select(h => new PlayerHomeDefinition { Name = h.Name, Position = Clone(h.Position), UpdatedAtUnixMilliseconds = h.UpdatedAtUnixMilliseconds }).ToList();
        if (existing == null)
        {
            existing = new PlayerHomeDefinition { Name = name };
            profile.Homes.Add(existing);
        }
        existing.Name = name;
        existing.Position = PlayerTeleportPosition.From(pos);
        existing.UpdatedAtUnixMilliseconds = NowMs();
        profile.LastKnownName = player.PlayerName;
        if (!Save())
        {
            profile.Homes = previousHomes;
            Reply(player, groupId, "Your home could not be saved. The previous homes were retained; please try again after the storage error is resolved.", false);
            return;
        }
        Reply(player, groupId, $"Home '{name}' saved at {pos.X:0.#}, {pos.Y:0.#}, {pos.Z:0.#}. ({profile.Homes.Count}/{limit})", true);
    }

    public void HandleHome(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (IsJailed(player)) { Reply(player, groupId, "You cannot use /home while jailed.", false); return; }
        if (!cfg.HomesEnabled)
        {
            Reply(player, groupId, "Homes are disabled by Server Essentials.", false);
            return;
        }

        if (!TryResolveTeleportPolicy(player, "home", cfg.HomeCooldownSeconds, groupId, out int homeCooldown))
            return;

        var profile = Profile(player);
        var home = ResolveHome(profile, (args.PopWord() ?? "").Trim(), out string error);
        if (home == null)
        {
            Reply(player, groupId, error, false);
            return;
        }

        if (!CheckCooldown(player, "home", homeCooldown, out int remaining))
        {
            Reply(player, groupId, $"You can use /home again in {remaining} second(s).", false);
            return;
        }

        BeginTeleport(player, groupId, new PendingTeleport
        {
            PlayerUid = player.PlayerUID,
            Kind = "home",
            Label = $"home '{home.Name}'",
            Destination = Clone(home.Position),
            CancelOnMove = cfg.HomeCancelOnMove,
            DueUtcMs = NowMs() + cfg.HomeWarmupSeconds * 1000L,
            CooldownKey = "home",
            CooldownSeconds = homeCooldown
        }, cfg.HomeWarmupSeconds);
    }

    public void HandleDeleteHome(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!EnsureWritable(player, groupId)) return;
        var cfg = getConfig();
        if (!cfg.HomesEnabled)
        {
            Reply(player, groupId, "Homes are disabled by Server Essentials.", false);
            return;
        }

        var profile = Profile(player);
        var home = ResolveHome(profile, (args.PopWord() ?? "").Trim(), out string error);
        if (home == null)
        {
            Reply(player, groupId, error, false);
            return;
        }
        int previousIndex = profile.Homes.IndexOf(home);
        profile.Homes.Remove(home);
        if (!Save())
        {
            profile.Homes.Insert(previousIndex, home);
            Reply(player, groupId, "Your home could not be deleted from storage. It has been retained; please try again after the storage error is resolved.", false);
            return;
        }
        Reply(player, groupId, $"Home '{home.Name}' deleted.", true);
    }

    public void HandleHomes(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (!cfg.HomesEnabled)
        {
            Reply(player, groupId, "Homes are disabled by Server Essentials.", false);
            return;
        }

        var profile = Profile(player);
        int limit = GetHomeLimit(player);
        if (profile.Homes.Count == 0)
        {
            Reply(player, groupId, $"You have no homes. Use /sethome [name]. Home limit: {limit}.", true);
            return;
        }
        string names = string.Join(", ", profile.Homes.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Select(h => h.Name));
        Reply(player, groupId, $"Homes ({profile.Homes.Count}/{limit}): {names}", true);
    }

    public void HandleT2p(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (IsJailed(player)) { Reply(player, groupId, "You cannot use /t2p while jailed.", false); return; }
        if (!cfg.T2pEnabled) { Reply(player, groupId, "The Server Essentials /t2p command is disabled.", false); return; }
        if (!TryResolveTeleportPolicy(player, "t2p", cfg.T2pCooldownSeconds, groupId, out int cooldown, requireAdminWhenUnconfigured: true))
            return;
        if (!CheckCooldown(player, "t2p", cooldown, out int remaining))
        {
            Reply(player, groupId, $"You can use /t2p again in {remaining} second(s).", false);
            return;
        }
        if (pendingByUid.ContainsKey(player.PlayerUID) || rtpSearching.Contains(player.PlayerUID) || HasAcceptedConsentPlayer(player.PlayerUID))
        {
            Reply(player, groupId, "You already have a Suite teleport in progress.", false);
            return;
        }
        if (consentOutgoingByRequesterUid.TryGetValue(player.PlayerUID, out var existingOutgoing))
        {
            var existingTarget = OnlineByUid(existingOutgoing.TargetUid);
            string targetLabel = existingTarget?.PlayerName ?? existingOutgoing.TargetName;
            Reply(player, groupId, $"You already have a /{existingOutgoing.Kind} request pending for {targetLabel}. Wait for it to be accepted or expire.", false);
            return;
        }

        string targetName = (args.PopAll() ?? "").Trim();
        if (targetName.Length == 0) { Reply(player, groupId, "Usage: /t2p <online player>", false); return; }
        var target = OnlineByName(targetName);
        if (target == null) { Reply(player, groupId, $"Player '{targetName}' is not online.", false); return; }
        if (string.Equals(target.PlayerUID, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
        {
            Reply(player, groupId, "You cannot send yourself a /t2p request.", false);
            return;
        }
        if (IsJailed(target))
        {
            Reply(player, groupId, $"{target.PlayerName} is jailed and cannot accept /t2p requests.", false);
            return;
        }
        if (consentIncomingByTargetUid.TryGetValue(target.PlayerUID, out var existingIncoming))
        {
            var otherRequester = OnlineByUid(existingIncoming.RequesterUid);
            string requesterLabel = otherRequester?.PlayerName ?? existingIncoming.RequesterName;
            Reply(player, groupId, $"{target.PlayerName} already has a /{existingIncoming.Kind} request from {requesterLabel} waiting for a response.", false);
            return;
        }

        long now = NowMs();
        var request = new PendingConsentTeleportRequest
        {
            Kind = "t2p",
            RequesterUid = player.PlayerUID,
            RequesterName = player.PlayerName,
            TargetUid = target.PlayerUID,
            TargetName = target.PlayerName,
            ExpiresUtcMs = now + ConsentRequestTimeoutSeconds * 1000L
        };
        consentOutgoingByRequesterUid[player.PlayerUID] = request;
        consentIncomingByTargetUid[target.PlayerUID] = request;

        Reply(player, groupId, $"Teleport request sent to {target.PlayerName}. They must use /tpaccept within {ConsentRequestTimeoutSeconds} seconds. Your cooldown starts only if the teleport completes.", true);
        target.SendMessage(
            GlobalConstants.GeneralChatGroup,
            $"{player.PlayerName} wants to teleport to you. Type /tpaccept within {ConsentRequestTimeoutSeconds} seconds (or /nse tpaccept if another mod owns that command). After accepting, they will be teleported to you in {ConsentAcceptWarmupSeconds} seconds.",
            EnumChatType.Notification);
    }

    public void HandleT2m(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (IsJailed(player)) { Reply(player, groupId, "You cannot use /t2m while jailed.", false); return; }
        if (!cfg.T2mEnabled) { Reply(player, groupId, "The Server Essentials /t2m command is disabled.", false); return; }
        if (!TryResolveTeleportPolicy(player, "t2m", cfg.T2mCooldownSeconds, groupId, out int cooldown, requireAdminWhenUnconfigured: true))
            return;
        if (!CheckCooldown(player, "t2m", cooldown, out int remaining))
        {
            Reply(player, groupId, $"You can use /t2m again in {remaining} second(s).", false);
            return;
        }
        if (HasAcceptedConsentPlayer(player.PlayerUID))
        {
            Reply(player, groupId, "You already have an accepted /t2m teleport in progress.", false);
            return;
        }

        if (consentOutgoingByRequesterUid.TryGetValue(player.PlayerUID, out var existingOutgoing))
        {
            var existingTarget = OnlineByUid(existingOutgoing.TargetUid);
            string targetLabel = existingTarget?.PlayerName ?? existingOutgoing.TargetName;
            Reply(player, groupId, $"You already have a /t2m request pending for {targetLabel}. Wait for it to be accepted or expire.", false);
            return;
        }

        string targetName = (args.PopAll() ?? "").Trim();
        if (targetName.Length == 0) { Reply(player, groupId, "Usage: /t2m <online player>", false); return; }
        var target = OnlineByName(targetName);
        if (target == null) { Reply(player, groupId, $"Player '{targetName}' is not online.", false); return; }
        if (string.Equals(target.PlayerUID, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
        {
            Reply(player, groupId, "You cannot send yourself a /t2m request.", false);
            return;
        }
        if (IsJailed(target))
        {
            Reply(player, groupId, $"{target.PlayerName} is jailed and cannot be moved with /t2m.", false);
            return;
        }
        if (pendingByUid.ContainsKey(target.PlayerUID) || rtpSearching.Contains(target.PlayerUID) || HasAcceptedConsentPlayer(target.PlayerUID))
        {
            Reply(player, groupId, $"{target.PlayerName} already has a Suite teleport in progress.", false);
            return;
        }
        if (consentIncomingByTargetUid.TryGetValue(target.PlayerUID, out var existingIncoming))
        {
            var otherRequester = OnlineByUid(existingIncoming.RequesterUid);
            string requesterLabel = otherRequester?.PlayerName ?? existingIncoming.RequesterName;
            Reply(player, groupId, $"{target.PlayerName} already has a /t2m request from {requesterLabel} waiting for a response.", false);
            return;
        }

        long now = NowMs();
        var request = new PendingConsentTeleportRequest
        {
            Kind = "t2m",
            RequesterUid = player.PlayerUID,
            RequesterName = player.PlayerName,
            TargetUid = target.PlayerUID,
            TargetName = target.PlayerName,
            ExpiresUtcMs = now + ConsentRequestTimeoutSeconds * 1000L
        };
        consentOutgoingByRequesterUid[player.PlayerUID] = request;
        consentIncomingByTargetUid[target.PlayerUID] = request;

        Reply(player, groupId, $"Teleport request sent to {target.PlayerName}. They must use /tpaccept within {ConsentRequestTimeoutSeconds} seconds. Your cooldown starts only if the teleport completes.", true);
        target.SendMessage(
            GlobalConstants.GeneralChatGroup,
            $"{player.PlayerName} wants to teleport you to them. Type /tpaccept within {ConsentRequestTimeoutSeconds} seconds (or /nse tpaccept if another mod owns that command). After accepting, you will be teleported in {ConsentAcceptWarmupSeconds} seconds.",
            EnumChatType.Notification);
    }

    public void HandleTpAccept(IServerPlayer target, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (IsJailed(target))
        {
            Reply(target, groupId, "You cannot accept a teleport request while jailed.", false);
            return;
        }
        if (!consentIncomingByTargetUid.TryGetValue(target.PlayerUID, out var request))
        {
            Reply(target, groupId, "You do not have a pending Suite teleport request to accept.", false);
            return;
        }

        bool enabled = request.Kind == "t2p" ? cfg.T2pEnabled : cfg.T2mEnabled;
        if (!enabled)
        {
            RemoveConsentRequest(request);
            Reply(target, groupId, $"The Server Essentials /{request.Kind} command is disabled.", false);
            return;
        }

        long now = NowMs();
        if (request.Accepted)
        {
            int remainingWarmup = Math.Max(0, (int)Math.Ceiling((request.DueUtcMs - now) / 1000d));
            Reply(target, groupId, $"That /{request.Kind} request is already accepted. Teleport completes in {remainingWarmup} second(s).", false);
            return;
        }
        if (now >= request.ExpiresUtcMs)
        {
            RemoveConsentRequest(request);
            Reply(target, groupId, $"That /{request.Kind} request has expired.", false);
            var expiredRequester = OnlineByUid(request.RequesterUid);
            if (expiredRequester != null)
                Reply(expiredRequester, GlobalConstants.GeneralChatGroup, $"Your /{request.Kind} request to {target.PlayerName} expired.", false);
            return;
        }

        var requester = OnlineByUid(request.RequesterUid);
        if (requester == null)
        {
            RemoveConsentRequest(request);
            Reply(target, groupId, "The player who sent that teleport request is no longer online.", false);
            return;
        }
        if (IsJailed(requester))
        {
            RemoveConsentRequest(request);
            Reply(target, groupId, $"{requester.PlayerName}'s /{request.Kind} request was cancelled because they are jailed.", false);
            return;
        }

        string movingUid = request.Kind == "t2p" ? request.RequesterUid : request.TargetUid;
        if (pendingByUid.ContainsKey(movingUid) || rtpSearching.Contains(movingUid))
        {
            Reply(target, groupId, "The player who would be teleported already has another Suite teleport in progress.", false);
            return;
        }

        int baseCooldown = request.Kind == "t2p" ? cfg.T2pCooldownSeconds : cfg.T2mCooldownSeconds;
        if (!TryResolveTeleportPolicy(requester, request.Kind, baseCooldown, GlobalConstants.GeneralChatGroup, out int cooldown, requireAdminWhenUnconfigured: true))
        {
            RemoveConsentRequest(request);
            Reply(target, groupId, $"{requester.PlayerName}'s /{request.Kind} request is no longer permitted.", false);
            return;
        }
        if (!CheckCooldown(requester, request.Kind, cooldown, out int remaining))
        {
            RemoveConsentRequest(request);
            Reply(target, groupId, $"{requester.PlayerName}'s /{request.Kind} request can no longer be completed because their cooldown is active.", false);
            Reply(requester, GlobalConstants.GeneralChatGroup, $"Your /{request.Kind} cooldown has {remaining} second(s) remaining, so the request was cancelled.", false);
            return;
        }

        request.Accepted = true;
        request.DueUtcMs = now + ConsentAcceptWarmupSeconds * 1000L;
        if (request.Kind == "t2p")
        {
            Reply(target, groupId, $"Accepted {requester.PlayerName}'s teleport request. They will be teleported to you in {ConsentAcceptWarmupSeconds} seconds.", true);
            Reply(requester, GlobalConstants.GeneralChatGroup, $"{target.PlayerName} accepted your /t2p request. You will be teleported to them in {ConsentAcceptWarmupSeconds} seconds.", true);
        }
        else
        {
            Reply(target, groupId, $"Accepted {requester.PlayerName}'s teleport request. Teleporting to them in {ConsentAcceptWarmupSeconds} seconds.", true);
            Reply(requester, GlobalConstants.GeneralChatGroup, $"{target.PlayerName} accepted your /t2m request. They will be teleported to you in {ConsentAcceptWarmupSeconds} seconds.", true);
        }
    }

    public void HandleRtp(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (IsJailed(player)) { Reply(player, groupId, "You cannot use /rtp while jailed.", false); return; }
        if (!cfg.RtpEnabled)
        {
            Reply(player, groupId, "Random teleport is disabled by Server Essentials.", false);
            return;
        }
        if (pendingByUid.ContainsKey(player.PlayerUID) || rtpSearching.Contains(player.PlayerUID) || HasAcceptedConsentPlayer(player.PlayerUID))
        {
            Reply(player, groupId, "You already have a teleport in progress.", false);
            return;
        }
        if (!TryResolveTeleportPolicy(player, "rtp", cfg.RtpCooldownSeconds, groupId, out int rtpCooldown))
            return;
        if (!CheckCooldown(player, "rtp", rtpCooldown, out int remaining))
        {
            Reply(player, groupId, $"You can use /rtp again in {remaining} second(s).", false);
            return;
        }
        if (cfg.RtpCostEnabled && !HasItemCost(player, cfg.RtpCostItemCode, cfg.RtpCostQuantity, out int have))
        {
            Reply(player, groupId, $"Random teleport requires {cfg.RtpCostQuantity} × {cfg.RtpCostItemCode}. You currently have {have}.", false);
            return;
        }

        var current = EntityRuntimeCompat.GetPos(player.Entity);
        if (current == null)
        {
            Reply(player, groupId, "Your current position could not be read safely.", false);
            return;
        }
        if (current.Dimension != 0)
        {
            Reply(player, groupId, "Random teleport currently supports the overworld dimension only.", false);
            return;
        }

        rtpSearching.Add(player.PlayerUID);
        Reply(player, groupId, "Searching for a safe random teleport location...", true);
        TryFindRtpDestination(player.PlayerUID, groupId, attempt: 1);
    }

    public int GetHomeLimit(IServerPlayer player)
    {
        var cfg = getConfig();
        if (cfg.HomePlayerLimits.TryGetValue(player.PlayerUID ?? "", out int playerLimit)) return Math.Max(0, playerLimit);
        if (cfg.HomePlayerLimits.TryGetValue(player.PlayerName ?? "", out playerLimit)) return Math.Max(0, playerLimit);

        string roleCode = player.ServerData?.RoleCode ?? player.Role?.Code ?? "";
        if (!string.IsNullOrWhiteSpace(roleCode) && cfg.HomeRoleLimits.TryGetValue(roleCode, out int roleLimit)) return Math.Max(0, roleLimit);
        string roleName = player.Role?.Name ?? "";
        if (!string.IsNullOrWhiteSpace(roleName) && cfg.HomeRoleLimits.TryGetValue(roleName, out roleLimit)) return Math.Max(0, roleLimit);
        return Math.Max(0, cfg.HomeGlobalLimit);
    }

    private void TryFindRtpDestination(string playerUid, int groupId, int attempt)
    {
        if (!rtpSearching.Contains(playerUid)) return;
        var player = OnlineByUid(playerUid);
        if (player == null)
        {
            rtpSearching.Remove(playerUid);
            return;
        }
        if (IsJailed(player))
        {
            rtpSearching.Remove(playerUid);
            Reply(player, groupId, "Random teleport cancelled because you are jailed.", false);
            return;
        }
        var cfg = getConfig();
        if (!cfg.RtpEnabled)
        {
            rtpSearching.Remove(playerUid);
            Reply(player, groupId, "Random teleport was disabled while the search was running.", false);
            return;
        }
        if (attempt > cfg.RtpMaxSearchAttempts)
        {
            rtpSearching.Remove(playerUid);
            Reply(player, groupId, $"No safe random teleport location was found after {cfg.RtpMaxSearchAttempts} attempts. Nothing was consumed.", false);
            return;
        }

        EntityPos? center = cfg.RtpCenterOnPlayerPosition
            ? EntityRuntimeCompat.GetPos(player.Entity)?.Copy()
            : api.World.DefaultSpawnPosition?.Copy();
        if (center == null)
        {
            rtpSearching.Remove(playerUid);
            Reply(player, groupId, cfg.RtpCenterOnPlayerPosition
                ? "Random teleport could not read your current position for the search center."
                : "Random teleport could not determine the server spawn position for the search center.", false);
            return;
        }

        double min = Math.Max(0, cfg.RtpMinRadius);
        double max = Math.Max(min, cfg.RtpMaxRadius);
        double radius = Math.Sqrt(min * min + api.World.Rand.NextDouble() * (max * max - min * min));
        double angle = api.World.Rand.NextDouble() * Math.PI * 2;
        int x = (int)Math.Round(center.X + Math.Cos(angle) * radius);
        int z = (int)Math.Round(center.Z + Math.Sin(angle) * radius);
        var accessor = api.World.BlockAccessor;
        if (x < 1 || z < 1 || x >= accessor.MapSizeX - 1 || z >= accessor.MapSizeZ - 1)
        {
            TryFindRtpDestination(playerUid, groupId, attempt + 1);
            return;
        }

        int chunkX = (int)Math.Floor(x / (double)GlobalConstants.ChunkSize);
        int chunkZ = (int)Math.Floor(z / (double)GlobalConstants.ChunkSize);
        try
        {
            // LoadChunkColumnPriority is a no-op for an already resident column on some
            // server builds, so resolve it immediately rather than waiting for a callback
            // that may never be raised.
            if (api.WorldManager.GetChunk(chunkX, 0, chunkZ) != null)
            {
                ResolveRtpCandidate(playerUid, groupId, attempt, x, z);
                return;
            }

            api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions
            {
                KeepLoaded = false,
                OnLoaded = () => api.Event.EnqueueMainThreadTask(
                    () => ResolveRtpCandidate(playerUid, groupId, attempt, x, z),
                    "nse-rtp-resolve")
            });
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/RTP] Failed loading candidate chunk for {0}: {1}", player.PlayerName, ex.Message);
            TryFindRtpDestination(playerUid, groupId, attempt + 1);
        }
    }

    private void ResolveRtpCandidate(string playerUid, int groupId, int attempt, int x, int z)
    {
        var currentPlayer = OnlineByUid(playerUid);
        if (currentPlayer == null || !rtpSearching.Contains(playerUid)) return;
        var cfg = getConfig();
        if (!cfg.RtpEnabled)
        {
            rtpSearching.Remove(playerUid);
            Reply(currentPlayer, groupId, "Random teleport was disabled while the search was running.", false);
            return;
        }

        var destination = FindSafeRtpPosition(x, z, cfg.RtpAvoidWaterAndLava);
        if (destination == null)
        {
            TryFindRtpDestination(playerUid, groupId, attempt + 1);
            return;
        }

        // Re-resolve the live role policy after the asynchronous search. This both
        // supplies the final cooldown and honors any access/cooldown change made
        // while the RTP candidate was being loaded.
        if (!TryResolveTeleportPolicy(currentPlayer, "rtp", cfg.RtpCooldownSeconds, groupId, out int rtpCooldown))
        {
            rtpSearching.Remove(playerUid);
            return;
        }

        rtpSearching.Remove(playerUid);
        BeginTeleport(currentPlayer, groupId, new PendingTeleport
        {
            PlayerUid = currentPlayer.PlayerUID,
            Kind = "rtp",
            Label = "random location",
            Destination = destination,
            CancelOnMove = cfg.RtpCancelOnMove,
            DueUtcMs = NowMs() + cfg.RtpWarmupSeconds * 1000L,
            CostEnabled = cfg.RtpCostEnabled,
            CostItemCode = cfg.RtpCostItemCode,
            CostQuantity = cfg.RtpCostQuantity,
            CooldownKey = "rtp",
            CooldownSeconds = rtpCooldown
        }, cfg.RtpWarmupSeconds);
    }

    private PlayerTeleportPosition? FindSafeRtpPosition(int x, int z, bool avoidLiquids)
    {
        var accessor = api.World.BlockAccessor;
        int maxY = Math.Max(3, accessor.MapSizeY - 3);
        int surfaceY = Math.Clamp(accessor.GetRainMapHeightAt(x, z), 1, maxY - 1);
        int baseY = Math.Clamp(surfaceY + 1, 1, maxY);
        for (int offset = 0; offset <= 24; offset++)
        {
            int up = baseY + offset;
            if (up <= maxY && IsSafeStandingPosition(x, up, z, avoidLiquids))
                return new PlayerTeleportPosition { X = x + 0.5, Y = up, Z = z + 0.5, Dimension = 0 };
            if (offset == 0) continue;
            int down = baseY - offset;
            if (down >= 1 && IsSafeStandingPosition(x, down, z, avoidLiquids))
                return new PlayerTeleportPosition { X = x + 0.5, Y = down, Z = z + 0.5, Dimension = 0 };
        }
        return null;
    }

    private bool IsSafeStandingPosition(int x, int y, int z, bool avoidLiquids)
    {
        var accessor = api.World.BlockAccessor;
        var feet = new BlockPos(x, y, z, 0);
        var head = new BlockPos(x, y + 1, z, 0);
        var ground = new BlockPos(x, y - 1, z, 0);
        if (accessor.IsNotTraversable(feet) || accessor.IsNotTraversable(head) || !accessor.IsNotTraversable(ground)) return false;
        if (!avoidLiquids) return true;
        var feetBlock = accessor.GetBlock(feet);
        var headBlock = accessor.GetBlock(head);
        var groundBlock = accessor.GetBlock(ground);
        return !IsUnsafeLiquid(feetBlock) && !IsUnsafeLiquid(headBlock) && !IsUnsafeLiquid(groundBlock);
    }

    private static bool IsUnsafeLiquid(Block? block)
        => block != null && (block.ForFluidsLayer || block.BlockMaterial == EnumBlockMaterial.Lava);

    private void BeginTeleport(IServerPlayer player, int groupId, PendingTeleport pending, int warmupSeconds)
    {
        if (pendingByUid.ContainsKey(player.PlayerUID) || HasAcceptedConsentPlayer(player.PlayerUID))
        {
            Reply(player, groupId, "You already have a teleport in progress.", false);
            return;
        }
        var pos = EntityRuntimeCompat.GetPos(player.Entity);
        if (pos == null)
        {
            Reply(player, groupId, "Your current position could not be read safely.", false);
            return;
        }
        pending = new PendingTeleport
        {
            PlayerUid = pending.PlayerUid,
            Kind = pending.Kind,
            Label = pending.Label,
            DueUtcMs = pending.DueUtcMs,
            StartX = pos.X,
            StartY = pos.Y,
            StartZ = pos.Z,
            CancelOnMove = pending.CancelOnMove,
            Destination = pending.Destination,
            CostEnabled = pending.CostEnabled,
            CostItemCode = pending.CostItemCode,
            CostQuantity = pending.CostQuantity,
            CooldownKey = pending.CooldownKey,
            CooldownSeconds = pending.CooldownSeconds,
            SwapBackAfterTeleport = pending.SwapBackAfterTeleport
        };

        if (warmupSeconds <= 0)
        {
            CompleteTeleport(player, pending, groupId);
            return;
        }
        pendingByUid[player.PlayerUID] = pending;
        string cost = pending.CostEnabled ? $" Cost: {pending.CostQuantity} × {pending.CostItemCode}." : "";
        string movement = pending.CancelOnMove ? " Do not move." : "";
        Reply(player, groupId, $"Teleporting to {pending.Label} in {warmupSeconds} second(s).{movement}{cost}", true);
    }

    private void Tick()
    {
        long maintenanceNow = NowMs();
        if (dirty && maintenanceNow - lastFlushUtcMs >= 5000) Save();
        if (maintenanceNow - lastCooldownPruneUtcMs >= 60_000)
        {
            lastCooldownPruneUtcMs = maintenanceNow;
            // All configured cooldowns are bounded to one day. Preserve reconnect cooldowns.
            foreach (var key in cooldownUtc.Where(x => maintenanceNow - x.Value > 86_400_000L).Select(x => x.Key).ToList()) cooldownUtc.Remove(key);
        }
        if (pendingByUid.Count == 0 && consentIncomingByTargetUid.Count == 0) return;
        long now = NowMs();
        foreach (var pending in pendingByUid.Values.ToArray())
        {
            var player = OnlineByUid(pending.PlayerUid);
            if (player == null)
            {
                pendingByUid.Remove(pending.PlayerUid);
                continue;
            }
            if (IsJailed(player))
            {
                pendingByUid.Remove(pending.PlayerUid);
                Reply(player, GlobalConstants.GeneralChatGroup, "Teleport cancelled because you were jailed.", false);
                continue;
            }
            if (pending.CancelOnMove)
            {
                var pos = EntityRuntimeCompat.GetPos(player.Entity);
                if (pos == null)
                {
                    pendingByUid.Remove(pending.PlayerUid);
                    Reply(player, GlobalConstants.GeneralChatGroup, "Teleport cancelled because your position could not be read safely.", false);
                    continue;
                }
                double dx = pos.X - pending.StartX;
                double dy = pos.Y - pending.StartY;
                double dz = pos.Z - pending.StartZ;
                if (dx * dx + dy * dy + dz * dz > 0.16)
                {
                    pendingByUid.Remove(pending.PlayerUid);
                    Reply(player, GlobalConstants.GeneralChatGroup, "Teleport cancelled because you moved.", false);
                    continue;
                }
            }
            if (now < pending.DueUtcMs) continue;
            pendingByUid.Remove(pending.PlayerUid);
            CompleteTeleport(player, pending, GlobalConstants.GeneralChatGroup);
        }

        foreach (var request in consentIncomingByTargetUid.Values.Distinct().ToArray())
        {
            if (!request.Accepted)
            {
                if (now < request.ExpiresUtcMs) continue;
                RemoveConsentRequest(request);
                var requester = OnlineByUid(request.RequesterUid);
                var target = OnlineByUid(request.TargetUid);
                if (requester != null)
                    Reply(requester, GlobalConstants.GeneralChatGroup, $"Your /{request.Kind} request to {request.TargetName} expired.", false);
                if (target != null)
                    Reply(target, GlobalConstants.GeneralChatGroup, $"The /{request.Kind} request from {request.RequesterName} expired.", false);
                continue;
            }

            if (now < request.DueUtcMs) continue;
            RemoveConsentRequest(request);
            CompleteAcceptedConsentTeleport(request);
        }
    }

    private void CompleteAcceptedConsentTeleport(PendingConsentTeleportRequest request)
    {
        var requester = OnlineByUid(request.RequesterUid);
        var target = OnlineByUid(request.TargetUid);
        if (requester == null)
        {
            if (target != null) Reply(target, GlobalConstants.GeneralChatGroup, "Teleport cancelled because the requesting player went offline.", false);
            return;
        }
        if (target == null)
        {
            Reply(requester, GlobalConstants.GeneralChatGroup, $"Teleport cancelled because {request.TargetName} went offline.", false);
            return;
        }

        var cfg = getConfig();
        bool enabled = request.Kind == "t2p" ? cfg.T2pEnabled : cfg.T2mEnabled;
        if (!enabled)
        {
            Reply(requester, GlobalConstants.GeneralChatGroup, $"Teleport cancelled because /{request.Kind} was disabled.", false);
            Reply(target, GlobalConstants.GeneralChatGroup, $"Teleport cancelled because /{request.Kind} was disabled.", false);
            return;
        }
        if (IsJailed(requester) || IsJailed(target))
        {
            Reply(requester, GlobalConstants.GeneralChatGroup, "Teleport cancelled because one of the players is jailed.", false);
            Reply(target, GlobalConstants.GeneralChatGroup, "Teleport cancelled because one of the players is jailed.", false);
            return;
        }

        var movingPlayer = request.Kind == "t2p" ? requester : target;
        var destinationPlayer = request.Kind == "t2p" ? target : requester;
        if (pendingByUid.ContainsKey(movingPlayer.PlayerUID) || rtpSearching.Contains(movingPlayer.PlayerUID))
        {
            Reply(requester, GlobalConstants.GeneralChatGroup, $"Teleport cancelled because {movingPlayer.PlayerName} has another Suite teleport in progress.", false);
            Reply(target, GlobalConstants.GeneralChatGroup, "Teleport cancelled because another Suite teleport is in progress.", false);
            return;
        }

        int baseCooldown = request.Kind == "t2p" ? cfg.T2pCooldownSeconds : cfg.T2mCooldownSeconds;
        if (!TryResolveTeleportPolicy(requester, request.Kind, baseCooldown, GlobalConstants.GeneralChatGroup, out int cooldown, requireAdminWhenUnconfigured: true))
        {
            Reply(target, GlobalConstants.GeneralChatGroup, $"{requester.PlayerName}'s /{request.Kind} request is no longer permitted.", false);
            return;
        }
        if (!CheckCooldown(requester, request.Kind, cooldown, out int remaining))
        {
            Reply(requester, GlobalConstants.GeneralChatGroup, $"Teleport cancelled because your /{request.Kind} cooldown has {remaining} second(s) remaining.", false);
            Reply(target, GlobalConstants.GeneralChatGroup, $"Teleport cancelled because the requester's /{request.Kind} cooldown is active.", false);
            return;
        }

        var destination = EntityRuntimeCompat.GetPos(destinationPlayer.Entity)?.Copy();
        var origin = EntityRuntimeCompat.GetPos(movingPlayer.Entity)?.Copy();
        if (destination == null || origin == null)
        {
            Reply(requester, GlobalConstants.GeneralChatGroup, "Teleport cancelled because a player position could not be read safely.", false);
            Reply(target, GlobalConstants.GeneralChatGroup, "Teleport cancelled because a player position could not be read safely.", false);
            return;
        }

        try
        {
            PacketGuardTrustedState.Mark(movingPlayer, request.Kind, 5_000, PacketGuardTrustScope.All);
            movingPlayer.Entity.TeleportTo(destination);
            RecordTeleportOrigin(movingPlayer, origin, request.Kind);
            if (cooldown > 0) cooldownUtc[CooldownKey(requester.PlayerUID, request.Kind)] = NowMs();
            if (request.Kind == "t2p")
            {
                Reply(requester, GlobalConstants.GeneralChatGroup, $"Teleported to {target.PlayerName} after they accepted your request.", true);
                Reply(target, GlobalConstants.GeneralChatGroup, $"{requester.PlayerName} was teleported to your position after you accepted the request.", true);
            }
            else
            {
                Reply(requester, GlobalConstants.GeneralChatGroup, $"{target.PlayerName} was teleported to your position after accepting the request.", true);
                Reply(target, GlobalConstants.GeneralChatGroup, $"Teleported to {requester.PlayerName}.", true);
            }
        }
        catch (Exception ex)
        {
            api.Logger.Error("[ServerEssentials/PlayerTeleports] accepted {0} teleport failed for {1} -> {2}: {3}", request.Kind, requester.PlayerName, target.PlayerName, ex);
            Reply(requester, GlobalConstants.GeneralChatGroup, "Teleport failed.", false);
            Reply(target, GlobalConstants.GeneralChatGroup, "Teleport failed.", false);
        }
    }

    private bool HasAcceptedConsentPlayer(string playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return false;
        if (consentIncomingByTargetUid.TryGetValue(playerUid, out var incoming) && incoming.Accepted) return true;
        return consentOutgoingByRequesterUid.TryGetValue(playerUid, out var outgoing) && outgoing.Accepted;
    }

    private void RemoveConsentRequest(PendingConsentTeleportRequest request)
    {
        if (request == null) return;
        if (consentIncomingByTargetUid.TryGetValue(request.TargetUid, out var incoming) && ReferenceEquals(incoming, request))
            consentIncomingByTargetUid.Remove(request.TargetUid);
        if (consentOutgoingByRequesterUid.TryGetValue(request.RequesterUid, out var outgoing) && ReferenceEquals(outgoing, request))
            consentOutgoingByRequesterUid.Remove(request.RequesterUid);
    }

    private void CompleteTeleport(IServerPlayer player, PendingTeleport pending, int groupId)
    {
        if (IsJailed(player))
        {
            Reply(player, groupId, "Teleport cancelled because you are jailed.", false);
            return;
        }
        var current = EntityRuntimeCompat.GetPos(player.Entity)?.Copy();
        if (current == null)
        {
            Reply(player, groupId, "Teleport cancelled because your current position could not be read. Nothing was consumed.", false);
            return;
        }
        if (pending.CostEnabled && !HasItemCost(player, pending.CostItemCode, pending.CostQuantity, out int have))
        {
            Reply(player, groupId, $"Teleport requires {pending.CostQuantity} × {pending.CostItemCode}. You currently have {have}. Nothing was consumed.", false);
            return;
        }

        ItemStack? refund = null;
        if (pending.CostEnabled && !TryRemoveItemCost(player, pending.CostItemCode, pending.CostQuantity, out refund))
        {
            Reply(player, groupId, "Your inventory changed before teleport completed, so nothing was consumed.", false);
            return;
        }

        try
        {
            PacketGuardTrustedState.Mark(player, pending.Kind, 5_000, PacketGuardTrustScope.All);
            player.Entity.TeleportTo(pending.Destination.ToEntityPos());
            if (pending.SwapBackAfterTeleport)
                SetBack(player, PlayerTeleportPosition.From(current), "back");
            else
                RecordTeleportOrigin(player, current, pending.Kind);
            if (pending.CooldownSeconds > 0)
                cooldownUtc[CooldownKey(player.PlayerUID, pending.CooldownKey)] = NowMs();
            Reply(player, groupId, pending.CostEnabled
                ? $"Teleported to {pending.Label}. Consumed {pending.CostQuantity} × {pending.CostItemCode}."
                : $"Teleported to {pending.Label}.", true);
        }
        catch (Exception ex)
        {
            if (refund != null)
            {
                try { player.Entity.TryGiveItemStack(refund); } catch { }
            }
            api.Logger.Error("[ServerEssentials/PlayerTeleports] {0} teleport failed for {1}: {2}", pending.Kind, player.PlayerName, ex);
            Reply(player, groupId, "Teleport failed. Any consumed item cost was refunded where inventory space allowed.", false);
        }
    }

    private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
    {
        var cfg = getConfig();
        if (!cfg.BackEnabled || !cfg.BackTrackDeaths) return;
        var pos = EntityRuntimeCompat.GetPos(player.Entity);
        if (pos == null) return;
        SetBack(player, PlayerTeleportPosition.From(pos), "last death");
    }

    private void SetBack(IServerPlayer player, PlayerTeleportPosition position, string reason)
    {
        if (!storageReady || disposed) return;
        var profile = Profile(player);
        profile.Back = Clone(position);
        profile.BackReason = string.IsNullOrWhiteSpace(reason) ? "previous location" : reason.Trim();
        profile.BackUpdatedAtUnixMilliseconds = NowMs();
        profile.LastKnownName = player.PlayerName;
        dirty = true;
    }

    private bool IsJailed(IServerPlayer player)
        => player != null && isJailed?.Invoke(player.PlayerUID) == true;

    private static string RoleCode(IServerPlayer player)
        => (player.ServerData?.RoleCode ?? player.Role?.Code ?? "").Trim();

    private bool TryResolveTeleportPolicy(
        IServerPlayer player,
        string command,
        int defaultCooldownSeconds,
        int groupId,
        out int cooldownSeconds,
        bool requireAdminWhenUnconfigured = false)
    {
        var policy = getConfig().ResolveTeleportRolePolicy(command, RoleCode(player), defaultCooldownSeconds);
        cooldownSeconds = policy.CooldownSeconds;
        if (requireAdminWhenUnconfigured && !policy.Overridden &&
            !player.HasPrivilege(Privilege.root) && !player.HasPrivilege(Privilege.controlserver))
        {
            string roleText = string.IsNullOrWhiteSpace(policy.RoleCode) ? "your role" : $"role '{policy.RoleCode}'";
            Reply(player, groupId, $"/{command} is not enabled for {roleText}. An administrator can grant it in Player Commands → Teleport Role Access & Cooldowns.", false);
            return false;
        }
        if (policy.Allowed) return true;
        string role = string.IsNullOrWhiteSpace(policy.RoleCode) ? "your role" : $"role '{policy.RoleCode}'";
        Reply(player, groupId, $"/{command} is not available to {role}.", false);
        return false;
    }

    private bool CheckCooldown(IServerPlayer player, string key, int cooldownSeconds, out int remainingSeconds)
    {
        remainingSeconds = 0;
        if (cooldownSeconds <= 0) return true;
        string full = CooldownKey(player.PlayerUID, key);
        if (!cooldownUtc.TryGetValue(full, out long last)) return true;
        long remainingMs = last + cooldownSeconds * 1000L - NowMs();
        if (remainingMs <= 0) return true;
        remainingSeconds = (int)Math.Ceiling(remainingMs / 1000d);
        return false;
    }

    private static string CooldownKey(string uid, string key) => $"{uid}:{key}";

    private PlayerTeleportProfile Profile(IServerPlayer player)
    {
        if (!profiles.TryGetValue(player.PlayerUID, out var profile))
        {
            profile = new PlayerTeleportProfile { PlayerUid = player.PlayerUID, LastKnownName = player.PlayerName };
            profiles[player.PlayerUID] = profile;
        }
        profile.Homes ??= new List<PlayerHomeDefinition>();
        profile.LastKnownName = player.PlayerName;
        return profile;
    }

    private PlayerHomeDefinition? ResolveHome(PlayerTeleportProfile profile, string requested, out string error)
    {
        error = "";
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string name = NormalizeHomeName(requested);
            var match = profile.Homes.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            error = $"Home '{requested}' was not found. Use /homes to list your homes.";
            return null;
        }
        var namedHome = profile.Homes.FirstOrDefault(h => h.Name.Equals("home", StringComparison.OrdinalIgnoreCase));
        if (namedHome != null) return namedHome;
        if (profile.Homes.Count == 1) return profile.Homes[0];
        if (profile.Homes.Count == 0) error = "You have no homes. Use /sethome [name].";
        else error = "You have multiple homes. Use /home <name>.";
        return null;
    }

    public static string NormalizeHomeName(string? value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return "home";
        if (text.Length > 32) return "";
        return text.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') ? text : "";
    }

    private static PlayerTeleportPosition Clone(PlayerTeleportPosition p) => new()
    {
        X = p.X, Y = p.Y, Z = p.Z, Dimension = p.Dimension, Yaw = p.Yaw, Pitch = p.Pitch
    };

    private IServerPlayer? OnlineByUid(string uid) => api.World.AllOnlinePlayers.OfType<IServerPlayer>()
        .FirstOrDefault(p => string.Equals(p.PlayerUID, uid, StringComparison.OrdinalIgnoreCase));

    private IServerPlayer? OnlineByName(string name) => api.World.AllOnlinePlayers.OfType<IServerPlayer>()
        .FirstOrDefault(p => string.Equals(p.PlayerName, name, StringComparison.OrdinalIgnoreCase));

    private bool HasItemCost(IServerPlayer player, string itemCode, int quantity, out int count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(itemCode) || quantity <= 0) return true;
        foreach (var inv in player.InventoryManager.InventoriesOrdered)
        {
            if (inv == null) continue;
            for (int i = 0; i < inv.Count; i++)
            {
                var stack = inv[i]?.Itemstack;
                if (stack?.Collectible?.Code == null) continue;
                if (!stack.Collectible.Code.ToString().Equals(itemCode, StringComparison.OrdinalIgnoreCase)) continue;
                count += Math.Max(0, stack.StackSize);
                if (count >= quantity) return true;
            }
        }
        return false;
    }

    private bool TryRemoveItemCost(IServerPlayer player, string itemCode, int quantity, out ItemStack? refund)
    {
        refund = null;
        if (!HasItemCost(player, itemCode, quantity, out _)) return false;
        int remaining = quantity;
        CollectibleObject? collectible = null;
        foreach (var inv in player.InventoryManager.InventoriesOrdered)
        {
            if (inv == null) continue;
            for (int i = 0; i < inv.Count && remaining > 0; i++)
            {
                var slot = inv[i];
                var stack = slot?.Itemstack;
                if (slot == null || stack?.Collectible?.Code == null) continue;
                if (!stack.Collectible.Code.ToString().Equals(itemCode, StringComparison.OrdinalIgnoreCase)) continue;
                collectible ??= stack.Collectible;
                int take = Math.Min(remaining, stack.StackSize);
                stack.StackSize -= take;
                remaining -= take;
                if (stack.StackSize <= 0) slot.Itemstack = null;
                slot.MarkDirty();
            }
            if (remaining <= 0) break;
        }
        if (remaining > 0 || collectible == null) return false;
        refund = new ItemStack(collectible, quantity);
        return true;
    }

    private void Load()
    {
        try
        {
            byte[]? bytes = api.WorldManager.SaveGame.GetData(SaveKey);
            if (bytes?.Length > 32 * 1024 * 1024) throw new InvalidOperationException("Saved teleport data exceeds the 32 MiB safety limit.");
            var db = bytes == null || bytes.Length == 0 ? new PlayerTeleportDatabase() :
                JsonSerializer.Deserialize<PlayerTeleportDatabase>(bytes) ?? throw new InvalidOperationException("Saved teleport data is null.");
            if (db.SchemaVersion != 1 || db.Players == null) throw new InvalidOperationException("Unsupported or invalid teleport data schema.");
            var loaded = new Dictionary<string, PlayerTeleportProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in db.Players)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.PlayerUid) || loaded.ContainsKey(profile.PlayerUid)) throw new InvalidOperationException("Invalid or duplicate saved player profile.");
                profile.Homes ??= new List<PlayerHomeDefinition>();
                if (profile.Homes.Any(h => h == null || h.Position == null || string.IsNullOrWhiteSpace(NormalizeHomeName(h.Name)) || !ValidStoredPosition(h.Position)) ||
                    (profile.Back != null && !ValidStoredPosition(profile.Back))) throw new InvalidOperationException("Invalid saved home/back position.");
                profile.Homes = profile.Homes
                    .Where(h => h != null && h.Position != null && !string.IsNullOrWhiteSpace(NormalizeHomeName(h.Name)))
                    .GroupBy(h => NormalizeHomeName(h.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(h => h.UpdatedAtUnixMilliseconds).First())
                    .ToList();
                loaded[profile.PlayerUid] = profile;
            }
            profiles.Clear();
            foreach (var entry in loaded) profiles[entry.Key] = entry.Value;
            storageReady = true;
            StorageError = "";
            dirty = false;
        }
        catch (Exception ex)
        {
            storageReady = false;
            StorageError = ex.Message;
            api.Logger.Error("[ServerEssentials/PlayerTeleports] Failed to load saved homes/back positions. Writes are disabled; saved bytes and cached profiles were retained. Restore valid data and restart to recover. {0}", ex);
        }
    }

    private static bool ValidStoredPosition(PlayerTeleportPosition pos) => double.IsFinite(pos.X) && double.IsFinite(pos.Y) && double.IsFinite(pos.Z) && float.IsFinite(pos.Yaw) && float.IsFinite(pos.Pitch);
    private bool EnsureWritable(IServerPlayer player, int groupId)
    {
        if (storageReady && !disposed) return true;
        Reply(player, groupId, "Saved homes/back data is unavailable. Changes are disabled to protect existing data; ask an administrator to check the server log.", false);
        return false;
    }
    private void OnWorldSave() { if (dirty) Save(); }

    private bool Save()
    {
        if (!storageReady || disposed) return false;
        dirty = true;
        lastFlushUtcMs = NowMs();
        try
        {
            var db = new PlayerTeleportDatabase
            {
                Players = profiles.Values.OrderBy(p => p.LastKnownName, StringComparer.OrdinalIgnoreCase).ToList()
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(db);
            if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Home/back data exceeds the 32 MiB safety limit; the previous saved data was retained.");
            api.WorldManager.SaveGame.StoreData(SaveKey, bytes);
            dirty = false;
            StorageError = "";
            return true;
        }
        catch (Exception ex)
        {
            api.Logger.Error("[ServerEssentials/PlayerTeleports] Failed to save player teleport data: {0}", ex);
            StorageError = ex.Message;
            return false;
        }
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        pendingByUid.Remove(player.PlayerUID);
        rtpSearching.Remove(player.PlayerUID);

        if (consentIncomingByTargetUid.TryGetValue(player.PlayerUID, out var incoming))
        {
            RemoveConsentRequest(incoming);
            var requester = OnlineByUid(incoming.RequesterUid);
            if (requester != null)
                Reply(requester, GlobalConstants.GeneralChatGroup, $"Your /{incoming.Kind} request to {player.PlayerName} was cancelled because they disconnected.", false);
        }
        if (consentOutgoingByRequesterUid.TryGetValue(player.PlayerUID, out var outgoing))
        {
            RemoveConsentRequest(outgoing);
            var target = OnlineByUid(outgoing.TargetUid);
            if (target != null)
                Reply(target, GlobalConstants.GeneralChatGroup, $"The /{outgoing.Kind} request from {player.PlayerName} was cancelled because they disconnected.", false);
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static void Reply(IServerPlayer player, int groupId, string message, bool success) =>
        player.SendMessage(groupId, message, success ? EnumChatType.CommandSuccess : EnumChatType.CommandError);

    public void Dispose()
    {
        if (disposed) return;
        if (dirty) Save();
        disposed = true;
        api.Event.PlayerDeath -= OnPlayerDeath;
        api.Event.PlayerDisconnect -= OnPlayerDisconnect;
        api.Event.GameWorldSave -= OnWorldSave;
        if (tickListenerId > 0)
        {
            try { api.Event.UnregisterGameTickListener(tickListenerId); } catch { }
        }
        pendingByUid.Clear();
        consentIncomingByTargetUid.Clear();
        consentOutgoingByRequesterUid.Clear();
        rtpSearching.Clear();
        cooldownUtc.Clear();
    }
}
