using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Restarts;

public sealed class AutomatedRestartService : IDisposable
{
	public const string DiscordRestartNoticeEvent = "newfiesse:restart-notice";

	public const string DiscordRestartQueryEvent = "newfiesse:restart-query";

	private readonly ICoreServerAPI api;

	private AutomatedRestartConfig config;

	private long tickListenerId = -1L;

	private DateTime nextRestartUtc = DateTime.MinValue;

	private DateTime? skippedOccurrenceUtc;

	private long previousRemainingMilliseconds = long.MaxValue;

	private readonly HashSet<int> sentWarnings = new HashSet<int>();

	private DateTime customRestartUtc = DateTime.MinValue;

	private string customRestartReason = "";

	private long customPreviousRemainingMilliseconds = long.MaxValue;

	private readonly HashSet<int> customSentWarnings = new HashSet<int>();

	private bool disposed;

	private bool rootRestartTimeOwned;

	private bool shutdownTriggered;

	private bool HasTh3Essentials => api.ModLoader.Mods.Any((Mod mod) => mod?.Info != null && string.Equals(mod.Info.ModID, "th3essentials", StringComparison.OrdinalIgnoreCase));

	public AutomatedRestartConfig Config => config;

	public AutomatedRestartService(ICoreServerAPI api)
	{
		this.api = api;
		config = AutomatedRestartConfig.Load(api);
	}

	public void Start()
	{
		RecalculateNext(DateTime.UtcNow);
		tickListenerId = api.Event.RegisterGameTickListener((float _) =>
		{
			Tick();
		}, 1000);
		RegisterRestartTimeCommand();
		api.Event.RegisterEventBusListener(OnRestartQuery, 0.5, "newfiesse:restart-query");
		api.Logger.Notification("[ServerEssentials/Restarts] Automated restart scheduler started. Enabled={0}, TimesUTC={1}, Warnings={2}.", config.Enabled, string.Join(",", config.EffectiveTimesUtc()), string.Join(",", config.WarningMinutes));
	}

