using System;
using Newfies.ChatProtocol;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using BlockLogger.Services;
using BlockLogger.Integrations;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class ChatDisplayConfig
{
    public const string ConfigFile = "NewfiesChatDisplay.json";
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool ShowRoleBeforeName { get; set; } = false;
    public bool BoldPlayerName { get; set; } = true;
    public bool ShowTimestampBeforeMessage { get; set; } = false;
    public string ChatFormat { get; set; } = ChatLayout.DefaultFormat;
    public string TimestampFormat { get; set; } = "HH:mm";
    public bool UseUtcTimestamp { get; set; } = false;
    // Chat formatting is intentionally scoped to vanilla General chat by default.
    // Third-party proximity/RP chat groups pass through untouched unless explicitly opted in.
    public bool FormatGeneralChat { get; set; } = true;
    public bool FormatOtherChatGroups { get; set; } = false;
    public List<int> AdditionalFormattedChatGroupIds { get; set; } = new();

    public bool MarkdownEnabled { get; set; } = true;
    public bool MarkdownAllowColors { get; set; } = false;
    public bool MarkdownAllowLinks { get; set; } = false;
    // Empty means all roles. Entries match role codes, not display names.
    public List<string> MarkdownAllowedRoleCodes { get; set; } = new();

    public void Clamp()
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion);
        if (ChatLayout.Validate(ChatFormat) != null) ChatFormat = ChatLayout.DefaultFormat;
        TimestampFormat = string.IsNullOrWhiteSpace(TimestampFormat) ? "HH:mm" : TimestampFormat.Trim();
        if (TimestampFormat.Length > 40) TimestampFormat = TimestampFormat[..40];
        try { _ = DateTime.Now.ToString(TimestampFormat); }
        catch { TimestampFormat = "HH:mm"; }
        MarkdownAllowedRoleCodes ??= new();
        MarkdownAllowedRoleCodes = MarkdownAllowedRoleCodes.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Where(x => x.Length <= 80).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
        AdditionalFormattedChatGroupIds ??= new List<int>();
        AdditionalFormattedChatGroupIds = AdditionalFormattedChatGroupIds
            .Where(id => id >= 0 && id != GlobalConstants.GeneralChatGroup)
            .Distinct()
            .OrderBy(id => id)
            .Take(64)
            .ToList();
    }

    public static ChatDisplayConfig Load(ICoreServerAPI api)
    {
        var cfg = ModConfigRecovery.LoadOrNull<ChatDisplayConfig>(api, ConfigFile, "ChatDisplay") ?? new ChatDisplayConfig();
        cfg.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, cfg, ConfigFile);
        return cfg;
    }
}

public sealed class ChatDisplaySnapshot
{
    public bool Available { get; set; } = true;
    public bool ShowRoleBeforeName { get; set; }
    public bool BoldPlayerName { get; set; }
    public bool ShowTimestampBeforeMessage { get; set; }
    public string ChatFormat { get; set; } = ChatLayout.DefaultFormat;
    public string TimestampFormat { get; set; } = "HH:mm";
    public bool UseUtcTimestamp { get; set; }
    public bool FormatGeneralChat { get; set; } = true;
    public bool FormatOtherChatGroups { get; set; }
    public List<int> AdditionalFormattedChatGroupIds { get; set; } = new();
    public bool MarkdownEnabled { get; set; } = true;
    public bool MarkdownAllowColors { get; set; } = false;
    public bool MarkdownAllowLinks { get; set; } = false;
    // Empty means all roles. Entries match role codes, not display names.
    public List<string> MarkdownAllowedRoleCodes { get; set; } = new();
    public bool UseRoleNameColors { get; set; }
    public string? RoleColorRevision {get;set;}
    public Dictionary<string, string> RoleNameColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Preview { get; set; } = "Player: Hello world";
    public string ConfigFile { get; set; } = ChatDisplayConfig.ConfigFile;
}

