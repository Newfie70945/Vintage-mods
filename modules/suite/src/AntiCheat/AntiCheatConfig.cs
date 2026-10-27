using System;
using System.Collections.Generic;
using System.Linq;
using NewfiesAntiCheat.Shared;
using Newtonsoft.Json;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatConsequenceRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New consequence";
    public bool Enabled { get; set; } = true;
    /// <summary>FindingCode, Status, or RiskAtLeast.</summary>
    public string TriggerType { get; set; } = "FindingCode";
    public string TriggerValue { get; set; } = "FORBIDDEN_MOD";
    /// <summary>Optional exact finding subject, such as a specific mod ID.</summary>
    public string Subject { get; set; } = "";
    /// <summary>Log, Kick, TempBan, or Freeze.</summary>
    public string Action { get; set; } = "Log";
    /// <summary>Used by TempBan and Freeze; e.g. 10m, 6h, 3d, 2w.</summary>
    public string Duration { get; set; } = "30m";
    public string Message { get; set; } = "Anti-cheat policy matched: {code} {subject}.";
}


public sealed class AntiCheatReleaseAssemblyFingerprint
{
    public string AssemblyName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int StructureTokenCount { get; set; }
    public List<ulong> SimilarityMinHash { get; set; } = new();
    public string BehaviorSha256 { get; set; } = "";
    public int BehaviorTokenCount { get; set; }
    public List<ulong> BehaviorMinHash { get; set; } = new();
    public long FileSizeBytes { get; set; }
}

public sealed class AntiCheatReleaseFingerprint
{
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public long PackageBytes { get; set; }
    public int DllCount { get; set; }
    public long DllBytes { get; set; }
    public long ResolvedUtc { get; set; }
    public List<AntiCheatReleaseAssemblyFingerprint> Assemblies { get; set; } = new();
}

public sealed class AntiCheatManualFingerprintEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Manual fingerprint";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Notes { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public List<string> AssemblySha256s { get; set; } = new();
    public List<string> AssemblyMvids { get; set; } = new();
    public List<string> AssemblyStructureSha256s { get; set; } = new();
    public List<string> AssemblyBehaviorSha256s { get; set; } = new();
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}

public sealed class AntiCheatForbiddenFingerprint
{
    public string ModId { get; set; } = "";
    public string Source { get; set; } = "";
    public string ResolvedVersion { get; set; } = "";
    public string ResolvedFileName { get; set; } = "";
    public long ResolvedUtc { get; set; }
    public string PackageSha256 { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public List<string> AssemblyNames { get; set; } = new();
    public List<string> AssemblySha256s { get; set; } = new();
    public List<string> AssemblyMvids { get; set; } = new();
    public List<string> AssemblyStructureSha256s { get; set; } = new();
    public List<string> AssemblyBehaviorSha256s { get; set; } = new();
    public List<string> ModSystemTypeNames { get; set; } = new();
    public List<AntiCheatReleaseFingerprint> Releases { get; set; } = new();
    public List<AntiCheatManualFingerprintEntry> ManualEntries { get; set; } = new();
}


public sealed class AntiCheatBlacklistProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Blacklist profile";
    public List<string> ModIds { get; set; } = new();
    // Snapshot of the exact fingerprints that belong to this profile. Loading a profile copies
    // both IDs and hashes into the active editable blacklist.
    public List<AntiCheatForbiddenFingerprint> Fingerprints { get; set; } = new();
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}

public sealed class AntiCheatConfig
{
    public const int CurrentSchemaVersion = 62;
    public static readonly string[] DefaultForbiddenModIds =
    {
        "prosping",
        "xrayblock",
        "blocksoverlay",
        "amfinder",
        "orewhisper",
        "prospectingscan",
        "gimap",
        "birdseye",
        "subtleparticlesplus",
        "nf3ttranslocatorfindercmd",
        "findanyblock",
        "modmenu",
        "autoattack",
        "clayformerprescko",
        "freeroamcam",
        "mobfinder",
        "smithingmacrodebug",
        "stepuptwoblocks",
        "zinkmodautoclayupdate"
    };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Ore Shield virtualizes hidden geology in outgoing server packets. The server world remains real;
    // Ore Shield uses OreShield* keys. Legacy OreGuard* setters below are read-only migration aliases.
    // clients receive host rock until a protected ore is legitimately exposed. Legacy decoy fields remain for config compatibility only.
    public bool OreShieldEnabled { get; set; } = false;
    public bool OreShieldProtectModdedOres { get; set; } = true;
    // Legacy compatibility switch. Secure Ore Shield always treats unknown/unloaded neighbors as
    // concealed so chunk streaming can never leak a real buried ore ID. Clamp() forces this false.
    public bool OreShieldRevealOnUnknownNeighbor { get; set; } = false;
    // Restrict automatic ore discovery to real ore material / exact ore-name tokens. Admin
    // AdditionalOrePatterns remain the explicit escape hatch for unusual modded deposits.
    public bool OreShieldStrictOreMatching { get; set; } = true;
    // Legacy compatibility field from the old proximity-repair design. Ore Shield v3 never reveals by
    // radius alone; server-authorized line of sight is required for every new ore discovery.
    // Clamp() normalizes this to zero while still accepting older config files.
    public int OreShieldRevealRadiusBlocks { get; set; } = 0;
    // Optional compatibility mode. Strict v3 defaults this off because whole-vein reveal intentionally
    // discloses buried vein geometry after the first legitimate discovery.
    public bool OreShieldRevealWholeVeinOnMine { get; set; } = false;
    public int OreShieldDiscoveryRangeBlocks { get; set; } = 96;
    public int OreShieldVisibilityScanIntervalMs { get; set; } = 250;
    public int OreShieldMaxLosChecksPerPass { get; set; } = 96;
    // Server-side view-cone gate. LOS is still authoritative; the cone prevents passive 360-degree
    // discovery and reduces ray-trace workload.
    public int OreShieldViewConeDegrees { get; set; } = 140;
    // Even motionless players receive a bounded safety rescan so unusual server-side orientation or
    // world changes cannot leave visible ore concealed forever.
    public int OreShieldIdleRescanMs { get; set; } = 3000;
    // Cheap outbound invariants verify that an undiscovered protected ore is never serialized as
    // its real block ID by OreShield-owned chunk/update paths.
    public bool OreShieldLeakAuditEnabled { get; set; } = true;
    // Safety bound for pathological/modded mega-veins. Normal veins are far below this value.
    public int OreShieldMaxVeinRevealBlocks { get; set; } = 2048;
    // Allows operators to disable masked-chunk reuse while diagnosing a compatibility issue.
    public bool OreShieldChunkCacheEnabled { get; set; } = true;
    public bool OreShieldDecoysEnabled { get; set; } = false;
    public int OreShieldDecoyPercent { get; set; } = 0;
    public int OreShieldDecoyClusterSize { get; set; } = 2;
    public int OreShieldChunkCacheMiB { get; set; } = 96;
    public int OreShieldMaxCachedChunks { get; set; } = 4096;
    public List<string> OreShieldAdditionalOrePatterns { get; set; } = new();
    public List<string> OreShieldExcludedBlockPatterns { get; set; } = new();

    // Legacy 1.2.28-pre-rename config keys. Setter-only aliases let existing configs migrate
    // to OreShield* names without serializing the old OreGuard* keys again.
    [JsonProperty("OreGuardEnabled")] private bool LegacyOreGuardEnabled { set => OreShieldEnabled = value; }
    [JsonProperty("OreGuardProtectModdedOres")] private bool LegacyOreGuardProtectModdedOres { set => OreShieldProtectModdedOres = value; }
    [JsonProperty("OreGuardDecoysEnabled")] private bool LegacyOreGuardDecoysEnabled { set => OreShieldDecoysEnabled = value; }
    [JsonProperty("OreGuardDecoyPercent")] private int LegacyOreGuardDecoyPercent { set => OreShieldDecoyPercent = value; }
    [JsonProperty("OreGuardDecoyClusterSize")] private int LegacyOreGuardDecoyClusterSize { set => OreShieldDecoyClusterSize = value; }
    [JsonProperty("OreGuardChunkCacheMiB")] private int LegacyOreGuardChunkCacheMiB { set => OreShieldChunkCacheMiB = value; }
    [JsonProperty("OreGuardMaxCachedChunks")] private int LegacyOreGuardMaxCachedChunks { set => OreShieldMaxCachedChunks = value; }
    [JsonProperty("OreGuardAdditionalOrePatterns")] private List<string>? LegacyOreGuardAdditionalOrePatterns { set { if (value != null) OreShieldAdditionalOrePatterns = value; } }
    [JsonProperty("OreGuardExcludedBlockPatterns")] private List<string>? LegacyOreGuardExcludedBlockPatterns { set { if (value != null) OreShieldExcludedBlockPatterns = value; } }

    // Server-authoritative packet/action validation. These checks do not trust the client
    // companion and therefore remain useful even if a cheat tampers with its scan report.
    public bool PacketGuardEnabled { get; set; } = true;
    // Applied before vanilla packet dispatch/chunk scheduling. These resource and protocol
    // invariants also apply to creative players and administrators.
    public bool PacketGuardIngressEnabled { get; set; } = true;
    public int PacketGuardIngressMaxPacketKiB { get; set; } = 2048;
    // Blocks, matching the vanilla player-settings packet. The server's own MaxChunkRadius
    // remains an additional upper bound; this does not increase the configured server radius.
    public int PacketGuardMaxViewDistanceBlocks { get; set; } = 1024;
    // Token buckets permit a normal burst and replenish at these sustained rates.
    public int PacketGuardIngressPacketsPerSecond { get; set; } = 300;
    public int PacketGuardIngressPacketBurst { get; set; } = 900;
    public int PacketGuardBlockPacketsPerSecond { get; set; } = 60;
    public int PacketGuardBlockPacketBurst { get; set; } = 180;
    public int PacketGuardSettingsPacketsPerSecond { get; set; } = 2;
    public int PacketGuardSettingsPacketBurst { get; set; } = 8;
    public int PacketGuardIngressFindingCooldownSeconds { get; set; } = 10;
    // Optional player-facing feedback for confirmed PacketGuard findings. This intentionally
    // reveals only the broad detection category, never thresholds/evidence that could help tune a bypass.
    public bool PacketGuardPlayerWarningEnabled { get; set; } = false;
    public int PacketGuardPlayerWarningCooldownSeconds { get; set; } = 15;
    public bool PacketGuardMovementEnabled { get; set; } = true;
    // Advisory only: real loaded terrain plus sustained non-falling samples can warrant
    // investigation, but modded flight/platform physics makes this insufficient for punishment.
    public bool PacketGuardFlightReviewEnabled { get; set; } = true;
    public int PacketGuardFlightReviewConfirmSeconds { get; set; } = 6;
    public int PacketGuardFlightReviewSampleMs { get; set; } = 250;
    public int PacketGuardMovementSampleMs { get; set; } = 75;
    public double PacketGuardHardMoveDistance { get; set; } = 8.0;
    // Scale hard displacement by the actual sampler delay so a server hitch is not judged as one normal sample.
    public double PacketGuardMovementMaxSampleScale { get; set; } = 4.0;
    // Borderline impossible jumps must persist across multiple samples before enforcement.
    // This filters one-tick server/client reconciliation spikes without raising the hard distance.
    public int PacketGuardMovementConfirmSamples { get; set; } = 2;
    public int PacketGuardMovementConfirmWindowMs { get; set; } = 600;
    public double PacketGuardMovementExtremeMultiplier { get; set; } = 2.75;
    public int PacketGuardCorrectionGraceMs { get; set; } = 1200;
    public bool PacketGuardRejectUnauthorizedMovement { get; set; } = true;
    // Large one-shot position changes are much more likely to be a server/mod teleport whose
    // transient teleport marker was missed than ordinary movement. Reconcile isolated teleport-sized
    // jumps and only surface a review finding when they repeat rapidly.
    public bool PacketGuardUnverifiedTeleportReconciliationEnabled { get; set; } = false;
    public double PacketGuardUnverifiedTeleportMinDistance { get; set; } = 128.0;
    public int PacketGuardUnverifiedTeleportRepeatWindowMs { get; set; } = 30000;
    public int PacketGuardUnverifiedTeleportRepeatCount { get; set; } = 3;

