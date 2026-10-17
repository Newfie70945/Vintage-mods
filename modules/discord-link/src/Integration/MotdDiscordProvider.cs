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
        return messages.Where(m => !string.IsNullOrWhiteSpace(m.Content)).Select(ToFeedItem).ToArray();
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

    private static DiscordMotdFeedItem ToFeedItem(IMessage message)
    {
        string content = message.Content.Trim();
        string title = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Discord update";
        if (title.Length > 96) title = title[..93] + "...";

        // Keep the complete Discord message in the MOTD feed. The client page uses a fixed
        // viewport with a growing scrollable document, so additional/longer posts extend the
        // scroll range instead of being truncated or squeezed into the visible area.
        string url = "";
        if (message.Channel is IGuildChannel guildChannel)
        {
            url = $"https://discord.com/channels/{guildChannel.GuildId}/{message.Channel.Id}/{message.Id}";
        }

        return new DiscordMotdFeedItem
        {
            Id = message.Id.ToString(),
            Title = EscapeVtml(title),
            BodyVtml = EscapeVtml(content).Replace("\r\n", "<br>").Replace("\n", "<br>"),
            Author = EscapeVtml(message.Author?.GlobalName ?? message.Author?.Username ?? "Discord"),
            UnixTime = message.Timestamp.ToUnixTimeSeconds(),
            Url = url,
            Important = message.IsPinned
        };
    }

    private static string EscapeVtml(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
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
