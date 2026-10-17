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

/// <summary>
/// Small, per-world persistent waypoint store for the native Web Map.
/// Admin-created waypoints and player-created waypoints share one file, but player ownership
/// is tracked by UID so per-player limits/removal rules cannot be bypassed by renaming.
/// </summary>
internal sealed class WebMapWaypointStore
{
    private const int MaxWaypoints = 500;
    private readonly ICoreServerAPI api;
    private readonly object gate = new();
    private readonly string filePath;
    private readonly string worldId;
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    private List<WebMapWaypoint> waypoints = new();
    private bool storageReady = true;
    public string StorageError { get; private set; } = "";

    public WebMapWaypointStore(ICoreServerAPI api, string mapDataRoot)
    {
        this.api = api;
        worldId = api.World.SavegameIdentifier ?? api.WorldManager.SaveGame.SavegameIdentifier ?? "unknownworld";
        string worldHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worldId))).ToLowerInvariant()[..16];
        filePath = Path.Combine(mapDataRoot, $"waypoints-{worldHash}.json");
        Load();
    }

    public List<WebMapWaypoint> Snapshot()
    {
        lock (gate)
        {
            var result = new List<WebMapWaypoint>(waypoints.Count);
            for (int i = 0; i < waypoints.Count; i++) result.Add(waypoints[i].Clone());
            return result;
        }
    }

    public int CountPlayerWaypoints(string playerUid) => CountPlayerWaypoints(playerUid, null);

    public int CountPlayerWaypoints(string playerUid, string? type)
    {
        string uid = (playerUid ?? "").Trim();
        string wanted = string.IsNullOrWhiteSpace(type) ? "" : NormalizeType(type);
        if (uid.Length == 0) return 0;
        lock (gate)
        {
            int count = 0;
            for (int i = 0; i < waypoints.Count; i++)
            {
                WebMapWaypoint item = waypoints[i];
                if (!item.IsPlayerWaypoint || !string.Equals(item.OwnerPlayerUid, uid, StringComparison.Ordinal)) continue;
                if (wanted.Length != 0 && !string.Equals(NormalizeType(item.Type), wanted, StringComparison.Ordinal)) continue;
                count++;
            }
            return count;
        }
    }

    public WebMapWaypoint CreatePlayerWaypoint(
        string playerUid,
        string playerName,
        string type,
        string name,
        string icon,
        double x,
        double y,
        double z,
        int dimension,
        int maxPlayerWaypoints)
    {
        string uid = (playerUid ?? "").Trim();
        string ownerName = (playerName ?? "player").Trim();
        string cleanName = (name ?? "").Trim();
        string cleanType = NormalizeType(type);
        string cleanIcon = NormalizeIcon(icon);
        if (uid.Length == 0) throw new InvalidOperationException("Player UID is unavailable.");
        if (cleanName.Length == 0) throw new InvalidOperationException("Waypoint name is required.");
        if (cleanName.Length > 80) cleanName = cleanName[..80];
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new InvalidOperationException("Player position is unavailable.");

        maxPlayerWaypoints = Math.Clamp(maxPlayerWaypoints, 0, 100);
        if (maxPlayerWaypoints <= 0)
            throw new InvalidOperationException("Player-created Web Map waypoints are disabled by the server.");

        lock (gate)
        {
            EnsureWritable();
            int owned = 0;
            for (int i = 0; i < waypoints.Count; i++)
            {
                WebMapWaypoint item = waypoints[i];
                if (!item.IsPlayerWaypoint || !string.Equals(item.OwnerPlayerUid, uid, StringComparison.Ordinal)) continue;
                if (string.Equals(item.Name, cleanName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"You already have a waypoint named '{cleanName}'. Remove it first or use another name.");
                if (string.Equals(NormalizeType(item.Type), cleanType, StringComparison.Ordinal)) owned++;
            }
            if (owned >= maxPlayerWaypoints)
                throw new InvalidOperationException($"Waypoint limit reached ({owned}/{maxPlayerWaypoints}). Remove one before adding another.");
            if (waypoints.Count >= MaxWaypoints)
                throw new InvalidOperationException($"Web Map is limited to {MaxWaypoints} waypoints per world.");

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (ownerName.Length == 0) ownerName = "player";
            if (ownerName.Length > 80) ownerName = ownerName[..80];
            var saved = new WebMapWaypoint
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = cleanName,
                Description = "",
                Type = cleanType,
                Icon = cleanIcon,
                X = x,
                Y = y,
                Z = z,
                Dimension = Math.Clamp(dimension, -1024, 1024),
                Color = cleanType == "major" ? "#f59e0b" : "#38bdf8",
                IsPlayerWaypoint = true,
                OwnerPlayerUid = uid,
                OwnerPlayerName = ownerName,
                CreatedBy = ownerName,
                CreatedUnixMilliseconds = now,
                UpdatedBy = ownerName,
                UpdatedUnixMilliseconds = now
            };
            var next = new List<WebMapWaypoint>(waypoints) { saved };
            CommitLocked(next);
            return saved.Clone();
        }
    }

    public bool DeletePlayerWaypoint(string playerUid, string nameOrId, out WebMapWaypoint? deleted)
    {
        deleted = null;
        string uid = (playerUid ?? "").Trim();
        string target = (nameOrId ?? "").Trim();
        if (uid.Length == 0 || target.Length == 0) return false;

        lock (gate)
        {
            EnsureWritable();
            int found = -1;
            for (int i = 0; i < waypoints.Count; i++)
            {
                WebMapWaypoint item = waypoints[i];
                if (!item.IsPlayerWaypoint || !string.Equals(item.OwnerPlayerUid, uid, StringComparison.Ordinal)) continue;
                if (string.Equals(item.Name, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Id, target, StringComparison.OrdinalIgnoreCase) ||
                    (target.Length >= 6 && item.Id.StartsWith(target, StringComparison.OrdinalIgnoreCase)))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0) return false;
            var next = new List<WebMapWaypoint>(waypoints);
            var removed = next[found].Clone();
            next.RemoveAt(found);
            CommitLocked(next);
            deleted = removed;
            return true;
        }
    }

    public WebMapWaypoint Upsert(WebMapWaypoint incoming, string actor)
    {
        incoming ??= new WebMapWaypoint();
        string name = (incoming.Name ?? "").Trim();
        if (name.Length == 0) throw new InvalidOperationException("Waypoint name is required.");
        if (name.Length > 80) name = name[..80];

        string description = (incoming.Description ?? "").Trim();
        if (description.Length > 300) description = description[..300];
        if (!double.IsFinite(incoming.X) || !double.IsFinite(incoming.Y) || !double.IsFinite(incoming.Z))
            throw new InvalidOperationException("Waypoint coordinates must be finite numbers.");

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string id = (incoming.Id ?? "").Trim();
        if (id.Length > 64) id = "";
        string by = (actor ?? "admin").Trim();
        if (by.Length == 0) by = "admin";
        if (by.Length > 80) by = by[..80];
        string type = NormalizeType(incoming.Type);
        string icon = NormalizeIcon(incoming.Icon);

        lock (gate)
        {
            EnsureWritable();
            int index = -1;
            if (id.Length > 0)
            {
                for (int i = 0; i < waypoints.Count; i++)
                {
                    if (string.Equals(waypoints[i].Id, id, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }
            }

            var next = new List<WebMapWaypoint>(waypoints);
            WebMapWaypoint saved;
            if (index >= 0)
            {
                var existing = waypoints[index];
                saved = new WebMapWaypoint
                {
                    Id = existing.Id,
                    Name = name,
                    Description = description,
                    Type = type,
                    Icon = icon,
                    X = incoming.X,
                    Y = incoming.Y,
                    Z = incoming.Z,
                    Dimension = Math.Clamp(incoming.Dimension, -1024, 1024),
                    Color = NormalizeColor(incoming.Color),
                    IsPlayerWaypoint = existing.IsPlayerWaypoint,
                    OwnerPlayerUid = existing.OwnerPlayerUid,
                    OwnerPlayerName = existing.OwnerPlayerName,
                    CreatedBy = existing.CreatedBy,
                    CreatedUnixMilliseconds = existing.CreatedUnixMilliseconds,
                    UpdatedBy = by,
                    UpdatedUnixMilliseconds = now
                };
                next[index] = saved;
            }
            else
            {
                if (waypoints.Count >= MaxWaypoints)
                    throw new InvalidOperationException($"Web Map is limited to {MaxWaypoints} waypoints per world.");

                saved = new WebMapWaypoint
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    Description = description,
                    Type = type,
                    Icon = icon,
                    X = incoming.X,
                    Y = incoming.Y,
                    Z = incoming.Z,
                    Dimension = Math.Clamp(incoming.Dimension, -1024, 1024),
                    Color = NormalizeColor(incoming.Color),
                    IsPlayerWaypoint = false,
                    CreatedBy = by,
                    CreatedUnixMilliseconds = now,
                    UpdatedBy = by,
                    UpdatedUnixMilliseconds = now
                };
                next.Add(saved);
            }

            CommitLocked(next);
            return saved.Clone();
        }
    }

    public bool Delete(string? id)
    {
        string target = (id ?? "").Trim();
        if (target.Length == 0) return false;
        lock (gate)
        {
            EnsureWritable();
            var next = new List<WebMapWaypoint>(waypoints);
            int removed = next.RemoveAll(x => string.Equals(x.Id, target, StringComparison.Ordinal));
            if (removed <= 0) return false;
            CommitLocked(next);
            return true;
        }
    }

    private void Load()
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var document = JsonStateRecovery.Read<WebMapWaypointDocument>(filePath, options);
            if (document.SchemaVersion < 1 || document.SchemaVersion > 3 ||
                !string.Equals(document.WorldId ?? "", worldId, StringComparison.Ordinal) ||
                document.Waypoints == null || document.Waypoints.Count > MaxWaypoints)
                throw new InvalidDataException("Unsupported, foreign-world or incomplete waypoint document.");
            var ids = new HashSet<string>(StringComparer.Ordinal);

            var loaded = new List<WebMapWaypoint>(Math.Min(document.Waypoints?.Count ?? 0, MaxWaypoints));
            if (document.Waypoints != null)
            {
                for (int i = 0; i < document.Waypoints.Count && loaded.Count < MaxWaypoints; i++)
                {
                    var item = document.Waypoints[i];
                    if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) || !ids.Add(item.Id))
                        throw new InvalidDataException("Invalid or duplicate waypoint identity.");
                    if (!double.IsFinite(item.X) || !double.IsFinite(item.Y) || !double.IsFinite(item.Z))
                        throw new InvalidDataException("Invalid waypoint coordinates.");
                    item.Name = item.Name.Trim();
                    if (item.Name.Length > 80) item.Name = item.Name[..80];
                    item.Description = (item.Description ?? "").Trim();
                    if (item.Description.Length > 300) item.Description = item.Description[..300];
                    item.Type = NormalizeType(item.Type);
                    item.Icon = NormalizeIcon(item.Icon);
                    item.Color = NormalizeColor(item.Color);
                    item.OwnerPlayerUid = (item.OwnerPlayerUid ?? "").Trim();
                    item.OwnerPlayerName = (item.OwnerPlayerName ?? "").Trim();
                    if (item.IsPlayerWaypoint && item.OwnerPlayerUid.Length == 0)
                        throw new InvalidDataException("Player waypoint ownership is missing.");
                    loaded.Add(item);
                }
            }

            lock (gate) waypoints = loaded;
        }
        catch (Exception ex)
        {
            storageReady = false;
            StorageError = "Waypoint storage could not be read. The original file was retained; restore it and reload the Suite before editing waypoints.";
            try { JsonStateRecovery.Preserve(filePath); } catch { }
            api.Logger.Warning("[ServerEssentials/WebMap] waypoint storage unavailable ({0}); edits are blocked and the original file was retained.", ex.GetType().Name);
        }
    }

    private void EnsureWritable()
    {
        if (!storageReady) throw new InvalidOperationException(StorageError);
    }

    private void CommitLocked(List<WebMapWaypoint> next)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string tempPath = filePath + ".tmp";
        var document = new WebMapWaypointDocument
        {
            SchemaVersion = 3,
            WorldId = worldId,
            Waypoints = next
        };
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, options);
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(tempPath, filePath, true);
            waypoints = next;
            StorageError = "";
        }
        catch
        {
            StorageError = "Waypoint changes could not be saved. Existing waypoints are unchanged; retry when storage is writable.";
            throw new InvalidOperationException(StorageError);
        }
    }

    internal static string NormalizeType(string? value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        return text == "minor" ? "minor" : "major";
    }

    internal static string NormalizeIcon(string? value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        return text switch
        {
            "base" or "home" => "base",
            "settlement" or "town" or "village" => "settlement",
            "shop" or "market" => "shop",
            "mine" or "cave" => "mine",
            "resource" => "resource",
            "danger" or "warning" => "danger",
            "landmark" => "landmark",
            "road" or "route" => "road",
            "farm" => "farm",
            _ => "pin"
        };
    }

    private static string NormalizeColor(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 7 && text[0] == '#')
        {
            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return "#38bdf8";
            }
            return text.ToLowerInvariant();
        }
        return "#38bdf8";
    }

    private sealed class WebMapWaypointDocument
    {
        public int SchemaVersion { get; set; } = 3;
        public string WorldId { get; set; } = "";
        [JsonRequired]
        public List<WebMapWaypoint> Waypoints { get; set; } = new();
    }
}

public sealed class WebMapWaypoint
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "major";
    public string Icon { get; set; } = "pin";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Dimension { get; set; }
    public string Color { get; set; } = "#38bdf8";
    public bool IsPlayerWaypoint { get; set; }
    public string OwnerPlayerUid { get; set; } = "";
    public string OwnerPlayerName { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public long CreatedUnixMilliseconds { get; set; }
    public string UpdatedBy { get; set; } = "";
    public long UpdatedUnixMilliseconds { get; set; }
    public WebMapWaypoint Clone() => (WebMapWaypoint)MemberwiseClone();
}
