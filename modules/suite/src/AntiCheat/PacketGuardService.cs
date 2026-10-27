using System;
using Newfies.ChatProtocol;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using BlockLogger.Services;
using NewfiesAntiCheat.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Shared trusted-transition registry for legitimate server-authorized state changes.
/// Movement, reach, and future detectors query the same source of truth instead of
/// maintaining independent teleport exceptions. Entries are short-lived and reasoned.
/// </summary>
[Flags]
internal enum PacketGuardTrustScope
{
    None = 0,
    Movement = 1 << 0,
    Reach = 1 << 1,
    All = Movement | Reach
}

internal static class PacketGuardTrustedState
{
    private sealed class Grant
    {
        public long UntilUtc;
        public string Reason = "trusted-transition";
    }

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<PacketGuardTrustScope, Grant>> Entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class TeleportSourceUsage
    {
        public long Count;
        public long LastSeenUtc;
        public string LastPlayerName = "";
        public string LastPlayerUid = "";
        public string LastReason = "";
    }

    internal readonly record struct TeleportSourceTelemetry(
        string Code, long Count, long LastSeenUtc, string LastPlayerName, string LastPlayerUid, string LastReason);

    internal readonly record struct TeleportEventTelemetry(
        string Code, long Utc, string PlayerName, string PlayerUid,
        double FromX, double FromY, double FromZ, int FromDimension,
        double ToX, double ToY, double ToZ, int ToDimension,
        string Reason, string Verification);

    private sealed class SuppressionUsage
    {
        public long Count;
        public long LastSeenUtc;
        public string LastPlayerName = "";
        public string LastPlayerUid = "";
        public string LastDetail = "";
    }

    internal readonly record struct SuppressionTelemetry(
        string Code, long Count, long LastSeenUtc, string LastPlayerName, string LastPlayerUid, string LastDetail);

    private const int MaxRecentTeleportEventsPerSource = 16;
    private static readonly ConcurrentDictionary<string, TeleportSourceUsage> TeleportSources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ExpiringDiagnosticDedupe TeleportSourceDedupe = new();
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<TeleportEventTelemetry>> TeleportEvents = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ExpiringDiagnosticDedupe TeleportEventDedupe = new();
    private static readonly ConcurrentDictionary<string, SuppressionUsage> Suppressions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ExpiringDiagnosticDedupe SuppressionDedupe = new();

