using System.Text;
using System.Text.Json;
using NewfiesServerEssentials.Discord.Linking.Models;
using Vintagestory.API.Server;

namespace NewfiesServerEssentials.Discord.Linking.Services;

public sealed class DiscordLinkStore
{
    public const string SaveDataKey = "newfiesdiscordlink:database-v1";

    private readonly ICoreServerAPI sapi;
    private readonly object gate = new();
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = false };
    private DiscordLinkDatabase database = new();
    private Dictionary<string, HashSet<string>> playerUidsByDiscordUserId = new(StringComparer.Ordinal);
    private bool storageReady;

    public bool IsStorageReady { get { lock (gate) return storageReady; } }

    public DiscordLinkStore(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
    }

    public void Load()
    {
        lock (gate)
        {
            storageReady = false;
            try
            {
                var bytes = sapi.WorldManager.SaveGame.GetData(SaveDataKey);
                if (bytes == null || bytes.Length == 0)
                {
                    database = new DiscordLinkDatabase();
                    playerUidsByDiscordUserId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                    storageReady = true;
                    return;
                }

                var json = Encoding.UTF8.GetString(bytes);
                var loaded = JsonSerializer.Deserialize<DiscordLinkDatabase>(json, jsonOptions)
                    ?? throw new InvalidDataException("Link database was null.");
                if (loaded.SchemaVersion != 1 || loaded.LinksByPlayerUid == null ||
                    loaded.LinksByPlayerUid.Any(x => x.Value == null || string.IsNullOrWhiteSpace(x.Key) || x.Key != x.Value.PlayerUid ||
                        !ulong.TryParse(x.Value.DiscordUserId, out ulong id) || id == 0))
                    throw new InvalidDataException("Link database schema or account records are invalid.");

                // Rebuild with a stable ordinal comparer after JSON deserialization.
                loaded.LinksByPlayerUid = new Dictionary<string, DiscordLinkRecord>(loaded.LinksByPlayerUid, StringComparer.Ordinal);
                database = loaded;
                RebuildDiscordIndexLocked();
                storageReady = true;
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[NSE Discord] Failed to load link database; account linking writes are disabled to preserve existing save data. Repair the data and restart before linking accounts. {0}", e);
            }
        }
    }

    public DiscordLinkRecord? GetByPlayerUid(string playerUid)
    {
        lock (gate)
        {
            return database.LinksByPlayerUid.TryGetValue(playerUid, out var record) ? Clone(record) : null;
        }
    }

    public IReadOnlyList<DiscordLinkRecord> GetByDiscordUserId(string discordUserId)
    {
        lock (gate)
        {
            if (!playerUidsByDiscordUserId.TryGetValue(discordUserId, out var playerUids) || playerUids.Count == 0)
                return Array.Empty<DiscordLinkRecord>();

            var result = new List<DiscordLinkRecord>(playerUids.Count);
            foreach (string playerUid in playerUids)
            {
                if (database.LinksByPlayerUid.TryGetValue(playerUid, out var record))
                    result.Add(Clone(record));
            }
            return result;
        }
    }

    public IReadOnlyList<DiscordLinkRecord> GetAll()
    {
        lock (gate)
        {
            return database.LinksByPlayerUid.Values.Select(Clone).ToArray();
        }
    }

    public void Upsert(DiscordLinkRecord record)
    {
        lock (gate)
        {
            EnsureWritableLocked();
            if (string.IsNullOrWhiteSpace(record.PlayerUid) || !ulong.TryParse(record.DiscordUserId, out ulong id) || id == 0)
                throw new ArgumentException("A valid player UID and Discord account ID are required.", nameof(record));
            if (database.LinksByPlayerUid.TryGetValue(record.PlayerUid, out var previous))
                RemoveFromDiscordIndexLocked(previous.DiscordUserId, previous.PlayerUid);

            database.LinksByPlayerUid[record.PlayerUid] = Clone(record);
            AddToDiscordIndexLocked(record.DiscordUserId, record.PlayerUid);
            try { SaveLocked(); }
            catch
            {
                if (previous == null) database.LinksByPlayerUid.Remove(record.PlayerUid);
                else database.LinksByPlayerUid[record.PlayerUid] = previous;
                RebuildDiscordIndexLocked();
                throw;
            }
        }
    }

    public DiscordLinkRecord? RemoveByPlayerUid(string playerUid)
    {
        lock (gate)
        {
            EnsureWritableLocked();
            if (!database.LinksByPlayerUid.Remove(playerUid, out var removed)) return null;
            RemoveFromDiscordIndexLocked(removed.DiscordUserId, playerUid);
            try { SaveLocked(); }
            catch
            {
                database.LinksByPlayerUid[playerUid] = removed;
                RebuildDiscordIndexLocked();
                throw;
            }
            return Clone(removed);
        }
    }

    public IReadOnlyList<DiscordLinkRecord> RemoveByDiscordUserId(string discordUserId)
    {
        lock (gate)
        {
            EnsureWritableLocked();
            if (!playerUidsByDiscordUserId.TryGetValue(discordUserId, out var playerUids) || playerUids.Count == 0)
                return Array.Empty<DiscordLinkRecord>();

            var removed = new List<DiscordLinkRecord>(playerUids.Count);
            foreach (string playerUid in playerUids.ToArray())
            {
                if (database.LinksByPlayerUid.Remove(playerUid, out var record))
                    removed.Add(Clone(record));
            }

            playerUidsByDiscordUserId.Remove(discordUserId);
            if (removed.Count > 0)
            {
                try { SaveLocked(); }
                catch
                {
                    foreach (var record in removed) database.LinksByPlayerUid[record.PlayerUid] = record;
                    RebuildDiscordIndexLocked();
                    throw;
                }
            }
            return removed;
        }
    }

    private void RebuildDiscordIndexLocked()
    {
        playerUidsByDiscordUserId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var record in database.LinksByPlayerUid.Values)
            AddToDiscordIndexLocked(record.DiscordUserId, record.PlayerUid);
    }

    private void AddToDiscordIndexLocked(string discordUserId, string playerUid)
    {
        if (string.IsNullOrWhiteSpace(discordUserId) || string.IsNullOrWhiteSpace(playerUid)) return;
        if (!playerUidsByDiscordUserId.TryGetValue(discordUserId, out var playerUids))
        {
            playerUids = new HashSet<string>(StringComparer.Ordinal);
            playerUidsByDiscordUserId[discordUserId] = playerUids;
        }
        playerUids.Add(playerUid);
    }

    private void RemoveFromDiscordIndexLocked(string discordUserId, string playerUid)
    {
        if (!playerUidsByDiscordUserId.TryGetValue(discordUserId, out var playerUids)) return;
        playerUids.Remove(playerUid);
        if (playerUids.Count == 0) playerUidsByDiscordUserId.Remove(discordUserId);
    }

    private void SaveLocked()
    {
        var json = JsonSerializer.Serialize(database, jsonOptions);
        sapi.WorldManager.SaveGame.StoreData(SaveDataKey, Encoding.UTF8.GetBytes(json));
    }

    private void EnsureWritableLocked()
    {
        if (!storageReady) throw new InvalidOperationException("Discord account storage could not be read. Linking changes are disabled to preserve the existing data.");
    }

    private static DiscordLinkRecord Clone(DiscordLinkRecord r) => new()
    {
        PlayerUid = r.PlayerUid,
        PlayerName = r.PlayerName,
        DiscordUserId = r.DiscordUserId,
        DiscordUserName = r.DiscordUserName,
        LinkedAtUnixMilliseconds = r.LinkedAtUnixMilliseconds
    };
}
