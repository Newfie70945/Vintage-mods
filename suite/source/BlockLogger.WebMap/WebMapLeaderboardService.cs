using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using BlockLogger.Admin;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

public sealed class WebMapLeaderboardService : IDisposable
{
	private sealed record SkillDefinition(string Name, string DisplayName, int MinLevel, Dictionary<int, double> CumulativeExperienceByLevel);

	private sealed record XSkillRow(string Name, string DisplayName, int MinLevel, int Level, double CurrentExperience, double TotalExperience);

	private readonly ICoreServerAPI api;

	private readonly Func<IReadOnlyList<AdminPlayerSummary>?> adminPlayersProvider;

	private readonly Func<string, bool> isPlayerHiddenProvider;

	private readonly Func<HashSet<string>> publicVisiblePlayerUidsProvider;

	private readonly Func<WebMapConfig> configProvider;

	private readonly object gate = new object();

	private WebMapLeaderboardSnapshot snapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard cache has not been built yet.");

	private WebMapLeaderboardSnapshot publicSnapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard cache has not been built yet.");

	private long listenerId = -1L;

	private long lastRefreshUnixMilliseconds;

	private bool disposed;

	private volatile bool active;

	private int refreshQueued;

	public WebMapLeaderboardService(ICoreServerAPI api, Func<IReadOnlyList<AdminPlayerSummary>?> adminPlayersProvider, Func<string, bool> isPlayerHiddenProvider, Func<HashSet<string>> publicVisiblePlayerUidsProvider, Func<WebMapConfig> configProvider)
	{
		this.api = api;
		this.adminPlayersProvider = adminPlayersProvider;
		this.isPlayerHiddenProvider = isPlayerHiddenProvider;
		this.publicVisiblePlayerUidsProvider = publicVisiblePlayerUidsProvider;
		this.configProvider = configProvider;
	}

	public void Start()
	{
		if (!disposed && listenerId < 0)
		{
			active = true;
			listenerId = api.Event.RegisterGameTickListener((float _) =>
			{
				RefreshIfDue();
			}, 10000, 3000);
			QueueRefresh();
		}
	}