    public static void Mark(
        IServerPlayer? player,
        string reason,
        int milliseconds = 2500,
        PacketGuardTrustScope scopes = PacketGuardTrustScope.All)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || scopes == PacketGuardTrustScope.None) return;
        long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long until = nowUtc + Math.Clamp(milliseconds, 250, 30000);
        string cleanReason = CleanReason(reason);
        RecordTeleportSource(player, cleanReason, nowUtc);
        var grants = Entries.GetOrAdd(player.PlayerUID, _ => new ConcurrentDictionary<PacketGuardTrustScope, Grant>());

        foreach (PacketGuardTrustScope scope in new[] { PacketGuardTrustScope.Movement, PacketGuardTrustScope.Reach })
        {
            if ((scopes & scope) == 0) continue;
            grants.AddOrUpdate(scope,
                _ => new Grant { UntilUtc = until, Reason = cleanReason },
                (_, current) =>
                {
                    if (until >= current.UntilUtc)
                    {
                        current.UntilUtc = until;
                        current.Reason = cleanReason;
                    }
                    return current;
                });
        }
    }

    public static bool IsActive(string? playerUid, long nowUtc, PacketGuardTrustScope scope) =>
        TryGetActiveReason(playerUid, nowUtc, scope, out _);

    public static bool TryGetActiveReason(string? playerUid, long nowUtc, PacketGuardTrustScope scope, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(playerUid) || scope == PacketGuardTrustScope.None) return false;
        if (!Entries.TryGetValue(playerUid, out ConcurrentDictionary<PacketGuardTrustScope, Grant>? grants)) return false;
        if (!grants.TryGetValue(scope, out Grant? grant)) return false;
        if (nowUtc <= grant.UntilUtc)
        {
            reason = grant.Reason;
            return true;
        }

        grants.TryRemove(scope, out _);
        if (grants.IsEmpty) Entries.TryRemove(playerUid, out _);
        return false;
    }

    public static void Clear(string? playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return;
        Entries.TryRemove(playerUid, out _);
        TeleportSourceDedupe.RemovePlayer(playerUid);
        TeleportEventDedupe.RemovePlayer(playerUid);
        SuppressionDedupe.RemovePlayer(playerUid);
    }

    public static void ClearAll()
    {
        Entries.Clear();
        TeleportSourceDedupe.Clear();
        TeleportEventDedupe.Clear();
        SuppressionDedupe.Clear();
        TeleportSources.Clear();
        TeleportEvents.Clear();
        Suppressions.Clear();
    }

    public static IReadOnlyList<TeleportSourceTelemetry> GetTeleportSourceTelemetry()
    {
        return TeleportSources
            .Select(pair =>
            {
                TeleportSourceUsage usage = pair.Value;
                return new TeleportSourceTelemetry(
                    pair.Key,
                    Interlocked.Read(ref usage.Count),
                    Interlocked.Read(ref usage.LastSeenUtc),
                    usage.LastPlayerName,
                    usage.LastPlayerUid,
                    usage.LastReason);
            })
            .OrderByDescending(x => x.LastSeenUtc)
            .ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<TeleportEventTelemetry> GetRecentTeleportEvents(string? sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode) || !TeleportEvents.TryGetValue(sourceCode, out ConcurrentQueue<TeleportEventTelemetry>? queue))
            return Array.Empty<TeleportEventTelemetry>();

        return queue.ToArray()
            .OrderByDescending(x => x.Utc)
            .Take(MaxRecentTeleportEventsPerSource)
            .ToList();
    }

    public static void RecordTeleportEvent(
        IServerPlayer? player,
        string reason,
        double fromX, double fromY, double fromZ, int fromDimension,
        double toX, double toY, double toZ, int toDimension,
        string verification,
        long nowUtc = 0)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        string cleanReason = CleanReason(reason);
        string code = CanonicalTeleportSource(cleanReason);
        if (string.IsNullOrWhiteSpace(code)) return;
        if (nowUtc <= 0) nowUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // The Harmony pre-hook and movement sampler can both observe the same teleport. Keep one
        // drill-down row per source/player transition rather than showing implementation duplicates.
        if (TeleportEventDedupe.IsDuplicate(code, player.PlayerUID, nowUtc, 1750)) return;

        var item = new TeleportEventTelemetry(
            code, nowUtc, player.PlayerName ?? "", player.PlayerUID ?? "",
            fromX, fromY, fromZ, fromDimension,
            toX, toY, toZ, toDimension,
            cleanReason, CleanDiagnosticText(verification, 180));
        ConcurrentQueue<TeleportEventTelemetry> queue = TeleportEvents.GetOrAdd(code, _ => new ConcurrentQueue<TeleportEventTelemetry>());
        queue.Enqueue(item);
        while (queue.Count > MaxRecentTeleportEventsPerSource && queue.TryDequeue(out _)) { }
    }

    public static void RecordSuppression(IServerPlayer? player, string code, string detail, int dedupeMs = 750, long nowUtc = 0)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || string.IsNullOrWhiteSpace(code)) return;
        if (nowUtc <= 0) nowUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string cleanCode = code.Trim().ToLowerInvariant();
        if (SuppressionDedupe.IsDuplicate(cleanCode, player.PlayerUID, nowUtc, Math.Clamp(dedupeMs, 100, 10000))) return;

        SuppressionUsage usage = Suppressions.GetOrAdd(cleanCode, _ => new SuppressionUsage());
        Interlocked.Increment(ref usage.Count);
        Interlocked.Exchange(ref usage.LastSeenUtc, nowUtc);
        usage.LastPlayerName = player.PlayerName ?? "";
        usage.LastPlayerUid = player.PlayerUID ?? "";
        usage.LastDetail = CleanDiagnosticText(detail, 180);
    }

    public static IReadOnlyList<SuppressionTelemetry> GetSuppressionTelemetry()
    {
        return Suppressions.Select(pair =>
        {
            SuppressionUsage usage = pair.Value;
            return new SuppressionTelemetry(
                pair.Key,
                Interlocked.Read(ref usage.Count),
                Interlocked.Read(ref usage.LastSeenUtc),
                usage.LastPlayerName,
                usage.LastPlayerUid,
                usage.LastDetail);
        }).OrderByDescending(x => x.Count).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void RecordTeleportSource(IServerPlayer player, string reason, long nowUtc)
    {
        string code = CanonicalTeleportSource(reason);
        if (string.IsNullOrWhiteSpace(code)) return;

        // A single teleport can produce more than one nearby trust signal (for example the
        // server TeleportToDouble hook followed by an engine teleport flag). Avoid turning
        // those duplicate observations into misleading event counts.
        if (TeleportSourceDedupe.IsDuplicate(code, player.PlayerUID, nowUtc, 1750))
        {
            TeleportSourceUsage existing = TeleportSources.GetOrAdd(code, _ => new TeleportSourceUsage());
            Interlocked.Exchange(ref existing.LastSeenUtc, nowUtc);
            existing.LastPlayerName = player.PlayerName ?? "";
            existing.LastPlayerUid = player.PlayerUID ?? "";
            existing.LastReason = reason;
            return;
        }

        TeleportSourceUsage usage = TeleportSources.GetOrAdd(code, _ => new TeleportSourceUsage());
        Interlocked.Increment(ref usage.Count);
        Interlocked.Exchange(ref usage.LastSeenUtc, nowUtc);
        usage.LastPlayerName = player.PlayerName ?? "";
        usage.LastPlayerUid = player.PlayerUID ?? "";
        usage.LastReason = reason;
    }

    private static string CanonicalTeleportSource(string reason)
    {
        return reason.ToLowerInvariant() switch
        {
            "server-teleport-call" or "engine-teleport" => "server-engine-teleport",
            "vanilla-translocator-linked-direct" or "vanilla-translocator-linked-target" or
            "vanilla-translocator-preauthorized" or "vanilla-translocator" => "vanilla-translocator",
            "suite-map-teleport" or "suite-admin-teleport" => "suite-admin-teleport",
            "dashboard-teleport" => "dashboard-teleport",
            "admin-teleport-command" => "admin-teleport-command",
            "th3essentials-spawn" => "th3essentials-spawn",
            "tp-runes" => "tp-runes",
            "spawn" => "spawn-arrival",
            "dimension-change" => "dimension-change",
            _ => ""
        };
    }

    internal static string CleanDiagnosticText(string? value, int maxLength)
    {
        string clean = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static string CleanReason(string? reason)
    {
        string value = (reason ?? "trusted-transition").Trim();
        return value.Length == 0 ? "trusted-transition" : value[..Math.Min(value.Length, 80)];
    }
}

/// <summary>
/// Server-authoritative validation for high-confidence client packet abuse.
/// This deliberately validates the meaning of ordinary Vintage Story actions instead of
/// relying on custom channel names: cheats can use the vanilla player-position and block-action
/// packets while remaining completely client-only.
/// </summary>
internal sealed class PacketGuardService : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly AntiCheatConfig config;
    private readonly Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> reportFinding;
    private readonly ServerTickHealthMonitor tickHealth;
    private readonly PacketIngressGuard ingressGuard;
    private readonly Dictionary<string, MovementState> movement = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PacketFlightReviewWindow> flightReviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<BreakSample>> breakHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ViolationCounter> reachViolations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<ReachPositionSample>> reachPositionHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ViolationCounter> miningViolations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<long>> miningCadenceWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrustedTransitionSample> trustedTransitionSamples = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingTranslocatorTransition> pendingTranslocatorTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> th3EssentialsSpawnIntents = new(StringComparer.OrdinalIgnoreCase);
    private const string ServerTeleportHarmonyId = "newfies.serveressentials.packetguard.teleporttrust";
    private static WeakReference<PacketGuardService>? activeServerTeleportHook;
    private object? serverTeleportHarmony;
    private bool serverTeleportHookInstalled;
    private const int TranslocatorArmDelayMs = 750;
    private const int TranslocatorDepartureWindowMs = 3000;
    private readonly bool tprunesEnabled;
    private readonly bool th3EssentialsEnabled;
    private long sharedSamplerTickId;
    private long lastMovementSampleUtc;
    private long lastFlightReviewSampleUtc;
    private bool flightReviewRuntimeDisabled;
    private long lastReachHistorySampleUtc;
    private long lastTrustedTransitionSampleUtc;
    private readonly List<IServerPlayer> sampledPlayers = new();
    private readonly Dictionary<string, EntityPos> sampledPlayerPositions = new(StringComparer.OrdinalIgnoreCase);
    private bool started;
    private readonly Dictionary<string,long> miningProgressReportUtc=new(StringComparer.Ordinal);
    internal string CombatCheckStatus=>actionChecks?.CombatStatus??"Inactive";
    internal string ImpulseHookStatus=>actionChecks?.ImpulseStatus??"Inactive";
    private ServerActionChecks? actionChecks;
    private bool movementRuntimeDisabled;
    private long lastMovementRuntimeErrorUtc;

    public PacketGuardService(
        ICoreServerAPI api,
        AntiCheatConfig config,
        Action<IServerPlayer, string, AntiCheatSeverity, string, string, string> reportFinding)
    {
        this.api = api;
        this.config = config;
        this.reportFinding = reportFinding;
        tickHealth = new ServerTickHealthMonitor(api, config);
        ingressGuard = new PacketIngressGuard(api, config, reportFinding,tickHealth.GetSnapshot);
        try { tprunesEnabled = api.ModLoader.IsModEnabled("tprunes"); }
        catch { tprunesEnabled = false; }
        try { th3EssentialsEnabled = api.ModLoader.IsModEnabled("th3essentials"); }
        catch { th3EssentialsEnabled = false; }
    }

    public void Start()
    {
        if (started || !config.PacketGuardEnabled) return;
        started = true;
        tickHealth.Start();
        ingressGuard.Start();
        actionChecks=new ServerActionChecks(api,config,tickHealth.GetSnapshot,reportFinding);
        actionChecks.Start();

        // Prefer a direct server-side teleport trust hook over guessing destinations. Any call to
        // EntityPlayer.TeleportToDouble originates in server/game/mod code, whereas a forged
        // movement packet does not. Reflection keeps 0Harmony optional at compile time.
        TryInstallServerTeleportTrustHook();

        api.Event.PlayerNowPlaying += ResetPlayer;
        api.Event.PlayerRespawn += ResetPlayer;
        api.Event.PlayerDisconnect += RemovePlayer;
        api.Event.PlayerChat += OnPlayerChat;
        api.Event.BreakBlock += OnBreakBlock;
        api.Event.CanUseBlock += OnCanUseBlock;
        api.Event.PlayerDimensionChanged += OnPlayerDimensionChanged;

        if (config.PacketGuardMovementEnabled && !EntityRuntimeCompat.CanResolvePosition)
        {
            movementRuntimeDisabled = true;
            api.Logger.Error("[ServerEssentials/AntiCheat] PacketGuard movement protection was not started because this Vintage Story runtime exposes no compatible entity position member. Reach/mining protection will continue.");
        }

        if ((config.PacketGuardMovementEnabled || config.PacketGuardReachEnabled || config.PacketGuardFlightReviewEnabled) && EntityRuntimeCompat.CanResolvePosition)
        {
            // One shared sampler owns the online-player snapshot and position resolution. Earlier
            // builds ran movement, trusted-transition and reach-history loops independently, which
            // could resolve the same EntityPlayer position ~40 times/second. The shared sampler
            // resolves each online player once, then feeds the same snapshot into all three checks.
            int sharedIntervalMs = config.PacketGuardMovementEnabled
                ? Math.Clamp(config.PacketGuardMovementSampleMs, 75, 500)
                : 100;
            sharedSamplerTickId = api.Event.RegisterGameTickListener(_ => SamplePlayersShared(), sharedIntervalMs, sharedIntervalMs);
            api.Logger.Notification(
                "[ServerEssentials/AntiCheat] PacketGuard shared player sampler active: base={0}ms, reach/trusted=100ms, adaptive degraded=100ms, critical=125ms.",
                sharedIntervalMs);
            if (config.PacketGuardMovementEnabled && !movementRuntimeDisabled)
                api.Logger.Notification("[ServerEssentials/AntiCheat] PacketGuard movement ABI-safe position accessor active.");
        }

        api.Logger.Notification(
            "[ServerEssentials/AntiCheat] PacketGuard enabled: movement={0}, reach={1}, mining={2}; hardMove={3:0.#} blocks, connectedBreakLimit={4}/{5}ms; adminsExempt={6}, mountsExempt={7}, engineTeleportGrace={8}ms, tprunes={9}; th3spawn={10}; reachHistory={11}; movementConfirm={12}/{13}ms; reachRewindCap={14}ms; miningDedupe={15}ms; speedModes={16}; endpointNoclip={17}; movementRisk={18}.",
            config.PacketGuardMovementEnabled,
            config.PacketGuardReachEnabled,
            config.PacketGuardMiningEnabled,
            config.PacketGuardHardMoveDistance,
            config.PacketGuardConnectedBreakLimit,
            config.PacketGuardVeinWindowMs,
            config.PacketGuardExemptAdmins,
            config.PacketGuardExemptMountedPlayers,
            config.PacketGuardEngineTeleportGraceMs,
            tprunesEnabled && config.PacketGuardTPRunesCompatibility ? "compatible" : (tprunesEnabled ? "detected/compat-off" : "not-loaded"),
            th3EssentialsEnabled && config.PacketGuardTh3EssentialsSpawnCompatibility ? "compatible" : (th3EssentialsEnabled ? "detected/compat-off" : "not-loaded"),
            config.PacketGuardReachPositionHistoryEnabled ? $"{config.PacketGuardReachPositionHistoryMs}ms" : "off",
            config.PacketGuardMovementConfirmSamples,
            config.PacketGuardMovementConfirmWindowMs,
            config.PacketGuardReachMaxRewindMs,
            config.PacketGuardMiningDuplicateTargetMs,
            config.PacketGuardMovementModeLimitsEnabled
                ? $"ground {config.PacketGuardGroundMaxHorizontalSpeed:0.#}/swim {config.PacketGuardSwimmingMaxHorizontalSpeed:0.#}/air {config.PacketGuardAirborneMaxHorizontalSpeed:0.#} bps"
                : $"off ({config.PacketGuardMaxHorizontalSpeed:0.#} bps)",
            config.PacketGuardNoclipEndpointEnabled ? $"on/{config.PacketGuardNoclipEndpointConfirmSamples}x" : "off",
            config.PacketGuardMovementRiskEnabled ? $"on decay {config.PacketGuardMovementRiskDecayPerSecond:0.#}/s" : "off");

        api.Logger.Notification(
            "[ServerEssentials/AntiCheat] Server teleport trust hook: {0}. Legitimate server/mod TeleportToDouble calls are {1}.",
            serverTeleportHookInstalled ? "active" : "unavailable",
            serverTeleportHookInstalled ? "trusted before movement sampling" : "using engine flags and compatibility fallbacks only");
        api.Logger.Notification("[ServerEssentials/AntiCheat] Unsupported flight review: enabled={0}, confirm={1}s, sample={2}ms; advisory history only, no risk/correlation/punishment. Loaded support, liquids, climbing, mounts, trusted transitions, fresh positions and healthy TPS are required.",
            config.PacketGuardFlightReviewEnabled, config.PacketGuardFlightReviewConfirmSeconds, config.PacketGuardFlightReviewSampleMs);

        api.Logger.Notification(
            "[ServerEssentials/AntiCheat] Vanilla translocator compatibility: linked-target verification active; warm-up authorization retained as fallback ({0}ms departure window).",
            TranslocatorDepartureWindowMs);

        api.Logger.Notification(
            "[ServerEssentials/AntiCheat] Mining Validation v2: enabled={0}, suspiciousRatio={1:0.00}, confirmations={2}/{3}ms, reject={4}; incidentCorrelation={5}, window={6}s, categories={7}, maxBonus={8}.",
            config.PacketGuardMiningCadenceValidationEnabled,
            config.PacketGuardMiningCadenceSuspiciousRatio,
            config.PacketGuardMiningCadenceConfirmations,
            config.PacketGuardMiningCadenceConfirmationWindowMs,
            config.PacketGuardRejectImpossibleMiningCadence,
            config.IncidentCorrelationEnabled,
            config.IncidentCorrelationWindowSeconds,
            config.IncidentCorrelationRequiredCategories,
            config.IncidentCorrelationMaxRiskBonus);

        PacketGuardTpsSnapshot initialTps = tickHealth.GetSnapshot();
        api.Logger.Notification(
            "[ServerEssentials/AntiCheat] TPS-aware timing: enabled={0}, target={1:0.0} TPS, degraded<{2:0.#}%, critical<{3:0.#}%, maxMultiplier={4:0.00}x, suppressCriticalTimingRejects={5}.",
            config.PacketGuardTpsAwarenessEnabled,
            initialTps.TargetTps,
            config.PacketGuardTpsDegradedPercent,
            config.PacketGuardTpsCriticalPercent,
            config.PacketGuardTpsMaxTimingMultiplier,
            config.PacketGuardTpsSuppressTimingRejectsWhenCritical);
    }

    public PacketGuardTpsSnapshot GetTpsSnapshot() => tickHealth.GetSnapshot();
    internal bool IsIngressActive => ingressGuard.IsActive;
    internal string IngressStatus => ingressGuard.Status;

    internal sealed class TrustedTeleportEventSnapshot
    {
        public long Utc { get; set; }
        public string PlayerName { get; set; } = "";
        public string PlayerUid { get; set; } = "";
        public double FromX { get; set; }
        public double FromY { get; set; }
        public double FromZ { get; set; }
        public int FromDimension { get; set; }
        public double ToX { get; set; }
        public double ToY { get; set; }
        public double ToZ { get; set; }
        public int ToDimension { get; set; }
        public string Reason { get; set; } = "";
        public string Verification { get; set; } = "";
    }

    internal sealed class TrustedTeleportSourceSnapshot
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "Active";
        public string TrustScope { get; set; } = "Movement + Reach";
        public string Verification { get; set; } = "";
        public long ObservedCount { get; set; }
        public long LastSeenUtc { get; set; }
        public string LastPlayerName { get; set; } = "";
        public string LastReason { get; set; } = "";
        public List<TrustedTeleportEventSnapshot> RecentEvents { get; set; } = new();
    }

    internal sealed class PacketSuppressionSnapshot
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "Safety";
        public string Description { get; set; } = "";
        public long Count { get; set; }
        public long LastSeenUtc { get; set; }
        public string LastPlayerName { get; set; } = "";
        public string LastDetail { get; set; } = "";
    }

    public IReadOnlyList<TrustedTeleportSourceSnapshot> GetTrustedTeleportSources()
    {
        Dictionary<string, PacketGuardTrustedState.TeleportSourceTelemetry> telemetry =
            PacketGuardTrustedState.GetTeleportSourceTelemetry()
                .ToDictionary(x => x.Code, x => x, StringComparer.OrdinalIgnoreCase);

        TrustedTeleportSourceSnapshot Build(string code, string name, string status, string verification)
        {
            telemetry.TryGetValue(code, out PacketGuardTrustedState.TeleportSourceTelemetry use);
            return new TrustedTeleportSourceSnapshot
            {
                Code = code,
                Name = name,
                Status = status,
                TrustScope = "Movement + Reach",
                Verification = verification,
                ObservedCount = use.Count,
                LastSeenUtc = use.LastSeenUtc,
                LastPlayerName = use.LastPlayerName ?? "",
                LastReason = use.LastReason ?? "",
                RecentEvents = PacketGuardTrustedState.GetRecentTeleportEvents(code).Select(e => new TrustedTeleportEventSnapshot
                {
                    Utc = e.Utc,
                    PlayerName = e.PlayerName,
                    PlayerUid = e.PlayerUid,
                    FromX = e.FromX,
                    FromY = e.FromY,
                    FromZ = e.FromZ,
                    FromDimension = e.FromDimension,
                    ToX = e.ToX,
                    ToY = e.ToY,
                    ToZ = e.ToZ,
                    ToDimension = e.ToDimension,
                    Reason = e.Reason,
                    Verification = e.Verification
                }).ToList()
            };
        }

        string serverHookStatus = serverTeleportHookInstalled ? "Active" : "Fallback only";
        string tprunesStatus = !tprunesEnabled ? "Not loaded" : (config.PacketGuardTPRunesCompatibility ? "Active" : "Disabled");
        string th3Status = !th3EssentialsEnabled ? "Not loaded" : (config.PacketGuardTh3EssentialsSpawnCompatibility ? "Active" : "Disabled");

        return new List<TrustedTeleportSourceSnapshot>
        {
            Build("server-engine-teleport", "Server / mod teleport API", serverHookStatus,
                serverTeleportHookInstalled
                    ? "EntityPlayer.TeleportToDouble is trusted before movement sampling; engine teleport flags remain a secondary signal."
                    : "Harmony hook unavailable; PacketGuard falls back to engine teleport flags and destination-specific compatibility checks."),
            Build("vanilla-translocator", "Vanilla static translocator", "Active",
                "Linked-target verification is preferred; warm-up authorization and endpoint validation are bounded fallbacks."),
            Build("suite-admin-teleport", "Server Essentials admin / map teleport", "Active",
                "Suite-owned teleport actions explicitly mark a trusted transition before moving the player."),
            Build("dashboard-teleport", "Web dashboard teleport", "Active",
                "Dashboard-admin teleport explicitly marks PacketGuard trust before the server moves the player."),
            Build("admin-teleport-command", "Admin /tp and /teleport commands", "Active",
                "Command intent is captured for authorized admins and paired with the subsequent server-side destination change."),
            Build("spawn-arrival", "Server spawn arrival", "Active",
                "An impossible jump is trusted only when the destination matches the server's spawn area."),
            Build("th3essentials-spawn", "Th3Essentials /spawn", th3Status,
                "Compatibility arms only from an actual /spawn command and accepts only a matching spawn destination."),
            Build("tp-runes", "TP Runes", tprunesStatus,
                "Compatibility accepts only the bounded rune-arrival path when the mod is detected and compatibility is enabled."),
            Build("dimension-change", "Dimension change", "Active",
                "Vintage Story's server-side dimension-change event resets movement/reach baselines and grants a short transition window.")
        };
    }

    public IReadOnlyList<PacketSuppressionSnapshot> GetPacketSuppressions()
    {
        Dictionary<string, PacketGuardTrustedState.SuppressionTelemetry> telemetry =
            PacketGuardTrustedState.GetSuppressionTelemetry()
                .ToDictionary(x => x.Code, x => x, StringComparer.OrdinalIgnoreCase);

        PacketSuppressionSnapshot Build(string code, string name, string category, string description)
        {
            telemetry.TryGetValue(code, out PacketGuardTrustedState.SuppressionTelemetry use);
            return new PacketSuppressionSnapshot
            {
                Code = code,
                Name = name,
                Category = category,
                Description = description,
                Count = use.Count,
                LastSeenUtc = use.LastSeenUtc,
                LastPlayerName = use.LastPlayerName ?? "",
                LastDetail = use.LastDetail ?? ""
            };
        }

        return new List<PacketSuppressionSnapshot>
        {
            Build("ingress-unloaded-target", "Unloaded block target discarded", "Safety",
                "An action targeted terrain the server had not loaded. It was discarded without loading chunks or scoring cheating evidence."),
            Build("ingress-stale-transition-target", "Stale dimension target discarded", "Teleport",
                "A packet from the prior dimension arrived during a verified server transition and was discarded without a cheating finding."),
            Build("trusted-translocator", "Translocator trusted", "Teleport",
                "A large movement was accepted because PacketGuard verified a vanilla translocator path or linked target."),
            Build("trusted-server-teleport", "Server teleport trusted", "Teleport",
                "A server/mod TeleportToDouble call or engine teleport signal established a trusted transition before packet enforcement."),
            Build("invalid-block-origin", "Invalid 0,0,0 target ignored", "Reach",
                "A default all-zero block selection was discarded because the player was clearly nowhere near world origin."),
            Build("entity-recreated", "Respawn/entity baseline reset", "Movement",
                "The player's EntityId changed, so movement/reach history was reset instead of comparing against the previous entity."),
            Build("movement-sampler-gap", "Sampler gap re-baselined", "Movement",
                "A long server sampling gap was treated as incomplete evidence and re-baselined instead of scored as a speed/teleport violation."),
            Build("movement-reconciliation-spike", "Transient movement spike discarded", "Movement",
                "An impossible position appeared for too few consecutive samples and recovered before the confirmation threshold, so it was retained only as suppression telemetry."),
            Build("dimension-change", "Dimension change baseline reset", "Teleport",
                "A server dimension transition reset movement/reach baselines and opened only the normal short trusted-transition window."),
            Build("reach-teleport-grace", "Reach action covered by teleport grace", "Reach",
                "A block action arrived while a previously verified teleport was still settling, so the reach detector did not double-report it."),
            Build("mounted-movement", "Mounted/dismount movement re-baselined", "Movement",
                "Seat or mount repositioning was recognized as non-locomotion and excluded from movement scoring."),
            Build("mining-cadence-collapsed-timestamp", "Lag-collapsed mining timestamp ignored", "Mining",
                "Adjacent mining callbacks separated by 0-5 ms during critical TPS degradation were treated as scheduler timing artifacts instead of player evidence.")
        };
    }

    private void ResetPlayer(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        miningProgressReportUtc.Remove(player.PlayerUID);
        movement.Remove(player.PlayerUID);
        flightReviews.Remove(player.PlayerUID);
        breakHistory.Remove(player.PlayerUID);
        reachViolations.Remove(player.PlayerUID);
        reachPositionHistory.Remove(player.PlayerUID);
        miningViolations.Remove(player.PlayerUID);
        miningViolations.Remove(player.PlayerUID + ":vein");
        miningCadenceWindows.Remove(player.PlayerUID + ":authoritative");
        miningCadenceWindows.Remove(player.PlayerUID + ":uncertain");
        trustedTransitionSamples.Remove(player.PlayerUID);
        pendingTranslocatorTransitions.Remove(player.PlayerUID);
        th3EssentialsSpawnIntents.Remove(player.PlayerUID);
        PacketGuardTrustedState.Clear(player.PlayerUID);
    }

    private void RemovePlayer(IServerPlayer player) => ResetPlayer(player);

    private bool ShouldGuard(IServerPlayer? player)
    {
        if (player?.Entity == null || player.WorldData == null) return false;
        if (player.WorldData.CurrentGameMode != EnumGameMode.Survival) return false;
        if (config.PacketGuardExemptAdmins && IsAdmin(player)) return false;
        return true;
    }

    private static bool IsAdmin(IServerPlayer player)
    {
        try
        {
            return player.HasPrivilege(Privilege.root) || player.HasPrivilege(Privilege.controlserver);
        }
        catch { return false; }
    }

    private void TryInstallServerTeleportTrustHook()
    {
        if (serverTeleportHookInstalled) return;
        try
        {
            Assembly? harmonyAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase));
            if (harmonyAssembly == null)
            {
                try { harmonyAssembly = Assembly.Load("0Harmony"); } catch { }
            }
            Type? harmonyType = harmonyAssembly?.GetType("HarmonyLib.Harmony");
            Type? harmonyMethodType = harmonyAssembly?.GetType("HarmonyLib.HarmonyMethod");
            if (harmonyType == null || harmonyMethodType == null) return;

            MethodInfo? target = typeof(EntityPlayer).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (!string.Equals(m.Name, "TeleportToDouble", StringComparison.Ordinal)) return false;
                    ParameterInfo[] ps = m.GetParameters();
                    return ps.Length >= 3 && ps[0].ParameterType == typeof(double) && ps[1].ParameterType == typeof(double) && ps[2].ParameterType == typeof(double);
                });
            MethodInfo? prefix = typeof(PacketGuardService).GetMethod(nameof(BeforeServerPlayerTeleport), BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || prefix == null) return;

            object? harmony = Activator.CreateInstance(harmonyType, new object[] { ServerTeleportHarmonyId });
            object? harmonyMethod = Activator.CreateInstance(harmonyMethodType, new object[] { prefix });
            if (harmony == null || harmonyMethod == null) return;

            MethodInfo? patch = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => string.Equals(m.Name, "Patch", StringComparison.Ordinal))
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] ps = m.GetParameters();
                    return ps.Length >= 2 && typeof(MethodBase).IsAssignableFrom(ps[0].ParameterType) && ps[1].ParameterType == harmonyMethodType;
                });
            if (patch == null) return;

            ParameterInfo[] patchParams = patch.GetParameters();
            object?[] args = new object?[patchParams.Length];
            args[0] = target;
            args[1] = harmonyMethod;
            patch.Invoke(harmony, args);

            serverTeleportHarmony = harmony;
            activeServerTeleportHook = new WeakReference<PacketGuardService>(this);
            serverTeleportHookInstalled = true;
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/AntiCheat] Could not install optional server TeleportToDouble trust hook; compatibility fallbacks remain active. {0}", ex.Message);
            serverTeleportHarmony = null;
            serverTeleportHookInstalled = false;
        }
    }

    private static void BeforeServerPlayerTeleport(EntityPlayer __instance, object[] __args)
    {
        try
        {
            if (__instance == null) return;
            if (activeServerTeleportHook == null || !activeServerTeleportHook.TryGetTarget(out PacketGuardService? service)) return;

            double? targetX = TryReadTeleportArgument(__args, 0);
            double? targetY = TryReadTeleportArgument(__args, 1);
            double? targetZ = TryReadTeleportArgument(__args, 2);
            service.MarkServerInitiatedTeleport(__instance, targetX, targetY, targetZ);
        }
        catch
        {
            // A safety hook must never interfere with the teleport it is observing.
        }
    }

    private static double? TryReadTeleportArgument(object[]? args, int index)
    {
        if (args == null || index < 0 || index >= args.Length || args[index] == null) return null;
        try { return Convert.ToDouble(args[index], System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private void MarkServerInitiatedTeleport(Entity entity, double? targetX = null, double? targetY = null, double? targetZ = null)
    {
        try
        {
            IServerPlayer? player = api.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(p => p?.Entity != null && (ReferenceEquals(p.Entity, entity) || p.Entity.EntityId == entity.EntityId));
            if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

            long now = NowMs();
            string trustReason = "server-teleport-call";

            // Preserve a more specific source label when the teleport was already armed by a
            // known integration. This makes diagnostics useful and avoids replacing a verified
            // translocator/admin/spawn reason with the generic server teleport hook label.
            if (pendingTranslocatorTransitions.TryGetValue(player.PlayerUID, out PendingTranslocatorTransition? pending) &&
                pending.Armed && now - pending.LastSeenUtc <= TranslocatorDepartureWindowMs)
            {
                trustReason = pending.HasLinkedTarget
                    ? "vanilla-translocator-linked-target"
                    : "vanilla-translocator-preauthorized";
            }
            else if (PacketGuardTrustedState.TryGetActiveReason(player.PlayerUID, now, PacketGuardTrustScope.Movement, out string existingReason) &&
                     IsSpecificTeleportTrustReason(existingReason))
            {
                trustReason = existingReason;
            }

            EntityPos? sourcePos = EntityRuntimeCompat.GetPos(entity);

            // TeleportToDouble is also used by the engine for tiny authoritative position
            // corrections (commonly ~1 block vertically). Those corrections cannot trip the
            // hard-movement detector, so treating every one as a trusted teleport only creates
            // noisy diagnostics and unnecessary grace windows. Keep specific/verified teleport
            // integrations untouched, but ignore generic server teleport calls unless the move is
            // large enough to matter to PacketGuard.
            if (string.Equals(trustReason, "server-teleport-call", StringComparison.OrdinalIgnoreCase) &&
                sourcePos != null && targetX.HasValue && targetY.HasValue && targetZ.HasValue)
            {
                double dx = targetX.Value - sourcePos.X;
                double dy = targetY.Value - sourcePos.Y;
                double dz = targetZ.Value - sourcePos.Z;
                double maxAxis = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
                double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                if (maxAxis <= config.PacketGuardHardMoveDistance &&
                    distance <= config.PacketGuardHardMoveDistance * 1.45)
                {
                    return;
                }
            }

            PacketGuardTrustedState.Mark(player, trustReason, config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);

            if (sourcePos != null && targetX.HasValue && targetY.HasValue && targetZ.HasValue)
            {
                string verification = TeleportVerificationForReason(trustReason, true);
                PacketGuardTrustedState.RecordTeleportEvent(
                    player, trustReason,
                    sourcePos.X, sourcePos.Y, sourcePos.Z, sourcePos.Dimension,
                    targetX.Value, targetY.Value, targetZ.Value, sourcePos.Dimension,
                    verification, now);
                PacketGuardTrustedState.RecordSuppression(
                    player,
                    IsTranslocatorTrustReason(trustReason) ? "trusted-translocator" : "trusted-server-teleport",
                    $"{trustReason}: {sourcePos.X:0.0},{sourcePos.Y:0.0},{sourcePos.Z:0.0} -> {targetX.Value:0.0},{targetY.Value:0.0},{targetZ.Value:0.0}",
                    1750, now);
            }

            if (movement.TryGetValue(player.PlayerUID, out MovementState? state))
                state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, NowMs() + config.PacketGuardEngineTeleportGraceMs);
            reachPositionHistory.Remove(player.PlayerUID);
            reachViolations.Remove(player.PlayerUID);
            pendingTranslocatorTransitions.Remove(player.PlayerUID);
        }
        catch
        {
            // Never block another mod's/vanilla's TeleportToDouble call.
        }
    }

    private static bool IsSpecificTeleportTrustReason(string? reason)
    {
        return (reason ?? "").ToLowerInvariant() switch
        {
            "vanilla-translocator-linked-direct" or "vanilla-translocator-linked-target" or
            "vanilla-translocator-preauthorized" or "vanilla-translocator" or
            "suite-map-teleport" or "suite-admin-teleport" or "dashboard-teleport" or
            "admin-teleport-command" or "th3essentials-spawn" or "tp-runes" or
            "spawn" or "dimension-change" => true,
            _ => false
        };
    }

    private static bool IsTranslocatorTrustReason(string? reason)
    {
        return (reason ?? "").StartsWith("vanilla-translocator", StringComparison.OrdinalIgnoreCase);
    }

    private static string TeleportVerificationForReason(string? reason, bool serverHookObserved = false)
    {
        string value = (reason ?? "").ToLowerInvariant();
        return value switch
        {
            "vanilla-translocator-linked-direct" => "Source-side static translocator link matched the observed destination.",
            "vanilla-translocator-linked-target" => "Warm-up authorization was armed at a static translocator and its linked target matched the destination.",
            "vanilla-translocator-preauthorized" => "Vanilla teleporter warm-up state authorized the transition; endpoint validation remained bounded.",
            "vanilla-translocator" => "Both source and destination were validated beside compatible translocator endpoints.",
            "th3essentials-spawn" => "Recent /spawn intent plus a matching saved spawn destination.",
            "tp-runes" => "TP Runes was detected and the arrival matched the rune's bounded beacon destination.",
            "spawn" => "Arrival matched the server/player spawn area.",
            "suite-map-teleport" or "suite-admin-teleport" => "Server Essentials explicitly marked this administrator teleport before movement.",
            "dashboard-teleport" => "Authenticated dashboard teleport explicitly marked trust before movement.",
            "admin-teleport-command" => "Authorized /tp or /teleport command intent was paired with the server-side move.",
            "dimension-change" => "Server dimension lifecycle reset baselines and granted a bounded transition window.",
            "engine-teleport" => "Vintage Story reported an engine-authorized teleport flag.",
            "server-teleport-call" when serverHookObserved => "EntityPlayer.TeleportToDouble was intercepted server-side before the destination was applied.",
            "server-teleport-call" => "Server-side teleport API trust was active.",
            _ => serverHookObserved ? "Server-side teleport API call was observed before movement." : "Recognized trusted transition."
        };
    }

    private void RecordTrustedMovementTransition(
        IServerPlayer player, string reason, MovementState state, EntityPos destination, string verification, string suppressionCode)
    {
        long now = NowMs();
        PacketGuardTrustedState.RecordTeleportEvent(
            player, reason,
            state.X, state.Y, state.Z, state.Dimension,
            destination.X, destination.Y, destination.Z, destination.Dimension,
            verification, now);
        PacketGuardTrustedState.RecordSuppression(
            player, suppressionCode,
            $"{reason}: {state.X:0.0},{state.Y:0.0},{state.Z:0.0} d{state.Dimension} -> {destination.X:0.0},{destination.Y:0.0},{destination.Z:0.0} d{destination.Dimension}",
            1750, now);
    }

    private void RecordTrustedTransitionSample(
        IServerPlayer player, string reason, TrustedTransitionSample source, EntityPos destination, string verification, string suppressionCode, long now)
    {
        PacketGuardTrustedState.RecordTeleportEvent(
            player, reason,
            source.X, source.Y, source.Z, source.Dimension,
            destination.X, destination.Y, destination.Z, destination.Dimension,
            verification, now);
        PacketGuardTrustedState.RecordSuppression(
            player, suppressionCode,
            $"{reason}: {source.X:0.0},{source.Y:0.0},{source.Z:0.0} d{source.Dimension} -> {destination.X:0.0},{destination.Y:0.0},{destination.Z:0.0} d{destination.Dimension}",
            1750, now);
    }

    private void RemoveServerTeleportTrustHook()
    {
        try
        {
            if (serverTeleportHarmony != null)
            {
                MethodInfo? unpatchSelf = serverTeleportHarmony.GetType().GetMethod("UnpatchSelf", BindingFlags.Instance | BindingFlags.Public);
                unpatchSelf?.Invoke(serverTeleportHarmony, null);
            }
        }
        catch { }
        finally
        {
            serverTeleportHarmony = null;
            serverTeleportHookInstalled = false;
            if (activeServerTeleportHook != null && activeServerTeleportHook.TryGetTarget(out PacketGuardService? service) && ReferenceEquals(service, this))
                activeServerTeleportHook = null;
        }
    }

    // ---------------------------------------------------------------------
    // Shared player sampler
    // ---------------------------------------------------------------------

    private void SamplePlayersShared()
    {
        if (!started) return;

        long now = NowMs();
        sampledPlayers.Clear();
        sampledPlayerPositions.Clear();

        foreach (IPlayer online in api.World.AllOnlinePlayers)
        {
            if (online is not IServerPlayer player || player.Entity == null || string.IsNullOrWhiteSpace(player.PlayerUID)) continue;
            sampledPlayers.Add(player);
            EntityPos? pos = EntityRuntimeCompat.GetPos(player.Entity);
            if (pos != null) sampledPlayerPositions[player.PlayerUID] = pos;
        }

        if (now - lastTrustedTransitionSampleUtc >= 100)
        {
            SampleTrustedTransitions(now);
            lastTrustedTransitionSampleUtc = now;
        }

        if (config.PacketGuardReachEnabled && config.PacketGuardReachPositionHistoryEnabled && now - lastReachHistorySampleUtc >= 100)
        {
            SampleReachPositions(now);
            lastReachHistorySampleUtc = now;
        }

        if (config.PacketGuardMovementEnabled && !movementRuntimeDisabled)
        {
            PacketGuardTpsSnapshot tps = tickHealth.GetSnapshot();
            int targetMs = Math.Clamp(config.PacketGuardMovementSampleMs, 75, 500);
            if (tps.IsCritical) targetMs = Math.Max(targetMs, 125);
            else if (tps.IsDegraded) targetMs = Math.Max(targetMs, 100);

            if (now - lastMovementSampleUtc >= targetMs)
            {
                SafeCheckMovement(now, tps);
                lastMovementSampleUtc = now;
            }
        }
        if (config.PacketGuardFlightReviewEnabled && !flightReviewRuntimeDisabled &&
            now - lastFlightReviewSampleUtc >= config.PacketGuardFlightReviewSampleMs)
        {
            lastFlightReviewSampleUtc = now;
            try { CheckFlightReviews(now, tickHealth.GetSnapshot()); }
            catch (Exception ex)
            {
                flightReviewRuntimeDisabled = true;
                flightReviews.Clear();
                api.Logger.Warning("[ServerEssentials/AntiCheat] Flight review disabled after a contained runtime error; other PacketGuard checks continue. {0}", ex.Message);
            }
        }
    }

    private void CheckFlightReviews(long now, PacketGuardTpsSnapshot tps)
    {
        foreach (IServerPlayer player in sampledPlayers)
        {
            if (!flightReviews.TryGetValue(player.PlayerUID, out PacketFlightReviewWindow? window))
                flightReviews[player.PlayerUID] = window = new PacketFlightReviewWindow();
            bool eligible = !ServerImpulseAllowance.Active(player.PlayerUID,now) && !ServerProtectionExemptions.Has(player.PlayerUID,ServerProtectionScope.Movement,now) && ShouldGuard(player) && player.Entity.Alive && player.Entity.MountedOn == null &&
                !(player.Entity.Controls.Gliding && VanillaGliderAllowance.Equipped(player)) &&
                !tps.IsDegraded && !tps.IsCritical && (!tps.Enabled || tps.Ready) &&
                !PacketGuardTrustedState.IsActive(player.PlayerUID, now, PacketGuardTrustScope.Movement);
            if (player is not ServerPlayer serverPlayer ||
                !PacketMovementEvidence.FreshPosition(api.World.ElapsedMilliseconds,serverPlayer.LastReceivedClientPosition))
                eligible = false;
            if (player.WorldData is ServerWorldPlayerData worldData && (worldData.FreeMove || worldData.NoClip)) eligible = false;
            if (!sampledPlayerPositions.TryGetValue(player.PlayerUID, out EntityPos? position) || !eligible)
            {
                window.Reset();
                continue;
            }
            // Falling is derived from sampled positions by the confirmation helper. Client
            // motion/OnGround/flying flags cannot exempt a physically unsupported hover.
            bool unsupported = HasLoadedUnsupportedFlightVolume(player.Entity, position);
            if (!window.Observe(now, position.Y, unsupported,
                    config.PacketGuardFlightReviewConfirmSeconds * 1000,
                    Math.Max(1000, config.PacketGuardFlightReviewSampleMs * 3),entityId:player.Entity.EntityId,dimension:position.Dimension)) continue;
            string evidence = $"decision=review-only;durationMs={window.DurationMs};rise={window.Rise:0.00};" +
                $"position={position.X:0.00},{position.Y:0.00},{position.Z:0.00};dimension={position.Dimension};" +
                $"supportProbe=loaded-collision-liquid-climb;groundClearance=2;freshClientPositionMs=1500;{tps.Evidence}";
            reportFinding(player, "PACKET_FLIGHT_REVIEW", AntiCheatSeverity.Moderate,
                "The server observed prolonged non-falling survival movement without nearby loaded ground support, liquid or climbable terrain. Review possible flight abuse or legitimate modded physics; this advisory observation does not score risk or trigger automatic punishment.",
                player.PlayerUID, evidence);
            api.Logger.Audit("[PacketGuard/FlightReview] playerUid={0};player={1};{2}",
                player.PlayerUID, PacketGuardTrustedState.CleanDiagnosticText(player.PlayerName, 80), evidence);
        }
    }

    private bool HasLoadedUnsupportedFlightVolume(Entity entity, EntityPos position)
    {
        Cuboidf? body = entity.OriginCollisionBox;
        if (body == null || !PacketReachGeometry.ValidBox(body.X1,body.Y1,body.Z1,body.X2,body.Y2,body.Z2)) return false;
        const double margin = 0.3;
        double minX = position.X + body.X1 - margin;
        double maxX = position.X + body.X2 + margin;
        double minZ = position.Z + body.Z1 - margin;
        double maxZ = position.Z + body.Z2 + margin;
        double feet = position.Y + body.Y1;
        int x0 = (int)Math.Floor(minX), x1 = (int)Math.Floor(maxX);
        int z0 = (int)Math.Floor(minZ), z1 = (int)Math.Floor(maxZ);
        int y0 = (int)Math.Floor(feet - 2), y1 = (int)Math.Floor(position.Y + body.Y2 + margin);
        // Bound probes even if a mod replaces the player with an unusual giant body shape.
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 72) return false;
        var accessor = api.World.BlockAccessor;
        var blockPos = new BlockPos(position.Dimension);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            if (x < 0 || z < 0 || y < 0 || x >= accessor.MapSizeX || z >= accessor.MapSizeZ || y >= accessor.MapSizeY) return false;
            blockPos.Set(x, y, z);
            if (accessor.GetChunkAtBlockPos(blockPos) == null) return false; // Never load/generate terrain for review.
            Block fluid = accessor.GetBlock(blockPos, BlockLayersAccess.Fluid);
            if (fluid.IsLiquid()) return false;
            Block block = accessor.GetBlock(blockPos, BlockLayersAccess.Solid);
            if (block.Climbable) return false;
            if (block.Id == 0) continue;
            Cuboidf[]? boxes;
            try { boxes = block.GetCollisionBoxes(accessor, blockPos); }
            catch { return false; } // Custom collision code is incomplete evidence, not proof of flight.
            if (boxes == null) continue;
            if(boxes.Length>128)return false;
            foreach (Cuboidf box in boxes)
            {
                if(box==null||!PacketReachGeometry.ValidBox(box.X1,box.Y1,box.Z1,box.X2,box.Y2,box.Z2))return false;
                if (x + box.X2 >= minX && x + box.X1 <= maxX && z + box.Z2 >= minZ && z + box.Z1 <= maxZ &&
                    y + box.Y2 >= feet - 2 && y + box.Y1 <= position.Y + body.Y2 + margin)
                    return false; // Nearby support/geometry is deliberately a broad safety exclusion.
            }
        }
        return true;
    }

    // ---------------------------------------------------------------------
    // Shared trusted-transition acquisition
    // ---------------------------------------------------------------------

    private void SampleTrustedTransitions(long now)
    {
        foreach (IServerPlayer player in sampledPlayers)
        {
            if (!sampledPlayerPositions.TryGetValue(player.PlayerUID, out EntityPos? pos)) continue;

            bool mounted = player.Entity.MountedOn != null;
            if (!trustedTransitionSamples.TryGetValue(player.PlayerUID, out TrustedTransitionSample previous))
            {
                UpdatePendingTranslocatorTransition(player, pos, now);
                trustedTransitionSamples[player.PlayerUID] = TrustedTransitionSample.From(pos, mounted);
                continue;
            }

            // Transition grace is independent of whether mounted movement is enforced.
            // Flight review also runs when the main movement detector is disabled.
            if (previous.Mounted != mounted)
            {
                PacketGuardTrustedState.Mark(
                    player,
                    mounted ? "mounted-reposition" : "dismount-reposition",
                    config.PacketGuardMountGraceMs,
                    PacketGuardTrustScope.Movement);
                PacketGuardTrustedState.RecordSuppression(
                    player, "mounted-movement", mounted ? "Mount transition re-baselined movement." : "Dismount transition re-baselined movement.", 1500, now);

                if (movement.TryGetValue(player.PlayerUID, out MovementState? movementState))
                    movementState.WasMounted = mounted;
            }

            if (player.Entity.IsTeleport || player.Entity.Teleporting)
            {
                string engineReason = "engine-teleport";
                if (PacketGuardTrustedState.TryGetActiveReason(player.PlayerUID, now, PacketGuardTrustScope.Movement, out string activeReason) &&
                    IsSpecificTeleportTrustReason(activeReason))
                    engineReason = activeReason;

                RecordTrustedTransitionSample(
                    player, engineReason, previous, pos, TeleportVerificationForReason(engineReason),
                    IsTranslocatorTrustReason(engineReason) ? "trusted-translocator" : "trusted-server-teleport", now);
                PacketGuardTrustedState.Mark(player, engineReason, config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                ClearPendingTranslocatorIfDeparted(player.PlayerUID, pos);
                trustedTransitionSamples[player.PlayerUID] = TrustedTransitionSample.From(pos, mounted);
                continue;
            }

            if (previous.Dimension != pos.Dimension)
            {
                RecordTrustedTransitionSample(player, "dimension-change", previous, pos, TeleportVerificationForReason("dimension-change"), "dimension-change", now);
                PacketGuardTrustedState.Mark(player, "dimension-change", config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                pendingTranslocatorTransitions.Remove(player.PlayerUID);
                reachPositionHistory.Remove(player.PlayerUID);
                trustedTransitionSamples[player.PlayerUID] = TrustedTransitionSample.From(pos, mounted);
                continue;
            }

            double dx = pos.X - previous.X;
            double dy = pos.Y - previous.Y;
            double dz = pos.Z - previous.Z;
            double maxAxis = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            bool largeSnap = maxAxis > config.PacketGuardHardMoveDistance || distance > config.PacketGuardHardMoveDistance * 1.45;

            if (largeSnap)
            {
                if (TryConsumeTh3EssentialsSpawnArrival(player, pos, now))
                {
                    RecordTrustedTransitionSample(player, "th3essentials-spawn", previous, pos, TeleportVerificationForReason("th3essentials-spawn"), "trusted-server-teleport", now);
                    PacketGuardTrustedState.Mark(player, "th3essentials-spawn", config.PacketGuardTh3EssentialsSpawnGraceMs, PacketGuardTrustScope.All);
                }
                else if (IsTrustedTPRunesArrival(player, pos))
                {
                    RecordTrustedTransitionSample(player, "tp-runes", previous, pos, TeleportVerificationForReason("tp-runes"), "trusted-server-teleport", now);
                    PacketGuardTrustedState.Mark(player, "tp-runes", config.PacketGuardTPRunesGraceMs, PacketGuardTrustScope.All);
                }
                else if (IsTrustedLinkedTranslocatorJump(player, previous.X, previous.Y, previous.Z, previous.Dimension, pos))
                {
                    RecordTrustedTransitionSample(player, "vanilla-translocator-linked-direct", previous, pos, TeleportVerificationForReason("vanilla-translocator-linked-direct"), "trusted-translocator", now);
                    PacketGuardTrustedState.Mark(player, "vanilla-translocator-linked-direct", config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                }
                else if (TryConsumePendingTranslocatorTransition(
                             player, previous.X, previous.Y, previous.Z, previous.Dimension, pos, now, out bool targetVerified))
                {
                    string reason = targetVerified ? "vanilla-translocator-linked-target" : "vanilla-translocator-preauthorized";
                    RecordTrustedTransitionSample(player, reason, previous, pos, TeleportVerificationForReason(reason), "trusted-translocator", now);
                    PacketGuardTrustedState.Mark(player, reason, config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                }
                else if (IsTrustedVanillaTranslocatorJump(previous, pos))
                {
                    RecordTrustedTransitionSample(player, "vanilla-translocator", previous, pos, TeleportVerificationForReason("vanilla-translocator"), "trusted-translocator", now);
                    PacketGuardTrustedState.Mark(player, "vanilla-translocator", config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                }
            }

            UpdatePendingTranslocatorTransition(player, pos, now);
            trustedTransitionSamples[player.PlayerUID] = TrustedTransitionSample.From(pos, mounted);
        }
    }

    private void OnPlayerDimensionChanged(IPlayer byPlayer)
    {
        if (byPlayer is not IServerPlayer player || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        long now = NowMs();
        EntityPos? pos = EntityRuntimeCompat.GetPos(player.Entity);
        if (pos != null && trustedTransitionSamples.TryGetValue(player.PlayerUID, out TrustedTransitionSample previous))
            RecordTrustedTransitionSample(player, "dimension-change", previous, pos, TeleportVerificationForReason("dimension-change"), "dimension-change", now);
        else
            PacketGuardTrustedState.RecordSuppression(player, "dimension-change", "Dimension lifecycle event reset PacketGuard baselines.", 1500, now);

        PacketGuardTrustedState.Mark(player, "dimension-change", config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
        pendingTranslocatorTransitions.Remove(player.PlayerUID);
        reachPositionHistory.Remove(player.PlayerUID);
        if (pos != null)
            trustedTransitionSamples[player.PlayerUID] =
                TrustedTransitionSample.From(pos, player.Entity.MountedOn != null);
    }

    // ---------------------------------------------------------------------
    // Movement / teleport packet validation
    // ---------------------------------------------------------------------

    private void SafeCheckMovement(long now, PacketGuardTpsSnapshot tps)
    {
        if (movementRuntimeDisabled) return;
        try
        {
            CheckMovement(now, tps);
        }
        catch (Exception ex)
        {
            // A packet guard must never be able to take down the server. In particular,
            // stale/mixed Vintage Story reference assemblies can turn a field/property ABI
            // change into a JIT-time FieldNotFoundException. Disable only movement protection
            // for this process and keep the rest of Server Essentials/PacketGuard running.
            movementRuntimeDisabled = true;
            long errorUtc = NowMs();
            if (errorUtc - lastMovementRuntimeErrorUtc >= 5000)
            {
                lastMovementRuntimeErrorUtc = errorUtc;
                api.Logger.Error(
                    "[ServerEssentials/AntiCheat] PacketGuard movement protection disabled for this server session after a contained runtime error. Server will continue running. Error: {0}",
                    ex);
            }
        }
    }

    private void CheckMovement(long now, PacketGuardTpsSnapshot tps)
    {
        if (!config.PacketGuardMovementEnabled || movementRuntimeDisabled) return;

        double timingMultiplier = tps.Enabled ? tps.TimingMultiplier : 1.0;
        foreach (IServerPlayer player in sampledPlayers)
        {
            if (!ShouldGuard(player) || ServerProtectionExemptions.Has(player.PlayerUID,ServerProtectionScope.Movement,now))
            {
                movement.Remove(player.PlayerUID);
                continue;
            }

            var entity = player.Entity;
            if (!sampledPlayerPositions.TryGetValue(player.PlayerUID, out EntityPos? pos)) continue;

            bool mounted = entity.MountedOn != null;
            if (!movement.TryGetValue(player.PlayerUID, out var state))
            {
                state = MovementState.From(pos, now, entity.EntityId);
                state.WasMounted = mounted;
                movement[player.PlayerUID] = state;
                continue;
            }

            state.DecayMovementRisk(now, config.PacketGuardMovementRiskEnabled, config.PacketGuardMovementRiskDecayPerSecond);

            // A respawn/reload/mod transition can replace the EntityPlayer instance without the
            // movement sampler seeing the lifecycle event first. Never compare the new entity's
            // position against the old entity's baseline.
            if (state.EntityId != entity.EntityId)
            {
                PacketGuardTrustedState.Mark(player, "player-entity-recreated", config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                PacketGuardTrustedState.RecordSuppression(player, "entity-recreated", $"EntityId {state.EntityId} -> {entity.EntityId}; movement/reach baseline reset.", 1500, now);
                reachPositionHistory.Remove(player.PlayerUID);
                reachViolations.Remove(player.PlayerUID);
                state.NoclipConfirmation.Reset();
                state.PendingNoclipCorrection.Reset();
                state.EntityId = entity.EntityId;
                state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
                state.Accept(pos, now);
                continue;
            }

            bool correctionTimingReliable=!tps.Enabled || (tps.Ready&&!tps.IsDegraded&&!tps.IsCritical);
            if(state.PendingNoclipCorrection.Observe(now,entity.EntityId,pos.Dimension,pos.X,pos.Y,pos.Z,
                config.Enabled && config.PacketGuardNoclipEnabled && config.PacketGuardRejectNoclip && !mounted && correctionTimingReliable))
            {
                state.CorrectionRecovery.Success();
                if(RecordConfirmedNoclipCorrection(player,state,now))continue;
            }
            if(state.PendingNoclipCorrection.ConsumeTimeout())RecordCorrectionFailure(player,state,now);

            // If the sampler did not run for a meaningful interval (chunk load, save stall, heavy
            // GC, debugger pause, etc.), displacement divided across that blind interval is not
            // reliable packet evidence. Re-baseline instead of treating the whole gap as a single normal sample.
            long acceptedSampleGapMs = Math.Max(0, now - state.LastAcceptedUtc);
            int staleMovementGapMs = Math.Max(1500, config.PacketGuardMovementSampleMs * 20);
            if (acceptedSampleGapMs > staleMovementGapMs)
            {
                PacketGuardTrustedState.Mark(player, "movement-sampler-gap", Math.Min(config.PacketGuardEngineTeleportGraceMs, 3000), PacketGuardTrustScope.All);
                PacketGuardTrustedState.RecordSuppression(player, "movement-sampler-gap", $"Sampling gap {acceptedSampleGapMs}ms exceeded {staleMovementGapMs}ms; baseline reset.", 1500, now);
                state.Accept(pos, now);
                continue;
            }

            // Mount-aware hardening. Known mounted entities are no longer blindly trusted: we use
            // generous mod-specific envelopes for Shipwright vessels and Draconis flyers, plus an
            // unknown-mount fallback. Rider noclip checks remain disabled while genuinely mounted
            // because valid seat offsets can overlap complex mount collision geometry.
            if (mounted)
            {
                state.NoclipConfirmation.Reset();
                // The first mounted sample must not be compared against the pre-mount player
                // position. Seat attachment can move the rider several blocks vertically or
                // horizontally in one server update, which is legitimate mount synchronization.
                if (!state.WasMounted)
                {
                    state.NoclipConfirmation.Reset();
                    state.WasMounted = true;
                    state.ResetMountedViolation();
                    PacketGuardTrustedState.Mark(player, "mount-transition", config.PacketGuardMountGraceMs, PacketGuardTrustScope.Movement);
                    PacketGuardTrustedState.RecordSuppression(
                        player, "mount-transition",
                        "decision=rebaseline;reason=first-mounted-sample;pre-mount movement baseline discarded.",
                        1500, now);
                    state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardMountGraceMs);
                    state.Accept(pos, now, markSafe: true);
                    continue;
                }

                if (config.PacketGuardMountProfilesEnabled && TryResolveMountProfile(entity, out MountProfile profile, out Entity? mountEntity))
                {
                    long sampleElapsedMs = Math.Max(1, now - state.LastAcceptedUtc);
                    double mdx = pos.X - state.X;
                    double mdy = pos.Y - state.Y;
                    double mdz = pos.Z - state.Z;
                    double horizontalDistance = Math.Sqrt(mdx * mdx + mdz * mdz);
                    double horizontalSpeed = horizontalDistance * 1000.0 / sampleElapsedMs;
                    state.AddDiagnosticSpeedSample(horizontalSpeed, now, config.PacketGuardDiagnosticSpeedHistorySamples);
                    double verticalSpeed = Math.Abs(mdy) * 1000.0 / sampleElapsedMs;
                    double acceleration = state.ObserveMountedAcceleration(horizontalSpeed, now, profile.MaxAcceleration);
                    double riderDistance = 0;
                    bool detachedFromMount = false;
                    double mountHorizontalSpeed = 0;
                    double riderRelativeSpeed = 0;
                    bool riderDivergedFromMount = false;
                    if (mountEntity != null)
                    {
                        EntityPos? mountPos = EntityRuntimeCompat.GetPos(mountEntity);
                        if (mountPos != null && mountPos.Dimension == pos.Dimension)
                        {
                            double rdx = pos.X - mountPos.X;
                            double rdy = pos.Y - mountPos.Y;
                            double rdz = pos.Z - mountPos.Z;
                            riderDistance = Math.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);
                            detachedFromMount = riderDistance > profile.MaxRiderDistance;
                            if (config.PacketGuardMountRiderCorrelationEnabled &&
                                state.ObserveMountDisplacement(mountEntity.EntityId, mountPos, now, out double mountDistance, out long mountElapsedMs))
                            {
                                mountHorizontalSpeed = mountDistance * 1000.0 / Math.Max(1, mountElapsedMs);
                                double relativeDistance = Math.Abs(horizontalDistance - mountDistance);
                                riderRelativeSpeed = relativeDistance * 1000.0 / Math.Max(1, sampleElapsedMs);
                                riderDivergedFromMount = riderRelativeSpeed > profile.MaxRiderRelativeSpeed;
                            }
                        }
                    }

                    bool mountAccelerationElevated =
                        acceleration > profile.MaxAcceleration &&
                        horizontalSpeed >= profile.MaxHorizontalSpeed * config.PacketGuardMountAccelerationMinSpeedRatio;
                    // Acceleration alone is not an enforcement signal for mounts. Modded mount
                    // physics can legitimately produce huge one-tick deltas while remaining
                    // inside the verified speed/position envelope.
                    // Only direct rider movement envelope violations can enforce on modded mounts.
                    // Rider-to-mount distance/correlation can be noisy because mount seat transforms,
                    // animation and entity update ordering are not guaranteed to stay frame-perfect.
                    // Keep those signals as diagnostics/corroboration, never as standalone rollback causes.
                    bool envelopeExceeded = horizontalSpeed > profile.MaxHorizontalSpeed ||
                                            verticalSpeed > profile.MaxVerticalSpeed;
                    bool unknownMountTelemetryOnly = string.Equals(profile.Code, "unknown-modded-mount", StringComparison.Ordinal) &&
                                                     !config.PacketGuardUnknownMountEnforcementEnabled;

                    if (envelopeExceeded && unknownMountTelemetryOnly)
                    {
                        state.ResetMountedViolation();
                        if (config.PacketGuardDiagnosticSuppressionEnabled)
                        {
                            PacketGuardTrustedState.RecordSuppression(
                                player, "unknown-mount-envelope",
                                $"decision=ignored;reason=unknown-mount-telemetry-only;mount={profile.MountCode};horizontalSpeed={horizontalSpeed:0.00}/{profile.MaxHorizontalSpeed:0.00};verticalSpeed={verticalSpeed:0.00}/{profile.MaxVerticalSpeed:0.00};sampleMs={sampleElapsedMs};history={state.GetDiagnosticSpeedHistory()}",
                                1500, now);
                        }
                        PacketGuardTrustedState.RecordSuppression(player, "mounted-profile", $"{profile.Code}: {profile.MountCode}", 2500, now);
                        state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + Math.Min(config.PacketGuardMountGraceMs, 750));
                        state.Accept(pos, now, markSafe: true);
                        continue;
                    }

                    bool suspicious = envelopeExceeded;
                    if (suspicious)
                    {
                        state.ObserveMountedViolation(now, config.PacketGuardMountProfileConfirmWindowMs);
                        if (state.MountedSuspiciousSamples >= config.PacketGuardMountProfileConfirmSamples)
                        {
                            double mountMovementRisk = AddMovementRisk(state, MovementRiskSignal.Mount, config.PacketGuardMovementRiskMount, now);
                            AntiCheatSeverity mountSeverity = ApplyMovementConfidenceSeverity(AntiCheatSeverity.High, state);
                            string mountEvidence =
                                $"profile={profile.Code};mount={profile.MountCode};horizontalSpeed={horizontalSpeed:0.00};hLimit={profile.MaxHorizontalSpeed:0.00};" +
                                $"verticalSpeed={verticalSpeed:0.00};vLimit={profile.MaxVerticalSpeed:0.00};acceleration={acceleration:0.00};aLimit={profile.MaxAcceleration:0.00};" +
                                $"accelElevated={mountAccelerationElevated};accelMinSpeedRatio={config.PacketGuardMountAccelerationMinSpeedRatio:0.00};" +
                                $"riderDistance={riderDistance:0.00};riderLimit={profile.MaxRiderDistance:0.00};mountSpeed={mountHorizontalSpeed:0.00};" +
                                $"riderRelativeSpeed={riderRelativeSpeed:0.00};relativeLimit={profile.MaxRiderRelativeSpeed:0.00};confirm={state.MountedSuspiciousSamples}/{config.PacketGuardMountProfileConfirmSamples};" +
                                $"sampleMs={sampleElapsedMs};movementRisk={mountMovementRisk:0.0};decision=confirmed;reason=mount-envelope-violation;{MovementDiagnosticEvidence(player, state, "mounted", pos, true)};{MovementConfidenceEvidence(state)};{tps.Evidence}";
                            if (ShouldReportMovementFinding(state, "PACKET_MOUNT_MOVEMENT_HACK", now))
                            reportFinding(
                                player,
                                "PACKET_MOUNT_MOVEMENT_HACK",
                                mountSeverity,
                                "The server observed mounted-player movement outside the verified mount profile and corrected the rider to a previously verified safe position.",
                                player.PlayerUID,
                                mountEvidence);
                            api.Logger.Warning("[ServerEssentials/AntiCheat] PacketGuard mount movement anomaly for {0}: {1}", player.PlayerName, mountEvidence);
                            if (config.PacketGuardMountProfileEnforcementEnabled && !tps.SuppressTimingRejections)
                            {
                                RollbackMovement(player, state, entity, now, "packetguard-mount-correction");
                                continue;
                            }
                        }
                    }
                    else
                    {
                        state.ResetMountedViolation();
                        if (mountAccelerationElevated && config.PacketGuardDiagnosticSuppressionEnabled)
                        {
                            PacketGuardTrustedState.RecordSuppression(
                                player, "mount-acceleration-only",
                                $"decision=ignored;reason=legal-mount-envelope;profile={profile.Code};speed={horizontalSpeed:0.00}/{profile.MaxHorizontalSpeed:0.00};vertical={verticalSpeed:0.00}/{profile.MaxVerticalSpeed:0.00};acceleration={acceleration:0.00}/{profile.MaxAcceleration:0.00};history={state.GetDiagnosticSpeedHistory()}",
                                1500, now);
                        }
                        if ((detachedFromMount || riderDivergedFromMount) && config.PacketGuardDiagnosticSuppressionEnabled)
                        {
                            PacketGuardTrustedState.RecordSuppression(
                                player, "mount-correlation-only",
                                $"decision=ignored;reason=mount-correlation-telemetry-only;profile={profile.Code};riderDistance={riderDistance:0.00}/{profile.MaxRiderDistance:0.00};riderRelativeSpeed={riderRelativeSpeed:0.00}/{profile.MaxRiderRelativeSpeed:0.00};mountSpeed={mountHorizontalSpeed:0.00}",
                                1500, now);
                        }
                    }

                    PacketGuardTrustedState.RecordSuppression(player, "mounted-profile", $"{profile.Code}: {profile.MountCode}", 2500, now);
                    state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + Math.Min(config.PacketGuardMountGraceMs, 750));
                    // A legal mounted position is a valid rollback checkpoint. Previously mounted
                    // samples were accepted without refreshing LastSafe, so a later false positive
                    // could send a rider back to the place they originally mounted minutes earlier.
                    state.Accept(pos, now, markSafe: true);
                    continue;
                }

                if (config.PacketGuardExemptMountedPlayers)
                {
                    PacketGuardTrustedState.Mark(player, "mounted-reposition", config.PacketGuardMountGraceMs, PacketGuardTrustScope.Movement);
                    PacketGuardTrustedState.RecordSuppression(player, "mounted-movement", "Mounted player movement re-baselined using legacy exemption because no mount profile could be resolved.", 1500, now);
                    state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardMountGraceMs);
                    state.Accept(pos, now, markSafe: false);
                    continue;
                }
            }
            if (state.WasMounted)
            {
                state.NoclipConfirmation.Reset();
                state.WasMounted = false;
                PacketGuardTrustedState.Mark(player, "dismount-reposition", config.PacketGuardMountGraceMs, PacketGuardTrustScope.Movement);
                PacketGuardTrustedState.RecordSuppression(player, "mounted-movement", "Dismount reposition grace re-baselined movement.", 1500, now);
                state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardMountGraceMs);
                state.Accept(pos, now);
                continue;
            }

            // Entity.Teleporting is explicitly used by the engine's teleporter path (including
            // vanilla translocators). IsTeleport marks server-authorized entity position snaps.
            // Convert either transient engine flag into a durable local grace window so a later
            // movement sample cannot accuse the player after the engine clears the flag.
            if (entity.IsTeleport || entity.Teleporting)
            {
                string engineReason = "engine-teleport";
                if (PacketGuardTrustedState.TryGetActiveReason(player.PlayerUID, now, PacketGuardTrustScope.Movement, out string activeReason) &&
                    IsSpecificTeleportTrustReason(activeReason))
                    engineReason = activeReason;

                RecordTrustedMovementTransition(
                    player, engineReason, state, pos, TeleportVerificationForReason(engineReason),
                    IsTranslocatorTrustReason(engineReason) ? "trusted-translocator" : "trusted-server-teleport");
                PacketGuardTrustedState.Mark(player, engineReason, config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
                state.Accept(pos, now);
                continue;
            }

            if (PacketGuardTrustedState.IsActive(player.PlayerUID, now, PacketGuardTrustScope.Movement) ||
                state.Dimension != pos.Dimension || now < state.GraceUntilUtc)
            {
                state.NoclipConfirmation.Reset();
                if (state.Dimension != pos.Dimension)
                {
                    RecordTrustedMovementTransition(player, "dimension-change", state, pos, TeleportVerificationForReason("dimension-change"), "dimension-change");
                    PacketGuardTrustedState.Mark(player, "dimension-change", config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                    state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
                }
                state.Accept(pos, now);
                continue;
            }

            double dx = pos.X - state.X;
            double dy = pos.Y - state.Y;
            double dz = pos.Z - state.Z;
            double maxAxis = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // ModMenu 1.2.1 deliberately advances teleport in 64-block per-axis steps. Keep the
            // default well below that but high enough that ordinary survival movement cannot hit
            // it between normal movement samples. Server owners can tune this for unusual movement mods.
            // Scale only the timing-sensitive displacement threshold. The extreme absolute
            // distance gate below deliberately stays on the configured base limit so TPS lag
            // can never grant unlimited movement headroom.
            long displacementElapsedMs = Math.Max(1, now - state.LastAcceptedUtc);
            double samplerScale = Math.Clamp(
                displacementElapsedMs / (double)Math.Max(1, config.PacketGuardMovementSampleMs),
                1.0,
                config.PacketGuardMovementMaxSampleScale);
            double effectiveMoveDistance = config.PacketGuardHardMoveDistance * samplerScale * timingMultiplier;
            bool extremeJump = maxAxis > config.PacketGuardHardMoveDistance * config.PacketGuardMovementExtremeMultiplier ||
                               distance > config.PacketGuardHardMoveDistance * 1.45 * config.PacketGuardMovementExtremeMultiplier;
            bool impossibleJump = extremeJump ||
                                  maxAxis > effectiveMoveDistance ||
                                  distance > effectiveMoveDistance * 1.45;

            // Th3Essentials /spawn compatibility is intentionally narrower than a generic teleport
            // grace. The player must have issued /spawn recently, Th3Essentials must be loaded, and
            // this large snap must land at the player's spawn or the saved world default spawn.
            if (impossibleJump && TryConsumeTh3EssentialsSpawnArrival(player, pos, now))
            {
                RecordTrustedMovementTransition(player, "th3essentials-spawn", state, pos, TeleportVerificationForReason("th3essentials-spawn"), "trusted-server-teleport");
                PacketGuardTrustedState.Mark(player, "th3essentials-spawn", config.PacketGuardTh3EssentialsSpawnGraceMs, PacketGuardTrustScope.All);
                state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardTh3EssentialsSpawnGraceMs);
                state.Accept(pos, now);
                continue;
            }

            // TPRunes normally sets the engine teleport flags caught above. As a fallback,
            // accept the move only when it lands beside the bound beacon encoded on the active
            // rune. This prevents a cleared transient flag from becoming a false hack report.
            if (impossibleJump && IsTrustedTPRunesArrival(player, pos))
            {
                RecordTrustedMovementTransition(player, "tp-runes", state, pos, TeleportVerificationForReason("tp-runes"), "trusted-server-teleport");
                PacketGuardTrustedState.Mark(player, "tp-runes", config.PacketGuardTPRunesGraceMs, PacketGuardTrustScope.All);
                state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardTPRunesGraceMs);
                state.Accept(pos, now);
                continue;
            }

            // A destination near spawn is not teleport authorization. Server API hooks,
            // lifecycle events and explicitly registered transitions establish trust instead.

            // Vanilla static translocators expose their actual linked destination. Verify the
            // player's last accepted source position against that link directly before relying on
            // short-lived warm-up bookkeeping. This survives timing differences where tpingEntities
            // or Entity.Teleporting clears between PacketGuard samples, but it cannot authorize an
            // arbitrary destination: the observed arrival must match the source translocator's link.
            if (impossibleJump && IsTrustedLinkedTranslocatorJump(
                    player, state.X, state.Y, state.Z, state.Dimension, pos))
            {
                RecordTrustedMovementTransition(player, "vanilla-translocator-linked-direct", state, pos, TeleportVerificationForReason("vanilla-translocator-linked-direct"), "trusted-translocator");
                PacketGuardTrustedState.Mark(player, "vanilla-translocator-linked-direct", config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
                state.Accept(pos, now);
                continue;
            }

            // Vanilla static translocators warm up for several seconds before calling TeleportTo.
            // During that warm-up the source block entity tracks the player's entity id and exposes
            // its linked target. PacketGuard records that source-side intent ahead of time, then
            // consumes it exactly once when the matching large jump arrives. This remains a second
            // source of proof when the direct source->target lookup cannot be completed.
            if (impossibleJump && TryConsumePendingTranslocatorTransition(
                    player, state.X, state.Y, state.Z, state.Dimension, pos, now, out bool linkedTargetVerified))
            {
                string translocatorReason = linkedTargetVerified ? "vanilla-translocator-linked-target" : "vanilla-translocator-preauthorized";
                RecordTrustedMovementTransition(player, translocatorReason, state, pos, TeleportVerificationForReason(translocatorReason), "trusted-translocator");
                PacketGuardTrustedState.Mark(
                    player,
                    translocatorReason,
                    config.PacketGuardEngineTeleportGraceMs,
                    PacketGuardTrustScope.All);
                state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
                state.Accept(pos, now);
                continue;
            }

            // Compatibility fallback for older/modified translocator implementations where the
            // source warm-up state is not reflectable. This still requires both endpoints to be
            // physically beside a translocator and is intentionally secondary to the linked-target path.
            if (impossibleJump && IsTrustedVanillaTranslocatorJump(state, pos))
            {
                RecordTrustedMovementTransition(player, "vanilla-translocator", state, pos, TeleportVerificationForReason("vanilla-translocator"), "trusted-translocator");
                PacketGuardTrustedState.Mark(player, "vanilla-translocator", config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
                state.GraceUntilUtc = Math.Max(state.GraceUntilUtc, now + config.PacketGuardEngineTeleportGraceMs);
                state.Accept(pos, now);
                continue;
            }

            // Generic reconciliation for teleport-sized transitions whose server/mod teleport flag
            // was not visible to PacketGuard. Ordinary movement/speed cheats remain below this
            // gate and are still handled by the normal hard-move/speed/acceleration detectors.
            // A single teleport-sized jump is re-baselined and logged as suppression telemetry;
            // only rapid repetition becomes a review finding, and it is never auto-rolled back here.
            if (impossibleJump &&
                config.PacketGuardUnverifiedTeleportReconciliationEnabled &&
                distance >= config.PacketGuardUnverifiedTeleportMinDistance)
            {
                int repeated = state.RegisterUnverifiedTeleport(now, config.PacketGuardUnverifiedTeleportRepeatWindowMs);
                string teleportEvidence =
                    $"dx={dx:0.00};dy={dy:0.00};dz={dz:0.00};distance={distance:0.00};maxAxis={maxAxis:0.00};" +
                    $"reconcileMin={config.PacketGuardUnverifiedTeleportMinDistance:0.00};repeat={repeated}/{config.PacketGuardUnverifiedTeleportRepeatCount};" +
                    $"repeatWindowMs={config.PacketGuardUnverifiedTeleportRepeatWindowMs};elapsedMs={displacementElapsedMs};" +
                    $"pingMs={Math.Max(0, player.Ping * 1000):0};decision=rebaseline;reason=unverified-teleport-sized-transition;{tps.Evidence}";

                PacketGuardTrustedState.RecordSuppression(
                    player, "unverified-teleport-reconcile", teleportEvidence, 1500, now);

                if (repeated >= config.PacketGuardUnverifiedTeleportRepeatCount &&
                    ShouldReportMovementFinding(state, "PACKET_UNVERIFIED_TELEPORT_PATTERN", now))
                {
                    reportFinding(
                        player,
                        "PACKET_UNVERIFIED_TELEPORT_PATTERN",
                        AntiCheatSeverity.Moderate,
                        "The server observed repeated teleport-sized position changes without a matching trusted teleport marker. This is compatibility telemetry only: the transitions were re-baselined and do not contribute to punishment risk because server/mod teleport metadata may be incomplete.",
                        player.PlayerUID,
                        teleportEvidence);
                }

                state.Accept(pos, now, markSafe: true);
                continue;
            }

            if (!impossibleJump)
            {
                if (state.SuspiciousCount > 0)
                {
                    PacketGuardTrustedState.RecordSuppression(
                        player,
                        "movement-reconciliation-spike",
                        $"Discarded {state.SuspiciousCount} transient impossible movement sample(s); position returned to a valid displacement before confirmation.",
                        1500, now);
                }

                long sampleElapsedMs = Math.Max(1, now - state.LastAcceptedUtc);
                double horizontalDistance = Math.Sqrt(dx * dx + dz * dz);
                double instantaneousHorizontalSpeed = horizontalDistance * 1000.0 / sampleElapsedMs;
                state.AddDiagnosticSpeedSample(instantaneousHorizontalSpeed, now, config.PacketGuardDiagnosticSpeedHistorySamples);
                bool gliding = state.ObserveGliding(VanillaGliderAllowance.Equipped(player), entity.Controls.Gliding,
                    now, entity.EntityId, pos.Dimension);
                (string movementMode, double movementSpeedLimit) = ResolveMovementSpeedLimit(entity, dy, gliding);
                bool sampleIsFast = config.PacketGuardSpeedHackEnabled &&
                                    instantaneousHorizontalSpeed > movementSpeedLimit;

                // Short-step endpoint validation closes the gap where a noclip client advances
                // through a wall in increments smaller than the path-sampling minimum distance.
                bool collisionTimingReliable=PacketMovementEvidence.ReliableCollisionTiming(tps.Enabled,tps.Ready,tps.IsDegraded,tps.IsCritical,
                    player is ServerPlayer observedPlayer && PacketMovementEvidence.FreshPosition(api.World.ElapsedMilliseconds,observedPlayer.LastReceivedClientPosition));
                if(!collisionTimingReliable)state.NoclipConfirmation.Reset();
                bool endpointPenetrating = false;
                double endpointPenetration = 0;
                double endpointNetMovement = 0;
                string endpointBlock = "";
                if (collisionTimingReliable && config.PacketGuardNoclipEnabled && config.PacketGuardNoclipEndpointEnabled &&
                    EntityBodyIntersectsCollision(entity, pos.X, pos.Y, pos.Z, pos.Dimension, out endpointBlock, out endpointPenetration) &&
                    endpointPenetration >= config.PacketGuardNoclipEndpointMinPenetrationDepth)
                {
                    endpointPenetrating = true;
                    endpointNetMovement = state.ObserveEndpointPenetration(
                        state.X, state.Z, pos.X, pos.Z, pos.Dimension, now);
                }
                else
                {
                    state.ResetEndpointPenetration();
                }

                if (endpointPenetrating &&
                    state.EndpointCollisionSamples >= config.PacketGuardNoclipEndpointConfirmSamples &&
                    endpointNetMovement < config.PacketGuardNoclipEndpointMinNetMovement &&
                    config.PacketGuardDiagnosticSuppressionEnabled)
                {
                    PacketGuardTrustedState.RecordSuppression(
                        player, "noclip-stationary-overlap",
                        $"decision=ignored;reason=insufficient-net-movement;block={endpointBlock};penetration={endpointPenetration:0.000};samples={state.EndpointCollisionSamples};netMovement={endpointNetMovement:0.000}/{config.PacketGuardNoclipEndpointMinNetMovement:0.000};history={state.GetDiagnosticSpeedHistory()}",
                        1250, now);
                }

                if (endpointPenetrating &&
                    state.EndpointCollisionSamples >= config.PacketGuardNoclipEndpointConfirmSamples &&
                    endpointNetMovement >= config.PacketGuardNoclipEndpointMinNetMovement && now-state.EndpointCollisionSinceUtc>=500)
                {
                    var terrain = new NoclipTerrainEvidence();
                    EntityBodyIntersectsCollision(entity,pos.X,pos.Y,pos.Z,pos.Dimension,out _,out _,out bool terrainKnown,terrain);
                    bool correctionConfirmed=terrainKnown && terrain.CorrectionCompatible && state.NoclipConfirmation.Observe(now);
                    if(!terrainKnown || !terrain.CorrectionCompatible)state.NoclipConfirmation.Reset();
                    double endpointMovementRisk = state.MovementRiskScore;
                    string endpointEvidence =
                        $"mode={movementMode};distance={distance:0.00};horizontal={horizontalDistance:0.00};sampleMs={sampleElapsedMs};" +
                        $"endpointPenetration={endpointPenetration:0.000};minPenetration={config.PacketGuardNoclipEndpointMinPenetrationDepth:0.000};" +
                        $"endpointConfirm={state.EndpointCollisionSamples}/{config.PacketGuardNoclipEndpointConfirmSamples};endpointSpanMs={Math.Max(0,now-state.EndpointCollisionSinceUtc)};minimumSpanMs=500;" +
                        $"endpointNetMovement={endpointNetMovement:0.000};minNetMovement={config.PacketGuardNoclipEndpointMinNetMovement:0.000};block={endpointBlock};{terrain.Describe()};" +
                        $"noclipEnforcementEligible=False;automaticPunishment=False;correctionConfirmed={correctionConfirmed};correctionConfigured={config.PacketGuardRejectNoclip};movementRisk={endpointMovementRisk:0.0};decision=confirmed;reason=endpoint-penetration-with-movement;{MovementDiagnosticEvidence(player, state, movementMode, pos, mounted)};{MovementConfidenceEvidence(state)};{tps.Evidence}";

                    if (ShouldReportMovementFinding(state, "PACKET_NOCLIP", now))
                    reportFinding(
                        player,
                        "PACKET_NOCLIP",
                        AntiCheatSeverity.Moderate,
                        "The server observed player-body penetration into collision geometry. Review terrain evidence; correction requires repeated compatible observations. This finding cannot automatically punish.",
                        player.PlayerUID,
                        endpointEvidence);

                    api.Logger.Warning(
                        "[ServerEssentials/AntiCheat] PacketGuard endpoint noclip anomaly for {0}: {1}",
                        player.PlayerName,
                        endpointEvidence);

                    if (config.PacketGuardRejectNoclip && correctionConfirmed)
                    {
                        CorrectNoclip(player,state,entity,now,"packetguard-noclip-endpoint-correction");
                    }
                    else
                    {
                        state.Accept(pos, now, markSafe: false);
                    }
                    continue;
                }

                // Noclip is independent of raw speed. A cheat can deliberately move slowly through
                // a wall, so test the intermediate server-observed body path against real collision
                // boxes before accepting this sample as a new safe location.
                if (collisionTimingReliable && config.PacketGuardNoclipEnabled &&
                    TryDetectNoclipPath(entity, state.X, state.Y, state.Z, pos.X, pos.Y, pos.Z, pos.Dimension,
                        out int collisionSamples, out double maxPenetrationDepth, out string collisionBlock))
                {
                    bool compatible=AntiCheatObservationPolicy.HasValue(collisionBlock,"geometryCorrectionCompatible","True");
                    bool correctionConfirmed=compatible && state.NoclipConfirmation.Observe(now);
                    if(!compatible)state.NoclipConfirmation.Reset();
                    double pathMovementRisk = state.MovementRiskScore;
                    string noclipEvidence =
                        $"mode={movementMode};distance={distance:0.00};horizontal={horizontalDistance:0.00};sampleMs={sampleElapsedMs};" +
                        $"collisionSamples={collisionSamples};required={config.PacketGuardNoclipRequiredCollisionSamples};" +
                        $"maxPenetration={maxPenetrationDepth:0.000};minPenetration={config.PacketGuardNoclipMinPenetrationDepth:0.000};" +
                        $"sampleStep={config.PacketGuardNoclipSampleStep:0.00};block={collisionBlock};noclipEnforcementEligible=False;automaticPunishment=False;correctionConfirmed={correctionConfirmed};correctionConfigured={config.PacketGuardRejectNoclip};movementRisk={pathMovementRisk:0.0};decision=confirmed;reason=collision-path-crossing;{MovementDiagnosticEvidence(player, state, movementMode, pos, mounted)};{MovementConfidenceEvidence(state)};{tps.Evidence}";

                    if (ShouldReportMovementFinding(state, "PACKET_NOCLIP", now))
                    reportFinding(
                        player,
                        "PACKET_NOCLIP",
                        AntiCheatSeverity.Moderate,
                        "The server observed a fully loaded movement path crossing solid collision geometry. Review modded movement and geometry compatibility; correction follows the configured policy.",
                        player.PlayerUID,
                        noclipEvidence);

                    api.Logger.Warning(
                        "[ServerEssentials/AntiCheat] PacketGuard noclip anomaly for {0}: {1}",
                        player.PlayerName,
                        noclipEvidence);

                    if (config.PacketGuardRejectNoclip && correctionConfirmed)
                    {
                        CorrectNoclip(player,state,entity,now,"packetguard-noclip-correction");
                    }
                    else
                    {
                        state.Accept(pos, now, markSafe: false);
                    }
                    continue;
                }

                // Server-observed damage impulses relax only timing-based speed/acceleration.
                // Hard displacement and collision checks above retain their independent bounds.
                if(config.PacketGuardServerImpulseAllowanceEnabled && ServerImpulseAllowance.Active(player.PlayerUID,now))
                {
                    state.ResetAnalysis(now);state.Accept(pos,now,markSafe:false);continue;
                }

                // Acceleration is evaluated independently of rolling speed. A hacked client can
                // ramp rapidly while keeping each speed window just under the sustained limit.
                double positiveHorizontalAcceleration = state.ObserveAcceleration(
                    instantaneousHorizontalSpeed, now,
                    config.PacketGuardAccelerationMinSpeed,
                    gliding && instantaneousHorizontalSpeed <= movementSpeedLimit ? double.MaxValue : config.PacketGuardMaxHorizontalAcceleration,
                    config.PacketGuardAccelerationConfirmWindowMs,
                    config.PacketGuardAccelerationSampleMinMs,
                    config.PacketGuardAccelerationSmoothingFactor,
                    out int accelerationConfirmSamples,
                    out double smoothedHorizontalSpeed);

                if (config.PacketGuardAccelerationEnabled &&
                    smoothedHorizontalSpeed >= config.PacketGuardAccelerationMinSpeed &&
                    positiveHorizontalAcceleration > config.PacketGuardMaxHorizontalAcceleration &&
                    accelerationConfirmSamples > 0 &&
                    accelerationConfirmSamples < config.PacketGuardAccelerationConfirmSamples &&
                    config.PacketGuardDiagnosticSuppressionEnabled)
                {
                    PacketGuardTrustedState.RecordSuppression(
                        player, "acceleration-awaiting-confirmation",
                        $"decision=deferred;reason=confirmation-pending;acceleration={positiveHorizontalAcceleration:0.00}/{config.PacketGuardMaxHorizontalAcceleration:0.00};smoothedSpeed={smoothedHorizontalSpeed:0.00};confirm={accelerationConfirmSamples}/{config.PacketGuardAccelerationConfirmSamples};history={state.GetDiagnosticSpeedHistory()}",
                        1000, now);
                }

                if (config.PacketGuardAccelerationEnabled &&
                    smoothedHorizontalSpeed >= config.PacketGuardAccelerationMinSpeed &&
                    positiveHorizontalAcceleration > config.PacketGuardMaxHorizontalAcceleration &&
                    accelerationConfirmSamples >= config.PacketGuardAccelerationConfirmSamples)
                {
                    bool accelerationTimingRejectionDeferred = tps.SuppressTimingRejections;
                    if (accelerationTimingRejectionDeferred && config.PacketGuardDiagnosticSuppressionEnabled)
                    {
                        PacketGuardTrustedState.RecordSuppression(
                            player, "acceleration-tps-deferred",
                            $"decision=deferred;reason=critical-tps;acceleration={positiveHorizontalAcceleration:0.00};smoothedSpeed={smoothedHorizontalSpeed:0.00};history={state.GetDiagnosticSpeedHistory()};{tps.Evidence}",
                            1500, now);
                    }
                    double accelerationMovementRisk = AddMovementRisk(state, MovementRiskSignal.Acceleration, config.PacketGuardMovementRiskAcceleration, now);
                    AntiCheatSeverity accelerationSeverity = accelerationTimingRejectionDeferred ? AntiCheatSeverity.High : ApplyMovementConfidenceSeverity(AntiCheatSeverity.Critical, state);
                    string accelerationEvidence =
                        $"mode={movementMode};horizontalAcceleration={positiveHorizontalAcceleration:0.00};limit={config.PacketGuardMaxHorizontalAcceleration:0.00};" +
                        $"speed={instantaneousHorizontalSpeed:0.00};smoothedSpeed={smoothedHorizontalSpeed:0.00};minSpeed={config.PacketGuardAccelerationMinSpeed:0.00};" +
                        $"confirmSamples={accelerationConfirmSamples}/{config.PacketGuardAccelerationConfirmSamples};" +
                        $"confirmWindowMs={config.PacketGuardAccelerationConfirmWindowMs};accelSampleMinMs={config.PacketGuardAccelerationSampleMinMs};sampleMs={sampleElapsedMs};" +
                        $"pingMs={Math.Max(0, player.Ping * 1000):0};movementRisk={accelerationMovementRisk:0.0};decision={(accelerationTimingRejectionDeferred ? "deferred" : "confirmed")};reason={(accelerationTimingRejectionDeferred ? "critical-tps" : "repeated-smoothed-acceleration")};{MovementDiagnosticEvidence(player, state, movementMode, pos, mounted)};{MovementConfidenceEvidence(state)};{tps.Evidence}";

                    if (ShouldReportMovementFinding(state, "PACKET_ACCELERATION_HACK", now))
                    reportFinding(
                        player,
                        "PACKET_ACCELERATION_HACK",
                        accelerationSeverity,
                        accelerationTimingRejectionDeferred
                            ? "The server observed repeated impossible horizontal acceleration, but rollback was deferred because server TPS was critically degraded."
                            : "The server observed repeated impossible horizontal acceleration and rolled the player back to a previously verified safe position.",
                        player.PlayerUID,
                        accelerationEvidence);

                    api.Logger.Warning(
                        "[ServerEssentials/AntiCheat] PacketGuard acceleration anomaly for {0}: {1}",
                        player.PlayerName,
                        accelerationEvidence);

                    if (config.PacketGuardRejectAccelerationHack && !accelerationTimingRejectionDeferred)
                    {
                        RollbackMovement(player, state, entity, now, "packetguard-acceleration-correction");
                        continue;
                    }
                }

                state.AddSpeedSample(horizontalDistance, now);
                if (config.PacketGuardSpeedHackEnabled && state.TryCloseSpeedWindow(
                        now,
                        config.PacketGuardSpeedWindowMs,
                        movementSpeedLimit,
                        config.PacketGuardSpeedConfirmWindows,
                        out double observedHorizontalSpeed,
                        out int confirmedWindows))
                {
                    bool speedTimingRejectionDeferred = tps.SuppressTimingRejections;
                    bool speedEscalationEligible = !tps.Enabled || (tps.Ready && !tps.IsDegraded);
                    double speedMovementRisk = speedEscalationEligible
                        ? AddMovementRisk(state, MovementRiskSignal.Speed, config.PacketGuardMovementRiskSpeed, now)
                        : state.MovementRiskScore;
                    AntiCheatSeverity speedSeverity = speedTimingRejectionDeferred ? AntiCheatSeverity.High : ApplyMovementConfidenceSeverity(AntiCheatSeverity.Critical, state);
                    string speedEvidence =
                        $"mode={movementMode};horizontalSpeed={observedHorizontalSpeed:0.00};limit={movementSpeedLimit:0.00};" +
                        $"windowMs={config.PacketGuardSpeedWindowMs};confirmWindows={confirmedWindows}/{config.PacketGuardSpeedConfirmWindows};" +
                        $"instantaneous={instantaneousHorizontalSpeed:0.00};pingMs={Math.Max(0, player.Ping * 1000):0};movementRisk={speedMovementRisk:0.0};" +
                        $"speedEscalationEligible={speedEscalationEligible};{MovementConfidenceEvidence(state)};{tps.Evidence}";

                    if (ShouldReportMovementFinding(state, "PACKET_SPEED_HACK", now))
                    reportFinding(
                        player,
                        "PACKET_SPEED_HACK",
                        speedSeverity,
                        speedTimingRejectionDeferred
                            ? "The server observed sustained impossible horizontal movement speed, but rollback was deferred because server TPS was critically degraded."
                            : "The server observed sustained impossible horizontal movement speed and rolled the player back to the last safe position.",
                        player.PlayerUID,
                        speedEvidence);

                    api.Logger.Warning(
                        "[ServerEssentials/AntiCheat] PacketGuard speed anomaly for {0}: {1}",
                        player.PlayerName,
                        speedEvidence);

                    if (config.PacketGuardRejectSpeedHack && !speedTimingRejectionDeferred)
                    {
                        RollbackMovement(player, state, entity, now, "packetguard-speed-correction");
                        continue;
                    }
                }

                bool accelerationSuspicious = accelerationConfirmSamples > 0;
                state.Accept(pos, now, markSafe: !sampleIsFast && !accelerationSuspicious && !endpointPenetrating);
                if (now - state.LastViolationUtc > 30_000) state.ViolationCount = 0;
                continue;
            }

            // Accuracy layer: never turn one sampled position into punishment-grade movement evidence.
            // Even an extreme displacement can be a one-tick reconciliation artifact, a late teleport
            // flag, or a server/mod position correction. Keep the last accepted baseline fixed and require
            // the impossible displacement to persist across actual sampler observations. Under critical TPS
            // degradation require one additional observation because packet ordering becomes less reliable.
            int effectiveConfirmWindowMs = Math.Clamp(
                (int)Math.Round(config.PacketGuardMovementConfirmWindowMs * timingMultiplier),
                config.PacketGuardMovementConfirmWindowMs,
                10_000);
            int requiredConfirmSamples = Math.Clamp(
                Math.Max(2, config.PacketGuardMovementConfirmSamples) + (tps.SuppressTimingRejections ? 1 : 0),
                2,
                4);

            if (state.SuspiciousCount == 0 || now - state.SuspiciousSinceUtc > effectiveConfirmWindowMs)
            {
                state.SuspiciousCount = 1;
                state.SuspiciousSinceUtc = now;
            }
            else
            {
                state.SuspiciousCount++;
            }

            state.SuspiciousLastUtc = now;
            if (state.SuspiciousCount < requiredConfirmSamples)
            {
                continue;
            }

            if (now - state.LastViolationUtc > 10_000) state.ViolationCount = 0;
            state.ViolationCount++;
            state.LastViolationUtc = now;

            bool timingRejectionDeferred = tps.SuppressTimingRejections && !extremeJump;
            AntiCheatSeverity severity = state.ViolationCount >= 2
                ? AntiCheatSeverity.Critical
                : AntiCheatSeverity.High;
            if (timingRejectionDeferred && severity == AntiCheatSeverity.Critical) severity = AntiCheatSeverity.High;
            else if (timingRejectionDeferred && severity == AntiCheatSeverity.High) severity = AntiCheatSeverity.Moderate;

            double movementRisk = AddMovementRisk(state, MovementRiskSignal.HardMove, config.PacketGuardMovementRiskHardMove, now);
            severity = ApplyMovementConfidenceSeverity(severity, state);
            string evidence =
                $"dx={dx:0.00};dy={dy:0.00};dz={dz:0.00};distance={distance:0.00};" +
                $"maxAxis={maxAxis:0.00};baseLimit={config.PacketGuardHardMoveDistance:0.00};effectiveLimit={effectiveMoveDistance:0.00};sampleScale={samplerScale:0.00};elapsedMs={displacementElapsedMs};" +
                $"confirmSamples={state.SuspiciousCount}/{requiredConfirmSamples};baseConfirmSamples={config.PacketGuardMovementConfirmSamples};confirmWindowMs={effectiveConfirmWindowMs};extreme={extremeJump};" +
                $"pingMs={Math.Max(0, player.Ping * 1000):0};violations={state.ViolationCount};movementRisk={movementRisk:0.0};{MovementConfidenceEvidence(state)};{tps.Evidence}";

            if (ShouldReportMovementFinding(state, "PACKET_UNAUTHORIZED_MOVEMENT", now))
            reportFinding(
                player,
                "PACKET_UNAUTHORIZED_MOVEMENT",
                severity,
                timingRejectionDeferred
                    ? "The server observed an impossible survival-mode position change, but automatic rollback was deferred because server TPS was critically degraded."
                    : "The server rejected an impossible survival-mode position change that was not marked as a legitimate server teleport.",
                player.PlayerUID,
                evidence);

            api.Logger.Warning(
                "[ServerEssentials/AntiCheat] PacketGuard movement anomaly for {0}: {1}",
                player.PlayerName,
                evidence);

            if (config.PacketGuardRejectUnauthorizedMovement && !timingRejectionDeferred)
            {
                RollbackMovement(player, state, entity, now, "packetguard-correction");
            }
            else
            {
                // Log-only mode accepts the position as the new baseline so the same movement
                // does not generate a finding every sample.
                state.Accept(pos, now);
            }
        }
    }

    private double AddMovementRisk(MovementState state, MovementRiskSignal signal, double amount, long now)
    {
        double score = state.AddMovementRisk(
            config.PacketGuardMovementRiskEnabled, signal, amount, now,
            config.PacketGuardMovementConfidenceCorrelationWindowMs,
            config.PacketGuardMovementConfidenceCorrelationBonus);

        if (config.PacketGuardMovementRiskEnabled && state.LastRollbackUtc > 0 &&
            now - state.LastRollbackUtc <= config.PacketGuardRollbackAntiLoopWindowMs &&
            !state.RollbackResistanceCounted)
        {
            state.RollbackResistanceCounted = true;
            score = state.AddMovementRisk(
                true, MovementRiskSignal.RollbackResistance, config.PacketGuardRollbackAntiLoopRisk, now,
                config.PacketGuardMovementConfidenceCorrelationWindowMs,
                config.PacketGuardMovementConfidenceCorrelationBonus);
        }

        return score;
    }

    private bool ShouldReportMovementFinding(MovementState state, string code, long now)
    {
        return state.ShouldReportFinding(code, now, config.PacketGuardMovementDuplicateFindingCooldownMs);
    }

    private string MovementConfidenceEvidence(MovementState state)
    {
        string band = state.GetMovementConfidenceBand(
            config.PacketGuardMovementConfidenceGuardedThreshold,
            config.PacketGuardMovementConfidenceHighThreshold,
            config.PacketGuardMovementConfidenceCriticalThreshold);
        return $"movementConfidence={state.MovementRiskScore:0.0};confidenceBand={band};confidenceSignals={state.GetMovementConfidenceSignals()};rollbackLoop={state.RollbackLoopCount}";
    }

    private string MovementDiagnosticEvidence(IServerPlayer player, MovementState state, string movementMode, EntityPos pos, bool mounted)
    {
        if (!config.PacketGuardDiagnosticContextEnabled) return "diagnostics=off";
        return $"diagnostics=on;modeContext={movementMode};mounted={mounted};pingMs={Math.Max(0, player.Ping * 1000):0};" +
               $"position={pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0};dimension={pos.Dimension};" +
               $"acceptedAgeMs={Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.LastAcceptedUtc)};" +
               $"speedHistory={state.GetDiagnosticSpeedHistory()}";
    }

    private AntiCheatSeverity ApplyMovementConfidenceSeverity(AntiCheatSeverity severity, MovementState state)
    {
        if (!config.PacketGuardMovementRiskEnabled || !config.PacketGuardMovementConfidenceEscalatesSeverity) return severity;
        double score = state.MovementRiskScore;
        if (score >= config.PacketGuardMovementConfidenceCriticalThreshold) return AntiCheatSeverity.Critical;
        if (score >= config.PacketGuardMovementConfidenceHighThreshold && severity == AntiCheatSeverity.Moderate) return AntiCheatSeverity.High;
        return severity;
    }

    private readonly record struct MountProfile(
        string Code, string MountCode, double MaxHorizontalSpeed, double MaxVerticalSpeed,
        double MaxAcceleration, double MaxRiderDistance, double MaxRiderRelativeSpeed);

    private bool TryResolveMountProfile(EntityPlayer rider, out MountProfile profile, out Entity? mountEntity)
    {
        profile = default;
        object? mountedOn = rider.MountedOn;
        mountEntity = ResolveMountedEntity(mountedOn);
        string mountCode = mountEntity?.Code?.ToString() ?? ResolveMountedCode(mountedOn) ?? string.Empty;
        mountCode ??= string.Empty;
        string lower = mountCode.ToLowerInvariant();

        if (lower.Contains("shipwright:"))
        {
            profile = new MountProfile(
                "shipwright-vessel", mountCode,
                config.PacketGuardShipwrightMaxHorizontalSpeed,
                config.PacketGuardShipwrightMaxVerticalSpeed,
                config.PacketGuardShipwrightMaxAcceleration,
                config.PacketGuardShipwrightMaxRiderDistance,
                config.PacketGuardShipwrightMaxRiderRelativeSpeed);
            return true;
        }

        if (lower.Contains("draconis:") || lower.Contains("jaunt:"))
        {
            profile = new MountProfile(
                "draconis-mount", mountCode,
                config.PacketGuardDraconisMaxHorizontalSpeed,
                config.PacketGuardDraconisMaxVerticalSpeed,
                config.PacketGuardDraconisMaxAcceleration,
                config.PacketGuardDraconisMaxRiderDistance,
                config.PacketGuardDraconisMaxRiderRelativeSpeed);
            return true;
        }

        if (mountedOn != null)
        {
            profile = new MountProfile(
                "unknown-modded-mount", string.IsNullOrWhiteSpace(mountCode) ? mountedOn.GetType().FullName ?? "unknown" : mountCode,
                config.PacketGuardUnknownMountMaxHorizontalSpeed,
                config.PacketGuardUnknownMountMaxVerticalSpeed,
                config.PacketGuardUnknownMountMaxAcceleration,
                config.PacketGuardUnknownMountMaxRiderDistance,
                config.PacketGuardUnknownMountMaxRiderRelativeSpeed);
            return true;
        }

        return false;
    }

    private static Entity? ResolveMountedEntity(object? mountedOn)
    {
        if (mountedOn == null) return null;
        if (mountedOn is Entity direct) return direct;
        Type t = mountedOn.GetType();
        foreach (string name in new[] { "Entity", "MountableEntity", "MountedEntity", "Carrier", "OwnerEntity" })
        {
            try
            {
                PropertyInfo? p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p?.GetValue(mountedOn) is Entity e) return e;
                FieldInfo? f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f?.GetValue(mountedOn) is Entity fe) return fe;
            }
            catch { }
        }
        return null;
    }

    private static string ResolveMountedCode(object? mountedOn)
    {
        if (mountedOn == null) return "";
        Type t = mountedOn.GetType();
        foreach (string name in new[] { "Entity", "MountableEntity", "MountedEntity", "Carrier", "OwnerEntity" })
        {
            try
            {
                object? value = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mountedOn) ??
                                t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mountedOn);
                if (value is Entity e) return e.Code?.ToString() ?? "";
            }
            catch { }
        }
        return t.FullName ?? t.Name;
    }

    private (string Mode, double Limit) ResolveMovementSpeedLimit(Entity entity, double deltaY, bool gliding = false)
    {
        if (gliding) return ("vanilla-glider", VanillaGliderAllowance.HorizontalCeiling);
        if (!config.PacketGuardMovementModeLimitsEnabled)
            return ("default", config.PacketGuardMaxHorizontalSpeed);

        try
        {
            if (entity.Swimming || entity.FeetInLiquid)
                return ("swimming", config.PacketGuardSwimmingMaxHorizontalSpeed);
        }
        catch
        {
            // Runtime compatibility fallback: speed validation still works using the generic envelope.
        }

        // Avoid ABI-sensitive OnGround access here. A meaningful vertical component is enough to
        // choose the deliberately generous airborne envelope; ground/stair motion keeps ground limits.
        if (Math.Abs(deltaY) > 0.30)
            return ("airborne", config.PacketGuardAirborneMaxHorizontalSpeed);

        return ("ground", config.PacketGuardGroundMaxHorizontalSpeed);
    }

    private bool TryDetectNoclipPath(
        Entity entity,
        double fromX, double fromY, double fromZ,
        double toX, double toY, double toZ,
        int dimension,
        out int collisionSamples,
        out double maxPenetrationDepth,
        out string firstBlock)
    {
        collisionSamples = 0;
        maxPenetrationDepth = 0;
        firstBlock = "";
        if (!config.PacketGuardNoclipEnabled) return false;

        double dx = toX - fromX;
        double dy = toY - fromY;
        double dz = toZ - fromZ;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (distance < config.PacketGuardNoclipMinPathDistance) return false;

        if(EntityBodyIntersectsCollision(entity,fromX,fromY,fromZ,dimension,out _,out _,out bool fromKnown)||!fromKnown)return false;
        if(EntityBodyIntersectsCollision(entity,toX,toY,toZ,dimension,out _,out _,out bool toKnown)||!toKnown)return false;
        var terrain = new NoclipTerrainEvidence();
        int steps = Math.Clamp((int)Math.Ceiling(distance / config.PacketGuardNoclipSampleStep), 2, 96);
        for (int i = 1; i < steps; i++)
        {
            double t = i / (double)steps;
            double x = fromX + dx * t;
            double y = fromY + dy * t;
            double z = fromZ + dz * t;
            bool intersects=EntityBodyIntersectsCollision(entity,x,y,z,dimension,out string blockCode,out double penetrationDepth,out bool known,terrain);
            if(!known)return false;
            if(!intersects)continue;
            if (penetrationDepth < config.PacketGuardNoclipMinPenetrationDepth) continue;

            collisionSamples++;
            maxPenetrationDepth = Math.Max(maxPenetrationDepth, penetrationDepth);
            if (firstBlock.Length == 0) firstBlock = blockCode;

        }

        firstBlock += $";{terrain.Describe()};geometryCorrectionCompatible={terrain.CorrectionCompatible}";
        return collisionSamples>=config.PacketGuardNoclipRequiredCollisionSamples;
    }

    private bool EntityBodyIntersectsCollision(Entity entity, double x, double y, double z, int dimension, out string blockCode, out double penetrationDepth)
        => EntityBodyIntersectsCollision(entity,x,y,z,dimension,out blockCode,out penetrationDepth,out _);

    private bool EntityBodyIntersectsCollision(Entity entity,double x,double y,double z,int dimension,out string blockCode,out double penetrationDepth,out bool complete)
        => EntityBodyIntersectsCollision(entity,x,y,z,dimension,out blockCode,out penetrationDepth,out complete,null);

    private bool EntityBodyIntersectsCollision(Entity entity,double x,double y,double z,int dimension,out string blockCode,out double penetrationDepth,out bool complete,NoclipTerrainEvidence? terrain)
    {
        complete=false;
        blockCode = "";
        penetrationDepth = 0;
        Cuboidf body = entity.OriginCollisionBox;
        if (body == null || !double.IsFinite(x)||!double.IsFinite(y)||!double.IsFinite(z)||!PacketReachGeometry.ValidBox(body.X1,body.Y1,body.Z1,body.X2,body.Y2,body.Z2)) return false;

        // Shrink the body slightly so merely touching floors/walls does not count as clipping.
        const double epsilon = 0.035;
        double minX = x + body.X1 + epsilon;
        double minY = y + body.Y1 + epsilon;
        double minZ = z + body.Z1 + epsilon;
        double maxX = x + body.X2 - epsilon;
        double maxY = y + body.Y2 - epsilon;
        double maxZ = z + body.Z2 - epsilon;
        if (maxX <= minX || maxY <= minY || maxZ <= minZ) return false;

        int bx0 = (int)Math.Floor(minX);
        int by0 = (int)Math.Floor(minY);
        int bz0 = (int)Math.Floor(minZ);
        int bx1 = (int)Math.Floor(maxX);
        int by1 = (int)Math.Floor(maxY);
        int bz1 = (int)Math.Floor(maxZ);

        if((long)(bx1-bx0+1)*(by1-by0+1)*(bz1-bz0+1)>128)return false;
        var blockAccessor = api.World.BlockAccessor;
        for (int bx = bx0; bx <= bx1; bx++)
        for (int by = by0; by <= by1; by++)
        for (int bz = bz0; bz <= bz1; bz++)
        {
            var blockPos = new BlockPos(bx, by, bz, dimension);
            if(bx<0||by<0||bz<0||bx>=blockAccessor.MapSizeX||by>=blockAccessor.MapSizeY||bz>=blockAccessor.MapSizeZ||blockAccessor.GetChunkAtBlockPos(blockPos)==null)return false;
            Block block = blockAccessor.GetBlock(blockPos);
            if (block == null || block.Id == 0) continue;

            Cuboidf[] boxes;
            try { boxes = block.GetCollisionBoxes(blockAccessor, blockPos) ?? Array.Empty<Cuboidf>(); }
            catch { return false; }

            if(boxes.Length>128)return false;
            foreach (Cuboidf box in boxes)
            {
                if(box==null||!PacketReachGeometry.ValidBox(box.X1,box.Y1,box.Z1,box.X2,box.Y2,box.Z2))return false;
                double cMinX = bx + box.X1;
                double cMinY = by + box.Y1;
                double cMinZ = bz + box.Z1;
                double cMaxX = bx + box.X2;
                double cMaxY = by + box.Y2;
                double cMaxZ = bz + box.Z2;

                if (maxX > cMinX && minX < cMaxX &&
                    maxY > cMinY && minY < cMaxY &&
                    maxZ > cMinZ && minZ < cMaxZ)
                {
                    double overlapX = Math.Min(maxX, cMaxX) - Math.Max(minX, cMinX);
                    double overlapY = Math.Min(maxY, cMaxY) - Math.Max(minY, cMinY);
                    double overlapZ = Math.Min(maxZ, cMaxZ) - Math.Max(minZ, cMinZ);
                    double depth = Math.Max(0, Math.Min(overlapX, Math.Min(overlapY, overlapZ)));
                    if(terrain!=null && depth>=config.PacketGuardNoclipMinPenetrationDepth)
                    {
                        string code=block.Code?.ToString() ?? block.Id.ToString();
                        // Dynamic/custom geometry is review evidence, never a correction cause.
                        bool complex=block.IsMissing || block.Climbable || !string.IsNullOrWhiteSpace(block.EntityClass) ||
                            (block.BlockBehaviors?.Length??0)>0 || block.GetType()!=typeof(Block) || boxes.Length!=1 ||
                            box.X1!=0 || box.Y1!=0 || box.Z1!=0 || box.X2!=1 || box.Y2!=1 || box.Z2!=1 ||
                            code.Contains("door",StringComparison.OrdinalIgnoreCase) || code.Contains("chisel",StringComparison.OrdinalIgnoreCase);
                        terrain?.Add(bx,by,bz,code,complex);
                    }
                    if (depth <= penetrationDepth) continue;

                    penetrationDepth = depth;
                    blockCode = block.Code?.ToString() ?? block.Id.ToString();
                }
            }
        }

        complete=true;
        return penetrationDepth > 0;
    }

    private void CorrectNoclip(IServerPlayer player,MovementState state,Entity entity,long now,string reason)
    {
        // Failed/unknown destinations never become punishment strikes. Start a new
        // confirmation episode after each correction, rather than recycling old samples.
        if(state.PendingNoclipCorrection.Active || state.CorrectionRecovery.Suspended(now))return;
        if(!RollbackMovement(player,state,entity,now,reason)){RecordCorrectionFailure(player,state,now);return;}
        state.NoclipConfirmation.Reset();
        state.PendingNoclipCorrection.Stage(now,entity.EntityId,state.Dimension,state.X,state.Y,state.Z);
    }

    private void RecordCorrectionFailure(IServerPlayer player,MovementState state,long now)
    {
        if(!state.CorrectionRecovery.Fail(now))return;
        state.NoclipConfirmation.Reset();
        reportFinding(player,"PACKET_CORRECTION_SUSPENDED",AntiCheatSeverity.Moderate,
            "Three failed noclip recovery attempts in 30 seconds: corrections paused for 60 seconds. Review terrain and teleport compatibility; no kick strike was added.",player.PlayerUID,"decision=suspended;pauseMs=60000;failures=3;windowMs=30000");
    }

    private bool RecordConfirmedNoclipCorrection(IServerPlayer player,MovementState state,long now)
    {
        bool threshold=state.NoclipCorrections.Record(now,config.PacketGuardNoclipCorrectionsBeforeKick,
            config.PacketGuardNoclipCorrectionDecaySeconds,config.PacketGuardNoclipCorrectionMinimumIntervalSeconds);
        string evidence=$"decision=observed-correction-arrival;activeCorrections={state.NoclipCorrections.Count};kickThreshold={config.PacketGuardNoclipCorrectionsBeforeKick};decaySeconds={config.PacketGuardNoclipCorrectionDecaySeconds};minimumIntervalSeconds={config.PacketGuardNoclipCorrectionMinimumIntervalSeconds};kickEnabled={config.PacketGuardNoclipKickAfterCorrections}";
        reportFinding(player,"PACKET_NOCLIP_CORRECTION",AntiCheatSeverity.Moderate,
            "The server observed arrival at the requested noclip correction destination. Only independently spaced observed corrections count toward the configured kick threshold.",player.PlayerUID,evidence);
        if(!threshold || !config.Enabled || !config.PacketGuardNoclipKickAfterCorrections)return false;
        api.Logger.Audit("[PacketGuard/Noclip] playerUid={0};{1};action=kick",player.PlayerUID,evidence);
        reportFinding(player,"PACKET_NOCLIP_KICK",AntiCheatSeverity.High,
            "Kicked after the configured number of independently spaced, unexpired noclip corrections.",player.PlayerUID,evidence+";action=kick");
        player.Disconnect("Repeated noclip corrections. Please reconnect; contact an administrator if this was legitimate movement.");
        return true;
    }

    private bool RollbackMovement(IServerPlayer player, MovementState state, Entity entity, long now, string reason)
    {
        SafePositionSample rollback = state.GetRollbackPosition(
            now,
            config.PacketGuardRollbackSafetyLagMs,
            config.PacketGuardSafePositionMaxAgeMs,
            config.PacketGuardSafePositionHistorySize);

        if (config.PacketGuardRollbackValidateCollision)
        {
            bool foundClearDestination = false;
            foreach (SafePositionSample candidate in state.GetRollbackCandidates(
                         now, config.PacketGuardRollbackSafetyLagMs, config.PacketGuardSafePositionMaxAgeMs,
                         config.PacketGuardSafePositionHistorySize, config.PacketGuardRollbackValidationCandidates))
            {
                EntityBodyIntersectsCollision(entity, candidate.X, candidate.Y, candidate.Z, candidate.Dimension,
                    out _, out double penetration, out bool complete);
                if (complete && penetration < 0.03)
                {
                    rollback = candidate;
                    foundClearDestination = true;
                    break;
                }
            }
            // Unknown terrain is not clear terrain. Never fall back to an unvalidated,
            // stale checkpoint when the bounded candidate search cannot establish safety.
            if (!foundClearDestination) return false;
        }

        state.GraceUntilUtc = now + config.PacketGuardCorrectionGraceMs;
        PacketGuardTrustedState.Mark(player, reason, config.PacketGuardCorrectionGraceMs, PacketGuardTrustScope.All);
        var safe = new Vec3d(rollback.X, rollback.Y, rollback.Z);
        try
        {
            EntityRuntimeCompat.GetPos(entity)?.Motion?.Set(0, 0, 0);
            entity.TeleportTo(safe);
            // Re-baseline immediately to the correction destination. Waiting for the next
            // sampler pass leaves the pre-rollback cheat position as LastAccepted and can
            // create a second synthetic movement violation after a successful correction.
            state.AcceptRollback(rollback, now);
            state.NoteRollback(now);
            state.ResetAnalysis(now);
            return true;
        }
        catch (Exception ex)
        {
            api.Logger.Warning(
                "[ServerEssentials/AntiCheat] PacketGuard could not roll {0} back to the last safe position: {1}",
                player.PlayerName,
                ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Reach + block-action validation
    // ---------------------------------------------------------------------

    private void SampleReachPositions(long now)
    {
        if (!config.PacketGuardReachEnabled || !config.PacketGuardReachPositionHistoryEnabled) return;

        foreach (IServerPlayer player in sampledPlayers)
        {
            if (!ShouldGuard(player))
            {
                if (player != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
                    reachPositionHistory.Remove(player.PlayerUID);
                continue;
            }

            try
            {
                if (sampledPlayerPositions.TryGetValue(player.PlayerUID, out EntityPos? pos))
                    RecordReachPosition(player, now, pos);
            }
            catch
            {
                // Reach history is only a false-positive safety net. A bad/temporarily missing
                // position sample must never disable the anti-cheat or reject a legitimate use.
            }
        }
    }

    private void RecordReachPosition(IServerPlayer player, long now)
    {
        if (player?.Entity == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        EntityPos? pos = EntityRuntimeCompat.GetPos(player.Entity);
        if (pos == null) return;
        RecordReachPosition(player, now, pos);
    }

    private void RecordReachPosition(IServerPlayer player, long now, EntityPos pos)
    {
        if (player?.Entity == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        Vec3d eye = player.Entity.LocalEyePos;
        var sample = new ReachPositionSample(
            now,
            pos.X + eye.X,
            pos.Y + eye.Y,
            pos.Z + eye.Z,
            pos.Dimension,
            player.Entity.EntityId);

        if (!reachPositionHistory.TryGetValue(player.PlayerUID, out Queue<ReachPositionSample>? queue))
        {
            queue = new Queue<ReachPositionSample>();
            reachPositionHistory[player.PlayerUID] = queue;
        }

        queue.Enqueue(sample);
        long cutoff = now - Math.Max(100, config.PacketGuardReachPositionHistoryMs) - 250;
        while (queue.Count > 0 && (queue.Peek().Utc < cutoff || queue.Count > 24)) queue.Dequeue();
    }

    private bool IsClearlyInvalidBlockSelection(IServerPlayer player, BlockSelection? blockSel)
    {
        BlockPos? target = blockSel?.Position;
        if (target == null) return true;

        // Vintage Story/mod callbacks can surface an all-zero BlockPos as a placeholder when there
        // is no real world block target. On normal large worlds this produced absurd 700k-block
        // reach findings. Only suppress the sentinel when the player is clearly nowhere near it,
        // so a legitimate interaction near the actual origin is not blindly ignored.
        if (target.X == 0 && target.InternalY == 0 && target.Z == 0)
        {
            EntityPos? pos = EntityRuntimeCompat.GetPos(player.Entity);
            if (pos == null) return true;
            double dx = pos.X;
            double dy = pos.Y;
            double dz = pos.Z;
            double distanceSq = dx * dx + dy * dy + dz * dz;
            double sanityRadius = Math.Max(64.0, Math.Max(1.0, player.WorldData?.PickingRange ?? 4.5) * 8.0);
            if (distanceSq > sanityRadius * sanityRadius)
            {
                PacketGuardTrustedState.RecordSuppression(player, "invalid-block-origin", $"Ignored placeholder block target 0,0,0 while player was at {pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0}.", 750);
                return true;
            }
        }

        return false;
    }

    private bool OnCanUseBlock(IServerPlayer player, BlockSelection blockSel)
    {
        // Use the stable public CanUseBlock hook rather than the lower-level HandInteract
        // network enum. Treat this callback as evidence-only because some modded right-click
        // placement flows also pass through block-use semantics.
        if (!config.PacketGuardReachEnabled || !ShouldGuard(player) || blockSel?.Position == null) return true;
        if (IsClearlyInvalidBlockSelection(player, blockSel))
        {
            reachViolations.Remove(player.PlayerUID);
            return true;
        }
        if (IsBlockInReach(player, blockSel, "use", out ReachCheckResult reach)) return true;

        // CanUseBlock is also used by some modded right-click placement flows. Hard-cancelling this
        // callback can therefore cancel a custom placement after the client has already predicted
        // inventory use. Keep impossible remote-use evidence, but fail open for the actual action.
        // Block breaking still has its own authoritative rejection path in OnBreakBlock.
        RegisterReachViolation(player, blockSel, reach, "use");
        return true;
    }

    /// <summary>
    /// Captures narrow command intent before a server-side teleport is sampled. Th3Essentials
    /// /spawn is armed for the issuing player only and is later accepted only at a real spawn
    /// destination. Admin /tp retains its existing named-target preauthorization behavior.
    /// </summary>
    private void OnPlayerChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if (byPlayer == null) return;
        string text = ChatMessageBoundary.Parse(message,byPlayer.PlayerName,null,byPlayer.Role?.Name).Body.Trim();
        if (text.Length < 2 || text[0] != '/') return;

        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return;
        string command = tokens[0].TrimStart('/').ToLowerInvariant();

        if (command == "spawn" && tokens.Length == 1 && th3EssentialsEnabled && config.PacketGuardTh3EssentialsSpawnCompatibility &&
            !string.IsNullOrWhiteSpace(byPlayer.PlayerUID))
        {
            th3EssentialsSpawnIntents[byPlayer.PlayerUID] = NowMs() + config.PacketGuardTh3EssentialsSpawnGraceMs;
        }

        if (!IsAdmin(byPlayer) || (command != "tp" && command != "teleport")) return;

        bool markAll = tokens.Skip(1).Any(t => t.Equals("all", StringComparison.OrdinalIgnoreCase) || t == "*");
        foreach (var candidate in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (candidate == null || candidate.PlayerUID == byPlayer.PlayerUID) continue;
            bool named = markAll || tokens.Skip(1).Any(t =>
            {
                string value = t.Trim('"', '\'');
                return value.Equals(candidate.PlayerName, StringComparison.OrdinalIgnoreCase) ||
                       value.Equals(candidate.PlayerUID, StringComparison.OrdinalIgnoreCase);
            });
            if (named) PacketGuardTrustedState.Mark(candidate, "admin-teleport-command", config.PacketGuardEngineTeleportGraceMs, PacketGuardTrustScope.All);
        }
    }

    private bool TryConsumeTh3EssentialsSpawnArrival(IServerPlayer player, EntityPos pos, long now)
    {
        if (!config.PacketGuardTh3EssentialsSpawnCompatibility || !th3EssentialsEnabled ||
            string.IsNullOrWhiteSpace(player.PlayerUID)) return false;

        if (!th3EssentialsSpawnIntents.TryGetValue(player.PlayerUID, out long until)) return false;
        if (now > until)
        {
            th3EssentialsSpawnIntents.Remove(player.PlayerUID);
            return false;
        }

        bool matches = false;
        try
        {
            FuzzyEntityPos? playerSpawn = player.GetSpawnPosition(false);
            if (playerSpawn != null) matches = IsNearSpawn(pos, playerSpawn, Math.Max(8.0, Math.Min(32.0, playerSpawn.Radius + 6.0)));
        }
        catch { }

        // Th3Essentials currently uses the world/default spawn path. Keep that exact destination
        // supported too, including saves where the stored spawn Y differs from the terrain landing Y.
        if (!matches)
        {
            try
            {
                var worldSpawn = CoordinateDisplayService.GetDefaultSpawnOrWorldCenter(api);
                double dx = pos.X - (worldSpawn.X + 0.5);
                double dz = pos.Z - (worldSpawn.Z + 0.5);
                matches = dx * dx + dz * dz <= 24.0 * 24.0 &&
                          (worldSpawn.Y <= 0 || Math.Abs(pos.Y - worldSpawn.Y) <= 32.0);
            }
            catch { }
        }

        if (!matches) return false;
        th3EssentialsSpawnIntents.Remove(player.PlayerUID);
        return true;
    }

    private static bool IsNearSpawn(EntityPos pos, EntityPos spawn, double horizontalAllowance)
    {
        if (pos.Dimension != spawn.Dimension) return false;
        double dx = pos.X - spawn.X;
        double dz = pos.Z - spawn.Z;
        if (dx * dx + dz * dz > horizontalAllowance * horizontalAllowance) return false;
        return Math.Abs(pos.Y - spawn.Y) <= 32.0;
    }

    /// <summary>
    /// Arms a one-shot vanilla translocator transition from the source side. Vanilla's
    /// BlockEntityTeleporterBase keeps an entity in its protected tpingEntities map for the
    /// warm-up period; BlockEntityStaticTranslocator also exposes TargetLocation. Reflection
    /// keeps Server Essentials independent of the Survival mod assembly at compile time.
    /// </summary>
    private void UpdatePendingTranslocatorTransition(IServerPlayer player, EntityPos pos, long now)
    {
        if (string.IsNullOrWhiteSpace(player.PlayerUID) || player.Entity == null) return;

        if (!TryFindActiveTranslocatorForPlayer(player, pos, out BlockPos? source, out BlockPos? target))
        {
            if (pendingTranslocatorTransitions.TryGetValue(player.PlayerUID, out PendingTranslocatorTransition? stale) &&
                now - stale.LastSeenUtc > TranslocatorDepartureWindowMs)
            {
                pendingTranslocatorTransitions.Remove(player.PlayerUID);
            }
            return;
        }

        BlockPos sourcePos = source!;
        if (!pendingTranslocatorTransitions.TryGetValue(player.PlayerUID, out PendingTranslocatorTransition? pending) ||
            pending.SourceDimension != sourcePos.dimension ||
            pending.SourceX != sourcePos.X || pending.SourceY != sourcePos.Y || pending.SourceZ != sourcePos.Z)
        {
            pending = new PendingTranslocatorTransition
            {
                SourceX = sourcePos.X,
                SourceY = sourcePos.Y,
                SourceZ = sourcePos.Z,
                SourceDimension = sourcePos.dimension,
                FirstSeenUtc = now,
                LastSeenUtc = now
            };
            pendingTranslocatorTransitions[player.PlayerUID] = pending;
        }
        else
        {
            pending.LastSeenUtc = now;
        }

        if (target != null)
        {
            pending.HasLinkedTarget = true;
            pending.TargetX = target.X;
            pending.TargetY = target.Y;
            pending.TargetZ = target.Z;
            pending.TargetDimension = target.dimension;
        }

        if (!pending.Armed && now - pending.FirstSeenUtc >= TranslocatorArmDelayMs)
            pending.Armed = true;
    }

    /// <summary>
    /// Consumes a source-side translocator authorization exactly once. When the vanilla linked
    /// target is available we require the observed destination to match that target; this is
    /// stronger than the old "both chunks must contain a translocator right now" heuristic and
    /// does not depend on destination chunk timing.
    /// </summary>
    private bool TryConsumePendingTranslocatorTransition(
        IServerPlayer player,
        double sourceX,
        double sourceY,
        double sourceZ,
        int sourceDimension,
        EntityPos destination,
        long now,
        out bool linkedTargetVerified)
    {
        linkedTargetVerified = false;
        if (string.IsNullOrWhiteSpace(player.PlayerUID) ||
            !pendingTranslocatorTransitions.TryGetValue(player.PlayerUID, out PendingTranslocatorTransition? pending))
            return false;

        if (!pending.Armed || now - pending.LastSeenUtc > TranslocatorDepartureWindowMs)
        {
            pendingTranslocatorTransitions.Remove(player.PlayerUID);
            return false;
        }

        // The last accepted movement position must still be at the translocator that armed this
        // transition. This prevents an old warm-up from authorizing a later jump elsewhere.
        double sourceDx = sourceX - (pending.SourceX + 0.5);
        double sourceDz = sourceZ - (pending.SourceZ + 0.5);
        bool sourceMatches = sourceDimension == pending.SourceDimension &&
                             sourceDx * sourceDx + sourceDz * sourceDz <= 6.0 * 6.0 &&
                             Math.Abs(sourceY - pending.SourceY) <= 6.0;
        if (!sourceMatches)
        {
            pendingTranslocatorTransitions.Remove(player.PlayerUID);
            return false;
        }

        if (pending.HasLinkedTarget)
        {
            // Static translocator GetTarget() lands roughly one block above and slightly offset
            // from TargetLocation. Use bounded headroom for entity centering/reconciliation.
            double dx = destination.X - (pending.TargetX + 0.5);
            double dz = destination.Z - (pending.TargetZ + 0.5);
            bool dimensionMatches = pending.TargetDimension == destination.Dimension ||
                                    pending.TargetDimension == 0 || destination.Dimension == pending.SourceDimension;
            bool targetMatches = dimensionMatches &&
                                 dx * dx + dz * dz <= 5.0 * 5.0 &&
                                 Math.Abs(destination.Y - (pending.TargetY + 1.0)) <= 5.0;
            if (!targetMatches)
            {
                pendingTranslocatorTransitions.Remove(player.PlayerUID);
                return false;
            }
            linkedTargetVerified = true;
        }
        else
        {
            // Unknown/custom teleporter bases only get the old endpoint validation fallback.
            if (!IsNearTranslocator(destination.X, destination.Y, destination.Z, destination.Dimension))
            {
                pendingTranslocatorTransitions.Remove(player.PlayerUID);
                return false;
            }
        }

        pendingTranslocatorTransitions.Remove(player.PlayerUID);
        return true;
    }

    private void ClearPendingTranslocatorIfDeparted(string playerUid, EntityPos pos)
    {
        if (!pendingTranslocatorTransitions.TryGetValue(playerUid, out PendingTranslocatorTransition? pending)) return;
        double dx = pos.X - (pending.SourceX + 0.5);
        double dz = pos.Z - (pending.SourceZ + 0.5);
        if (pos.Dimension != pending.SourceDimension || dx * dx + dz * dz > 8.0 * 8.0 || Math.Abs(pos.Y - pending.SourceY) > 8.0)
            pendingTranslocatorTransitions.Remove(playerUid);
    }

    private bool TryFindActiveTranslocatorForPlayer(
        IServerPlayer player,
        EntityPos pos,
        out BlockPos? source,
        out BlockPos? target)
    {
        source = null;
        target = null;
        long entityId = player.Entity.EntityId;
        int cx = (int)Math.Floor(pos.X);
        int cy = (int)Math.Floor(pos.Y);
        int cz = (int)Math.Floor(pos.Z);

        try
        {
            // The player must actually be colliding with the teleporter, so a tight scan is enough
            // and avoids the cost of the wider post-jump compatibility search every 100 ms.
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
            {
                var candidate = new BlockPos(cx + dx, cy + dy, cz + dz, pos.Dimension);
                var block = api.World.BlockAccessor.GetBlock(candidate);
                string path = block?.Code?.Path ?? string.Empty;
                if (path.IndexOf("translocator", StringComparison.OrdinalIgnoreCase) < 0) continue;

                BlockEntity? blockEntity = api.World.BlockAccessor.GetBlockEntity(candidate);
                if (blockEntity == null || !BlockEntityTracksTeleportingEntity(blockEntity, entityId)) continue;

                source = candidate.Copy();
                target = TryReadTranslocatorTarget(blockEntity);
                return true;
            }
        }
        catch
        {
            // Source-side preauthorization is an accuracy feature. Failure simply falls back to
            // engine teleport flags / endpoint validation; it must never disable PacketGuard.
        }

        return false;
    }

    private static bool BlockEntityTracksTeleportingEntity(BlockEntity blockEntity, long entityId)
    {
        try
        {
            for (Type? type = blockEntity.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo? field = type.GetField(
                    "tpingEntities",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null) continue;

                object? value = field.GetValue(blockEntity);
                if (value is IDictionary dictionary) return dictionary.Contains(entityId);

                MethodInfo? containsKey = value?.GetType().GetMethod("ContainsKey", new[] { typeof(long) });
                if (containsKey?.Invoke(value, new object[] { entityId }) is bool contains) return contains;
                return false;
            }
        }
        catch { }
        return false;
    }

    private static BlockPos? TryReadTranslocatorTarget(BlockEntity blockEntity)
    {
        try
        {
            Type type = blockEntity.GetType();
            PropertyInfo? property = type.GetProperty(
                "TargetLocation",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(blockEntity) is BlockPos propertyTarget) return propertyTarget.Copy();

            for (Type? current = type; current != null; current = current.BaseType)
            {
                FieldInfo? field = current.GetField(
                    "tpLocation",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field?.GetValue(blockEntity) is BlockPos fieldTarget) return fieldTarget.Copy();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Validates a large movement against the actual linked target of a nearby vanilla static
    /// translocator. Unlike the old endpoint-only fallback, this does not require the destination
    /// chunk to have finished loading and does not grant a generic grace merely for standing near
    /// a translocator. The destination must match the source block entity's own GetTarget/
    /// TargetLocation value.
    /// </summary>
    private bool IsTrustedLinkedTranslocatorJump(
        IServerPlayer player,
        double sourceX,
        double sourceY,
        double sourceZ,
        int sourceDimension,
        EntityPos destination)
    {
        if (player?.Entity == null || destination == null) return false;
        if (sourceDimension != destination.Dimension) return false;

        int cx = (int)Math.Floor(sourceX);
        int cy = (int)Math.Floor(sourceY);
        int cz = (int)Math.Floor(sourceZ);

        try
        {
            const int horizontalRadius = 6;
            const int verticalRadius = 5;
            for (int dy = -verticalRadius; dy <= verticalRadius; dy++)
            for (int dx = -horizontalRadius; dx <= horizontalRadius; dx++)
            for (int dz = -horizontalRadius; dz <= horizontalRadius; dz++)
            {
                var candidate = new BlockPos(cx + dx, cy + dy, cz + dz, sourceDimension);
                Block? block = api.World.BlockAccessor.GetBlock(candidate);
                string path = block?.Code?.Path ?? string.Empty;
                if (path.IndexOf("translocator", StringComparison.OrdinalIgnoreCase) < 0) continue;

                BlockEntity? blockEntity = api.World.BlockAccessor.GetBlockEntity(candidate);
                if (blockEntity == null) continue;
                if (!TryResolveTranslocatorTarget(blockEntity, player.Entity, sourceDimension, out Vec3d target, out int targetDimension))
                    continue;

                if (targetDimension != destination.Dimension) continue;
                double tx = destination.X - target.X;
                double ty = destination.Y - target.Y;
                double tz = destination.Z - target.Z;

                // Vanilla GetTarget lands at roughly tpLocation + (-0.3, +1, -0.3). Allow a few
                // blocks for collision resolution/entity centering while still binding the grace
                // to one specific linked destination.
                if (tx * tx + tz * tz <= 6.0 * 6.0 && Math.Abs(ty) <= 6.0)
                    return true;
            }
        }
        catch
        {
            // Compatibility verification is only a false-positive safety net. Fail closed.
        }

        return false;
    }

    private static bool TryResolveTranslocatorTarget(
        BlockEntity blockEntity,
        Entity forEntity,
        int sourceDimension,
        out Vec3d target,
        out int targetDimension)
    {
        target = new Vec3d();
        targetDimension = sourceDimension;

        try
        {
            // This is the strongest/public vanilla signal. BlockEntityStaticTranslocator.GetTarget
            // returns tpLocation offset to the actual entity landing position. Reflection keeps the
            // Suite independent of VSSurvivalMod at compile time.
            MethodInfo? getTarget = blockEntity.GetType().GetMethod(
                "GetTarget",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Entity) },
                modifiers: null);
            if (getTarget?.Invoke(blockEntity, new object[] { forEntity }) is Vec3d resolved)
            {
                target = resolved.Clone();
                BlockPos? rawTarget = TryReadTranslocatorTarget(blockEntity);
                if (rawTarget != null) targetDimension = rawTarget.dimension;
                return true;
            }

            BlockPos? blockTarget = TryReadTranslocatorTarget(blockEntity);
            if (blockTarget != null)
            {
                target = new Vec3d(blockTarget.X - 0.3, blockTarget.Y + 1.0, blockTarget.Z - 0.3);
                targetDimension = blockTarget.dimension;
                return true;
            }
        }
        catch
        {
            // Optional runtime compatibility probe only.
        }

        return false;
    }

    private bool IsTrustedVanillaTranslocatorJump(MovementState state, EntityPos destination)
    {
        if (state.Dimension != destination.Dimension) return false;
        return IsNearTranslocator(state.X, state.Y, state.Z, state.Dimension) &&
               IsNearTranslocator(destination.X, destination.Y, destination.Z, destination.Dimension);
    }

    private bool IsTrustedVanillaTranslocatorJump(TrustedTransitionSample state, EntityPos destination)
    {
        if (state.Dimension != destination.Dimension) return false;
        return IsNearTranslocator(state.X, state.Y, state.Z, state.Dimension) &&
               IsNearTranslocator(destination.X, destination.Y, destination.Z, destination.Dimension);
    }

    private bool IsNearTranslocator(double x, double y, double z, int dimension)
    {
        const int horizontalRadius = 5;
        const int verticalRadius = 4;
        int cx = (int)Math.Floor(x);
        int cy = (int)Math.Floor(y);
        int cz = (int)Math.Floor(z);

        try
        {
            for (int dy = -verticalRadius; dy <= verticalRadius; dy++)
            for (int dx = -horizontalRadius; dx <= horizontalRadius; dx++)
            for (int dz = -horizontalRadius; dz <= horizontalRadius; dz++)
            {
                var block = api.World.BlockAccessor.GetBlock(new BlockPos(cx + dx, cy + dy, cz + dz, dimension));
                string path = block?.Code?.Path ?? string.Empty;
                if (path.IndexOf("translocator", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        catch
        {
            // Fail closed: an unavailable chunk/block lookup must not create teleport grace.
        }

        return false;
    }

    /// <summary>
    /// Narrow fallback for Teleportation Runes. The normal path is the engine's IsTeleport /
    /// Teleporting flags handled in CheckMovement. If those transient flags clear between
    /// samples, only accept a large move when the player is still holding a bound tprunes rune
    /// and the new position is actually beside that rune's stored beacon coordinates.
    /// Merely holding an unbound rune does not grant movement grace.
    /// </summary>
    private bool IsTrustedTPRunesArrival(IServerPlayer? player, EntityPos? pos)
    {
        if (!config.PacketGuardTPRunesCompatibility || !tprunesEnabled || player == null || pos == null) return false;

        try
        {
            ItemSlot? slot = player.InventoryManager?.ActiveHotbarSlot;
            ItemStack? stack = slot?.Itemstack;
            CollectibleObject? collectible = stack?.Collectible;
            AssetLocation? code = collectible?.Code;
            if (code == null || !string.Equals(code.Domain, "tprunes", StringComparison.OrdinalIgnoreCase)) return false;

            string typeName = collectible?.GetType().FullName ?? "";
            bool isRune = typeName.EndsWith(".ItemRune", StringComparison.Ordinal) ||
                          code.Path.Contains("rune", StringComparison.OrdinalIgnoreCase);
            if (!isRune || stack?.Attributes == null) return false;
            if (!stack.Attributes.HasAttribute("x") || !stack.Attributes.HasAttribute("y") || !stack.Attributes.HasAttribute("z")) return false;

            int tx = stack.Attributes.GetInt("x");
            int ty = stack.Attributes.GetInt("y");
            int tz = stack.Attributes.GetInt("z");

            // TPRunes chooses one of the four horizontal blocks beside the saved beacon.
            // Allow a little entity-position/centering headroom, but only at that destination.
            double dx = pos.X - tx;
            double dy = pos.Y - ty;
            double dz = pos.Z - tz;
            double horizontal = Math.Sqrt(dx * dx + dz * dz);
            return horizontal <= 3.5 && Math.Abs(dy) <= 3.0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetSelectionBoxIdCompat(BlockSelection blockSel)
    {
        // SelectionBoxId does not exist in every Vintage Story API build. Treat it as
        // optional diagnostic evidence so reach protection is not tied to that ABI detail.
        try
        {
            Type type = blockSel.GetType();

            var field = type.GetField("SelectionBoxId");
            if (field?.GetValue(blockSel) is string fieldValue) return fieldValue;

            var property = type.GetProperty("SelectionBoxId");
            if (property?.GetValue(blockSel) is string propertyValue) return propertyValue;
        }
        catch { }

        return "";
    }

    private void OnBreakBlock(IServerPlayer player, BlockSelection blockSel, ref float dropQuantityMultiplier, ref EnumHandling handling)
    {
        if (!ShouldGuard(player) || blockSel?.Position == null) return;
        if (IsClearlyInvalidBlockSelection(player, blockSel))
        {
            reachViolations.Remove(player.PlayerUID);
            return;
        }

        if (config.PacketGuardReachEnabled && !IsBlockInReach(player, blockSel, "break", out ReachCheckResult reach))
        {
            bool reject = RegisterReachViolation(player, blockSel, reach, "break");
            if (reject)
            {
                handling = EnumHandling.PreventDefault;
                ingressGuard.ResyncDeniedBlock(player,blockSel.Position);
                return;
            }
        }

        if (!config.PacketGuardMiningEnabled || ServerProtectionExemptions.Has(player.PlayerUID,ServerProtectionScope.Mining,NowMs())) return;
        CheckMiningBurst(player, blockSel, ref handling);
        if(handling==EnumHandling.PreventDefault)ingressGuard.ResyncDeniedBlock(player,blockSel.Position);
    }

    private bool IsBlockInReach(IServerPlayer player, BlockSelection blockSel, string action, out ReachCheckResult result)
    {
        // A block action immediately after an engine/server-authorized teleport can be processed
        // while the player's replicated position is still settling. The movement guard already
        // proved/authorized that position change, so do not independently accuse the same short
        // window as a reach hack.
        long now = NowMs();
        PacketGuardTpsSnapshot tps = tickHealth.GetSnapshot();
        double timingMultiplier = tps.Enabled ? tps.TimingMultiplier : 1.0;
        if (PacketGuardTrustedState.TryGetActiveReason(player.PlayerUID, now, PacketGuardTrustScope.Reach, out string reachTrustReason))
        {
            PacketGuardTrustedState.RecordSuppression(player, "reach-teleport-grace", $"Block {action} accepted during trusted transition: {reachTrustReason}.", 1000, now);
            result = ReachCheckResult.AllowedByTeleportGrace;
            return true;
        }

        BlockPos pos = blockSel.Position;
        Entity entity = player.Entity;
        Vec3d eyeOffset = entity.LocalEyePos;
        EntityPos? entityPos = EntityRuntimeCompat.GetPos(entity);
        if (entityPos == null)
        {
            result = ReachCheckResult.Unavailable;
            return true;
        }

        if(!double.IsFinite(entityPos.X)||!double.IsFinite(entityPos.Y)||!double.IsFinite(entityPos.Z)||!double.IsFinite(eyeOffset.X)||!double.IsFinite(eyeOffset.Y)||!double.IsFinite(eyeOffset.Z))
        {result=ReachCheckResult.Unavailable;return true;}
        double currentEyeX = entityPos.X + eyeOffset.X;
        double currentEyeY = entityPos.Y + eyeOffset.Y;
        double currentEyeZ = entityPos.Z + eyeOffset.Z;
        double currentDistance = DistanceToBlockCube(currentEyeX, currentEyeY, currentEyeZ, pos);
        double bestDistance = currentDistance;
        bool usedHistory = false;
        long bestSampleAgeMs = 0;
        double pingMs = PacketReachEvidence.PingMilliseconds(player.Ping);
        int effectiveRewindMs = Math.Min(
            config.PacketGuardReachPositionHistoryMs,
            Math.Max(100, (int)Math.Round(config.PacketGuardReachMaxRewindMs * timingMultiplier)));
        // Bound history reconciliation by observed RTT, but allow the window to expand under
        // measured server lag. This changes only how far back server-authoritative positions are
        // considered; it does not increase the player's actual block reach distance.
        int latencyBoundedRewindMs = Math.Max(
            100,
            (int)Math.Round((150.0 + pingMs * 0.75) * timingMultiplier));
        effectiveRewindMs = Math.Min(effectiveRewindMs, latencyBoundedRewindMs);

        if (config.PacketGuardReachPositionHistoryEnabled &&
            reachPositionHistory.TryGetValue(player.PlayerUID, out Queue<ReachPositionSample>? samples))
        {
            foreach (ReachPositionSample sample in samples)
            {
                if (!PacketReachEvidence.IsUsableSample(now,sample.Utc,effectiveRewindMs,entityPos.Dimension,sample.Dimension,entity.EntityId,sample.EntityId)) continue;
                double candidate = DistanceToBlockCube(sample.EyeX, sample.EyeY, sample.EyeZ, pos);
                if (candidate + 0.0001 >= bestDistance) continue;
                bestDistance = candidate;
                usedHistory = true;
                bestSampleAgeMs = Math.Max(0, now - sample.Utc);
            }
        }

        // Also keep the current interaction position in history. This is useful immediately after
        // login/reload before the 100 ms sampler has accumulated its first few entries.
        if (config.PacketGuardReachPositionHistoryEnabled)
        {
            try { RecordReachPosition(player, now); } catch { }
        }

        if(!double.IsFinite(player.WorldData.PickingRange)||player.WorldData.PickingRange<=0){result=ReachCheckResult.Unavailable;return true;}
        double baseRange = Math.Max(0.1, player.WorldData.PickingRange);
        double actionAllowance = string.Equals(action, "use", StringComparison.OrdinalIgnoreCase)
            ? config.PacketGuardReachUseExtraTolerance
            : config.PacketGuardReachBreakExtraTolerance;

        double latencyAllowance = 0;
        if (config.PacketGuardReachLatencyCompensation)
        {
            // player.Ping is seconds in the server API. Give a small floor for normal packet/
            // tick ordering, then scale gently with RTT and cap the result. This does not alter
            // the player's actual PickingRange; it only prevents the anti-cheat from treating a
            // slightly stale server position as proof of reach abuse.
            latencyAllowance = Math.Min(
                config.PacketGuardReachMaxLatencyCompensation,
                0.20 + pingMs / 1000.0 * 3.0);
        }

        double allowed = baseRange + config.PacketGuardReachTolerance + actionAllowance + latencyAllowance;
        if(bestDistance>allowed)
        {
            if(pos.dimension!=entityPos.Dimension){result=ReachCheckResult.Unavailable;return true;}
            try
            {
                var accessor=api.World.BlockAccessor;
                if(accessor.GetChunkAtBlockPos(pos)==null){result=ReachCheckResult.Unavailable;return true;}
                var boxes=accessor.GetBlock(pos).GetSelectionBoxes(accessor,pos);
                if(boxes==null||boxes.Length is 0 or >128){result=ReachCheckResult.Unavailable;return true;}
                foreach(var box in boxes)
                {
                    if(!PacketReachGeometry.ValidBox(box.X1,box.Y1,box.Z1,box.X2,box.Y2,box.Z2)){result=ReachCheckResult.Unavailable;return true;}
                    if(box.X1>=0&&box.Y1>=0&&box.Z1>=0&&box.X2<=1&&box.Y2<=1&&box.Z2<=1)continue;
                    double x1=(double)pos.X+box.X1,y1=(double)pos.Y+box.Y1,z1=(double)pos.Z+box.Z1,x2=(double)pos.X+box.X2,y2=(double)pos.Y+box.Y2,z2=(double)pos.Z+box.Z2;
                    currentDistance=Math.Min(currentDistance,PacketReachGeometry.Distance(currentEyeX,currentEyeY,currentEyeZ,x1,y1,z1,x2,y2,z2));
                    bestDistance=Math.Min(bestDistance,currentDistance);
                    if(config.PacketGuardReachPositionHistoryEnabled&&reachPositionHistory.TryGetValue(player.PlayerUID,out var shapeSamples))
                    foreach(var sample in shapeSamples)
                    {
                        if(!PacketReachEvidence.IsUsableSample(now,sample.Utc,effectiveRewindMs,entityPos.Dimension,sample.Dimension,entity.EntityId,sample.EntityId))continue;
                        double candidate=PacketReachGeometry.Distance(sample.EyeX,sample.EyeY,sample.EyeZ,x1,y1,z1,x2,y2,z2);
                        if(candidate>=bestDistance)continue;bestDistance=candidate;usedHistory=true;bestSampleAgeMs=Math.Max(0,now-sample.Utc);
                    }
                }
            }
            catch{result=ReachCheckResult.Unavailable;return true;}
        }
        double exactHitDistance = TryDistanceToExactHit(currentEyeX, currentEyeY, currentEyeZ, blockSel, out double hitDistance)
            ? hitDistance
            : -1;

        result = new ReachCheckResult(
            bestDistance,
            allowed,
            latencyAllowance,
            actionAllowance,
            currentDistance,
            exactHitDistance,
            usedHistory,
            bestSampleAgeMs,
            effectiveRewindMs,
            false,
            false);
        return bestDistance <= allowed;
    }

    private static double DistanceToBlockCube(double eyeX, double eyeY, double eyeZ, BlockPos pos)
    {
        // Measure to the nearest point of the block's unit cube rather than the center. This
        // deliberately favors legitimate edge/corner interactions. Eye samples use EntityPos.Y,
        // which is local to its dimension, so use the block's local Y after dimension validation.
        double blockY = pos.Y;
        double nearX = Math.Clamp(eyeX, pos.X, pos.X + 1.0);
        double nearY = Math.Clamp(eyeY, blockY, blockY + 1.0);
        double nearZ = Math.Clamp(eyeZ, pos.Z, pos.Z + 1.0);
        double dx = eyeX - nearX;
        double dy = eyeY - nearY;
        double dz = eyeZ - nearZ;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool TryDistanceToExactHit(double eyeX, double eyeY, double eyeZ, BlockSelection blockSel, out double distance)
    {
        distance = 0;
        Vec3d? hit = blockSel.HitPosition;
        BlockPos? pos = blockSel.Position;
        if (hit == null || pos == null) return false;
        if (!double.IsFinite(hit.X) || !double.IsFinite(hit.Y) || !double.IsFinite(hit.Z)) return false;

        // Malformed client hit coordinates should never be used to make the check more lenient.
        // Keep this value for evidence/diagnostics only; the enforcement distance remains the
        // conservative nearest-point-on-block-cube calculation above.
        if (hit.X < -0.25 || hit.X > 1.25 || hit.Y < -0.25 || hit.Y > 1.25 || hit.Z < -0.25 || hit.Z > 1.25)
            return false;

        double tx = pos.X + hit.X;
        double ty = pos.Y + hit.Y;
        double tz = pos.Z + hit.Z;
        double dx = eyeX - tx;
        double dy = eyeY - ty;
        double dz = eyeZ - tz;
        distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return true;
    }

    /// <summary>
    /// Handles an out-of-range reach sample. Small overages are treated as strikes and must
    /// repeat before becoming a finding/rejection. Large overages trip immediately. Near-limit
    /// strikes decay between incidents instead of remaining sticky for the entire window.
    /// Repeated callbacks for the same target inside a short duplicate window count as one strike
    /// so a single held interaction cannot self-escalate into a false positive. The duplicate
    /// window and near-strike requirement expand conservatively when measured server TPS degrades.
    /// Returns true only when the action should actually be rejected.
    /// </summary>
    private bool RegisterReachViolation(
        IServerPlayer player,
        BlockSelection blockSel,
        ReachCheckResult reach,
        string action)
    {
        BlockPos pos = blockSel.Position;
        long now = NowMs();
        PacketGuardTpsSnapshot tps = tickHealth.GetSnapshot();
        double timingMultiplier = tps.Enabled ? tps.TimingMultiplier : 1.0;
        int effectiveNearThresholdStrikes = Math.Clamp(
            (int)Math.Ceiling(config.PacketGuardReachNearThresholdStrikes * Math.Min(2.0, timingMultiplier)),
            config.PacketGuardReachNearThresholdStrikes,
            12);
        int duplicateWindowMs = Math.Clamp((int)Math.Round(300 * timingMultiplier), 300, 1500);
        string targetSignature=$"{pos.X}:{pos.InternalY}:{pos.Z}:{pos.dimension}";
        string signature = action+":"+targetSignature;

        if (!reachViolations.TryGetValue(player.PlayerUID, out ViolationCounter? counter))
        {
            counter = new ViolationCounter();
            reachViolations[player.PlayerUID] = counter;
        }
        else
        {
            long elapsed = Math.Max(0, now - counter.LastUtc);
            if (counter.LastUtc > 0 && elapsed > config.PacketGuardReachStrikeWindowMs)
            {
                counter.Count = 0;
            }
            else if (counter.LastUtc > 0 && elapsed >= config.PacketGuardReachStrikeDecayMs && counter.Count > 0)
            {
                int decay = Math.Max(1, (int)(elapsed / config.PacketGuardReachStrikeDecayMs));
                counter.Count = Math.Max(0, counter.Count - decay);
            }
        }

        bool timingReliable=!tps.Enabled||(tps.Ready&&!tps.IsDegraded);
        // Lag/warmup observations must not prime a later healthy reach punishment.
        if(!timingReliable||!counter.ReachTimingReliable){counter.Count=0;counter.FirstUtc=0;counter.LastUtc=0;counter.LastSignature="";}
        counter.ReachTimingReliable=timingReliable;
        counter.PruneSignatures(now - config.PacketGuardReachStrikeWindowMs);
        bool duplicate = counter.LastSignature == signature && now - counter.LastUtc <= duplicateWindowMs;
        double excess = Math.Max(0, reach.Distance - reach.Allowed);
        bool hard = excess >= config.PacketGuardReachHardExcess;

        if (duplicate)
        {
            bool enoughDistinct = counter.DistinctSignatureCount >= config.PacketGuardReachDistinctTargetsRequired;
            bool detectionOnlyUse = action.Equals("use", StringComparison.OrdinalIgnoreCase);
            return !detectionOnlyUse &&
                   config.PacketGuardRejectOutOfReachActions &&
                   (hard || (counter.Count >= effectiveNearThresholdStrikes && enoughDistinct)) &&
                   (!tps.SuppressTimingRejections || hard);
        }

        if(counter.Count==0)counter.FirstUtc=now;
        counter.Count++;
        counter.LastUtc = now;
        counter.LastSignature = signature;
        counter.RecordSignature(targetSignature, now);

        bool enoughStrikes = counter.Count >= effectiveNearThresholdStrikes;
        bool enoughDistinctTargets = counter.DistinctSignatureCount >= config.PacketGuardReachDistinctTargetsRequired;
        if (!hard && (!enoughStrikes || !enoughDistinctTargets)) return false;

        bool enforcementEligible=PacketReachEvidence.Eligible(action,counter.Count,counter.DistinctSignatureCount,Math.Max(0,now-counter.FirstUtc),effectiveNearThresholdStrikes,config.PacketGuardReachDistinctTargetsRequired,timingReliable);
        bool reportDue=counter.LastReportUtc==0||now-counter.LastReportUtc>=5000||(enforcementEligible&&!counter.LastReportEligible);
        if(reportDue)
        {
        counter.LastReportUtc=now;counter.LastReportEligible=enforcementEligible;
        AntiCheatSeverity severity = hard || counter.Count >= effectiveNearThresholdStrikes + 2
            ? AntiCheatSeverity.High
            : AntiCheatSeverity.Moderate;

        string hit = blockSel.HitPosition == null
            ? "none"
            : $"{blockSel.HitPosition.X:0.00},{blockSel.HitPosition.Y:0.00},{blockSel.HitPosition.Z:0.00}";
        string selectionId = GetSelectionBoxIdCompat(blockSel).Replace(';', '_').Replace('=', '_');
        string evidence =
            $"reachEnforcementEligible={enforcementEligible};confirmationSpanMs={Math.Max(0,now-counter.FirstUtc)};action={action};target={pos.X},{pos.InternalY},{pos.Z};distance={reach.Distance:0.00};" +
            $"currentDistance={reach.CurrentDistance:0.00};exactHitDistance={(reach.ExactHitDistance >= 0 ? reach.ExactHitDistance.ToString("0.00") : "n/a")};" +
            $"allowed={reach.Allowed:0.00};excess={excess:0.00};hardExcess={config.PacketGuardReachHardExcess:0.00};" +
            $"basePickingRange={player.WorldData.PickingRange:0.00};configuredTolerance={config.PacketGuardReachTolerance:0.00};" +
            $"actionAllowance={reach.ActionAllowance:0.00};latencyAllowance={reach.LatencyAllowance:0.00};pingMs={Math.Max(0, player.Ping * 1000):0};" +
            $"historyEnabled={config.PacketGuardReachPositionHistoryEnabled};historyWindowMs={config.PacketGuardReachPositionHistoryMs};historyRewindMs={reach.HistoryRewindMs};" +
            $"historyUsed={reach.UsedHistory};historySampleAgeMs={reach.HistorySampleAgeMs};" +
            $"strikes={counter.Count}/{effectiveNearThresholdStrikes};baseStrikes={config.PacketGuardReachNearThresholdStrikes};distinctTargets={counter.DistinctSignatureCount}/{config.PacketGuardReachDistinctTargetsRequired};duplicateWindowMs={duplicateWindowMs};windowMs={config.PacketGuardReachStrikeWindowMs};decayMs={config.PacketGuardReachStrikeDecayMs};" +
            $"hit={hit};selectionBoxIndex={blockSel.SelectionBoxIndex};selectionBoxId={selectionId};didOffset={blockSel.DidOffset};" +
            $"zone={(hard ? "hard" : "repeated-near")};{tps.Evidence}";

        reportFinding(
            player,
            "PACKET_BLOCK_REACH",
            severity,
            action.Equals("use", StringComparison.OrdinalIgnoreCase)
                ? "The server observed a block-use action outside the player's server-side interaction range. The action was recorded as evidence but allowed to continue so modded right-click placement flows cannot lose or desync items."
                : hard
                    ? "The server rejected a block action substantially outside the player's server-side interaction range."
                    : tps.SuppressTimingRejections
                        ? "The server observed repeated block actions outside the player's TPS-, history-, and latency-adjusted interaction range; automatic rejection was deferred during critical server lag."
                        : "The server observed repeated block actions outside the player's TPS-, history-, and latency-adjusted interaction range.",
            player.PlayerUID,
            evidence);
        }

        if (action.Equals("use", StringComparison.OrdinalIgnoreCase)) return false;
        return config.PacketGuardRejectOutOfReachActions && (!tps.SuppressTimingRejections || hard);
    }

    // ---------------------------------------------------------------------
    // Ban-safe vein-miner packet pattern validation
    // ---------------------------------------------------------------------

    private void CheckMiningBurst(IServerPlayer player, BlockSelection blockSel, ref EnumHandling handling)
    {
        BlockPos pos = blockSel.Position;
        long now = NowMs();
        PacketGuardTpsSnapshot tps = tickHealth.GetSnapshot();
        double timingMultiplier = tps.Enabled ? tps.TimingMultiplier : 1.0;
        double effectiveCadenceRatio = Math.Clamp(
            config.PacketGuardMiningCadenceSuspiciousRatio / timingMultiplier,
            0.05,
            config.PacketGuardMiningCadenceSuspiciousRatio);
        int effectiveCadenceConfirmations = Math.Clamp(
            (int)Math.Ceiling(config.PacketGuardMiningCadenceConfirmations * Math.Min(2.0, timingMultiplier)),
            config.PacketGuardMiningCadenceConfirmations,
            12);
        int effectiveCadenceWindowMs = Math.Clamp(
            (int)Math.Round(config.PacketGuardMiningCadenceConfirmationWindowMs * timingMultiplier),
            config.PacketGuardMiningCadenceConfirmationWindowMs,
            60_000);
        int effectiveDuplicateTargetMs = Math.Clamp(
            (int)Math.Round(config.PacketGuardMiningDuplicateTargetMs * timingMultiplier),
            config.PacketGuardMiningDuplicateTargetMs,
            5000);
        int effectiveConnectedBreakLimit = Math.Clamp(
            (int)Math.Ceiling(config.PacketGuardConnectedBreakLimit * timingMultiplier),
            config.PacketGuardConnectedBreakLimit,
            config.PacketGuardConnectedBreakLimit * 4);
        if (!breakHistory.TryGetValue(player.PlayerUID, out var queue))
        {
            queue = new Queue<BreakSample>();
            breakHistory[player.PlayerUID] = queue;
        }

        int historyWindow = Math.Max(config.PacketGuardVeinWindowMs, effectiveCadenceWindowMs);
        while (queue.Count > 0 && now - queue.Peek().Utc > historyWindow)
            queue.Dequeue();

        int candidateDimension = EntityRuntimeCompat.GetPos(player.Entity)?.Dimension ?? 0;
        if (queue.Any(x => x.Dimension == candidateDimension && x.X == pos.X && x.Y == pos.Y && x.Z == pos.Z &&
                           now - x.Utc <= effectiveDuplicateTargetMs))
        {
            return;
        }

        // Selection metadata can be stale or client-influenced. Mining estimates and burst
        // grouping use the loaded authoritative world block, without generating terrain.
        if(api.World.BlockAccessor.GetChunkAtBlockPos(pos)==null)return;
        bool blockFromSelection = false;
        Block? block = api.World.BlockAccessor.GetBlock(pos);
        if (block == null || block.Id <= 0)
        {
            queue.Enqueue(new BreakSample(now, pos.X, pos.Y, pos.Z, candidateDimension, 0, 0, 0, 0, 0, 0, 0));
            return;
        }

        int blockId = block.Id;
        BlockSelection miningSelection=blockSel;
        if(blockSel.Block?.Id!=block.Id){miningSelection=blockSel.Clone();miningSelection.Block=block;}
        MiningExpectation expectation = CalculateMiningExpectation(player, miningSelection, block);
        long? requestedElapsed=ingressGuard.MiningRequestElapsed(player,pos,blockId,expectation.ToolCode,now);
        if(requestedElapsed.HasValue&&expectation.Available&&expectation.ExpectedMs>=config.PacketGuardMiningCadenceMinExpectedMs&&
            requestedElapsed.Value<expectation.ExpectedMs*effectiveCadenceRatio&&
            (!miningProgressReportUtc.TryGetValue(player.PlayerUID,out long lastProgressReport)||now-lastProgressReport>=5000))
        {
            miningProgressReportUtc[player.PlayerUID]=now;
            reportFinding(player,"SERVER_ACTION_MINING_PROGRESS",AntiCheatSeverity.Info,
                "Mining completion followed a recent matching damage-request marker unusually quickly. This marker is client-originated and cannot prove completed work; review tools, perks and server timing.",player.PlayerUID,
                $"decision=observed;target={pos.X},{pos.InternalY},{pos.Z};blockId={blockId};tool={expectation.ToolCode};requestElapsedMs={requestedElapsed};expectedMs={expectation.ExpectedMs:0};reliability={expectation.Reliability};clientMarkerNotProof=True;{tps.Evidence}");
        }
        var candidate = new BreakSample(
            now, pos.X, pos.Y, pos.Z, candidateDimension, blockId,
            expectation.ExpectedMs, expectation.Resistance, expectation.MiningSpeed,
            expectation.BlockModifier, expectation.RequiredTier, expectation.ToolTier);

        if (config.PacketGuardMiningCadenceValidationEnabled &&
            expectation.Available &&
            expectation.ExpectedMs >= config.PacketGuardMiningCadenceMinExpectedMs)
        {
            BreakSample? previousAdjacent = null;
            foreach (BreakSample prior in queue)
            {
                if (prior.Dimension != candidate.Dimension || prior.BlockId <= 0 || !Adjacent26(prior, candidate)) continue;
                if (!previousAdjacent.HasValue || prior.Utc > previousAdjacent.Value.Utc) previousAdjacent = prior;
            }

            if (previousAdjacent.HasValue)
            {
                long observedMs = Math.Max(0, now - previousAdjacent.Value.Utc);
                double suspiciousBelowMs = expectation.ExpectedMs * effectiveCadenceRatio;

                // During a severe server stall, completion callbacks can be drained in the same or
                // adjacent millisecond even though the player did not actually mine blocks that fast.
                // Treat these collapsed timestamps as scheduler evidence, not player evidence.
                if (tps.SuppressTimingRejections && observedMs <= 5)
                {
                    PacketGuardTrustedState.RecordSuppression(
                        player,
                        "mining-cadence-collapsed-timestamp",
                        $"Ignored {observedMs}ms adjacent-break interval during critical TPS degradation; expectedMs={expectation.ExpectedMs:0}.",
                        1000, now);
                    queue.Enqueue(candidate);
                    return;
                }

                if (observedMs < suspiciousBelowMs)
                {
                    string cadenceKey = player.PlayerUID + (expectation.Reliability == MiningExpectationReliability.CustomUnknown ? ":uncertain" : ":authoritative");
                    int confirmations = RegisterFixedWindow(miningCadenceWindows, cadenceKey, now, effectiveCadenceWindowMs);
                    if (confirmations >= effectiveCadenceConfirmations)
                    {
                        string blockCode = block.Code?.ToString() ?? blockId.ToString();
                        string toolCode = expectation.ToolCode.Replace(';', '_').Replace('=', '_');
                        string evidence =
                            $"block={blockCode};blockId={blockId};target={pos.X},{pos.InternalY},{pos.Z};" +
                            $"expectedMs={expectation.ExpectedMs:0};observedAdjacentMs={observedMs};suspiciousBelowMs={suspiciousBelowMs:0};ratio={(expectation.ExpectedMs > 0 ? observedMs / expectation.ExpectedMs : 0):0.000};" +
                            $"baseSuspiciousRatio={config.PacketGuardMiningCadenceSuspiciousRatio:0.000};effectiveSuspiciousRatio={effectiveCadenceRatio:0.000};" +
                            $"resistance={expectation.Resistance:0.###};miningSpeed={expectation.MiningSpeed:0.###};blockModifier={expectation.BlockModifier:0.###};" +
                            $"requiredTier={expectation.RequiredTier};toolTier={expectation.ToolTier};tool={toolCode};tierAdequate={expectation.TierAdequate};" +
                            $"material={expectation.Material};materialCapable={expectation.MaterialCapable};canMineNormally={expectation.CanMineNormally};reliability={expectation.Reliability};" +
                            $"confirmations={confirmations}/{effectiveCadenceConfirmations};baseConfirmations={config.PacketGuardMiningCadenceConfirmations};confirmationWindowMs={effectiveCadenceWindowMs};baseConfirmationWindowMs={config.PacketGuardMiningCadenceConfirmationWindowMs};" +
                            $"duplicateTargetMs={effectiveDuplicateTargetMs};pingMs={Math.Max(0, player.Ping * 1000):0};{tps.Evidence}";

                        if (expectation.Reliability == MiningExpectationReliability.CustomUnknown)
                        {
                            // Custom OnGettingBroken/OnBlockBreaking paths can legitimately alter the
                            // normal resistance/speed formula. Retain the anomaly as context without
                            // adding risk or allowing it to participate in incident correlation.
                            reportFinding(
                                player,
                                "PACKET_MINING_CADENCE_UNCERTAIN",
                                AntiCheatSeverity.Info,
                                "The server observed an unusually fast mining cadence, but the block or held collectible uses custom breaking logic so the normal expected-time model is not punishment-grade evidence.",
                                player.PlayerUID,
                                evidence);
                            // Start a fresh confirmation window after emitting a finding so a long
                            // mining run does not produce one history row per subsequent block.
                            miningCadenceWindows.Remove(cadenceKey);
                        }
                        else
                        {
                            AntiCheatSeverity severity = !expectation.CanMineNormally || observedMs <= expectation.ExpectedMs * 0.15 || confirmations >= effectiveCadenceConfirmations + 2
                                ? AntiCheatSeverity.High
                                : AntiCheatSeverity.Moderate;
                            if (tps.SuppressTimingRejections && expectation.CanMineNormally && severity == AntiCheatSeverity.High)
                                severity = AntiCheatSeverity.Moderate;

                            reportFinding(
                                player,
                                "PACKET_IMPOSSIBLE_MINING_CADENCE",
                                severity,
                                tps.SuppressTimingRejections
                                    ? "The server observed repeated mining cadence evidence outside the TPS-adjusted server expectation; automatic rejection was deferred during critical server lag."
                                    : expectation.CanMineNormally
                                        ? "The server observed repeated adjacent block breaks substantially faster than the TPS-adjusted server-derived expected mining time."
                                        : "The server observed repeated break completions using a held item that does not satisfy the normal material/tier mining path for this block.",
                                player.PlayerUID,
                                evidence);
                            miningCadenceWindows.Remove(cadenceKey);

                            if (config.PacketGuardRejectImpossibleMiningCadence && !tps.SuppressTimingRejections)
                            {
                                handling = EnumHandling.PreventDefault;
                                return;
                            }
                        }
                    }
                }
            }
        }

        int extremeLimit = Math.Max(effectiveConnectedBreakLimit + 1,
            (int)Math.Ceiling(effectiveConnectedBreakLimit * config.PacketGuardConnectedBreakExtremeMultiplier));
        int connected = CountConnectedSameBlock(
            queue,
            candidate,
            extremeLimit,
            now,
            config.PacketGuardVeinWindowMs);
        if (connected >= effectiveConnectedBreakLimit)
        {
            var counter = GetCounter(miningViolations, player.PlayerUID + ":vein", now, 15_000);
            bool extreme = connected >= extremeLimit;
            bool confirmed = counter.Count >= config.PacketGuardConnectedBreakConfirmations;
            AntiCheatSeverity severity = extreme || confirmed
                ? (counter.Count >= config.PacketGuardConnectedBreakConfirmations + 1 ? AntiCheatSeverity.Critical : AntiCheatSeverity.High)
                : AntiCheatSeverity.Moderate;
            if (tps.SuppressTimingRejections && severity == AntiCheatSeverity.Critical) severity = AntiCheatSeverity.High;
            else if (tps.SuppressTimingRejections && severity == AntiCheatSeverity.High) severity = AntiCheatSeverity.Moderate;
            string code = block.Code?.ToString() ?? blockId.ToString();
            string evidence =
                $"block={code};blockId={blockId};blockSource={(blockFromSelection ? "selection" : "world")};connectedDistinct={connected};windowMs={config.PacketGuardVeinWindowMs};" +
                $"baseLimit={config.PacketGuardConnectedBreakLimit};effectiveLimit={effectiveConnectedBreakLimit};extremeLimit={extremeLimit};extreme={extreme};" +
                $"confirmations={counter.Count}/{config.PacketGuardConnectedBreakConfirmations};duplicateTargetMs={effectiveDuplicateTargetMs};target={pos.X},{pos.Y},{pos.Z};{tps.Evidence}";

            reportFinding(
                player,
                "PACKET_CONNECTED_BREAK_BURST",
                severity,
                tps.SuppressTimingRejections
                    ? "The server observed a TPS-adjusted connected block-break burst, but automatic rejection was deferred during critical server lag."
                    : extreme
                        ? "The server observed an extreme connected run of identical block breaks consistent with automated vein-mining packets."
                        : confirmed
                            ? "The server observed repeated connected block-break bursts consistent with automated vein-mining packets."
                            : "The server observed a connected block-break burst above the normal threshold; enforcement is waiting for confirmation.",
                player.PlayerUID,
                evidence);

            api.Logger.Warning(
                "[ServerEssentials/AntiCheat] PacketGuard connected break burst for {0}: {1}",
                player.PlayerName,
                evidence);

            if (config.PacketGuardRejectConnectedBreakBursts && !tps.SuppressTimingRejections && (extreme || confirmed))
            {
                handling = EnumHandling.PreventDefault;
                return;
            }
        }

        queue.Enqueue(candidate);
        while (queue.Count > effectiveConnectedBreakLimit * 6)
            queue.Dequeue();
    }

    private MiningExpectation CalculateMiningExpectation(IServerPlayer player, BlockSelection blockSel, Block block)
    {
        try
        {
            BlockPos pos = blockSel.Position;
            ItemSlot? slot = player.InventoryManager?.ActiveHotbarSlot;
            ItemStack? stack = slot?.Itemstack;
            CollectibleObject? collectible = stack?.Collectible;
            float resistance = Math.Max(0.001f, block.GetResistance(api.World.BlockAccessor, pos));
            int requiredTier = Math.Max(0, block.RequiredMiningTier);
            int toolTier = collectible != null ? Math.Max(0, collectible.ToolTier) : 0;
            EnumBlockMaterial material = block.GetBlockMaterial(api.World.BlockAccessor, pos, stack);

            bool hasHeldCollectible = collectible != null && slot != null;
            Dictionary<EnumBlockMaterial, float>? miningSpeeds = hasHeldCollectible
                ? collectible!.MiningSpeed
                : null;
            // Match the vanilla held-collectible eligibility gate: tier/material rejection applies
            // when a collectible is actually held. Empty-hand behavior is left to the normal
            // resistance/speed path rather than being promoted to impossible evidence.
            bool materialCapable = requiredTier <= 0 || !hasHeldCollectible ||
                                   (miningSpeeds != null && miningSpeeds.TryGetValue(material, out float materialSpeed) && materialSpeed > 0);
            bool tierAdequate = requiredTier <= 0 || !hasHeldCollectible || toolTier >= requiredTier;
            bool canMineNormally = requiredTier <= 0 || !hasHeldCollectible || (tierAdequate && materialCapable);

            float miningSpeed = collectible != null && stack != null
                ? Math.Max(0.001f, collectible.GetMiningSpeed(stack, blockSel, block, player))
                : 1f;

            float blockModifier = 1f;
            if (block.BlockBehaviors != null)
            {
                foreach (BlockBehavior behavior in block.BlockBehaviors)
                {
                    if (behavior == null) continue;
                    float modifier = behavior.GetMiningSpeedModifier(api.World, pos, player);
                    if (float.IsFinite(modifier) && modifier > 0) blockModifier *= modifier;
                }
            }
            blockModifier = Math.Max(0.001f, blockModifier);

            MiningExpectationReliability reliability = ClassifyMiningExpectation(block, collectible);
            if(config.PacketGuardMiningReviewOnlyTools.Contains(collectible?.Code?.ToString()??"hand",StringComparer.OrdinalIgnoreCase))reliability=MiningExpectationReliability.CustomUnknown;
            // GetMiningSpeed already incorporates the held collectible's standard and behavior
            // mining modifiers. Block behavior modifiers are separate and applied here.
            double effectiveSpeed = Math.Max(0.001, miningSpeed * blockModifier);
            double expectedMs = resistance / effectiveSpeed * 1000.0;
            // A tool that cannot use the normal material/tier path must never make a too-fast
            // completion look more legitimate. Keep a finite cap for evidence formatting only.
            if (!canMineNormally) expectedMs = 120_000;

            return new MiningExpectation(
                true, Math.Clamp(expectedMs, 1, 120_000), resistance, miningSpeed, blockModifier,
                requiredTier, toolTier, tierAdequate, materialCapable, canMineNormally, material, reliability,
                collectible?.Code?.ToString() ?? "hand");
        }
        catch
        {
            // Failure to derive an expectation disables only cadence validation for this break;
            // the independent connected/vein-burst detector still runs.
            return MiningExpectation.Unavailable;
        }
    }

    private static MiningExpectationReliability ClassifyMiningExpectation(Block block, CollectibleObject? collectible)
    {
        try
        {
            Type[] blockBreakingSignature =
            {
                typeof(IPlayer), typeof(BlockSelection), typeof(ItemSlot),
                typeof(float), typeof(float), typeof(int)
            };
            var blockBreaking = block.GetType().GetMethod(nameof(Block.OnGettingBroken), blockBreakingSignature);
            if (HasForeignMiningPatch(blockBreaking))return MiningExpectationReliability.CustomUnknown;
            if (blockBreaking != null && blockBreaking.DeclaringType != typeof(Block))
                return MiningExpectationReliability.CustomUnknown;

            // Behaviors can alter the remaining-resistance path even when the Block/Item class
            // itself does not override the top-level method. Treat those as custom rather than
            // pretending the resistance/speed estimate is authoritative.
            if (HasCustomBreakingBehavior(block.BlockBehaviors))
                return MiningExpectationReliability.CustomUnknown;

            if (collectible != null)
            {
                if (HasCustomBreakingBehavior(collectible.CollectibleBehaviors))
                    return MiningExpectationReliability.CustomUnknown;
                var heldBreaking = collectible.GetType().GetMethod(nameof(CollectibleObject.OnBlockBreaking), blockBreakingSignature);
                if (HasForeignMiningPatch(heldBreaking))return MiningExpectationReliability.CustomUnknown;
                if (heldBreaking != null && heldBreaking.DeclaringType != typeof(CollectibleObject))
                    return MiningExpectationReliability.CustomUnknown;

                Type[] miningSpeedSignature = { typeof(IItemStack), typeof(BlockSelection), typeof(Block), typeof(IPlayer) };
                var miningSpeed = collectible.GetType().GetMethod(nameof(CollectibleObject.GetMiningSpeed), miningSpeedSignature);
                if (HasForeignMiningPatch(miningSpeed))return MiningExpectationReliability.CustomUnknown;
                if (miningSpeed != null && miningSpeed.DeclaringType != typeof(CollectibleObject))
                    return MiningExpectationReliability.Compatible;
            }

            Type[] resistanceSignature = { typeof(IBlockAccessor), typeof(BlockPos) };
            var resistance = block.GetType().GetMethod(nameof(Block.GetResistance), resistanceSignature);
            if (HasForeignMiningPatch(resistance))return MiningExpectationReliability.CustomUnknown;
            if (resistance != null && resistance.DeclaringType != typeof(Block))
                return MiningExpectationReliability.Compatible;

            return MiningExpectationReliability.Authoritative;
        }
        catch
        {
            return MiningExpectationReliability.CustomUnknown;
        }
    }

    private static bool HasForeignMiningPatch(MethodInfo? method)
    {
        if(method==null)return false;
        var patches=HarmonyLib.Harmony.GetPatchInfo(method);
        return patches!=null&&patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers)
            .Any(p=>!p.owner.StartsWith("newfies.serveressentials",StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasCustomBreakingBehavior(IEnumerable<CollectibleBehavior>? behaviors)
    {
        if (behaviors == null) return false;
        Type[] signature =
        {
            typeof(IPlayer), typeof(BlockSelection), typeof(ItemSlot),
            typeof(float), typeof(float), typeof(int), typeof(EnumHandling).MakeByRefType()
        };

        foreach (CollectibleBehavior behavior in behaviors)
        {
            if (behavior == null) continue;
            var method = behavior.GetType().GetMethod(nameof(CollectibleBehavior.OnBlockBreaking), signature);
            if (method != null && method.DeclaringType != typeof(CollectibleBehavior)) return true;
        }
        return false;
    }

    /// <summary>
    /// Counts the connected component of same-block break samples containing the current break.
    /// 26-neighbour adjacency is intentional because ModMenu's public vein search also treats
    /// diagonal blocks as connected.
    /// </summary>
    private static int CountConnectedSameBlock(Queue<BreakSample> history, BreakSample current, int stopAt, long now, int windowMs)
    {
        long cutoff = now - Math.Max(1, windowMs);
        var same = history
            .Where(x => x.Utc >= cutoff && x.BlockId == current.BlockId && x.Dimension == current.Dimension)
            .GroupBy(x => (x.X, x.Y, x.Z, x.Dimension))
            .Select(g => g.OrderByDescending(x => x.Utc).First())
            .Where(x => x.X != current.X || x.Y != current.Y || x.Z != current.Z || x.Dimension != current.Dimension)
            .ToList();
        // Always append the current break last so the flood-fill root is deterministic.
        same.Add(current);

        var connected = new List<BreakSample> { current };
        var used = new bool[same.Count];
        used[same.Count - 1] = true;

        bool grew;
        do
        {
            grew = false;
            for (int i = 0; i < same.Count; i++)
            {
                if (used[i]) continue;
                var sample = same[i];
                if (!connected.Any(x => Adjacent26(x, sample))) continue;
                used[i] = true;
                connected.Add(sample);
                grew = true;
                if (connected.Count >= stopAt) return connected.Count;
            }
        } while (grew);

        return connected.Count;
    }

    private static bool Adjacent26(BreakSample a, BreakSample b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        int dz = Math.Abs(a.Z - b.Z);
        return dx <= 1 && dy <= 1 && dz <= 1 && (dx != 0 || dy != 0 || dz != 0);
    }

    private static ViolationCounter GetCounter(Dictionary<string, ViolationCounter> counters, string uid, long now, long windowMs)
    {
        if (!counters.TryGetValue(uid, out var counter) || now - counter.LastUtc > windowMs)
        {
            counter = new ViolationCounter();
            counters[uid] = counter;
        }
        counter.Count++;
        counter.LastUtc = now;
        return counter;
    }

    private static int RegisterFixedWindow(Dictionary<string, Queue<long>> windows, string key, long now, long windowMs)
    {
        if (!windows.TryGetValue(key, out Queue<long>? timestamps))
        {
            timestamps = new Queue<long>();
            windows[key] = timestamps;
        }

        long cutoff = now - Math.Max(1, windowMs);
        while (timestamps.Count > 0 && timestamps.Peek() < cutoff) timestamps.Dequeue();
        timestamps.Enqueue(now);
        while (timestamps.Count > 16) timestamps.Dequeue();
        return timestamps.Count;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        if (!started) return;
        started = false;
        ingressGuard.Dispose();
        actionChecks?.Dispose();
        actionChecks=null;
        RemoveServerTeleportTrustHook();

        api.Event.PlayerNowPlaying -= ResetPlayer;
        api.Event.PlayerRespawn -= ResetPlayer;
        api.Event.PlayerDisconnect -= RemovePlayer;
        api.Event.PlayerChat -= OnPlayerChat;
        api.Event.BreakBlock -= OnBreakBlock;
        api.Event.CanUseBlock -= OnCanUseBlock;
        api.Event.PlayerDimensionChanged -= OnPlayerDimensionChanged;
        if (sharedSamplerTickId != 0) api.Event.UnregisterGameTickListener(sharedSamplerTickId);

        sharedSamplerTickId = 0;
        sampledPlayers.Clear();
        sampledPlayerPositions.Clear();
        tickHealth.Dispose();
        movement.Clear();
        miningProgressReportUtc.Clear();
        flightReviews.Clear();
        breakHistory.Clear();
        reachViolations.Clear();
        reachPositionHistory.Clear();
        miningViolations.Clear();
        miningCadenceWindows.Clear();
        trustedTransitionSamples.Clear();
        pendingTranslocatorTransitions.Clear();
        th3EssentialsSpawnIntents.Clear();
        // These grants are service-owned. Do not carry a stale grace window across a dashboard
        // settings reload/restart of PacketGuard.
        PacketGuardTrustedState.ClearAll();
    }

    [Flags]
    private enum MovementRiskSignal
    {
        None = 0,
        HardMove = 1,
        Speed = 2,
        Acceleration = 4,
        Noclip = 8,
        Mount = 16,
        RollbackResistance = 32
    }

    private sealed class MovementState
    {
        public double X;
        public double Y;
        public double Z;
        public double SafeX;
        public double SafeY;
        public double SafeZ;
        public int Dimension;
        public long EntityId;
        public long LastAcceptedUtc;
        public long GraceUntilUtc;
        public long LastViolationUtc;
        public int ViolationCount;
        public readonly NoclipConfirmationWindow NoclipConfirmation = new();
        public readonly NoclipCorrectionHistory NoclipCorrections = new();
        public readonly PendingNoclipCorrection PendingNoclipCorrection = new();
        public readonly CorrectionRecoveryGuard CorrectionRecovery = new();
        public readonly VanillaGliderAllowance Glider = new();
        public bool WasGliding;
        public bool WasMounted;
        public int MountedSuspiciousSamples;
        public long MountedSuspiciousSinceUtc;
        public double LastMountedHorizontalSpeed;
        public long LastMountedSpeedUtc;
        public int SuspiciousCount;
        public long SuspiciousSinceUtc;
        public long SuspiciousLastUtc;
        public long SpeedWindowStartUtc;
        public double SpeedWindowDistance;
        public int SpeedSuspiciousWindows;
        public double LastClosedWindowSpeed;
        public double LastInstantHorizontalSpeed;
        public double SmoothedHorizontalSpeed;
        public long LastInstantSpeedUtc;
        public int AccelerationSuspiciousSamples;
        public long AccelerationSuspiciousSinceUtc;
        public int EndpointCollisionSamples;
        public double EndpointCollisionStartX;
        public double EndpointCollisionStartZ;
        public int EndpointCollisionDimension;
        public double MovementRiskScore;
        public long MovementRiskUpdatedUtc;
        public MovementRiskSignal MovementRiskSignalMask;
        public long MovementRiskSignalWindowStartUtc;
        public double RiskHardMove;
        public double RiskSpeed;
        public double RiskAcceleration;
        public double RiskNoclip;
        public double RiskMount;
        public double RiskRollbackResistance;
        public double RiskCorrelation;
        public long LastRollbackUtc;
        public int RollbackLoopCount;
        public bool RollbackResistanceCounted;
        public string LastFindingCode = "";
        public long LastFindingUtc;
        public int UnverifiedTeleportCount;
        public long UnverifiedTeleportWindowStartUtc;
        public long TrackedMountEntityId;
        public double MountX;
        public double MountY;
        public double MountZ;
        public long MountPosUtc;
        public readonly List<SafePositionSample> SafePositions = new();
        public readonly Queue<DiagnosticSpeedSample> DiagnosticSpeeds = new();

        public static MovementState From(EntityPos pos, long now, long entityId)
        {
            var state = new MovementState { EntityId = entityId };
            state.SafeX = pos.X;
            state.SafeY = pos.Y;
            state.SafeZ = pos.Z;
            state.SpeedWindowStartUtc = now;
            state.Accept(pos, now);
            return state;
        }

        public void Accept(EntityPos pos, long now, bool markSafe = true)
        {
            X = pos.X;
            Y = pos.Y;
            Z = pos.Z;
            Dimension = pos.Dimension;
            LastAcceptedUtc = now;
            if (markSafe)
            {
                SafeX = pos.X;
                SafeY = pos.Y;
                SafeZ = pos.Z;
                AddSafePosition(pos, now);
            }
            SuspiciousCount = 0;
            SuspiciousSinceUtc = 0;
            SuspiciousLastUtc = 0;
        }

        public void AcceptRollback(SafePositionSample sample, long now)
        {
            X = sample.X;
            Y = sample.Y;
            Z = sample.Z;
            Dimension = sample.Dimension;
            SafeX = sample.X;
            SafeY = sample.Y;
            SafeZ = sample.Z;
            LastAcceptedUtc = now;
            SuspiciousCount = 0;
            SuspiciousSinceUtc = 0;
            SuspiciousLastUtc = 0;
        }

        private void AddSafePosition(EntityPos pos, long now)
        {
            if (SafePositions.Count > 0)
            {
                SafePositionSample last = SafePositions[SafePositions.Count - 1];
                double dx = pos.X - last.X;
                double dy = pos.Y - last.Y;
                double dz = pos.Z - last.Z;
                if (last.Dimension == pos.Dimension && dx * dx + dy * dy + dz * dz < 0.0025) return;
            }

            SafePositions.Add(new SafePositionSample(pos.X, pos.Y, pos.Z, pos.Dimension, now));
            // Keep a generous internal ring; configured selection limits are applied at rollback.
            if (SafePositions.Count > 64) SafePositions.RemoveRange(0, SafePositions.Count - 64);
        }

        public SafePositionSample GetRollbackPosition(long now, int safetyLagMs, int maxAgeMs, int historySize)
        {
            long latestAllowed = now - Math.Max(0, safetyLagMs);
            long oldestAllowed = now - Math.Max(1000, maxAgeMs);
            int oldestIndex = Math.Max(0, SafePositions.Count - Math.Max(1, historySize));
            for (int i = SafePositions.Count - 1; i >= oldestIndex; i--)
            {
                SafePositionSample sample = SafePositions[i];
                if (sample.Utc > latestAllowed) continue;
                if (sample.Utc < oldestAllowed) break;
                if (sample.Dimension == Dimension) return sample;
            }

            return new SafePositionSample(SafeX, SafeY, SafeZ, Dimension, LastAcceptedUtc);
        }

        public IEnumerable<SafePositionSample> GetRollbackCandidates(long now, int safetyLagMs, int maxAgeMs, int historySize, int maxCandidates)
        {
            long latestAllowed = now - Math.Max(0, safetyLagMs);
            long oldestAllowed = now - Math.Max(1000, maxAgeMs);
            int oldestIndex = Math.Max(0, SafePositions.Count - Math.Max(1, historySize));
            int yielded = 0;
            for (int i = SafePositions.Count - 1; i >= oldestIndex && yielded < Math.Max(1, maxCandidates); i--)
            {
                SafePositionSample sample = SafePositions[i];
                if (sample.Utc > latestAllowed) continue;
                if (sample.Utc < oldestAllowed) break;
                if (sample.Dimension != Dimension) continue;
                yielded++;
                yield return sample;
            }
        }

        public void NoteRollback(long now)
        {
            if (LastRollbackUtc > 0 && now - LastRollbackUtc <= 5000) RollbackLoopCount++;
            else RollbackLoopCount = 1;
            LastRollbackUtc = now;
            RollbackResistanceCounted = false;
        }

        public int RegisterUnverifiedTeleport(long now, int windowMs)
        {
            if (UnverifiedTeleportWindowStartUtc <= 0 || now - UnverifiedTeleportWindowStartUtc > Math.Max(1000, windowMs))
            {
                UnverifiedTeleportWindowStartUtc = now;
                UnverifiedTeleportCount = 1;
            }
            else
            {
                UnverifiedTeleportCount++;
            }
            return UnverifiedTeleportCount;
        }

        public bool ShouldReportFinding(string code, long now, int cooldownMs)
        {
            if (cooldownMs <= 0 || !string.Equals(code, LastFindingCode, StringComparison.Ordinal) || now - LastFindingUtc >= cooldownMs)
            {
                LastFindingCode = code;
                LastFindingUtc = now;
                return true;
            }
            return false;
        }

        public bool ObserveMountDisplacement(long mountEntityId, EntityPos mountPos, long now, out double horizontalDistance, out long elapsedMs)
        {
            horizontalDistance = 0;
            elapsedMs = 0;
            if (TrackedMountEntityId != mountEntityId || MountPosUtc <= 0 || now <= MountPosUtc)
            {
                TrackedMountEntityId = mountEntityId;
                MountX = mountPos.X; MountY = mountPos.Y; MountZ = mountPos.Z; MountPosUtc = now;
                return false;
            }
            double dx = mountPos.X - MountX;
            double dz = mountPos.Z - MountZ;
            horizontalDistance = Math.Sqrt(dx * dx + dz * dz);
            elapsedMs = now - MountPosUtc;
            MountX = mountPos.X; MountY = mountPos.Y; MountZ = mountPos.Z; MountPosUtc = now;
            return elapsedMs > 0;
        }

        public double ObserveAcceleration(
            double instantaneousSpeed, long now,
            double minSpeed, double accelerationLimit, int confirmWindowMs,
            int sampleMinMs, double smoothingFactor,
            out int suspiciousSamples, out double smoothedSpeed)
        {
            double alpha = Math.Clamp(smoothingFactor, 0.05, 1.0);
            if (SmoothedHorizontalSpeed <= 0) SmoothedHorizontalSpeed = instantaneousSpeed;
            else SmoothedHorizontalSpeed += (instantaneousSpeed - SmoothedHorizontalSpeed) * alpha;
            smoothedSpeed = SmoothedHorizontalSpeed;

            if (LastInstantSpeedUtc <= 0 || now <= LastInstantSpeedUtc)
            {
                LastInstantHorizontalSpeed = SmoothedHorizontalSpeed;
                LastInstantSpeedUtc = now;
                suspiciousSamples = AccelerationSuspiciousSamples;
                return 0;
            }

            long elapsedMs = now - LastInstantSpeedUtc;
            if (elapsedMs < Math.Max(1, sampleMinMs))
            {
                suspiciousSamples = AccelerationSuspiciousSamples;
                return 0;
            }

            double elapsedSeconds = elapsedMs / 1000.0;
            double acceleration = Math.Max(0, SmoothedHorizontalSpeed - LastInstantHorizontalSpeed) / elapsedSeconds;
            bool suspicious = SmoothedHorizontalSpeed >= minSpeed && acceleration > accelerationLimit;
            if (suspicious)
            {
                if (AccelerationSuspiciousSamples == 0 ||
                    now - AccelerationSuspiciousSinceUtc > confirmWindowMs)
                {
                    AccelerationSuspiciousSamples = 1;
                    AccelerationSuspiciousSinceUtc = now;
                }
                else
                {
                    AccelerationSuspiciousSamples++;
                }
            }
            else
            {
                // Confirmation must be consecutive evaluated samples. A normal sample breaks the
                // chain instead of allowing unrelated jitter spikes to combine across the window.
                AccelerationSuspiciousSamples = 0;
                AccelerationSuspiciousSinceUtc = 0;
            }

            LastInstantHorizontalSpeed = SmoothedHorizontalSpeed;
            LastInstantSpeedUtc = now;
            suspiciousSamples = AccelerationSuspiciousSamples;
            return acceleration;
        }

        public long EndpointCollisionSinceUtc, EndpointCollisionLastUtc;
        public double ObserveEndpointPenetration(
            double previousX, double previousZ, double currentX, double currentZ, int dimension,long now)
        {
            if (EndpointCollisionSamples <= 0 || EndpointCollisionDimension != dimension || now<=EndpointCollisionLastUtc || now-EndpointCollisionLastUtc>1000)
            {
                EndpointCollisionSinceUtc=now;
                EndpointCollisionStartX = previousX;
                EndpointCollisionStartZ = previousZ;
                EndpointCollisionDimension = dimension;
                EndpointCollisionSamples = 1;
            }
            else
            {
                EndpointCollisionSamples++;
            }

            EndpointCollisionLastUtc=now;
            double dx = currentX - EndpointCollisionStartX;
            double dz = currentZ - EndpointCollisionStartZ;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        public void ResetEndpointPenetration()
        {
            EndpointCollisionSamples = 0;
            EndpointCollisionSinceUtc=EndpointCollisionLastUtc=0;
            EndpointCollisionStartX = 0;
            EndpointCollisionStartZ = 0;
            EndpointCollisionDimension = 0;
        }

        public double ObserveMountedAcceleration(double horizontalSpeed, long now, double accelerationLimit)
        {
            double acceleration = 0;
            if (LastMountedSpeedUtc > 0 && now > LastMountedSpeedUtc)
            {
                double elapsedSeconds = (now - LastMountedSpeedUtc) / 1000.0;
                acceleration = Math.Max(0, horizontalSpeed - LastMountedHorizontalSpeed) / elapsedSeconds;
            }
            LastMountedHorizontalSpeed = horizontalSpeed;
            LastMountedSpeedUtc = now;
            return acceleration;
        }

        public void ObserveMountedViolation(long now, int confirmWindowMs)
        {
            if (MountedSuspiciousSamples == 0 || now - MountedSuspiciousSinceUtc > confirmWindowMs)
            {
                MountedSuspiciousSamples = 1;
                MountedSuspiciousSinceUtc = now;
            }
            else MountedSuspiciousSamples++;
        }

        public void ResetMountedViolation()
        {
            MountedSuspiciousSamples = 0;
            MountedSuspiciousSinceUtc = 0;
        }

        public void DecayMovementRisk(long now, bool enabled, double decayPerSecond)
        {
            if (!enabled)
            {
                MovementRiskScore = 0;
                RiskHardMove = RiskSpeed = RiskAcceleration = RiskNoclip = RiskMount = RiskRollbackResistance = RiskCorrelation = 0;
                MovementRiskUpdatedUtc = now;
                return;
            }

            if (MovementRiskUpdatedUtc <= 0)
            {
                MovementRiskUpdatedUtc = now;
                return;
            }

            long elapsedMs = Math.Max(0, now - MovementRiskUpdatedUtc);
            if (elapsedMs > 0)
            {
                double seconds = elapsedMs / 1000.0;
                RiskHardMove = Math.Max(0, RiskHardMove - decayPerSecond * 1.0 * seconds);
                RiskSpeed = Math.Max(0, RiskSpeed - decayPerSecond * 1.2 * seconds);
                RiskAcceleration = Math.Max(0, RiskAcceleration - decayPerSecond * 1.6 * seconds);
                RiskNoclip = Math.Max(0, RiskNoclip - decayPerSecond * 0.5 * seconds);
                RiskMount = Math.Max(0, RiskMount - decayPerSecond * 0.9 * seconds);
                RiskRollbackResistance = Math.Max(0, RiskRollbackResistance - decayPerSecond * 0.6 * seconds);
                RiskCorrelation = Math.Max(0, RiskCorrelation - decayPerSecond * 1.0 * seconds);
                MovementRiskScore = Math.Clamp(RiskHardMove + RiskSpeed + RiskAcceleration + RiskNoclip + RiskMount + RiskRollbackResistance + RiskCorrelation, 0, 100);
            }
            MovementRiskUpdatedUtc = now;
            if (MovementRiskScore <= 0.001)
            {
                MovementRiskSignalMask = MovementRiskSignal.None;
                MovementRiskSignalWindowStartUtc = 0;
                RollbackLoopCount = 0;
            }
        }

        public double AddMovementRisk(
            bool enabled, MovementRiskSignal signal, double amount, long now,
            int correlationWindowMs, double correlationBonus)
        {
            if (!enabled) return 0;
            if (MovementRiskUpdatedUtc <= 0) MovementRiskUpdatedUtc = now;

            if (MovementRiskSignalWindowStartUtc <= 0 ||
                now - MovementRiskSignalWindowStartUtc > Math.Max(250, correlationWindowMs))
            {
                MovementRiskSignalWindowStartUtc = now;
                MovementRiskSignalMask = MovementRiskSignal.None;
            }

            double add = Math.Max(0, amount);
            bool newSignal = signal != MovementRiskSignal.None && (MovementRiskSignalMask & signal) == 0;
            if (newSignal && MovementRiskSignalMask != MovementRiskSignal.None)
                RiskCorrelation += Math.Max(0, correlationBonus);

            switch (signal)
            {
                case MovementRiskSignal.HardMove: RiskHardMove += add; break;
                case MovementRiskSignal.Speed: RiskSpeed += add; break;
                case MovementRiskSignal.Acceleration: RiskAcceleration += add; break;
                case MovementRiskSignal.Noclip: RiskNoclip += add; break;
                case MovementRiskSignal.Mount: RiskMount += add; break;
                case MovementRiskSignal.RollbackResistance: RiskRollbackResistance += add; break;
            }

            MovementRiskSignalMask |= signal;
            MovementRiskScore = Math.Clamp(RiskHardMove + RiskSpeed + RiskAcceleration + RiskNoclip + RiskMount + RiskRollbackResistance + RiskCorrelation, 0, 100);
            MovementRiskUpdatedUtc = now;
            return MovementRiskScore;
        }

        public string GetMovementConfidenceBand(double guarded, double high, double critical)
        {
            if (MovementRiskScore >= critical) return "critical";
            if (MovementRiskScore >= high) return "high";
            if (MovementRiskScore >= guarded) return "guarded";
            return "low";
        }

        public string GetMovementConfidenceSignals()
        {
            if (MovementRiskSignalMask == MovementRiskSignal.None) return "none";
            var parts = new List<string>(5);
            if ((MovementRiskSignalMask & MovementRiskSignal.HardMove) != 0) parts.Add("hardmove");
            if ((MovementRiskSignalMask & MovementRiskSignal.Speed) != 0) parts.Add("speed");
            if ((MovementRiskSignalMask & MovementRiskSignal.Acceleration) != 0) parts.Add("acceleration");
            if ((MovementRiskSignalMask & MovementRiskSignal.Noclip) != 0) parts.Add("noclip");
            if ((MovementRiskSignalMask & MovementRiskSignal.Mount) != 0) parts.Add("mount");
            if ((MovementRiskSignalMask & MovementRiskSignal.RollbackResistance) != 0) parts.Add("rollback-resistance");
            return string.Join(",", parts);
        }

        public void AddDiagnosticSpeedSample(double speed, long now, int maxSamples)
        {
            DiagnosticSpeeds.Enqueue(new DiagnosticSpeedSample(now, Math.Max(0, speed)));
            int keep = Math.Clamp(maxSamples, 3, 16);
            while (DiagnosticSpeeds.Count > keep) DiagnosticSpeeds.Dequeue();
        }

        public string GetDiagnosticSpeedHistory()
        {
            if (DiagnosticSpeeds.Count == 0) return "none";
            return string.Join(",", DiagnosticSpeeds.Select(x => x.Speed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        }

        public void AddSpeedSample(double horizontalDistance, long now)
        {
            if (SpeedWindowStartUtc <= 0) SpeedWindowStartUtc = now;
            SpeedWindowDistance += Math.Max(0, horizontalDistance);
        }

        public bool TryCloseSpeedWindow(
            long now,
            int windowMs,
            double speedLimit,
            int requiredWindows,
            out double observedSpeed,
            out int confirmedWindows)
        {
            observedSpeed = 0;
            confirmedWindows = SpeedSuspiciousWindows;
            long elapsed = now - SpeedWindowStartUtc;
            if (elapsed < windowMs) return false;

            observedSpeed = elapsed > 0 ? SpeedWindowDistance * 1000.0 / elapsed : 0;
            LastClosedWindowSpeed = observedSpeed;
            if (observedSpeed > speedLimit) SpeedSuspiciousWindows++;
            else SpeedSuspiciousWindows = 0;

            confirmedWindows = SpeedSuspiciousWindows;
            SpeedWindowStartUtc = now;
            SpeedWindowDistance = 0;
            return SpeedSuspiciousWindows >= requiredWindows;
        }

        public bool ObserveGliding(bool equipped, bool flag, long now, long entityId, int dimension)
        {
            bool gliding = Glider.Observe(equipped, flag, now, entityId, dimension);
            if (gliding != WasGliding)
            {
                // Preserve collision evidence; only mode-dependent timing windows restart.
                SpeedWindowStartUtc = now; SpeedWindowDistance = 0;
                SpeedSuspiciousWindows = 0; LastClosedWindowSpeed = 0;
                LastInstantSpeedUtc = 0; SmoothedHorizontalSpeed = 0;
                AccelerationSuspiciousSamples = 0; AccelerationSuspiciousSinceUtc = 0;
                WasGliding = gliding;
            }
            return gliding;
        }

        public void ResetAnalysis(long now)
        {
            SuspiciousCount = 0;
            SuspiciousSinceUtc = 0;
            SuspiciousLastUtc = 0;
            SpeedWindowStartUtc = now;
            SpeedWindowDistance = 0;
            SpeedSuspiciousWindows = 0;
            LastClosedWindowSpeed = 0;
            LastInstantHorizontalSpeed = 0;
            LastInstantSpeedUtc = 0;
            AccelerationSuspiciousSamples = 0;
            AccelerationSuspiciousSinceUtc = 0;
            EndpointCollisionSamples = 0;
        }
    }

    private readonly record struct DiagnosticSpeedSample(long Utc, double Speed);

    private readonly record struct SafePositionSample(double X, double Y, double Z, int Dimension, long Utc);

    private sealed class ViolationCounter
    {
        public int Count;
        public long LastUtc;
        public string LastSignature = "";
        public long FirstUtc,LastReportUtc;public bool LastReportEligible,ReachTimingReliable;
        private readonly List<string> expiredSignatures=new();
        private readonly Dictionary<string, long> signatures = new(StringComparer.Ordinal);
        public int DistinctSignatureCount => signatures.Count;

        public void RecordSignature(string signature, long now)
        {
            if (!string.IsNullOrEmpty(signature)&&(signatures.Count<128||signatures.ContainsKey(signature))) signatures[signature] = now;
        }

        public void PruneSignatures(long cutoffUtc)
        {
            if (signatures.Count == 0) return;
            expiredSignatures.Clear();foreach(var item in signatures)if(item.Value<cutoffUtc)expiredSignatures.Add(item.Key);
            foreach(string key in expiredSignatures)signatures.Remove(key);
            expiredSignatures.Clear();
            if (Count == 0 && signatures.Count > 0) signatures.Clear();
        }
    }

    private readonly record struct ReachPositionSample(long Utc, double EyeX, double EyeY, double EyeZ, int Dimension, long EntityId);

    private sealed class PendingTranslocatorTransition
    {
        public int SourceX;
        public int SourceY;
        public int SourceZ;
        public int SourceDimension;
        public long FirstSeenUtc;
        public long LastSeenUtc;
        public bool Armed;
        public bool HasLinkedTarget;
        public int TargetX;
        public int TargetY;
        public int TargetZ;
        public int TargetDimension;
    }

    private readonly record struct TrustedTransitionSample(double X, double Y, double Z, int Dimension, bool Mounted)
    {
        public static TrustedTransitionSample From(EntityPos pos, bool mounted) =>
            new(pos.X, pos.Y, pos.Z, pos.Dimension, mounted);
    }

    private readonly record struct ReachCheckResult(
        double Distance,
        double Allowed,
        double LatencyAllowance,
        double ActionAllowance,
        double CurrentDistance,
        double ExactHitDistance,
        bool UsedHistory,
        long HistorySampleAgeMs,
        int HistoryRewindMs,
        bool TeleportGrace,
        bool PositionUnavailable)
    {
        public static ReachCheckResult AllowedByTeleportGrace => new(
            0, double.MaxValue, 0, 0, 0, -1, false, 0, 0, true, false);

        public static ReachCheckResult Unavailable => new(
            0, double.MaxValue, 0, 0, 0, -1, false, 0, 0, false, true);
    }

    private readonly record struct BreakSample(
        long Utc, int X, int Y, int Z, int Dimension, int BlockId,
        double ExpectedMs, float Resistance, float MiningSpeed, float BlockModifier, int RequiredTier, int ToolTier);

    private enum MiningExpectationReliability
    {
        Authoritative = 0,
        Compatible = 1,
        CustomUnknown = 2
    }

    private readonly record struct MiningExpectation(
        bool Available, double ExpectedMs, float Resistance, float MiningSpeed, float BlockModifier,
        int RequiredTier, int ToolTier, bool TierAdequate, bool MaterialCapable, bool CanMineNormally,
        EnumBlockMaterial Material, MiningExpectationReliability Reliability, string ToolCode)
    {
        public static MiningExpectation Unavailable => new(
            false, 0, 0, 0, 0, 0, 0, true, true, true, EnumBlockMaterial.Other,
            MiningExpectationReliability.CustomUnknown, "unknown");
    }
}
