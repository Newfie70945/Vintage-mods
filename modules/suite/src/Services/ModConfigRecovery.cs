using System;
using System.IO;
using System.Linq;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public static class ModConfigRecovery
{
    public static T? LoadOrNull<T>(
        ICoreServerAPI api,
        string filename,
        string subsystem)
        where T : class
    {
        var migration = ConfigMigrationService.Prepare(api, filename, subsystem);

        try
        {
            var loaded = BlockLogger.Services.SuiteConfigStorage.Load<T>(api, filename);
            if (loaded != null)
                ConfigMigrationService.ApplyIfNeeded(api, loaded, migration);
            return loaded;
        }
        catch (ConfigMigrationException)
        {
            // Migration failures are not corruption. Fail this subsystem load safely so the
            // caller cannot overwrite the original config with defaults or a partial migration.
            throw;
        }
        catch (Exception ex)
        {
            string backup = BackupCorrupt(api, filename);

            api.Logger.Error(
                "[{0}] Invalid/corrupt config {1}: {2}: {3}. Backup: {4}",
                subsystem,
                filename,
                ex.GetType().Name,
                ex.Message,
                backup);

            return null;
        }
    }

    public static string BackupCorrupt(
        ICoreServerAPI api,
        string filename)
    {
        try
        {
            string source = SuiteConfigStorage.GetConfigPath(api, filename);
            if (!File.Exists(source))
            {
                source = SuiteConfigStorage.FindLegacyRootFile(api, filename) ?? "";
            }

            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                return "(source file not found; defaults will be regenerated)";

            string stem = Path.GetFileNameWithoutExtension(filename);
            string extension = Path.GetExtension(filename);
            string target = Path.Combine(
                SuiteConfigStorage.GetBackupRoot(api),
                $"{stem}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}");

            File.Move(source, target);
            return target;
        }
        catch (Exception ex)
        {
            return
                $"(backup failed: {ex.GetType().Name}: {ex.Message})";
        }
    }
}
