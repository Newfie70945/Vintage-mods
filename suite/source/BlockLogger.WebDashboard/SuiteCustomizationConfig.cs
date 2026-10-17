using System;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.WebDashboard;

public sealed class SuiteCustomizationConfig
{
	public const string ConfigFile = "NewfiesSuiteCustomization.json";

	public const int CurrentSchemaVersion = 4;

	public int SchemaVersion { get; set; } = 4;

	public string PanelName { get; set; } = "Newfie's Server Suite";

	public string HeaderSubtitle { get; set; } = "WebPanel";

	public string LoginKicker { get; set; } = "Secure Administration Console";

	public string LoginSubtitle { get; set; } = "Remote server administration. Fresh installs on 127.0.0.1 enter automatically without a login.";

	public string DashboardTitle { get; set; } = "Server Dashboard";

	public string DashboardSubtitle { get; set; } = "Actionable server and Suite overview: what needs attention, current operations state, health evidence, and recent lag.";

	public string SearchPlaceholder { get; set; } = "Search Server Essentials Suite…";

	public string SidebarFooterText { get; set; } = "Newfie's Server Suite · Server Administration";

	public string AccentHex { get; set; } = "#69AEFA";

	public string Theme { get; set; } = "original";

	public string BackgroundHex { get; set; } = "#101820";

	public string PanelHex { get; set; } = "#192631";

	public string TextHex { get; set; } = "#E3EDF5";

	public string LogoUrl { get; set; } = "";

	public string SurfaceTexture { get; set; } = "slate";

	public int TextureStrength { get; set; } = 12;

	public bool ShowModuleIcons { get; set; } = true;

	public bool HighContrastBorders { get; set; } = false;

	public bool ReduceMotion { get; set; } = false;

	public bool ShowBuildId { get; set; } = true;

	public bool ShowHeaderLogo { get; set; } = true;

	public bool CompactLayout { get; set; } = false;

	public int SidebarWidth { get; set; } = 232;

	public int CardRadius { get; set; } = 14;

	public bool ShowStatusBar { get; set; } = true;

	public bool ShowModuleStatus { get; set; } = true;

	public bool ShowDashboardAttention { get; set; } = true;

	public bool ShowDashboardOperations { get; set; } = true;

	public void Clamp()
	{
		SchemaVersion = Math.Max(4, SchemaVersion);
		PanelName = Clean(PanelName, "Newfie's Server Suite", 80);
		HeaderSubtitle = Clean(HeaderSubtitle, "WebPanel", 40);
		LoginKicker = Clean(LoginKicker, "Secure Administration Console", 80);
		LoginSubtitle = Clean(LoginSubtitle, "Remote server administration. Fresh installs on 127.0.0.1 enter automatically without a login.", 240);
		DashboardTitle = Clean(DashboardTitle, "Server Dashboard", 80);
		DashboardSubtitle = Clean(DashboardSubtitle, "Actionable server and Suite overview: what needs attention, current operations state, health evidence, and recent lag.", 240);
		SearchPlaceholder = Clean(SearchPlaceholder, "Search Server Essentials Suite…", 100);
		SidebarFooterText = Clean(SidebarFooterText, "Newfie's Server Suite · Server Administration", 120);
		AccentHex = NormalizeHex(AccentHex, "#69AEFA");
		Theme = (Theme ?? "original").ToLowerInvariant();
		bool flag;
		switch (Theme)
		{
		case "original":
		case "forest":
		case "slate":
		case "custom":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			Theme = "original";
		}
		BackgroundHex = NormalizeHex(BackgroundHex, "#101820");
		PanelHex = NormalizeHex(PanelHex, "#192631");
		TextHex = NormalizeHex(TextHex, "#E3EDF5");
		LogoUrl = (LogoUrl ?? "").Trim();
		if (LogoUrl.Length > 2048 || (LogoUrl.Length != 0 && (!LogoUrl.StartsWith("/") || LogoUrl.StartsWith("//") || LogoUrl.Contains('\\')) && (!Uri.TryCreate(LogoUrl, UriKind.Absolute, out Uri result) || !(result.Scheme == "https"))))
		{
			LogoUrl = "";
		}
		SurfaceTexture = (SurfaceTexture ?? "slate").Trim().ToLowerInvariant();
		switch (SurfaceTexture)
		{
		case "none":
		case "slate":
		case "brushed":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			SurfaceTexture = "slate";
		}
		TextureStrength = Math.Clamp(TextureStrength, 0, 25);
		SidebarWidth = Math.Clamp(SidebarWidth, 190, 320);
		CardRadius = Math.Clamp(CardRadius, 0, 24);
	}

	public static SuiteCustomizationConfig Load(ICoreServerAPI api)
	{
		SuiteCustomizationConfig suiteCustomizationConfig = ModConfigRecovery.LoadOrNull<SuiteCustomizationConfig>(api, "NewfiesSuiteCustomization.json", "SuiteCustomization") ?? new SuiteCustomizationConfig();
		suiteCustomizationConfig.Clamp();
		SuiteConfigStorage.Store(api, suiteCustomizationConfig, "NewfiesSuiteCustomization.json");
		return suiteCustomizationConfig;
	}

	private static string Clean(string? value, string fallback, int max)
	{
		string text = (string.IsNullOrWhiteSpace(value) ? fallback : value.Trim());
		return (text.Length > max) ? text.Substring(0, max) : text;
	}

	private static string NormalizeHex(string? value, string fallback)
	{
		string text = (value ?? "").Trim();
		if (text.Length == 6)
		{
			text = "#" + text;
		}
		if (text.Length != 7 || text[0] != '#')
		{
			return fallback;
		}
		for (int i = 1; i < text.Length; i++)
		{
			char character = text[i];
			if (!Uri.IsHexDigit(character))
			{
				return fallback;
			}
		}
		return text.ToUpperInvariant();
	}
}
