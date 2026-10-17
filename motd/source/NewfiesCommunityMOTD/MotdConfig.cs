using System.Collections.Generic;

namespace NewfiesCommunityMOTD;

public class MotdConfig
{
	public string? Title { get; set; }

	public string? Subtitle { get; set; }

	public MotdFormat? Format { get; set; }

	public List<string>? ContentLines { get; set; } = new List<string>();

	public string? Content { get; set; }

	public int? Width { get; set; }

	public int? Height { get; set; }

	public bool? OpenOnEveryJoin { get; set; }

	public bool? OnlyOncePerSession { get; set; }

	public bool? OnlyOncePerVersion { get; set; }

	public string? VersionLabel { get; set; }

	public bool? EnableDontShowAgain { get; set; }

	public int? ScrollBarWidth { get; set; }

	public float? ScrollEstimateMultiplier { get; set; }

	public int? ScrollExtraPixels { get; set; }

	public int? ScrollOverrideHeightPx { get; set; }

	public bool? EnableLinkButton { get; set; }

	public string? LinkButtonText { get; set; }

	public string? LinkUrl { get; set; }

	public bool? EnableWebMapButton { get; set; }

	public string? WebMapButtonText { get; set; }

	public string? WebMapUrl { get; set; }

	public string? AccentColor { get; set; }

	public bool? EnableLogo { get; set; }

	public string? LogoAsset { get; set; }

	public string? LogoSource { get; set; }

	public string? ServerLogoFile { get; set; }

	public int? LogoWidth { get; set; }

	public int? LogoHeight { get; set; }

	public bool? LogoPreserveAspectRatio { get; set; }

	public bool? EnablePlayersTab { get; set; }

	public bool? ShowPlayerRoles { get; set; }

	public bool? ShowPlayerPing { get; set; }

	public bool? SeparateStaff { get; set; }

	public string? FooterText { get; set; }

	public bool? EnableAnnouncementsTab { get; set; }

	public bool? EnableUpdatesTab { get; set; }

	public bool? EnableEventsTab { get; set; }

	public bool? EnableRulesTab { get; set; }

	public bool? EnableServerTab { get; set; }

	public string? ServerInfoVtml { get; set; }

	public bool? EnableCommandsTab { get; set; }

	public bool? EnableCommunityTab { get; set; }

	public string? AnnouncementsVtml { get; set; }

	public string? UpdatesVtml { get; set; }

	public string? EventsVtml { get; set; }

	public string? RulesVtml { get; set; }

	public string? CommandsVtml { get; set; }

	public string? CommunityVtml { get; set; }

	public bool? UseDiscordAnnouncements { get; set; }

	public bool? UseDiscordUpdates { get; set; }

	public bool? UseDiscordEvents { get; set; }

	public bool? MergeLocalAndDiscord { get; set; }

	public int? DiscordMaxItemsPerFeed { get; set; }

	public bool? UseSuiteStatus { get; set; }

	public bool? ServerInfoShowServer { get; set; }

	public bool? ServerInfoShowWorld { get; set; }

	public bool? ServerInfoShowGameplay { get; set; }

	public bool? ServerInfoShowProspecting { get; set; }

	public bool? ServerInfoShowTemporal { get; set; }

	public bool? ServerInfoShowMods { get; set; }
}
