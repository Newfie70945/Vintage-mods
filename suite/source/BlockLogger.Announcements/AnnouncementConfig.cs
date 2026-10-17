using System.Collections.Generic;

namespace BlockLogger.Announcements;

public sealed class AnnouncementConfig
{
	public const int CurrentSchemaVersion = 1;

	public int SchemaVersion { get; set; } = 1;

	public bool Enabled { get; set; } = true;

	public string Prefix { get; set; } = "[Server]";

	public List<AnnouncementRule> Rules { get; set; } = new List<AnnouncementRule>();
}
