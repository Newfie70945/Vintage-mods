using System;

namespace NewfiesAntiCheat.Shared;

[Flags]
public enum ClientScannerFlags
{
	None = 0,
	Mods = 1,
	Assemblies = 2,
	Integrity = 4,
	Runtime = 8,
	Harmony = 0x10,
	Hotkeys = 0x20,
	ClientCommands = 0x40,
	ProtectedAssets = 0x80,
	PackageMetrics = 0x100,
	ReportHashV2 = 0x200,
	BoundHeartbeat = 0x400,
	ChallengeQueueing = 0x800,
	ModTransferConsent = 0x1000,
	ServerBaselineSelfIntegrity = 0x2000,
	RandomizedSelfAttestation = 0x4000,
	SignedBuildManifest = 0x8000,
	OreShieldClientSync = 0x10000,
	GhostVisibilitySafe = 0x20000,
	Core = Mods | Assemblies | Integrity | Runtime | Harmony | Hotkeys | ClientCommands | ProtectedAssets,
	HardenedProtocol9 = ReportHashV2 | BoundHeartbeat,
	CurrentClientRelease = HardenedProtocol9 | ChallengeQueueing | ModTransferConsent | ServerBaselineSelfIntegrity | RandomizedSelfAttestation | SignedBuildManifest | OreShieldClientSync | GhostVisibilitySafe,
	All = CurrentClientRelease | Core | PackageMetrics
}
