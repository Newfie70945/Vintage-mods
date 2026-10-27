using Newfies.Storage;
using Newfies.ChatProtocol;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace NewfiesChatGuard;

public sealed class ChatGuardService : IDisposable
{
    public const string ControlEvent = "newfieschatguard:control";
    public const string StateFile = "NewfiesChatGuardState.json";
    public const string Version = "2.1.6";

    private static readonly Regex TagRegex = new("<[^>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MentionRegex = new(@"(?<!\w)@[\p{L}\p{N}_\-]{1,64}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UrlRegex = new(@"(?ix)\b(?:https?://|www\.)[^\s<>]+|\b(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?:com|net|org|gg|io|co|ca|uk|dev|app|me|tv|info|xyz|site|online|store|link)(?:/[^\s<>]*)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DurationRegex = new(@"^(?<value>\d{1,6})(?<unit>[mhdw])$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private const int MaxManualMuteMinutes = 43200; // 30 days; use perm/permanent for longer sanctions.

    private readonly ICoreServerAPI api;
    private readonly EventBusListenerDelegate controlEventListener;
    private ChatGuardConfig config;
    private ChatGuardState state;
    private readonly Dictionary<string, Queue<ChatSample>> samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedDecision> recentDecisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ChatGuardAuditEntry> recentAudit = new();
    private readonly Dictionary<string, long> ruleMatches = new(StringComparer.OrdinalIgnoreCase);
    private Action<IServerPlayer>? standaloneGuiOpener;
    private readonly string auditPath;
    private readonly string statePath;
    private readonly DeferredJsonFileWriter stateWriter;
    private readonly BoundedAuditWriter auditWriter;
    private bool stateStorageReady = true;
    private long maintenanceListener = -1, lastSamplePrune, lastStateCleanup;
    private bool disposed;
    private long scannedMessages;
    private long matchedRules;
    private long blockedMessages;
    private long warningsIssued;
    private long mutesIssued;
    private long kicksIssued;

    public ChatGuardService(ICoreServerAPI api)
    {
        this.api = api;
        controlEventListener = OnControlEvent;
        config = LoadConfig();
        statePath = Path.Combine(api.GetOrCreateDataPath("ModConfig"), StateFile);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        state = LoadState();
        stateWriter = new DeferredJsonFileWriter(statePath, error => api.Logger.Warning("[ChatGuard] State persistence failed; retry queued: {0}", error));
        string dataRoot = api.GetOrCreateDataPath("ModData");
        string dir = Path.Combine(dataRoot, "NewfiesChatGuard");
        Directory.CreateDirectory(dir);
        auditPath = Path.Combine(dir, "audit.jsonl");
        auditWriter = new BoundedAuditWriter(auditPath, error => api.Logger.Warning("[ChatGuard] Audit persistence: {0}", error));
        CleanupExpiredState(save: true);
    }

    public ChatGuardConfig Config => config;

    public void SetStandaloneGuiOpener(Action<IServerPlayer>? opener) => standaloneGuiOpener = opener;

    public void Start()
    {
        api.Event.PlayerChat += OnPlayerChat;
        api.Event.RegisterEventBusListener(controlEventListener, filterByEventName: ControlEvent);
        maintenanceListener = api.Event.RegisterGameTickListener(_ => { PruneSamples(UtcNowMs()); CleanupExpiredState(save: true); }, 60_000);
        api.Event.GameWorldSave += OnWorldSave;
        RegisterCommands();
        api.Logger.Notification(
            "[ChatGuard] Started. Enabled={0}, Mode={1}, SuiteIntegration={2}.",
            config.Enabled,
            config.Mode,
            IsSuiteInstalled());
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { api.Event.PlayerChat -= OnPlayerChat; } catch { }
        TryUnregisterControlEventListener();
        SaveState();
        try { api.Event.GameWorldSave -= OnWorldSave; if (maintenanceListener >= 0) api.Event.UnregisterGameTickListener(maintenanceListener); } catch { }
        stateWriter.Dispose(); auditWriter.Dispose();
    }

    private void TryUnregisterControlEventListener()
    {
        // Vintage Story API builds differ here: newer API surfaces expose
        // UnregisterEventBusListener on IEventAPI, while the user's 1.22.7
        // reference set does not expose it through IServerEventAPI. Use a
        // reflection compatibility shim so ChatGuard builds against both.
        try
        {
            var method = api.Event.GetType().GetMethod(
                "UnregisterEventBusListener",
                new[] { typeof(EventBusListenerDelegate) });

            method?.Invoke(api.Event, new object[] { controlEventListener });
        }
        catch
        {
            // On builds without an unregister method, the disposed guard in
            // OnControlEvent makes any retained callback inert until shutdown.
        }
    }

    private void OnPlayerChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if (disposed || byPlayer == null || consumed.value || string.IsNullOrWhiteSpace(message)) return;
        var boundary=ChatMessageBoundary.Parse(message,byPlayer.PlayerName,ResolveChatDisplayName(byPlayer),byPlayer.Role?.Name);
        string body = boundary.HasSender?boundary.Body:ExtractBody(message,byPlayer.PlayerName);

        // Slash commands pass through PlayerChat too. Never count/moderate them as chat:
        // doing so can trigger spam/repeat rules from normal command use and can consume
        // the command before Vintage Story's command dispatcher gets it.
        if (boundary.IsCommand) return;

        var decision = EvaluateChatLine(byPlayer, channelId, body, "chat", message);
        if (decision.Blocked) consumed.value = true;
    }

    private string ResolveChatDisplayName(IServerPlayer player)
    {
        var tree=new TreeAttribute();tree.SetString("playerUid",player.PlayerUID);
        try {api.Event.PushEvent("newfiesse:chat-sender-query",tree);}
        catch {return player.PlayerName;}
        return tree.GetBool("available",false)?tree.GetString("displayName",player.PlayerName):player.PlayerName;
    }

    private ChatGuardDecision EvaluateCached(IServerPlayer player, int channelId, string body, string source) => EvaluateChatLine(player, channelId, body, source, null);

    private ChatGuardDecision EvaluateChatLine(IServerPlayer player, int channelId, string body, string source, string? rawLine)
    {
        // Suite Markdown supplies one canonical visible message for both its moderation
        // bridge and our normal PlayerChat hook. This prevents handler-order-dependent
        // spam/repeat double counts and checks text spanning formatting boundaries.
        var presentation = new TreeAttribute();
        presentation.SetString("playerUid", player.PlayerUID);
        presentation.SetInt("channelId", channelId);
        presentation.SetString("body", body);
        if (rawLine != null) presentation.SetString("rawLine", rawLine);
        try { api.Event.PushEvent("newfiesse:chat-moderation-text-query", presentation); }
        catch { /* Standalone moderation remains available if a presentation mod fails. */ }
        if (presentation.GetBool("available", false)) body = presentation.GetString("moderationText", body);
        long now = UtcNowMs();
        string normalized = NormalizeForMatching(body);
        string key = player.PlayerUID + "\n" + channelId.ToString(CultureInfo.InvariantCulture) + "\n" + normalized;
        if (recentDecisions.TryGetValue(key, out var cached) && now - cached.CreatedUtcMs <= 100)
            return cached.Decision;

        CleanupDecisionCache(now);
        var decision = EvaluateCore(player, channelId, body, normalized, now, source);
        recentDecisions[key] = new CachedDecision { CreatedUtcMs = now, Decision = decision };
        return decision;
    }

    private ChatGuardDecision EvaluateCore(IServerPlayer player, int channelId, string body, string normalized, long now, string source)
    {
        if (!config.Enabled || IsExempt(player)) return Allow();
        scannedMessages++;
        CleanupExpiredState(save: false);

        if (state.Mutes.TryGetValue(player.PlayerUID, out var mute) && IsMuteActive(mute, now))
        {
            blockedMessages++;
            Notify(player, mute.Permanent
                ? $"You are permanently muted. Reason: {mute.Reason}"
                : $"You are muted for another {FormatRemaining(mute.ExpiresUtcMs - now)}. Reason: {mute.Reason}");
            return new ChatGuardDecision { Blocked = true, Rule = "Mute", Action = "Mute", Detail = mute.Reason };
        }

        var history = GetHistory(player.PlayerUID);
        AddSample(history, now, normalized);

        if (config.Length.Enabled && body.Length > config.Length.MaxCharacters)
            return ApplyViolation(player, channelId, body, "MessageLength", config.Length, $"Message length {body.Length} exceeds {config.Length.MaxCharacters} characters.", source);

        if (config.BlockedTerms.Enabled && TryFindBlockedTerm(normalized, out string term))
            return ApplyViolation(player, channelId, body, "BlockedTerm", config.BlockedTerms, $"Matched blocked phrase '{term}'.", source);

        if (config.Links.Enabled && TryFindBlockedDomain(body, out string domain))
            return ApplyViolation(player, channelId, body, "Link", config.Links, $"Link/domain '{domain}' is not allowed.", source);

        if (config.Mentions.Enabled)
        {
            int mentions = MentionRegex.Matches(body).Count;
            if (mentions > config.Mentions.MaxMentions)
                return ApplyViolation(player, channelId, body, "MentionSpam", config.Mentions, $"Message contained {mentions} mentions; limit is {config.Mentions.MaxMentions}.", source);
        }

        if (config.Caps.Enabled && IsExcessiveCaps(body, out int uppercasePercent, out int letters))
            return ApplyViolation(player, channelId, body, "Caps", config.Caps, $"Uppercase ratio {uppercasePercent}% across {letters} letters; limit is {config.Caps.MaxUppercasePercent}%.", source);

        if (config.Repeat.Enabled)
        {
            long cutoff = now - config.Repeat.WindowSeconds * 1000L;
            int repeats = history.Count(s => s.TimestampUtcMs >= cutoff && s.Normalized == normalized && normalized.Length > 0);
            if (repeats >= config.Repeat.MaxRepeats)
                return ApplyViolation(player, channelId, body, "Repeat", config.Repeat, $"Repeated the same message {repeats} times within {config.Repeat.WindowSeconds}s.", source);
        }

        if (config.Spam.Enabled)
        {
            long cutoff = now - config.Spam.WindowSeconds * 1000L;
            int count = history.Count(s => s.TimestampUtcMs >= cutoff);
            if (count > config.Spam.MaxMessages)
                return ApplyViolation(player, channelId, body, "Spam", config.Spam, $"Sent {count} messages within {config.Spam.WindowSeconds}s; limit is {config.Spam.MaxMessages}.", source);
        }

        return Allow();
    }

    private ChatGuardDecision ApplyViolation(IServerPlayer player, int channelId, string body, string rule, ChatRuleConfig ruleConfig, string detail, string source)
    {
        matchedRules++;
        ruleMatches[rule] = ruleMatches.TryGetValue(rule, out long existingRuleMatches) ? existingRuleMatches + 1 : 1;
        string configuredAction = ruleConfig.Action;
        string action = config.Mode.Equals("LogOnly", StringComparison.OrdinalIgnoreCase) ? "LogOnly" : configuredAction;
        bool blocked = false;

        if (action == "LogOnly")
        {
            WriteAudit(player, channelId, rule, action, detail, body, source);
            return new ChatGuardDecision { Blocked = false, Rule = rule, Action = action, Detail = detail };
        }

        switch (configuredAction)
        {
            case "Kick":
                blocked = true;
                blockedMessages++;
                kicksIssued++;
                WriteAudit(player, channelId, rule, "Kick", detail, body, source);
                try { player.Disconnect($"ChatGuard: {HumanRule(rule)} violation."); } catch { }
                break;

            case "Mute":
                blocked = true;
                blockedMessages++;
                Mute(player.PlayerUID, player.PlayerName, ruleConfig.MuteMinutes, $"{HumanRule(rule)} violation", "ChatGuard", persist: true);
                mutesIssued++;
                WriteAudit(player, channelId, rule, "Mute", detail, body, source);
                Notify(player, $"Message blocked. You have been muted for {ruleConfig.MuteMinutes} minute(s): {HumanRule(rule)}.");
                break;

            case "Block":
                blocked = true;
                blockedMessages++;
                WriteAudit(player, channelId, rule, "Block", detail, body, source);
                Notify(player, $"Message blocked: {HumanRule(rule)}.");
                break;

            default:
                blocked = true;
                blockedMessages++;
                warningsIssued++;
                int warningCount = AddWarning(player.PlayerUID, player.PlayerName, nowMs: UtcNowMs());
                string warningAction = "Warn";
                if (config.Escalation.Enabled && warningCount >= config.Escalation.WarningsBeforeMute)
                {
                    Mute(player.PlayerUID, player.PlayerName, config.Escalation.MuteMinutes, $"ChatGuard escalation after {warningCount} warnings", "ChatGuard", persist: true);
                    mutesIssued++;
                    warningAction = "Warn+Mute";
                    Notify(player, $"Message blocked. Warning {warningCount}/{config.Escalation.WarningsBeforeMute}; automatic mute for {config.Escalation.MuteMinutes} minute(s).");
                }
                else
                {
                    Notify(player, config.Escalation.Enabled
                        ? $"Message blocked. Warning {warningCount}/{config.Escalation.WarningsBeforeMute}: {HumanRule(rule)}."
                        : $"Message blocked with a warning: {HumanRule(rule)}.");
                }
                WriteAudit(player, channelId, rule, warningAction, detail, body, source);
                action = warningAction;
                break;
        }

        return new ChatGuardDecision { Blocked = blocked, Rule = rule, Action = action, Detail = detail };
    }

    private void OnControlEvent(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (disposed || data is not ITreeAttribute tree) return;
        tree.SetBool("available", true);
        tree.SetString("version", Version);
        string op = tree.GetString("operation", "get").Trim().ToLowerInvariant();

        try
        {
            switch (op)
            {
                case "moderate":
                    HandleModerationRequest(tree);
                    break;
                case "save":
                    HandleSaveRequest(tree);
                    break;
                case "action":
                    HandleActionRequest(tree);
                    break;
                case "test":
                    HandleTestRequest(tree);
                    break;
                case "reload":
                    config = LoadConfig();
                    tree.SetBool("success", true);
                    tree.SetString("message", "ChatGuard configuration reloaded.");
                    FillSnapshot(tree);
                    break;
                default:
                    tree.SetBool("success", true);
                    tree.SetString("message", "ChatGuard status loaded.");
                    FillSnapshot(tree);
                    break;
            }
        }
        catch (Exception ex)
        {
            tree.SetBool("success", false);
            tree.SetString("message", "ChatGuard request failed: " + ex.Message);
            FillSnapshot(tree);
        }
    }

    private void HandleModerationRequest(ITreeAttribute tree)
    {
        string uid = tree.GetString("playerUid", "");
        string name = tree.GetString("playerName", "");
        int channelId = tree.GetInt("channelId", GlobalConstants.GeneralChatGroup);
        string body = tree.GetString("message", "");
        var player = FindOnline(uid, name);
        if (player == null)
        {
            tree.SetBool("success", false);
            tree.SetBool("blocked", false);
            tree.SetString("message", "Player is no longer online.");
            return;
        }
        if(ChatMessageBoundary.Parse(body,null).IsCommand)
        {tree.SetBool("success",true);tree.SetBool("blocked",false);tree.SetString("rule","");tree.SetString("action","");tree.SetString("detail","Command bypasses chat moderation.");return;}
        var decision = EvaluateCached(player, channelId, body, "suite");
        tree.SetBool("success", true);
        tree.SetBool("blocked", decision.Blocked);
        tree.SetString("rule", decision.Rule);
        tree.SetString("action", decision.Action);
        tree.SetString("detail", decision.Detail);
    }

    private void HandleSaveRequest(ITreeAttribute tree)
    {
        string json = tree.GetString("configJson", "");
        var candidate = JsonConvert.DeserializeObject<ChatGuardConfig>(json) ?? throw new InvalidOperationException("Configuration payload was empty.");
        CommitConfig(candidate);
        tree.SetBool("success", true);
        tree.SetString("message", "ChatGuard settings saved and applied immediately.");
        FillSnapshot(tree);
    }

    private void HandleActionRequest(ITreeAttribute tree)
    {
        string action = tree.GetString("action", "").Trim().ToLowerInvariant();
        string targetUid = tree.GetString("targetUid", "").Trim();
        string targetName = tree.GetString("targetName", "").Trim();
        string reason = tree.GetString("reason", "").Trim();
        string actor = tree.GetString("actor", "WebPanel").Trim();
        int minutes = Math.Clamp(tree.GetInt("minutes", 10), 1, MaxManualMuteMinutes);
        bool permanent = tree.GetBool("permanent", false);
        var player = FindOnline(targetUid, targetName);
        if (player != null)
        {
            targetUid = player.PlayerUID;
            targetName = player.PlayerName;
        }

        switch (action)
        {
            case "mute":
                if (player == null)
                    throw new InvalidOperationException("The player must be online for a new ChatGuard mute so the sanction can be bound to the authoritative PlayerUID.");
                targetUid = player.PlayerUID;
                targetName = player.PlayerName;
                string muteReason = string.IsNullOrWhiteSpace(reason) ? "Manual ChatGuard mute" : reason;
                string durationLabel = permanent ? "permanent" : FormatMinutes(minutes);
                Mute(targetUid, targetName, minutes, muteReason, actor, persist: true, permanent: permanent);
                mutesIssued++;
                WriteAudit(targetUid, targetName, GlobalConstants.GeneralChatGroup, "ManualMute", "Mute", $"{muteReason} ({durationLabel})", "", actor);
                Notify(player, permanent
                    ? $"You were permanently muted. Reason: {muteReason}"
                    : $"You were muted for {durationLabel}. Reason: {muteReason}");
                tree.SetBool("success", true);
                tree.SetString("message", permanent
                    ? $"Permanently muted {targetName}."
                    : $"Muted {targetName} for {durationLabel}.");
                break;
            case "unmute":
                if (!TryResolveKnownTarget(string.IsNullOrWhiteSpace(targetUid) ? targetName : targetUid, out targetUid, out targetName, out player) ||
                    !state.Mutes.Remove(targetUid))
                    throw new InvalidOperationException("No matching active ChatGuard mute was found.");
                string unmuteReason = string.IsNullOrWhiteSpace(reason) ? "Manual staff unmute" : reason;
                SaveState();
                WriteAudit(targetUid, targetName, GlobalConstants.GeneralChatGroup, "ManualUnmute", "Unmute", unmuteReason, "", actor);
                tree.SetBool("success", true);
                tree.SetString("message", $"Unmuted {targetName}. Reason: {unmuteReason}");
                if (player != null) Notify(player, $"Your ChatGuard mute was removed. Reason: {unmuteReason}");
                break;
            case "warn":
                if (player == null) throw new InvalidOperationException("The player must be online to issue a ChatGuard warning.");
                int warnings = AddWarning(player.PlayerUID, player.PlayerName, UtcNowMs());
                warningsIssued++;
                string why = string.IsNullOrWhiteSpace(reason) ? "Manual staff warning" : reason;
                WriteAudit(player, GlobalConstants.GeneralChatGroup, "ManualWarning", "Warn", why, "", actor);
                Notify(player, $"Staff warning: {why} (active warnings: {warnings}/{config.Escalation.WarningsBeforeMute}).");
                tree.SetBool("success", true);
                tree.SetString("message", $"Warned {player.PlayerName}. Active warnings: {warnings}/{config.Escalation.WarningsBeforeMute}.");
                break;
            case "clearwarnings":
                if (!TryResolveKnownTarget(string.IsNullOrWhiteSpace(targetUid) ? targetName : targetUid, out targetUid, out targetName, out player) ||
                    !state.Warnings.ContainsKey(targetUid))
                    throw new InvalidOperationException("No matching active ChatGuard warnings were found.");
                int removedWarnings = GetActiveWarningCount(targetUid, UtcNowMs());
                state.Warnings.Remove(targetUid);
                string clearReason = string.IsNullOrWhiteSpace(reason) ? "Manual warning reset" : reason;
                SaveState();
                WriteAudit(targetUid, targetName, GlobalConstants.GeneralChatGroup, "ManualWarningClear", "ClearWarnings", $"{clearReason} ({removedWarnings} warning(s) cleared)", "", actor);
                if (player != null) Notify(player, $"Your active ChatGuard warnings were cleared. Reason: {clearReason}");
                tree.SetBool("success", true);
                tree.SetString("message", $"Cleared {removedWarnings} active warning(s) for {targetName}. Reason: {clearReason}");
                break;
            default:
                throw new InvalidOperationException("Unknown ChatGuard action.");
        }
        FillSnapshot(tree);
    }

    private void FillSnapshot(ITreeAttribute tree)
    {
        tree.SetBool("stateStorageReady", stateStorageReady);
        tree.SetString("stateStorageError", stateStorageReady ? stateWriter.LastError : "Persistent moderation state is protected because it could not be read.");
        tree.SetLong("auditDroppedEntries", auditWriter.Dropped);
        tree.SetString("auditStorageError", auditWriter.LastError);
        tree.SetInt("retainedSamplePlayers", samples.Count);
        CleanupExpiredState(save: true);
        tree.SetBool("available", true);
        tree.SetString("version", Version);
        tree.SetBool("enabled", config.Enabled);
        tree.SetString("mode", config.Mode);
        tree.SetBool("suiteIntegrated", IsSuiteInstalled());
        tree.SetLong("scannedMessages", scannedMessages);
        tree.SetLong("matchedRules", matchedRules);
        tree.SetLong("blockedMessages", blockedMessages);
        tree.SetLong("warningsIssued", warningsIssued);
        tree.SetLong("mutesIssued", mutesIssued);
        tree.SetLong("kicksIssued", kicksIssued);
        tree.SetInt("activeMutes", state.Mutes.Count);
        tree.SetString("configJson", JsonConvert.SerializeObject(config));
        tree.SetString("recentJson", JsonConvert.SerializeObject(recentAudit.OrderByDescending(x => x.TimestampUtcMs).Take(100).ToList()));
        tree.SetString("mutesJson", JsonConvert.SerializeObject(state.Mutes.Values.OrderBy(x => x.Permanent ? long.MaxValue : x.ExpiresUtcMs).ToList()));
        tree.SetString("warningsJson", JsonConvert.SerializeObject(state.Warnings.Select(x => new
        {
            PlayerUid = x.Key,
            x.Value.PlayerName,
            ActiveWarnings = GetActiveWarningCount(x.Key, UtcNowMs()),
            WarningUtcMs = x.Value.WarningUtcMs
        }).OrderByDescending(x => x.ActiveWarnings).ThenBy(x => x.PlayerName).ToList()));
        tree.SetString("ruleStatsJson", JsonConvert.SerializeObject(BuildRuleStats()));
        tree.SetString("onlinePlayersJson", JsonConvert.SerializeObject(api.World.AllOnlinePlayers.OfType<IServerPlayer>()
            .Select(p => new
            {
                p.PlayerUID,
                p.PlayerName,
                Role = p.Role?.Code ?? p.Role?.Name ?? "",
                Exempt = IsExempt(p),
                ActiveWarnings = GetActiveWarningCount(p.PlayerUID, UtcNowMs()),
                Muted = state.Mutes.TryGetValue(p.PlayerUID, out var mute) && IsMuteActive(mute, UtcNowMs())
            })
            .OrderBy(p => p.PlayerName)
            .ToList()));
    }

    private void HandleTestRequest(ITreeAttribute tree)
    {
        string target = tree.GetString("targetName", tree.GetString("targetUid", "")).Trim();
        string message = tree.GetString("testMessage", tree.GetString("message", ""));
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("Enter a message to test.");
        tree.SetBool("success", true);
        tree.SetString("message", "Dry-run complete. No moderation state or counters were changed.");
        tree.SetString("testJson", BuildRuleTestJson(target, message));
        FillSnapshot(tree);
    }

    private object[] BuildRuleStats()
    {
        return new object[]
        {
            RuleStat("Spam", config.Spam),
            RuleStat("Repeat", config.Repeat),
            RuleStat("Caps", config.Caps),
            RuleStat("Link", config.Links),
            RuleStat("MentionSpam", config.Mentions),
            RuleStat("MessageLength", config.Length),
            RuleStat("BlockedTerm", config.BlockedTerms)
        };
    }

    private object RuleStat(string rule, ChatRuleConfig settings) => new
    {
        Rule = rule,
        Label = HumanRule(rule),
        settings.Enabled,
        settings.Action,
        settings.MuteMinutes,
        Matches = ruleMatches.TryGetValue(rule, out long matches) ? matches : 0L
    };

    private string BuildRuleTestJson(string target, string body)
    {
        body ??= string.Empty;
        long now = UtcNowMs();
        string normalized = NormalizeForMatching(body);
        IServerPlayer? player = string.IsNullOrWhiteSpace(target) ? null : FindOnline("", target) ?? FindOnline(target, "");
        bool exempt = player != null && IsExempt(player);
        var checks = new List<object>();
        string matchedRule = "";
        string action = "Allow";
        string detail = "No enabled rule matched.";

        void Check(string rule, ChatRuleConfig ruleConfig, bool matched, string why)
        {
            checks.Add(new { Rule = rule, Label = HumanRule(rule), ruleConfig.Enabled, Matched = ruleConfig.Enabled && matched, Detail = why, ConfiguredAction = ruleConfig.Action });
            if (matchedRule.Length == 0 && ruleConfig.Enabled && matched)
            {
                matchedRule = rule;
                action = config.Mode.Equals("LogOnly", StringComparison.OrdinalIgnoreCase) ? "LogOnly" : ruleConfig.Action;
                detail = why;
            }
        }

        Check("MessageLength", config.Length, body.Length > config.Length.MaxCharacters,
            $"Length {body.Length}/{config.Length.MaxCharacters} characters.");
        bool termMatched = TryFindBlockedTerm(normalized, out string term);
        Check("BlockedTerm", config.BlockedTerms, termMatched,
            termMatched ? $"Matched blocked phrase '{term}'." : "No blocked phrase matched.");
        bool linkMatched = TryFindBlockedDomain(body, out string domain);
        Check("Link", config.Links, linkMatched,
            linkMatched ? $"Domain '{domain}' is not allowed." : "No unapproved domain matched.");
        int mentions = MentionRegex.Matches(body).Count;
        Check("MentionSpam", config.Mentions, mentions > config.Mentions.MaxMentions,
            $"Mentions {mentions}/{config.Mentions.MaxMentions}.");
        bool capsMatched = IsExcessiveCaps(body, out int upperPct, out int letters);
        Check("Caps", config.Caps, capsMatched,
            $"Uppercase {upperPct}% across {letters} letters; threshold {config.Caps.MaxUppercasePercent}% after {config.Caps.MinLetters} letters.");

        int repeats = 1;
        int spamCount = 1;
        if (player != null && samples.TryGetValue(player.PlayerUID, out var history))
        {
            long repeatCutoff = now - config.Repeat.WindowSeconds * 1000L;
            repeats += history.Count(s => s.TimestampUtcMs >= repeatCutoff && s.Normalized == normalized && normalized.Length > 0);
            long spamCutoff = now - config.Spam.WindowSeconds * 1000L;
            spamCount += history.Count(s => s.TimestampUtcMs >= spamCutoff);
        }
        Check("Repeat", config.Repeat, player != null && repeats >= config.Repeat.MaxRepeats,
            player == null ? "Supply an online player to test live repeat history." : $"Hypothetical repeat count {repeats}/{config.Repeat.MaxRepeats} in {config.Repeat.WindowSeconds}s.");
        Check("Spam", config.Spam, player != null && spamCount > config.Spam.MaxMessages,
            player == null ? "Supply an online player to test live flood history." : $"Hypothetical message count {spamCount}/{config.Spam.MaxMessages} in {config.Spam.WindowSeconds}s.");

        if (!config.Enabled)
        {
            matchedRule = "";
            action = "Allow";
            detail = "ChatGuard is disabled; no action would be enforced.";
        }
        else if (exempt)
        {
            matchedRule = "";
            action = "Allow";
            detail = "Target is exempt from ChatGuard rules.";
        }

        return JsonConvert.SerializeObject(new
        {
            DryRun = true,
            Target = target,
            ResolvedPlayer = player?.PlayerName ?? "",
            PlayerUid = player?.PlayerUID ?? "",
            Exempt = exempt,
            Enabled = config.Enabled,
            Mode = config.Mode,
            Matched = matchedRule.Length > 0,
            Rule = matchedRule,
            Action = action,
            Detail = detail,
            Checks = checks
        });
    }

    public string BuildGuiSnapshotJson()
    {
        CleanupExpiredState(save: true);
        long now = UtcNowMs();
        return JsonConvert.SerializeObject(new
        {
            version = Version,
            enabled = config.Enabled,
            mode = config.Mode,
            suiteIntegrated = IsSuiteInstalled(),
            scannedMessages,
            matchedRules,
            blockedMessages,
            warningsIssued,
            mutesIssued,
            kicksIssued,
            activeMutes = state.Mutes.Count,
            warnedPlayers = state.Warnings.Count(x => GetActiveWarningCount(x.Key, now) > 0),
            config,
            ruleStats = BuildRuleStats(),
            recent = recentAudit.OrderByDescending(x => x.TimestampUtcMs).Take(100).ToList(),
            mutes = state.Mutes.Values.Where(x => IsMuteActive(x, now)).OrderByDescending(x => x.Permanent).ThenBy(x => x.ExpiresUtcMs).ToList(),
            warnings = state.Warnings.Select(x => new
            {
                PlayerUid = x.Key,
                x.Value.PlayerName,
                ActiveWarnings = GetActiveWarningCount(x.Key, now),
                x.Value.WarningUtcMs
            }).Where(x => x.ActiveWarnings > 0).OrderByDescending(x => x.ActiveWarnings).ThenBy(x => x.PlayerName).ToList(),
            onlinePlayers = api.World.AllOnlinePlayers.OfType<IServerPlayer>().Select(p => new { p.PlayerUID, p.PlayerName, Role = p.Role?.Code ?? "", Exempt = IsExempt(p) }).OrderBy(p => p.PlayerName).ToList()
        });
    }

    public ChatGuardGuiResponse ProcessGuiRequest(IServerPlayer actor, ChatGuardGuiRequest request)
    {
        request ??= new ChatGuardGuiRequest();
        string operation = (request.Operation ?? "snapshot").Trim().ToLowerInvariant();
        var response = new ChatGuardGuiResponse { RequestId = request.RequestId, Success = true };
        try
        {
            switch (operation)
            {
                case "action":
                    var actionTree = new TreeAttribute();
                    actionTree.SetString("action", request.Action ?? "");
                    actionTree.SetString("targetName", request.Target ?? "");
                    actionTree.SetInt("minutes", Math.Clamp(request.Minutes, 1, MaxManualMuteMinutes));
                    actionTree.SetBool("permanent", request.Permanent);
                    actionTree.SetString("reason", request.Reason ?? "");
                    actionTree.SetString("actor", actor.PlayerName);
                    HandleActionRequest(actionTree);
                    response.Success = actionTree.GetBool("success", false);
                    response.Message = actionTree.GetString("message", "ChatGuard action completed.");
                    break;
                case "save":
                    var saveTree = new TreeAttribute();
                    saveTree.SetString("configJson", request.ConfigJson ?? "");
                    HandleSaveRequest(saveTree);
                    response.Success = saveTree.GetBool("success", false);
                    response.Message = saveTree.GetString("message", "ChatGuard settings saved.");
                    break;
                case "test":
                    response.TestJson = BuildRuleTestJson(request.Target ?? "", request.TestMessage ?? "");
                    response.Message = "Dry-run complete. No moderation state or counters were changed.";
                    break;
                case "reload":
                    config = LoadConfig();
                    response.Message = "ChatGuard configuration reloaded.";
                    break;
                default:
                    response.Message = "ChatGuard snapshot loaded.";
                    break;
            }
        }
        catch (Exception ex)
        {
            response.Success = false;
            response.Message = ex.Message;
        }
        response.SnapshotJson = BuildGuiSnapshotJson();
        return response;
    }

    private void RegisterCommands()
    {
#pragma warning disable CS0618
        api.RegisterCommand("chatguard", "Manage Newfie's ChatGuard", "status | gui | reload | mode enforce|logonly | recent [count] | help", OnChatGuardCommand, "controlserver");
        api.RegisterCommand("cgmute", "Mute a player in ChatGuard", "<player> <duration|perm> [reason]", OnMuteCommand, "controlserver");
        api.RegisterCommand("cgunmute", "Remove a ChatGuard mute", "<player|uid> [reason]", OnUnmuteCommand, "controlserver");
        api.RegisterCommand("cgwarn", "Issue a ChatGuard warning", "<player> [reason]", OnWarnCommand, "controlserver");
        api.RegisterCommand("cgwarnings", "Show active ChatGuard warnings for a player", "<player|uid>", OnWarningsCommand, "controlserver");
        api.RegisterCommand("cgclearwarnings", "Clear active ChatGuard warnings for a player", "<player|uid> [reason]", OnClearWarningsCommand, "controlserver");
        api.RegisterCommand("cgmutes", "List active ChatGuard mutes", "[count]", OnMutesCommand, "controlserver");
        api.RegisterCommand("cginfo", "Show ChatGuard moderation state for a player", "<player|uid>", OnInfoCommand, "controlserver");
#pragma warning restore CS0618
    }

    private void OnChatGuardCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        string op = (args.PopWord() ?? "status").Trim().ToLowerInvariant();
        if (op == "gui")
        {
            if (standaloneGuiOpener == null)
            {
                Reply(player, groupId, "The standalone ChatGuard GUI is unavailable on this server build.", false);
                return;
            }
            standaloneGuiOpener(player);
            return;
        }
        if (op == "reload")
        {
            config = LoadConfig();
            Reply(player, groupId, "ChatGuard configuration reloaded.");
            return;
        }
        if (op == "mode")
        {
            string mode = (args.PopWord() ?? "").Trim();
            if (!mode.Equals("enforce", StringComparison.OrdinalIgnoreCase) && !mode.Equals("logonly", StringComparison.OrdinalIgnoreCase))
            {
                Reply(player, groupId, "Usage: /chatguard mode enforce|logonly", false);
                return;
            }
            var candidate = JsonConvert.DeserializeObject<ChatGuardConfig>(JsonConvert.SerializeObject(config))!;
            candidate.Mode = mode.Equals("logonly", StringComparison.OrdinalIgnoreCase) ? "LogOnly" : "Enforce";
            try { CommitConfig(candidate); }
            catch (Exception ex) { Reply(player, groupId, "Could not save ChatGuard mode (" + ex.GetType().Name + "); previous settings remain active.", false); return; }
            Reply(player, groupId, "ChatGuard mode set to " + config.Mode + ".");
            return;
        }
        if (op == "recent")
        {
            int count = 10;
            string raw = args.PopWord() ?? "";
            if (int.TryParse(raw, out int parsed)) count = Math.Clamp(parsed, 1, 30);
            var list = recentAudit.OrderByDescending(x => x.TimestampUtcMs).Take(count).ToList();
            if (list.Count == 0) { Reply(player, groupId, "No ChatGuard actions recorded this session."); return; }
            foreach (var entry in list)
                Reply(player, groupId, $"{DateTimeOffset.FromUnixTimeMilliseconds(entry.TimestampUtcMs):HH:mm:ss} {entry.PlayerName} {entry.Rule}/{entry.Action}: {entry.Detail}");
            return;
        }
        if (op == "help")
        {
            Reply(player, groupId, "ChatGuard commands: /chatguard gui, /cgmute <player> <30m|2h|3d|1w|perm> [reason], /cgunmute <player> [reason], /cgwarn <player> [reason], /cgwarnings <player>, /cgclearwarnings <player> [reason], /cgmutes [count], /cginfo <player>.");
            return;
        }
        CleanupExpiredState(save: true);
        int warnedPlayers = state.Warnings.Count(x => GetActiveWarningCount(x.Key, UtcNowMs()) > 0);
        Reply(player, groupId, $"ChatGuard {Version}: enabled={config.Enabled}, mode={config.Mode}, scanned={scannedMessages}, blocked={blockedMessages}, activeMutes={state.Mutes.Count}, warnedPlayers={warnedPlayers}, suiteIntegration={IsSuiteInstalled()}.");
    }

