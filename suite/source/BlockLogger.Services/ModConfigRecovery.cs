using System;
using System.IO;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public static class ModConfigRecovery
{
	public static T? LoadOrNull<T>(ICoreServerAPI api, string filename, string subsystem) where T : class
	{
		ConfigMigrationService.MigrationProbe probe = ConfigMigrationService.Prepare(api, filename, subsystem);
		try
		{
			T val = SuiteConfigStorage.Load<T>(api, filename);
			if (val != null)
			{
				ConfigMigrationService.ApplyIfNeeded(api, val, probe);
			}
			return val;
		}
		catch (ConfigMigrationException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			string text = BackupCorrupt(api, filename);
			api.Logger.Error("[{0}] Invalid/corrupt config {1}: {2}: {3}. Backup: {4}", subsystem, filename, ex2.GetType().Name, ex2.Message, text);
			return null;
		}
	}

	public static string BackupCorrupt(ICoreServerAPI api, string filename)
	{
		try
		{
			string text = SuiteConfigStorage.GetConfigPath(api, filename);
			if (!File.Exists(text))
			{
				text = SuiteConfigStorage.FindLegacyRootFile(api, filename) ?? "";
			}
			if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
			{
				return "(source file not found; defaults will be regenerated)";
			}
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
			string extension = Path.GetExtension(filename);
			string text2 = Path.Combine(SuiteConfigStorage.GetBackupRoot(api), $"{fileNameWithoutExtension}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}");
			File.Move(text, text2);
			return text2;
		}
		catch (Exception ex)
		{
			return $"(backup failed: {ex.GetType().Name}: {ex.Message})";
		}
	}
}
