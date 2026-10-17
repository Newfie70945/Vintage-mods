using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using BlockLogger.Admin;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

/// <summary>
/// Builds the shared leaderboard cache used by the authenticated WebPanel and, when enabled,
/// the public Web Map from Server Essentials player history and XLib/XSkills server-side data.
///
/// XSkills is discovered dynamically. There is intentionally no compile-time reference to
/// XLib or XSkills, so Server Essentials remains fully functional when those mods are absent.
/// All XSkills reflection runs on the server thread through the Suite game-tick listener;
/// HTTP requests only serialize the already-built immutable snapshot.
/// </summary>
public sealed class WebMapLeaderboardService : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly global::System.Func<IReadOnlyList<AdminPlayerSummary>?> adminPlayersProvider;
    private readonly global::System.Func<string, bool> isPlayerHiddenProvider;
    private readonly global::System.Func<HashSet<string>> publicVisiblePlayerUidsProvider;
    private readonly global::System.Func<WebMapConfig> configProvider;
    private readonly object gate = new();
    private WebMapLeaderboardSnapshot snapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard cache has not been built yet.");
    private WebMapLeaderboardSnapshot publicSnapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard cache has not been built yet.");
    private long listenerId = -1;
    private long lastRefreshUnixMilliseconds;
    private bool disposed;
    private volatile bool active;
    private int refreshQueued;

    public WebMapLeaderboardService(
        ICoreServerAPI api,
        global::System.Func<IReadOnlyList<AdminPlayerSummary>?> adminPlayersProvider,
        global::System.Func<string, bool> isPlayerHiddenProvider,
        global::System.Func<HashSet<string>> publicVisiblePlayerUidsProvider,
        global::System.Func<WebMapConfig> configProvider)
    {
        this.api = api;
        this.adminPlayersProvider = adminPlayersProvider;
        this.isPlayerHiddenProvider = isPlayerHiddenProvider;
        this.publicVisiblePlayerUidsProvider = publicVisiblePlayerUidsProvider;
        this.configProvider = configProvider;
    }

    public void Start()
    {
        if (disposed || listenerId >= 0) return;
        active = true;
        // Tick cheaply every 10 seconds but only rebuild at the configured cadence.
        listenerId = api.Event.RegisterGameTickListener(_ => RefreshIfDue(), 10_000, 3_000);
        QueueRefresh();
    }

    public void Stop()
    {
        active = false;
        if (listenerId >= 0)
        {
            try { api.Event.UnregisterGameTickListener(listenerId); } catch { }
            listenerId = -1;
        }
        lastRefreshUnixMilliseconds = 0;
        lock (gate)
        {
            snapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard service is stopped.");
            publicSnapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard service is stopped.");
        }
    }

    public void RequestRefresh()
    {
        lastRefreshUnixMilliseconds = 0;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (disposed || !active) return;
        if (Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        try
        {
            api.Event.EnqueueMainThreadTask(() =>
            {
                Interlocked.Exchange(ref refreshQueued, 0);
                if (!disposed && active) RefreshNow();
            }, "webmap-leaderboard-refresh");
        }
        catch
        {
            Interlocked.Exchange(ref refreshQueued, 0);
        }
    }

    public WebMapLeaderboardSnapshot GetSnapshot()
    {
        lock (gate) return snapshot;
    }

    public WebMapLeaderboardSnapshot GetPublicSnapshot()
    {
        lock (gate) return publicSnapshot;
    }

    private void RefreshIfDue()
    {
        if (!active || disposed) return;
        WebMapConfig c = configProvider();
        long now = NowMs();
        long intervalMs = Math.Max(15_000L, c.LeaderboardRefreshSeconds * 1000L);
        if (now - lastRefreshUnixMilliseconds < intervalMs) return;
        RefreshNow();
    }

    private void RefreshNow()
    {
        if (disposed || !active) return;
        WebMapConfig c = configProvider();

        try
        {
            var players = (adminPlayersProvider() ?? Array.Empty<AdminPlayerSummary>())
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                .Where(p => string.IsNullOrWhiteSpace(p.PlayerUid) || !isPlayerHiddenProvider(p.PlayerUid))
                .GroupBy(ProfileKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // The authenticated WebPanel uses actual Suite online/session state. Public Web Map
            // exposure is sanitized into a separate snapshot after the full cache is built.
            HashSet<string> actualOnlineUids = players
                .Where(p => p.Online && !string.IsNullOrWhiteSpace(p.PlayerUid))
                .Select(p => p.PlayerUid.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> publicVisibleUids = publicVisiblePlayerUidsProvider();

            int top = Math.Clamp(c.LeaderboardTopPlayers, 3, 20);
            var built = new WebMapLeaderboardSnapshot
            {
                Enabled = true,
                GeneratedUnixMilliseconds = NowMs(),
                RefreshSeconds = c.LeaderboardRefreshSeconds,
                TopCount = top,
                PlayerCount = players.Count,
                PlaytimeEnabled = c.LeaderboardShowPlaytime,
                XSkillsEnabled = c.LeaderboardShowXSkills
            };

            if (c.LeaderboardShowPlaytime)
            {
                built.Playtime = Rank(players
                    .Where(p => p.TotalPlaySeconds > 0)
                    .OrderByDescending(p => p.TotalPlaySeconds)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(top)
                    .Select(p => new WebMapLeaderboardEntry
                    {
                        Player = p.Name,
                        Online = p.Online,
                        Value = p.TotalPlaySeconds,
                        DisplayValue = FormatDuration(p.TotalPlaySeconds)
                    }));

                built.CurrentSession = Rank(players
                    .Where(p => p.Online && p.SessionSeconds > 0)
                    .OrderByDescending(p => p.SessionSeconds)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(top)
                    .Select(p => new WebMapLeaderboardEntry
                    {
                        Player = p.Name,
                        Online = true,
                        Value = p.SessionSeconds,
                        DisplayValue = FormatDuration(p.SessionSeconds)
                    }));

                built.Veterans = Rank(players
                    .Where(p => p.FirstSeenUnixMilliseconds > 0)
                    .OrderBy(p => p.FirstSeenUnixMilliseconds)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(top)
                    .Select(p => new WebMapLeaderboardEntry
                    {
                        Player = p.Name,
                        Online = p.Online,
                        Value = p.FirstSeenUnixMilliseconds,
                        DisplayValue = DateTimeOffset.FromUnixTimeMilliseconds(p.FirstSeenUnixMilliseconds).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    }));
            }

            var profiles = players.ToDictionary(
                ProfileKey,
                p => new WebMapLeaderboardPlayerProfile
                {
                    Player = p.Name,
                    Online = p.Online,
                    TotalPlaySeconds = p.TotalPlaySeconds,
                    SessionSeconds = p.Online ? p.SessionSeconds : 0,
                    FirstSeenUnixMilliseconds = p.FirstSeenUnixMilliseconds,
                    LastSeenUnixMilliseconds = p.LastSeenUnixMilliseconds
                },
                StringComparer.OrdinalIgnoreCase);

            if (c.LeaderboardShowXSkills)
                PopulateXSkills(built, profiles, actualOnlineUids, top);
            else
                built.XSkillsStatus = "XSkills leaderboards are disabled in the Web Map configuration.";

            built.PlayerCount = profiles.Count;
            built.OfflinePlayerCount = profiles.Values.Count(p => !p.Online);
            built.Players = profiles.Values
                .OrderByDescending(p => p.TotalSkillLevels)
                .ThenByDescending(p => p.TotalPlaySeconds)
                .ThenBy(p => p.Player, StringComparer.OrdinalIgnoreCase)
                .ToList();

            WebMapLeaderboardSnapshot builtPublic = BuildPublicSnapshot(built, profiles, publicVisibleUids);
            lastRefreshUnixMilliseconds = built.GeneratedUnixMilliseconds;
            lock (gate)
            {
                snapshot = built;
                publicSnapshot = builtPublic;
            }
        }
        catch (Exception ex)
        {
            lastRefreshUnixMilliseconds = NowMs();
            api.Logger.Warning("[ServerEssentials/WebMap] leaderboard refresh failed safely: {0}: {1}", ex.GetType().Name, ex.Message);
            lock (gate)
            {
                snapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard refresh failed safely. Check the server log for details.");
                snapshot.GeneratedUnixMilliseconds = lastRefreshUnixMilliseconds;
                publicSnapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard refresh failed safely. Check the server log for details.");
                publicSnapshot.GeneratedUnixMilliseconds = lastRefreshUnixMilliseconds;
            }
        }
    }

    private static WebMapLeaderboardSnapshot BuildPublicSnapshot(
        WebMapLeaderboardSnapshot source,
        Dictionary<string, WebMapLeaderboardPlayerProfile> profilesByUid,
        HashSet<string> publicVisibleUids)
    {
        // Clone only at cache-refresh time. Public requests stay cheap and never touch XSkills.
        WebMapLeaderboardSnapshot clone = JsonSerializer.Deserialize<WebMapLeaderboardSnapshot>(
            JsonSerializer.Serialize(source)) ?? WebMapLeaderboardSnapshot.Disabled("Leaderboard cache clone failed.");

        var publicVisibleNames = profilesByUid
            .Where(pair => publicVisibleUids.Contains(pair.Key))
            .Select(pair => pair.Value.Player)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        static void ApplyBadges(IEnumerable<WebMapLeaderboardEntry> entries, HashSet<string> visibleNames)
        {
            foreach (WebMapLeaderboardEntry entry in entries)
                entry.Online = visibleNames.Contains(entry.Player);
        }

        ApplyBadges(clone.Playtime, publicVisibleNames);
        ApplyBadges(clone.Veterans, publicVisibleNames);
        ApplyBadges(clone.TotalLevels, publicVisibleNames);
        ApplyBadges(clone.TotalExperience, publicVisibleNames);
        foreach (WebMapSkillLeaderboard skill in clone.Skills) ApplyBadges(skill.Entries, publicVisibleNames);

        clone.CurrentSession = Rank(profilesByUid
            .Where(pair => publicVisibleUids.Contains(pair.Key) && pair.Value.SessionSeconds > 0)
            .Select(pair => pair.Value)
            .OrderByDescending(p => p.SessionSeconds)
            .ThenBy(p => p.Player, StringComparer.OrdinalIgnoreCase)
            .Take(source.TopCount)
            .Select(p => new WebMapLeaderboardEntry
            {
                Player = p.Player,
                Online = true,
                Value = p.SessionSeconds,
                DisplayValue = FormatDuration(p.SessionSeconds)
            }));

        foreach (WebMapLeaderboardPlayerProfile profile in clone.Players)
        {
            profile.Online = publicVisibleNames.Contains(profile.Player);
            if (!profile.Online) profile.SessionSeconds = 0;
        }
        clone.OfflinePlayerCount = clone.Players.Count(p => !p.Online);
        return clone;
    }

    private void PopulateXSkills(
        WebMapLeaderboardSnapshot built,
        Dictionary<string, WebMapLeaderboardPlayerProfile> profiles,
        HashSet<string> actualOnlineUids,
        int top)
    {
        object? xLeveling = api.ModLoader.GetModSystem("XLib.XLeveling.XLeveling");
        if (xLeveling == null)
        {
            built.XSkillsDetected = false;
            built.XSkillsStatus = "XSkills/XLib not detected. Playtime leaderboards remain available.";
            return;
        }

        built.XSkillsDetected = true;
        object? serverApi = GetMemberValue(xLeveling, "IXLevelingAPI");
        object? template = GetMemberValue(xLeveling, "SkillSetTemplate");
        if (serverApi == null || template == null)
        {
            built.XSkillsStatus = "XSkills/XLib was detected, but its server skill interface was not available.";
            return;
        }

        var skillDefs = ReadSkillDefinitions(template);
        if (skillDefs.Count == 0)
        {
            built.XSkillsStatus = "XSkills/XLib was detected, but no registered skills were exposed.";
            return;
        }

        var rowsByUid = new Dictionary<string, Dictionary<string, XSkillRow>>(StringComparer.OrdinalIgnoreCase);

        // Offline/disconnected records contain stable persisted values for every player that
        // has made progress. Seed each player with the registered skill minimums so total-level
        // calculations are comparable to online players, then overlay their saved progress.
        object? disconnected = GetMemberValue(serverApi, "DiscPlayerSkillSets");
        foreach (var (key, value) in EnumerateDictionary(disconnected))
        {
            string uid = Convert.ToString(key, CultureInfo.InvariantCulture) ?? "";
            if (uid.Length == 0 || value == null || !EnsureProfileForUid(profiles, uid, actualOnlineUids)) continue;
            var rows = CreateBaseRows(skillDefs);
            object? savedSkills = GetMemberValue(value, "Skills");
            foreach (var (skillKey, savedSkill) in EnumerateDictionary(savedSkills))
            {
                string skillName = Convert.ToString(skillKey, CultureInfo.InvariantCulture) ?? "";
                if (skillName.Length == 0 || savedSkill == null) continue;
                SkillDefinition? def = skillDefs.GetValueOrDefault(skillName);
                int level = Math.Max(def?.MinLevel ?? 1, GetInt(savedSkill, "Level", def?.MinLevel ?? 1));
                double currentXp = Math.Max(0, GetDouble(savedSkill, "Experience", 0));
                rows[skillName] = BuildSkillRow(def, skillName, level, currentXp);
            }
            rowsByUid[uid] = rows;
        }

        // Online PlayerSkillSets are authoritative for connected players and replace any
        // disconnected copy of the same UID.
        object? onlineSets = GetMemberValue(serverApi, "PlayerSkillSets");
        foreach (var (playerObject, playerSkillSet) in EnumerateDictionary(onlineSets))
        {
            if (playerObject == null || playerSkillSet == null) continue;
            string uid = GetString(playerObject, "PlayerUID") ?? GetString(GetMemberValue(playerSkillSet, "Player"), "PlayerUID") ?? "";
            if (uid.Length == 0) continue;
            actualOnlineUids.Add(uid);
            if (!EnsureProfileForUid(profiles, uid, actualOnlineUids)) continue;

            var rows = CreateBaseRows(skillDefs);
            if (GetMemberValue(playerSkillSet, "PlayerSkills") is IEnumerable playerSkills)
            {
                foreach (object? playerSkill in playerSkills)
                {
                    if (playerSkill == null) continue;
                    object? skill = GetMemberValue(playerSkill, "Skill");
                    string skillName = GetString(skill, "Name") ?? "";
                    if (skillName.Length == 0) continue;
                    SkillDefinition? def = skillDefs.GetValueOrDefault(skillName);
                    int level = Math.Max(def?.MinLevel ?? 1, GetInt(playerSkill, "Level", def?.MinLevel ?? 1));
                    double currentXp = Math.Max(0, GetDouble(playerSkill, "Experience", 0));
                    rows[skillName] = BuildSkillRow(def, skillName, level, currentXp);
                }
            }
            rowsByUid[uid] = rows;
        }

        var skillBoards = new List<WebMapSkillLeaderboard>();
        foreach (SkillDefinition def in skillDefs.Values.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = new List<(string Player, bool Online, XSkillRow Row)>();
            foreach (var profilePair in profiles)
            {
                if (!rowsByUid.TryGetValue(profilePair.Key, out var rows) || !rows.TryGetValue(def.Name, out var row)) continue;
                if (row.Level <= def.MinLevel && row.CurrentExperience <= 0) continue;
                candidates.Add((profilePair.Value.Player, profilePair.Value.Online, row));
            }

            var board = new WebMapSkillLeaderboard
            {
                Name = def.Name,
                DisplayName = def.DisplayName,
                Icon = SkillVisual(def.Name).Icon,
                Accent = SkillVisual(def.Name).Accent,
                Entries = Rank(candidates
                    .OrderByDescending(x => x.Row.Level)
                    .ThenByDescending(x => x.Row.TotalExperience)
                    .ThenBy(x => x.Player, StringComparer.OrdinalIgnoreCase)
                    .Take(top)
                    .Select(x => new WebMapLeaderboardEntry
                    {
                        Player = x.Player,
                        Online = x.Online,
                        Level = x.Row.Level,
                        Experience = x.Row.CurrentExperience,
                        Value = x.Row.Level,
                        DisplayValue = $"Lv. {x.Row.Level}"
                    }))
            };
            skillBoards.Add(board);
        }

        foreach (var pair in profiles)
        {
            if (!rowsByUid.TryGetValue(pair.Key, out var rows)) continue;
            var progressed = rows.Values.Where(r => r.Level > r.MinLevel || r.CurrentExperience > 0).ToList();
            pair.Value.TotalSkillLevels = rows.Values.Sum(r => Math.Max(0, r.Level));
            pair.Value.TotalSkillExperience = rows.Values.Sum(r => Math.Max(0, r.TotalExperience));
            pair.Value.SkillsLeveled = progressed.Count;
            pair.Value.TopSkills = progressed
                .OrderByDescending(r => r.Level)
                .ThenByDescending(r => r.TotalExperience)
                .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .Select(r => new WebMapPlayerSkillSummary
                {
                    Name = r.Name,
                    DisplayName = r.DisplayName,
                    Icon = SkillVisual(r.Name).Icon,
                    Level = r.Level,
                    Experience = r.CurrentExperience
                })
                .ToList();
        }

        built.Skills = skillBoards;
        built.TotalLevels = Rank(profiles.Values
            .Where(p => p.TotalSkillLevels > 0)
            .OrderByDescending(p => p.TotalSkillLevels)
            .ThenByDescending(p => p.TotalSkillExperience)
            .ThenBy(p => p.Player, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .Select(p => new WebMapLeaderboardEntry
            {
                Player = p.Player,
                Online = p.Online,
                Value = p.TotalSkillLevels,
                DisplayValue = p.TotalSkillLevels.ToString("N0", CultureInfo.InvariantCulture)
            }));

        built.TotalExperience = Rank(profiles.Values
            .Where(p => p.TotalSkillExperience > 0)
            .OrderByDescending(p => p.TotalSkillExperience)
            .ThenByDescending(p => p.TotalSkillLevels)
            .ThenBy(p => p.Player, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .Select(p => new WebMapLeaderboardEntry
            {
                Player = p.Player,
                Online = p.Online,
                Value = p.TotalSkillExperience,
                DisplayValue = FormatNumber(p.TotalSkillExperience)
            }));

        int offlineSkillSets = rowsByUid.Keys.Count(uid => !actualOnlineUids.Contains(uid));
        built.XSkillsPlayerCount = rowsByUid.Count;
        built.XSkillsOfflinePlayerCount = offlineSkillSets;
        built.XSkillsStatus = $"XSkills/XLib detected. {skillDefs.Count} registered skills and {rowsByUid.Count} player skill sets are represented ({offlineSkillSets} offline).";
    }

    private bool EnsureProfileForUid(
        Dictionary<string, WebMapLeaderboardPlayerProfile> profiles,
        string uid,
        HashSet<string> actualOnlineUids)
    {
        uid = (uid ?? "").Trim();
        if (uid.Length == 0) return false;
        if (profiles.ContainsKey(uid)) return true;
        if (isPlayerHiddenProvider(uid)) return false;

        try
        {
            IServerPlayerData? playerData = api.PlayerData.GetPlayerDataByUid(uid);
            string name = (playerData?.LastKnownPlayername ?? "").Trim();
            if (name.Length == 0)
                name = (api.World.PlayerByUid(uid)?.PlayerName ?? "").Trim();
            if (name.Length == 0) return false;

            profiles[uid] = new WebMapLeaderboardPlayerProfile
            {
                Player = name,
                Online = actualOnlineUids.Contains(uid),
                FirstSeenUnixMilliseconds = ParsePlayerDataDate(playerData?.FirstJoinDate),
                LastSeenUnixMilliseconds = ParsePlayerDataDate(playerData?.LastJoinDate)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long ParsePlayerDataDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.ToUnixTimeMilliseconds();
        return 0;
    }

    private static Dictionary<string, SkillDefinition> ReadSkillDefinitions(object template)
    {
        var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        if (GetMemberValue(template, "Skills") is not IEnumerable skills) return result;
        foreach (object? skill in skills)
        {
            if (skill == null) continue;
            string name = GetString(skill, "Name") ?? "";
            if (name.Length == 0 || !GetBool(skill, "Enabled", true)) continue;
            string display = GetString(skill, "DisplayName") ?? name;
            int min = Math.Max(0, GetInt(skill, "MinLevel", 1));
            int max = Math.Clamp(GetInt(skill, "MaxLevel", Math.Max(min, 100)), min, 1000);
            result[name] = new SkillDefinition(name, display, min, BuildCumulativeExperienceTable(skill, min, max));
        }
        return result;
    }

    private static Dictionary<string, XSkillRow> CreateBaseRows(Dictionary<string, SkillDefinition> defs)
    {
        var rows = new Dictionary<string, XSkillRow>(StringComparer.OrdinalIgnoreCase);
        foreach (SkillDefinition def in defs.Values)
            rows[def.Name] = BuildSkillRow(def, def.Name, def.MinLevel, 0);
        return rows;
    }

    private static XSkillRow BuildSkillRow(SkillDefinition? def, string fallbackName, int level, double currentExperience)
    {
        string name = def?.Name ?? fallbackName;
        string display = def?.DisplayName ?? fallbackName;
        int min = def?.MinLevel ?? 1;
        double completedExperience = 0;

        if (def != null && level > min)
        {
            if (!def.CumulativeExperienceByLevel.TryGetValue(level, out completedExperience))
            {
                completedExperience = def.CumulativeExperienceByLevel
                    .Where(pair => pair.Key <= level)
                    .OrderByDescending(pair => pair.Key)
                    .Select(pair => pair.Value)
                    .FirstOrDefault();
            }
        }

        double total = Math.Max(0, completedExperience) + Math.Max(0, currentExperience);
        return new XSkillRow(name, display, min, level, currentExperience, total);
    }

    private static Dictionary<int, double> BuildCumulativeExperienceTable(object skill, int minLevel, int maxLevel)
    {
        var result = new Dictionary<int, double> { [minLevel] = 0 };
        MethodInfo? method = skill.GetType().GetMethod(
            "GetRequiredExperience",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(int) },
            null);
        if (method == null) return result;

        double cumulative = 0;
        for (int targetLevel = minLevel + 1; targetLevel <= maxLevel; targetLevel++)
        {
            try
            {
                object? required = method.Invoke(skill, new object[] { targetLevel });
                cumulative += Math.Max(0, Convert.ToDouble(required, CultureInfo.InvariantCulture));
                result[targetLevel] = cumulative;
            }
            catch
            {
                break;
            }
        }
        return result;
    }

    private static List<WebMapLeaderboardEntry> Rank(IEnumerable<WebMapLeaderboardEntry> source)
    {
        int rank = 1;
        return source.Select(row =>
        {
            row.Rank = rank++;
            return row;
        }).ToList();
    }

    private static IEnumerable<(object? Key, object? Value)> EnumerateDictionary(object? value)
    {
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) yield return (entry.Key, entry.Value);
            yield break;
        }
        if (value is not IEnumerable enumerable) yield break;
        foreach (object? entry in enumerable)
        {
            if (entry == null) continue;
            yield return (GetMemberValue(entry, "Key"), GetMemberValue(entry, "Value"));
        }
    }

    private static object? GetMemberValue(object? target, string name)
    {
        if (target == null) return null;
        Type type = target.GetType();
        try
        {
            PropertyInfo? prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null) return prop.GetValue(target);
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(target);
        }
        catch { return null; }
    }

    private static string? GetString(object? target, string name) => Convert.ToString(GetMemberValue(target, name), CultureInfo.InvariantCulture);
    private static int GetInt(object? target, string name, int fallback)
    {
        try { object? v = GetMemberValue(target, name); return v == null ? fallback : Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }
    private static double GetDouble(object? target, string name, double fallback)
    {
        try { object? v = GetMemberValue(target, name); return v == null ? fallback : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }
    private static bool GetBool(object? target, string name, bool fallback)
    {
        try { object? v = GetMemberValue(target, name); return v == null ? fallback : Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    private static (string Icon, string Accent) SkillVisual(string? skillName)
    {
        string key = (skillName ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        return key switch
        {
            "brewing" => ("⚗", "#dc5d5d"),
            "combat" => ("⚔", "#38bdf8"),
            "cooking" => ("♨", "#f59e0b"),
            "digging" => ("⛏", "#d7a86e"),
            "farming" => ("🌱", "#84cc16"),
            "fishing" => ("🐟", "#22d3ee"),
            "forestry" => ("🪵", "#4ade80"),
            "husbandry" => ("🐄", "#facc15"),
            "knapping" => ("◆", "#93c5fd"),
            "metalworking" => ("⚒", "#a78bfa"),
            "mining" => ("⛏", "#d1d5db"),
            "pottery" => ("🏺", "#fb923c"),
            "riding" => ("🐎", "#ef4444"),
            "sailing" => ("⛵", "#38bdf8"),
            "survival" => ("🔥", "#f59e0b"),
            "tailoring" => ("🧵", "#d946ef"),
            "temporaladaptation" => ("⌛", "#22d3ee"),
            _ => ("★", "#60a5fa")
        };
    }

    private static string FormatNumber(double value)
    {
        if (value >= 1_000_000_000) return (value / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        if (value >= 1_000_000) return (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (value >= 1_000) return (value / 1_000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
        return Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatDuration(long seconds)
    {
        seconds = Math.Max(0, seconds);
        long hours = seconds / 3600;
        long minutes = (seconds % 3600) / 60;
        if (hours >= 24)
        {
            long days = hours / 24;
            long remHours = hours % 24;
            return $"{days:N0}d {remHours}h";
        }
        if (hours > 0) return $"{hours:N0}h {minutes}m";
        return $"{Math.Max(0, minutes):N0}m";
    }

    private static bool IsPubliclyVisible(AdminPlayerSummary player, HashSet<string> publicVisibleUids)
    {
        string uid = (player.PlayerUid ?? "").Trim();
        return uid.Length > 0 && publicVisibleUids.Contains(uid);
    }

    private static string ProfileKey(AdminPlayerSummary player)
    {
        string uid = (player.PlayerUid ?? "").Trim();
        return uid.Length > 0 ? uid : "__name__:" + (player.Name ?? "").Trim();
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
    }

    private sealed record SkillDefinition(string Name, string DisplayName, int MinLevel, Dictionary<int, double> CumulativeExperienceByLevel);
    private sealed record XSkillRow(string Name, string DisplayName, int MinLevel, int Level, double CurrentExperience, double TotalExperience);
}

public sealed class WebMapLeaderboardSnapshot
{
    public bool Enabled { get; set; }
    public long GeneratedUnixMilliseconds { get; set; }
    public int RefreshSeconds { get; set; } = 60;
    public int TopCount { get; set; } = 5;
    public int PlayerCount { get; set; }
    public int OfflinePlayerCount { get; set; }
    public int XSkillsPlayerCount { get; set; }
    public int XSkillsOfflinePlayerCount { get; set; }
    public bool PlaytimeEnabled { get; set; }
    public bool XSkillsEnabled { get; set; }
    public bool XSkillsDetected { get; set; }
    public string XSkillsStatus { get; set; } = "";
    public List<WebMapLeaderboardEntry> Playtime { get; set; } = new();
    public List<WebMapLeaderboardEntry> CurrentSession { get; set; } = new();
    public List<WebMapLeaderboardEntry> Veterans { get; set; } = new();
    public List<WebMapLeaderboardEntry> TotalLevels { get; set; } = new();
    public List<WebMapLeaderboardEntry> TotalExperience { get; set; } = new();
    public List<WebMapSkillLeaderboard> Skills { get; set; } = new();
    public List<WebMapLeaderboardPlayerProfile> Players { get; set; } = new();

    public static WebMapLeaderboardSnapshot Disabled(string status) => new()
    {
        Enabled = false,
        GeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        XSkillsStatus = status ?? ""
    };
}

public sealed class WebMapLeaderboardEntry
{
    public int Rank { get; set; }
    public string Player { get; set; } = "";
    public bool Online { get; set; }
    public double Value { get; set; }
    public string DisplayValue { get; set; } = "";
    public int Level { get; set; }
    public double Experience { get; set; }
}

public sealed class WebMapSkillLeaderboard
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Icon { get; set; } = "★";
    public string Accent { get; set; } = "#60a5fa";
    public List<WebMapLeaderboardEntry> Entries { get; set; } = new();
}

public sealed class WebMapLeaderboardPlayerProfile
{
    public string Player { get; set; } = "";
    public bool Online { get; set; }
    public long TotalPlaySeconds { get; set; }
    public long SessionSeconds { get; set; }
    public long FirstSeenUnixMilliseconds { get; set; }
    public long LastSeenUnixMilliseconds { get; set; }
    public int TotalSkillLevels { get; set; }
    public double TotalSkillExperience { get; set; }
    public int SkillsLeveled { get; set; }
    public List<WebMapPlayerSkillSummary> TopSkills { get; set; } = new();
}

public sealed class WebMapPlayerSkillSummary
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Icon { get; set; } = "★";
    public int Level { get; set; }
    public double Experience { get; set; }
}