    private void OnMuteCommand(IServerPlayer actor, int groupId, CmdArgs args)
    {
        string target = (args.PopWord() ?? "").Trim();
        string durationWord = (args.PopWord() ?? "").Trim();
        if (target.Length == 0 || !TryParseMuteDuration(durationWord, out int minutes, out bool permanent, out string durationLabel))
        {
            Reply(actor, groupId, "Usage: /cgmute <player> <30m|2h|3d|1w|perm> [reason]. Plain numbers are treated as minutes.", false);
            return;
        }
        var player = FindOnline("", target);
        if (player == null) { Reply(actor, groupId, "Player must be online for a new /cgmute so ChatGuard can bind the mute to the correct PlayerUID.", false); return; }
        string reason = (args.PopAll() ?? "").Trim();
        if (reason.Length == 0) reason = "Manual staff mute";
        Mute(player.PlayerUID, player.PlayerName, minutes, reason, actor.PlayerName, persist: true, permanent: permanent);
        mutesIssued++;
        WriteAudit(player, GlobalConstants.GeneralChatGroup, "ManualMute", "Mute", $"{reason} ({durationLabel})", "", actor.PlayerName);
        Notify(player, permanent
            ? $"You were permanently muted. Reason: {reason}"
            : $"You were muted for {durationLabel}. Reason: {reason}");
        Reply(actor, groupId, permanent
            ? $"Permanently muted {player.PlayerName}."
            : $"Muted {player.PlayerName} for {durationLabel}.");
    }

