using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Announcements;

public sealed class AnnouncementService : IDisposable
{
	private const string ConfigFile = "NewfiesAnnouncements.json";

	private readonly ICoreServerAPI api;

	private readonly object sync = new object();

	private AnnouncementConfig config = new AnnouncementConfig();

	private long tickListenerId = -1L;

	private bool disposed;

	public AnnouncementService(ICoreServerAPI api)
	{
		this.api = api;
	}

	public void Start()
	{
		Load();
		RecalculateAllNextRuns(DateTime.Now, onlyMissing: false);
		Save();
		tickListenerId = api.Event.RegisterGameTickListener((float _) =>
		{
			Tick();
		}, 10000);
		api.Logger.Notification("[ServerEssentials/Announcements] Loaded {0} announcement rule(s), {1} enabled.", config.Rules.Count, config.Rules.Count((AnnouncementRule x) => x.Enabled));
	}

	public AnnouncementSnapshot GetSnapshot()
	{
		lock (sync)
		{
			AnnouncementSnapshot announcementSnapshot = new AnnouncementSnapshot();
			announcementSnapshot.Enabled = config.Enabled;
			announcementSnapshot.Prefix = config.Prefix;
			announcementSnapshot.Timezone = TimeZoneInfo.Local.DisplayName;
			announcementSnapshot.EnabledRules = config.Rules.Count((AnnouncementRule x) => x.Enabled);
			announcementSnapshot.TotalRules = config.Rules.Count;
			announcementSnapshot.Rules = (from x in config.Rules.Select(Clone)
				orderby (x.NextRunUnixMilliseconds <= 0) ? long.MaxValue : x.NextRunUnixMilliseconds
				select x).ToList();
			return announcementSnapshot;
		}
	}

	public string SaveFromJson(string body, JsonSerializerOptions options)
	{
		AnnouncementConfig announcementConfig;
		try
		{
			announcementConfig = JsonSerializer.Deserialize<AnnouncementConfig>(body, options);
		}
		catch (Exception ex)
		{
			return "Invalid announcement configuration: " + ex.Message;
		}
		if (announcementConfig == null)
		{
			return "Announcement configuration is required.";
		}
		lock (sync)
		{
			Normalize(announcementConfig);
			Dictionary<string, AnnouncementRule> dictionary = config.Rules.ToDictionary((AnnouncementRule x) => x.Id, StringComparer.OrdinalIgnoreCase);
			foreach (AnnouncementRule rule in announcementConfig.Rules)
			{
				if (dictionary.TryGetValue(rule.Id, out var value))
				{
					rule.RunsCompleted = value.RunsCompleted;
					rule.LastRunUnixMilliseconds = value.LastRunUnixMilliseconds;
				}
			}
			config = announcementConfig;
			RecalculateAllNextRuns(DateTime.Now, onlyMissing: false);
			SaveUnsafe();
			return $"Saved {config.Rules.Count} announcement rule(s).";
		}
	}

