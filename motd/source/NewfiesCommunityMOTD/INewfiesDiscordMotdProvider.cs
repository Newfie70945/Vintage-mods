using System;
using System.Collections.Generic;

namespace NewfiesCommunityMOTD;

public interface INewfiesDiscordMotdProvider
{
	int ProtocolVersion { get; }

	bool IsAvailable { get; }

	event Action? ContentChanged;

	IReadOnlyList<MotdFeedItem> GetAnnouncements();

	IReadOnlyList<MotdFeedItem> GetUpdates();

	IReadOnlyList<MotdFeedItem> GetEvents();
}
