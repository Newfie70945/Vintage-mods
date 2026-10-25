using Discord;
using NewfiesServerEssentials.Discord.Config;
using NewfiesServerEssentials.Discord.Discord;

namespace NewfiesServerEssentials.Discord.Integration;

public sealed class MotdDiscordProvider : IDisposable
{
    private readonly Func<DiscordIntegrationConfig> getConfig;
    private readonly Func<bool> isConnected;
    private readonly Func<ulong, int, Task<IReadOnlyList<IMessage>>> readMessages;
    private readonly Func<long> elapsedMilliseconds;
    private readonly Action<Exception>? logError;
    private readonly object sync = new();
    private IReadOnlyList<DiscordMotdFeedItem> announcements = Array.Empty<DiscordMotdFeedItem>();
    private IReadOnlyList<DiscordMotdFeedItem> updates = Array.Empty<DiscordMotdFeedItem>();
    private IReadOnlyList<DiscordMotdFeedItem> eventsFeed = Array.Empty<DiscordMotdFeedItem>();
    private bool refreshing, disposed;
    private FeedKey? cachedKey;
    private long refreshAfter;
    private const int RefreshIntervalMs = 30_000;
    private const int FailureRetryMs = 10_000;

    public MotdDiscordProvider(DiscordBridgeService discord, Func<DiscordIntegrationConfig> getConfig, Action<Exception>? logError = null)
        : this(getConfig, () => discord.IsConnected, discord.GetRecentMessagesAsync, () => Environment.TickCount64, logError)
    {
    }

    internal MotdDiscordProvider(Func<DiscordIntegrationConfig> getConfig, Func<bool> isConnected,
        Func<ulong, int, Task<IReadOnlyList<IMessage>>> readMessages, Func<long> elapsedMilliseconds, Action<Exception>? logError)
    {
        this.getConfig = getConfig;
        this.isConnected = isConnected;
        this.readMessages = readMessages;
        this.elapsedMilliseconds = elapsedMilliseconds;
        this.logError = logError;
    }

    public bool IsAvailable => !Volatile.Read(ref disposed) && getConfig().Motd.Enabled && isConnected();
    public event Action? ContentChanged;

    public IReadOnlyList<DiscordMotdFeedItem> GetAnnouncements() { QueueRefresh(); lock (sync) return announcements; }
    public IReadOnlyList<DiscordMotdFeedItem> GetUpdates() { QueueRefresh(); lock (sync) return updates; }
    public IReadOnlyList<DiscordMotdFeedItem> GetEvents() { QueueRefresh(); lock (sync) return eventsFeed; }

    public void QueueRefresh(bool force = false)
    {
        if (!IsAvailable) return;
        var key = Key(getConfig());
        lock (sync)
        {
            if (disposed || refreshing) return;
            if (cachedKey != key)
            {
                // A channel/guild change must not expose content from the old feed.
                announcements = updates = eventsFeed = Array.Empty<DiscordMotdFeedItem>();
                cachedKey = key;
                refreshAfter = 0;
            }
            if (!force && elapsedMilliseconds() < refreshAfter) return;
            refreshing = true;
            refreshAfter = elapsedMilliseconds() + FailureRetryMs;
        }
        _ = RefreshAsync(key);
    }

    private async Task RefreshAsync(FeedKey key)
    {
        try
        {
            var a = await ReadFeed(key.Announcements, key.Limit).ConfigureAwait(false);
            var u = await ReadFeed(key.Updates, key.Limit).ConfigureAwait(false);
            var e = await ReadFeed(key.Events, key.Limit).ConfigureAwait(false);
            bool changed;
            lock (sync)
            {
                if (disposed || Key(getConfig()) != key) return;
                changed = !SameFeed(announcements, a) || !SameFeed(updates, u) || !SameFeed(eventsFeed, e);
                announcements = a; updates = u; eventsFeed = e;
                refreshAfter = elapsedMilliseconds() + RefreshIntervalMs;
            }
            if (changed) ContentChanged?.Invoke();
        }
        catch (Exception error)
        {
            // Failed REST calls leave the last good feed available and retry later.
            // Observe the fire-and-forget task rather than leaking an unobserved fault.
            try { logError?.Invoke(error); } catch { }
        }
        finally { lock (sync) refreshing = false; }
    }

