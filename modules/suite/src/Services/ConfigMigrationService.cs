using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BlockLogger.AnimalLimiter;
using BlockLogger.Afk;
using BlockLogger.Announcements;
using BlockLogger.AntiCheat;
using BlockLogger.Config;
using BlockLogger.LoginProtection;
using BlockLogger.PlayerCommands;
using BlockLogger.PlayerList;
using BlockLogger.Restarts;
using BlockLogger.WebDashboard;
using BlockLogger.WebMap;
using Newtonsoft.Json.Linq;
using NewfiesAntiCheat.Shared;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

/// <summary>
/// Central schema-migration coordinator for persistent Suite configuration files.
///
/// Migration rules live in one registry, every migration is applied one schema step at a time,
/// and the original JSON is copied to a timestamped pre-migration backup before the first change.
/// Existing subsystem Clamp()/Normalize() methods remain responsible for runtime value validation.
/// </summary>
public sealed class ConfigMigrationException : Exception
{
    public string ConfigFileName { get; }

    public ConfigMigrationException(string configFileName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ConfigFileName = configFileName;
    }
}

public static class ConfigMigrationService
{
    private const int BackupRetentionPerConfig = 5;

    private sealed class MigrationStep
    {
        public int TargetVersion { get; init; }
        public string Description { get; init; } = "Schema update";
        public Action<object, ICoreServerAPI, int>? Apply { get; init; }
    }

    private sealed class MigrationPlan
    {
        public string FileName { get; init; } = "";
        public int CurrentVersion { get; init; }
        public Dictionary<int, MigrationStep> Steps { get; init; } = new();
        // Used by configs whose established typed migration logic must see the original schema
        // as one operation (notably Anti-Cheat). If this raises SchemaVersion to CurrentVersion,
        // the normal per-step loop is skipped.
        public Action<object, ICoreServerAPI, int>? WholeMigration { get; init; }
    }

    public readonly record struct MigrationProbe(
        bool Registered,
        bool FileExists,
        string FileName,
        string Subsystem,
        string SourcePath,
        int SourceVersion,
        int TargetVersion,
        string BackupPath,
        bool NeedsMigration,
        bool FutureSchema);

    private static readonly IReadOnlyDictionary<string, MigrationPlan> Plans = BuildPlans();

    public static MigrationProbe Prepare(ICoreServerAPI api, string fileName, string subsystem)
    {
        if (!Plans.TryGetValue(fileName, out var plan))
            return default;

        string? source = FindConfigPath(api, fileName);
        if (source == null)
        {
            return new MigrationProbe(
                true, false, fileName, subsystem, "", plan.CurrentVersion,
                plan.CurrentVersion, "", false, false);
        }

        int sourceVersion;
        try
        {
            string raw = File.ReadAllText(source);
            var json = JObject.Parse(raw);
            sourceVersion = ReadSchemaVersion(json);
        }
        catch
        {
            // Let the normal config loader/recovery path handle malformed JSON. It already
            // creates a separate .corrupt-* backup and has subsystem-specific logging.
            return new MigrationProbe(
                true, true, fileName, subsystem, source, plan.CurrentVersion,
                plan.CurrentVersion, "", false, false);
        }

        if (sourceVersion > plan.CurrentVersion)
        {
            string message =
                $"{fileName} uses future schema {sourceVersion}; this build only knows schema {plan.CurrentVersion}. " +
                "The file has been left untouched. Install a Suite version that supports this schema before editing it.";
            api.Logger.Error("[ServerEssentials/ConfigMigration] {0}", message);
            throw new ConfigMigrationException(fileName, message);
        }

        if (sourceVersion >= plan.CurrentVersion)
        {
            return new MigrationProbe(
                true, true, fileName, subsystem, source, sourceVersion,
                plan.CurrentVersion, "", false, false);
        }

        string backup = CreatePreMigrationBackup(api, source, fileName, sourceVersion, plan.CurrentVersion);
        api.Logger.Notification(
            "[ServerEssentials/ConfigMigration] {0}: preparing schema {1} -> {2}. Backup: {3}",
            fileName,
            sourceVersion,
            plan.CurrentVersion,
            backup);

        return new MigrationProbe(
            true, true, fileName, subsystem, source, sourceVersion,
            plan.CurrentVersion, backup, true, false);
    }

