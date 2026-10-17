using System.Text;
using System.Text.Json;
using NewfiesServerEssentials.Discord.Linking.Models;
using Vintagestory.API.Server;

namespace NewfiesServerEssentials.Discord.Linking.Services;

public sealed class DiscordRoleSyncStore
{
    public const string SaveDataKey = "newfiesdiscordlink:rolesync-v1";

    private readonly ICoreServerAPI sapi;
    private readonly object gate = new();
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = false };
    private DiscordRoleSyncDatabase database = new();
    private bool storageReady;

    public bool IsStorageReady { get { lock (gate) return storageReady; } }

    public DiscordRoleSyncStore(ICoreServerAPI sapi)
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
                    database = new DiscordRoleSyncDatabase();
                    storageReady = true;
                    return;
                }

                var loaded = JsonSerializer.Deserialize<DiscordRoleSyncDatabase>(Encoding.UTF8.GetString(bytes), jsonOptions)
                    ?? throw new InvalidDataException("Role ownership database was null.");
                if (loaded.SchemaVersion != 1 || loaded.StatesByPlayerUid == null ||
                    loaded.StatesByPlayerUid.Any(x => x.Value == null || string.IsNullOrWhiteSpace(x.Key) || x.Key != x.Value.PlayerUid))
                    throw new InvalidDataException("Role ownership database schema or player records are invalid.");
                loaded.StatesByPlayerUid = new Dictionary<string, DiscordRoleSyncState>(loaded.StatesByPlayerUid, StringComparer.Ordinal);
                database = loaded;
                storageReady = true;
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[NSE Discord] Failed to load Discord role-sync state; role changes are disabled to preserve existing ownership data. Repair the data and restart before synchronizing roles. {0}", e);
            }
        }
    }

    public DiscordRoleSyncState? Get(string playerUid)
    {
        lock (gate)
        {
            return database.StatesByPlayerUid.TryGetValue(playerUid, out var state) ? Clone(state) : null;
        }
    }

    public IReadOnlyList<DiscordRoleSyncState> GetAll()
    {
        lock (gate)
        {
            return database.StatesByPlayerUid.Values.Select(Clone).ToArray();
        }
    }

    public void Upsert(DiscordRoleSyncState state)
    {
        lock (gate)
        {
            EnsureWritableLocked();
            if (string.IsNullOrWhiteSpace(state.PlayerUid)) throw new ArgumentException("A player UID is required.", nameof(state));
            database.StatesByPlayerUid.TryGetValue(state.PlayerUid, out var previous);
            database.StatesByPlayerUid[state.PlayerUid] = Clone(state);
            try { SaveLocked(); }
            catch
            {
                if (previous == null) database.StatesByPlayerUid.Remove(state.PlayerUid);
                else database.StatesByPlayerUid[state.PlayerUid] = previous;
                throw;
            }
        }
    }

    public DiscordRoleSyncState? Remove(string playerUid)
    {
        lock (gate)
        {
            EnsureWritableLocked();
            if (!database.StatesByPlayerUid.Remove(playerUid, out var removed)) return null;
            try { SaveLocked(); }
            catch { database.StatesByPlayerUid[playerUid] = removed; throw; }
            return Clone(removed);
        }
    }

    private void SaveLocked()
    {
        string json = JsonSerializer.Serialize(database, jsonOptions);
        sapi.WorldManager.SaveGame.StoreData(SaveDataKey, Encoding.UTF8.GetBytes(json));
    }

    private static DiscordRoleSyncState Clone(DiscordRoleSyncState state) => new()
    {
        PlayerUid = state.PlayerUid,
        PlayerName = state.PlayerName,
        DiscordUserId = state.DiscordUserId,
        PreviousRoleCode = state.PreviousRoleCode,
        ManagedRoleCode = state.ManagedRoleCode,
        DiscordRoleId = state.DiscordRoleId,
        SuspendedByManualOverride = state.SuspendedByManualOverride,
        ManualRoleCode = state.ManualRoleCode,
        LastCheckedUnixMilliseconds = state.LastCheckedUnixMilliseconds,
        LastAppliedUnixMilliseconds = state.LastAppliedUnixMilliseconds
    };

    private void EnsureWritableLocked()
    {
        if (!storageReady) throw new InvalidOperationException("Discord role ownership storage could not be read. Role changes are disabled to preserve the existing data.");
    }
}