	public AutomatedRestartSnapshot GetSnapshot()
	{
		DateTime utcNow = DateTime.UtcNow;
		if (config.Enabled && (nextRestartUtc == DateTime.MinValue || nextRestartUtc <= utcNow.AddMilliseconds(-1500.0)))
		{
			RecalculateNext(utcNow);
		}
		long remainingSeconds = ((config.Enabled && nextRestartUtc > utcNow) ? Math.Max(0L, (long)(nextRestartUtc - utcNow).TotalSeconds) : 0);
		string rootRestartTimeOwner;
		if (HasTh3Essentials)
		{
			rootRestartTimeOwner = "Th3Essentials";
		}
		else if (rootRestartTimeOwned)
		{
			rootRestartTimeOwner = "Newfies Server Essentials";
		}
		else
		{
			rootRestartTimeOwner = (RootCommandExists("restarttime") ? "Existing provider" : "Unregistered");
		}
		long customRestartRemainingSeconds = ((customRestartUtc > utcNow) ? Math.Max(0L, (long)(customRestartUtc - utcNow).TotalSeconds) : 0);
		return new AutomatedRestartSnapshot
		{
			Enabled = config.Enabled,
			RestartTimeUtc = config.RestartTimeUtc,
			RestartTimesUtc = config.RestartTimesUtc.ToList(),
			EffectiveRestartTimesUtc = config.EffectiveTimesUtc().ToList(),
			WarningMinutes = config.WarningMinutes.ToList(),
			AnnounceInGame = config.AnnounceInGame,
			AnnounceToDiscord = config.AnnounceToDiscord,
			Reason = config.Reason,
			NextRestartUnixMilliseconds = ((config.Enabled && nextRestartUtc != DateTime.MinValue) ? new DateTimeOffset(DateTime.SpecifyKind(nextRestartUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : 0),
			RemainingSeconds = remainingSeconds,
			NextRestartUtc = ((config.Enabled && nextRestartUtc != DateTime.MinValue) ? nextRestartUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : ""),
			CustomRestartScheduled = (customRestartUtc > utcNow),
			CustomRestartUnixMilliseconds = ((customRestartUtc > utcNow) ? new DateTimeOffset(DateTime.SpecifyKind(customRestartUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : 0),
			CustomRestartRemainingSeconds = customRestartRemainingSeconds,
			CustomRestartUtc = ((customRestartUtc > utcNow) ? customRestartUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : ""),
			CustomRestartReason = ((customRestartUtc > utcNow) ? customRestartReason : ""),
			Th3EssentialsDetected = HasTh3Essentials,
			RootRestartTimeOwner = rootRestartTimeOwner
		};
	}

	public (bool Success, string Message, AutomatedRestartSnapshot Snapshot) SaveSettings(AutomatedRestartSettingsRequest? request)
	{
		if (request == null)
		{
			return (Success: false, Message: "Automated restart settings are required.", Snapshot: GetSnapshot());
		}
		AutomatedRestartConfig automatedRestartConfig = new AutomatedRestartConfig
		{
			SchemaVersion = 1,
			Enabled = request.Enabled,
			RestartTimeUtc = request.RestartTimeUtc,
			RestartTimesUtc = (request.RestartTimesUtc ?? new List<string>()),
			WarningMinutes = (request.WarningMinutes ?? new List<int>()),
			AnnounceInGame = request.AnnounceInGame,
			AnnounceToDiscord = request.AnnounceToDiscord,
			Reason = request.Reason
		};
		List<string> list = automatedRestartConfig.RestartTimesUtc.Where((string x) => !string.IsNullOrWhiteSpace(x)).ToList();
		string normalized;
		foreach (string item in list)
		{
			if (!AutomatedRestartConfig.TryNormalizeTime(item, out normalized))
			{
				return (Success: false, Message: "Invalid UTC restart time '" + item + "'. Use HH:mm or HH:mm:ss.", Snapshot: GetSnapshot());
			}
		}
		if (list.Count == 0 && !AutomatedRestartConfig.TryNormalizeTime(automatedRestartConfig.RestartTimeUtc, out normalized))
		{
			return (Success: false, Message: "Invalid UTC restart time. Use HH:mm or HH:mm:ss.", Snapshot: GetSnapshot());
		}
		automatedRestartConfig.Clamp();
		config = automatedRestartConfig;
		skippedOccurrenceUtc = null;
		SuiteConfigStorage.Store(api, config, "NewfiesAutomatedRestarts.json");
		RecalculateNext(DateTime.UtcNow);
		api.Logger.Notification("[ServerEssentials/Restarts] Settings saved. Enabled={0}, TimesUTC={1}.", config.Enabled, string.Join(",", config.EffectiveTimesUtc()));
		return (Success: true, Message: "Automated restart settings saved and applied immediately.", Snapshot: GetSnapshot());
	}

	public string SetEnabled(bool enabled)
	{
		config.Enabled = enabled;
		SuiteConfigStorage.Store(api, config, "NewfiesAutomatedRestarts.json");
		skippedOccurrenceUtc = null;
		RecalculateNext(DateTime.UtcNow);
		return enabled ? ("Automated restarts enabled. Next restart: " + GetSnapshot().NextRestartUtc + ".") : "Automated restarts disabled.";
	}

	public string Reload()
	{
		config = AutomatedRestartConfig.Load(api);
		skippedOccurrenceUtc = null;
		RecalculateNext(DateTime.UtcNow);
		return "Automated restart configuration reloaded.";
	}

	public string SkipNext(string actor = "admin")
	{
		if (!config.Enabled || nextRestartUtc == DateTime.MinValue)
		{
			return "Automated restarts are disabled or no valid restart time is configured.";
		}
		DateTime dateTime = nextRestartUtc;
		skippedOccurrenceUtc = dateTime;
		RecalculateNext(dateTime.AddSeconds(1.0));
		api.Logger.Notification("[ServerEssentials/Restarts] {0} skipped scheduled restart {1:u}. Next={2:u}", actor, dateTime, nextRestartUtc);
		return $"Skipped the restart scheduled for {dateTime:yyyy-MM-dd HH:mm:ss} UTC. Next restart: {nextRestartUtc:yyyy-MM-dd HH:mm:ss} UTC.";
	}

	public string StatusText()
	{
		AutomatedRestartSnapshot snapshot = GetSnapshot();
		if (snapshot.CustomRestartScheduled)
		{
			return $"One-off restart in {FormatDuration(TimeSpan.FromSeconds(snapshot.CustomRestartRemainingSeconds))} at {snapshot.CustomRestartUtc}: {snapshot.CustomRestartReason}.";
		}
		if (!snapshot.Enabled)
		{
			return "Automated restarts are disabled.";
		}
		return $"Next restart in {FormatDuration(TimeSpan.FromSeconds(snapshot.RemainingSeconds))} at {snapshot.NextRestartUtc}.";
	}

	public string ScheduleCustomRestart(int delayMinutes, string reason, string actor = "admin")
	{
		delayMinutes = Math.Clamp(delayMinutes, 1, 1440);
		customRestartReason = (string.IsNullOrWhiteSpace(reason) ? "Maintenance" : reason.Trim());
		if (customRestartReason.Length > 240)
		{
			customRestartReason = customRestartReason.Substring(0, 240);
		}
		customRestartUtc = DateTime.UtcNow.AddMinutes(delayMinutes);
		customSentWarnings.Clear();
		customPreviousRemainingMilliseconds = (long)delayMinutes * 60000L + 1500;
		shutdownTriggered = false;
		api.Logger.Notification("[ServerEssentials/Restarts] {0} scheduled one-off restart for {1:u}. Reason={2}", actor, customRestartUtc, customRestartReason);
		return $"One-off restart scheduled in {delayMinutes} minute{((delayMinutes == 1) ? "" : "s")} for {customRestartUtc:yyyy-MM-dd HH:mm:ss} UTC. Reason: {customRestartReason}.";
	}

	public string CancelCustomRestart(string actor = "admin")
	{
		if (customRestartUtc == DateTime.MinValue || customRestartUtc <= DateTime.UtcNow)
		{
			return "No one-off restart is currently scheduled.";
		}
		DateTime dateTime = customRestartUtc;
		string text = customRestartReason;
		customRestartUtc = DateTime.MinValue;
		customRestartReason = "";
		customPreviousRemainingMilliseconds = long.MaxValue;
		customSentWarnings.Clear();
		api.Logger.Notification("[ServerEssentials/Restarts] {0} cancelled one-off restart {1:u}. Reason={2}", actor, dateTime, text);
		return $"Cancelled the one-off restart scheduled for {dateTime:yyyy-MM-dd HH:mm:ss} UTC.";
	}

	private void Tick()
	{
		if (disposed || shutdownTriggered)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (customRestartUtc != DateTime.MinValue)
		{
			long num = (long)(customRestartUtc - utcNow).TotalMilliseconds;
			if (num <= 0)
			{
				string message = "Server is restarting now — " + customRestartReason;
				if (config.AnnounceInGame)
				{
					api.BroadcastMessageToAllGroups(FormatRestartChatNotice(message), EnumChatType.Notification);
				}
				if (config.AnnounceToDiscord)
				{
					PublishDiscordNotice(message, "now", 0, customRestartReason, customRestartUtc);
				}
				api.Logger.Notification("[ServerEssentials/Restarts] One-off restart reached. Reason={0}. Performing graceful server shutdown.", customRestartReason);
				shutdownTriggered = true;
				api.Server.ShutDown();
				return;
			}
			foreach (int item in config.WarningMinutes.OrderByDescending((int x) => x))
			{
				long num2 = (long)item * 60000L;
				if (!customSentWarnings.Contains(item) && customPreviousRemainingMilliseconds > num2 && num <= num2)
				{
					customSentWarnings.Add(item);
					string message2 = $"Server is restarting in {item} minute{((item == 1) ? "" : "s")} — {customRestartReason}";
					if (config.AnnounceInGame)
					{
						api.BroadcastMessageToAllGroups(FormatRestartChatNotice(message2), EnumChatType.Notification);
					}
					if (config.AnnounceToDiscord)
					{
						PublishDiscordNotice(message2, "warning", item, customRestartReason, customRestartUtc);
					}
				}
			}
			customPreviousRemainingMilliseconds = num;
		}
		if (!config.Enabled)
		{
			return;
		}
		if (nextRestartUtc == DateTime.MinValue)
		{
			RecalculateNext(utcNow);
		}
		if (nextRestartUtc == DateTime.MinValue)
		{
			return;
		}
		long num3 = (long)(nextRestartUtc - utcNow).TotalMilliseconds;
		if (num3 <= 0)
		{
			string message3 = "Server is restarting now — " + config.Reason;
			if (config.AnnounceInGame)
			{
				api.BroadcastMessageToAllGroups(FormatRestartChatNotice(message3), EnumChatType.Notification);
			}
			if (config.AnnounceToDiscord)
			{
				PublishDiscordNotice(message3, "now", 0, config.Reason, nextRestartUtc);
			}
			api.Logger.Notification("[ServerEssentials/Restarts] Scheduled restart reached. Performing graceful server shutdown. Host/wrapper must relaunch the process.");
			shutdownTriggered = true;
			api.Server.ShutDown();
			return;
		}
		foreach (int item2 in config.WarningMinutes.OrderByDescending((int x) => x))
		{
			long num4 = (long)item2 * 60000L;
			if (!sentWarnings.Contains(item2) && previousRemainingMilliseconds > num4 && num3 <= num4)
			{
				sentWarnings.Add(item2);
				string message4 = $"Server is restarting in {item2} minute{((item2 == 1) ? "" : "s")} — {config.Reason}";
				if (config.AnnounceInGame)
				{
					api.BroadcastMessageToAllGroups(FormatRestartChatNotice(message4), EnumChatType.Notification);
				}
				if (config.AnnounceToDiscord)
				{
					PublishDiscordNotice(message4, "warning", item2, config.Reason, nextRestartUtc);
				}
			}
		}
		previousRemainingMilliseconds = num3;
	}

	private static string FormatRestartChatNotice(string message)
	{
		return "<font color=\"#ffd166\"><strong>" + WebUtility.HtmlEncode(message ?? string.Empty) + "</strong></font>";
	}

	private void OnRestartQuery(string eventName, ref EnumHandling handling, IAttribute data)
	{
		if (data is ITreeAttribute treeAttribute)
		{
			AutomatedRestartSnapshot snapshot = GetSnapshot();
			treeAttribute.SetBool("handled", value: true);
			treeAttribute.SetBool("enabled", snapshot.Enabled);
			treeAttribute.SetString("status", StatusText());
			treeAttribute.SetString("nextRestartUtc", snapshot.NextRestartUtc ?? "");
			treeAttribute.SetLong("remainingSeconds", snapshot.RemainingSeconds);
			treeAttribute.SetString("reason", snapshot.Reason ?? "");
		}
	}

	private void PublishDiscordNotice(string message, string kind, int minutes, string? reason = null, DateTime? restartAtUtc = null)
	{
		try
		{
			TreeAttribute treeAttribute = new TreeAttribute();
			treeAttribute.SetString("message", message);
			treeAttribute.SetString("kind", kind);
			treeAttribute.SetInt("minutes", minutes);
			treeAttribute.SetString("reason", string.IsNullOrWhiteSpace(reason) ? config.Reason : reason);
			DateTime dateTime = restartAtUtc ?? nextRestartUtc;
			treeAttribute.SetLong("restartAtUnixMilliseconds", (dateTime == DateTime.MinValue) ? 0 : new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
			api.Event.PushEvent("newfiesse:restart-notice", treeAttribute);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Restarts] Discord restart notice failed: {0}: {1}", ex.GetType().Name, ex.Message);
		}
	}

	private void RecalculateNext(DateTime fromUtc)
	{
		sentWarnings.Clear();
		shutdownTriggered = false;
		nextRestartUtc = FindNextOccurrence(fromUtc);
		previousRemainingMilliseconds = ((nextRestartUtc == DateTime.MinValue) ? long.MaxValue : Math.Max(0L, (long)(nextRestartUtc - DateTime.UtcNow).TotalMilliseconds));
	}

	private DateTime FindNextOccurrence(DateTime fromUtc)
	{
		List<TimeSpan> list = (from x in config.EffectiveTimesUtc()
			select TimeSpan.TryParseExact(x, "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out var result) ? new TimeSpan?(result) : ((TimeSpan?)null) into x
			where x.HasValue
			select x.Value into x
			orderby x
			select x).ToList();
		if (list.Count == 0)
		{
			return DateTime.MinValue;
		}
		DateTime date = fromUtc.Date;
		for (int num = 0; num <= 2; num++)
		{
			DateTime dateTime = date.AddDays(num);
			foreach (TimeSpan item in list)
			{
				DateTime dateTime2 = DateTime.SpecifyKind(dateTime.Add(item), DateTimeKind.Utc);
				if (dateTime2 <= fromUtc.AddMilliseconds(250.0) || (skippedOccurrenceUtc.HasValue && Math.Abs((dateTime2 - skippedOccurrenceUtc.Value).TotalSeconds) < 1.0))
				{
					continue;
				}
				return dateTime2;
			}
		}
		return DateTime.MinValue;
	}

	private void RegisterRestartTimeCommand()
	{
		if (HasTh3Essentials)
		{
			api.Logger.Notification("[ServerEssentials/Restarts] Th3Essentials detected. NSE will not register root /restarttime; /nse restarttime remains available.");
			return;
		}
		if (RootCommandExists("restarttime"))
		{
			api.Logger.Notification("[ServerEssentials/Restarts] Root /restarttime is already owned by another provider. NSE left it untouched; /nse restarttime remains available.");
			return;
		}
		try
		{
			api.RegisterCommand("restarttime", "Show time until the next automated server restart", "", HandleRestartTime, Privilege.chat);
			rootRestartTimeOwned = true;
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Restarts] Could not register root /restarttime: {0}: {1}", ex.GetType().Name, ex.Message);
		}
	}

	public void HandleRestartTime(IServerPlayer player, int groupId, CmdArgs args)
	{
		player.SendMessage(groupId, StatusText(), EnumChatType.CommandSuccess);
	}

	private bool RootCommandExists(string command)
	{
		try
		{
			return api.ChatCommands.Get(command) != null;
		}
		catch
		{
			return false;
		}
	}

	private static string FormatDuration(TimeSpan value)
	{
		if (!(value.TotalDays >= 1.0))
		{
			if (!(value.TotalHours >= 1.0))
			{
				if (!(value.TotalMinutes >= 1.0))
				{
					return $"{Math.Max(0, value.Seconds)}s";
				}
				return $"{(int)value.TotalMinutes}m {value.Seconds}s";
			}
			return $"{(int)value.TotalHours}h {value.Minutes}m";
		}
		return $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m";
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			if (tickListenerId >= 0)
			{
				api.Event.UnregisterGameTickListener(tickListenerId);
				tickListenerId = -1L;
			}
		}
	}
}
