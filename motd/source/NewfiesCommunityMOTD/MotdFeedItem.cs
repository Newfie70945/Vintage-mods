namespace NewfiesCommunityMOTD;

public sealed class MotdFeedItem
{
	public string Id { get; set; } = "";

	public string Title { get; set; } = "";

	public string BodyVtml { get; set; } = "";

	public string Author { get; set; } = "";

	public long UnixTime { get; set; }

	public string Url { get; set; } = "";

	public bool Important { get; set; }
}