    // Sustained movement-abuse protection. The existing hard-move detector catches position
    // snaps; this layer catches cheats that deliberately stay below that per-sample threshold.
    // Horizontal speed is used so normal falling does not become a speed finding.
    public int PacketGuardConfirmedSpeedRepeatRisk { get; set; } = 25;
    public bool PacketGuardKickRepeatedConfirmedSpeed { get; set; } = true;
    public bool PacketGuardSpeedHackEnabled { get; set; } = true;
    public double PacketGuardMaxHorizontalSpeed { get; set; } = 30.0;
    // Movement-mode-aware envelopes. Mounted players are handled separately by trusted mount logic.
    // These are intentionally generous survival limits; they are anti-cheat ceilings, not gameplay speeds.
    public bool PacketGuardMovementModeLimitsEnabled { get; set; } = true;
    public double PacketGuardGroundMaxHorizontalSpeed { get; set; } = 24.0;
    public double PacketGuardSwimmingMaxHorizontalSpeed { get; set; } = 14.0;
    public double PacketGuardAirborneMaxHorizontalSpeed { get; set; } = 22.0;
    public int PacketGuardSpeedWindowMs { get; set; } = 750;
    public int PacketGuardSpeedConfirmWindows { get; set; } = 2;
    public bool PacketGuardRejectSpeedHack { get; set; } = true;

    // Acceleration protection catches clients that ramp to cheat speed while deliberately
    // staying below both the hard-snap and rolling-speed thresholds. It only considers
    // positive horizontal acceleration above a minimum speed and requires repeat samples.
    public bool PacketGuardAccelerationEnabled { get; set; } = true;
    public double PacketGuardAccelerationMinSpeed { get; set; } = 16.0;
    public double PacketGuardMaxHorizontalAcceleration { get; set; } = 120.0;
    public int PacketGuardAccelerationConfirmSamples { get; set; } = 2;
    public int PacketGuardAccelerationConfirmWindowMs { get; set; } = 900;
    // Instantaneous 50-70ms movement samples are too noisy for acceleration enforcement on
    // modded servers. Acceleration is measured from a smoothed speed baseline no more often
    // than this interval, and confirmation requires consecutive evaluated samples.
    public int PacketGuardAccelerationSampleMinMs { get; set; } = 220;
    public double PacketGuardAccelerationSmoothingFactor { get; set; } = 0.25;
    public bool PacketGuardRejectAccelerationHack { get; set; } = true;

    // Noclip protection samples the server-observed movement segment against actual block
    // collision boxes. It only evaluates unmounted, untrusted survival movement and requires
    // multiple collision samples through the same movement segment before rollback.
    public bool PacketGuardNoclipEnabled { get; set; } = false;
    public double PacketGuardNoclipMinPathDistance { get; set; } = 1.25;
    public double PacketGuardNoclipSampleStep { get; set; } = 0.30;
    public int PacketGuardNoclipRequiredCollisionSamples { get; set; } = 3;
    public double PacketGuardNoclipMinPenetrationDepth { get; set; } = 0.18;
    // Short-step noclip protection: even when the travelled segment is too short for path sampling,
    // repeated deep endpoint penetration must never become a new safe baseline.
    public bool PacketGuardNoclipEndpointEnabled { get; set; } = true;
    public double PacketGuardNoclipEndpointMinPenetrationDepth { get; set; } = 0.18;
    public int PacketGuardNoclipEndpointConfirmSamples { get; set; } = 3;
    // Endpoint overlap by itself is not noclip. Collision-box mismatches can leave a stationary
    // player slightly embedded in complex/modded blocks. Require meaningful net movement while
    // continuously penetrating before endpoint noclip can be confirmed.
    public double PacketGuardNoclipEndpointMinNetMovement { get; set; } = 0.45;
    public bool PacketGuardRejectNoclip { get; set; } = true;
    public bool PacketGuardNewActionShadowMode { get; set; } = true;
    public bool PacketGuardCombatChecksEnabled { get; set; } = true;
    public bool PacketGuardInventoryChecksEnabled { get; set; } = true;
    public bool PacketGuardDeniedActionResyncEnabled { get; set; } = true;
    public bool PacketGuardEscalationWarningsEnabled { get; set; } = true;
    public bool PacketGuardServerImpulseAllowanceEnabled { get; set; } = true;
    public int PacketGuardCombatMaxRequestsPerSecond { get; set; } = 20;
    public int PacketGuardInventoryMaxRequestsPerSecond { get; set; } = 100;
    public Dictionary<string,int> PacketGuardWeaponMinimumIntervalMs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PacketGuardMiningReviewOnlyTools { get; set; } = new();
    public bool PacketGuardBlockNukerEnabled { get; set; } = true;
    public int PacketGuardBlockNukerLimit { get; set; } = 30;
    public bool PacketGuardBlockNukerKickEnabled { get; set; } = true;
    public bool PacketGuardNoclipKickAfterCorrections { get; set; } = true;
    public int PacketGuardNoclipCorrectionsBeforeKick { get; set; } = 5;
    public int PacketGuardNoclipCorrectionDecaySeconds { get; set; } = 120;
    public int PacketGuardNoclipCorrectionMinimumIntervalSeconds { get; set; } = 10;

    // Rollback uses a ring of recently verified-safe server positions. The safety lag prevents
    // the newest borderline sample from becoming the rollback destination.
    public int PacketGuardSafePositionHistorySize { get; set; } = 24;
    public int PacketGuardSafePositionMaxAgeMs { get; set; } = 10000;
    public int PacketGuardRollbackSafetyLagMs { get; set; } = 300;
    public bool PacketGuardRollbackValidateCollision { get; set; } = true;
    public int PacketGuardRollbackValidationCandidates { get; set; } = 12;
    public int PacketGuardRollbackAntiLoopWindowMs { get; set; } = 2500;
    public double PacketGuardRollbackAntiLoopRisk { get; set; } = 20.0;
    public int PacketGuardMovementDuplicateFindingCooldownMs { get; set; } = 1200;

    // Rolling movement-risk score is diagnostic evidence used to distinguish isolated anomalies
    // from repeated multi-detector abuse. Consequence rules can still decide kick/freeze policy.
    public bool PacketGuardMovementRiskEnabled { get; set; } = true;
    public double PacketGuardMovementRiskDecayPerSecond { get; set; } = 2.0;
    public double PacketGuardMovementRiskAcceleration { get; set; } = 18.0;
    public double PacketGuardMovementRiskSpeed { get; set; } = 30.0;
    public double PacketGuardMovementRiskNoclip { get; set; } = 45.0;
    public double PacketGuardMovementRiskHardMove { get; set; } = 30.0;
    public double PacketGuardMovementRiskMount { get; set; } = 35.0;
    // Different confirmed movement detectors agreeing within this window increases confidence.
    // The correlation bonus is only added once for each newly contributing detector signal.
    public int PacketGuardMovementConfidenceCorrelationWindowMs { get; set; } = 2500;
    public double PacketGuardMovementConfidenceCorrelationBonus { get; set; } = 12.0;
    public double PacketGuardMovementConfidenceGuardedThreshold { get; set; } = 25.0;
    public double PacketGuardMovementConfidenceHighThreshold { get; set; } = 50.0;
    public double PacketGuardMovementConfidenceCriticalThreshold { get; set; } = 75.0;
    public bool PacketGuardMovementConfidenceEscalatesSeverity { get; set; } = true;

    // False-positive protections. Admins are identified by root/controlserver privilege; creative
    // and spectator players are already excluded because PacketGuard only evaluates survival.
    public bool PacketGuardExemptAdmins { get; set; } = true;
    public bool PacketGuardExemptMountedPlayers { get; set; } = true;
    public int PacketGuardMountGraceMs { get; set; } = 2000;

    // Mount-aware hardening. Known mount mods use deliberately generous anti-cheat ceilings
    // rather than a blanket mounted-player exemption. Shipwright vessels are intentionally
    // allowed a higher horizontal ceiling than Draconis flyers. Noclip body-path checks stay
    // disabled while genuinely mounted because seats can overlap mount collision geometry.
    public bool PacketGuardMountProfilesEnabled { get; set; } = true;
    public bool PacketGuardMountProfileEnforcementEnabled { get; set; } = true;
    // Unknown/modded mounts can reposition riders vertically as seats, animations and carrier
    // entities synchronize. Their envelope is retained for diagnostics but is not punishment-grade
    // unless explicitly enabled by an administrator.
    public bool PacketGuardUnknownMountEnforcementEnabled { get; set; } = false;
    public int PacketGuardMountProfileConfirmSamples { get; set; } = 3;
    public int PacketGuardMountProfileConfirmWindowMs { get; set; } = 1200;
    public double PacketGuardShipwrightMaxHorizontalSpeed { get; set; } = 55.0;
    public double PacketGuardShipwrightMaxVerticalSpeed { get; set; } = 20.0;
    public double PacketGuardShipwrightMaxAcceleration { get; set; } = 300.0;
    public double PacketGuardShipwrightMaxRiderDistance { get; set; } = 20.0;
    public double PacketGuardDraconisMaxHorizontalSpeed { get; set; } = 80.0;
    public double PacketGuardDraconisMaxVerticalSpeed { get; set; } = 50.0;
    public double PacketGuardDraconisMaxAcceleration { get; set; } = 420.0;
    public double PacketGuardDraconisMaxRiderDistance { get; set; } = 12.0;
    public double PacketGuardUnknownMountMaxHorizontalSpeed { get; set; } = 40.0;
    public double PacketGuardUnknownMountMaxVerticalSpeed { get; set; } = 40.0;
    public double PacketGuardUnknownMountMaxAcceleration { get; set; } = 350.0;
    // Mount acceleration is intentionally corroborating evidence. Physics/mod mounts can report
    // large one-tick acceleration while remaining well inside their legal speed envelope.
    public double PacketGuardMountAccelerationMinSpeedRatio { get; set; } = 0.85;

    // Diagnostic Context v35. Adds decision/suppression reasons and short movement history to
    // PacketGuard evidence so false positives can be tuned from real server telemetry.
    public bool PacketGuardDiagnosticContextEnabled { get; set; } = true;
    public bool PacketGuardDiagnosticSuppressionEnabled { get; set; } = true;
    public int PacketGuardDiagnosticSpeedHistorySamples { get; set; } = 8;
    public double PacketGuardUnknownMountMaxRiderDistance { get; set; } = 16.0;
    public bool PacketGuardMountRiderCorrelationEnabled { get; set; } = true;
    public double PacketGuardShipwrightMaxRiderRelativeSpeed { get; set; } = 24.0;
    public double PacketGuardDraconisMaxRiderRelativeSpeed { get; set; } = 20.0;
    public double PacketGuardUnknownMountMaxRiderRelativeSpeed { get; set; } = 24.0;