    public static void ApplyIfNeeded(ICoreServerAPI api, object config, MigrationProbe probe)
    {
        if (!probe.Registered || !probe.FileExists || !probe.NeedsMigration || probe.FutureSchema)
            return;
        if (!Plans.TryGetValue(probe.FileName, out var plan))
            return;

        try
        {
            int current = probe.SourceVersion;

            if (plan.WholeMigration != null)
            {
                plan.WholeMigration(config, api, probe.SourceVersion);
                current = GetSchemaVersion(config, current);
            }

            if (current < plan.CurrentVersion)
            {
                for (int target = current + 1; target <= plan.CurrentVersion; target++)
                {
                    if (plan.Steps.TryGetValue(target, out var step))
                    {
                        step.Apply?.Invoke(config, api, probe.SourceVersion);
                        api.Logger.Notification(
                            "[ServerEssentials/ConfigMigration] {0}: applied schema {1} -> {2} ({3}).",
                            probe.FileName,
                            target - 1,
                            target,
                            step.Description);
                    }

                    SetSchemaVersion(config, target);
                }
            }

            // Persist the migrated shape immediately so a later subsystem startup failure cannot
            // leave the server repeatedly re-running the same migration on every restart.
            try
            {
                BlockLogger.Services.SuiteConfigStorage.Store(api, config, probe.FileName);
            }
            catch (Exception ex)
            {
                string message =
                    $"{probe.FileName}: migration reached schema {GetSchemaVersion(config, probe.SourceVersion)}, " +
                    $"but saving failed: {ex.GetType().Name}: {ex.Message}. Original backup remains at {probe.BackupPath}";
                api.Logger.Error("[ServerEssentials/ConfigMigration] {0}", message);
                throw new ConfigMigrationException(probe.FileName, message, ex);
            }

            api.Logger.Notification(
                "[ServerEssentials/ConfigMigration] {0}: migration complete at schema {1}. Pre-migration backup: {2}",
                probe.FileName,
                GetSchemaVersion(config, plan.CurrentVersion),
                probe.BackupPath);
        }
        catch (ConfigMigrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string message =
                $"{probe.FileName}: migration failed before the migrated config was committed. " +
                $"{ex.GetType().Name}: {ex.Message}. Original backup: {probe.BackupPath}";
            api.Logger.Error("[ServerEssentials/ConfigMigration] {0}", message);
            throw new ConfigMigrationException(probe.FileName, message, ex);
        }
    }

    public static bool IsRegistered(string fileName) => Plans.ContainsKey(fileName);

    public static int CurrentSchema(string fileName) => Plans.TryGetValue(fileName, out var plan)
        ? plan.CurrentVersion
        : 0;