	public string RunNow(string id)
	{
		lock (sync)
		{
			AnnouncementRule announcementRule = config.Rules.FirstOrDefault((AnnouncementRule x) => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
			if (announcementRule == null)
			{
				return "Announcement rule was not found.";
			}
			return BroadcastUnsafe(announcementRule, manual: true);
		}
	}

	public string ResetRuns(string id)
	{
		lock (sync)
		{
			AnnouncementRule announcementRule = config.Rules.FirstOrDefault((AnnouncementRule x) => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
			if (announcementRule == null)
			{
				return "Announcement rule was not found.";
			}
			announcementRule.RunsCompleted = 0;
			announcementRule.LastRunUnixMilliseconds = 0L;
			announcementRule.Enabled = true;
			announcementRule.NextRunUnixMilliseconds = CalculateNextRun(announcementRule, DateTime.Now);
			SaveUnsafe();
			return "Reset run counter for " + announcementRule.Name + ".";
		}
	}

	private void Tick()
	{
		if (disposed)
		{
			return;
		}
		lock (sync)
		{
			if (!config.Enabled)
			{
				return;
			}
			long num = DateTimeOffset.Now.ToUnixTimeMilliseconds();
			bool flag = false;
			foreach (AnnouncementRule item in config.Rules.Where((AnnouncementRule x) => x.Enabled).ToList())
			{
				if (item.MaxRuns > 0 && item.RunsCompleted >= item.MaxRuns)
				{
					item.Enabled = false;
					item.NextRunUnixMilliseconds = 0L;
					flag = true;
				}
				else if (item.NextRunUnixMilliseconds <= 0)
				{
					item.NextRunUnixMilliseconds = CalculateNextRun(item, DateTime.Now);
					flag = true;
				}
				else if (num >= item.NextRunUnixMilliseconds)
				{
					BroadcastUnsafe(item, manual: false);
					flag = true;
				}
			}
			if (flag)
			{
				SaveUnsafe();
			}
		}
	}

	private string BroadcastUnsafe(AnnouncementRule rule, bool manual)
	{
		if (string.IsNullOrWhiteSpace(rule.Message))
		{
			return "Announcement message is empty.";
		}
		string text = Expand(rule.Message);
		string text2 = (string.IsNullOrWhiteSpace(config.Prefix) ? "" : (config.Prefix.Trim() + " "));
		api.BroadcastMessageToAllGroups(text2 + text, EnumChatType.Notification);
		long lastRunUnixMilliseconds = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		rule.LastRunUnixMilliseconds = lastRunUnixMilliseconds;
		if (!manual)
		{
			rule.RunsCompleted++;
		}
		if (!manual && rule.MaxRuns > 0 && rule.RunsCompleted >= rule.MaxRuns)
		{
			rule.Enabled = false;
			rule.NextRunUnixMilliseconds = 0L;
		}
		else
		{
			rule.NextRunUnixMilliseconds = CalculateNextRun(rule, DateTime.Now.AddSeconds(1.0));
		}
		SaveUnsafe();
		return manual ? ("Sent test announcement: " + rule.Name + ".") : ("Sent announcement: " + rule.Name + ".");
	}

	private string Expand(string message)
	{
		int num = 0;
		try
		{
			num = api.World.AllOnlinePlayers?.Length ?? 0;
		}
		catch
		{
		}
		return (message ?? "").Replace("{players}", num.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase).Replace("{time}", DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase).Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
	}

	private void RecalculateAllNextRuns(DateTime now, bool onlyMissing)
	{
		foreach (AnnouncementRule rule in config.Rules)
		{
			if (rule.MaxRuns > 0 && rule.RunsCompleted >= rule.MaxRuns)
			{
				rule.Enabled = false;
				rule.NextRunUnixMilliseconds = 0L;
			}
			else if (!onlyMissing || rule.NextRunUnixMilliseconds <= 0)
			{
				rule.NextRunUnixMilliseconds = (rule.Enabled ? CalculateNextRun(rule, now) : 0);
			}
		}
	}

	private static long CalculateNextRun(AnnouncementRule rule, DateTime now)
	{
		if (!rule.Enabled)
		{
			return 0L;
		}
		if (string.Equals(rule.Mode, "daily", StringComparison.OrdinalIgnoreCase))
		{
			List<TimeSpan> list = ParseTimes(rule.DailyTimes);
			if (list.Count == 0)
			{
				list.Add(new TimeSpan(12, 0, 0));
			}
			foreach (TimeSpan item in list)
			{
				DateTime dateTime = now.Date + item;
				if (dateTime > now)
				{
					return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds();
				}
			}
			return new DateTimeOffset(now.Date.AddDays(1.0) + list[0]).ToUnixTimeMilliseconds();
		}
		int num = Math.Clamp(rule.IntervalMinutes, 1, 10080);
		if (TryTime(rule.StartTime, out var time))
		{
			DateTime dateTime2 = now.Date + time;
			if (dateTime2 > now)
			{
				return new DateTimeOffset(dateTime2).ToUnixTimeMilliseconds();
			}
			double totalMinutes = (now - dateTime2).TotalMinutes;
			double num2 = Math.Floor(totalMinutes / (double)num) + 1.0;
			return new DateTimeOffset(dateTime2.AddMinutes(num2 * (double)num)).ToUnixTimeMilliseconds();
		}
		return new DateTimeOffset(now.AddMinutes(num)).ToUnixTimeMilliseconds();
	}

	private void Load()
	{
		config = ModConfigRecovery.LoadOrNull<AnnouncementConfig>(api, "NewfiesAnnouncements.json", "ServerEssentials/Announcements") ?? new AnnouncementConfig();
		Normalize(config);
	}

	private static void Normalize(AnnouncementConfig cfg)
	{
		cfg.SchemaVersion = Math.Max(1, cfg.SchemaVersion);
		cfg.Prefix = (cfg.Prefix ?? "").Trim();
		if (cfg.Prefix.Length > 80)
		{
			cfg.Prefix = cfg.Prefix.Substring(0, 80);
		}
		if (cfg.Rules == null)
		{
			List<AnnouncementRule> list = (cfg.Rules = new List<AnnouncementRule>());
		}
		if (cfg.Rules.Count > 100)
		{
			cfg.Rules = cfg.Rules.Take(100).ToList();
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (AnnouncementRule rule in cfg.Rules)
		{
			rule.Id = (rule.Id ?? "").Trim();
			if (rule.Id.Length == 0 || !hashSet.Add(rule.Id))
			{
				do
				{
					rule.Id = Guid.NewGuid().ToString("N").Substring(0, 10);
				}
				while (!hashSet.Add(rule.Id));
			}
			rule.Name = (string.IsNullOrWhiteSpace(rule.Name) ? "Announcement" : rule.Name.Trim());
			if (rule.Name.Length > 80)
			{
				rule.Name = rule.Name.Substring(0, 80);
			}
			rule.Message = (rule.Message ?? "").Trim();
			if (rule.Message.Length > 1000)
			{
				rule.Message = rule.Message.Substring(0, 1000);
			}
			rule.Mode = (string.Equals(rule.Mode, "daily", StringComparison.OrdinalIgnoreCase) ? "daily" : "interval");
			rule.IntervalMinutes = Math.Clamp(rule.IntervalMinutes, 1, 10080);
			rule.StartTime = NormalizeTime(rule.StartTime);
			AnnouncementRule announcementRule = rule;
			if (announcementRule.DailyTimes == null)
			{
				List<string> list3 = (announcementRule.DailyTimes = new List<string>());
			}
			rule.DailyTimes = (from x in (from x in rule.DailyTimes.Select(NormalizeTime)
					where x.Length > 0
					select x).Distinct()
				orderby x
				select x).Take(24).ToList();
			rule.MaxRuns = Math.Clamp(rule.MaxRuns, 0, 1000000);
			rule.RunsCompleted = Math.Max(0, rule.RunsCompleted);
		}
	}

	private static string NormalizeTime(string? value)
	{
		TimeSpan time;
		return TryTime(value, out time) ? $"{time.Hours:00}:{time.Minutes:00}" : "";
	}

	private static bool TryTime(string? value, out TimeSpan time)
	{
		return TimeSpan.TryParseExact((value ?? "").Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out time) && time.TotalHours < 24.0;
	}

	private static List<TimeSpan> ParseTimes(IEnumerable<string>? values)
	{
		return (from x in (from x in values ?? Array.Empty<string>()
				select TryTime(x, out var time) ? new TimeSpan?(time) : ((TimeSpan?)null) into x
				where x.HasValue
				select x.Value).Distinct()
			orderby x
			select x).ToList();
	}

	private static AnnouncementRule Clone(AnnouncementRule r)
	{
		return new AnnouncementRule
		{
			Id = r.Id,
			Name = r.Name,
			Enabled = r.Enabled,
			Message = r.Message,
			Mode = r.Mode,
			IntervalMinutes = r.IntervalMinutes,
			StartTime = r.StartTime,
			DailyTimes = r.DailyTimes.ToList(),
			MaxRuns = r.MaxRuns,
			RunsCompleted = r.RunsCompleted,
			LastRunUnixMilliseconds = r.LastRunUnixMilliseconds,
			NextRunUnixMilliseconds = r.NextRunUnixMilliseconds
		};
	}

	private void Save()
	{
		lock (sync)
		{
			SaveUnsafe();
		}
	}

	private void SaveUnsafe()
	{
		try
		{
			SuiteConfigStorage.Store(api, config, "NewfiesAnnouncements.json");
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Announcements] Could not save config: {0}: {1}", ex.GetType().Name, ex.Message);
		}
	}

	public void Dispose()
	{
		disposed = true;
		if (tickListenerId >= 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(tickListenerId);
			}
			catch
			{
			}
			tickListenerId = -1L;
		}
		Save();
	}
}
