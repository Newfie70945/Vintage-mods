using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Newfies.WebsiteConnector;

public static partial class PublicContract
{
    public static bool AllowedPath(string path) => path is "/api/map" or "/api/players" or "/api/leaderboard" or "/api/status" or "/api/community" or "/api/health" or "/overview.png" || TilePath().IsMatch(path);
    [GeneratedRegex(@"^/(?:styles/(?:sepia|topo)/)?(?:tiles/-?\d{1,10}/-?\d{1,10}|lod/(?:2|4|8|16|32|64|128|256|512|1024|2048|4096)/-?\d{1,10}/-?\d{1,10})\.png\z")]
    private static partial Regex TilePath();
    [GeneratedRegex(@"^v=\d{1,16}\z")]
    public static partial Regex TileQuery();
    public static bool Authorized(string? header, string token)
    {
        if (header == null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        // Hash both sides to fixed size before comparison; no token appears in responses or logs.
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])), SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
    private static JsonObject Pick(JsonObject source, string fields)
    {
        var result = new JsonObject();
        foreach (var key in fields.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (source.TryGetPropertyValue(key, out var value)) result[key] = value?.DeepClone();
        return result;
    }
    private static JsonArray Rows(JsonNode? node, string fields, int cap = 1000)
    {
        var result = new JsonArray();
        if (node is JsonArray list)
            foreach (var item in list.Take(cap).OfType<JsonObject>()) result.Add(Pick(item, fields));
        return result;
    }
    public static JsonObject Sanitize(string path, JsonObject source)
    {
        const string playerFields = "name x y z dimension yawRadians";
        if (path == "/api/players")
        {
            var result = Pick(source, "playerRefreshSeconds playerSnapshotUnixMilliseconds showPlayers activePlayerCount");
            result["players"] = source["showPlayers"]?.GetValue<bool>() == true ? Rows(source["players"], playerFields) : new JsonArray();
            return result;
        }
        if (path == "/api/map")
        {
            var result = Pick(source, "enabled running coordinateOriginX coordinateOriginZ spawnX spawnZ showPlayers showWaypoints showLeaderboards exportNormalTerrain exportSepiaTerrain exportTopographicTerrain maximumZoomOutSpanBlocks defaultViewSpanBlocks publicRefreshSeconds playerRefreshSeconds generatedUnixMilliseconds activePlayerCount ingameDate playerSnapshotUnixMilliseconds");
            if (source["terrainManifest"] is JsonObject manifest)
                result["terrainManifest"] = Pick(manifest, "tileSize generatedUnixMilliseconds minWorldX maxWorldXExclusive minWorldZ maxWorldZExclusive lodFactors lodTilePixelSize overviewAvailable");
            result["players"] = source["showPlayers"]?.GetValue<bool>() == true ? Rows(source["players"], playerFields) : new JsonArray();
            result["waypoints"] = source["showWaypoints"]?.GetValue<bool>() == true ? Rows(source["waypoints"], "name description type x y z dimension color ownerPlayerName") : new JsonArray();
            return result;
        }
        if (path == "/api/leaderboard")
        {
            var result = Pick(source, "enabled generatedUnixMilliseconds refreshSeconds topCount playerCount offlinePlayerCount xSkillsPlayerCount xSkillsOfflinePlayerCount playtimeEnabled xSkillsEnabled xSkillsDetected xSkillsStatus");
            const string entry = "rank player online value displayValue level experience";
            foreach (var key in new[] { "playtime", "currentSession", "veterans", "totalLevels", "totalExperience" }) result[key] = Rows(source[key], entry);
            var skills = new JsonArray();
            if (source["skills"] is JsonArray list) foreach (var row in list.Take(256).OfType<JsonObject>())
            { var skill = Pick(row, "name displayName icon accent"); skill["entries"] = Rows(row["entries"], entry); skills.Add(skill); }
            result["skills"] = skills;
            var players = new JsonArray();
            if (source["players"] is JsonArray profiles) foreach (var row in profiles.Take(1000).OfType<JsonObject>())
            { var player = Pick(row, "player online totalPlaySeconds sessionSeconds firstSeenUnixMilliseconds lastSeenUnixMilliseconds totalSkillLevels totalSkillExperience skillsLeveled"); player["topSkills"] = Rows(row["topSkills"], "name displayName icon level experience", 256); players.Add(player); }
            result["players"] = players;
            return result;
        }
        throw new ArgumentException("Unsupported Suite feed.");
    }
}
