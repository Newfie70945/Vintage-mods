using System.Security.Cryptography;

namespace Newfies.WebsiteConnector;

/// <summary>Bounded, temporary challenges. Only the authenticated in-game caller can confirm one.</summary>
public sealed class AccountLinking
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly object gate = new();
    private readonly Dictionary<string, Challenge> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long At, int Count)> attempts = new(StringComparer.Ordinal);
    private readonly Queue<long> created = new();
    private readonly Func<long> clock;
    public bool Enabled { get; }
    private sealed class Challenge(string secret, string code, long expires)
    {
        public string Secret = secret, Code = code;
        public long Expires = expires;
        public string? Uid, Name;
    }
    public AccountLinking(bool enabled, Func<long>? clock = null) { Enabled = enabled; this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
    public object Create(string secret)
    {
        if (!Enabled) throw new InvalidOperationException("Account linking is disabled.");
        ValidateSecret(secret);
        lock (gate)
        {
            long now = clock(); Cleanup(now);
            if (pending.TryGetValue(secret, out var existing)) return new { code = existing.Code, expiresAt = existing.Expires };
            if (pending.Count >= 200 || created.Count >= 60) throw new InvalidOperationException("Linking is busy. Try again shortly.");
            string code;
            do { code = string.Concat(RandomNumberGenerator.GetBytes(10).Select(b => Alphabet[b & 31])); } while (pending.Values.Any(p => p.Code == code));
            var entry = new Challenge(secret, code, now + 600000); pending.Add(secret, entry); created.Enqueue(now);
            return new { code, expiresAt = entry.Expires };
        }
    }
    public object Status(string secret)
    {
        if (!Enabled) throw new InvalidOperationException("Account linking is disabled.");
        ValidateSecret(secret);
        lock (gate)
        {
            Cleanup(clock());
            if (!pending.TryGetValue(secret, out var entry)) return new { confirmed = false };
            return entry.Uid == null ? new { confirmed = false } : (object)new { confirmed = true, playerUid = entry.Uid, playerName = entry.Name };
        }
    }
    public string Confirm(string code, string uid, string name)
    {
        if (!Enabled) return "Website account linking is disabled by the server owner.";
        if (string.IsNullOrEmpty(uid) || uid.Length > 128 || string.IsNullOrWhiteSpace(name) || name.Length > 60) return "Your game identity could not be verified.";
        lock (gate)
        {
            long now = clock(); Cleanup(now);
            var attempt = attempts.GetValueOrDefault(uid);
            if (attempt.At + 60000 <= now) attempt = (now, 0);
            if (!attempts.ContainsKey(uid) && attempts.Count >= 4096) return "Account linking is busy. Try again shortly.";
            attempts[uid] = (attempt.At, attempt.Count + 1);
            if (attempt.Count >= 5) return "Too many linking attempts. Wait a minute before trying again.";
            if (code.Length != 10 || code.Any(c => !Alphabet.Contains(c))) return "Enter the ten-character code shown on your website account page.";
            var entry = pending.Values.FirstOrDefault(p => p.Code == code);
            if (entry == null) return "This code expired or no longer exists. Create a new code on the website.";
            if (entry.Uid != null) return entry.Uid == uid ? "This code is confirmed. Return to the website and check confirmation." : "This code has already been confirmed.";
            entry.Uid = uid; entry.Name = name;
            return "Game identity confirmed. Return to your website account page and click Check confirmation.";
        }
    }
    private void Cleanup(long now)
    {
        foreach (var key in pending.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) pending.Remove(key);
        foreach (var key in attempts.Where(p => p.Value.At + 60000 <= now).Select(p => p.Key).ToArray()) attempts.Remove(key);
        while (created.TryPeek(out var at) && at + 60000 <= now) created.Dequeue();
    }
    private static void ValidateSecret(string secret)
    {
        if (secret.Length != 64 || secret.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("Invalid private challenge.");
    }
}
