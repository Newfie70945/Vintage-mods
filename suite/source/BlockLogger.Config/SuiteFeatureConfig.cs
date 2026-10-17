using System;
using System.Collections.Generic;
using System.Linq;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.Config;

public sealed class SuiteFeatureConfig
{
	public const string ConfigFile = "NewfiesServerEssentialsFeatures.json";

	public const int CurrentSchemaVersion = 2;

	public int SchemaVersion { get; set; } = 2;

	public bool BlockLogger { get; set; } = true;

	public bool IngameAdminGui { get; set; } = true;

	public bool Administration { get; set; } = true;

	public bool PlayerCommands { get; set; } = true;

	public bool Afk { get; set; } = true;

	public bool AntiCheat { get; set; } = true;

	public bool AnimalLimiter { get; set; } = true;

	public bool LoginProtection { get; set; } = true;

	public bool WebDashboard { get; set; } = true;

	public bool WebConsole { get; set; } = true;

	public bool WebMap { get; set; } = true;

	public bool Announcements { get; set; } = true;

	public bool Reports { get; set; } = true;

	public bool PlayerAnalytics { get; set; } = true;

	public bool PerformanceDiagnostics { get; set; } = true;

	public bool ModUpdateChecker { get; set; } = true;

	public bool LogIntelligence { get; set; } = true;

	public bool PerformanceCenter { get; set; } = true;

	public bool OperationsCenter { get; set; } = true;

	public static SuiteFeatureConfig Load(ICoreServerAPI api)
	{
		SuiteFeatureConfig suiteFeatureConfig = ModConfigRecovery.LoadOrNull<SuiteFeatureConfig>(api, "NewfiesServerEssentialsFeatures.json", "ServerEssentialsFeatures") ?? new SuiteFeatureConfig();
		suiteFeatureConfig.SchemaVersion = Math.Max(2, suiteFeatureConfig.SchemaVersion);
		SuiteConfigStorage.Store(api, suiteFeatureConfig, "NewfiesServerEssentialsFeatures.json");
		return suiteFeatureConfig;
	}

	public void Save(ICoreServerAPI api)
	{
		SchemaVersion = Math.Max(2, SchemaVersion);
		SuiteConfigStorage.Store(api, this, "NewfiesServerEssentialsFeatures.json");
	}

	public SuiteFeatureConfig Clone()
	{
		return new SuiteFeatureConfig
		{
			SchemaVersion = SchemaVersion,
			BlockLogger = BlockLogger,
			IngameAdminGui = IngameAdminGui,
			Administration = Administration,
			PlayerCommands = PlayerCommands,
			Afk = Afk,
			AntiCheat = AntiCheat,
			AnimalLimiter = AnimalLimiter,
			LoginProtection = LoginProtection,
			WebDashboard = WebDashboard,
			WebConsole = WebConsole,
			WebMap = WebMap,
			Announcements = Announcements,
			Reports = Reports,
			PlayerAnalytics = PlayerAnalytics,
			PerformanceDiagnostics = PerformanceDiagnostics,
			ModUpdateChecker = ModUpdateChecker,
			LogIntelligence = LogIntelligence,
			PerformanceCenter = PerformanceCenter,
			OperationsCenter = OperationsCenter
		};
	}

	public IReadOnlyList<(string Name, bool Enabled)> GetEntries()
	{
		return new (string, bool)[19]
		{
			("blocklogger", BlockLogger),
			("ingamegui", IngameAdminGui),
			("admin", Administration),
			("playercommands", PlayerCommands),
			("afk", Afk),
			("anticheat", AntiCheat),
			("animallimiter", AnimalLimiter),
			("loginprotection", LoginProtection),
			("webdashboard", WebDashboard),
			("webconsole", WebConsole),
			("webmap", WebMap),
			("announcements", Announcements),
			("reports", Reports),
			("analytics", PlayerAnalytics),
			("diagnostics", PerformanceDiagnostics),
			("modupdates", ModUpdateChecker),
			("logintelligence", LogIntelligence),
			("performancecenter", PerformanceCenter),
			("operationscenter", OperationsCenter)
		};
	}

