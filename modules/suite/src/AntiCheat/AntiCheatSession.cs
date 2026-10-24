using System;
using System.Collections.Generic;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatSession
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public bool ClientInstalled { get; set; }
    public string ClientVersion { get; set; } = "";
    public string ClientGameVersion { get; set; } = "";
    public string ClientPlatform { get; set; } = "";
    public int SupportedScanners { get; set; }
    public int RequestedScanners { get; set; }
    public int ProtocolVersion { get; set; }
    public bool StartupGatePending { get; set; }
    public bool StartupIntegrityVerified { get; set; }
    public long StartupGateStartedUtc { get; set; }
    public long StartupGateCompletedUtc { get; set; }
    public string StartupGateVerdict { get; set; } = "NotStarted";
    public int StartupGateBlockedActions { get; set; }
    public long StartupGateLastNoticeUtc { get; set; }
    public AntiCheatPlayerStatus Status { get; set; } = AntiCheatPlayerStatus.Unknown;
    internal ConfirmedSpeedRisk ConfirmedSpeed { get; } = new();
    public int RiskScore { get; set; }
    public int CorrelationRiskBonus { get; set; }
    public long ConnectedUtc { get; set; }
    public long LastHelloUtc { get; set; }
    public long ClientMissingSinceUtc { get; set; }
    public bool RequiredClientDisconnectScheduled { get; set; }
    public long LastScanUtc { get; set; }
    public long LastHeartbeatUtc { get; set; }
    public long NetworkPacketWindowStartUtc { get; set; }
    public int NetworkPacketWindowCount { get; set; }
    public long LastNetworkFloodUtc { get; set; }
    public int NetworkPacketsDropped { get; set; }
    public long NextScanUtc { get; set; }
    public string ChallengeId { get; set; } = "";
    public byte[] ChallengeNonce { get; set; } = Array.Empty<byte>();
    public long ChallengeIssuedUtc { get; set; }
    public long ChallengeExpiresUtc { get; set; }
    public bool ChallengeConsumed { get; set; }
    public List<SelfAttestationProbeDto> SelfAttestationProbes { get; set; } = new();
    public List<SelfAttestationResultDto> ExpectedSelfAttestationResults { get; set; } = new();
    public string ExpectedSelfAttestationDigest { get; set; } = "";
    public string LastAcceptedSelfAttestationDigest { get; set; } = "";
    public int SelfAttestationFailureCount { get; set; }
    public int SelfIntegrityMeasurementFailureCount { get; set; }
    public long LastSelfAttestationFailureUtc { get; set; }
    public string LastSelfAttestationFailureReason { get; set; } = "";
    public bool SelfAttestationConfirmationPending { get; set; }
    public long LastHeartbeatSequence { get; set; }
    public int HeartbeatBindingRejectCount { get; set; }
    public long LastHeartbeatBindingRejectUtc { get; set; }
    public List<ClientModDto> LastMods { get; set; } = new();
    public List<ClientAssemblyDto> LastAssemblies { get; set; } = new();
    public ClientIntegrityDto? LastIntegrity { get; set; }
    public ClientRuntimeDto? LastRuntime { get; set; }
    public ClientHarmonyDto? LastHarmony { get; set; }
    public List<ClientScannerMetricDto> LastScannerMetrics { get; set; } = new();
    public string LastRuntimeDigest { get; set; } = "";
    public string LastSelfIntegrityDigest { get; set; } = "";
    public string LastHeartbeatSelfIntegrityDigest { get; set; } = "";
    public string LastHeartbeatSelfAttestationDigest { get; set; } = "";
    public string LastHeartbeatRuntimeDigest { get; set; } = "";
    public int LastHeartbeatRuntimeGeneration { get; set; }
    public int LastHeartbeatAssemblyCount { get; set; }
    public int LastHeartbeatDynamicAssemblyCount { get; set; }
    public string LastHarmonyPatchDigest { get; set; } = "";
    public string LastHeartbeatHarmonyPatchDigest { get; set; } = "";
    public int LastHeartbeatHarmonyPatchCount { get; set; }
    public int LastHeartbeatSuspiciousHarmonyOwnerCount { get; set; }
    public long NextRuntimeChangeChallengeUtc { get; set; }
    public int ScanRetryCount { get; set; }
    public List<ScanFindingDto> Findings { get; set; } = new();
    // Last observation time for transient server-side behavior findings. PacketGuard findings
    // are live behavior evidence, not permanent identity/integrity evidence, so their risk must
    // age out instead of remaining sticky for the entire player session.
    public Dictionary<string, long> ServerBehaviorFindingUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // v40 timestamps all current evidence so each risk family can decay independently.
    // Key format is scanner|code|subject; values are last confirmed observation time.
    public Dictionary<string, long> FindingObservedUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> FamilyRiskScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> FamilyRiskLastEvidenceUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AppliedConsequenceKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string LastConsequence { get; set; } = "";
    public long LastConsequenceUtc { get; set; }
    public long ConsequenceDisconnectPendingUntilUtc { get; set; }
    public string AdminSuiteVersionWarningKey { get; set; } = "";
}
