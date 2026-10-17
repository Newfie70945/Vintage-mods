using System.Collections.Generic;

namespace NewfiesCommunityMOTD;

internal class MotdClientState
{
	public HashSet<string> SeenEver = new HashSet<string>();

	public Dictionary<string, string> SeenByVersion = new Dictionary<string, string>();

	public HashSet<string> ReadAnnouncementIds = new HashSet<string>();

	public HashSet<string> ReadUpdateIds = new HashSet<string>();
}
