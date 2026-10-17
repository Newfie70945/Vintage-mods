using System.Collections.Concurrent;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using NewfiesServerEssentials.Discord.Config;
using NewfiesServerEssentials.Discord.Linking;
using Vintagestory.API.Server;
using VsDiscordConfig = NewfiesServerEssentials.Discord.Config.DiscordIntegrationConfig;

namespace NewfiesServerEssentials.Discord.Discord;

public sealed class DiscordBridgeService : IAsyncDisposable
{
    private readonly ICoreServerAPI sapi;
    private readonly Func<VsDiscordConfig> getConfig;
    private readonly DiscordLinkService linkService;
    private readonly DiscordRoleSyncService roleSyncService;
    private readonly ConcurrentDictionary<string, byte> roleSyncInFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<OutboundMessage> queue = new();
    private readonly SemaphoreSlim queueSignal = new(0);
    private CancellationTokenSource? cts;
    private Task? workerTask;
    private DiscordSocketClient? client;
    private DiscordCommandService? commands;
    private int queuedCount;
    private int stopping;
    private long lastRelayFailureLogMs;
    private int suppressedRelayFailures;
    private long lastQueueFullLogMs;
    private int suppressedQueueFullDrops;
    private readonly object channelCacheLock = new();
    private (ulong Id, string Name)[] cachedTextChannels = Array.Empty<(ulong, string)>();
    private long cachedTextChannelsAtMs;

    public bool IsConnected => client?.ConnectionState == ConnectionState.Connected;
    public string ConnectedBotName => client?.CurrentUser?.Username ?? "";
    public async Task<IReadOnlyList<IMessage>> GetRecentMessagesAsync(ulong channelId, int limit)
    {
        var socketClient = client;
        if (socketClient == null || socketClient.ConnectionState != ConnectionState.Connected || channelId == 0)
            return Array.Empty<IMessage>();

        IMessageChannel? channel = socketClient.GetChannel(channelId) as IMessageChannel;
        var cfg = getConfig();
        if (channel == null && cfg.GuildId != 0)
            channel = socketClient.GetGuild(cfg.GuildId)?.GetChannel(channelId) as IMessageChannel;
        if (channel == null) return Array.Empty<IMessage>();

        limit = Math.Clamp(limit, 1, 25);
        return (await channel.GetMessagesAsync(limit).FlattenAsync()).OrderByDescending(m => m.Timestamp).ToArray();
    }


    public DiscordBridgeService(
        ICoreServerAPI sapi,
        Func<VsDiscordConfig> getConfig,
        DiscordLinkService linkService,
        DiscordRoleSyncService roleSyncService)
    {
        this.sapi = sapi;
        this.getConfig = getConfig;
        this.linkService = linkService;
        this.roleSyncService = roleSyncService;
    }


    /// <summary>
    /// Fetches the linked Discord member through REST (no privileged Guild Members gateway
    /// intent required), then hands only the role IDs to the main-thread role authority.
    /// Duplicate requests for the same player are coalesced while one lookup is in flight.
    /// </summary>
    public async Task RequestRoleSyncAsync(string playerUid, string source)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return;
        var cfg = getConfig();
        if (!cfg.Enabled || !cfg.Linking.Enabled || cfg.Linking.RoleSync?.Enabled != true || cfg.GuildId == 0) return;

        var socketClient = client;
        var requestLifetime = cts;
        if (socketClient == null || socketClient.ConnectionState != ConnectionState.Connected || requestLifetime == null || requestLifetime.IsCancellationRequested) return;
        if (!roleSyncInFlight.TryAdd(playerUid, 0)) return;

