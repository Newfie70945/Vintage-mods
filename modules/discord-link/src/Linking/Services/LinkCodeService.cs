using System.Security.Cryptography;
using System.Text;
using NewfiesServerEssentials.Discord.Linking;
using NewfiesServerEssentials.Discord.Linking.Models;

namespace NewfiesServerEssentials.Discord.Linking.Services;

public sealed class LinkCodeService
{
    // No 0/O/1/I/L to reduce copy/typing mistakes.
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    private readonly Func<DiscordLinkConfig> getConfig;
    private readonly Func<long> clock;
    internal const int MaximumTrackedIdentities = 4096;
    internal const int MaximumPendingCodes = 1024;
    private readonly Dictionary<string, PendingLinkCode> pendingByPlayerUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingLinkCode> pendingByCodeHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastIssuedAtByPlayerUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<long>> failedAttemptsByDiscordUser = new(StringComparer.Ordinal);

    public LinkCodeService(Func<DiscordLinkConfig> getConfig, Func<long>? clock = null)
    {
        this.getConfig = getConfig;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public bool TryCreate(string playerUid, string playerName, out string code, out string error)
    {
        CleanupExpired();
        code = "";
        error = "";
        var cfg = getConfig();
        var now = NowMs();
        var cooldownMs = Math.Max(0, cfg.CodeCooldownSeconds) * 1000L;

        if (lastIssuedAtByPlayerUid.TryGetValue(playerUid, out var last) && now - last < cooldownMs)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((cooldownMs - (now - last)) / 1000d));
            error = $"Please wait {seconds} second(s) before generating another Discord link code.";
            return false;
        }

        if ((!pendingByPlayerUid.ContainsKey(playerUid) && pendingByPlayerUid.Count >= MaximumPendingCodes) ||
            (!lastIssuedAtByPlayerUid.ContainsKey(playerUid) && lastIssuedAtByPlayerUid.Count >= MaximumTrackedIdentities))
        {
            error = "Discord linking is busy. Try again after existing codes expire.";
            return false;
        }

        if (pendingByPlayerUid.TryGetValue(playerUid, out var old))
        {
            pendingByPlayerUid.Remove(playerUid);
            pendingByCodeHash.Remove(old.CodeHash);
        }

        var length = Math.Clamp(cfg.CodeLength, 6, 16);
        for (var tries = 0; tries < 20; tries++)
        {
            var candidate = GenerateCode(length);
            var hash = Hash(candidate);
            if (pendingByCodeHash.ContainsKey(hash)) continue;

            var pending = new PendingLinkCode
            {
                PlayerUid = playerUid,
                PlayerName = playerName,
                CodeHash = hash,
                CreatedAtUnixMilliseconds = now,
                ExpiresAtUnixMilliseconds = now + Math.Clamp(cfg.CodeExpirySeconds, 60, 3600) * 1000L
            };

            pendingByPlayerUid[playerUid] = pending;
            pendingByCodeHash[hash] = pending;
            lastIssuedAtByPlayerUid[playerUid] = now;
            code = candidate;
            return true;
        }

