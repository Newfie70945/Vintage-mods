using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BlockLogger.AnimalLimiter;
using BlockLogger.AntiCheat;
using BlockLogger.Config;
using BlockLogger.WebMap;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public static class ConfigMigrationService
{
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

		public Dictionary<int, MigrationStep> Steps { get; init; } = new Dictionary<int, MigrationStep>();

		public Action<object, ICoreServerAPI, int>? WholeMigration { get; init; }
	}

	public readonly record struct MigrationProbe(bool Registered, bool FileExists, string FileName, string Subsystem, string SourcePath, int SourceVersion, int TargetVersion, string BackupPath, bool NeedsMigration, bool FutureSchema);

	private const int BackupRetentionPerConfig = 5;

	private static readonly IReadOnlyDictionary<string, MigrationPlan> Plans = BuildPlans();

	public static MigrationProbe Prepare(ICoreServerAPI api, string fileName, string subsystem)
	{
		if (!Plans.TryGetValue(fileName, out MigrationPlan value))
		{
			return default;
		}
		string text = FindConfigPath(api, fileName);
		if (text == null)
		{
			return new MigrationProbe(Registered: true, FileExists: false, fileName, subsystem, "", value.CurrentVersion, value.CurrentVersion, "", NeedsMigration: false, FutureSchema: false);
		}
		int num;
		try
		{
			string json = File.ReadAllText(text);
			JObject json2 = JObject.Parse(json);
			num = ReadSchemaVersion(json2);
		}
		catch
		{
			return new MigrationProbe(Registered: true, FileExists: true, fileName, subsystem, text, value.CurrentVersion, value.CurrentVersion, "", NeedsMigration: false, FutureSchema: false);
		}
		if (num > value.CurrentVersion)
		{
			string text2 = $"{fileName} uses future schema {num}; this build only knows schema {value.CurrentVersion}. " + "The file has been left untouched. Install a Suite version that supports this schema before editing it.";
			api.Logger.Error("[ServerEssentials/ConfigMigration] {0}", text2);
			throw new ConfigMigrationException(fileName, text2);
		}
		if (num >= value.CurrentVersion)
		{
			return new MigrationProbe(Registered: true, FileExists: true, fileName, subsystem, text, num, value.CurrentVersion, "", NeedsMigration: false, FutureSchema: false);
		}
		string text3 = CreatePreMigrationBackup(api, text, fileName, num, value.CurrentVersion);
		api.Logger.Notification("[ServerEssentials/ConfigMigration] {0}: preparing schema {1} -> {2}. Backup: {3}", fileName, num, value.CurrentVersion, text3);
		return new MigrationProbe(Registered: true, FileExists: true, fileName, subsystem, text, num, value.CurrentVersion, text3, NeedsMigration: true, FutureSchema: false);
	}

	public static void ApplyIfNeeded(ICoreServerAPI api, object config, MigrationProbe probe)
	{
		if (!probe.Registered || !probe.FileExists || !probe.NeedsMigration || probe.FutureSchema || !Plans.TryGetValue(probe.FileName, out MigrationPlan value))
		{
			return;
		}
		try
		{
			int num = probe.SourceVersion;
			if (value.WholeMigration != null)
			{
				value.WholeMigration(config, api, probe.SourceVersion);
				num = GetSchemaVersion(config, num);
			}
			if (num < value.CurrentVersion)
			{
				for (int i = num + 1; i <= value.CurrentVersion; i++)
				{
					if (value.Steps.TryGetValue(i, out MigrationStep value2))
					{
						value2.Apply?.Invoke(config, api, probe.SourceVersion);
						api.Logger.Notification("[ServerEssentials/ConfigMigration] {0}: applied schema {1} -> {2} ({3}).", probe.FileName, i - 1, i, value2.Description);
					}
					SetSchemaVersion(config, i);
				}
			}
			try
			{
				SuiteConfigStorage.Store(api, config, probe.FileName);
			}
			catch (Exception ex)
			{
				string text = $"{probe.FileName}: migration reached schema {GetSchemaVersion(config, probe.SourceVersion)}, but saving failed: {ex.GetType().Name}: {ex.Message}. Original backup remains at {probe.BackupPath}";
				api.Logger.Error("[ServerEssentials/ConfigMigration] {0}", text);
				throw new ConfigMigrationException(probe.FileName, text, ex);
			}
			api.Logger.Notification("[ServerEssentials/ConfigMigration] {0}: migration complete at schema {1}. Pre-migration backup: {2}", probe.FileName, GetSchemaVersion(config, value.CurrentVersion), probe.BackupPath);
		}
		catch (ConfigMigrationException)
		{
			throw;
		}
		catch (Exception ex3)
		{
			string text2 = $"{probe.FileName}: migration failed before the migrated config was committed. {ex3.GetType().Name}: {ex3.Message}. Original backup: {probe.BackupPath}";
			api.Logger.Error("[ServerEssentials/ConfigMigration] {0}", text2);
			throw new ConfigMigrationException(probe.FileName, text2, ex3);
		}
	}

	public static bool IsRegistered(string fileName)
	{
		return Plans.ContainsKey(fileName);
	}

	public static int CurrentSchema(string fileName)
	{
		MigrationPlan value;
		return Plans.TryGetValue(fileName, out value) ? value.CurrentVersion : 0;
	}

	private static IReadOnlyDictionary<string, MigrationPlan> BuildPlans()
	{
		Dictionary<string, MigrationPlan> dictionary = new Dictionary<string, MigrationPlan>(StringComparer.OrdinalIgnoreCase)
		{
			["NewfiesBlocklogger.json"] = new MigrationPlan
			{
				FileName = "NewfiesBlocklogger.json",
				CurrentVersion = 3,
				Steps = new Dictionary<int, MigrationStep> { [3] = new MigrationStep
				{
					TargetVersion = 3,
					Description = "full-fidelity entity lifecycle audit defaults",
					Apply = (object obj, ICoreServerAPI _, int _) =>
					{
						BlockLoggerConfig blockLoggerConfig = (BlockLoggerConfig)obj;
						blockLoggerConfig.LogEntityDespawn = true;
						blockLoggerConfig.AggregateEntitySpawnDespawn = false;
						blockLoggerConfig.AdaptiveEntityAggregation = false;
						blockLoggerConfig.BulkEntityPositionSampleIntervalMs = 0;
					}
				} }
			},
			["NewfiesServerEssentialsFeatures.json"] = new MigrationPlan
			{
				FileName = "NewfiesServerEssentialsFeatures.json",
				CurrentVersion = 2,
				Steps = new Dictionary<int, MigrationStep> { [2] = new MigrationStep
				{
					TargetVersion = 2,
					Description = "integrated AFK subsystem feature gate",
					Apply = (object obj, ICoreServerAPI _, int _) =>
					{
						((SuiteFeatureConfig)obj).Afk = true;
					}
				} }
			},
			["NewfiesAnimalLimiter.json"] = new MigrationPlan
			{
				FileName = "NewfiesAnimalLimiter.json",
				CurrentVersion = 2,
				Steps = new Dictionary<int, MigrationStep> { [2] = new MigrationStep
				{
					TargetVersion = 2,
					Description = "explicit enable gate and per-rule radius support",
					Apply = (object obj, ICoreServerAPI _, int _) =>
					{
						AnimalLimiterConfig animalLimiterConfig = (AnimalLimiterConfig)obj;
						if (!animalLimiterConfig.Enabled.HasValue)
						{
							animalLimiterConfig.Enabled = true;
						}
					}
				} }
			},
			["NewfiesLoginProtection.json"] = Simple("NewfiesLoginProtection.json", 2),
			["NewfiesAntiCheat.json"] = new MigrationPlan
			{
				FileName = "NewfiesAntiCheat.json",
				CurrentVersion = 62,
				WholeMigration = (object obj, ICoreServerAPI api, int sourceVersion) =>
				{
					AntiCheatConfig antiCheatConfig = (AntiCheatConfig)obj;
					if (sourceVersion < 14 && IsAntiCheatCompanionInstalled(api))
					{
						antiCheatConfig.Enabled = true;
					}
					antiCheatConfig.Clamp();
				}
			},
			["NewfiesAutomatedRestarts.json"] = Simple("NewfiesAutomatedRestarts.json", 1),
			["NewfiesChatDisplay.json"] = new MigrationPlan
			{
				FileName = "NewfiesChatDisplay.json",
				CurrentVersion = 4,
				Steps = new Dictionary<int, MigrationStep> { [2] = new MigrationStep
				{
					TargetVersion = 2,
					Description = "scope formatting to General chat by default",
					Apply = (object obj, ICoreServerAPI _, int _) =>
					{
						ChatDisplayConfig chatDisplayConfig = (ChatDisplayConfig)obj;
						chatDisplayConfig.FormatGeneralChat = true;
						chatDisplayConfig.FormatOtherChatGroups = false;
						chatDisplayConfig.AdditionalFormattedChatGroupIds = new List<int>();
					}
				} }
			},
			["NewfiesPlayerCommands.json"] = Simple("NewfiesPlayerCommands.json", 4),
			["NewfiesAfk.json"] = Simple("NewfiesAfk.json", 2),
			["NewfiesStarterKit.json"] = Simple("NewfiesStarterKit.json", 1),
			["NewfiesWarps.json"] = Simple("NewfiesWarps.json", 2),
			["NewfiesPlayerList.json"] = Simple("NewfiesPlayerList.json", 6),
			["NewfiesWebServer.json"] = Simple("NewfiesWebServer.json", 2),
			["NewfiesWebAdminAccounts.json"] = Simple("NewfiesWebAdminAccounts.json", 1),
			["NewfiesCompatibilityIgnores.json"] = Simple("NewfiesCompatibilityIgnores.json", 1),
			["NewfiesWebMap.json"] = BuildWebMapPlan(),
			["NewfiesAnnouncements.json"] = Simple("NewfiesAnnouncements.json", 1)
		};
		dictionary["blocklogger-v2.json"] = dictionary["NewfiesBlocklogger.json"];
		dictionary["loginprotection.json"] = dictionary["NewfiesLoginProtection.json"];
		return dictionary;
	}

	private static MigrationPlan BuildWebMapPlan()
	{
		Dictionary<int, MigrationStep> steps = new Dictionary<int, MigrationStep>
		{
			[2] = Step(2, "public-map privacy defaults", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.ShowPlayers = false;
				webMapConfig.ShowBadBlockEntities = false;
				webMapConfig.PublicUrl = "";
				webMapConfig.DashboardUrl = "";
			}),
			[3] = Step(3, "terrain-export safety defaults", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.ShowActivity = false;
				webMapConfig.ShowBadBlockEntities = false;
				webMapConfig.ExportTerrainOnStart = true;
				webMapConfig.LockServerDuringTerrainExport = true;
				webMapConfig.TerrainTileSize = 256;
			}),
			[4] = Step(4, "live-player coordinate privacy gate", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.AllowLivePlayerData = false;
				webMapConfig.ShowPlayers = false;
			}),
			[5] = Step(5, "admin waypoint layer", (object obj, ICoreServerAPI _, int _) =>
			{
				((WebMapConfig)obj).ShowWaypoints = true;
			}),
			[6] = Step(6, "scheduled terrain-export defaults", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.ScheduledTerrainExportEnabled = false;
				webMapConfig.ScheduledTerrainExportTime = "04:00";
				webMapConfig.ScheduledTerrainExportOnlyWhenEmpty = true;
			}),
			[7] = Step(7, "remove recurring runtime terrain exports", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.ExportTerrainOnStart = false;
				webMapConfig.ScheduledTerrainExportEnabled = false;
			}),
			[8] = Step(8, "incremental startup terrain export", (object obj, ICoreServerAPI _, int _) =>
			{
				((WebMapConfig)obj).UseIncrementalTerrainExport = true;
			}),
			[9] = Step(9, "automatic startup export policy", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.AutomaticStartupExportEnabled = false;
				webMapConfig.AutomaticStartupExportIntervalDays = 7;
				webMapConfig.LastSuccessfulTerrainExportUnixMilliseconds = 0L;
			}),
			[10] = Step(10, "direct save-database terrain reader and pyramid workers", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.UseDirectSaveDatabaseTerrain = true;
				webMapConfig.TerrainPyramidWorkers = 2;
			}),
			[11] = Step(11, "parallel base-tile renderer", (object obj, ICoreServerAPI _, int _) =>
			{
				((WebMapConfig)obj).TerrainBaseRenderWorkers = 2;
			}),
			[12] = Step(12, "separate player waypoint limits", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.PlayerMajorWaypointLimit = 5;
				webMapConfig.PlayerMinorWaypointLimit = 10;
			}),
			[13] = Step(13, "map zoom limits", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.MaximumZoomOutSpanBlocks = 20000;
				webMapConfig.DefaultViewSpanBlocks = 8000;
			}),
			[14] = Step(14, "30k maximum zoom-out migration", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				if (webMapConfig.MaximumZoomOutSpanBlocks == 20000)
				{
					webMapConfig.MaximumZoomOutSpanBlocks = 30000;
				}
			}),
			[15] = Step(15, "50k maximum zoom-out migration", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				if (webMapConfig.MaximumZoomOutSpanBlocks == 30000)
				{
					webMapConfig.MaximumZoomOutSpanBlocks = 50000;
				}
			}),
			[16] = Step(16, "major/minor waypoint caps and export-duration state", (object obj, ICoreServerAPI _, int _) =>
			{
				WebMapConfig webMapConfig = (WebMapConfig)obj;
				webMapConfig.PlayerMajorWaypointLimit = 5;
				webMapConfig.PlayerMinorWaypointLimit = 10;
				webMapConfig.LastTerrainExportDurationMilliseconds = Math.Max(0L, webMapConfig.LastTerrainExportDurationMilliseconds);
			}),
			[17] = Step(17, "terrain style export controls", null)
		};
		return new MigrationPlan
		{
			FileName = "NewfiesWebMap.json",
			CurrentVersion = 19,
			Steps = steps
		};
	}

	private static MigrationPlan Simple(string fileName, int currentVersion)
	{
		return new MigrationPlan
		{
			FileName = fileName,
			CurrentVersion = currentVersion
		};
	}

	private static MigrationStep Step(int targetVersion, string description, Action<object, ICoreServerAPI, int>? apply)
	{
		return new MigrationStep
		{
			TargetVersion = targetVersion,
			Description = description,
			Apply = apply
		};
	}

	private static bool IsAntiCheatCompanionInstalled(ICoreServerAPI api)
	{
		try
		{
			return api.ModLoader.Mods.Any((Mod x) => x?.Info != null && string.Equals(x.Info.ModID, "newfiesanticheatclient", StringComparison.OrdinalIgnoreCase));
		}
		catch
		{
			return false;
		}
	}

	private static int ReadSchemaVersion(JObject json)
	{
		JProperty jProperty = json.Properties().FirstOrDefault((JProperty p) => string.Equals(p.Name, "SchemaVersion", StringComparison.OrdinalIgnoreCase));
		if (jProperty?.Value == null)
		{
			return 0;
		}
		int result;
		return int.TryParse(jProperty.Value.ToString(), out result) ? Math.Max(0, result) : 0;
	}

	private static int GetSchemaVersion(object config, int fallback)
	{
		try
		{
			if (config.GetType().GetProperty("SchemaVersion", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config) is int result)
			{
				return result;
			}
		}
		catch
		{
		}
		return fallback;
	}

	private static void SetSchemaVersion(object config, int value)
	{
		PropertyInfo property = config.GetType().GetProperty("SchemaVersion", BindingFlags.Instance | BindingFlags.Public);
		if (property == null || property.PropertyType != typeof(int) || !property.CanWrite)
		{
			throw new InvalidOperationException(config.GetType().Name + " does not expose a writable int SchemaVersion property.");
		}
		property.SetValue(config, value);
	}

	private static string? FindConfigPath(ICoreServerAPI api, string fileName)
	{
		string configPath = SuiteConfigStorage.GetConfigPath(api, fileName);
		if (File.Exists(configPath))
		{
			return configPath;
		}
		return SuiteConfigStorage.FindLegacyRootFile(api, fileName);
	}

	private static string CreatePreMigrationBackup(ICoreServerAPI api, string source, string fileName, int sourceVersion, int targetVersion)
	{
		try
		{
			string backupRoot = SuiteConfigStorage.GetBackupRoot(api);
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
			string extension = Path.GetExtension(fileName);
			string value = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
			string text = Path.Combine(backupRoot, $"{fileNameWithoutExtension}.pre-migration-v{sourceVersion}-to-v{targetVersion}-{value}{extension}");
			File.Copy(source, text, overwrite: false);
			TrimOldBackups(backupRoot, fileNameWithoutExtension, extension);
			return text;
		}
		catch (Exception ex)
		{
			string text2 = $"Could not create the required pre-migration backup for {fileName}: {ex.GetType().Name}: {ex.Message}. " + "Migration was aborted and the original config was left untouched.";
			api.Logger.Error("[ServerEssentials/ConfigMigration] {0}", text2);
			throw new ConfigMigrationException(fileName, text2, ex);
		}
	}

	private static void TrimOldBackups(string directory, string stem, string extension)
	{
		try
		{
			List<FileInfo> source = (from path in Directory.GetFiles(directory, stem + ".pre-migration-v*-to-v*-*" + extension)
				select new FileInfo(path) into x
				orderby x.LastWriteTimeUtc descending
				select x).ToList();
			foreach (FileInfo item in source.Skip(5))
			{
				try
				{
					item.Delete();
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}
}