        try
        {
            var link = linkService.GetByPlayerUid(playerUid);
            if (link == null || !ulong.TryParse(link.DiscordUserId, out ulong discordUserId) || discordUserId == 0) return;

            IReadOnlyCollection<ulong> roleIds;
            try
            {
                // Use the REST client deliberately. Socket guild-member caches can be incomplete
                // for offline users/large guilds, while GET Guild Member gives us authoritative
                // role IDs without enabling the privileged GuildMembers gateway intent.
                var guildUser = await socketClient.Rest.GetGuildUserAsync(cfg.GuildId, discordUserId).ConfigureAwait(false);
                roleIds = guildUser?.RoleIds?.ToArray() ?? Array.Empty<ulong>();
            }
            catch (global::Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
            {
                // Linked Discord account is no longer a member of the configured guild. Treat
                // that exactly like having no mapped Discord roles so our own managed role can
                // be safely restored. Other Discord/HTTP failures leave game state untouched.
                roleIds = Array.Empty<ulong>();
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[NSE Discord] Could not read Discord roles for linked player UID {0}: {1}", playerUid, e.Message);
                return;
            }

            // A config reload can replace the Discord gateway while this REST request is in
            // flight. Do not apply a response from an old/disposed session afterward.
            if (requestLifetime.IsCancellationRequested || !ReferenceEquals(cts, requestLifetime)) return;

            sapi.Event.EnqueueMainThreadTask(() =>
            {
                if (requestLifetime.IsCancellationRequested || !ReferenceEquals(cts, requestLifetime)) return;
                try { roleSyncService.ReconcileFromDiscord(playerUid, link.DiscordUserId, roleIds, source); }
                catch (Exception e) { sapi.Logger.Error("[NSE Discord] Role reconciliation failed for player UID {0}: {1}", playerUid, e); }
            }, "nse-discord-role-sync-apply");
        }
        finally
        {
            roleSyncInFlight.TryRemove(playerUid, out _);
        }
    }

