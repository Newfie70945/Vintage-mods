using System;
using System.Collections.Generic;
using System.Linq;

namespace NewfiesChatGuard;

public sealed class ChatGuardConfig
{
    public const string ConfigFile = "NewfiesChatGuard.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = "Enforce";
    public bool NotifyPlayers { get; set; } = true;
    public string NotificationPrefix { get; set; } = "[ChatGuard]";
    public List<string> ExemptRoleCodes { get; set; } = new() { "admin", "moderator" };
    public List<string> ExemptPlayerUids { get; set; } = new();
    public SpamRuleConfig Spam { get; set; } = new();
    public RepeatRuleConfig Repeat { get; set; } = new();
    public CapsRuleConfig Caps { get; set; } = new();
    public LinkRuleConfig Links { get; set; } = new();
    public MentionRuleConfig Mentions { get; set; } = new();
    public LengthRuleConfig Length { get; set; } = new();
    public BlockedTermsRuleConfig BlockedTerms { get; set; } = new();
    public EscalationConfig Escalation { get; set; } = new();
    public int AuditMessagePreviewCharacters { get; set; } = 160;

    public void Clamp()
    {
        SchemaVersion = CurrentSchemaVersion;
        Mode = string.Equals(Mode, "LogOnly", StringComparison.OrdinalIgnoreCase) ? "LogOnly" : "Enforce";
        NotificationPrefix = string.IsNullOrWhiteSpace(NotificationPrefix) ? "[ChatGuard]" : NotificationPrefix.Trim();
        ExemptRoleCodes = CleanList(ExemptRoleCodes, 40, 64);
        ExemptPlayerUids = CleanList(ExemptPlayerUids, 200, 128);
        Spam ??= new(); Spam.Clamp();
        Repeat ??= new(); Repeat.Clamp();
        Caps ??= new(); Caps.Clamp();
        Links ??= new(); Links.Clamp();
        Mentions ??= new(); Mentions.Clamp();
        Length ??= new(); Length.Clamp();
        BlockedTerms ??= new(); BlockedTerms.Clamp();
        Escalation ??= new(); Escalation.Clamp();
        AuditMessagePreviewCharacters = Math.Clamp(AuditMessagePreviewCharacters, 0, 500);
    }

    private static List<string> CleanList(IEnumerable<string>? source, int maxItems, int maxLength) =>
        (source ?? Array.Empty<string>())
            .Select(v => (v ?? string.Empty).Trim())
            .Where(v => v.Length > 0)
            .Select(v => v.Length > maxLength ? v[..maxLength] : v)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToList();
}

public abstract class ChatRuleConfig
{
    public bool Enabled { get; set; } = true;
    public string Action { get; set; } = "Warn";
    public int MuteMinutes { get; set; } = 5;

    public virtual void Clamp()
    {
        Action = NormalizeAction(Action);
        MuteMinutes = Math.Clamp(MuteMinutes, 1, 10080);
    }

    protected static string NormalizeAction(string? value)
    {
        string v = (value ?? string.Empty).Trim();
        if (v.Equals("Block", StringComparison.OrdinalIgnoreCase)) return "Block";
        if (v.Equals("Mute", StringComparison.OrdinalIgnoreCase)) return "Mute";
        if (v.Equals("Kick", StringComparison.OrdinalIgnoreCase)) return "Kick";
        return "Warn";
    }
}

public sealed class SpamRuleConfig : ChatRuleConfig
{
    public int MaxMessages { get; set; } = 5;
    public int WindowSeconds { get; set; } = 4;
    public SpamRuleConfig() { Action = "Mute"; MuteMinutes = 2; }
    public override void Clamp() { base.Clamp(); MaxMessages = Math.Clamp(MaxMessages, 2, 30); WindowSeconds = Math.Clamp(WindowSeconds, 1, 60); }
}

public sealed class RepeatRuleConfig : ChatRuleConfig
{
    public int MaxRepeats { get; set; } = 3;
    public int WindowSeconds { get; set; } = 20;
    public override void Clamp() { base.Clamp(); MaxRepeats = Math.Clamp(MaxRepeats, 2, 12); WindowSeconds = Math.Clamp(WindowSeconds, 2, 120); }
}

public sealed class CapsRuleConfig : ChatRuleConfig
{
    public int MinLetters { get; set; } = 12;
    public int MaxUppercasePercent { get; set; } = 75;
    public override void Clamp() { base.Clamp(); MinLetters = Math.Clamp(MinLetters, 4, 200); MaxUppercasePercent = Math.Clamp(MaxUppercasePercent, 50, 100); }
}

public sealed class LinkRuleConfig : ChatRuleConfig
{
    public LinkRuleConfig() { Enabled = false; }
    public List<string> AllowedDomains { get; set; } = new();
    public override void Clamp()
    {
        base.Clamp();
        AllowedDomains = (AllowedDomains ?? new List<string>())
            .Select(v => (v ?? string.Empty).Trim().Trim('.').ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
    }
}

public sealed class MentionRuleConfig : ChatRuleConfig
{
    public int MaxMentions { get; set; } = 5;
    public override void Clamp() { base.Clamp(); MaxMentions = Math.Clamp(MaxMentions, 1, 30); }
}

public sealed class LengthRuleConfig : ChatRuleConfig
{
    public LengthRuleConfig() { Action = "Block"; }
    public int MaxCharacters { get; set; } = 500;
    public override void Clamp() { base.Clamp(); MaxCharacters = Math.Clamp(MaxCharacters, 32, 4000); }
}

public sealed class BlockedTermsRuleConfig : ChatRuleConfig
{
    public BlockedTermsRuleConfig() { Enabled = false; MuteMinutes = 10; }
    public List<string> Terms { get; set; } = new();
    public override void Clamp()
    {
        base.Clamp();
        Terms = (Terms ?? new List<string>())
            .Select(v => (v ?? string.Empty).Trim())
            .Where(v => v.Length > 0)
            .Select(v => v.Length > 120 ? v[..120] : v)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
    }
}

public sealed class EscalationConfig
{
    public bool Enabled { get; set; } = true;
    public int WarningWindowMinutes { get; set; } = 60;
    public int WarningsBeforeMute { get; set; } = 3;
    public int MuteMinutes { get; set; } = 10;
    public void Clamp()
    {
        WarningWindowMinutes = Math.Clamp(WarningWindowMinutes, 1, 10080);
        WarningsBeforeMute = Math.Clamp(WarningsBeforeMute, 2, 20);
        MuteMinutes = Math.Clamp(MuteMinutes, 1, 10080);
    }
}
