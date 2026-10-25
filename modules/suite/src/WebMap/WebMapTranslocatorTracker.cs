using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

/// <summary>What the public map receives. No player identities, only counts.</summary>
public sealed class WebMapTranslocatorMarker
{
    public string Id { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public bool HasTarget { get; set; }
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public int TargetZ { get; set; }
    /// <summary>active | broken</summary>
    public string State { get; set; } = "active";
    public int UseCount { get; set; }
    public int DiscoveredBy { get; set; }
    public long LastUsedUnixMilliseconds { get; set; }
}

/// <summary>Persisted record. Player ids stay in this file and never reach the map feed.</summary>
public sealed class WebMapTranslocatorRecord
{
    public string Id { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public bool HasTarget { get; set; }
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public int TargetZ { get; set; }
    public string State { get; set; } = "active";
    public int UseCount { get; set; }
    public long FirstSeenUnixMilliseconds { get; set; }
    public long LastUsedUnixMilliseconds { get; set; }
    public List<string> Discoverers { get; set; } = new();
}

public sealed class WebMapTranslocatorDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<WebMapTranslocatorRecord> Translocators { get; set; } = new();
}

/// <summary>
/// Pure rules, kept apart from the game API so they can be tested.
/// </summary>
public static class WebMapTranslocatorRules
{
    public const int MaximumRecords = 5000;
    public const int MaximumDiscoverersPerRecord = 256;

    public static bool IsTranslocatorCode(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.StartsWith("statictranslocator", StringComparison.Ordinal) ||
               path.StartsWith("translocator", StringComparison.Ordinal);
    }

    public static string StateFor(string path) => path.Contains("broken", StringComparison.Ordinal) ? "broken" : "active";

    public static string IdFor(int x, int y, int z) => x + "," + y + "," + z;

    /// <summary>
    /// A teleport shows up as a jump no walking, sprinting or riding speed explains. The allowance grows with the
    /// time between samples so a lagging server does not look like a teleport.
    /// </summary>
    public static bool IsTeleportJump(double dx, double dz, double seconds, double minimumBlocks = 20, double blocksPerSecond = 25)
    {
        if (seconds < 0) seconds = 0;
        double allowed = minimumBlocks + blocksPerSecond * Math.Min(seconds, 5);
        return Math.Sqrt(dx * dx + dz * dz) > allowed;
    }

    /// <summary>Squared horizontal distance test used for "close enough to the pad".</summary>
    public static bool Near(int ax, int az, double bx, double bz, double radius)
    {
        double dx = ax + 0.5 - bx, dz = az + 0.5 - bz;
        return dx * dx + dz * dz <= radius * radius;
    }
}

/// <summary>
/// Records translocators that players have actually found or used, for the map's translocator layer.
/// Discovery: a player standing within <c>DiscoveryRadius</c> blocks of one. Use: a teleport-sized jump that starts
/// next to one translocator and ends next to another. Nothing is read from the world beyond block codes near players,
/// so translocators nobody has visited never appear.
/// </summary>
public sealed class WebMapTranslocatorTracker : IDisposable
{
    private const string FileName = "translocators-v1.json";
    private const int ScanIntervalMs = 3000;
    private const int JumpIntervalMs = 500;
    private const int ScanVertical = 4;
    private const int PadSearchRadius = 4;

    private readonly ICoreServerAPI api;
    private readonly string filePath;
    private readonly Func<int> discoveryRadius;
    private readonly object gate = new();
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private readonly Dictionary<string, WebMapTranslocatorRecord> records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerSample> lastSample = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int X, int Z)> lastScanAt = new(StringComparer.Ordinal);
    private long scanListener = -1, jumpListener = -1;
    private bool dirty, disposed, started;
    private long lastSaveMs;
    private long version;

    private readonly struct PlayerSample
    {
        public readonly double X, Z;
        public readonly long AtMs;
        public PlayerSample(double x, double z, long atMs) { X = x; Z = z; AtMs = atMs; }
    }

    public WebMapTranslocatorTracker(ICoreServerAPI api, string dataRoot, Func<int> discoveryRadius)
    {
        this.api = api;
        filePath = Path.Combine(dataRoot, FileName);
        this.discoveryRadius = discoveryRadius;
    }