	public bool TrySet(string? rawName, bool enabled, out string canonicalName)
	{
		canonicalName = NormalizeName(rawName);
		switch (canonicalName)
		{
		case "blocklogger":
			BlockLogger = enabled;
			break;
		case "ingamegui":
			IngameAdminGui = enabled;
			break;
		case "admin":
			Administration = enabled;
			break;
		case "playercommands":
			PlayerCommands = enabled;
			break;
		case "afk":
			Afk = enabled;
			break;
		case "anticheat":
			AntiCheat = enabled;
			break;
		case "animallimiter":
			AnimalLimiter = enabled;
			break;
		case "loginprotection":
			LoginProtection = enabled;
			break;
		case "webdashboard":
			WebDashboard = enabled;
			break;
		case "webconsole":
			WebConsole = enabled;
			break;
		case "webmap":
			WebMap = enabled;
			break;
		case "announcements":
			Announcements = enabled;
			break;
		case "reports":
			Reports = enabled;
			break;
		case "analytics":
			PlayerAnalytics = enabled;
			break;
		case "diagnostics":
			PerformanceDiagnostics = enabled;
			break;
		case "modupdates":
			ModUpdateChecker = enabled;
			break;
		case "logintelligence":
			LogIntelligence = enabled;
			break;
		case "performancecenter":
			PerformanceCenter = enabled;
			break;
		case "operationscenter":
			OperationsCenter = enabled;
			break;
		default:
			canonicalName = "";
			return false;
		}
		return true;
	}

	public void SetAll(bool enabled)
	{
		BlockLogger = enabled;
		IngameAdminGui = enabled;
		Administration = enabled;
		PlayerCommands = enabled;
		Afk = enabled;
		AntiCheat = enabled;
		AnimalLimiter = enabled;
		LoginProtection = enabled;
		WebDashboard = enabled;
		WebConsole = enabled;
		WebMap = enabled;
		Announcements = enabled;
		Reports = enabled;
		PlayerAnalytics = enabled;
		PerformanceDiagnostics = enabled;
		ModUpdateChecker = enabled;
		LogIntelligence = enabled;
		PerformanceCenter = enabled;
		OperationsCenter = enabled;
	}

	public string EnabledSummary()
	{
		return string.Join(", ", from x in GetEntries()
			where x.Enabled
			select x.Name);
	}

	public string DisabledSummary()
	{
		return string.Join(", ", from x in GetEntries()
			where !x.Enabled
			select x.Name);
	}

	private static string NormalizeName(string? raw)
	{
		string text = (raw ?? "").Trim().ToLowerInvariant().Replace("-", "")
			.Replace("_", "")
			.Replace(" ", "");
		if (1 == 0)
		{
		}
		string result;
		switch (text)
		{
		case "blocklog":
		case "logger":
		case "audit":
			result = "blocklogger";
			break;
		case "gui":
		case "ingame":
		case "investigator":
		case "adminui":
			result = "ingamegui";
			break;
		case "administration":
		case "admintools":
			result = "admin";
			break;
		case "playercommand":
		case "playercmd":
		case "playercmds":
		case "commands":
			result = "playercommands";
			break;
		case "away":
		case "awayfromkeyboard":
		case "afkcamera":
			result = "afk";
			break;
		case "anti-cheat":
		case "integrity":
			result = "anticheat";
			break;
		case "animal":
		case "animals":
		case "limiter":
			result = "animallimiter";
			break;
		case "login":
		case "loginprot":
			result = "loginprotection";
			break;
		case "web":
		case "dashboard":
			result = "webdashboard";
			break;
		case "console":
			result = "webconsole";
			break;
		case "map":
		case "webmapaddon":
		case "cartographer":
			result = "webmap";
			break;
		case "announcement":
			result = "announcements";
			break;
		case "report":
			result = "reports";
			break;
		case "playeranalytics":
		case "playercount":
			result = "analytics";
			break;
		case "profiler":
		case "performance":
		case "serverhealth":
			result = "diagnostics";
			break;
		case "updates":
		case "modupdate":
			result = "modupdates";
			break;
		case "logs":
		case "logintel":
			result = "logintelligence";
			break;
		case "perfcenter":
			result = "performancecenter";
			break;
		case "ops":
		case "opscenter":
			result = "operationscenter";
			break;
		default:
			result = text;
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}
}