    private void OnUnmuteCommand(IServerPlayer actor, int groupId, CmdArgs args)
    {
        string target = (args.PopWord() ?? "").Trim();
        if (target.Length == 0) { Reply(actor, groupId, "Usage: /cgunmute <player|uid> [reason]", false); return; }
        if (!TryResolveKnownTarget(target, out string uid, out string name, out var player) || !state.Mutes.Remove(uid))
        {
            Reply(actor, groupId, "No matching active ChatGuard mute was found.", false);
            return;
        }
        string reason = (args.PopAll() ?? "").Trim();
        if (reason.Length == 0) reason = "Manual staff unmute";
        SaveState();
        WriteAudit(uid, name, GlobalConstants.GeneralChatGroup, "ManualUnmute", "Unmute", reason, "", actor.PlayerName);
        if (player != null) Notify(player, $"Your ChatGuard mute was removed. Reason: {reason}");
        Reply(actor, groupId, $"Unmuted {name}. Reason: {reason}");
    }

    private void OnWarnCommand(IServerPlayer actor, int groupId, CmdArgs args)
    {
        string target = (args.PopWord() ?? "").Trim();
        var player = FindOnline("", target);
        if (player == null) { Reply(actor, groupId, "Player must be online for /cgwarn.", false); return; }
        string reason = (args.PopAll() ?? "").Trim();
        if (reason.Length == 0) reason = "Manual staff warning";
        int count = AddWarning(player.PlayerUID, player.PlayerName, UtcNowMs());
        warningsIssued++;
        WriteAudit(player, GlobalConstants.GeneralChatGroup, "ManualWarning", "Warn", reason, "", actor.PlayerName);
        Notify(player, $"Staff warning: {reason} (active warnings: {count}/{config.Escalation.WarningsBeforeMute}).");
        Reply(actor, groupId, $"Warned {player.PlayerName}. Active warnings: {count}/{config.Escalation.WarningsBeforeMute}.");
    }