    public long Version => System.Threading.Interlocked.Read(ref version);

    public void Start()
    {
        if (started || disposed) return;
        started = true;
        Load();
        scanListener = api.Event.RegisterGameTickListener(_ => SafeScan(), ScanIntervalMs);
        jumpListener = api.Event.RegisterGameTickListener(_ => SafeJumpCheck(), JumpIntervalMs);
        api.Event.PlayerDisconnect += OnDisconnect;
        api.Event.PlayerNowPlaying += OnNowPlaying;
    }

    public List<WebMapTranslocatorMarker> Snapshot()
    {
        lock (gate)
            return records.Values.Select(r => new WebMapTranslocatorMarker
            {
                Id = r.Id, X = r.X, Y = r.Y, Z = r.Z, HasTarget = r.HasTarget, TargetX = r.TargetX, TargetY = r.TargetY,
                TargetZ = r.TargetZ, State = r.State, UseCount = r.UseCount, DiscoveredBy = r.Discoverers.Count,
                LastUsedUnixMilliseconds = r.LastUsedUnixMilliseconds
            }).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    public int Count { get { lock (gate) return records.Count; } }

    /// <summary>Removes everything that was recorded (admin reset).</summary>
    public int Clear()
    {
        int removed;
        lock (gate) { removed = records.Count; records.Clear(); dirty = true; }
        Bump();
        Save(force: true);
        return removed;
    }

    private void OnNowPlaying(IServerPlayer player) { lock (gate) { lastSample.Remove(player.PlayerUID); lastScanAt.Remove(player.PlayerUID); } }
    private void OnDisconnect(IServerPlayer player) { lock (gate) { lastSample.Remove(player.PlayerUID); lastScanAt.Remove(player.PlayerUID); } }

    private static bool Eligible(IServerPlayer player) =>
        player?.Entity != null && player.ConnectionState == EnumClientState.Playing &&
        player.Entity.Pos.Dimension == 0 &&
        player.WorldData?.CurrentGameMode is not (EnumGameMode.Spectator or EnumGameMode.Creative);

    private void SafeScan()
    {
        if (disposed) return;
        try
        {
            int radius = Math.Clamp(discoveryRadius(), 3, 16);
            foreach (IPlayer p in api.World.AllOnlinePlayers)
            {
                if (p is not IServerPlayer player || !Eligible(player)) continue;
                int px = (int)Math.Floor(player.Entity.Pos.X), pz = (int)Math.Floor(player.Entity.Pos.Z);
                string uid = player.PlayerUID;
                lock (gate)
                {
                    // A player who has not moved since the last scan cannot have found anything new.
                    if (lastScanAt.TryGetValue(uid, out var last) && Math.Abs(last.X - px) < 3 && Math.Abs(last.Z - pz) < 3) continue;
                    lastScanAt[uid] = (px, pz);
                }
                ScanAround(player, radius);
            }
            Save(force: false);
        }
        catch (Exception ex) { api.Logger.Debug("[ServerEssentials/WebMap] translocator scan skipped: {0}", ex.Message); }
    }

    private void ScanAround(IServerPlayer player, int radius)
    {
        var pos = player.Entity.Pos.AsBlockPos;
        var min = new BlockPos(pos.X - radius, pos.Y - ScanVertical, pos.Z - radius, 0);
        var max = new BlockPos(pos.X + radius, pos.Y + ScanVertical, pos.Z + radius, 0);
        double ex = player.Entity.Pos.X, ez = player.Entity.Pos.Z;
        var found = new List<(int X, int Y, int Z, string State)>();
        api.World.BlockAccessor.WalkBlocks(min, max, (block, x, y, z) =>
        {
            string? path = block?.Code?.Path;
            if (!WebMapTranslocatorRules.IsTranslocatorCode(path)) return;
            if (!WebMapTranslocatorRules.Near(x, z, ex, ez, radius)) return;
            found.Add((x, y, z, WebMapTranslocatorRules.StateFor(path!)));
        });
        foreach (var hit in found) Discover(hit.X, hit.Y, hit.Z, hit.State, player.PlayerUID, used: false);
    }

    private void SafeJumpCheck()
    {
        if (disposed) return;
        try
        {
            long now = Environment.TickCount64;
            foreach (IPlayer p in api.World.AllOnlinePlayers)
            {
                if (p is not IServerPlayer player || !Eligible(player)) continue;
                double x = player.Entity.Pos.X, z = player.Entity.Pos.Z;
                PlayerSample previous;
                bool had;
                lock (gate)
                {
                    had = lastSample.TryGetValue(player.PlayerUID, out previous);
                    lastSample[player.PlayerUID] = new PlayerSample(x, z, now);
                }
                if (!had) continue;
                double seconds = (now - previous.AtMs) / 1000.0;
                if (!WebMapTranslocatorRules.IsTeleportJump(x - previous.X, z - previous.Z, seconds)) continue;
                RecordUse(player, previous.X, previous.Z, x, z);
            }
        }
        catch (Exception ex) { api.Logger.Debug("[ServerEssentials/WebMap] translocator jump check skipped: {0}", ex.Message); }
    }

    /// <summary>Both ends of a jump must be next to a translocator, otherwise it was a /tp, respawn or warp.</summary>
    private void RecordUse(IServerPlayer player, double fromX, double fromZ, double toX, double toZ)
    {
        var from = FindPadNear(fromX, player.Entity.Pos.Y, fromZ);
        var to = FindPadNear(toX, player.Entity.Pos.Y, toZ);
        if (from == null || to == null) return;
        var a = Discover(from.Value.X, from.Value.Y, from.Value.Z, from.Value.State, player.PlayerUID, used: true);
        var b = Discover(to.Value.X, to.Value.Y, to.Value.Z, to.Value.State, player.PlayerUID, used: false);
        lock (gate)
        {
            // The destination of a one-way trip is also where the return pad stands: link them both ways.
            Link(a, b);
            Link(b, a);
            dirty = true;
        }
        Bump();
    }

    private static void Link(WebMapTranslocatorRecord? from, WebMapTranslocatorRecord? to)
    {
        if (from == null || to == null || ReferenceEquals(from, to) || from.HasTarget) return;
        from.HasTarget = true; from.TargetX = to.X; from.TargetY = to.Y; from.TargetZ = to.Z;
    }

    private (int X, int Y, int Z, string State)? FindPadNear(double x, double y, double z)
    {
        // The player's own Y can sit on the pad or one block above it; search a small box around them.
        var cx = (int)Math.Floor(x); var cy = (int)Math.Floor(y); var cz = (int)Math.Floor(z);
        var min = new BlockPos(cx - PadSearchRadius, cy - 3, cz - PadSearchRadius, 0);
        var max = new BlockPos(cx + PadSearchRadius, cy + 3, cz + PadSearchRadius, 0);
        (int X, int Y, int Z, string State)? best = null;
        double bestDistance = double.MaxValue;
        api.World.BlockAccessor.WalkBlocks(min, max, (block, bx, by, bz) =>
        {
            string? path = block?.Code?.Path;
            if (!WebMapTranslocatorRules.IsTranslocatorCode(path)) return;
            double d = (bx + .5 - x) * (bx + .5 - x) + (bz + .5 - z) * (bz + .5 - z);
            if (d >= bestDistance) return;
            bestDistance = d; best = (bx, by, bz, WebMapTranslocatorRules.StateFor(path!));
        });
        return best;
    }

    private WebMapTranslocatorRecord? Discover(int x, int y, int z, string state, string playerUid, bool used)
    {
        string id = WebMapTranslocatorRules.IdFor(x, y, z);
        bool created = false, changed = false;
        WebMapTranslocatorRecord? record;
        lock (gate)
        {
            if (!records.TryGetValue(id, out record))
            {
                if (records.Count >= WebMapTranslocatorRules.MaximumRecords) return null;
                record = new WebMapTranslocatorRecord
                {
                    Id = id, X = x, Y = y, Z = z, State = state,
                    FirstSeenUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                records[id] = record;
                created = changed = true;
            }
            if (!string.Equals(record.State, state, StringComparison.Ordinal)) { record.State = state; changed = true; }
            if (!string.IsNullOrEmpty(playerUid) && record.Discoverers.Count < WebMapTranslocatorRules.MaximumDiscoverersPerRecord &&
                !record.Discoverers.Contains(playerUid, StringComparer.Ordinal))
            { record.Discoverers.Add(playerUid); changed = true; }
            if (used)
            {
                record.UseCount++;
                record.LastUsedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                changed = true;
            }
            if (changed) dirty = true;
        }
        if (created || !record.HasTarget) TryReadTarget(record);
        if (changed) Bump();
        return record;
    }

    /// <summary>
    /// Static translocators store their destination on the block entity. Read it by name so a changed field layout
    /// degrades to "no link line" instead of breaking the layer. Unreadable or implausible targets are ignored.
    /// </summary>
    private void TryReadTarget(WebMapTranslocatorRecord record)
    {
        try
        {
            if (record.State == "broken") return;
            object? entity = api.World.BlockAccessor.GetBlockEntity(new BlockPos(record.X, record.Y, record.Z, 0));
            if (entity == null) return;
            foreach (string name in new[] { "tpLocation", "TpLocation", "TargetPos", "targetPos", "Target" })
            {
                object? value = ReadMember(entity, name);
                if (value == null) continue;
                if (!TryReadCoordinate(value, out int tx, out int ty, out int tz)) continue;
                if (tx == record.X && tz == record.Z) continue;
                if (tx == 0 && ty == 0 && tz == 0) continue; // unset default
                lock (gate)
                {
                    record.HasTarget = true; record.TargetX = tx; record.TargetY = ty; record.TargetZ = tz;
                    dirty = true;
                }
                Bump();
                return;
            }
        }
        catch { /* layout differs; the link line is optional */ }
    }

    private static object? ReadMember(object source, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = source.GetType();
        return type.GetField(name, flags)?.GetValue(source) ?? type.GetProperty(name, flags)?.GetValue(source);
    }

    private static bool TryReadCoordinate(object value, out int x, out int y, out int z)
    {
        x = y = z = 0;
        object? ox = ReadMember(value, "X"), oy = ReadMember(value, "Y"), oz = ReadMember(value, "Z");
        if (ox == null || oy == null || oz == null) return false;
        try
        {
            double dx = Convert.ToDouble(ox), dy = Convert.ToDouble(oy), dz = Convert.ToDouble(oz);
            if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(dz)) return false;
            x = (int)Math.Floor(dx); y = (int)Math.Floor(dy); z = (int)Math.Floor(dz);
            return true;
        }
        catch { return false; }
    }

    private void Bump() => System.Threading.Interlocked.Increment(ref version);

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var document = JsonSerializer.Deserialize<WebMapTranslocatorDocument>(File.ReadAllBytes(filePath), json);
            if (document?.Translocators == null) return;
            lock (gate)
                foreach (var record in document.Translocators.Where(x => !string.IsNullOrWhiteSpace(x.Id)).Take(WebMapTranslocatorRules.MaximumRecords))
                {
                    record.Discoverers ??= new List<string>();
                    records[record.Id] = record;
                }
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/WebMap] translocator file unreadable ({0}); starting empty and keeping the original file.", ex.GetType().Name);
            try { File.Copy(filePath, filePath + ".unreadable", true); } catch { }
        }
    }

    private void Save(bool force)
    {
        long now = Environment.TickCount64;
        byte[] bytes;
        lock (gate)
        {
            if (!dirty || (!force && now - lastSaveMs < 60_000)) return;
            bytes = JsonSerializer.SerializeToUtf8Bytes(new WebMapTranslocatorDocument { Translocators = records.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToList() }, json);
            dirty = false;
            lastSaveMs = now;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            string temp = filePath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, filePath, true);
        }
        catch (Exception ex)
        {
            lock (gate) dirty = true;
            api.Logger.Warning("[ServerEssentials/WebMap] could not save translocators: {0}", ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (scanListener >= 0) api.Event.UnregisterGameTickListener(scanListener); } catch { }
        try { if (jumpListener >= 0) api.Event.UnregisterGameTickListener(jumpListener); } catch { }
        try { api.Event.PlayerDisconnect -= OnDisconnect; api.Event.PlayerNowPlaying -= OnNowPlaying; } catch { }
        Save(force: true);
    }
}