    private async Task<IReadOnlyList<DiscordMotdFeedItem>> ReadFeed(ulong channelId, int limit)
    {
        if (channelId == 0) return Array.Empty<DiscordMotdFeedItem>();
        var messages = await readMessages(channelId, limit).ConfigureAwait(false);
        return messages.Where(HasFeedContent).Select(ToFeedItem).ToArray();
    }

    private static FeedKey Key(DiscordIntegrationConfig config) => new(config.GuildId,
        config.Motd.AnnouncementsChannelId, config.Motd.UpdatesChannelId, config.Motd.EventsChannelId,
        Math.Clamp(config.Motd.MaxItemsPerFeed, 1, 25), config.Motd.Enabled);

    private static bool SameFeed(IReadOnlyList<DiscordMotdFeedItem> left, IReadOnlyList<DiscordMotdFeedItem> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            var a = left[i]; var b = right[i];
            if (a.Id != b.Id || a.Title != b.Title || a.BodyVtml != b.BodyVtml || a.Author != b.Author ||
                a.UnixTime != b.UnixTime || a.Url != b.Url || a.Important != b.Important) return false;
        }
        return true;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            announcements = updates = eventsFeed = Array.Empty<DiscordMotdFeedItem>();
            ContentChanged = null;
        }
    }

    private readonly record struct FeedKey(ulong Guild, ulong Announcements, ulong Updates, ulong Events, int Limit, bool Enabled);

    private static bool HasFeedContent(IMessage message) =>
        !string.IsNullOrWhiteSpace(message.Content) || (message.Embeds?.Any(HasEmbedContent) ?? false);

    private static bool HasEmbedContent(IEmbed? embed) =>
        embed != null && (!string.IsNullOrWhiteSpace(embed.Title) || !string.IsNullOrWhiteSpace(embed.Description) || !embed.Fields.IsDefaultOrEmpty);

    private static DiscordMotdFeedItem ToFeedItem(IMessage message)
    {
        var resolve = MentionResolverFor(message);
        var embeds = message.Embeds?.Where(HasEmbedContent).ToArray() ?? Array.Empty<IEmbed>();
        var (title, body) = SplitTitle(message.Content ?? "", embeds.FirstOrDefault()?.Title, resolve);

        // Keep the complete Discord message in the MOTD feed. The client page uses a fixed
        // viewport with a growing scrollable document, so additional/longer posts extend the
        // scroll range instead of being truncated or squeezed into the visible area.
        var vtml = new List<string>();
        if (!string.IsNullOrWhiteSpace(body)) vtml.Add(DiscordMarkdown.ToVtml(body, resolve));
        foreach (var embed in embeds)
        {
            // The first embed title already became the card title when the message had no text.
            bool titleUsed = embed == embeds[0] && string.IsNullOrWhiteSpace(message.Content);
            vtml.Add(EmbedVtml(embed, titleUsed, resolve));
        }

        string url = "";
        if (message.Channel is IGuildChannel guildChannel)
        {
            url = $"https://discord.com/channels/{guildChannel.GuildId}/{message.Channel.Id}/{message.Id}";
        }

        return new DiscordMotdFeedItem
        {
            Id = message.Id.ToString(),
            // Title and Author are plain text: the MOTD formatter escapes them itself.
            Title = title,
            BodyVtml = string.Join("\n\n", vtml.Where(x => x.Length > 0)),
            Author = AuthorName(message.Author),
            UnixTime = message.Timestamp.ToUnixTimeSeconds(),
            Url = url,
            Important = message.IsPinned
        };
    }

    // The first line becomes the card title and is not repeated in the body. A first line too
    // long for a title is previewed (shortened) and the body keeps the whole message.
    internal static (string Title, string Body) SplitTitle(string content, string? embedTitle, DiscordMarkdown.MentionResolver? resolve)
    {
        content = content.Replace("\r\n", "\n").Trim();
        if (content.Length == 0)
            return (Shorten(DiscordMarkdown.ToPlainText(embedTitle, resolve), "Discord update"), "");

        int newline = content.IndexOf('\n');
        string first = (newline < 0 ? content : content[..newline]).Trim();
        string rest = newline < 0 ? "" : content[(newline + 1)..].Trim('\n');
        string plain = DiscordMarkdown.ToPlainText(first, resolve);
        if (plain.Length is > 0 and <= 96) return (plain, rest);
        return (Shorten(plain, "Discord update"), content);
    }

    private static string Shorten(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        return text.Length > 96 ? text[..93].TrimEnd() + "..." : text;
    }

    private static string EmbedVtml(IEmbed embed, bool titleUsed, DiscordMarkdown.MentionResolver? resolve)
    {
        var parts = new List<string>();
        if (!titleUsed && !string.IsNullOrWhiteSpace(embed.Title))
            parts.Add("<font size=\"17\"><strong>" + DiscordMarkdown.Escape(DiscordMarkdown.ToPlainText(embed.Title, resolve)) + "</strong></font>");
        if (!string.IsNullOrWhiteSpace(embed.Description)) parts.Add(DiscordMarkdown.ToVtml(embed.Description, resolve));
        foreach (var field in embed.Fields.IsDefault ? Array.Empty<EmbedField>() : (IEnumerable<EmbedField>)embed.Fields)
        {
            string name = DiscordMarkdown.Escape(DiscordMarkdown.ToPlainText(field.Name, resolve));
            parts.Add("<strong>" + name + "</strong>\n" + DiscordMarkdown.ToVtml(field.Value, resolve));
        }
        if (!string.IsNullOrWhiteSpace(embed.Footer?.Text))
            parts.Add("<font size=\"12\" color=\"#8A9AA5\">" + DiscordMarkdown.Escape(DiscordMarkdown.ToPlainText(embed.Footer.Value.Text, resolve)) + "</font>");
        return string.Join("\n\n", parts);
    }

    private static string AuthorName(IUser? author)
    {
        string name = DiscordMarkdown.StripEmoji((author as IGuildUser)?.DisplayName ?? author?.GlobalName ?? author?.Username).Trim();
        return name.Length > 0 ? name : "Discord";
    }

    // Gateway/REST messages carry resolved mention tags; unresolved IDs fall back to "@user".
    private static DiscordMarkdown.MentionResolver MentionResolverFor(IMessage message)
    {
        var names = new Dictionary<(char, ulong), string>();
        try
        {
            foreach (var tag in message.Tags ?? Array.Empty<ITag>())
            {
                switch (tag.Value)
                {
                    case IGuildUser member when tag.Type == TagType.UserMention: names[('@', tag.Key)] = DiscordMarkdown.StripEmoji(member.DisplayName); break;
                    case IUser user when tag.Type == TagType.UserMention: names[('@', tag.Key)] = user.GlobalName ?? user.Username; break;
                    case IRole role when tag.Type == TagType.RoleMention: names[('&', tag.Key)] = role.Name; break;
                    case IChannel channel when tag.Type == TagType.ChannelMention: names[('#', tag.Key)] = channel.Name; break;
                }
            }
        }
        catch { /* Mention names are cosmetic; a partial message still renders. */ }
        return (kind, id) => names.TryGetValue((kind, id), out var name) ? name : null;
    }
}

public sealed class DiscordMotdFeedItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string BodyVtml { get; set; } = "";
    public string Author { get; set; } = "";
    public long UnixTime { get; set; }
    public string Url { get; set; } = "";
    public bool Important { get; set; }
}