    private static IReadOnlyDictionary<string, MigrationPlan> BuildPlans()
    {
        var plans = new Dictionary<string, MigrationPlan>(StringComparer.OrdinalIgnoreCase)
        {
            ["NewfiesBlocklogger.json"] = new MigrationPlan
            {
                FileName = "NewfiesBlocklogger.json",
                CurrentVersion = BlockLoggerConfig.CurrentSchemaVersion,
                Steps = new Dictionary<int, MigrationStep>
                {
                    [3] = new MigrationStep
                    {
                        TargetVersion = 3,
                        Description = "full-fidelity entity lifecycle audit defaults",
                        Apply = (obj, _, _) =>
                        {
                            var cfg = (BlockLoggerConfig)obj;
                            cfg.LogEntityDespawn = true;
                            cfg.AggregateEntitySpawnDespawn = false;
                            cfg.AdaptiveEntityAggregation = false;
                            cfg.BulkEntityPositionSampleIntervalMs = 0;
                        }
                    }
                }
            },
            ["NewfiesServerEssentialsFeatures.json"] = new MigrationPlan
            {
                FileName = "NewfiesServerEssentialsFeatures.json",
                CurrentVersion = SuiteFeatureConfig.CurrentSchemaVersion,
                Steps = new Dictionary<int, MigrationStep>
                {
                    [2] = new MigrationStep
                    {
                        TargetVersion = 2,
                        Description = "integrated AFK subsystem feature gate",
                        Apply = (obj, _, _) => ((SuiteFeatureConfig)obj).Afk = true
                    }
                }
            },
            ["NewfiesAnimalLimiter.json"] = new MigrationPlan
            {
                FileName = "NewfiesAnimalLimiter.json",
                CurrentVersion = AnimalLimiterConfig.CurrentSchemaVersion,
                Steps = new Dictionary<int, MigrationStep>
                {
                    [2] = new MigrationStep
                    {
                        TargetVersion = 2,
                        Description = "explicit enable gate and per-rule radius support",
                        Apply = (obj, _, _) =>
                        {
                            var cfg = (AnimalLimiterConfig)obj;
                            // Established pre-v2 configs were active before the explicit Enabled
                            // switch existed, so migration preserves their prior behavior.
                            if (!cfg.Enabled.HasValue) cfg.Enabled = true;
                        }
                    }
                }
            },
            ["NewfiesLoginProtection.json"] = Simple("NewfiesLoginProtection.json", LoginProtectionConfig.CurrentSchemaVersion),
            ["NewfiesAntiCheat.json"] = new MigrationPlan
            {
                FileName = "NewfiesAntiCheat.json",
                CurrentVersion = AntiCheatConfig.CurrentSchemaVersion,
                WholeMigration = (obj, api, sourceVersion) =>
                {
                    var cfg = (AntiCheatConfig)obj;
                    if (sourceVersion < 14 && IsAntiCheatCompanionInstalled(api))
                        cfg.Enabled = true;
                    // AntiCheatConfig.Clamp intentionally contains the historical, value-sensitive
                    // migrations for schemas 1..CurrentSchemaVersion. Invoking it here centralizes
                    // backup/orchestration without changing those well-tested transformations.
                    cfg.Clamp();
                }
            },
            ["NewfiesAutomatedRestarts.json"] = Simple("NewfiesAutomatedRestarts.json", AutomatedRestartConfig.CurrentSchemaVersion),
            ["NewfiesChatDisplay.json"] = new MigrationPlan
            {
                FileName = "NewfiesChatDisplay.json",
                CurrentVersion = ChatDisplayConfig.CurrentSchemaVersion,
                Steps = new Dictionary<int, MigrationStep>
                {
                    [2] = new MigrationStep
                    {
                        TargetVersion = 2,
                        Description = "scope formatting to General chat by default",
                        Apply = (obj, _, _) =>
                        {
                            var cfg = (ChatDisplayConfig)obj;
                            cfg.FormatGeneralChat = true;
                            cfg.FormatOtherChatGroups = false;
                            cfg.AdditionalFormattedChatGroupIds = new List<int>();
                        }
                    }
                }
            },
            ["NewfiesPlayerCommands.json"] = Simple("NewfiesPlayerCommands.json", PlayerCommandConfig.CurrentSchemaVersion),
            ["NewfiesAfk.json"] = Simple("NewfiesAfk.json", AfkConfig.CurrentSchemaVersion),
            ["NewfiesStarterKit.json"] = Simple("NewfiesStarterKit.json", StarterKitDefinition.CurrentSchemaVersion),
            ["NewfiesWarps.json"] = Simple("NewfiesWarps.json", WarpDatabase.CurrentSchemaVersion),
            ["NewfiesPlayerList.json"] = Simple("NewfiesPlayerList.json", PublicPlayerListConfig.CurrentSchemaVersion),
            ["NewfiesWebServer.json"] = Simple("NewfiesWebServer.json", WebDashboardConfig.CurrentSchemaVersion),
            ["NewfiesWebAdminAccounts.json"] = Simple("NewfiesWebAdminAccounts.json", WebAdminAccountStore.CurrentSchemaVersion),
            ["NewfiesCompatibilityIgnores.json"] = Simple("NewfiesCompatibilityIgnores.json", WebDashboardService.CompatibilityIgnoreSchemaVersion),
            ["NewfiesWebMap.json"] = BuildWebMapPlan(),
            ["NewfiesAnnouncements.json"] = Simple("NewfiesAnnouncements.json", AnnouncementConfig.CurrentSchemaVersion)
        };

        // Same-shape legacy filenames are registered too so their schema is upgraded before
        // the existing filename-import path copies them into the current Newfies-prefixed file.
        plans["blocklogger-v2.json"] = plans["NewfiesBlocklogger.json"];
        plans["loginprotection.json"] = plans["NewfiesLoginProtection.json"];

        return plans;
    }