	public void Stop()
	{
		active = false;
		if (listenerId >= 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(listenerId);
			}
			catch
			{
			}
			listenerId = -1L;
		}
		lastRefreshUnixMilliseconds = 0L;
		lock (gate)
		{
			snapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard service is stopped.");
			publicSnapshot = WebMapLeaderboardSnapshot.Disabled("Leaderboard service is stopped.");
		}
	}

	public void RequestRefresh()
	{
		lastRefreshUnixMilliseconds = 0L;
		QueueRefresh();
	}

	private void QueueRefresh()
	{
		if (disposed || !active || Interlocked.Exchange(ref refreshQueued, 1) != 0)
		{
			return;
		}
		try
		{
			api.Event.EnqueueMainThreadTask(() =>
			{
				Interlocked.Exchange(ref refreshQueued, 0);
				if (!disposed && active)
				{
					RefreshNow();
				}
			}, "webmap-leaderboard-refresh");
		}
		catch
		{
			Interlocked.Exchange(ref refreshQueued, 0);
		}
	}

	public WebMapLeaderboardSnapshot GetSnapshot()
	{
		lock (gate)
		{
			return snapshot;
		}
	}

	public WebMapLeaderboardSnapshot GetPublicSnapshot()
	{
		lock (gate)
		{
			return publicSnapshot;
		}
	}

	private void RefreshIfDue()
	{
		if (active && !disposed)
		{
			WebMapConfig webMapConfig = configProvider();
			long num = NowMs();
			long num2 = Math.Max(15000L, (long)webMapConfig.LeaderboardRefreshSeconds * 1000L);
			if (num - lastRefreshUnixMilliseconds >= num2)
			{
				RefreshNow();
			}
		}
	}

	private void RefreshNow()
	{
		if (disposed || !active)
		{
			return;
		}
		WebMapConfig webMapConfig = configProvider();
		try
		{
			List<AdminPlayerSummary> list = (from g in (from p in adminPlayersProvider() ?? Array.Empty<AdminPlayerSummary>()
					where p != null && !string.IsNullOrWhiteSpace(p.Name)
					where string.IsNullOrWhiteSpace(p.PlayerUid) || !isPlayerHiddenProvider(p.PlayerUid)
					select p).GroupBy(ProfileKey, StringComparer.OrdinalIgnoreCase)
				select g.First()).ToList();
			HashSet<string> actualOnlineUids = (from p in list
				where p.Online && !string.IsNullOrWhiteSpace(p.PlayerUid)
				select p.PlayerUid.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			HashSet<string> publicVisibleUids = publicVisiblePlayerUidsProvider();
			int num = Math.Clamp(webMapConfig.LeaderboardTopPlayers, 3, 20);
			WebMapLeaderboardSnapshot webMapLeaderboardSnapshot = new WebMapLeaderboardSnapshot
			{
				Enabled = true,
				GeneratedUnixMilliseconds = NowMs(),
				RefreshSeconds = webMapConfig.LeaderboardRefreshSeconds,
				TopCount = num,
				PlayerCount = list.Count,
				PlaytimeEnabled = webMapConfig.LeaderboardShowPlaytime,
				XSkillsEnabled = webMapConfig.LeaderboardShowXSkills
			};
			if (webMapConfig.LeaderboardShowPlaytime)
			{
				webMapLeaderboardSnapshot.Playtime = Rank(from p in (from p in list
						where p.TotalPlaySeconds > 0
						orderby p.TotalPlaySeconds descending
						select p).ThenBy((AdminPlayerSummary p) => p.Name, StringComparer.OrdinalIgnoreCase).Take(num)
					select new WebMapLeaderboardEntry
					{
						Player = p.Name,
						Online = p.Online,
						Value = p.TotalPlaySeconds,
						DisplayValue = FormatDuration(p.TotalPlaySeconds)
					});
				webMapLeaderboardSnapshot.CurrentSession = Rank(from p in (from p in list
						where p.Online && p.SessionSeconds > 0
						orderby p.SessionSeconds descending
						select p).ThenBy((AdminPlayerSummary p) => p.Name, StringComparer.OrdinalIgnoreCase).Take(num)
					select new WebMapLeaderboardEntry
					{
						Player = p.Name,
						Online = true,
						Value = p.SessionSeconds,
						DisplayValue = FormatDuration(p.SessionSeconds)
					});
				webMapLeaderboardSnapshot.Veterans = Rank(from p in (from p in list
						where p.FirstSeenUnixMilliseconds > 0
						orderby p.FirstSeenUnixMilliseconds
						select p).ThenBy((AdminPlayerSummary p) => p.Name, StringComparer.OrdinalIgnoreCase).Take(num)
					select new WebMapLeaderboardEntry
					{
						Player = p.Name,
						Online = p.Online,
						Value = p.FirstSeenUnixMilliseconds,
						DisplayValue = DateTimeOffset.FromUnixTimeMilliseconds(p.FirstSeenUnixMilliseconds).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
					});
			}
			Dictionary<string, WebMapLeaderboardPlayerProfile> dictionary = list.ToDictionary(ProfileKey, (AdminPlayerSummary p) => new WebMapLeaderboardPlayerProfile
			{
				Player = p.Name,
				Online = p.Online,
				TotalPlaySeconds = p.TotalPlaySeconds,
				SessionSeconds = (p.Online ? p.SessionSeconds : 0),
				FirstSeenUnixMilliseconds = p.FirstSeenUnixMilliseconds,
				LastSeenUnixMilliseconds = p.LastSeenUnixMilliseconds
			}, StringComparer.OrdinalIgnoreCase);
			if (webMapConfig.LeaderboardShowXSkills)
			{
				PopulateXSkills(webMapLeaderboardSnapshot, dictionary, actualOnlineUids, num);
			}
			else
			{
				webMapLeaderboardSnapshot.XSkillsStatus = "XSkills leaderboards are disabled in the Web Map configuration.";
			}
			webMapLeaderboardSnapshot.PlayerCount = dictionary.Count;
			webMapLeaderboardSnapshot.OfflinePlayerCount = dictionary.Values.Count((WebMapLeaderboardPlayerProfile p) => !p.Online);
			webMapLeaderboardSnapshot.Players = (from p in dictionary.Values
				orderby p.TotalSkillLevels descending, p.TotalPlaySeconds descending
				select p).ThenBy((WebMapLeaderboardPlayerProfile p) => p.Player, StringComparer.OrdinalIgnoreCase).ToList();
			WebMapLeaderboardSnapshot webMapLeaderboardSnapshot2 = BuildPublicSnapshot(webMapLeaderboardSnapshot, dictionary, publicVisibleUids);
			lastRefreshUnixMilliseconds = webMapLeaderboardSnapshot.GeneratedUnixMilliseconds;
			lock (gate)
			{
				snapshot = webMapLeaderboardSnapshot;
				publicSnapshot = webMapLeaderboardSnapshot2;
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

	private static WebMapLeaderboardSnapshot BuildPublicSnapshot(WebMapLeaderboardSnapshot source, Dictionary<string, WebMapLeaderboardPlayerProfile> profilesByUid, HashSet<string> publicVisibleUids)
	{
		WebMapLeaderboardSnapshot webMapLeaderboardSnapshot = JsonSerializer.Deserialize<WebMapLeaderboardSnapshot>(JsonSerializer.Serialize(source)) ?? WebMapLeaderboardSnapshot.Disabled("Leaderboard cache clone failed.");
		HashSet<string> hashSet = (from pair in profilesByUid
			where publicVisibleUids.Contains(pair.Key)
			select pair.Value.Player into name
			where !string.IsNullOrWhiteSpace(name)
			select name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		ApplyBadges(webMapLeaderboardSnapshot.Playtime, hashSet);
		ApplyBadges(webMapLeaderboardSnapshot.Veterans, hashSet);
		ApplyBadges(webMapLeaderboardSnapshot.TotalLevels, hashSet);
		ApplyBadges(webMapLeaderboardSnapshot.TotalExperience, hashSet);
		foreach (WebMapSkillLeaderboard skill in webMapLeaderboardSnapshot.Skills)
		{
			ApplyBadges(skill.Entries, hashSet);
		}
		webMapLeaderboardSnapshot.CurrentSession = Rank(from p in (from pair in profilesByUid
				where publicVisibleUids.Contains(pair.Key) && pair.Value.SessionSeconds > 0
				select pair.Value into p
				orderby p.SessionSeconds descending
				select p).ThenBy((WebMapLeaderboardPlayerProfile p) => p.Player, StringComparer.OrdinalIgnoreCase).Take(source.TopCount)
			select new WebMapLeaderboardEntry
			{
				Player = p.Player,
				Online = true,
				Value = p.SessionSeconds,
				DisplayValue = FormatDuration(p.SessionSeconds)
			});
		foreach (WebMapLeaderboardPlayerProfile player in webMapLeaderboardSnapshot.Players)
		{
			player.Online = hashSet.Contains(player.Player);
			if (!player.Online)
			{
				player.SessionSeconds = 0L;
			}
		}
		webMapLeaderboardSnapshot.OfflinePlayerCount = webMapLeaderboardSnapshot.Players.Count((WebMapLeaderboardPlayerProfile p) => !p.Online);
		return webMapLeaderboardSnapshot;
		static void ApplyBadges(IEnumerable<WebMapLeaderboardEntry> entries, HashSet<string> visibleNames)
		{
			foreach (WebMapLeaderboardEntry entry in entries)
			{
				entry.Online = visibleNames.Contains(entry.Player);
			}
		}
	}

	private void PopulateXSkills(WebMapLeaderboardSnapshot built, Dictionary<string, WebMapLeaderboardPlayerProfile> profiles, HashSet<string> actualOnlineUids, int top)
	{
		object modSystem = api.ModLoader.GetModSystem("XLib.XLeveling.XLeveling");
		if (modSystem == null)
		{
			built.XSkillsDetected = false;
			built.XSkillsStatus = "XSkills/XLib not detected. Playtime leaderboards remain available.";
			return;
		}
		built.XSkillsDetected = true;
		object memberValue = GetMemberValue(modSystem, "IXLevelingAPI");
		object memberValue2 = GetMemberValue(modSystem, "SkillSetTemplate");
		if (memberValue == null || memberValue2 == null)
		{
			built.XSkillsStatus = "XSkills/XLib was detected, but its server skill interface was not available.";
			return;
		}
		Dictionary<string, SkillDefinition> dictionary = ReadSkillDefinitions(memberValue2);
		if (dictionary.Count == 0)
		{
			built.XSkillsStatus = "XSkills/XLib was detected, but no registered skills were exposed.";
			return;
		}
		Dictionary<string, Dictionary<string, XSkillRow>> dictionary2 = new Dictionary<string, Dictionary<string, XSkillRow>>(StringComparer.OrdinalIgnoreCase);
		object memberValue3 = GetMemberValue(memberValue, "DiscPlayerSkillSets");
		foreach (var item6 in EnumerateDictionary(memberValue3))
		{
			object item = item6.Key;
			object item2 = item6.Value;
			string text = Convert.ToString(item, CultureInfo.InvariantCulture) ?? "";
			if (text.Length == 0 || item2 == null || !EnsureProfileForUid(profiles, text, actualOnlineUids))
			{
				continue;
			}
			Dictionary<string, XSkillRow> dictionary3 = CreateBaseRows(dictionary);
			object memberValue4 = GetMemberValue(item2, "Skills");
			foreach (var item7 in EnumerateDictionary(memberValue4))
			{
				object item3 = item7.Key;
				object item4 = item7.Value;
				string text2 = Convert.ToString(item3, CultureInfo.InvariantCulture) ?? "";
				if (text2.Length != 0 && item4 != null)
				{
					SkillDefinition valueOrDefault = dictionary.GetValueOrDefault(text2);
					int level = Math.Max(valueOrDefault?.MinLevel ?? 1, GetInt(item4, "Level", valueOrDefault?.MinLevel ?? 1));
					double currentExperience = Math.Max(0.0, GetDouble(item4, "Experience", 0.0));
					dictionary3[text2] = BuildSkillRow(valueOrDefault, text2, level, currentExperience);
				}
			}
			dictionary2[text] = dictionary3;
		}
		object memberValue5 = GetMemberValue(memberValue, "PlayerSkillSets");
		foreach (var (obj, obj2) in EnumerateDictionary(memberValue5))
		{
			if (obj == null || obj2 == null)
			{
				continue;
			}
			string text3 = GetString(obj, "PlayerUID") ?? GetString(GetMemberValue(obj2, "Player"), "PlayerUID") ?? "";
			if (text3.Length == 0)
			{
				continue;
			}
			actualOnlineUids.Add(text3);
			if (!EnsureProfileForUid(profiles, text3, actualOnlineUids))
			{
				continue;
			}
			Dictionary<string, XSkillRow> dictionary4 = CreateBaseRows(dictionary);
			if (GetMemberValue(obj2, "PlayerSkills") is IEnumerable enumerable)
			{
				foreach (object item8 in enumerable)
				{
					if (item8 != null)
					{
						object memberValue6 = GetMemberValue(item8, "Skill");
						string text4 = GetString(memberValue6, "Name") ?? "";
						if (text4.Length != 0)
						{
							SkillDefinition valueOrDefault2 = dictionary.GetValueOrDefault(text4);
							int level2 = Math.Max(valueOrDefault2?.MinLevel ?? 1, GetInt(item8, "Level", valueOrDefault2?.MinLevel ?? 1));
							double currentExperience2 = Math.Max(0.0, GetDouble(item8, "Experience", 0.0));
							dictionary4[text4] = BuildSkillRow(valueOrDefault2, text4, level2, currentExperience2);
						}
					}
				}
			}
			dictionary2[text3] = dictionary4;
		}
		List<WebMapSkillLeaderboard> list = new List<WebMapSkillLeaderboard>();
		foreach (SkillDefinition item9 in dictionary.Values.OrderBy((SkillDefinition s) => s.DisplayName, StringComparer.OrdinalIgnoreCase))
		{
			List<(string, bool, XSkillRow)> list2 = new List<(string, bool, XSkillRow)>();
			foreach (KeyValuePair<string, WebMapLeaderboardPlayerProfile> profile in profiles)
			{
				if (dictionary2.TryGetValue(profile.Key, out var value) && value.TryGetValue(item9.Name, out var value2) && (value2.Level > item9.MinLevel || !(value2.CurrentExperience <= 0.0)))
				{
					list2.Add((profile.Value.Player, profile.Value.Online, value2));
				}
			}
			WebMapSkillLeaderboard item5 = new WebMapSkillLeaderboard
			{
				Name = item9.Name,
				DisplayName = item9.DisplayName,
				Icon = SkillVisual(item9.Name).Icon,
				Accent = SkillVisual(item9.Name).Accent,
				Entries = Rank(list2.OrderByDescending(((string Player, bool Online, XSkillRow Row) x) => x.Row.Level).ThenByDescending(((string Player, bool Online, XSkillRow Row) x) => x.Row.TotalExperience).ThenBy(((string Player, bool Online, XSkillRow Row) x) => x.Player, StringComparer.OrdinalIgnoreCase)
					.Take(top)
					.Select(((string Player, bool Online, XSkillRow Row) x) => new WebMapLeaderboardEntry
					{
						Player = x.Player,
						Online = x.Online,
						Level = x.Row.Level,
						Experience = x.Row.CurrentExperience,
						Value = x.Row.Level,
						DisplayValue = $"Lv. {x.Row.Level}"
					}))
			};
			list.Add(item5);
		}
		foreach (KeyValuePair<string, WebMapLeaderboardPlayerProfile> profile2 in profiles)
		{
			if (dictionary2.TryGetValue(profile2.Key, out var value3))
			{
				List<XSkillRow> list3 = value3.Values.Where((XSkillRow r) => r.Level > r.MinLevel || r.CurrentExperience > 0.0).ToList();
				profile2.Value.TotalSkillLevels = value3.Values.Sum((XSkillRow r) => Math.Max(0, r.Level));
				profile2.Value.TotalSkillExperience = value3.Values.Sum((XSkillRow r) => Math.Max(0.0, r.TotalExperience));
				profile2.Value.SkillsLeveled = list3.Count;
				profile2.Value.TopSkills = (from r in (from r in list3
						orderby r.Level descending, r.TotalExperience descending
						select r).ThenBy((XSkillRow r) => r.DisplayName, StringComparer.OrdinalIgnoreCase).Take(6)
					select new WebMapPlayerSkillSummary
					{
						Name = r.Name,
						DisplayName = r.DisplayName,
						Icon = SkillVisual(r.Name).Icon,
						Level = r.Level,
						Experience = r.CurrentExperience
					}).ToList();
			}
		}
		built.Skills = list;
		built.TotalLevels = Rank(from p in (from p in profiles.Values
				where p.TotalSkillLevels > 0
				orderby p.TotalSkillLevels descending, p.TotalSkillExperience descending
				select p).ThenBy((WebMapLeaderboardPlayerProfile p) => p.Player, StringComparer.OrdinalIgnoreCase).Take(top)
			select new WebMapLeaderboardEntry
			{
				Player = p.Player,
				Online = p.Online,
				Value = p.TotalSkillLevels,
				DisplayValue = p.TotalSkillLevels.ToString("N0", CultureInfo.InvariantCulture)
			});
		built.TotalExperience = Rank(from p in (from p in profiles.Values
				where p.TotalSkillExperience > 0.0
				orderby p.TotalSkillExperience descending, p.TotalSkillLevels descending
				select p).ThenBy((WebMapLeaderboardPlayerProfile p) => p.Player, StringComparer.OrdinalIgnoreCase).Take(top)
			select new WebMapLeaderboardEntry
			{
				Player = p.Player,
				Online = p.Online,
				Value = p.TotalSkillExperience,
				DisplayValue = FormatNumber(p.TotalSkillExperience)
			});
		int num = dictionary2.Keys.Count((string uid) => !actualOnlineUids.Contains(uid));
		built.XSkillsPlayerCount = dictionary2.Count;
		built.XSkillsOfflinePlayerCount = num;
		built.XSkillsStatus = $"XSkills/XLib detected. {dictionary.Count} registered skills and {dictionary2.Count} player skill sets are represented ({num} offline).";
	}

	private bool EnsureProfileForUid(Dictionary<string, WebMapLeaderboardPlayerProfile> profiles, string uid, HashSet<string> actualOnlineUids)
	{
		uid = (uid ?? "").Trim();
		if (uid.Length == 0)
		{
			return false;
		}
		if (profiles.ContainsKey(uid))
		{
			return true;
		}
		if (isPlayerHiddenProvider(uid))
		{
			return false;
		}
		try
		{
			IServerPlayerData playerDataByUid = api.PlayerData.GetPlayerDataByUid(uid);
			string text = (playerDataByUid?.LastKnownPlayername ?? "").Trim();
			if (text.Length == 0)
			{
				text = (api.World.PlayerByUid(uid)?.PlayerName ?? "").Trim();
			}
			if (text.Length == 0)
			{
				return false;
			}
			profiles[uid] = new WebMapLeaderboardPlayerProfile
			{
				Player = text,
				Online = actualOnlineUids.Contains(uid),
				FirstSeenUnixMilliseconds = ParsePlayerDataDate(playerDataByUid?.FirstJoinDate),
				LastSeenUnixMilliseconds = ParsePlayerDataDate(playerDataByUid?.LastJoinDate)
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
		if (string.IsNullOrWhiteSpace(value))
		{
			return 0L;
		}
		if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result))
		{
			return result.ToUnixTimeMilliseconds();
		}
		return 0L;
	}

	private static Dictionary<string, SkillDefinition> ReadSkillDefinitions(object template)
	{
		Dictionary<string, SkillDefinition> dictionary = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
		if (!(GetMemberValue(template, "Skills") is IEnumerable enumerable))
		{
			return dictionary;
		}
		foreach (object item in enumerable)
		{
			if (item != null)
			{
				string text = GetString(item, "Name") ?? "";
				if (text.Length != 0 && GetBool(item, "Enabled", fallback: true))
				{
					string displayName = GetString(item, "DisplayName") ?? text;
					int num = Math.Max(0, GetInt(item, "MinLevel", 1));
					int maxLevel = Math.Clamp(GetInt(item, "MaxLevel", Math.Max(num, 100)), num, 1000);
					dictionary[text] = new SkillDefinition(text, displayName, num, BuildCumulativeExperienceTable(item, num, maxLevel));
				}
			}
		}
		return dictionary;
	}

	private static Dictionary<string, XSkillRow> CreateBaseRows(Dictionary<string, SkillDefinition> defs)
	{
		Dictionary<string, XSkillRow> dictionary = new Dictionary<string, XSkillRow>(StringComparer.OrdinalIgnoreCase);
		foreach (SkillDefinition value in defs.Values)
		{
			dictionary[value.Name] = BuildSkillRow(value, value.Name, value.MinLevel, 0.0);
		}
		return dictionary;
	}

	private static XSkillRow BuildSkillRow(SkillDefinition? def, string fallbackName, int level, double currentExperience)
	{
		string name = def?.Name ?? fallbackName;
		string displayName = def?.DisplayName ?? fallbackName;
		int num = def?.MinLevel ?? 1;
		double value = 0.0;
		if (def != null && level > num && !def.CumulativeExperienceByLevel.TryGetValue(level, out value))
		{
			value = (from pair in def.CumulativeExperienceByLevel
				where pair.Key <= level
				orderby pair.Key descending
				select pair.Value).FirstOrDefault();
		}
		double totalExperience = Math.Max(0.0, value) + Math.Max(0.0, currentExperience);
		return new XSkillRow(name, displayName, num, level, currentExperience, totalExperience);
	}

	private static Dictionary<int, double> BuildCumulativeExperienceTable(object skill, int minLevel, int maxLevel)
	{
		Dictionary<int, double> dictionary = new Dictionary<int, double> { [minLevel] = 0.0 };
		MethodInfo method = skill.GetType().GetMethod("GetRequiredExperience", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(int) }, null);
		if (method == null)
		{
			return dictionary;
		}
		double num = 0.0;
		for (int i = minLevel + 1; i <= maxLevel; i++)
		{
			try
			{
				object value = method.Invoke(skill, new object[1] { i });
				num = (dictionary[i] = num + Math.Max(0.0, Convert.ToDouble(value, CultureInfo.InvariantCulture)));
			}
			catch
			{
				break;
			}
		}
		return dictionary;
	}

	private static List<WebMapLeaderboardEntry> Rank(IEnumerable<WebMapLeaderboardEntry> source)
	{
		int rank = 1;
		return source.Select((WebMapLeaderboardEntry row) =>
		{
			row.Rank = rank++;
			return row;
		}).ToList();
	}

	private static IEnumerable<(object? Key, object? Value)> EnumerateDictionary(object? value)
	{
		if (value is IDictionary dictionary)
		{
			foreach (DictionaryEntry entry in dictionary)
			{
				yield return (Key: entry.Key, Value: entry.Value);
			}
		}
		else
		{
			if (!(value is IEnumerable enumerable))
			{
				yield break;
			}
			foreach (object entry2 in enumerable)
			{
				if (entry2 != null)
				{
					yield return (Key: GetMemberValue(entry2, "Key"), Value: GetMemberValue(entry2, "Value"));
				}
			}
		}
	}

	private static object? GetMemberValue(object? target, string name)
	{
		if (target == null)
		{
			return null;
		}
		Type type = target.GetType();
		try
		{
			PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null)
			{
				return property.GetValue(target);
			}
			return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
		}
		catch
		{
			return null;
		}
	}

	private static string? GetString(object? target, string name)
	{
		return Convert.ToString(GetMemberValue(target, name), CultureInfo.InvariantCulture);
	}

	private static int GetInt(object? target, string name, int fallback)
	{
		try
		{
			object memberValue = GetMemberValue(target, name);
			return (memberValue == null) ? fallback : Convert.ToInt32(memberValue, CultureInfo.InvariantCulture);
		}
		catch
		{
			return fallback;
		}
	}

	private static double GetDouble(object? target, string name, double fallback)
	{
		try
		{
			object memberValue = GetMemberValue(target, name);
			return (memberValue == null) ? fallback : Convert.ToDouble(memberValue, CultureInfo.InvariantCulture);
		}
		catch
		{
			return fallback;
		}
	}

	private static bool GetBool(object? target, string name, bool fallback)
	{
		try
		{
			object memberValue = GetMemberValue(target, name);
			return (memberValue == null) ? fallback : Convert.ToBoolean(memberValue, CultureInfo.InvariantCulture);
		}
		catch
		{
			return fallback;
		}
	}

	private static (string Icon, string Accent) SkillVisual(string? skillName)
	{
		string text = (skillName ?? "").Trim().ToLowerInvariant().Replace(" ", "")
			.Replace("_", "")
			.Replace("-", "");
		if (1 == 0)
		{
		}
		(string, string) result = text switch
		{
			"brewing" => ("⚗", "#dc5d5d"), 
			"combat" => ("⚔", "#38bdf8"), 
			"cooking" => ("♨", "#f59e0b"), 
			"digging" => ("⛏", "#d7a86e"), 
			"farming" => ("\ud83c\udf31", "#84cc16"), 
			"fishing" => ("\ud83d\udc1f", "#22d3ee"), 
			"forestry" => ("\ud83e\udeb5", "#4ade80"), 
			"husbandry" => ("\ud83d\udc04", "#facc15"), 
			"knapping" => ("◆", "#93c5fd"), 
			"metalworking" => ("⚒", "#a78bfa"), 
			"mining" => ("⛏", "#d1d5db"), 
			"pottery" => ("\ud83c\udffa", "#fb923c"), 
			"riding" => ("\ud83d\udc0e", "#ef4444"), 
			"sailing" => ("⛵", "#38bdf8"), 
			"survival" => ("\ud83d\udd25", "#f59e0b"), 
			"tailoring" => ("\ud83e\uddf5", "#d946ef"), 
			"temporaladaptation" => ("⌛", "#22d3ee"), 
			_ => ("★", "#60a5fa"), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string FormatNumber(double value)
	{
		if (value >= 1000000000.0)
		{
			return (value / 1000000000.0).ToString("0.##", CultureInfo.InvariantCulture) + "B";
		}
		if (value >= 1000000.0)
		{
			return (value / 1000000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M";
		}
		if (value >= 1000.0)
		{
			return (value / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + "K";
		}
		return Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);
	}

	private static string FormatDuration(long seconds)
	{
		seconds = Math.Max(0L, seconds);
		long num = seconds / 3600;
		long num2 = seconds % 3600 / 60;
		if (num >= 24)
		{
			long value = num / 24;
			long value2 = num % 24;
			return $"{value:N0}d {value2}h";
		}
		if (num <= 0)
		{
			return $"{Math.Max(0L, num2):N0}m";
		}
		return $"{num:N0}h {num2}m";
	}

	private static bool IsPubliclyVisible(AdminPlayerSummary player, HashSet<string> publicVisibleUids)
	{
		string text = (player.PlayerUid ?? "").Trim();
		return text.Length > 0 && publicVisibleUids.Contains(text);
	}

	private static string ProfileKey(AdminPlayerSummary player)
	{
		string text = (player.PlayerUid ?? "").Trim();
		return (text.Length > 0) ? text : ("__name__:" + (player.Name ?? "").Trim());
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			Stop();
		}
	}
}
