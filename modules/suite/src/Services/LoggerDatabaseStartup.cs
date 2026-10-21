using Microsoft.Data.Sqlite;

namespace BlockLogger.Services;

// Startup validation is read-only. Failure never renames, deletes or restores
// a database: BUSY, access failures and I/O errors are not corruption evidence.
internal static class LoggerDatabaseStartup
{
    internal static bool DeferAutomaticBackup(bool running, TimeSpan elapsed) =>
        !running || elapsed < TimeSpan.FromMinutes(2);

    internal static string Probe(string path, bool fullCheck)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name IN ('events','snapshots','entity_activity');";
        var tables = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        foreach (string table in tables)
        {
            command.CommandText = "SELECT rowid FROM " + table + " NOT INDEXED LIMIT 1;";
            command.ExecuteScalar();
        }
        if (!fullCheck)
            return "Lightweight startup probe passed; full integrity not scanned. Use manual integrity maintenance for a complete check.";

        command.CommandText = "PRAGMA quick_check(1);";
        string result = Convert.ToString(command.ExecuteScalar()) ?? "unknown";
        if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Full startup integrity scan failed: " + result + ". Database preserved; manual investigation required.");
        return "ok";
    }
}
