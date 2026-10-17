namespace BlockLogger.WebDashboard;

public sealed class BadBlockEntityRecord
{
	public string Key { get; set; } = "";

	public int X { get; set; }

	public int Y { get; set; }

	public int Z { get; set; }

	public int Dimension { get; set; }

	public string BlockCode { get; set; } = "";

	public string BlockEntityType { get; set; } = "";

	public long Occurrences { get; set; }

	public long FirstSeenUnixMilliseconds { get; set; }

	public long LastSeenUnixMilliseconds { get; set; }

	public string LastException { get; set; } = "";

	public string LastTopFrame { get; set; } = "";

	public bool Tracking { get; set; } = true;

	public string LastAction { get; set; } = "";

	public string LastActionBy { get; set; } = "";

	public long LastActionUnixMilliseconds { get; set; }
}
