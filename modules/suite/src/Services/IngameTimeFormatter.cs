using System;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class IngameTimeFormatter
{
    private readonly ICoreServerAPI api;
    private static readonly TimeZoneInfo NewfoundlandTimeZone = ResolveNewfoundlandTimeZone();
    private const string TranslationDomain = "newfiesserveressentialssuite";
    private static readonly Regex StoredStampPattern = new(
        @"^Year\s+(?<year>-?\d+)\s*,\s*Month\s+(?<month>\d+)\s*,\s*Day\s+(?<day>\d+)\s*,\s*(?<hour>-?\d+(?:\.\d+)?)h$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private long cachedAtMs;
    private string cachedStamp = "unknown";

    public IngameTimeFormatter(ICoreServerAPI api)
    {
        this.api = api;
    }

    public string GetCurrentStamp()
    {
        // Entity-heavy mods can generate thousands of events inside a tiny time window.
        // The displayed in-game timestamp does not need to be re-formatted for every event.
        var nowMs = Environment.TickCount64;
        if (nowMs - cachedAtMs < 100) return cachedStamp;

        var cal = api.World.Calendar;
        if (cal == null)
        {
            return "unknown";
        }

        try
        {
            var dayOfMonth = (cal.DayOfYear % Math.Max(1, cal.DaysPerMonth)) + 1;
            cachedStamp = $"Year {cal.Year}, Month {cal.Month}, Day {dayOfMonth}, {cal.HourOfDay:0.00}h";
            cachedAtMs = nowMs;
            return cachedStamp;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Localizes a canonical BlockLogger in-game date for the viewer without changing
    /// the language-neutral value stored in SQLite. Legacy records remain compatible.
    /// </summary>
    public static string LocalizeStoredStamp(string? stamp, string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(stamp)) return string.Empty;

        var language = string.IsNullOrWhiteSpace(languageCode) ? "en" : languageCode.Trim();
        if (string.Equals(stamp, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return Translate(language, "date.unknown", "Unknown date");
        }

        var match = StoredStampPattern.Match(stamp.Trim());
        if (!match.Success) return stamp;

        return Translate(
            language,
            "date.ingame-format",
            $"Year {match.Groups["year"].Value}, Month {match.Groups["month"].Value}, Day {match.Groups["day"].Value}, {match.Groups["hour"].Value}h",
            match.Groups["year"].Value,
            match.Groups["month"].Value,
            match.Groups["day"].Value,
            match.Groups["hour"].Value);
    }

    private static string Translate(string languageCode, string key, string fallback, params object[] args)
    {
        var fullKey = $"{TranslationDomain}:{key}";
        try
        {
            var value = Lang.GetL(languageCode, fullKey, args);
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, fullKey, StringComparison.Ordinal)) return value;
        }
        catch
        {
        }

        var builtInFallback = TryBuiltInDateTranslation(languageCode, key, args);
        if (builtInFallback != null) return builtInFallback;

        if (!string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var value = Lang.GetL("en", fullKey, args);
                if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, fullKey, StringComparison.Ordinal)) return value;
            }
            catch
            {
            }
        }

        return args.Length == 4
            ? $"Year {args[0]}, Month {args[1]}, Day {args[2]}, {args[3]}h"
            : fallback;
    }

    private static string? TryBuiltInDateTranslation(string languageCode, string key, object[] args)
    {
        var shortCode = languageCode.Split('-', '_')[0].ToLowerInvariant();
        if (key == "date.unknown")
        {
            return shortCode switch
            {
                "fr" => "Date inconnue",
                "de" => "Unbekanntes Datum",
                "ru" => "Неизвестная дата",
                "es" => "Fecha desconocida",
                "pt" => "Data desconhecida",
                "pl" => "Nieznana data",
                _ => null
            };
        }

        if (key != "date.ingame-format" || args.Length != 4) return null;
        return shortCode switch
        {
            "fr" => $"Année {args[0]}, Mois {args[1]}, Jour {args[2]}, {args[3]} h",
            "de" => $"Jahr {args[0]}, Monat {args[1]}, Tag {args[2]}, {args[3]}h",
            "ru" => $"Год {args[0]}, Месяц {args[1]}, День {args[2]}, {args[3]} ч",
            "es" => $"Año {args[0]}, Mes {args[1]}, Día {args[2]}, {args[3]} h",
            "pt" => $"Ano {args[0]}, Mês {args[1]}, Dia {args[2]}, {args[3]} h",
            "pl" => $"Rok {args[0]}, Miesiąc {args[1]}, Dzień {args[2]}, {args[3]} h",
            _ => null
        };
    }

    public static string FormatNewfoundlandTimestamp(DateTime timestampUtc)
    {
        try
        {
            var utc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, NewfoundlandTimeZone);
            return local.ToString("yyyy-MM-dd HH:mm:ss zzz");
        }
        catch
        {
            return timestampUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        }
    }

    private static TimeZoneInfo ResolveNewfoundlandTimeZone()
    {
        var ids = new[]
        {
            "America/St_Johns",
            "Canada/Newfoundland",
            "Newfoundland Standard Time"
        };

        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
