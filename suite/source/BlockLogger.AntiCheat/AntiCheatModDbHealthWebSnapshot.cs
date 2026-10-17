namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModDbHealthWebSnapshot
{
	public bool Enabled { get; set; }

	public int CacheEntries { get; set; }

	public int Pending { get; set; }

	public int Resolved { get; set; }

	public int NotFound { get; set; }

	public int Unavailable { get; set; }

	public int Fresh { get; set; }

	public int Stale { get; set; }

	public long LastCheckedUtc { get; set; }

	public long LastResolvedUtc { get; set; }

	public long LastUnavailableUtc { get; set; }

	public int MaxConcurrentRequests { get; set; }

	public int ServerBaselineTotal { get; set; }

	public int ServerBaselineFresh { get; set; }

	public int ServerBaselinePending { get; set; }

	public int ServerBaselineResolved { get; set; }

	public int ServerBaselineNotFound { get; set; }

	public int ServerBaselineUnavailable { get; set; }

	public int ServerBaselineOfficialMatches { get; set; }

	public int ServerBaselineCustomBuilds { get; set; }

	public int ServerBaselineDiagnosticDifferences { get; set; }

	public long ServerBaselineLastQueuedUtc { get; set; }
}