    private void OnWarningsCommand(IServerPlayer actor, int groupId, CmdArgs args)
    {
        string target = (args.PopWord() ?? "").Trim();
        if (target.Length == 0) { Reply(actor, groupId, "Usage: /cgwarnings <player|uid>", false); return; }
        if (!TryResolveKnownTarget(target, out string uid, out string name, out _))
        {
            Reply(actor, groupId, "No matching online player or ChatGuard moderation record was found.", false);
            return;
        }
        long now = UtcNowMs();
        int count = GetActiveWarningCount(uid, now);
        if (count == 0)
        {
            Reply(actor, groupId, $"{name} has no active ChatGuard warnings.");
            return;
        }
        long oldest = state.Warnings[uid].WarningUtcMs.Min();
        long expires = oldest + config.Escalation.WarningWindowMinutes * 60_000L;
        Reply(actor, groupId, $"{name}: {count}/{config.Escalation.WarningsBeforeMute} active warnings in the {config.Escalation.WarningWindowMinutes}m escalation window; oldest expires in {FormatRemaining(expires - now)}.");
    }

    private void OnClearWarningsCommand(IServerPlayer actor, int groupId, CmdArgs args)
    {
        string target = (args.PopWord() ?? "").Trim();
        if (target.Length == 0) { Reply(actor, groupId, "Usage: /cgclearwarnings <player|uid> [reason]", false); return; }
        if (!TryResolveKnownTarget(target, out string uid, out string name, out var player) || !state.Warnings.TryGetValue(uid, out var warning))
        {
            Reply(actor, groupId, "No matching active ChatGuard warnings were found.", false);
            return;
        }
        int removed = GetActiveWarningCount(uid, UtcNowMs());
        state.Warnings.Remove(uid);
        string reason = (args.PopAll() ?? "").Trim();
        if (reason.Length == 0) reason = "Manual warning reset";
        SaveState();
        WriteAudit(uid, name, GlobalConstants.GeneralChatGroup, "ManualWarningClear", "ClearWarnings", $"{reason} ({removed} warning(s) cleared)", "", actor.PlayerName);
        if (player != null) Notify(player, $"Your active ChatGuard warnings were cleared. Reason: {reason}");
        Reply(actor, groupId, $"Cleared {removed} active warning(s) for {name}. Reason: {reason}");
    }

