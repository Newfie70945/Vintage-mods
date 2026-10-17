namespace NewfiesAntiCheat.Shared;

public static class OreShieldSyncProtocol
{
	public const string ChannelName = "newfiesoreshieldsync";

	public const int Version = 3;

	public const int MaxBatchEntries = 128;

	public const string Capability = "sync-remesh-v3";

	public const string BuildMarker = "ac1127-oreshield-v329-perf1";
}