public sealed class ChatDisplaySettingsRequest
{
    public bool ShowRoleBeforeName { get; set; }
    public bool BoldPlayerName { get; set; }
    public bool ShowTimestampBeforeMessage { get; set; }
    public string? ChatFormat { get; set; }
    public string TimestampFormat { get; set; } = "HH:mm";
    public bool UseUtcTimestamp { get; set; }
    public bool FormatGeneralChat { get; set; } = true;
    public bool FormatOtherChatGroups { get; set; }
    public List<int> AdditionalFormattedChatGroupIds { get; set; } = new();
    // Nullable new fields preserve settings when older Shift+L clients omit them.
    public bool? MarkdownEnabled { get; set; }
    public bool? MarkdownAllowColors { get; set; }
    public bool? MarkdownAllowLinks { get; set; }
    public List<string>? MarkdownAllowedRoleCodes { get; set; }
    public bool UseRoleNameColors { get; set; }
    public string? RoleColorRevision {get;set;}
    public Dictionary<string, string> RoleNameColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Optional server-side chat presentation layer. Fresh installs bold player names by default,
/// while role prefixes and timestamps remain disabled until explicitly enabled. When formatting
/// is active, the normal player-chat line is rewritten in-place and left on Vintage Story's
/// normal delivery path. Commands are never touched.
/// </summary>
public sealed class ChatDisplayService : IDisposable
{
    public const string QueryEvent = "newfiesse:chat-display-query";
    public const string ChangedEvent = "newfiesse:chat-display-changed";
    public const string ModerationTextQueryEvent = "newfiesse:chat-moderation-text-query";
    public const string PresentationQueryEvent = "newfiesse:chat-presentation-query";
    private sealed record ModerationLine(string Raw, ChatMarkdown.Result Result, long CreatedTicks)
    { public string? EmittedBody { get; init; } public string? EmittedLine { get; init; } }
    private readonly Dictionary<(string Uid, int Channel), ModerationLine> moderationLines = new();
    public const string SenderQueryEvent = "newfiesse:chat-sender-query";
    private long lastChatFailureTicks;

    private readonly ICoreServerAPI api;
    private readonly global::System.Func<IPlayerRole?, string>? resolveConfiguredNameColor;
    private readonly global::System.Func<string, string, string>? resolveDisplayName;
    private ChatDisplayConfig config;
    private bool registered;
    private bool disposed;

    public ChatDisplayService(
        ICoreServerAPI api,
        global::System.Func<IPlayerRole?, string>? resolveConfiguredNameColor = null,
        global::System.Func<string, string, string>? resolveDisplayName = null)
    {
        this.api = api;
        this.resolveConfiguredNameColor = resolveConfiguredNameColor;
        this.resolveDisplayName = resolveDisplayName;
        config = ChatDisplayConfig.Load(api);
    }

    public ChatDisplayConfig Config => config;

    public void Start()
    {
        if (registered) return;
        registered = true;
        api.Event.PlayerChat += OnPlayerChat;
        api.Event.RegisterEventBusListener(OnChatDisplayQuery, filterByEventName: QueryEvent);
        api.Event.RegisterEventBusListener(OnChatSenderQuery, filterByEventName: SenderQueryEvent);
        api.Event.RegisterEventBusListener(OnModerationTextQuery, filterByEventName: ModerationTextQueryEvent);
        api.Event.RegisterEventBusListener(OnPresentationQuery, filterByEventName: PresentationQueryEvent);
        if (api.ChatCommands.Get("chatformat") == null)
        {
            api.ChatCommands.Create("chatformat")
                .WithDescription("Show your private chat formatting guide and available styles.")
                .RequiresPlayer()
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(OnChatFormatCommand);
        }
        else
        {
            api.Logger.Warning("[ServerEssentials/ChatDisplay] /chatformat is already registered; the existing command was preserved.");
        }
        api.Logger.Notification(
            "[ServerEssentials/ChatDisplay] Ready. RolePrefix={0}, BoldName={1}, Timestamp={2}, General={3}, OtherGroups={4}, AdditionalGroups={5}.",
            config.ShowRoleBeforeName,
            config.BoldPlayerName,
            config.ShowTimestampBeforeMessage,
            config.FormatGeneralChat,
            config.FormatOtherChatGroups,
            string.Join(",", config.AdditionalFormattedChatGroupIds));
    }

    private TextCommandResult OnChatFormatCommand(TextCommandCallingArgs args)
    {
        if (disposed) return TextCommandResult.Error("Chat formatting is unavailable.");
        if (args.Caller?.Player is not IServerPlayer player)
            return TextCommandResult.Error("Run /chatformat in game.");

        bool roleAllowed = config.MarkdownAllowedRoleCodes.Count == 0 ||
            config.MarkdownAllowedRoleCodes.Contains(player.Role?.Code ?? "", StringComparer.OrdinalIgnoreCase);
        string status = !config.MarkdownEnabled ? "Formatting is disabled on this server." :
            !roleAllowed ? "Formatting is not enabled for your role." :
            "Formatting is enabled for your role.";
        string scope = config.FormatOtherChatGroups ? "All other chat groups" :
            config.AdditionalFormattedChatGroupIds.Count > 0 ?
                "Other group IDs: " + string.Join(", ", config.AdditionalFormattedChatGroupIds) : "Other groups: off";
        bool available = config.MarkdownEnabled && roleAllowed;
        // Returning a command result uses the game's private reply path, never PlayerChat/broadcast.
        return TextCommandResult.Success(
            "<strong>Chat formatting guide</strong>\n" + status + "\n" +
            "General chat: " + (config.FormatGeneralChat ? "on" : "off") + "; " + scope + ".\n" +
            "Bold: <code>**text**</code> or <code>__text__</code>\n" +
            "Italic: <code>*text*</code> or <code>_text_</code>\n" +
            "Bold + italic: <code>***text***</code>\n" +
            "Code: <code>`literal text`</code>\n" +
            "Nested styles: <code>**bold with *italic***</code>\n" +
            "Literal symbols: <code>\\*text\\*</code> displays *text*.\n" +
            "Colors (" + (available && config.MarkdownAllowColors ? "enabled" : "off for you") +
            "): <code>[color=#80C8FF]text[/color]</code>\n" +
            "Links (" + (available && config.MarkdownAllowLinks ? "enabled" : "off for you") +
            "): <code>[label](https://example.com)</code>\n" +
            "Use these in a normal chat message. Commands stay unchanged; raw HTML, underline and strikethrough are unsupported.");
    }

