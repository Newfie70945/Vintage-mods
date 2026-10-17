using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace BlockLogger.Services;

// JSONL remains the durable authority. This rebuildable disk index retains all
// uncertain intents without keeping every historical receipt in managed memory.
internal sealed class RecoveryReceiptIndex
{
    private readonly string journalPath, indexPath;
    private readonly SemaphoreSlim gate = new(1, 1);
    public RecoveryReceiptIndex(string journalPath) { this.journalPath = journalPath; indexPath = journalPath + ".index.sqlite"; }
    internal Task InitializeAsync(CancellationToken cancellation = default) => FindAsync(Array.Empty<string>(), cancellation);
    internal async Task<HashSet<string>> FindAsync(IEnumerable<string> requested, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await Task.Run(() => Find(requested, cancellation), cancellation).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private HashSet<string> Find(IEnumerable<string> requested, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = indexPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        db.Open();
        using var schema = db.CreateCommand();
        schema.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS receipts (key TEXT PRIMARY KEY); CREATE TABLE IF NOT EXISTS checkpoint (id INTEGER PRIMARY KEY, offset INTEGER NOT NULL, stamp INTEGER NOT NULL, anchor TEXT NOT NULL);";
        schema.ExecuteNonQuery();
        long offset = 0, stamp = 0; string anchor = "";
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT offset, stamp, anchor FROM checkpoint WHERE id=1";
            using var row = read.ExecuteReader();
            if (row.Read()) { offset = row.GetInt64(0); stamp = row.GetInt64(1); anchor = row.GetString(2); }
        }
        long length = File.Exists(journalPath) ? new FileInfo(journalPath).Length : 0;
        long currentStamp = File.Exists(journalPath) ? File.GetLastWriteTimeUtc(journalPath).Ticks : 0;
        using var transaction = db.BeginTransaction();
        // Never erase known uncertain intents when the authoritative journal disappears or
        // changes. A repair must preserve the journal; the index itself is rebuildable.
        if (offset > 0 && (length < offset ||
            (length == offset && currentStamp != stamp) || ContentAnchor(offset) != anchor))
            throw new InvalidDataException("Recovery receipt journal changed or was truncated; preserve journal/index and restore consistent history before recovery.");
        if (length > offset)
        {
            using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.Position = offset;
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            using var insert = db.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO receipts(key) VALUES($key)";
            var parameter = insert.Parameters.Add("$key", SqliteType.Text);
            string? line;
            int rows = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if (++rows % 256 == 0) cancellation.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var record = JsonDocument.Parse(line);
                string? key = record.RootElement.TryGetProperty("key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String ? keyValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Recovery receipt without key; preserve the journal before repair.");
                parameter.Value = key; insert.ExecuteNonQuery();
            }
        }
        using (var save = db.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = "INSERT OR REPLACE INTO checkpoint(id,offset,stamp,anchor) VALUES(1,$offset,$stamp,$anchor)";
            save.Parameters.AddWithValue("$offset", length); save.Parameters.AddWithValue("$stamp", currentStamp); save.Parameters.AddWithValue("$anchor", length > 0 ? ContentAnchor(length) : ""); save.ExecuteNonQuery();
        }
        cancellation.ThrowIfCancellationRequested();
        transaction.Commit();
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var lookup = db.CreateCommand(); lookup.CommandText = "SELECT 1 FROM receipts WHERE key=$key";
        var keyParameter = lookup.Parameters.Add("$key", SqliteType.Text);
        int checkedKeys = 0;
        foreach (string key in requested.Distinct(StringComparer.Ordinal))
        { if (++checkedKeys % 256 == 0) cancellation.ThrowIfCancellationRequested(); keyParameter.Value = key; if (lookup.ExecuteScalar() != null) result.Add(key); }
        return result;
    }
    private string ContentAnchor(long length)
    {
        using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[(int)Math.Min(65536, length)];
        stream.ReadExactly(buffer); hash.AppendData(buffer);
        if (length > buffer.Length)
        { stream.Position = Math.Max(buffer.Length, length - buffer.Length); int tail = (int)(length - stream.Position); stream.ReadExactly(buffer.AsSpan(0, tail)); hash.AppendData(buffer.AsSpan(0, tail)); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

}
