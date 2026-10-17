using System;

namespace NewfiesAntiCheat.Shared;

public static class AntiCheatProtocol
{
    public const string ChannelName = "newfiesanticheat";
    public const int ProtocolVersion = 13;
    public const string ClientModId = "newfiesanticheatclient";
    public const string ServerModId = "newfiesserveressentialssuite";
    public const int DefaultChallengeTimeoutSeconds = 30;
}

[Flags]
public enum ClientScannerFlags
{
    None = 0,
    Mods = 1 << 0,
    Assemblies = 1 << 1,
    Integrity = 1 << 2,
    Runtime = 1 << 3,
    Harmony = 1 << 4,
    Hotkeys = 1 << 5,
    ClientCommands = 1 << 6,
    ProtectedAssets = 1 << 7,
    // Optional hardened capabilities. Protocol 13 adds randomized challenge-bound IL/metadata
    // attestation on top of authoritative server-baseline self-integrity while retaining explicit
    // per-request player authorization before suspicious mod package transfer.
    PackageMetrics = 1 << 8,
    ReportHashV2 = 1 << 9,
    BoundHeartbeat = 1 << 10,
    ChallengeQueueing = 1 << 11,
    ModTransferConsent = 1 << 12,
    ServerBaselineSelfIntegrity = 1 << 13,
    RandomizedSelfAttestation = 1 << 14,
    SignedBuildManifest = 1 << 15,
    OreShieldClientSync = 1 << 16,
    // Client can safely process Server Essentials ghost hide/restore packets on Vintage Story 1.22.7
    // without directly binding to the Entity.Pos field/property ABI. Older 1.1.26 clients do not
    // advertise this flag and are intentionally skipped by server ghost-visibility broadcasts.
    GhostVisibilitySafe = 1 << 17,
    Core = Mods | Assemblies | Integrity | Runtime | Harmony | Hotkeys | ClientCommands | ProtectedAssets,
    HardenedProtocol9 = ReportHashV2 | BoundHeartbeat,
    CurrentClientRelease = HardenedProtocol9 | ChallengeQueueing | ModTransferConsent | ServerBaselineSelfIntegrity | RandomizedSelfAttestation | SignedBuildManifest | OreShieldClientSync | GhostVisibilitySafe,
    All = Core | PackageMetrics | CurrentClientRelease
}

public enum AntiCheatSeverity
{
    Info = 0,
    Low = 1,
    Moderate = 2,
    High = 3,
    Critical = 4
}

public enum AntiCheatPlayerStatus
{
    Unknown = 0,
    AwaitingClient = 1,
    Scanning = 2,
    Clean = 3,
    Review = 4,
    Suspicious = 5,
    Failed = 6,
    TimedOut = 7,
    ProtocolMismatch = 8
}