    public ChatDisplaySnapshot GetSnapshot() => new()
    {
        ShowRoleBeforeName = config.ShowRoleBeforeName,
        BoldPlayerName = config.BoldPlayerName,
        ShowTimestampBeforeMessage = config.ShowTimestampBeforeMessage,
        ChatFormat = config.ChatFormat,
        TimestampFormat = config.TimestampFormat,
        UseUtcTimestamp = config.UseUtcTimestamp,
        FormatGeneralChat = config.FormatGeneralChat,
        FormatOtherChatGroups = config.FormatOtherChatGroups,
        AdditionalFormattedChatGroupIds = config.AdditionalFormattedChatGroupIds.ToList(),
        MarkdownEnabled = config.MarkdownEnabled,
        MarkdownAllowColors = config.MarkdownAllowColors,
        MarkdownAllowLinks = config.MarkdownAllowLinks,
        MarkdownAllowedRoleCodes = config.MarkdownAllowedRoleCodes.ToList(),
        Preview = BuildPreview()
    };

    public (bool Success, string Message, ChatDisplaySnapshot Snapshot) SaveSettings(ChatDisplaySettingsRequest? request)
    {
        if (request == null) return (false, "Chat display settings are required.", GetSnapshot());

        var candidate = new ChatDisplayConfig
        {
            SchemaVersion = ChatDisplayConfig.CurrentSchemaVersion,
            MarkdownEnabled = request.MarkdownEnabled ?? config.MarkdownEnabled,
            MarkdownAllowColors = request.MarkdownAllowColors ?? config.MarkdownAllowColors,
            MarkdownAllowLinks = request.MarkdownAllowLinks ?? config.MarkdownAllowLinks,
            MarkdownAllowedRoleCodes = request.MarkdownAllowedRoleCodes?.ToList() ?? config.MarkdownAllowedRoleCodes.ToList(),
            ShowRoleBeforeName = request.ShowRoleBeforeName,
            BoldPlayerName = request.BoldPlayerName,
            ShowTimestampBeforeMessage = request.ShowTimestampBeforeMessage,
            ChatFormat = request.ChatFormat ?? config.ChatFormat,
            TimestampFormat = request.TimestampFormat,
            UseUtcTimestamp = request.UseUtcTimestamp,
            FormatGeneralChat = request.FormatGeneralChat,
            FormatOtherChatGroups = request.FormatOtherChatGroups,
            AdditionalFormattedChatGroupIds = request.AdditionalFormattedChatGroupIds?.ToList() ?? new List<int>()
        };

        string supplied = (candidate.TimestampFormat ?? "").Trim();
        if (candidate.ShowTimestampBeforeMessage && string.IsNullOrWhiteSpace(supplied))
            return (false, "Timestamp format cannot be empty while timestamps are enabled.", GetSnapshot());
        try { _ = DateTime.Now.ToString(string.IsNullOrWhiteSpace(supplied) ? "HH:mm" : supplied); }
        catch (Exception ex) { return (false, "Invalid timestamp format: " + ex.Message, GetSnapshot()); }

        string? formatError = ChatLayout.Validate(candidate.ChatFormat);
        if (formatError != null) return (false, formatError, GetSnapshot());
        candidate.Clamp();
        config = candidate;
        moderationLines.Clear();
        BlockLogger.Services.SuiteConfigStorage.Store(api, config, ChatDisplayConfig.ConfigFile);
        NotifyChanged();
        return (true, "Chat display settings saved and applied immediately.", GetSnapshot());
    }

    public string Reload()
    {
        config = ChatDisplayConfig.Load(api);
        moderationLines.Clear();
        NotifyChanged();
        return "Chat display configuration reloaded.";
    }