    private void OnMutesCommand(IServerPlayer actor, int groupId, CmdArgs args)
    {
        CleanupExpiredState(save: true);
        int count = 10;
        string raw = (args.PopWord() ?? "").Trim();
        if (int.TryParse(raw, out int parsed)) count = Math.Clamp(parsed, 1, 30);
        var mutes = state.Mutes.Values
            .Where(x => IsMuteActive(x, UtcNowMs()))
            .OrderByDescending(x => x.Permanent)
            .ThenBy(x => x.ExpiresUtcMs)
            .Take(count)
            .ToList();
        if (mutes.Count == 0) { Reply(actor, groupId, "No active ChatGuard mutes."); return; }
        Reply(actor, groupId, $"Active ChatGuard mutes ({state.Mutes.Count} total):");
        long now = UtcNowMs();
        foreach (var mute in mutes)
        {
            string remaining = mute.Permanent ? "permanent" : FormatRemaining(mute.ExpiresUtcMs - now);
            Reply(actor, groupId, $"- {mute.PlayerName}: {remaining}; by {mute.Actor}; {mute.Reason}");
        }
    }

    private void OnInfoCommand(IServerPlayer actor, int groupId, CmdArgs args)
    {
        string target = (args.PopWord() ?? "").Trim();
        if (target.Length == 0) { Reply(actor, groupId, "Usage: /cginfo <player|uid>", false); return; }
        if (!TryResolveKnownTarget(target, out string uid, out string name, out var player))
        {
            Reply(actor, groupId, "No matching online player or ChatGuard moderation record was found.", false);
            return;
        }
        long now = UtcNowMs();
        int warnings = GetActiveWarningCount(uid, now);
        string muteText = "none";
        if (state.Mutes.TryGetValue(uid, out var mute) && IsMuteActive(mute, now))
            muteText = mute.Permanent ? $"permanent ({mute.Reason})" : $"{FormatRemaining(mute.ExpiresUtcMs - now)} remaining ({mute.Reason})";
        Reply(actor, groupId, $"{name}: {(player != null ? "online" : "offline/known")}; mute={muteText}; warnings={warnings}/{config.Escalation.WarningsBeforeMute}; warningWindow={config.Escalation.WarningWindowMinutes}m.");
    }

