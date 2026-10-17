namespace BlockLogger.Admin;

public sealed class JailSentence
{
	public string PlayerUid { get; set; } = "";

	public string LastKnownName { get; set; } = "";

	public string JailedBy { get; set; } = "";

	public string Reason { get; set; } = "";

	public long StartedAtUnixMilliseconds { get; set; }

	public long ReleaseAtUnixMilliseconds { get; set; }

	public bool ReleasePending { get; set; }

	public JailPosition ReturnPosition { get; set; } = new JailPosition();
}