    // Engine-authorized teleports (vanilla translocators, server TeleportTo/TeleportToDouble,
    // respawns, and compatible mods) extend a grace window instead of becoming movement findings.
    public int PacketGuardEngineTeleportGraceMs { get; set; } = 5000;
    public bool PacketGuardTPRunesCompatibility { get; set; } = true;
    public int PacketGuardTPRunesGraceMs { get; set; } = 8000;
    // Th3Essentials /spawn can complete its server-side teleport between PacketGuard samples.
    // Compatibility is destination-bound and only arms after the player actually issues /spawn.
    public bool PacketGuardTh3EssentialsSpawnCompatibility { get; set; } = true;
    public int PacketGuardTh3EssentialsSpawnGraceMs { get; set; } = 8000;

    public bool PacketGuardReachEnabled { get; set; } = true;
    public double PacketGuardReachTolerance { get; set; } = 1.25;
    // Network-aware reach headroom. Client block-selection packets can arrive one or more
    // movement samples away from the exact position where the click happened, especially at
    // higher latency. This compensation is capped and is added only to the reach validator.
    public bool PacketGuardReachLatencyCompensation { get; set; } = true;
    public double PacketGuardReachMaxLatencyCompensation { get; set; } = 1.25;
    // Keep a short server-authoritative eye-position history so interactions can be reconciled
    // against where the player actually was when a delayed packet was generated.
    public bool PacketGuardReachPositionHistoryEnabled { get; set; } = true;
    public int PacketGuardReachPositionHistoryMs { get; set; } = 650;
    // Do not blindly accept the oldest/best point in the full history buffer. The validator
    // derives a bounded rewind from RTT and this cap, preventing stale proximity from becoming
    // a reach bypass while still reconciling delayed interaction packets.
    public int PacketGuardReachMaxRewindMs { get; set; } = 350;
    // Use and break do not have identical packet/selection timing. Keep action-specific headroom
    // small and explicit rather than raising the global reach limit.
    public double PacketGuardReachUseExtraTolerance { get; set; } = 0.20;
    public double PacketGuardReachBreakExtraTolerance { get; set; } = 0.00;
    // Slight overages must repeat before they become a finding/rejection. Large overages still
    // trip immediately, preserving useful protection against actual reach hacks. Near-limit
    // strikes decay over time so isolated legitimate edge cases do not remain sticky.
    public int PacketGuardReachNearThresholdStrikes { get; set; } = 3;
    public int PacketGuardReachStrikeWindowMs { get; set; } = 3500;
    public int PacketGuardReachStrikeDecayMs { get; set; } = 1800;
    public double PacketGuardReachHardExcess { get; set; } = 1.5;
    // Near-limit reach evidence must span more than one distinct block target before it can
    // become punishment-grade. A genuinely large hard overreach remains immediate.
    public int PacketGuardReachDistinctTargetsRequired { get; set; } = 2;
    public bool PacketGuardRejectOutOfReachActions { get; set; } = true;
    public bool PacketGuardMiningEnabled { get; set; } = true;
    public int PacketGuardVeinWindowMs { get; set; } = 2200;
    public int PacketGuardConnectedBreakLimit { get; set; } = 18;
    // Connected-burst enforcement is staged. A single threshold crossing is retained as
    // evidence, while repeat bursts (or an extreme burst) can be rejected.
    public int PacketGuardConnectedBreakConfirmations { get; set; } = 2;
    public double PacketGuardConnectedBreakExtremeMultiplier { get; set; } = 1.75;
    // Duplicate callbacks/packets against the same target inside this interval count once.
    public int PacketGuardMiningDuplicateTargetMs { get; set; } = 450;
    public bool PacketGuardRejectConnectedBreakBursts { get; set; } = true;

    // Mining Validation v2 compares observed consecutive adjacent-break cadence with a
    // server-derived expected break time using resistance, held tool/tier, mining speed,
    // required tier, and block-behavior mining modifiers. It requires repeated confirmation.
    public bool PacketGuardMiningCadenceValidationEnabled { get; set; } = false;
    public double PacketGuardMiningCadenceMinExpectedMs { get; set; } = 250;
    public double PacketGuardMiningCadenceSuspiciousRatio { get; set; } = 0.38;
    public int PacketGuardMiningCadenceConfirmations { get; set; } = 3;
    public int PacketGuardMiningCadenceConfirmationWindowMs { get; set; } = 8000;
    public bool PacketGuardRejectImpossibleMiningCadence { get; set; } = false;

    // Server-TPS-aware timing. PacketGuard measures actual main-thread tick cadence against
    // api.Server.Config.TickTime. Timing-sensitive detectors become more conservative during
    // sustained lag, while absolute extreme movement and integrity/repack evidence stay active.
    public bool PacketGuardTpsAwarenessEnabled { get; set; } = true;
    public double PacketGuardTpsDegradedPercent { get; set; } = 85.0;
    public double PacketGuardTpsCriticalPercent { get; set; } = 60.0;
    public double PacketGuardTpsMaxTimingMultiplier { get; set; } = 2.5;
    public bool PacketGuardTpsSuppressTimingRejectsWhenCritical { get; set; } = true;

    // v40 live-risk families. Each evidence family decays on its own clock and uses its own
    // Review/Suspicious thresholds. The global live-risk score is the strongest current family,
    // never the sum of unrelated families. This prevents movement noise from priming Reach,
    // Mining, packet-validation or Client Integrity consequences.
    public bool PerFamilyRiskEnabled { get; set; } = true;
    public int MovementRiskDecaySeconds { get; set; } = 180;
    public int MovementRiskReviewThreshold { get; set; } = 25;
    public int MovementRiskSuspiciousThreshold { get; set; } = 50;
    public int ReachRiskDecaySeconds { get; set; } = 300;
    public int ReachRiskReviewThreshold { get; set; } = 25;
    public int ReachRiskSuspiciousThreshold { get; set; } = 50;
    public int MiningRiskDecaySeconds { get; set; } = 300;
    public int MiningRiskReviewThreshold { get; set; } = 25;
    public int MiningRiskSuspiciousThreshold { get; set; } = 50;
    public int PacketAbuseRiskDecaySeconds { get; set; } = 600;
    public int PacketAbuseRiskReviewThreshold { get; set; } = 25;
    public int PacketAbuseRiskSuspiciousThreshold { get; set; } = 50;
    public int ClientIntegrityRiskDecaySeconds { get; set; } = 1800;
    public int ClientIntegrityRiskReviewThreshold { get; set; } = 10;
    public int ClientIntegrityRiskSuspiciousThreshold { get; set; } = 50;

    // Cross-detector incident correlation. In v40 this remains corroborating evidence only while
    // per-family risk is enabled, so unrelated families never add together. The legacy bounded
    // bonus setting is retained for compatibility if per-family risk is explicitly disabled.
    public bool IncidentCorrelationEnabled { get; set; } = true;
    public int IncidentCorrelationWindowSeconds { get; set; } = 90;
    public int IncidentCorrelationRequiredCategories { get; set; } = 2;
    public int IncidentCorrelationMaxRiskBonus { get; set; } = 20;

