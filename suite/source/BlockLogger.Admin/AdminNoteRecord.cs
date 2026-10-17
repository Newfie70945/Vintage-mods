namespace BlockLogger.Admin;

public sealed class AdminNoteRecord
{
	public long Id { get; set; }

	public long UnixMilliseconds { get; set; }

	public string Author { get; set; } = "";

	public string Text { get; set; } = "";

	public bool Removed { get; set; }
}
