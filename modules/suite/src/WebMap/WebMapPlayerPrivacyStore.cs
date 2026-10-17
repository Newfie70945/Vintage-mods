using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Server;
using Newfies.Storage;

namespace BlockLogger.WebMap;

/// <summary>Per-world persistent player opt-out state for the public live-player Web Map layer.</summary>
internal sealed class WebMapPlayerPrivacyStore
{
    private readonly object gate = new();
    private readonly ICoreServerAPI api;
    private bool storageReady = true;
    private bool dirty;
    public string StorageError { get; private set; } = "";
    private readonly string filePath;
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    private HashSet<string> hiddenPlayerUids = new(StringComparer.Ordinal);

    public WebMapPlayerPrivacyStore(ICoreServerAPI api, string mapDataRoot)
    {
        this.api = api;
        string worldId = api.World.SavegameIdentifier ?? api.WorldManager.SaveGame.SavegameIdentifier ?? "unknownworld";
        string worldHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worldId))).ToLowerInvariant()[..16];
        filePath = Path.Combine(mapDataRoot, $"player-privacy-{worldHash}.json");
        Load();
    }

    public bool IsHidden(string? playerUid)
    {
        string uid = (playerUid ?? "").Trim();
        if (uid.Length == 0) return false;
        lock (gate) return !storageReady || hiddenPlayerUids.Contains(uid);
    }

    public bool SetHidden(string? playerUid, bool hidden)
    {
        string uid = (playerUid ?? "").Trim();
        if (uid.Length == 0) return false;
        lock (gate)
        {
            if (!storageReady) throw new InvalidOperationException(StorageError);
            var next = new HashSet<string>(hiddenPlayerUids, StringComparer.Ordinal);
            bool changed = hidden ? next.Add(uid) : next.Remove(uid);
            if (!changed && !dirty) return false;
            try
            {
                SaveLocked(next);
                hiddenPlayerUids = next;
                dirty = false;
                StorageError = "";
            }
            catch
            {
                // A failed show request must not expose a player. A failed hide request
                // takes effect immediately in memory and remains dirty for an explicit retry.
                if (hidden) { hiddenPlayerUids.Add(uid); dirty = true; }
                StorageError = "Live-map privacy could not be saved. Hidden players remain hidden; retry when storage is writable.";
                throw new InvalidOperationException(StorageError);
            }
            return changed;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var doc = JsonStateRecovery.Read<PrivacyDocument>(filePath, options);
            if (doc.SchemaVersion != 1 || doc.HiddenPlayerUids == null)
                throw new InvalidDataException("Unsupported or incomplete privacy document.");
            var loaded = new HashSet<string>(StringComparer.Ordinal);
            foreach (string? raw in doc.HiddenPlayerUids)
            {
                string uid = (raw ?? "").Trim();
                if (uid.Length == 0 || uid.Length > 200)
                    throw new InvalidDataException("Invalid privacy identity.");
                loaded.Add(uid);
            }
            hiddenPlayerUids = loaded;
        }
        catch (Exception ex)
        {
            // A broken opt-out file cannot safely mean that everyone opted in.
            storageReady = false;
            StorageError = "Live-player map data is hidden because privacy storage could not be read. Restore the privacy file and reload the Suite.";
            try { JsonStateRecovery.Preserve(filePath); } catch { }
            api.Logger.Warning("[ServerEssentials/WebMap] privacy storage unavailable ({0}); live-player exposure is blocked and the original file was retained.", ex.GetType().Name);
        }
    }

    private void SaveLocked(HashSet<string> next)
    {
        string dir = Path.GetDirectoryName(filePath) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = filePath + ".tmp";
        var doc = new PrivacyDocument { HiddenPlayerUids = new List<string>(next) };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(doc, options);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(tmp, filePath, true);
    }

    private sealed class PrivacyDocument
    {
        public int SchemaVersion { get; set; } = 1;
        [JsonRequired]
        public List<string> HiddenPlayerUids { get; set; } = new();
    }
}