    private void NotifyChanged()
    {
        try
        {
            var tree = new TreeAttribute();
            tree.SetBool("showRoleBeforeName", config.ShowRoleBeforeName);
            tree.SetBool("boldPlayerName", config.BoldPlayerName);
            api.Event.PushEvent(ChangedEvent, tree);
        }
        catch
        {
            // Discord is optional; presentation settings must still save even if no listener exists.
        }
    }

    private void OnChatDisplayQuery(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (disposed || data is not ITreeAttribute tree) return;
        tree.SetBool("available", true);
        tree.SetBool("showRoleBeforeName", config.ShowRoleBeforeName);
        tree.SetBool("boldPlayerName", config.BoldPlayerName);
        tree.SetBool("showTimestampBeforeMessage", config.ShowTimestampBeforeMessage);
        tree.SetString("chatFormat", config.ChatFormat);
        tree.SetString("timestampFormat", config.TimestampFormat ?? "HH:mm");
        tree.SetBool("useUtcTimestamp", config.UseUtcTimestamp);
        tree.SetBool("formatGeneralChat", config.FormatGeneralChat);
        tree.SetBool("formatOtherChatGroups", config.FormatOtherChatGroups);
        tree.SetString("additionalFormattedChatGroupIds", string.Join(",", config.AdditionalFormattedChatGroupIds));
    }

    private void OnChatSenderQuery(string eventName,ref EnumHandling handling,IAttribute data)
    {
        if(disposed || data is not ITreeAttribute tree)return;
        var uid=tree.GetString("playerUid","");
        var player=api.World.AllOnlinePlayers.FirstOrDefault(p=>p.PlayerUID==uid);
        if(player==null)return;
        string display=player.PlayerName??"";
        try {if(resolveDisplayName!=null)display=resolveDisplayName(uid,display)??display;}
        catch {return;}
        tree.SetString("displayName",display);tree.SetBool("available",true);
    }

    private bool MarkdownAllowed(IServerPlayer player, int channelId) => config.MarkdownEnabled && ShouldFormatChannel(channelId) &&
        (config.MarkdownAllowedRoleCodes.Count == 0 || config.MarkdownAllowedRoleCodes.Contains(player.Role?.Code ?? "", StringComparer.OrdinalIgnoreCase));

    private ChatMarkdown.Result PrepareMarkdown(IServerPlayer player, int channel, string body, bool allowAliases = true)
    {
        var key = (player.PlayerUID, channel);
        long now = DateTime.UtcNow.Ticks;
        // Raw input, emitted VTML and visible text share a decision regardless of event
        // handler order. Keep one short-lived entry per sender/channel, with a hard cap.
        if (moderationLines.TryGetValue(key, out var prior) && now - prior.CreatedTicks <= TimeSpan.FromMilliseconds(100).Ticks &&
            (body == prior.Raw || (allowAliases && (body == prior.Result.Vtml || body == prior.Result.ModerationText || body == prior.EmittedBody || body == prior.EmittedLine)))) return prior.Result;
        var result = ChatMarkdown.Format(body, config.MarkdownAllowColors, config.MarkdownAllowLinks);
        if (moderationLines.Count >= 1024) moderationLines.Clear();
        moderationLines[key] = new(body, result, now);
        return result;
    }

    private ChatMarkdown.Result? EmittedResult(IServerPlayer player, int channel, string body)
    {
        if (moderationLines.TryGetValue((player.PlayerUID ?? "", channel), out var line) &&
            DateTime.UtcNow.Ticks - line.CreatedTicks <= TimeSpan.FromMilliseconds(100).Ticks &&
            (body == line.EmittedBody || body == line.EmittedLine)) return line.Result;
        return null;
    }

    private void OnModerationTextQuery(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (disposed || data is not ITreeAttribute tree) return;
        string uid = tree.GetString("playerUid", "");
        var player = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p => p.PlayerUID == uid);
        int channel = tree.GetInt("channelId", -1);
        if (player == null) return;
        string body = tree.GetString("body", "");
        var result = EmittedResult(player, channel, tree.GetString("rawLine", body)) ?? EmittedResult(player, channel, body);
        if (result == null && MarkdownAllowed(player, channel)) result = PrepareMarkdown(player, channel, body);
        if (result == null) return;
        tree.SetString("moderationText", result.ModerationText);
        tree.SetBool("available", true);
    }