    // Persistent incident history is world-specific and lives under ModData, not ModConfig.
    public bool HistoryEnabled { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 90;
    public int HistoryMaxRecords { get; set; } = 500;
    // Anti-Cheat is an optional server-owner add-on, but when the companion is installed
    // protection starts enabled by default. Existing configs preserve their saved value.
    public bool Enabled { get; set; } = true;
    public bool RequireClient { get; set; } = true;
    // ModDB comparisons are external/asynchronous reference checks. Keep disruptive automatic
    // consequences disabled by default so a transient API/cache/re-upload ambiguity cannot kick
    // a whole server. Staff can still review every exact mismatch in Client Mods / findings.
    public bool ModDbAutomaticConsequencesEnabled { get; set; } = false;
    // Exact client ModID/version fingerprints are resolved against the official Vintage Story ModDB.
    // This remains enabled by default because it detects modified/repacked copies of otherwise allowed mods.
    // Operators can disable the external verification layer without disabling the local client mod inventory.
    public bool ClientModDbVerificationEnabled { get; set; } = true;
    // Strict database admission: every reported third-party client mod must match one trusted
    // source exactly (server-loaded baseline, explicit custom approval, or fresh official ModDB
    // fingerprint). Unknown/unverifiable releases and exact hash mismatches are disconnected.
    public bool StrictClientModDbEnforcementEnabled { get; set; } = true;

    // Startup Integrity Gate v43. The first accepted deep attestation is treated differently
    // from routine rescans: while it is pending, optionally block world-changing interactions.
    // This targets cheats that are already loaded before the player connects without hard-freezing
    // movement during spawn/chunk initialization. A valid scan releases the gate by default even
    // when it produces Review/Suspicious evidence; consequence policy remains administrator-owned.
    public bool StartupIntegrityGateEnabled { get; set; } = true;
    public bool StartupIntegrityGateBlockWorldInteractions { get; set; } = false;
    public bool StartupIntegrityGateRequireClean { get; set; } = false;
    public bool StartupIntegrityGateDisconnectOnFailure { get; set; } = true;
    public int StartupIntegrityGateNoticeCooldownSeconds { get; set; } = 4;
    public bool StartupCoreAssemblyInspectionEnabled { get; set; } = true;
    // Only assemblies that exist on both server and client should be listed here. VintagestoryAPI
    // is the conservative default; server owners can add additional shared managed assemblies after
    // validating their platform/build topology.
    public List<string> StartupCoreAssemblyNames { get; set; } = new() { "VintagestoryAPI" };

    // Routine rescan deadline. Startup scans use their own larger, configurable window below.
    public int ChallengeTimeoutSeconds { get; set; } = 30;
    // The initial startup attestation hashes client mod content and can be disk/CPU heavy on large
    // modpacks. Give it a substantially larger window than routine rescans.
    public int StartupChallengeTimeoutSeconds { get; set; } = 300;
    // While the client's heartbeat remains healthy, extend the same in-flight startup challenge
    // rather than issuing overlapping retries. The hard limit caps the total startup wait.
    public int StartupChallengeHeartbeatExtensionSeconds { get; set; } = 120;
    public int StartupChallengeHardLimitSeconds { get; set; } = 900;
    public int ClientHelloGraceSeconds { get; set; } = 90;
    // After the hello grace expires, keep a recovery window before disconnecting. This lets
    // slow-loading clients finish channel setup without turning a transport delay into a false missing-client kick.
    public int ClientHelloDisconnectDelaySeconds { get; set; } = 60;
    public int HeartbeatTimeoutSeconds { get; set; } = 180;
    public int RescanMinutes { get; set; } = 15;
    public bool RuntimeRescanOnChange { get; set; } = true;
    public int RuntimeChangeRescanCooldownSeconds { get; set; } = 30;
    public bool RequireApprovedClientBuild { get; set; } = false;
    // Optional rollout gate for the hardened Client Integrity transport. Protocol 13 still requires
    // the core report-hash/challenge-bound heartbeat protections; enabling this keeps those hardened
    // transport capabilities mandatory independently of the stronger self-attestation policy.
    public bool RequireHardenedClientCapabilities { get; set; } = false;
    // Optional semantic-version gate. The serialized property name is retained for config
    // compatibility, but the required release is resolved dynamically from the Client Integrity
    // companion installed on the server. Protocol/capability enforcement remains separate.
    public bool RequireCurrentClientRelease { get; set; } = false;
    // Protocol 13 randomized self-attestation. Fresh method/type probes are selected from the
    // exact Client Integrity assembly installed on the server for every deep challenge.
    public bool RandomizedSelfAttestationEnabled { get; set; } = true;
    public int SelfAttestationMethodProbeCount { get; set; } = 6;
    public int SelfAttestationTypeProbeCount { get; set; } = 3;
    // A reflection/read failure or one unexpected randomized result is confirmed with a fresh
    // challenge before disconnect. Exact DLL/MVID/content baseline mismatches remain fail-closed.
    public int SelfAttestationFailuresBeforeKick { get; set; } = 3;
    // Optional publisher-signature enforcement. Generate the key/signature outside the repository
    // and paste only the SubjectPublicKeyInfo DER as Base64 here. Never put the private key in Git.
    public bool RequireSignedClientBuildManifest { get; set; } = false;
    public string ClientBuildManifestPublicKeyBase64 { get; set; } = "";
    public bool SimilarityDetectionEnabled { get; set; } = true;
    public int SimilarityReviewThresholdPercent { get; set; } = 72;
    public int SimilarityHighThresholdPercent { get; set; } = 88;

    // Runtime Surface Integrity v42. Keybind/command names are deliberately weak evidence;
    // canonical package mismatches and protected asset changes are stronger but still correlate
    // with server-authoritative behavior before disruptive enforcement is recommended.
    public bool RuntimeSurfaceInspectionEnabled { get; set; } = true;
    public bool SuspiciousHotkeyInspectionEnabled { get; set; } = true;
    public bool SuspiciousClientCommandInspectionEnabled { get; set; } = true;
    public List<string> SuspiciousRuntimeTerms { get; set; } = new()
    {
        "xray", "x-ray", "esp", "freecam", "free camera", "noclip", "no clip",
        "killaura", "kill aura", "aimbot", "autoclick", "auto click",
        "speedhack", "speed hack", "flyhack", "fly hack", "fullbright", "full bright",
        "wallhack", "wall hack", "tracer", "reachhack", "reach hack",
        "orefinder", "ore finder", "mobfinder", "mob finder", "entityesp", "playeresp"
    };
    public bool CanonicalServerModIntegrityEnabled { get; set; } = true;

    // Suspicious mod-package transfer is administrator-requested and scoped to the exact package
    // already reported by Vintage Story's ModLoader for a suspicious player/mod. Protocol 13
    // requires an in-game player authorization decision before package preparation or transfer.
    // The server never sends an arbitrary filesystem path to the client.
    public bool SuspectPackageCollectionEnabled { get; set; } = true;
    public int SuspectPackageCollectionMaxMb { get; set; } = 96;
    public int SuspectPackageCollectionTimeoutSeconds { get; set; } = 120;
    // Policy applied only after an explicit player decline/dismissal of a manual administrator
    // Collect ZIP request. None leaves the player connected; Disconnect kicks; TempBan applies
    // the configured temporary-ban duration. The selected policy is snapshotted per request.
    public string SuspectPackageDeclineAction { get; set; } = "TempBan";
    public int SuspectPackageDeclineTempBanHours { get; set; } = 24;
    // Authorized package transfer only. Keep this conservative so Anti-Cheat cannot saturate a
    // player's upstream connection while they are actively playing.
    public int SuspectPackageUploadKbps { get; set; } = 384;
    public bool ProtectedAssetInspectionEnabled { get; set; } = true;
    // Prefixes are case-insensitive. Use contains:<text> for paths whose exact folder layout can vary.
    public List<string> ProtectedAssetPatterns { get; set; } = new()
    {
        "game:textures/block/stone",
        "game:textures/block/ore",
        "game:textures/block/rock",
        "game:shaders/"
    };
    // Detailed per-asset hashes help staff identify the changed file. The aggregate digest covers
    // every matching asset, so the detailed list may stay deliberately small.
    public int ProtectedAssetDetailedEntries { get; set; } = 96;
    public List<string> ApprovedClientBuildHashes { get; set; } = new();
    public List<string> RequiredClientModIds { get; set; } = new() { AntiCheatProtocol.ClientModId };
    public List<string> ForbiddenModIds { get; set; } = DefaultForbiddenModIds.ToList();
    // Number of latest official Mod DB releases to fingerprint per blacklisted mod.
    public int FingerprintReleaseDepth { get; set; } = 6;
    // Named snapshots of blacklist policy. Loading a profile replaces ForbiddenModIds; manual edits
    // do not mutate the saved profile until the operator explicitly saves it again.
    public List<AntiCheatBlacklistProfile> BlacklistProfiles { get; set; } = new();
    public string ActiveBlacklistProfileId { get; set; } = "";
    public List<AntiCheatForbiddenFingerprint> ForbiddenFingerprints { get; set; } = new();
    public List<string> AllowedExtraCodeModIds { get; set; } = new();
    // Keep this list empty at construction time. Vintage Story's config loader may populate
    // existing collection instances instead of replacing them, so pre-seeding defaults here
    // caused the two default rules to be appended again on every server restart.
    public List<AntiCheatConsequenceRule> ConsequenceRules { get; set; } = new();


    public void SeedDefaultFingerprintsIfEmpty()
    {
        ForbiddenFingerprints ??= new();
        const string packageHash = "9f97c05e33cccab034b7ea5e8a6a2a0ab80cc6ebd374516a86287628eb048986";
        const string contentHash = "be29fd5a9fca7b22368dd0e8ad16e9fb50bd7efaf7cc7ce63f19ae867502e456";
        const string assemblyHash = "d8e0e89c93aaebf171ee8aef67841273c10b98b941f48d2476a7125da99541a2";

        var existing = ForbiddenFingerprints.FirstOrDefault(x =>
            x != null && string.Equals(x.ModId, "modmenu", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(x.PackageSha256, packageHash, StringComparison.OrdinalIgnoreCase) ||
             (x.AssemblySha256s ?? new()).Any(h => string.Equals(h, assemblyHash, StringComparison.OrdinalIgnoreCase))));

        if (existing != null)
        {
            // Upgrade the built-in ModMenu 1.2.1 fingerprint in-place for existing configs.
            // This is important because users who already ran schema 11 would otherwise keep
            // only the raw ZIP hash and miss the stable normalized-content fingerprint.
            if (string.IsNullOrWhiteSpace(existing.ContentSha256))
                existing.ContentSha256 = contentHash;

            existing.AssemblyNames ??= new();
            if (!existing.AssemblyNames.Contains("ModMenu", StringComparer.OrdinalIgnoreCase))
                existing.AssemblyNames.Add("ModMenu");

            existing.AssemblySha256s ??= new();
            if (!existing.AssemblySha256s.Contains(assemblyHash, StringComparer.OrdinalIgnoreCase))
                existing.AssemblySha256s.Add(assemblyHash);

            existing.ModSystemTypeNames ??= new();
            foreach (string typeName in CreateModMenu121Fingerprint().ModSystemTypeNames)
                if (!existing.ModSystemTypeNames.Contains(typeName, StringComparer.Ordinal))
                    existing.ModSystemTypeNames.Add(typeName);
            return;
        }

        ForbiddenFingerprints.Add(CreateModMenu121Fingerprint());
    }

    private static AntiCheatForbiddenFingerprint CreateModMenu121Fingerprint() => new()
    {
        ModId = "modmenu",
        Source = "built-in",
        ResolvedVersion = "1.2.1",
        ResolvedFileName = "ModMenu_1.2.1.zip",
        PackageSha256 = "9f97c05e33cccab034b7ea5e8a6a2a0ab80cc6ebd374516a86287628eb048986",
        ContentSha256 = "be29fd5a9fca7b22368dd0e8ad16e9fb50bd7efaf7cc7ce63f19ae867502e456",
        AssemblyNames = new() { "ModMenu" },
        AssemblySha256s = new() { "d8e0e89c93aaebf171ee8aef67841273c10b98b941f48d2476a7125da99541a2" },
        ModSystemTypeNames = new()
        {
            "ModMenu.ModMenuSystem",
            "ModMenu.Patches.MoveSpeedPatch",
            "ModMenu.Patches.ReachMouseRayPatch",
            "ModMenu.Patches.InstantMinePatch",
            "ModMenu.Patches.VeinMinePatch"
        }
    };

    public void SeedDefaultConsequenceRulesIfEmpty()
    {
        ConsequenceRules ??= new();
        if (ConsequenceRules.Count != 0) return;
        ConsequenceRules.AddRange(CreateDefaultConsequenceRules());
    }

    private static IEnumerable<AntiCheatConsequenceRule> CreateDefaultConsequenceRules()
    {
        yield return new AntiCheatConsequenceRule
        {
            Name = "Log forbidden client mod", TriggerType = "FindingCode", TriggerValue = "FORBIDDEN_MOD", Action = "Log", Duration = "30m",
            Message = "Anti-cheat detected forbidden mod {subject} on {player}."
        };
        yield return new AntiCheatConsequenceRule
        {
            Name = "Log suspicious status", TriggerType = "Status", TriggerValue = "Suspicious", Action = "Log", Duration = "30m",
            Message = "Anti-cheat marked {player} suspicious (risk {risk})."
        };
    }

    public void Clamp()
    {
        int incomingSchema = SchemaVersion;
        // v40 splits live risk into independently decaying Movement, Reach, Mining, Packet Abuse
        // and Client Integrity families. Global live risk is the strongest family, not a cross-family sum.
        // v39 isolates PacketGuard movement findings into one live-risk family, expires stale
        // packet behavior on the incident window, and makes unverified teleport patterns risk-neutral.
        // v21 finishes PacketGuard movement hardening with elapsed-time scaling, movement-mode
        // speed ceilings, short-step endpoint noclip validation, and rolling movement-risk evidence.
        // v19 hardens PacketGuard against observed false-positive classes: placeholder 0,0,0
        // block selections, single-sample movement reconciliation spikes, and collapsed mining
        // callback timestamps during severe server lag. No global reach/movement thresholds are
        // relaxed by this migration.
        SchemaVersion = CurrentSchemaVersion;
        PacketGuardConfirmedSpeedRepeatRisk = Math.Clamp(PacketGuardConfirmedSpeedRepeatRisk, 0, 50);
        // v60 hardens Ore Shield LOS with real selection-geometry occlusion, movement/view-driven
        // scanning, exposed-candidate indexing, per-chunk discovery state and outbound leak invariants.
        // v59 moves Ore Shield to recipient-aware LOS discovery. Every protected ore is concealed by
        // default, including cave-exposed ore; server LOS creates one-way per-player discoveries.
        // Whole-vein reveal is migrated off because it leaks buried vein geometry by design.
        // v58 moved Ore Shield to exposure-driven/event-driven reveal and retired radius repair.
        // v51 adds server-authoritative Ore Shield. Clamp its cache/decoy controls aggressively because
        // they execute in the chunk-send path and should never be able to create unbounded work.
        // Visual fake-ore decoys are disabled in the concealment-only hotfix. Keep the legacy
        // properties for config compatibility, but normalize them off so old configs cannot
        // re-enable client-visible fake ore.
        OreShieldDecoysEnabled = false;
        OreShieldDecoyPercent = 0;
        OreShieldDecoyClusterSize = Math.Clamp(OreShieldDecoyClusterSize, 1, 4);
        // Security hardening: unknown/unloaded neighbor data must never cause a real ore ID to be
        // sent to the client. Older correctness-first builds allowed fail-open reveal here, which
        // leaked a small number of fully buried chunk-edge ores to overlay/xray-style clients.
        // Keep the legacy property for config compatibility, but force secure fail-closed mode.
        OreShieldRevealOnUnknownNeighbor = false;
        OreShieldRevealRadiusBlocks = 0;
        if (incomingSchema < 59) OreShieldRevealWholeVeinOnMine = false;
        OreShieldMaxVeinRevealBlocks = Math.Clamp(OreShieldMaxVeinRevealBlocks, 64, 8192);
        OreShieldDiscoveryRangeBlocks = Math.Clamp(OreShieldDiscoveryRangeBlocks, 16, 192);
        OreShieldVisibilityScanIntervalMs = Math.Clamp(OreShieldVisibilityScanIntervalMs, 100, 2000);
        OreShieldMaxLosChecksPerPass = Math.Clamp(OreShieldMaxLosChecksPerPass, 16, 512);
        OreShieldViewConeDegrees = Math.Clamp(OreShieldViewConeDegrees, 90, 180);
        OreShieldIdleRescanMs = Math.Clamp(OreShieldIdleRescanMs, 500, 15000);
        OreShieldChunkCacheMiB = Math.Clamp(OreShieldChunkCacheMiB, 8, 1024);
        OreShieldMaxCachedChunks = Math.Clamp(OreShieldMaxCachedChunks, 256, 16384);
        OreShieldAdditionalOrePatterns ??= new();
        OreShieldExcludedBlockPatterns ??= new();
        OreShieldAdditionalOrePatterns = OreShieldAdditionalOrePatterns
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
        OreShieldExcludedBlockPatterns = OreShieldExcludedBlockPatterns
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
        ChallengeTimeoutSeconds = Math.Clamp(ChallengeTimeoutSeconds, 10, 120);
        SelfAttestationMethodProbeCount = Math.Clamp(SelfAttestationMethodProbeCount, 2, 12);
        SelfAttestationTypeProbeCount = Math.Clamp(SelfAttestationTypeProbeCount, 1, 8);
        SelfAttestationFailuresBeforeKick = Math.Clamp(SelfAttestationFailuresBeforeKick, 2, 4);
        ClientBuildManifestPublicKeyBase64 = (ClientBuildManifestPublicKeyBase64 ?? "").Trim();
        if (incomingSchema < 17) ClientHelloGraceSeconds = Math.Max(ClientHelloGraceSeconds, 60);
        // v50 makes startup interaction quarantine opt-in. Live player feedback showed that
        // blocking place/break/use/entity interaction while the first deep scan runs can make
        // reconnecting players effectively helpless during combat or world loading. Existing
        // installs are migrated to OFF once; administrators can explicitly re-enable the gate
        // from Anti-Cheat -> Client Integrity if their server wants quarantine semantics.
        if (incomingSchema < 50)
            StartupIntegrityGateBlockWorldInteractions = false;
        // v49 adds an opt-in exact current-client release gate for Client Integrity 1.1.22.
        // It defaults OFF so operators can roll the new client out before enforcing it.
        // v48 adds an opt-in hardened protocol-9 capability gate. It defaults OFF for compatibility.
        // v47 increases only the exact previous defaults. Administrator-customized timing is preserved.
        // The client mod inventory became more expensive once normalized package fingerprints were added,
        // so slower PCs/storage need a wider startup/heartbeat recovery envelope.
        if (incomingSchema < 47)
        {
            if (ClientHelloGraceSeconds == 60) ClientHelloGraceSeconds = 90;
            if (ClientHelloDisconnectDelaySeconds == 30) ClientHelloDisconnectDelaySeconds = 60;
            if (HeartbeatTimeoutSeconds == 90) HeartbeatTimeoutSeconds = 180;
        }
        // Completion-to-completion block timing cannot prove when the second block began mining.
        // Pre-damaged blocks, overlapping mining and other legitimate server mechanics can make
        // that heuristic look impossible. Disable it during the v18 migration; the independent
        // connected-break burst detector remains enabled. Administrators can opt it back in.
        if (incomingSchema < 18) PacketGuardMiningCadenceValidationEnabled = false;
        // v21 tightens only the legacy default. Preserve any administrator-customized hard-move
        // threshold rather than silently changing an intentional server-specific value.
        if (incomingSchema < 21 && Math.Abs(PacketGuardHardMoveDistance - 32.0) < 0.001)
            PacketGuardHardMoveDistance = 8.0;
        // v23 hardens existing PacketGuard detectors without adding a new detector family:
        // near-limit Reach requires distinct targets, connected mining bursts use staged
        // confirmation, and movement rollback re-baselines to the actual correction point.
        // v22 tunes mount profiles from the initial deliberately-wide compatibility ceilings to
        // conservative production ceilings. Only migrate the exact old defaults; preserve any
        // administrator-customized mount values. Confirmation is also raised to three samples so
        // one-off seat/physics/lag spikes cannot cause a rollback.
        if (incomingSchema < 22)
        {
            if (PacketGuardMountProfileConfirmSamples == 2) PacketGuardMountProfileConfirmSamples = 3;
            if (PacketGuardMountProfileConfirmWindowMs == 700) PacketGuardMountProfileConfirmWindowMs = 1200;
            if (Math.Abs(PacketGuardShipwrightMaxHorizontalSpeed - 120.0) < 0.001) PacketGuardShipwrightMaxHorizontalSpeed = 32.0;
            if (Math.Abs(PacketGuardShipwrightMaxVerticalSpeed - 30.0) < 0.001) PacketGuardShipwrightMaxVerticalSpeed = 20.0;
            if (Math.Abs(PacketGuardShipwrightMaxAcceleration - 700.0) < 0.001) PacketGuardShipwrightMaxAcceleration = 300.0;
            if (Math.Abs(PacketGuardDraconisMaxHorizontalSpeed - 80.0) < 0.001) PacketGuardDraconisMaxHorizontalSpeed = 26.0;
            if (Math.Abs(PacketGuardDraconisMaxVerticalSpeed - 65.0) < 0.001) PacketGuardDraconisMaxVerticalSpeed = 40.0;
            if (Math.Abs(PacketGuardDraconisMaxAcceleration - 500.0) < 0.001) PacketGuardDraconisMaxAcceleration = 260.0;
            if (Math.Abs(PacketGuardUnknownMountMaxHorizontalSpeed - 100.0) < 0.001) PacketGuardUnknownMountMaxHorizontalSpeed = 40.0;
            if (Math.Abs(PacketGuardUnknownMountMaxVerticalSpeed - 60.0) < 0.001) PacketGuardUnknownMountMaxVerticalSpeed = 40.0;
            if (Math.Abs(PacketGuardUnknownMountMaxAcceleration - 600.0) < 0.001) PacketGuardUnknownMountMaxAcceleration = 350.0;
        }
        // v24 retunes Draconis from the earlier conservative estimate using live server testing.
        // Alpine reaches about 42.5 b/s at full flight and Stormweaver flyfast is expected higher,
        // so the production ceiling is moved to 65 b/s with extra vertical/acceleration headroom.
        // Only migrate the exact v22/v23 defaults; preserve administrator-customized values.
        if (incomingSchema < 24)
        {
            if (Math.Abs(PacketGuardDraconisMaxHorizontalSpeed - 26.0) < 0.001) PacketGuardDraconisMaxHorizontalSpeed = 65.0;
            if (Math.Abs(PacketGuardDraconisMaxVerticalSpeed - 40.0) < 0.001) PacketGuardDraconisMaxVerticalSpeed = 50.0;
            if (Math.Abs(PacketGuardDraconisMaxAcceleration - 260.0) < 0.001) PacketGuardDraconisMaxAcceleration = 420.0;
        }
        // v25 adds extra horizontal headroom for servers using XSkills movement buffs.
        // Shipwright is raised to 55 b/s and Draconis to 80 b/s. Only migrate the
        // exact stock v24 values so administrator-customized ceilings remain untouched.
        if (incomingSchema < 25)
        {
            if (Math.Abs(PacketGuardShipwrightMaxHorizontalSpeed - 32.0) < 0.001) PacketGuardShipwrightMaxHorizontalSpeed = 55.0;
            if (Math.Abs(PacketGuardDraconisMaxHorizontalSpeed - 65.0) < 0.001) PacketGuardDraconisMaxHorizontalSpeed = 80.0;
        }
        // v26 turns the existing diagnostic movement-risk value into a correlated confidence system.
        // Detector confirmations remain authoritative; confidence only correlates confirmed signals,
        // decays during clean movement, and can escalate finding severity when multiple signals agree.
        if (incomingSchema < 26)
        {
            // Preserve existing custom risk weights. The new mount/correlation/band defaults are additive.
            PacketGuardMovementRiskMount = PacketGuardMovementRiskMount <= 0 ? 35.0 : PacketGuardMovementRiskMount;
        }
        // v27 hardens existing movement enforcement: validates rollback destinations, detects
        // clients that immediately fight a correction, correlates riders with mount displacement,
        // suppresses duplicate movement findings, and gives stronger signals longer confidence memory.
        if (incomingSchema < 27)
        {
            PacketGuardRollbackValidateCollision = true;
            PacketGuardMountRiderCorrelationEnabled = true;
        }
        // v28 adds optional player-only chat feedback for confirmed PacketGuard findings.
        // It defaults OFF so existing servers do not change player-facing behavior unless enabled.
        if (incomingSchema < 28)
        {
            PacketGuardPlayerWarningEnabled = false;
            if (PacketGuardPlayerWarningCooldownSeconds <= 0) PacketGuardPlayerWarningCooldownSeconds = 15;
        }
        StartupChallengeTimeoutSeconds = Math.Clamp(StartupChallengeTimeoutSeconds, 60, 900);
        StartupChallengeHeartbeatExtensionSeconds = Math.Clamp(StartupChallengeHeartbeatExtensionSeconds, 30, 300);
        StartupChallengeHardLimitSeconds = Math.Clamp(StartupChallengeHardLimitSeconds, StartupChallengeTimeoutSeconds, 1800);
        ClientHelloGraceSeconds = Math.Clamp(ClientHelloGraceSeconds, 15, 300);
        ClientHelloDisconnectDelaySeconds = Math.Clamp(ClientHelloDisconnectDelaySeconds, 10, 300);
        HeartbeatTimeoutSeconds = Math.Clamp(HeartbeatTimeoutSeconds, 30, 900);
        RescanMinutes = Math.Clamp(RescanMinutes, 1, 1440);
        RuntimeChangeRescanCooldownSeconds = Math.Clamp(RuntimeChangeRescanCooldownSeconds, 10, 300);
        SuspectPackageCollectionMaxMb = Math.Clamp(SuspectPackageCollectionMaxMb, 8, 256);
        SuspectPackageCollectionTimeoutSeconds = Math.Clamp(SuspectPackageCollectionTimeoutSeconds, 30, 300);
        SuspectPackageDeclineAction = NormalizeChoice(SuspectPackageDeclineAction, "None", "Disconnect", "TempBan");
        SuspectPackageDeclineTempBanHours = Math.Clamp(SuspectPackageDeclineTempBanHours, 1, 720);
        SuspectPackageUploadKbps = Math.Clamp(SuspectPackageUploadKbps, 128, 4096);
        SimilarityReviewThresholdPercent = Math.Clamp(SimilarityReviewThresholdPercent, 50, 95);
        SimilarityHighThresholdPercent = Math.Clamp(SimilarityHighThresholdPercent, SimilarityReviewThresholdPercent + 1, 100);
        PacketGuardPlayerWarningCooldownSeconds = Math.Clamp(PacketGuardPlayerWarningCooldownSeconds, 3, 300);
        PacketGuardMaxViewDistanceBlocks = Math.Clamp(PacketGuardMaxViewDistanceBlocks, 64, 4096);
        PacketGuardIngressMaxPacketKiB = Math.Clamp(PacketGuardIngressMaxPacketKiB, 128, 8192);
        PacketGuardIngressPacketsPerSecond = Math.Clamp(PacketGuardIngressPacketsPerSecond, 60, 2000);
        PacketGuardIngressPacketBurst = Math.Clamp(PacketGuardIngressPacketBurst, PacketGuardIngressPacketsPerSecond, 6000);
        PacketGuardBlockPacketsPerSecond = Math.Clamp(PacketGuardBlockPacketsPerSecond, 10, 500);
        PacketGuardBlockPacketBurst = Math.Clamp(PacketGuardBlockPacketBurst, PacketGuardBlockPacketsPerSecond, 1500);
        PacketGuardSettingsPacketsPerSecond = Math.Clamp(PacketGuardSettingsPacketsPerSecond, 1, 20);
        PacketGuardSettingsPacketBurst = Math.Clamp(PacketGuardSettingsPacketBurst, PacketGuardSettingsPacketsPerSecond, 60);
        PacketGuardIngressFindingCooldownSeconds = Math.Clamp(PacketGuardIngressFindingCooldownSeconds, 2, 120);
        // v61 closes the old compatibility default that accepted arbitrary >=128-block jumps.
        // Known server teleports still use explicit trust hooks. Operators can opt back into
        // reconciliation after migration if a specific mod cannot mark its teleports.
        if (incomingSchema < 61) PacketGuardUnverifiedTeleportReconciliationEnabled = false;
        PacketGuardMovementSampleMs = Math.Clamp(PacketGuardMovementSampleMs, 75, 500);
        PacketGuardFlightReviewConfirmSeconds = Math.Clamp(PacketGuardFlightReviewConfirmSeconds, 3, 60);
        PacketGuardFlightReviewSampleMs = Math.Clamp(PacketGuardFlightReviewSampleMs, 200, 1000);
        PacketGuardHardMoveDistance = Math.Clamp(PacketGuardHardMoveDistance, 4.0, 96.0);
        PacketGuardMovementMaxSampleScale = Math.Clamp(PacketGuardMovementMaxSampleScale, 1.0, 8.0);
        PacketGuardMovementConfirmSamples = Math.Clamp(PacketGuardMovementConfirmSamples, 1, 4);
        // v34 hardens PacketGuard against live false positives observed on modded movement.
        // Preserve administrator-customized values; only migrate the exact v33 defaults.
        if (incomingSchema < 34)
        {
            if (PacketGuardAccelerationConfirmWindowMs == 650) PacketGuardAccelerationConfirmWindowMs = 900;
            if (PacketGuardNoclipRequiredCollisionSamples == 2) PacketGuardNoclipRequiredCollisionSamples = 3;
            if (Math.Abs(PacketGuardNoclipMinPenetrationDepth - 0.14) < 0.001) PacketGuardNoclipMinPenetrationDepth = 0.18;
            if (PacketGuardNoclipEndpointConfirmSamples == 2) PacketGuardNoclipEndpointConfirmSamples = 3;
        }
        // v36 disables collision-box based noclip detection by default. Live telemetry showed
        // legitimate trapdoors, doors, kilns, rock edges, and modded collision shapes can leave
        // players partially embedded even with negligible movement. Existing installs are migrated
        // to OFF once so the detector cannot continue kicking players after this update. Admins may
        // explicitly re-enable it from Anti-Cheat -> Packet Protection after reviewing telemetry.
        if (incomingSchema < 36)
        {
            PacketGuardNoclipEnabled = false;
        }

        // v37 hardens modded mount enforcement after live Draconis testing. Rider-distance
        // and rider-to-mount correlation remain diagnostic signals but are no longer standalone
        // enforcement causes. Legal mounted positions also refresh the rollback checkpoint so a
        // later correction cannot return a rider to a stale pre-mount location.

        PacketGuardMovementConfirmWindowMs = Math.Clamp(PacketGuardMovementConfirmWindowMs, 100, 2000);
        PacketGuardMovementExtremeMultiplier = Math.Clamp(PacketGuardMovementExtremeMultiplier, 1.5, 6.0);
        PacketGuardCorrectionGraceMs = Math.Clamp(PacketGuardCorrectionGraceMs, 250, 5000);
        PacketGuardMaxHorizontalSpeed = Math.Clamp(PacketGuardMaxHorizontalSpeed, 8.0, 200.0);
        PacketGuardGroundMaxHorizontalSpeed = Math.Clamp(PacketGuardGroundMaxHorizontalSpeed, 6.0, 200.0);
        PacketGuardSwimmingMaxHorizontalSpeed = Math.Clamp(PacketGuardSwimmingMaxHorizontalSpeed, 4.0, 120.0);
        PacketGuardAirborneMaxHorizontalSpeed = Math.Clamp(PacketGuardAirborneMaxHorizontalSpeed, 6.0, 200.0);
        PacketGuardSpeedWindowMs = Math.Clamp(PacketGuardSpeedWindowMs, 250, 3000);
        PacketGuardSpeedConfirmWindows = Math.Clamp(PacketGuardSpeedConfirmWindows, 1, 5);
        PacketGuardAccelerationMinSpeed = Math.Clamp(PacketGuardAccelerationMinSpeed, 4.0, 100.0);
        PacketGuardMaxHorizontalAcceleration = Math.Clamp(PacketGuardMaxHorizontalAcceleration, 20.0, 1000.0);
        PacketGuardAccelerationConfirmSamples = Math.Clamp(PacketGuardAccelerationConfirmSamples, 1, 6);
        PacketGuardAccelerationConfirmWindowMs = Math.Clamp(PacketGuardAccelerationConfirmWindowMs, 100, 3000);
        PacketGuardAccelerationSampleMinMs = Math.Clamp(PacketGuardAccelerationSampleMinMs, 100, 1000);
        PacketGuardAccelerationSmoothingFactor = Math.Clamp(PacketGuardAccelerationSmoothingFactor, 0.05, 1.0);
        PacketGuardNoclipMinPathDistance = Math.Clamp(PacketGuardNoclipMinPathDistance, 0.5, 8.0);
        PacketGuardNoclipSampleStep = Math.Clamp(PacketGuardNoclipSampleStep, 0.10, 0.75);
        PacketGuardNoclipRequiredCollisionSamples = Math.Clamp(PacketGuardNoclipRequiredCollisionSamples, 1, 6);
        PacketGuardNoclipMinPenetrationDepth = Math.Clamp(PacketGuardNoclipMinPenetrationDepth, 0.03, 0.75);
        PacketGuardNoclipEndpointMinPenetrationDepth = Math.Clamp(PacketGuardNoclipEndpointMinPenetrationDepth, 0.05, 0.75);
        PacketGuardNoclipEndpointConfirmSamples = Math.Clamp(PacketGuardNoclipEndpointConfirmSamples, 1, 6);
        PacketGuardNoclipEndpointMinNetMovement = Math.Clamp(PacketGuardNoclipEndpointMinNetMovement, 0.10, 2.0);
        PacketGuardSafePositionHistorySize = Math.Clamp(PacketGuardSafePositionHistorySize, 4, 64);
        PacketGuardSafePositionMaxAgeMs = Math.Clamp(PacketGuardSafePositionMaxAgeMs, 1000, 30000);
        PacketGuardRollbackSafetyLagMs = Math.Clamp(PacketGuardRollbackSafetyLagMs, 0, 3000);
        PacketGuardRollbackValidationCandidates = Math.Clamp(PacketGuardRollbackValidationCandidates, 1, 32);
        PacketGuardRollbackAntiLoopWindowMs = Math.Clamp(PacketGuardRollbackAntiLoopWindowMs, 250, 10000);
        PacketGuardRollbackAntiLoopRisk = Math.Clamp(PacketGuardRollbackAntiLoopRisk, 1.0, 100.0);
        PacketGuardMovementDuplicateFindingCooldownMs = Math.Clamp(PacketGuardMovementDuplicateFindingCooldownMs, 0, 10000);
        PacketGuardUnverifiedTeleportMinDistance = Math.Clamp(PacketGuardUnverifiedTeleportMinDistance, 32.0, 4096.0);
        PacketGuardUnverifiedTeleportRepeatWindowMs = Math.Clamp(PacketGuardUnverifiedTeleportRepeatWindowMs, 5000, 300000);
        PacketGuardUnverifiedTeleportRepeatCount = Math.Clamp(PacketGuardUnverifiedTeleportRepeatCount, 2, 10);
        PacketGuardMovementRiskDecayPerSecond = Math.Clamp(PacketGuardMovementRiskDecayPerSecond, 0.1, 25.0);
        PacketGuardMovementRiskAcceleration = Math.Clamp(PacketGuardMovementRiskAcceleration, 1.0, 100.0);
        PacketGuardMovementRiskSpeed = Math.Clamp(PacketGuardMovementRiskSpeed, 1.0, 100.0);
        PacketGuardMovementRiskNoclip = Math.Clamp(PacketGuardMovementRiskNoclip, 1.0, 100.0);
        PacketGuardMovementRiskHardMove = Math.Clamp(PacketGuardMovementRiskHardMove, 1.0, 100.0);
        PacketGuardMovementRiskMount = Math.Clamp(PacketGuardMovementRiskMount, 1.0, 100.0);
        PacketGuardMovementConfidenceCorrelationWindowMs = Math.Clamp(PacketGuardMovementConfidenceCorrelationWindowMs, 250, 10000);
        PacketGuardMovementConfidenceCorrelationBonus = Math.Clamp(PacketGuardMovementConfidenceCorrelationBonus, 0.0, 50.0);
        PacketGuardMovementConfidenceGuardedThreshold = Math.Clamp(PacketGuardMovementConfidenceGuardedThreshold, 1.0, 95.0);
        PacketGuardMovementConfidenceHighThreshold = Math.Clamp(PacketGuardMovementConfidenceHighThreshold, PacketGuardMovementConfidenceGuardedThreshold + 1.0, 98.0);
        PacketGuardMovementConfidenceCriticalThreshold = Math.Clamp(PacketGuardMovementConfidenceCriticalThreshold, PacketGuardMovementConfidenceHighThreshold + 1.0, 100.0);
        PacketGuardCombatMaxRequestsPerSecond = Math.Clamp(PacketGuardCombatMaxRequestsPerSecond, 5, 100);
        PacketGuardInventoryMaxRequestsPerSecond = Math.Clamp(PacketGuardInventoryMaxRequestsPerSecond, 20, 500);
        PacketGuardWeaponMinimumIntervalMs ??= new(StringComparer.OrdinalIgnoreCase);
        PacketGuardWeaponMinimumIntervalMs = PacketGuardWeaponMinimumIntervalMs.Where(x=>!string.IsNullOrWhiteSpace(x.Key)&&x.Key.Length<=160&&x.Value>=50&&x.Value<=5000).GroupBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).Take(128).ToDictionary(x=>x.Key,x=>x.First().Value,StringComparer.OrdinalIgnoreCase);
        PacketGuardMiningReviewOnlyTools = (PacketGuardMiningReviewOnlyTools ?? new()).Where(x=>!string.IsNullOrWhiteSpace(x)&&x.Length<=160).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
        PacketGuardBlockNukerLimit = Math.Clamp(PacketGuardBlockNukerLimit, 5, 200);
        PacketGuardNoclipCorrectionsBeforeKick = Math.Clamp(PacketGuardNoclipCorrectionsBeforeKick, 2, 50);
        PacketGuardNoclipCorrectionDecaySeconds = Math.Clamp(PacketGuardNoclipCorrectionDecaySeconds, 30, 3600);
        PacketGuardNoclipCorrectionMinimumIntervalSeconds = Math.Clamp(PacketGuardNoclipCorrectionMinimumIntervalSeconds, 5, 300);
        PacketGuardMountGraceMs = Math.Clamp(PacketGuardMountGraceMs, 250, 10000);
        PacketGuardMountProfileConfirmSamples = Math.Clamp(PacketGuardMountProfileConfirmSamples, 1, 5);
        PacketGuardMountProfileConfirmWindowMs = Math.Clamp(PacketGuardMountProfileConfirmWindowMs, 150, 3000);
        PacketGuardShipwrightMaxHorizontalSpeed = Math.Clamp(PacketGuardShipwrightMaxHorizontalSpeed, 20.0, 300.0);
        PacketGuardShipwrightMaxVerticalSpeed = Math.Clamp(PacketGuardShipwrightMaxVerticalSpeed, 5.0, 150.0);
        PacketGuardShipwrightMaxAcceleration = Math.Clamp(PacketGuardShipwrightMaxAcceleration, 50.0, 3000.0);
        PacketGuardShipwrightMaxRiderDistance = Math.Clamp(PacketGuardShipwrightMaxRiderDistance, 4.0, 64.0);
        PacketGuardDraconisMaxHorizontalSpeed = Math.Clamp(PacketGuardDraconisMaxHorizontalSpeed, 20.0, 300.0);
        PacketGuardDraconisMaxVerticalSpeed = Math.Clamp(PacketGuardDraconisMaxVerticalSpeed, 5.0, 150.0);
        PacketGuardDraconisMaxAcceleration = Math.Clamp(PacketGuardDraconisMaxAcceleration, 50.0, 3000.0);
        PacketGuardDraconisMaxRiderDistance = Math.Clamp(PacketGuardDraconisMaxRiderDistance, 4.0, 64.0);
        PacketGuardUnknownMountMaxHorizontalSpeed = Math.Clamp(PacketGuardUnknownMountMaxHorizontalSpeed, 20.0, 300.0);
        PacketGuardUnknownMountMaxVerticalSpeed = Math.Clamp(PacketGuardUnknownMountMaxVerticalSpeed, 5.0, 150.0);
        PacketGuardUnknownMountMaxAcceleration = Math.Clamp(PacketGuardUnknownMountMaxAcceleration, 50.0, 3000.0);
        PacketGuardMountAccelerationMinSpeedRatio = Math.Clamp(PacketGuardMountAccelerationMinSpeedRatio, 0.50, 1.25);
        PacketGuardDiagnosticSpeedHistorySamples = Math.Clamp(PacketGuardDiagnosticSpeedHistorySamples, 3, 16);
        PacketGuardUnknownMountMaxRiderDistance = Math.Clamp(PacketGuardUnknownMountMaxRiderDistance, 4.0, 64.0);
        PacketGuardShipwrightMaxRiderRelativeSpeed = Math.Clamp(PacketGuardShipwrightMaxRiderRelativeSpeed, 5.0, 150.0);
        PacketGuardDraconisMaxRiderRelativeSpeed = Math.Clamp(PacketGuardDraconisMaxRiderRelativeSpeed, 5.0, 150.0);
        PacketGuardUnknownMountMaxRiderRelativeSpeed = Math.Clamp(PacketGuardUnknownMountMaxRiderRelativeSpeed, 5.0, 150.0);
        PacketGuardEngineTeleportGraceMs = Math.Clamp(PacketGuardEngineTeleportGraceMs, 500, 15000);
        PacketGuardTPRunesGraceMs = Math.Clamp(PacketGuardTPRunesGraceMs, 1000, 20000);
        PacketGuardTh3EssentialsSpawnGraceMs = Math.Clamp(PacketGuardTh3EssentialsSpawnGraceMs, 1000, 20000);
        PacketGuardReachTolerance = Math.Clamp(PacketGuardReachTolerance, 0.25, 3.0);
        PacketGuardReachMaxLatencyCompensation = Math.Clamp(PacketGuardReachMaxLatencyCompensation, 0.0, 3.0);
        PacketGuardReachPositionHistoryMs = Math.Clamp(PacketGuardReachPositionHistoryMs, 100, 1500);
        PacketGuardReachMaxRewindMs = Math.Clamp(PacketGuardReachMaxRewindMs, 100, PacketGuardReachPositionHistoryMs);
        PacketGuardReachUseExtraTolerance = Math.Clamp(PacketGuardReachUseExtraTolerance, 0.0, 1.5);
        PacketGuardReachBreakExtraTolerance = Math.Clamp(PacketGuardReachBreakExtraTolerance, 0.0, 1.5);
        PacketGuardReachStrikeDecayMs = Math.Clamp(PacketGuardReachStrikeDecayMs, 500, 10000);
        PacketGuardReachNearThresholdStrikes = Math.Clamp(PacketGuardReachNearThresholdStrikes, 1, 8);
        PacketGuardReachStrikeWindowMs = Math.Clamp(PacketGuardReachStrikeWindowMs, 500, 10000);
        PacketGuardReachHardExcess = Math.Clamp(PacketGuardReachHardExcess, 0.5, 4.0);
        PacketGuardReachDistinctTargetsRequired = Math.Clamp(PacketGuardReachDistinctTargetsRequired, 1, 6);
        PacketGuardVeinWindowMs = Math.Clamp(PacketGuardVeinWindowMs, 500, 5000);
        PacketGuardConnectedBreakLimit = Math.Clamp(PacketGuardConnectedBreakLimit, 8, 40);
        PacketGuardConnectedBreakConfirmations = Math.Clamp(PacketGuardConnectedBreakConfirmations, 1, 5);
        PacketGuardConnectedBreakExtremeMultiplier = Math.Clamp(PacketGuardConnectedBreakExtremeMultiplier, 1.10, 4.0);
        PacketGuardMiningDuplicateTargetMs = Math.Clamp(PacketGuardMiningDuplicateTargetMs, 100, 1500);
        PacketGuardMiningCadenceMinExpectedMs = Math.Clamp(PacketGuardMiningCadenceMinExpectedMs, 100, 10000);
        PacketGuardMiningCadenceSuspiciousRatio = Math.Clamp(PacketGuardMiningCadenceSuspiciousRatio, 0.10, 0.85);
        PacketGuardMiningCadenceConfirmations = Math.Clamp(PacketGuardMiningCadenceConfirmations, 2, 8);
        PacketGuardMiningCadenceConfirmationWindowMs = Math.Clamp(PacketGuardMiningCadenceConfirmationWindowMs, 1000, 30000);
        PacketGuardTpsDegradedPercent = Math.Clamp(PacketGuardTpsDegradedPercent, 55.0, 99.0);
        PacketGuardTpsCriticalPercent = Math.Clamp(PacketGuardTpsCriticalPercent, 25.0, PacketGuardTpsDegradedPercent - 1.0);
        PacketGuardTpsMaxTimingMultiplier = Math.Clamp(PacketGuardTpsMaxTimingMultiplier, 1.0, 4.0);
        MovementRiskDecaySeconds = Math.Clamp(MovementRiskDecaySeconds, 30, 3600);
        MovementRiskReviewThreshold = Math.Clamp(MovementRiskReviewThreshold, 1, 100);
        MovementRiskSuspiciousThreshold = Math.Clamp(MovementRiskSuspiciousThreshold, MovementRiskReviewThreshold, 100);
        ReachRiskDecaySeconds = Math.Clamp(ReachRiskDecaySeconds, 30, 7200);
        ReachRiskReviewThreshold = Math.Clamp(ReachRiskReviewThreshold, 1, 100);
        ReachRiskSuspiciousThreshold = Math.Clamp(ReachRiskSuspiciousThreshold, ReachRiskReviewThreshold, 100);
        MiningRiskDecaySeconds = Math.Clamp(MiningRiskDecaySeconds, 30, 7200);
        MiningRiskReviewThreshold = Math.Clamp(MiningRiskReviewThreshold, 1, 100);
        MiningRiskSuspiciousThreshold = Math.Clamp(MiningRiskSuspiciousThreshold, MiningRiskReviewThreshold, 100);
        PacketAbuseRiskDecaySeconds = Math.Clamp(PacketAbuseRiskDecaySeconds, 30, 14400);
        PacketAbuseRiskReviewThreshold = Math.Clamp(PacketAbuseRiskReviewThreshold, 1, 100);
        PacketAbuseRiskSuspiciousThreshold = Math.Clamp(PacketAbuseRiskSuspiciousThreshold, PacketAbuseRiskReviewThreshold, 100);
        ClientIntegrityRiskDecaySeconds = Math.Clamp(ClientIntegrityRiskDecaySeconds, 60, 86400);
        ClientIntegrityRiskReviewThreshold = Math.Clamp(ClientIntegrityRiskReviewThreshold, 1, 100);
        ClientIntegrityRiskSuspiciousThreshold = Math.Clamp(ClientIntegrityRiskSuspiciousThreshold, ClientIntegrityRiskReviewThreshold, 100);
        IncidentCorrelationWindowSeconds = Math.Clamp(IncidentCorrelationWindowSeconds, 10, 600);
        IncidentCorrelationRequiredCategories = Math.Clamp(IncidentCorrelationRequiredCategories, 2, 5);
        IncidentCorrelationMaxRiskBonus = Math.Clamp(IncidentCorrelationMaxRiskBonus, 0, 35);
        // v41 reduces the persistent Anti-Cheat report archive to a gameplay-safe size.
        // Migrate the exact legacy stock value to the new 500-record default; explicitly
        // customized larger archives are retained only up to the new 1,000-record hard cap.
        if (incomingSchema < 41 && HistoryMaxRecords == 10000) HistoryMaxRecords = 500;
        HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 1, 3650);
        HistoryMaxRecords = Math.Clamp(HistoryMaxRecords, 100, 1000);
        RequiredClientModIds ??= new(); ForbiddenModIds ??= new(); ForbiddenFingerprints ??= new(); BlacklistProfiles ??= new();
        ApprovedClientBuildHashes ??= new(); AllowedExtraCodeModIds ??= new(); ConsequenceRules ??= new();
        SuspiciousRuntimeTerms ??= new(); ProtectedAssetPatterns ??= new(); StartupCoreAssemblyNames ??= new();
        if (incomingSchema < 2 && ConsequenceRules.Count == 0) SeedDefaultConsequenceRulesIfEmpty();
        if (incomingSchema < 4) ForbiddenModIds.AddRange(DefaultForbiddenModIds);
        if (incomingSchema < 12) SeedDefaultFingerprintsIfEmpty();
        SchemaVersion = Math.Max(SchemaVersion, CurrentSchemaVersion);
        RequiredClientModIds = NormalizeIds(RequiredClientModIds);
        FingerprintReleaseDepth = Math.Clamp(FingerprintReleaseDepth <= 0 ? 6 : FingerprintReleaseDepth, 1, 20);
        ForbiddenModIds = NormalizeIds(ForbiddenModIds);
        // v33: refresh the built-in profile to the latest exact snapshot of Newfie's supplied Anti-Cheat config,
        // including every supplied Mod ID and every supplied fingerprint/hash/release signature.
        // Replace the short-lived ID-only built-in snapshot once during migration. Loading remains
        // copy-on-load, so admins can edit the active list without mutating the saved profile.
        var suppliedBaseline = AntiCheatDefaultBlacklistProfileData.CreateProfile();
        var builtInProfile = BlacklistProfiles.FirstOrDefault(x => string.Equals(x.Id, AntiCheatDefaultBlacklistProfileData.ProfileId, StringComparison.OrdinalIgnoreCase));
        if (builtInProfile == null)
        {
            BlacklistProfiles.Add(suppliedBaseline);
        }
        else if (incomingSchema < 33)
        {
            builtInProfile.Name = suppliedBaseline.Name;
            builtInProfile.ModIds = suppliedBaseline.ModIds;
            builtInProfile.Fingerprints = suppliedBaseline.Fingerprints;
            if (builtInProfile.CreatedUtc <= 0) builtInProfile.CreatedUtc = suppliedBaseline.CreatedUtc;
            builtInProfile.UpdatedUtc = suppliedBaseline.UpdatedUtc;
        }
        if (incomingSchema < 33)
        {
            // Older saved profiles may only store Mod IDs. Preserve any hashes already present in the
            // active policy by snapshotting matching fingerprint records into those legacy profiles.
            foreach (var legacyProfile in BlacklistProfiles.Where(x => x != null && !string.Equals(x.Id, AntiCheatDefaultBlacklistProfileData.ProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                legacyProfile.Fingerprints ??= new();
                if (legacyProfile.Fingerprints.Count == 0)
                {
                    var ids = new HashSet<string>(legacyProfile.ModIds ?? new(), StringComparer.OrdinalIgnoreCase);
                    legacyProfile.Fingerprints = AntiCheatDefaultBlacklistProfileData.CloneFingerprints(ForbiddenFingerprints.Where(fp => fp != null && ids.Contains(fp.ModId ?? "")));
                }
            }
        }
        BlacklistProfiles = BlacklistProfiles.Where(x => x != null).Take(32).ToList();
        var seenProfileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in BlacklistProfiles)
        {
            string id = (profile.Id ?? "").Trim();
            if (id.Length == 0 || !seenProfileIds.Add(id)) { id = Guid.NewGuid().ToString("N"); seenProfileIds.Add(id); }
            profile.Id = id;
            profile.Name = Clean(profile.Name, 64, "Blacklist profile");
            profile.ModIds = NormalizeIds(profile.ModIds ?? new()).Take(256).ToList();
            profile.Fingerprints ??= new();
            profile.Fingerprints = profile.Fingerprints.Where(x => x != null).Take(256).ToList();
            if (profile.CreatedUtc < 0) profile.CreatedUtc = 0;
            if (profile.UpdatedUtc < profile.CreatedUtc) profile.UpdatedUtc = profile.CreatedUtc;
        }
        ActiveBlacklistProfileId = (ActiveBlacklistProfileId ?? "").Trim();
        if (ActiveBlacklistProfileId.Length > 0 && !BlacklistProfiles.Any(x => string.Equals(x.Id, ActiveBlacklistProfileId, StringComparison.OrdinalIgnoreCase)))
            ActiveBlacklistProfileId = "";
        AllowedExtraCodeModIds = NormalizeIds(AllowedExtraCodeModIds);
        SuspiciousRuntimeTerms = SuspiciousRuntimeTerms
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Clean(x, 48, ""))
            .Where(x => x.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
        ProtectedAssetPatterns = ProtectedAssetPatterns
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Clean(x, 160, "").ToLowerInvariant())
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
        ProtectedAssetDetailedEntries = Math.Clamp(ProtectedAssetDetailedEntries, 0, 128);
        StartupIntegrityGateNoticeCooldownSeconds = Math.Clamp(StartupIntegrityGateNoticeCooldownSeconds, 2, 30);
        if (StrictClientModDbEnforcementEnabled) { ClientModDbVerificationEnabled = true; RequireClient = true; }
        StartupCoreAssemblyNames = StartupCoreAssemblyNames
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Clean(x, 128, ""))
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        if (StartupCoreAssemblyNames.Count == 0) StartupCoreAssemblyNames.Add("VintagestoryAPI");
        ApprovedClientBuildHashes = ApprovedClientBuildHashes.Select(NormalizeHash).Where(x => x.Length == 64).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Take(32).ToList();
        ForbiddenFingerprints = ForbiddenFingerprints.Where(x => x != null).Take(256).ToList();
        foreach (var fp in ForbiddenFingerprints)
        {
            fp.ModId = (fp.ModId ?? "").Trim().ToLowerInvariant();
            fp.Source = Clean(fp.Source, 32, "");
            fp.ResolvedVersion = Clean(fp.ResolvedVersion, 64, "");
            fp.ResolvedFileName = Clean(fp.ResolvedFileName, 160, "");
            if (fp.ResolvedUtc < 0) fp.ResolvedUtc = 0;
            fp.PackageSha256 = NormalizeHash(fp.PackageSha256);
            fp.ContentSha256 = NormalizeHash(fp.ContentSha256);
            fp.AssemblyNames = (fp.AssemblyNames ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            fp.AssemblySha256s = (fp.AssemblySha256s ?? new()).Select(NormalizeHash).Where(x => x.Length == 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            fp.AssemblyMvids = (fp.AssemblyMvids ?? new()).Where(x => Guid.TryParse(x, out _)).Select(x => Guid.Parse(x).ToString("D")).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            fp.AssemblyStructureSha256s = (fp.AssemblyStructureSha256s ?? new()).Select(NormalizeHash).Where(x => x.Length == 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            fp.AssemblyBehaviorSha256s = (fp.AssemblyBehaviorSha256s ?? new()).Select(NormalizeHash).Where(x => x.Length == 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            fp.ModSystemTypeNames = (fp.ModSystemTypeNames ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).Take(64).ToList();
            fp.ManualEntries = (fp.ManualEntries ?? new()).Where(x => x != null).Take(32).ToList();
            var seenManualIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in fp.ManualEntries)
            {
                string id = (entry.Id ?? "").Trim();
                if (id.Length == 0 || !seenManualIds.Add(id)) { id = Guid.NewGuid().ToString("N"); seenManualIds.Add(id); }
                entry.Id = id;
                entry.Label = Clean(entry.Label, 80, "Manual fingerprint");
                entry.Version = Clean(entry.Version, 64, "");
                entry.FileName = Clean(entry.FileName, 160, "");
                entry.Notes = Clean(entry.Notes, 300, "");
                entry.PackageSha256 = NormalizeHash(entry.PackageSha256);
                entry.ContentSha256 = NormalizeHash(entry.ContentSha256);
                entry.AssemblySha256s = (entry.AssemblySha256s ?? new()).Select(NormalizeHash).Where(x => x.Length == 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
                entry.AssemblyMvids = (entry.AssemblyMvids ?? new()).Where(x => Guid.TryParse(x, out _)).Select(x => Guid.Parse(x).ToString("D")).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
                entry.AssemblyStructureSha256s = (entry.AssemblyStructureSha256s ?? new()).Select(NormalizeHash).Where(x => x.Length == 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
                entry.AssemblyBehaviorSha256s = (entry.AssemblyBehaviorSha256s ?? new()).Select(NormalizeHash).Where(x => x.Length == 64).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
                if (entry.CreatedUtc < 0) entry.CreatedUtc = 0;
                if (entry.UpdatedUtc < 0) entry.UpdatedUtc = 0;
            }
            fp.ManualEntries = fp.ManualEntries.Where(entry =>
                entry.PackageSha256.Length == 64 || entry.ContentSha256.Length == 64 || entry.AssemblySha256s.Count > 0 || entry.AssemblyMvids.Count > 0 || entry.AssemblyStructureSha256s.Count > 0 || entry.AssemblyBehaviorSha256s.Count > 0).ToList();
            fp.Releases = (fp.Releases ?? new()).Where(x => x != null).Take(6).ToList();
            foreach (var release in fp.Releases)
            {
                release.Version = Clean(release.Version, 64, "");
                release.FileName = Clean(release.FileName, 160, "");
                release.PackageSha256 = NormalizeHash(release.PackageSha256);
                release.ContentSha256 = NormalizeHash(release.ContentSha256);
                if (release.ResolvedUtc < 0) release.ResolvedUtc = 0;
                release.Assemblies = (release.Assemblies ?? new()).Where(x => x != null).Take(32).ToList();
                foreach (var asm in release.Assemblies)
                {
                    asm.AssemblyName = Clean(asm.AssemblyName, 160, "");
                    asm.Sha256 = NormalizeHash(asm.Sha256);
                    asm.StructureTokenCount = Math.Clamp(asm.StructureTokenCount, 0, 4096);
                    asm.SimilarityMinHash = (asm.SimilarityMinHash ?? new()).Take(StructuralSimilarity.SignatureSize).ToList();
                    asm.BehaviorSha256 = NormalizeHash(asm.BehaviorSha256);
                    asm.BehaviorTokenCount = Math.Clamp(asm.BehaviorTokenCount, 0, 4096);
                    asm.BehaviorMinHash = (asm.BehaviorMinHash ?? new()).Take(StructuralSimilarity.SignatureSize).ToList();
                }
            }
        }
        ConsequenceRules = ConsequenceRules.Where(x => x != null).Take(64).ToList();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in ConsequenceRules)
        {
            string candidateId = (rule.Id ?? "").Trim();
            if (candidateId.Length == 0 || !seenIds.Add(candidateId)) { candidateId = Guid.NewGuid().ToString("N"); seenIds.Add(candidateId); }
            rule.Id = candidateId; rule.Name = Clean(rule.Name, 80, "Consequence");
            rule.TriggerType = NormalizeChoice(rule.TriggerType, "FindingCode", "Status", "RiskAtLeast");
            rule.TriggerValue = Clean(rule.TriggerValue, 80, rule.TriggerType == "RiskAtLeast" ? "50" : "FORBIDDEN_MOD");
            rule.Subject = Clean(rule.Subject, 80, ""); rule.Action = NormalizeChoice(rule.Action, "Log", "Kick", "TempBan", "Freeze");
            rule.Duration = Clean(rule.Duration, 24, "30m"); rule.Message = Clean(rule.Message, 240, "Anti-cheat policy matched for {player}.");
        }

        // Repair configs affected by the pre-seeded collection bug. IDs are intentionally
        // excluded from this fingerprint because the duplicated defaults received a new GUID
        // every startup even though the actual rule was identical.
        var seenRules = new HashSet<string>(StringComparer.Ordinal);
        ConsequenceRules = ConsequenceRules.Where(rule => seenRules.Add(ConsequenceSemanticKey(rule))).ToList();
    }


    private static string ConsequenceSemanticKey(AntiCheatConsequenceRule rule)
    {
        return string.Join("\u001f", new[]
        {
            rule.Name ?? "",
            rule.Enabled ? "1" : "0",
            rule.TriggerType ?? "",
            rule.TriggerValue ?? "",
            rule.Subject ?? "",
            rule.Action ?? "",
            rule.Duration ?? "",
            rule.Message ?? ""
        });
    }

    private static List<string> NormalizeIds(IEnumerable<string> values) => values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static string NormalizeHash(string? value) { string v=(value??"").Trim().ToLowerInvariant(); return v.All(Uri.IsHexDigit) ? v : ""; }
    private static string NormalizeChoice(string? value, params string[] choices) { string v=(value??"").Trim(); foreach(string choice in choices) if(string.Equals(v,choice,StringComparison.OrdinalIgnoreCase)) return choice; return choices[0]; }
    private static string Clean(string? value, int max, string fallback) { string v=(value??"").Trim(); if(v.Length==0) return fallback; return v.Length<=max?v:v[..max]; }
}