    private ChatGuardConfig LoadConfig()
    {
        ChatGuardConfig loaded;
        try { loaded = api.LoadModConfig<ChatGuardConfig>(ChatGuardConfig.ConfigFile) ?? new ChatGuardConfig(); }
        catch (Exception ex)
        {
            api.Logger.Warning("[ChatGuard] Could not read {0}: {1}. Using defaults.", ChatGuardConfig.ConfigFile, ex.Message);
            loaded = new ChatGuardConfig();
        }
        loaded.Clamp();
        try { api.StoreModConfig(loaded, ChatGuardConfig.ConfigFile); } catch { }
        return loaded;
    }

    private ChatGuardState LoadState()
    {
        try
        {
            var loaded = File.Exists(statePath)
                ? JsonStateRecovery.Read<ChatGuardState>(statePath, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                : new ChatGuardState();
            if (loaded.Mutes?.Any(x => x.Value == null) == true || loaded.Warnings?.Any(x => x.Value == null) == true) JsonStateRecovery.Preserve(statePath);
            loaded.Mutes = (loaded.Mutes ?? new()).Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            loaded.Warnings = (loaded.Warnings ?? new()).Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var warning in loaded.Warnings.Values) warning.WarningUtcMs ??= new();
            loaded.Mutes = new Dictionary<string, ChatGuardMuteRecord>(loaded.Mutes ?? new Dictionary<string, ChatGuardMuteRecord>(), StringComparer.OrdinalIgnoreCase);
            loaded.Warnings = new Dictionary<string, ChatGuardWarningState>(loaded.Warnings ?? new Dictionary<string, ChatGuardWarningState>(), StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch (Exception ex)
        {
            stateStorageReady = false;
            if (ex is System.Text.Json.JsonException || ex is InvalidDataException)
                try { JsonStateRecovery.Preserve(statePath); stateStorageReady = true; } catch { }
            api.Logger.Warning("[ChatGuard] Could not read state: {0}. Original retained/preserved; persistent writes enabled={1}.", ex.Message, stateStorageReady);
            return new ChatGuardState();
        }
    }

    private void CommitConfig(ChatGuardConfig candidate)
    {
        candidate.Clamp();
        api.StoreModConfig(candidate, ChatGuardConfig.ConfigFile);
        config = candidate; // Publish only after persistence succeeds.
    }

    private void SaveState()
    {
        if (!stateStorageReady) return;
        var snapshot = new ChatGuardState
        {
            Mutes = state.Mutes.ToDictionary(x => x.Key, x => new ChatGuardMuteRecord { PlayerUid = x.Value.PlayerUid, PlayerName = x.Value.PlayerName, CreatedUtcMs = x.Value.CreatedUtcMs, ExpiresUtcMs = x.Value.ExpiresUtcMs, Permanent = x.Value.Permanent, Reason = x.Value.Reason, Actor = x.Value.Actor }, StringComparer.OrdinalIgnoreCase),
            Warnings = state.Warnings.ToDictionary(x => x.Key, x => new ChatGuardWarningState { PlayerName = x.Value.PlayerName, WarningUtcMs = x.Value.WarningUtcMs.ToList() }, StringComparer.OrdinalIgnoreCase)
        };
        stateWriter.Queue(() => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(snapshot, Formatting.Indented)));
    }

    private void OnWorldSave()
    {
        SaveState();
        // Both workers write/flush their own queues. Never wait for disk on the game-thread
        // autosave callback; Dispose still allows each writer its bounded shutdown flush.
        if (!string.IsNullOrEmpty(stateWriter.LastError) || !string.IsNullOrEmpty(auditWriter.LastError))
            api.Logger.Warning("[ChatGuard] Persistence is retrying in the background; inspect ChatGuard storage diagnostics.");
    }

    private void CleanupExpiredState(bool save)
    {
        long now = UtcNowMs();
        if (!save && now - lastStateCleanup < 60_000) return;
        lastStateCleanup = now;
        bool changed = false;
        foreach (string uid in state.Mutes.Where(x => !x.Value.Permanent && x.Value.ExpiresUtcMs <= now).Select(x => x.Key).ToList())
        {
            state.Mutes.Remove(uid);
            changed = true;
        }
        long warningCutoff = now - config.Escalation.WarningWindowMinutes * 60_000L;
        foreach (var kv in state.Warnings.ToList())
        {
            kv.Value.WarningUtcMs.RemoveAll(x => x < warningCutoff);
            if (kv.Value.WarningUtcMs.Count == 0) { state.Warnings.Remove(kv.Key); changed = true; }
        }
        if (changed && save) SaveState();
    }

    private int AddWarning(string uid, string name, long nowMs)
    {
        if (!state.Warnings.TryGetValue(uid, out var warning))
        {
            warning = new ChatGuardWarningState { PlayerName = name };
            state.Warnings[uid] = warning;
        }
        warning.PlayerName = name;
        long cutoff = nowMs - config.Escalation.WarningWindowMinutes * 60_000L;
        warning.WarningUtcMs.RemoveAll(x => x < cutoff);
        warning.WarningUtcMs.Add(nowMs);
        SaveState();
        return warning.WarningUtcMs.Count;
    }

    private void Mute(string uid, string name, int minutes, string reason, string actor, bool persist, bool permanent = false)
    {
        long now = UtcNowMs();
        state.Mutes[uid] = new ChatGuardMuteRecord
        {
            PlayerUid = uid,
            PlayerName = name,
            CreatedUtcMs = now,
            ExpiresUtcMs = permanent ? 0 : now + Math.Clamp(minutes, 1, MaxManualMuteMinutes) * 60_000L,
            Permanent = permanent,
            Reason = reason,
            Actor = actor
        };
        if (persist) SaveState();
    }

    private static bool IsMuteActive(ChatGuardMuteRecord mute, long now) => mute.Permanent || mute.ExpiresUtcMs > now;

    private int GetActiveWarningCount(string uid, long now)
    {
        if (!state.Warnings.TryGetValue(uid, out var warning)) return 0;
        long cutoff = now - config.Escalation.WarningWindowMinutes * 60_000L;
        return warning.WarningUtcMs.Count(x => x >= cutoff);
    }

    private bool TryResolveKnownTarget(string target, out string uid, out string name, out IServerPlayer? onlinePlayer)
    {
        uid = "";
        name = target;
        onlinePlayer = FindOnline("", target) ?? FindOnline(target, "");
        if (onlinePlayer != null)
        {
            uid = onlinePlayer.PlayerUID;
            name = onlinePlayer.PlayerName;
            return true;
        }

        var mute = state.Mutes.FirstOrDefault(x =>
            x.Key.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            x.Value.PlayerName.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(mute.Key))
        {
            uid = mute.Key;
            name = string.IsNullOrWhiteSpace(mute.Value.PlayerName) ? target : mute.Value.PlayerName;
            return true;
        }

        var warning = state.Warnings.FirstOrDefault(x =>
            x.Key.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            x.Value.PlayerName.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(warning.Key))
        {
            uid = warning.Key;
            name = string.IsNullOrWhiteSpace(warning.Value.PlayerName) ? target : warning.Value.PlayerName;
            return true;
        }
        return false;
    }

    private static bool TryParseMuteDuration(string raw, out int minutes, out bool permanent, out string label)
    {
        minutes = 0;
        permanent = false;
        label = "";
        string value = (raw ?? "").Trim().ToLowerInvariant();
        if (value is "perm" or "permanent" or "forever")
        {
            permanent = true;
            label = "permanent";
            return true;
        }
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plainMinutes))
        {
            if (plainMinutes < 1 || plainMinutes > MaxManualMuteMinutes) return false;
            minutes = plainMinutes;
            label = FormatMinutes(minutes);
            return true;
        }
        Match match = DurationRegex.Match(value);
        if (!match.Success || !int.TryParse(match.Groups["value"].Value, out int amount) || amount < 1) return false;
        long multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "m" => 1L,
            "h" => 60L,
            "d" => 1440L,
            "w" => 10080L,
            _ => 0L
        };
        long total = amount * multiplier;
        if (total < 1 || total > MaxManualMuteMinutes) return false;
        minutes = (int)total;
        label = FormatMinutes(minutes);
        return true;
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes % 10080 == 0) return (minutes / 10080) + "w";
        if (minutes % 1440 == 0) return (minutes / 1440) + "d";
        if (minutes % 60 == 0) return (minutes / 60) + "h";
        return minutes + "m";
    }

    private static string FormatRemaining(long remainingMs)
    {
        if (remainingMs <= 0) return "expired";
        long totalMinutes = Math.Max(1, (long)Math.Ceiling(remainingMs / 60_000d));
        long days = totalMinutes / 1440;
        long hours = (totalMinutes % 1440) / 60;
        long minutes = totalMinutes % 60;
        if (days > 0) return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        if (hours > 0) return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        return $"{minutes}m";
    }

    private Queue<ChatSample> GetHistory(string uid)
    {
        PruneSamples(UtcNowMs());
        if (!samples.TryGetValue(uid, out var queue))
        {
            // Cap retained identities as well as each history. Prefer evicting the oldest
            // history; normal disconnect/reconnect retains the configured spam window.
            if (samples.Count >= 4096)
            {
                string oldest = samples.OrderBy(x => x.Value.Count == 0 ? 0 : x.Value.Last().TimestampUtcMs).First().Key;
                samples.Remove(oldest);
            }
            samples[uid] = queue = new Queue<ChatSample>();
        }
        return queue;
    }

    private void PruneSamples(long now)
    {
        if (now - lastSamplePrune < 60_000) return;
        lastSamplePrune = now;
        long cutoff = now - Math.Max(10, Math.Max(config.Spam.WindowSeconds, config.Repeat.WindowSeconds) + 5) * 1000L;
        foreach (var entry in samples.ToArray())
        {
            while (entry.Value.Count > 0 && entry.Value.Peek().TimestampUtcMs < cutoff) entry.Value.Dequeue();
            if (entry.Value.Count == 0) samples.Remove(entry.Key);
        }
    }

    private void AddSample(Queue<ChatSample> queue, long now, string normalized)
    {
        int maxWindow = Math.Max(config.Spam.WindowSeconds, config.Repeat.WindowSeconds);
        long cutoff = now - Math.Max(10, maxWindow + 5) * 1000L;
        while (queue.Count > 0 && queue.Peek().TimestampUtcMs < cutoff) queue.Dequeue();
        queue.Enqueue(new ChatSample { TimestampUtcMs = now, Normalized = normalized });
        while (queue.Count > 100) queue.Dequeue();
    }

    private bool IsExempt(IServerPlayer player)
    {
        if (config.ExemptPlayerUids.Any(x => x.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase))) return true;
        string roleCode = player.Role?.Code ?? "";
        string roleName = player.Role?.Name ?? "";
        return config.ExemptRoleCodes.Any(x => x.Equals(roleCode, StringComparison.OrdinalIgnoreCase) || x.Equals(roleName, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryFindBlockedTerm(string normalized, out string term)
    {
        foreach (string configured in config.BlockedTerms.Terms)
        {
            string needle = NormalizeForMatching(configured);
            if (needle.Length > 0)
            {
                string haystack = " " + normalized + " ";
                string boundedNeedle = " " + needle + " ";
                if (haystack.Contains(boundedNeedle, StringComparison.OrdinalIgnoreCase))
                {
                    term = configured;
                    return true;
                }
            }
        }
        term = "";
        return false;
    }

    private bool TryFindBlockedDomain(string body, out string domain)
    {
        foreach (Match match in UrlRegex.Matches(body))
        {
            string candidate = match.Value.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            string host = ExtractHost(candidate);
            if (host.Length == 0) continue;
            bool allowed = config.Links.AllowedDomains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
            if (!allowed) { domain = host; return true; }
        }
        domain = "";
        return false;
    }

    private static string ExtractHost(string candidate)
    {
        string value = candidate;
        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) value = "http://" + value;
        else if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? (uri.Host ?? "").Trim('.').ToLowerInvariant() : "";
    }

    private bool IsExcessiveCaps(string body, out int uppercasePercent, out int letters)
    {
        letters = 0;
        int upper = 0;
        foreach (char c in body)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (char.IsUpper(c)) upper++;
        }
        uppercasePercent = letters == 0 ? 0 : (int)Math.Round(upper * 100d / letters);
        return letters >= config.Caps.MinLetters && uppercasePercent >= config.Caps.MaxUppercasePercent;
    }

    private void WriteAudit(IServerPlayer player, int channelId, string rule, string action, string detail, string body, string actor) =>
        WriteAudit(player.PlayerUID, player.PlayerName, channelId, rule, action, detail, body, actor);

    private void WriteAudit(string playerUid, string playerName, int channelId, string rule, string action, string detail, string body, string actor)
    {
        body ??= string.Empty;
        int previewChars = config.AuditMessagePreviewCharacters;
        string preview = previewChars <= 0 ? "" : body.Length <= previewChars ? body : body[..previewChars] + "…";
        var entry = new ChatGuardAuditEntry
        {
            TimestampUtcMs = UtcNowMs(),
            PlayerUid = playerUid ?? string.Empty,
            PlayerName = playerName ?? string.Empty,
            ChannelId = channelId,
            Rule = rule,
            Action = action,
            Detail = detail,
            MessagePreview = preview,
            Actor = actor
        };
        recentAudit.Add(entry);
        if (recentAudit.Count > 200) recentAudit.RemoveRange(0, recentAudit.Count - 200);
        auditWriter.Queue(JsonConvert.SerializeObject(entry));
    }

    private void Notify(IServerPlayer player, string message)
    {
        if (!config.NotifyPlayers) return;
        try { player.SendMessage(GlobalConstants.GeneralChatGroup, config.NotificationPrefix + " " + message, EnumChatType.Notification); } catch { }
    }

    private IServerPlayer? FindOnline(string uid, string name) =>
        api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p =>
            (!string.IsNullOrWhiteSpace(uid) && p.PlayerUID.Equals(uid, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(name) && p.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase)));

    private bool IsSuiteInstalled() => api.ModLoader.Mods.Any(m => string.Equals(m?.Info?.ModID, "newfiesserveressentialssuite", StringComparison.OrdinalIgnoreCase));

    private static ChatGuardDecision Allow() => new() { Blocked = false, Action = "Allow" };

    private static string HumanRule(string rule) => rule switch
    {
        "Spam" => "message flooding",
        "Repeat" => "repeated messages",
        "Caps" => "excessive caps",
        "Link" => "unapproved link",
        "MentionSpam" => "mention spam",
        "MessageLength" => "message length",
        "BlockedTerm" => "blocked phrase",
        _ => rule
    };

    private static string ExtractBody(string raw, string? playerName)
    {
        string plain = WebUtility.HtmlDecode(TagRegex.Replace(raw ?? "", string.Empty)).Trim();
        string name = (playerName ?? "").Trim();
        if (name.Length > 0)
        {
            string prefix = name + ":";
            if (plain.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) plain = plain[prefix.Length..].TrimStart();
        }
        return plain;
    }

    public static string NormalizeForMatching(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string decomposed = WebUtility.HtmlDecode(TagRegex.Replace(value, string.Empty)).Normalize(NormalizationForm.FormD).ToLowerInvariant();
        var sb = new StringBuilder(decomposed.Length);
        bool previousSpace = false;
        foreach (char c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) { sb.Append(c); previousSpace = false; continue; }
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (!previousSpace && sb.Length > 0) { sb.Append(' '); previousSpace = true; }
            }
        }
        return sb.ToString().Trim();
    }

    private void CleanupDecisionCache(long now)
    {
        if (recentDecisions.Count < 128) return;
        foreach (string key in recentDecisions.Where(x => now - x.Value.CreatedUtcMs > 1000).Select(x => x.Key).ToList()) recentDecisions.Remove(key);
        if (recentDecisions.Count > 256)
        {
            foreach (string key in recentDecisions.OrderBy(x => x.Value.CreatedUtcMs).Take(recentDecisions.Count - 192).Select(x => x.Key).ToList()) recentDecisions.Remove(key);
        }
    }

    private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string FormatDurationSeconds(int seconds)
    {
        if (seconds < 60) return seconds + "s";
        int minutes = (int)Math.Ceiling(seconds / 60d);
        if (minutes < 60) return minutes + "m";
        int hours = (int)Math.Ceiling(minutes / 60d);
        return hours + "h";
    }

    private static void Reply(IServerPlayer player, int groupId, string message, bool success = true) =>
        player.SendMessage(groupId, message, success ? EnumChatType.CommandSuccess : EnumChatType.CommandError);
}