    private void OnPresentationQuery(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (disposed || data is not ITreeAttribute tree) return;
        string uid = tree.GetString("playerUid", "");
        var player = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p => p.PlayerUID == uid);
        if (player == null) return;
        int channel = tree.GetInt("channelId", -1);
        string body = tree.GetString("body", "");
        if (body.TrimStart().StartsWith('/')) return;
        string visible = (EmittedResult(player, channel, tree.GetString("rawLine", body)) ?? EmittedResult(player, channel, body))?.ModerationText ??
            (MarkdownAllowed(player, channel) ? PrepareMarkdown(player, channel, body).ModerationText : body);
        // The same decision cache is used by the normal Suite/ChatGuard hooks. Early
        // relays therefore cannot publish a blocked message or add another spam sample.
        var decision = ChatGuardControlBridge.Moderate(api, player, channel, visible);
        tree.SetString("visibleText", visible);
        tree.SetBool("blocked", decision.Blocked);
        tree.SetBool("available", true);
    }

    private void OnPlayerChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if (byPlayer == null || consumed.value || string.IsNullOrWhiteSpace(message)) return;

        string originalMessage=message;
        try
        {
        // Vintage Story 1.22.x supplies PlayerChat.message with its own sender VTML,
        // typically: <strong>PlayerName:</strong> actual message. Parse the body once so the
        // optional standalone ChatGuard module can make the moderation decision before this
        // service consumes/re-emits a formatted line. When ChatGuard is absent the event-bus
        // bridge is a no-op. ChatGuard also hooks PlayerChat directly, and its short decision
        // cache prevents this compatibility query from applying a rule twice.
        string displayName=byPlayer.PlayerName??string.Empty;
        if(resolveDisplayName!=null)displayName=resolveDisplayName(byPlayer.PlayerUID??string.Empty,displayName)??displayName;
        var boundary=ChatMessageBoundary.Parse(message,byPlayer.PlayerName,displayName,byPlayer.Role?.Name);
        var chatLine = ExtractPlayerChatPresentation(message, byPlayer.PlayerName);
        if(boundary.HasSender)chatLine=new ParsedPlayerChatLine {Body=boundary.Body,PlayerNameColor=chatLine.PlayerNameColor};

        // Commands also pass through PlayerChat on current Vintage Story builds. The raw
        // event can already contain the engine's "<strong>Name:</strong>" prefix, so checking
        // message.StartsWith("/") is not reliable. Detect the command after extracting the
        // real player-authored body and leave it completely untouched for ChatCommands.
        if (boundary.IsCommand) return;

        bool markdownActive = MarkdownAllowed(byPlayer, channelId);
        var markdown = markdownActive ? PrepareMarkdown(byPlayer, channelId, chatLine.Body, allowAliases: false) : null;
        // Moderation sees the text players will read, so punctuation cannot hide a word
        // across Markdown spans. Link destinations are also included in moderation text.
        var chatGuard = ChatGuardControlBridge.Moderate(api, byPlayer, channelId, markdown?.ModerationText ?? chatLine.Body);
        if (chatGuard.Available && chatGuard.Blocked)
        {
            consumed.value = true;
            return;
        }

        string configuredNameColor = resolveConfiguredNameColor?.Invoke(byPlayer.Role) ?? string.Empty;
        bool nicknameActive = !string.Equals(displayName, byPlayer.PlayerName ?? string.Empty, StringComparison.Ordinal);
        bool suiteFormattingActive = markdownActive || config.ShowRoleBeforeName ||
                                     config.ChatFormat != ChatLayout.DefaultFormat ||
                                     config.BoldPlayerName ||
                                     config.ShowTimestampBeforeMessage ||
                                     !string.IsNullOrWhiteSpace(configuredNameColor);
        // Compatibility boundary: never consume/re-emit a player-chat event outside the
        // explicitly configured chat groups. This preserves third-party proximity/RP chat
        // routing, data payloads and custom chat windows by default.
        if (!ShouldFormatChannel(channelId)) return;

        // A supporter nickname must not, by itself, turn Suite Chat Display into a chat
        // rebroadcaster. Doing that changes routing semantics and can break proximity/RP
        // chat mods or other handlers that depend on the original PlayerChat event. When
        // no Suite presentation formatting is otherwise active, replace only the trusted
        // sender prefix in-place and leave consumed=false so Vintage Story continues the
        // normal delivery path.
        if (!suiteFormattingActive)
        {
            if (nicknameActive)
                message = ReplaceSenderDisplayName(message, byPlayer.PlayerName, displayName);
            return;
        }

        string chatBody = chatLine.Body;

        string rolePrefix = "";
        if (config.ShowRoleBeforeName)
        {
            IPlayerRole? playerRole = byPlayer.Role;
            string role = playerRole?.Name ?? playerRole?.Code ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(role))
            {
                // Role.Color is the exact color configured for this role in serverconfig.json.
                // The role tag itself is bold; player-controlled text is always escaped.
                string roleColor = playerRole == null
                    ? "#FFFFFF"
                    : $"#{playerRole.Color.R:X2}{playerRole.Color.G:X2}{playerRole.Color.B:X2}";
                string safeRole = EscapeVtmlText(role.Trim());
                rolePrefix = $"<font color=\"{roleColor}\"><strong>[{safeRole}]</strong></font>";
            }
        }

        string timePrefix = "";
        if (config.ShowTimestampBeforeMessage)
        {
            DateTime now = config.UseUtcTimestamp ? DateTime.UtcNow : DateTime.Now;
            string stamp;
            try { stamp = now.ToString(config.TimestampFormat); }
            catch { stamp = now.ToString("HH:mm"); }
            timePrefix = EscapeVtmlText($"[{stamp}]");
        }

        string safePlayerName = EscapeVtmlText(displayName);
        string styledPlayerName = config.BoldPlayerName ? $"<strong>{safePlayerName}</strong>" : safePlayerName;
        string effectiveNameColor = !string.IsNullOrWhiteSpace(configuredNameColor)
            ? configuredNameColor
            : chatLine.PlayerNameColor;
        if (!string.IsNullOrWhiteSpace(effectiveNameColor))
            styledPlayerName = $"<font color=\"{effectiveNameColor}\">{styledPlayerName}</font>";
        // WebUtility.HtmlEncode encodes apostrophes as &#39;, but Vintage Story's
        // VTML chat renderer does not decode that HTML entity. Escape only characters
        // that can alter VTML markup and keep ordinary apostrophes readable.
        string safeMessage = markdown?.Vtml ?? EscapeVtmlText(chatBody);
        string formatted = ChatLayout.Render(config.ChatFormat, rolePrefix, styledPlayerName, timePrefix, safeMessage, EscapeVtmlText);
        // Canonical aliases are exact, short-lived and bounded. Presentation changes must
        // not add prefixes/suffixes to moderation or Discord/Web Console message bodies.
        var canonical = markdown ?? new ChatMarkdown.Result(safeMessage, chatBody);
        if (moderationLines.Count >= 1024) moderationLines.Clear();
        moderationLines[(byPlayer.PlayerUID ?? "", channelId)] = new(chatBody, canonical, DateTime.UtcNow.Ticks)
        {
            EmittedBody = ChatMessageBoundary.Parse(formatted, byPlayer.PlayerName, displayName, byPlayer.Role?.Name).Body,
            EmittedLine = formatted
        };

        // Keep the original Vintage Story chat delivery path. PlayerChat exposes message/data
        // by ref specifically so presentation mods can rewrite them without consuming the event.
        // Manual SendMessageToGroup() rebroadcasts can bypass or duplicate routing decisions made
        // by proximity/RP/group-chat handlers and were especially fragile once a nickname changed
        // the visible sender identity.
        message = formatted;
        }
        catch(Exception ex)
        {
            message=originalMessage;
            long now=DateTime.UtcNow.Ticks;
            if(now-lastChatFailureTicks>TimeSpan.FromSeconds(30).Ticks)
            {lastChatFailureTicks=now;api.Logger.Warning("[ChatDisplay] Formatting failed; original chat preserved. {0}: {1}",ex.GetType().Name,ex.Message);}
        }
    }

    private bool ShouldFormatChannel(int channelId)
    {
        if (channelId == GlobalConstants.GeneralChatGroup)
            return config.FormatGeneralChat;

        if (config.FormatOtherChatGroups)
            return true;

        return config.AdditionalFormattedChatGroupIds.Contains(channelId);
    }

    private sealed class ParsedPlayerChatLine
    {
        public string Body { get; init; } = string.Empty;
        public string PlayerNameColor { get; init; } = string.Empty;
    }

    private static ParsedPlayerChatLine ExtractPlayerChatPresentation(string message, string? playerName)
    {
        if (string.IsNullOrEmpty(message)) return new ParsedPlayerChatLine();

        string value = message.TrimStart();

        // Supporter/name-color mods can wrap Vintage Story's normal sender prefix in a
        // font tag, e.g. <font color="#FEC500"><strong>Name:</strong></font> body.
        // Preserve that trusted sender color while stripping the engine/mod-owned VTML
        // from the actual chat body. The color is accepted only when it is #RRGGBB.
        if (TryExtractColoredSenderPrefix(value, playerName, out string coloredBody, out string playerNameColor))
        {
            return new ParsedPlayerChatLine
            {
                Body = coloredBody,
                PlayerNameColor = playerNameColor
            };
        }

        // Normal VS player chat arrives as: <strong>Name:</strong> body
        const string strongOpen = "<strong>";
        const string strongClose = "</strong>";
        if (value.StartsWith(strongOpen, StringComparison.OrdinalIgnoreCase))
        {
            int close = value.IndexOf(strongClose, StringComparison.OrdinalIgnoreCase);
            if (close >= 0)
            {
                string sender = WebUtility.HtmlDecode(value[strongOpen.Length..close]).Trim();
                if (SenderMatchesPlayer(sender, playerName))
                {
                    int bodyStart = close + strongClose.Length;
                    return new ParsedPlayerChatLine
                    {
                        Body = bodyStart < value.Length ? value[bodyStart..].TrimStart() : string.Empty
                    };
                }
            }
        }

        // Defensive fallback for any server/version that supplies a plain "Name: body" line.
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            string plainPrefix = playerName.Trim() + ":";
            if (value.StartsWith(plainPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedPlayerChatLine
                {
                    Body = value[plainPrefix.Length..].TrimStart()
                };
            }
        }

        // If the event already contains only the player's body, keep it unchanged.
        return new ParsedPlayerChatLine { Body = value };
    }


    private static string ReplaceSenderDisplayName(string message, string? realName, string? displayName)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrWhiteSpace(realName) || string.IsNullOrWhiteSpace(displayName))
            return message;

        string safeDisplayName = EscapeVtmlText(displayName.Trim());
        string value = message;
        int leadingWhitespace = value.Length - value.TrimStart().Length;
        string prefixWhitespace = leadingWhitespace > 0 ? value[..leadingWhitespace] : string.Empty;
        string trimmed = value[leadingWhitespace..];

        // Preserve any trusted outer font/color wrapper and replace only the sender text.
        if (trimmed.StartsWith("<font", StringComparison.OrdinalIgnoreCase))
        {
            int fontOpenEnd = trimmed.IndexOf('>');
            if (fontOpenEnd >= 0)
            {
                int fontClose = trimmed.IndexOf("</font>", fontOpenEnd + 1, StringComparison.OrdinalIgnoreCase);
                if (fontClose >= 0)
                {
                    string inner = trimmed[(fontOpenEnd + 1)..fontClose];
                    string replacedInner = ReplaceStrongOrPlainSender(inner, realName, safeDisplayName);
                    if (!ReferenceEquals(replacedInner, inner) && !string.Equals(replacedInner, inner, StringComparison.Ordinal))
                        return prefixWhitespace + trimmed[..(fontOpenEnd + 1)] + replacedInner + trimmed[fontClose..];
                }
            }
        }

        string replaced = ReplaceStrongOrPlainSender(trimmed, realName, safeDisplayName);
        if (!string.Equals(replaced, trimmed, StringComparison.Ordinal))
            return prefixWhitespace + replaced;

        return message;
    }

    private static string ReplaceStrongOrPlainSender(string value, string realName, string safeDisplayName)
    {
        const string strongOpen = "<strong>";
        const string strongClose = "</strong>";
        if (value.StartsWith(strongOpen, StringComparison.OrdinalIgnoreCase))
        {
            int close = value.IndexOf(strongClose, StringComparison.OrdinalIgnoreCase);
            if (close >= 0)
            {
                string sender = WebUtility.HtmlDecode(value[strongOpen.Length..close]).Trim();
                if (SenderMatchesPlayer(sender, realName))
                {
                    bool hadColon = sender.EndsWith(':');
                    string suffix = hadColon ? ":" : string.Empty;
                    return strongOpen + safeDisplayName + suffix + strongClose + value[(close + strongClose.Length)..];
                }
            }
        }

        string plainPrefix = realName.Trim() + ":";
        if (value.StartsWith(plainPrefix, StringComparison.OrdinalIgnoreCase))
            return safeDisplayName + ":" + value[plainPrefix.Length..];

        return value;
    }

    private static bool TryExtractColoredSenderPrefix(string value, string? playerName, out string body, out string color)
    {
        body = string.Empty;
        color = string.Empty;
        if (!value.StartsWith("<font", StringComparison.OrdinalIgnoreCase)) return false;

        int fontOpenEnd = value.IndexOf('>');
        if (fontOpenEnd < 0) return false;
        string fontTag = value[..(fontOpenEnd + 1)];
        if (!TryReadSafeHexColor(fontTag, out color)) return false;

        int fontClose = value.IndexOf("</font>", fontOpenEnd + 1, StringComparison.OrdinalIgnoreCase);
        if (fontClose < 0) return false;

        string inner = value[(fontOpenEnd + 1)..fontClose].Trim();
        const string strongOpen = "<strong>";
        const string strongClose = "</strong>";
        if (inner.StartsWith(strongOpen, StringComparison.OrdinalIgnoreCase))
        {
            int strongEnd = inner.IndexOf(strongClose, StringComparison.OrdinalIgnoreCase);
            if (strongEnd < 0) return false;
            string sender = WebUtility.HtmlDecode(inner[strongOpen.Length..strongEnd]).Trim();
            if (!SenderMatchesPlayer(sender, playerName)) return false;
            if (!string.IsNullOrWhiteSpace(inner[(strongEnd + strongClose.Length)..])) return false;
        }
        else
        {
            string sender = WebUtility.HtmlDecode(inner).Trim();
            if (!SenderMatchesPlayer(sender, playerName)) return false;
        }

        int bodyStart = fontClose + "</font>".Length;
        body = bodyStart < value.Length ? value[bodyStart..].TrimStart() : string.Empty;
        return true;
    }

    private static bool SenderMatchesPlayer(string senderWithColon, string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return false;
        string sender = senderWithColon.Trim();
        if (sender.EndsWith(':')) sender = sender[..^1].TrimEnd();
        return sender.Equals(playerName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadSafeHexColor(string fontTag, out string color)
    {
        color = string.Empty;
        int colorIndex = fontTag.IndexOf("color", StringComparison.OrdinalIgnoreCase);
        if (colorIndex < 0) return false;
        int equalsIndex = fontTag.IndexOf('=', colorIndex + 5);
        if (equalsIndex < 0) return false;

        int i = equalsIndex + 1;
        while (i < fontTag.Length && char.IsWhiteSpace(fontTag[i])) i++;
        if (i >= fontTag.Length) return false;

        char quote = fontTag[i];
        if (quote != '\"' && quote != '\'') return false;
        int endQuote = fontTag.IndexOf(quote, i + 1);
        if (endQuote < 0) return false;

        string candidate = fontTag[(i + 1)..endQuote].Trim();
        if (candidate.Length != 7 || candidate[0] != '#') return false;
        for (int c = 1; c < candidate.Length; c++)
        {
            if (!Uri.IsHexDigit(candidate[c])) return false;
        }

        color = candidate.ToUpperInvariant();
        return true;
    }

    private static string EscapeVtmlText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Vintage Story VTML decodes &lt; and &gt; in text nodes, but it does not
        // decode normal HTML entities such as &amp;, &quot; or &#39;. Escaping those
        // therefore makes ordinary chat show the entity text literally. Only angle
        // brackets need escaping to prevent player text from becoming VTML tags.
        // Leaving '&', single quotes and double quotes untouched also avoids
        // double-escaping text that Vintage Story has already represented as &lt;/&gt;.
        return text.Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private string BuildPreview()
    {
        string role = config.ShowRoleBeforeName ? "[Player]" : "";
        string stamp = config.ShowTimestampBeforeMessage
            ? $"[{(config.UseUtcTimestamp ? DateTime.UtcNow : DateTime.Now).ToString(config.TimestampFormat)}]"
            : "";
        return ChatLayout.Render(config.ChatFormat, role, "ExamplePlayer", stamp, "Hello world", value => value);
    }

    public void Dispose()
    {
        disposed = true;
        if (!registered) return;
        registered = false;
        api.Event.PlayerChat -= OnPlayerChat;
        // Vintage Story 1.22.x does not expose an event-bus unregister API.
        // OnChatDisplayQuery is guarded by the disposed flag instead.
    }
}


/// <summary>Owner-defined plain-text layout; player data is never reparsed as a template.</summary>
public static class ChatLayout
{
    public const string DefaultFormat = "{role} {name}: {timestamp} {message}";
    private static readonly string[] Tokens = ["role", "name", "timestamp", "message"];

    public static string? Validate(string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > 512)
            return "Chat format must contain 1–512 characters.";
        if (format.Any(char.IsControl) || format.TrimStart().StartsWith('/'))
            return "Chat format must be a single line and cannot start with a slash.";
        var counts = new int[Tokens.Length];
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] == '}') return "Unmatched closing brace in chat format.";
            if (format[i] != '{') continue;
            int end = format.IndexOf('}', i + 1);
            if (end < 0) return "Unclosed placeholder in chat format.";
            int token = Array.IndexOf(Tokens, format[(i + 1)..end]);
            if (token < 0) return "Use only {role}, {name}, {timestamp} and {message} placeholders.";
            if (++counts[token] > 1) return "Each chat placeholder can appear only once.";
            i = end;
        }
        return counts[1] == 1 && counts[3] == 1 ? null : "Chat format requires exactly one {name} and one {message}.";
    }

    public static string Render(string format, string role, string name, string timestamp, string message, System.Func<string, string> escapeLiteral)
    {
        string[] values = [role, name, timestamp, message];
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < format.Length;)
        {
            int open = format.IndexOf('{', i);
            if (open < 0) { result.Append(escapeLiteral(format[i..])); break; }
            result.Append(escapeLiteral(format[i..open]));
            int close = format.IndexOf('}', open + 1);
            int token = close < 0 ? -1 : Array.IndexOf(Tokens, format[(open + 1)..close]);
            if (token < 0) { result.Append(escapeLiteral(format[open..])); break; }
            result.Append(values[token]);
            i = close + 1;
            // Hide one separator after an omitted role/timestamp, preserving message spacing.
            if (values[token].Length == 0 && token is 0 or 2 && i < format.Length && format[i] == ' ') i++;
        }
        return result.ToString();
    }
}