        error = "Could not generate a unique link code. Try again.";
        return false;
    }

    public bool TryValidate(string code, string discordUserId, out PendingLinkCode? pending, out string error)
    {
        CleanupExpired();
        pending = null;
        error = "";

        if (IsRateLimited(discordUserId, out var waitSeconds))
        {
            error = $"Too many invalid link attempts. Try again in about {waitSeconds} second(s).";
            return false;
        }

        if (!failedAttemptsByDiscordUser.ContainsKey(discordUserId) && failedAttemptsByDiscordUser.Count >= MaximumTrackedIdentities)
        {
            // Preserve active rate limits rather than evicting them to admit a new caller.
            error = "Discord linking is busy. Try again shortly.";
            return false;
        }

        var normalized = Normalize(code);
        if (normalized.Length < 6 || !pendingByCodeHash.TryGetValue(Hash(normalized), out var found))
        {
            RecordFailure(discordUserId);
            error = "That link code is invalid or has expired. Generate a new code in-game with /discordlink.";
            return false;
        }

        if (found.ExpiresAtUnixMilliseconds <= NowMs())
        {
            pendingByCodeHash.Remove(found.CodeHash);
            pendingByPlayerUid.Remove(found.PlayerUid);
            RecordFailure(discordUserId);
            error = "That link code has expired. Generate a new code in-game with /discordlink.";
            return false;
        }

        failedAttemptsByDiscordUser.Remove(discordUserId);
        pending = found;
        return true;
    }

    public bool Consume(string playerUid)
    {
        CleanupExpired();
        if (!pendingByPlayerUid.Remove(playerUid, out var pending)) return false;
        pendingByCodeHash.Remove(pending.CodeHash);
        return true;
    }

    public bool Cancel(string playerUid)
    {
        CleanupExpired();
        if (!pendingByPlayerUid.Remove(playerUid, out var pending)) return false;
        pendingByCodeHash.Remove(pending.CodeHash);
        return true;
    }

    public int PendingCount
    {
        get { CleanupExpired(); return pendingByPlayerUid.Count; }
    }

    private bool IsRateLimited(string discordUserId, out int waitSeconds)
    {
        waitSeconds = 0;
        var cfg = getConfig();
        var max = Math.Clamp(cfg.MaxFailedAttemptsPerDiscordUser, 3, 100);
        var windowMs = Math.Clamp(cfg.FailedAttemptWindowSeconds, 60, 3600) * 1000L;
        var now = NowMs();

        if (!failedAttemptsByDiscordUser.TryGetValue(discordUserId, out var attempts)) return false;
        while (attempts.Count > 0 && now - attempts.Peek() >= windowMs) attempts.Dequeue();
        if (attempts.Count < max) return false;

        waitSeconds = Math.Max(1, (int)Math.Ceiling((windowMs - (now - attempts.Peek())) / 1000d));
        return true;
    }

    private void RecordFailure(string discordUserId)
    {
        if (!failedAttemptsByDiscordUser.TryGetValue(discordUserId, out var attempts))
        {
            attempts = new Queue<long>();
            failedAttemptsByDiscordUser[discordUserId] = attempts;
        }
        attempts.Enqueue(NowMs());
        IsRateLimited(discordUserId, out _); // also trims stale entries
    }

    private void CleanupExpired()
    {
        var now = NowMs();
        var expired = pendingByPlayerUid.Values.Where(x => x.ExpiresAtUnixMilliseconds <= now).ToArray();
        foreach (var entry in expired)
        {
            pendingByPlayerUid.Remove(entry.PlayerUid);
            pendingByCodeHash.Remove(entry.CodeHash);
        }
        var cfg = getConfig();
        long cooldownMs = Math.Max(0, cfg.CodeCooldownSeconds) * 1000L;
        foreach (var key in lastIssuedAtByPlayerUid.Where(x => now - x.Value >= cooldownMs).Select(x => x.Key).ToArray())
            lastIssuedAtByPlayerUid.Remove(key);
        long windowMs = Math.Clamp(cfg.FailedAttemptWindowSeconds, 60, 3600) * 1000L;
        foreach (var row in failedAttemptsByDiscordUser.ToArray())
        {
            while (row.Value.Count > 0 && now - row.Value.Peek() >= windowMs) row.Value.Dequeue();
            if (row.Value.Count == 0) failedAttemptsByDiscordUser.Remove(row.Key);
        }
    }

    public void PruneExpired() => CleanupExpired();
    public void Clear()
    {
        pendingByPlayerUid.Clear(); pendingByCodeHash.Clear();
        lastIssuedAtByPlayerUid.Clear(); failedAttemptsByDiscordUser.Clear();
    }

    private static string GenerateCode(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    private static string Normalize(string value) => (value ?? "").Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private long NowMs() => clock();
}