    private static MigrationPlan BuildWebMapPlan()
    {
        var steps = new Dictionary<int, MigrationStep>
        {
            [2] = Step(2, "public-map privacy defaults", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.ShowPlayers = false;
                cfg.ShowBadBlockEntities = false;
                cfg.PublicUrl = "";
                cfg.DashboardUrl = "";
            }),
            [3] = Step(3, "terrain-export safety defaults", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.ShowActivity = false;
                cfg.ShowBadBlockEntities = false;
                cfg.ExportTerrainOnStart = true;
                cfg.LockServerDuringTerrainExport = true;
                cfg.TerrainTileSize = 256;
            }),
            [4] = Step(4, "live-player coordinate privacy gate", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.AllowLivePlayerData = false;
                cfg.ShowPlayers = false;
            }),
            [5] = Step(5, "admin waypoint layer", (obj, _, _) => ((WebMapConfig)obj).ShowWaypoints = true),
            [6] = Step(6, "scheduled terrain-export defaults", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.ScheduledTerrainExportEnabled = false;
                cfg.ScheduledTerrainExportTime = "04:00";
                cfg.ScheduledTerrainExportOnlyWhenEmpty = true;
            }),
            [7] = Step(7, "remove recurring runtime terrain exports", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.ExportTerrainOnStart = false;
                cfg.ScheduledTerrainExportEnabled = false;
            }),
            [8] = Step(8, "incremental startup terrain export", (obj, _, _) => ((WebMapConfig)obj).UseIncrementalTerrainExport = true),
            [9] = Step(9, "automatic startup export policy", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.AutomaticStartupExportEnabled = false;
                cfg.AutomaticStartupExportIntervalDays = 7;
                cfg.LastSuccessfulTerrainExportUnixMilliseconds = 0;
            }),
            [10] = Step(10, "direct save-database terrain reader and pyramid workers", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.UseDirectSaveDatabaseTerrain = true;
                cfg.TerrainPyramidWorkers = 2;
            }),
            [11] = Step(11, "parallel base-tile renderer", (obj, _, _) => ((WebMapConfig)obj).TerrainBaseRenderWorkers = 2),
            [12] = Step(12, "separate player waypoint limits", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.PlayerMajorWaypointLimit = 5;
                cfg.PlayerMinorWaypointLimit = 10;
            }),
            [13] = Step(13, "map zoom limits", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.MaximumZoomOutSpanBlocks = 20000;
                cfg.DefaultViewSpanBlocks = 8000;
            }),
            [14] = Step(14, "30k maximum zoom-out migration", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                if (cfg.MaximumZoomOutSpanBlocks == 20000) cfg.MaximumZoomOutSpanBlocks = 30000;
            }),
            [15] = Step(15, "50k maximum zoom-out migration", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                if (cfg.MaximumZoomOutSpanBlocks == 30000) cfg.MaximumZoomOutSpanBlocks = 50000;
            }),
            [16] = Step(16, "major/minor waypoint caps and export-duration state", (obj, _, _) =>
            {
                var cfg = (WebMapConfig)obj;
                cfg.PlayerMajorWaypointLimit = 5;
                cfg.PlayerMinorWaypointLimit = 10;
                cfg.LastTerrainExportDurationMilliseconds = Math.Max(0, cfg.LastTerrainExportDurationMilliseconds);
            }),
            [17] = Step(17, "terrain style export controls", null)
        };

        return new MigrationPlan
        {
            FileName = "NewfiesWebMap.json",
            CurrentVersion = WebMapConfig.CurrentSchemaVersion,
            Steps = steps
        };
    }

    private static MigrationPlan Simple(string fileName, int currentVersion) => new()
    {
        FileName = fileName,
        CurrentVersion = currentVersion
    };

    private static MigrationStep Step(
        int targetVersion,
        string description,
        Action<object, ICoreServerAPI, int>? apply) => new()
    {
        TargetVersion = targetVersion,
        Description = description,
        Apply = apply
    };

    private static bool IsAntiCheatCompanionInstalled(ICoreServerAPI api)
    {
        try
        {
            return api.ModLoader.Mods.Any(x =>
                x?.Info != null &&
                string.Equals(x.Info.ModID, AntiCheatProtocol.ClientModId, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static int ReadSchemaVersion(JObject json)
    {
        var property = json.Properties().FirstOrDefault(p =>
            string.Equals(p.Name, "SchemaVersion", StringComparison.OrdinalIgnoreCase));
        if (property?.Value == null) return 0;
        return int.TryParse(property.Value.ToString(), out int value) ? Math.Max(0, value) : 0;
    }

    private static int GetSchemaVersion(object config, int fallback)
    {
        try
        {
            PropertyInfo? property = config.GetType().GetProperty("SchemaVersion", BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(config) is int value) return value;
        }
        catch { }
        return fallback;
    }

    private static void SetSchemaVersion(object config, int value)
    {
        PropertyInfo? property = config.GetType().GetProperty("SchemaVersion", BindingFlags.Instance | BindingFlags.Public);
        if (property == null || property.PropertyType != typeof(int) || !property.CanWrite)
            throw new InvalidOperationException($"{config.GetType().Name} does not expose a writable int SchemaVersion property.");
        property.SetValue(config, value);
    }

    private static string? FindConfigPath(ICoreServerAPI api, string fileName)
    {
        string centralized = SuiteConfigStorage.GetConfigPath(api, fileName);
        if (File.Exists(centralized)) return centralized;

        // Defensive fallback for a load path invoked before startup relocation. Normal 1.2.22+
        // startup centralizes these files before any subsystem reads its configuration.
        return SuiteConfigStorage.FindLegacyRootFile(api, fileName);
    }

    private static string CreatePreMigrationBackup(
        ICoreServerAPI api,
        string source,
        string fileName,
        int sourceVersion,
        int targetVersion)
    {
        try
        {
            string directory = SuiteConfigStorage.GetBackupRoot(api);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string backup = Path.Combine(
                directory,
                $"{stem}.pre-migration-v{sourceVersion}-to-v{targetVersion}-{stamp}{extension}");

            File.Copy(source, backup, overwrite: false);
            TrimOldBackups(directory, stem, extension);
            return backup;
        }
        catch (Exception ex)
        {
            string message =
                $"Could not create the required pre-migration backup for {fileName}: {ex.GetType().Name}: {ex.Message}. " +
                "Migration was aborted and the original config was left untouched.";
            api.Logger.Error("[ServerEssentials/ConfigMigration] {0}", message);
            throw new ConfigMigrationException(fileName, message, ex);
        }
    }

    private static void TrimOldBackups(string directory, string stem, string extension)
    {
        try
        {
            var files = Directory.GetFiles(directory, $"{stem}.pre-migration-v*-to-v*-*{extension}")
                .Select(path => new FileInfo(path))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ToList();

            foreach (var old in files.Skip(BackupRetentionPerConfig))
            {
                try { old.Delete(); } catch { }
            }
        }
        catch { }
    }
}
