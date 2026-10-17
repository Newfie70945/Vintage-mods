using System;
using System.Collections.Generic;
using System.Linq;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.Config;

/// <summary>
/// Master feature gates for Newfie's Server Essentials Suite.
/// A disabled subsystem is not started, so its event handlers, timers, workers,
/// network channels, and background maintenance do not consume runtime resources.
/// Changes are persisted immediately and take full effect after a server restart.
/// </summary>
public sealed class SuiteFeatureConfig
{
    public const string ConfigFile = "NewfiesServerEssentialsFeatures.json";
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

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
        var loaded = ModConfigRecovery.LoadOrNull<SuiteFeatureConfig>(
            api,
            ConfigFile,
            "ServerEssentialsFeatures") ?? new SuiteFeatureConfig();

        loaded.SchemaVersion = Math.Max(CurrentSchemaVersion, loaded.SchemaVersion);
        SuiteConfigStorage.Store(api, loaded, ConfigFile);
        return loaded;
    }

    public void Save(ICoreServerAPI api)
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion);
        SuiteConfigStorage.Store(api, this, ConfigFile);
    }

    public SuiteFeatureConfig Clone() => new()
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

    public IReadOnlyList<(string Name, bool Enabled)> GetEntries() => new (string, bool)[]
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

    public bool TrySet(string? rawName, bool enabled, out string canonicalName)
    {
        canonicalName = NormalizeName(rawName);
        switch (canonicalName)
        {
            case "blocklogger": BlockLogger = enabled; break;
            case "ingamegui": IngameAdminGui = enabled; break;
            case "admin": Administration = enabled; break;
            case "playercommands": PlayerCommands = enabled; break;
            case "afk": Afk = enabled; break;
            case "anticheat": AntiCheat = enabled; break;
            case "animallimiter": AnimalLimiter = enabled; break;
            case "loginprotection": LoginProtection = enabled; break;
            case "webdashboard": WebDashboard = enabled; break;
            case "webconsole": WebConsole = enabled; break;
            case "webmap": WebMap = enabled; break;
            case "announcements": Announcements = enabled; break;
            case "reports": Reports = enabled; break;
            case "analytics": PlayerAnalytics = enabled; break;
            case "diagnostics": PerformanceDiagnostics = enabled; break;
            case "modupdates": ModUpdateChecker = enabled; break;
            case "logintelligence": LogIntelligence = enabled; break;
            case "performancecenter": PerformanceCenter = enabled; break;
            case "operationscenter": OperationsCenter = enabled; break;
            default: canonicalName = ""; return false;
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

    public string EnabledSummary() => string.Join(", ", GetEntries().Where(x => x.Enabled).Select(x => x.Name));
    public string DisabledSummary() => string.Join(", ", GetEntries().Where(x => !x.Enabled).Select(x => x.Name));

    private static string NormalizeName(string? raw)
    {
        var name = (raw ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        return name switch
        {
            "blocklog" or "logger" or "audit" => "blocklogger",
            "gui" or "ingame" or "investigator" or "adminui" => "ingamegui",
            "administration" or "admintools" => "admin",
            "playercommand" or "playercmd" or "playercmds" or "commands" => "playercommands",
            "away" or "awayfromkeyboard" or "afkcamera" => "afk",
            "anti-cheat" or "integrity" => "anticheat",
            "animal" or "animals" or "limiter" => "animallimiter",
            "login" or "loginprot" => "loginprotection",
            "web" or "dashboard" => "webdashboard",
            "console" => "webconsole",
            "map" or "webmapaddon" or "cartographer" => "webmap",
            "announcement" => "announcements",
            "report" => "reports",
            "playeranalytics" or "playercount" => "analytics",
            "profiler" or "performance" or "serverhealth" => "diagnostics",
            "updates" or "modupdate" => "modupdates",
            "logs" or "logintel" => "logintelligence",
            "perfcenter" => "performancecenter",
            "ops" or "opscenter" => "operationscenter",
            _ => name
        };
    }
}
