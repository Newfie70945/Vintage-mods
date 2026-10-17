using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Restarts;

public sealed class AutomatedRestartConfig
{
    public const string ConfigFile = "NewfiesAutomatedRestarts.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool Enabled { get; set; } = false;
    // Matches the familiar Th3Essentials model: one time plus an optional multi-time list.
    // RestartTimesUtc has priority when at least one valid entry exists.
    public string RestartTimeUtc { get; set; } = "00:00:00";
    public List<string> RestartTimesUtc { get; set; } = new();
    public List<int> WarningMinutes { get; set; } = new() { 1, 2, 3, 4, 5, 10, 20, 30 };
    public bool AnnounceInGame { get; set; } = true;
    public bool AnnounceToDiscord { get; set; } = true;
    public string Reason { get; set; } = "Scheduled server restart";

    public void Clamp()
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion);
        RestartTimeUtc = NormalizeTime(RestartTimeUtc, "00:00:00");
        RestartTimesUtc ??= new List<string>();
        RestartTimesUtc = RestartTimesUtc
            .Select(x => TryNormalizeTime(x, out string normalized) ? normalized : "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        WarningMinutes ??= new List<int>();
        WarningMinutes = WarningMinutes
            .Where(x => x > 0 && x <= 24 * 60)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        Reason = string.IsNullOrWhiteSpace(Reason) ? "Scheduled server restart" : Reason.Trim();
        if (Reason.Length > 240) Reason = Reason[..240];
    }

    public IReadOnlyList<string> EffectiveTimesUtc() => RestartTimesUtc.Count > 0
        ? RestartTimesUtc
        : new[] { RestartTimeUtc };

    public static AutomatedRestartConfig Load(ICoreServerAPI api)
    {
        var cfg = ModConfigRecovery.LoadOrNull<AutomatedRestartConfig>(api, ConfigFile, "AutomatedRestarts") ?? new AutomatedRestartConfig();
        cfg.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, cfg, ConfigFile);
        return cfg;
    }

    public static bool TryNormalizeTime(string? raw, out string normalized)
    {
        normalized = "";
        string text = (raw ?? "").Trim();
        if (DateTime.TryParseExact(text, new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime parsed))
        {
            normalized = parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    private static string NormalizeTime(string? raw, string fallback) => TryNormalizeTime(raw, out string normalized) ? normalized : fallback;
}

public sealed class AutomatedRestartSnapshot
{
    public bool Available { get; set; } = true;
    public bool Enabled { get; set; }
    public string RestartTimeUtc { get; set; } = "00:00:00";
    public List<string> RestartTimesUtc { get; set; } = new();
    public List<string> EffectiveRestartTimesUtc { get; set; } = new();
    public List<int> WarningMinutes { get; set; } = new();
    public bool AnnounceInGame { get; set; }
    public bool AnnounceToDiscord { get; set; }
    public string Reason { get; set; } = "";
    public long NextRestartUnixMilliseconds { get; set; }
    public long RemainingSeconds { get; set; }
    public string NextRestartUtc { get; set; } = "";
    public bool CustomRestartScheduled { get; set; }
    public long CustomRestartUnixMilliseconds { get; set; }
    public long CustomRestartRemainingSeconds { get; set; }
    public string CustomRestartUtc { get; set; } = "";
    public string CustomRestartReason { get; set; } = "";
    public bool Th3EssentialsDetected { get; set; }
    public string RootRestartTimeOwner { get; set; } = "";
    public string FallbackCommand { get; set; } = "/nse restarttime";
    public string ConfigFile { get; set; } = AutomatedRestartConfig.ConfigFile;
    public string RestartBehavior { get; set; } = "Graceful shutdown; the server host/wrapper must relaunch the process.";
}

public sealed class AutomatedRestartSettingsRequest
{
    public bool Enabled { get; set; }
    public string RestartTimeUtc { get; set; } = "00:00:00";
    public List<string> RestartTimesUtc { get; set; } = new();
    public List<int> WarningMinutes { get; set; } = new();
    public bool AnnounceInGame { get; set; } = true;
    public bool AnnounceToDiscord { get; set; } = true;
    public string Reason { get; set; } = "Scheduled server restart";
}

/// <summary>
/// Th3Essentials-style scheduled restart controller. The mod owns UTC schedules,
/// warning announcements and graceful shutdown. Process relaunch remains the job of
/// the hosting panel/service/wrapper because no in-process mod exists after shutdown.
/// </summary>
public sealed class AutomatedRestartService : IDisposable
{
    public const string DiscordRestartNoticeEvent = "newfiesse:restart-notice";
    public const string DiscordRestartQueryEvent = "newfiesse:restart-query";

    private readonly ICoreServerAPI api;
    private AutomatedRestartConfig config;
    private long tickListenerId = -1;
    private DateTime nextRestartUtc = DateTime.MinValue;
    private DateTime? skippedOccurrenceUtc;
    private long previousRemainingMilliseconds = long.MaxValue;
    private readonly HashSet<int> sentWarnings = new();
    private DateTime customRestartUtc = DateTime.MinValue;
    private string customRestartReason = "";
    private long customPreviousRemainingMilliseconds = long.MaxValue;
    private readonly HashSet<int> customSentWarnings = new();
    private bool disposed;
    private bool rootRestartTimeOwned;
    private bool shutdownTriggered;

    public AutomatedRestartService(ICoreServerAPI api)
    {
        this.api = api;
        config = AutomatedRestartConfig.Load(api);
    }

    private bool HasTh3Essentials => api.ModLoader.Mods.Any(mod =>
        mod?.Info != null && string.Equals(mod.Info.ModID, "th3essentials", StringComparison.OrdinalIgnoreCase));

    public AutomatedRestartConfig Config => config;

    public void Start()
    {
        RecalculateNext(DateTime.UtcNow);
        tickListenerId = api.Event.RegisterGameTickListener(_ => Tick(), 1000);
        RegisterRestartTimeCommand();
        api.Event.RegisterEventBusListener(OnRestartQuery, filterByEventName: DiscordRestartQueryEvent);
        api.Logger.Notification(
            "[ServerEssentials/Restarts] Automated restart scheduler started. Enabled={0}, TimesUTC={1}, Warnings={2}.",
            config.Enabled,
            string.Join(",", config.EffectiveTimesUtc()),
            string.Join(",", config.WarningMinutes));
    }

    public AutomatedRestartSnapshot GetSnapshot()
    {
        DateTime now = DateTime.UtcNow;
        if (config.Enabled && (nextRestartUtc == DateTime.MinValue || nextRestartUtc <= now.AddMilliseconds(-1500)))
            RecalculateNext(now);
        long remaining = config.Enabled && nextRestartUtc > now
            ? Math.Max(0, (long)(nextRestartUtc - now).TotalSeconds)
            : 0;
        string owner = HasTh3Essentials
            ? "Th3Essentials"
            : rootRestartTimeOwned
                ? "Newfies Server Essentials"
                : RootCommandExists("restarttime") ? "Existing provider" : "Unregistered";
        long customRemaining = customRestartUtc > now
            ? Math.Max(0, (long)(customRestartUtc - now).TotalSeconds)
            : 0;
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
            NextRestartUnixMilliseconds = config.Enabled && nextRestartUtc != DateTime.MinValue
                ? new DateTimeOffset(DateTime.SpecifyKind(nextRestartUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
                : 0,
            RemainingSeconds = remaining,
            NextRestartUtc = config.Enabled && nextRestartUtc != DateTime.MinValue
                ? nextRestartUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
                : "",
            CustomRestartScheduled = customRestartUtc > now,
            CustomRestartUnixMilliseconds = customRestartUtc > now
                ? new DateTimeOffset(DateTime.SpecifyKind(customRestartUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
                : 0,
            CustomRestartRemainingSeconds = customRemaining,
            CustomRestartUtc = customRestartUtc > now
                ? customRestartUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
                : "",
            CustomRestartReason = customRestartUtc > now ? customRestartReason : "",
            Th3EssentialsDetected = HasTh3Essentials,
            RootRestartTimeOwner = owner
        };
    }

    public (bool Success, string Message, AutomatedRestartSnapshot Snapshot) SaveSettings(AutomatedRestartSettingsRequest? request)
    {
        if (request == null) return (false, "Automated restart settings are required.", GetSnapshot());

        var candidate = new AutomatedRestartConfig
        {
            SchemaVersion = AutomatedRestartConfig.CurrentSchemaVersion,
            Enabled = request.Enabled,
            RestartTimeUtc = request.RestartTimeUtc,
            RestartTimesUtc = request.RestartTimesUtc ?? new List<string>(),
            WarningMinutes = request.WarningMinutes ?? new List<int>(),
            AnnounceInGame = request.AnnounceInGame,
            AnnounceToDiscord = request.AnnounceToDiscord,
            Reason = request.Reason
        };

        // Reject invalid non-empty time input instead of silently turning a typo into midnight.
        var suppliedTimes = candidate.RestartTimesUtc.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        foreach (string raw in suppliedTimes)
        {
            if (!AutomatedRestartConfig.TryNormalizeTime(raw, out _))
                return (false, $"Invalid UTC restart time '{raw}'. Use HH:mm or HH:mm:ss.", GetSnapshot());
        }
        if (suppliedTimes.Count == 0 && !AutomatedRestartConfig.TryNormalizeTime(candidate.RestartTimeUtc, out _))
            return (false, "Invalid UTC restart time. Use HH:mm or HH:mm:ss.", GetSnapshot());

        candidate.Clamp();
        config = candidate;
        skippedOccurrenceUtc = null;
        BlockLogger.Services.SuiteConfigStorage.Store(api, config, AutomatedRestartConfig.ConfigFile);
        RecalculateNext(DateTime.UtcNow);
        api.Logger.Notification("[ServerEssentials/Restarts] Settings saved. Enabled={0}, TimesUTC={1}.", config.Enabled, string.Join(",", config.EffectiveTimesUtc()));
        return (true, "Automated restart settings saved and applied immediately.", GetSnapshot());
    }

    public string SetEnabled(bool enabled)
    {
        config.Enabled = enabled;
        BlockLogger.Services.SuiteConfigStorage.Store(api, config, AutomatedRestartConfig.ConfigFile);
        skippedOccurrenceUtc = null;
        RecalculateNext(DateTime.UtcNow);
        return enabled
            ? $"Automated restarts enabled. Next restart: {GetSnapshot().NextRestartUtc}."
            : "Automated restarts disabled.";
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
        if (!config.Enabled || nextRestartUtc == DateTime.MinValue) return "Automated restarts are disabled or no valid restart time is configured.";
        DateTime skipped = nextRestartUtc;
        skippedOccurrenceUtc = skipped;
        RecalculateNext(skipped.AddSeconds(1));
        api.Logger.Notification("[ServerEssentials/Restarts] {0} skipped scheduled restart {1:u}. Next={2:u}", actor, skipped, nextRestartUtc);
        return $"Skipped the restart scheduled for {skipped:yyyy-MM-dd HH:mm:ss} UTC. Next restart: {nextRestartUtc:yyyy-MM-dd HH:mm:ss} UTC.";
    }

    public string StatusText()
    {
        var snap = GetSnapshot();
        if (snap.CustomRestartScheduled)
            return $"One-off restart in {FormatDuration(TimeSpan.FromSeconds(snap.CustomRestartRemainingSeconds))} at {snap.CustomRestartUtc}: {snap.CustomRestartReason}.";
        if (!snap.Enabled) return "Automated restarts are disabled.";
        return $"Next restart in {FormatDuration(TimeSpan.FromSeconds(snap.RemainingSeconds))} at {snap.NextRestartUtc}.";
    }

    public string ScheduleCustomRestart(int delayMinutes, string reason, string actor = "admin")
    {
        delayMinutes = Math.Clamp(delayMinutes, 1, 24 * 60);
        customRestartReason = string.IsNullOrWhiteSpace(reason) ? "Maintenance" : reason.Trim();
        if (customRestartReason.Length > 240) customRestartReason = customRestartReason[..240];
        customRestartUtc = DateTime.UtcNow.AddMinutes(delayMinutes);
        customSentWarnings.Clear();
        customPreviousRemainingMilliseconds = delayMinutes * 60_000L + 1500;
        shutdownTriggered = false;
        api.Logger.Notification("[ServerEssentials/Restarts] {0} scheduled one-off restart for {1:u}. Reason={2}", actor, customRestartUtc, customRestartReason);
        return $"One-off restart scheduled in {delayMinutes} minute{(delayMinutes == 1 ? "" : "s")} for {customRestartUtc:yyyy-MM-dd HH:mm:ss} UTC. Reason: {customRestartReason}.";
    }

    public string CancelCustomRestart(string actor = "admin")
    {
        if (customRestartUtc == DateTime.MinValue || customRestartUtc <= DateTime.UtcNow)
            return "No one-off restart is currently scheduled.";
        DateTime cancelled = customRestartUtc;
        string reason = customRestartReason;
        customRestartUtc = DateTime.MinValue;
        customRestartReason = "";
        customPreviousRemainingMilliseconds = long.MaxValue;
        customSentWarnings.Clear();
        api.Logger.Notification("[ServerEssentials/Restarts] {0} cancelled one-off restart {1:u}. Reason={2}", actor, cancelled, reason);
        return $"Cancelled the one-off restart scheduled for {cancelled:yyyy-MM-dd HH:mm:ss} UTC.";
    }

    private void Tick()
    {
        if (disposed || shutdownTriggered) return;
        DateTime now = DateTime.UtcNow;

        if (customRestartUtc != DateTime.MinValue)
        {
            long customRemaining = (long)(customRestartUtc - now).TotalMilliseconds;
            if (customRemaining <= 0)
            {
                string nowMessage = $"Server is restarting now — {customRestartReason}";
                if (config.AnnounceInGame) api.BroadcastMessageToAllGroups(FormatRestartChatNotice(nowMessage), EnumChatType.Notification);
                if (config.AnnounceToDiscord) PublishDiscordNotice(nowMessage, "now", 0, customRestartReason, customRestartUtc);
                api.Logger.Notification("[ServerEssentials/Restarts] One-off restart reached. Reason={0}. Performing graceful server shutdown.", customRestartReason);
                shutdownTriggered = true;
                api.Server.ShutDown();
                return;
            }

            foreach (int minutes in config.WarningMinutes.OrderByDescending(x => x))
            {
                long threshold = minutes * 60_000L;
                if (customSentWarnings.Contains(minutes)) continue;
                if (customPreviousRemainingMilliseconds > threshold && customRemaining <= threshold)
                {
                    customSentWarnings.Add(minutes);
                    string msg = $"Server is restarting in {minutes} minute{(minutes == 1 ? "" : "s")} — {customRestartReason}";
                    if (config.AnnounceInGame) api.BroadcastMessageToAllGroups(FormatRestartChatNotice(msg), EnumChatType.Notification);
                    if (config.AnnounceToDiscord) PublishDiscordNotice(msg, "warning", minutes, customRestartReason, customRestartUtc);
                }
            }
            customPreviousRemainingMilliseconds = customRemaining;
        }

        if (!config.Enabled) return;
        if (nextRestartUtc == DateTime.MinValue) RecalculateNext(now);
        if (nextRestartUtc == DateTime.MinValue) return;

        long remaining = (long)(nextRestartUtc - now).TotalMilliseconds;
        if (remaining <= 0)
        {
            string nowMessage = $"Server is restarting now — {config.Reason}";
            if (config.AnnounceInGame) api.BroadcastMessageToAllGroups(FormatRestartChatNotice(nowMessage), EnumChatType.Notification);
            if (config.AnnounceToDiscord) PublishDiscordNotice(nowMessage, "now", 0, config.Reason, nextRestartUtc);
            api.Logger.Notification("[ServerEssentials/Restarts] Scheduled restart reached. Performing graceful server shutdown. Host/wrapper must relaunch the process.");
            shutdownTriggered = true;
            api.Server.ShutDown();
            return;
        }

        foreach (int minutes in config.WarningMinutes.OrderByDescending(x => x))
        {
            long threshold = minutes * 60_000L;
            if (sentWarnings.Contains(minutes)) continue;
            if (previousRemainingMilliseconds > threshold && remaining <= threshold)
            {
                sentWarnings.Add(minutes);
                string msg = $"Server is restarting in {minutes} minute{(minutes == 1 ? "" : "s")} — {config.Reason}";
                if (config.AnnounceInGame) api.BroadcastMessageToAllGroups(FormatRestartChatNotice(msg), EnumChatType.Notification);
                if (config.AnnounceToDiscord) PublishDiscordNotice(msg, "warning", minutes, config.Reason, nextRestartUtc);
            }
        }
        previousRemainingMilliseconds = remaining;
    }

    private static string FormatRestartChatNotice(string message)
    {
        // Vintage Story chat accepts VTML. Restart notices are intentionally prominent,
        // while encoding the configurable reason so it cannot inject markup.
        return $"<font color=\"#ffd166\"><strong>{WebUtility.HtmlEncode(message ?? string.Empty)}</strong></font>";
    }

    private void OnRestartQuery(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (data is not ITreeAttribute tree) return;
        var snap = GetSnapshot();
        tree.SetBool("handled", true);
        tree.SetBool("enabled", snap.Enabled);
        tree.SetString("status", StatusText());
        tree.SetString("nextRestartUtc", snap.NextRestartUtc ?? "");
        tree.SetLong("remainingSeconds", snap.RemainingSeconds);
        tree.SetString("reason", snap.Reason ?? "");
    }

    private void PublishDiscordNotice(string message, string kind, int minutes, string? reason = null, DateTime? restartAtUtc = null)
    {
        try
        {
            var tree = new TreeAttribute();
            tree.SetString("message", message);
            tree.SetString("kind", kind);
            tree.SetInt("minutes", minutes);
            tree.SetString("reason", string.IsNullOrWhiteSpace(reason) ? config.Reason : reason);
            DateTime effectiveRestart = restartAtUtc ?? nextRestartUtc;
            tree.SetLong("restartAtUnixMilliseconds", effectiveRestart == DateTime.MinValue ? 0 : new DateTimeOffset(DateTime.SpecifyKind(effectiveRestart, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
            api.Event.PushEvent(DiscordRestartNoticeEvent, tree);
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
        previousRemainingMilliseconds = nextRestartUtc == DateTime.MinValue
            ? long.MaxValue
            : Math.Max(0, (long)(nextRestartUtc - DateTime.UtcNow).TotalMilliseconds);
    }

    private DateTime FindNextOccurrence(DateTime fromUtc)
    {
        var times = config.EffectiveTimesUtc()
            .Select(x => TimeSpan.TryParseExact(x, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out TimeSpan time) ? time : (TimeSpan?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .OrderBy(x => x)
            .ToList();
        if (times.Count == 0) return DateTime.MinValue;

        DateTime day = fromUtc.Date;
        for (int dayOffset = 0; dayOffset <= 2; dayOffset++)
        {
            DateTime date = day.AddDays(dayOffset);
            foreach (TimeSpan time in times)
            {
                DateTime candidate = DateTime.SpecifyKind(date.Add(time), DateTimeKind.Utc);
                if (candidate <= fromUtc.AddMilliseconds(250)) continue;
                if (skippedOccurrenceUtc.HasValue && Math.Abs((candidate - skippedOccurrenceUtc.Value).TotalSeconds) < 1) continue;
                return candidate;
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
#pragma warning disable CS0618
            api.RegisterCommand("restarttime", "Show time until the next automated server restart", "", HandleRestartTime, Privilege.chat);
#pragma warning restore CS0618
            rootRestartTimeOwned = true;
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/Restarts] Could not register root /restarttime: {0}: {1}", ex.GetType().Name, ex.Message);
        }
    }

    public void HandleRestartTime(IServerPlayer player, int groupId, CmdArgs args) =>
        player.SendMessage(groupId, StatusText(), EnumChatType.CommandSuccess);

    private bool RootCommandExists(string command)
    {
        try { return api.ChatCommands.Get(command) != null; }
        catch { return false; }
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours}h {value.Minutes}m";
        if (value.TotalMinutes >= 1) return $"{(int)value.TotalMinutes}m {value.Seconds}s";
        return $"{Math.Max(0, value.Seconds)}s";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (tickListenerId >= 0)
        {
            api.Event.UnregisterGameTickListener(tickListenerId);
            tickListenerId = -1;
        }
        // Vintage Story 1.22.7 event-bus listeners have no unregister API.
        // This service is disposed as the mod/server shuts down; the event bus is torn down with it.
    }
}
