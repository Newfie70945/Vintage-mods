namespace NewfiesAntiCheat.Shared;

public enum AntiCheatPlayerStatus
{
	Unknown,
	AwaitingClient,
	Scanning,
	Clean,
	Review,
	Suspicious,
	Failed,
	TimedOut,
	ProtocolMismatch
}