    public async Task StartAsync()
    {
        var cfg = getConfig();
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.BotToken)) return;

        await StopAsync();

        cts = new CancellationTokenSource();
        Volatile.Write(ref stopping, 0);

        var gatewayIntents = GatewayIntents.Guilds;
        var needsMessageContent = cfg.Chat.Enabled && cfg.Chat.DiscordToGame;
        if (needsMessageContent)
        {
            gatewayIntents |= GatewayIntents.GuildMessages | GatewayIntents.MessageContent;
            sapi.Logger.Notification("[NSE Discord] Discord-to-game chat is enabled; requesting the privileged Message Content gateway intent.");
        }

        sapi.Logger.Notification("[NSE Discord] Starting Discord gateway with intents: {0}", gatewayIntents);

        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = gatewayIntents,
            LogGatewayIntentWarnings = true,
            AlwaysDownloadUsers = false
        });

        client.Log += OnDiscordLog;
        client.MessageReceived += OnDiscordMessageReceived;
        client.SlashCommandExecuted += OnSlashCommandExecuted;
        client.Ready += OnReady;
        client.Disconnected += OnDisconnected;

        await client.LoginAsync(TokenType.Bot, cfg.BotToken);
        await client.StartAsync();
        // Do not start the outbound worker until Discord fires Ready.
        // Before Ready, guild/channel caches are not guaranteed to be populated, which
        // can make valid configured channel IDs look missing during startup.
    }

    public async Task StopAsync(bool shutdown = false)
    {
        // Block new normal relay work before touching the gateway. During server shutdown
        // Vintage Story can still emit late events while mods are being disposed. Allowing
        // those events to enqueue after cancellation creates a canceled-send/error storm.
        Interlocked.Exchange(ref stopping, 1);

        var oldCts = cts;
        cts = null;
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch { }
            try { queueSignal.Release(); } catch { }
        }

        // Queued chat/audit/event messages are no longer useful once shutdown has begun.
        // The final server-stopping notice uses SendCriticalAsync before StopAsync is called.
        while (queue.TryDequeue(out _))
            Interlocked.Decrement(ref queuedCount);

        var socketClient = client;
        client = null;
        commands = null;

        if (socketClient != null)
        {
            socketClient.Log -= OnDiscordLog;
            socketClient.MessageReceived -= OnDiscordMessageReceived;
            socketClient.SlashCommandExecuted -= OnSlashCommandExecuted;
            socketClient.Ready -= OnReady;
            socketClient.Disconnected -= OnDisconnected;

            var teardownTask = TearDownClientAsync(socketClient);
            if (shutdown)
            {
                // A server restart must never hang indefinitely on Discord/network teardown.
                // Give Discord a short chance to close cleanly, then let process shutdown continue.
                var completed = await Task.WhenAny(teardownTask, Task.Delay(2000));
                if (completed == teardownTask)
                    await teardownTask;
                else
                    sapi.Logger.Warning("[NSE Discord] Discord shutdown exceeded 2000 ms; continuing server shutdown without waiting for the gateway.");
            }
            else
            {
                // Config-driven Discord restarts are not process shutdowns; fully tear down the
                // old client before starting a new one to avoid duplicate gateway sessions.
                await teardownTask;
            }
        }

        if (workerTask != null)
        {
            var oldWorker = workerTask;
            workerTask = null;
            if (shutdown)
            {
                var completed = await Task.WhenAny(oldWorker, Task.Delay(1000));
                if (completed == oldWorker)
                {
                    try { await oldWorker; } catch (OperationCanceledException) { }
                }
                else
                {
                    sapi.Logger.Warning("[NSE Discord] Outbound relay worker did not exit within 1000 ms; continuing server shutdown.");
                }
            }
            else
            {
                try { await oldWorker; } catch (OperationCanceledException) { }
            }
        }

        Interlocked.Exchange(ref queuedCount, 0);
        roleSyncInFlight.Clear();
        // Clearing the concurrent queue does not consume the semaphore permits that were
        // released for those messages. Drain stale permits so a later config-driven restart
        // cannot spin the new worker through a large sequence of empty wake-ups.
        try { while (queueSignal.Wait(0)) { } } catch { }
        lock (channelCacheLock)
        {
            cachedTextChannels = Array.Empty<(ulong, string)>();
            cachedTextChannelsAtMs = 0;
        }
        oldCts?.Dispose();
    }

    private static async Task TearDownClientAsync(DiscordSocketClient socketClient)
    {
        try { await socketClient.StopAsync(); } catch { }
        try { await socketClient.LogoutAsync(); } catch { }
        try { await socketClient.DisposeAsync(); } catch { }
    }

    /// <summary>
    /// Sends a high-priority Discord message directly instead of using the normal
    /// outbound queue. Intended for final shutdown/restart notices where the game
    /// process may exit before the queue worker gets another turn.
    /// </summary>
    public async Task<bool> SendCriticalAsync(ulong channelId, string message, int timeoutMilliseconds = 2500)
    {
        if (channelId == 0 || string.IsNullOrWhiteSpace(message)) return false;

        var socketClient = client;
        if (socketClient == null || socketClient.ConnectionState != ConnectionState.Connected)
        {
            sapi.Logger.Warning("[NSE Discord] Critical Discord message was not sent because the bot is not connected.");
            return false;
        }

        IMessageChannel? channel = socketClient.GetChannel(channelId) as IMessageChannel;
        if (channel == null)
        {
            var cfg = getConfig();
            channel = socketClient.GetGuild(cfg.GuildId)?.GetChannel(channelId) as IMessageChannel;
        }

        if (channel == null)
        {
            sapi.Logger.Warning("[NSE Discord] Critical Discord message was not sent because channel {0} is unavailable.", channelId);
            return false;
        }

        try
        {
            Task sendTask = channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
            Task finished = await Task.WhenAny(sendTask, Task.Delay(Math.Max(500, timeoutMilliseconds)));
            if (finished != sendTask)
            {
                sapi.Logger.Warning("[NSE Discord] Timed out after {0} ms while sending a critical Discord message; continuing shutdown.", timeoutMilliseconds);
                return false;
            }

            await sendTask;
            return true;
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[NSE Discord] Failed to send critical Discord message: {0}", e.Message);
            return false;
        }
    }

    public bool Enqueue(ulong channelId, string message)
        => EnqueueInternal(channelId, message, null);

    public bool EnqueueRoleColorEmbed(ulong channelId, string message, int red, int green, int blue)
    {
        if (channelId == 0 || string.IsNullOrWhiteSpace(message)) return false;

        var embed = new EmbedBuilder()
            .WithDescription(message)
            .WithColor(new Color(red, green, blue))
            .Build();
        return EnqueueInternal(channelId, string.Empty, embed);
    }

    private bool EnqueueInternal(ulong channelId, string message, Embed? embed)
    {
        if (channelId == 0 || (string.IsNullOrWhiteSpace(message) && embed == null)) return false;
        if (Volatile.Read(ref stopping) != 0 || cts == null) return false;

        var cfg = getConfig();
        if (Interlocked.Increment(ref queuedCount) > Math.Max(25, cfg.Safety.OutboundQueueCapacity))
        {
            Interlocked.Decrement(ref queuedCount);
            LogQueueFullDrop();
            return false;
        }

        queue.Enqueue(new OutboundMessage(channelId, message ?? string.Empty, embed));
        queueSignal.Release();
        return true;
    }

    public IReadOnlyList<(ulong Id, string Name)> GetTextChannels()
    {
        // The web panel can request Discord status frequently. Avoid sorting and allocating the
        // complete guild channel list on every status read; refresh at Ready and at most once
        // every 30 seconds thereafter.
        lock (channelCacheLock)
        {
            if (cachedTextChannels.Length > 0 && Environment.TickCount64 - cachedTextChannelsAtMs < 30_000)
                return cachedTextChannels;
        }

        RefreshTextChannelCache();
        lock (channelCacheLock) return cachedTextChannels;
    }

    private void RefreshTextChannelCache()
    {
        var cfg = getConfig();
        var guild = client?.GetGuild(cfg.GuildId);
        var channels = guild == null
            ? Array.Empty<(ulong Id, string Name)>()
            : guild.TextChannels
                .OrderBy(c => c.Position)
                .Select(c => (c.Id, c.Name))
                .ToArray();

        lock (channelCacheLock)
        {
            cachedTextChannels = channels;
            cachedTextChannelsAtMs = Environment.TickCount64;
        }
    }

    private async Task QueueWorkerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await queueSignal.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }

            while (!token.IsCancellationRequested && queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref queuedCount);
                try
                {
                    var socketClient = client;
                    while (socketClient == null || socketClient.ConnectionState != ConnectionState.Connected)
                    {
                        if (token.IsCancellationRequested || Volatile.Read(ref stopping) != 0)
                            return;

                        // Keep the dequeued item local while the gateway is unavailable. The old
                        // implementation re-enqueued and re-signaled once per second, creating
                        // avoidable queue churn throughout an outage.
                        await Task.Delay(500, token);
                        socketClient = client;
                    }

                    if (socketClient.GetChannel(item.ChannelId) is IMessageChannel channel)
                    {
                        await channel.SendMessageAsync(
                            text: string.IsNullOrWhiteSpace(item.Message) ? null : item.Message,
                            embed: item.Embed,
                            allowedMentions: AllowedMentions.None);
                    }
                    else
                    {
                        var cfg = getConfig();
                        var guild = socketClient.GetGuild(cfg.GuildId);
                        if (guild == null)
                        {
                            sapi.Logger.Warning("[NSE Discord] Discord guild {0} is not available after Ready. Check Guild ID and confirm the bot is a member of that server.", cfg.GuildId);
                        }
                        else if (guild.GetChannel(item.ChannelId) == null)
                        {
                            sapi.Logger.Warning("[NSE Discord] Discord channel {0} is not visible in guild '{1}' ({2}) after Ready. Check the channel ID and make sure the bot has View Channel permission.", item.ChannelId, guild.Name, guild.Id);
                        }
                        else
                        {
                            sapi.Logger.Warning("[NSE Discord] Discord channel {0} exists but is not a message-capable channel supported by NSE Discord. Use a normal text/announcement channel.", item.ChannelId);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested || Volatile.Read(ref stopping) != 0)
                {
                    return;
                }
                catch (ObjectDisposedException) when (token.IsCancellationRequested || Volatile.Read(ref stopping) != 0)
                {
                    return;
                }
                catch (Exception e)
                {
                    if (token.IsCancellationRequested || Volatile.Read(ref stopping) != 0)
                        return;

                    LogRelayFailure(e);
                }
            }
        }
    }


    private void LogQueueFullDrop()
    {
        long now = Environment.TickCount64;
        if (lastQueueFullLogMs == 0 || now - lastQueueFullLogMs >= 5000)
        {
            int suppressed = suppressedQueueFullDrops;
            suppressedQueueFullDrops = 0;
            lastQueueFullLogMs = now;
            if (suppressed > 0)
                sapi.Logger.Warning("[NSE Discord] Outbound queue is full; dropping a Discord message. (suppressed {0} additional queue-full drops)", suppressed);
            else
                sapi.Logger.Warning("[NSE Discord] Outbound queue is full; dropping a Discord message.");
        }
        else
        {
            suppressedQueueFullDrops++;
        }
    }

    private void LogRelayFailure(Exception e)
    {
        // A network outage or Discord-side failure should never flood the server console.
        // Keep the first/current error visible while summarizing repeats at most every 5s.
        long now = Environment.TickCount64;
        if (lastRelayFailureLogMs == 0 || now - lastRelayFailureLogMs >= 5000)
        {
            int suppressed = suppressedRelayFailures;
            suppressedRelayFailures = 0;
            lastRelayFailureLogMs = now;
            if (suppressed > 0)
                sapi.Logger.Error("[NSE Discord] Failed to relay message to Discord: {0} (suppressed {1} repeated relay failures)", e.Message, suppressed);
            else
                sapi.Logger.Error("[NSE Discord] Failed to relay message to Discord: {0}", e.Message);
        }
        else
        {
            suppressedRelayFailures++;
        }
    }

    private async Task OnReady()
    {
        sapi.Logger.Notification("[NSE Discord] Connected as {0}.", client?.CurrentUser?.Username ?? "unknown");
        if (client != null)
        {
            RefreshTextChannelCache();
            commands ??= new DiscordCommandService(sapi, client, getConfig, this, linkService);
            await commands.RegisterGuildCommandsAsync();
            sapi.Event.EnqueueMainThreadTask(() => roleSyncService.RequestSyncForOnline("discord-ready"), "nse-discord-role-sync-ready");

            // Start outbound delivery only after Ready so guild/channel caches are populated.
            var activeCts = cts;
            if (activeCts != null && !activeCts.IsCancellationRequested && workerTask == null)
            {
                // QueueWorkerAsync is I/O-bound and immediately awaits the semaphore; an extra
                // ThreadPool hop via Task.Run only adds scheduling overhead.
                workerTask = QueueWorkerAsync(activeCts.Token);
            }
        }
    }

    private Task OnSlashCommandExecuted(SocketSlashCommand command)
    {
        return commands?.HandleAsync(command) ?? Task.CompletedTask;
    }

    private Task OnDisconnected(Exception ex)
    {
        var closed = FindWebSocketClosedException(ex);
        if (closed != null)
        {
            sapi.Logger.Warning("[NSE Discord] Discord gateway closed. Code={0}, Reason={1}", closed.CloseCode, string.IsNullOrWhiteSpace(closed.Reason) ? "(none)" : closed.Reason);

            if (closed.CloseCode == 4014)
            {
                sapi.Logger.Warning("[NSE Discord] Discord rejected a privileged gateway intent. If Discord-to-game chat is enabled, enable Message Content Intent in Discord Developer Portal > Bot > Privileged Gateway Intents, or disable Discord-to-game chat.");
            }
            else if (closed.CloseCode == 4004)
            {
                sapi.Logger.Warning("[NSE Discord] Discord rejected the bot token. Replace/reset the Bot Token in the Discord Developer Portal and update NSE Discord configuration.");
            }
            else if (closed.CloseCode == 4013)
            {
                sapi.Logger.Warning("[NSE Discord] Discord rejected the requested gateway intents as invalid.");
            }
        }
        else
        {
            sapi.Logger.Warning("[NSE Discord] Discord disconnected: {0}", ex);
        }

        return Task.CompletedTask;
    }

    private static WebSocketClosedException? FindWebSocketClosedException(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is WebSocketClosedException closed) return closed;
            ex = ex.InnerException;
        }

        return null;
    }

    private Task OnDiscordLog(LogMessage msg)
    {
        var detail = msg.Exception == null
            ? (msg.Message ?? "")
            : string.IsNullOrWhiteSpace(msg.Message)
                ? msg.Exception.ToString()
                : $"{msg.Message} | {msg.Exception}";

        if (msg.Severity <= LogSeverity.Warning)
            sapi.Logger.Warning("[NSE Discord] {0}: {1}", msg.Source, string.IsNullOrWhiteSpace(detail) ? "Discord warning" : detail);
        else
            sapi.Logger.Debug("[NSE Discord] {0}: {1}", msg.Source, detail);
        return Task.CompletedTask;
    }

    private Task OnDiscordMessageReceived(SocketMessage raw)
    {
        if (raw is not SocketUserMessage message) return Task.CompletedTask;

        var cfg = getConfig();
        if (!cfg.Enabled || !cfg.Chat.Enabled || !cfg.Chat.DiscordToGame) return Task.CompletedTask;
        if (message.Channel.Id != cfg.Chat.ChannelId) return Task.CompletedTask;
        if (cfg.Safety.IgnoreBots && message.Author.IsBot) return Task.CompletedTask;
        if (message.Author.Id == client?.CurrentUser.Id) return Task.CompletedTask;

        var content = message.Content ?? "";
        if (string.IsNullOrWhiteSpace(content) && message.Attachments.Count > 0)
            content = string.Join(" ", message.Attachments.Select(a => a.Url));
        if (string.IsNullOrWhiteSpace(content)) return Task.CompletedTask;

        content = SanitizeDiscordToGame(content, cfg.Safety.MaxDiscordToGameLength);
        var displayName = message.Author is SocketGuildUser gu ? gu.DisplayName : message.Author.Username;

        // Discord -> game uses VTML because Vintage Story chat supports rich text. Keep the
        // sender label colored, but escape and leave the actual message body uncolored.
        // This prevents a Discord user from injecting VTML and guarantees the configured
        // name color cannot bleed into the message text.
        var safeName = EscapeVtml(displayName);
        var safeContent = EscapeVtml(content);
        var senderLabel = (cfg.Chat.ShowDiscordTag ? "[DC] " : "") + safeName;
        if (cfg.Chat.BoldDiscordSenderName) senderLabel = $"<strong>{senderLabel}</strong>";
        var nameColor = NormalizeDiscordNameColor(cfg.Chat.DiscordNameColor);
        var styledUser = $"<font color=\"{nameColor}\">{senderLabel}</font>";

        var formatted = (cfg.Chat.DiscordToGameFormat ?? "{user}: {message}")
            .Replace("{user}", styledUser)
            .Replace("{message}", safeContent);

        sapi.Event.EnqueueMainThreadTask(
            () => sapi.SendMessageToGroup(0, formatted, Vintagestory.API.Common.EnumChatType.Notification),
            "nse-discord-chat"
        );

        return Task.CompletedTask;
    }

    private static string SanitizeDiscordToGame(string value, int maxLen)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (value.Length > Math.Max(50, maxLen)) value = value[..Math.Max(50, maxLen)] + "…";
        return value;
    }

    private static string EscapeVtml(string value) => (value ?? "")
        // VTML text nodes only require angle brackets to be escaped. Vintage Story
        // decodes &lt;/&gt;, but not &amp;/&quot;/&#39;, so keep normal punctuation literal.
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static string NormalizeDiscordNameColor(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit))
            return value.ToUpperInvariant();
        return "#5865F2";
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed record OutboundMessage(ulong ChannelId, string Message, Embed? Embed);
}
