using System;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class IngameTimeFormatter
{
	private readonly ICoreServerAPI api;

	private static readonly TimeZoneInfo NewfoundlandTimeZone = ResolveNewfoundlandTimeZone();

	private const string TranslationDomain = "newfiesserveressentialssuite";

	private static readonly Regex StoredStampPattern = new Regex("^Year\\s+(?<year>-?\\d+)\\s*,\\s*Month\\s+(?<month>\\d+)\\s*,\\s*Day\\s+(?<day>\\d+)\\s*,\\s*(?<hour>-?\\d+(?:\\.\\d+)?)h$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private long cachedAtMs;

	private string cachedStamp = "unknown";

	public IngameTimeFormatter(ICoreServerAPI api)
	{
		this.api = api;
	}

	public string GetCurrentStamp()
	{
		long tickCount = Environment.TickCount64;
		if (tickCount - cachedAtMs < 100)
		{
			return cachedStamp;
		}
		IGameCalendar calendar = api.World.Calendar;
		if (calendar == null)
		{
			return "unknown";
		}
		try
		{
			int value = calendar.DayOfYear % Math.Max(1, calendar.DaysPerMonth) + 1;
			cachedStamp = $"Year {calendar.Year}, Month {calendar.Month}, Day {value}, {calendar.HourOfDay:0.00}h";
			cachedAtMs = tickCount;
			return cachedStamp;
		}
		catch
		{
			return "unknown";
		}
	}

	public static string LocalizeStoredStamp(string? stamp, string? languageCode)
	{
		if (string.IsNullOrWhiteSpace(stamp))
		{
			return string.Empty;
		}
		string languageCode2 = (string.IsNullOrWhiteSpace(languageCode) ? "en" : languageCode.Trim());
		if (string.Equals(stamp, "unknown", StringComparison.OrdinalIgnoreCase))
		{
			return Translate(languageCode2, "date.unknown", "Unknown date");
		}
		Match match = StoredStampPattern.Match(stamp.Trim());
		if (!match.Success)
		{
			return stamp;
		}
		return Translate(languageCode2, "date.ingame-format", $"Year {match.Groups["year"].Value}, Month {match.Groups["month"].Value}, Day {match.Groups["day"].Value}, {match.Groups["hour"].Value}h", match.Groups["year"].Value, match.Groups["month"].Value, match.Groups["day"].Value, match.Groups["hour"].Value);
	}

	private static string Translate(string languageCode, string key, string fallback, params object[] args)
	{
		string text = "newfiesserveressentialssuite:" + key;
		try
		{
			string l = Lang.GetL(languageCode, text, args);
			if (!string.IsNullOrWhiteSpace(l) && !string.Equals(l, text, StringComparison.Ordinal))
			{
				return l;
			}
		}
		catch
		{
		}
		string text2 = TryBuiltInDateTranslation(languageCode, key, args);
		if (text2 != null)
		{
			return text2;
		}
		if (!string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				string l2 = Lang.GetL("en", text, args);
				if (!string.IsNullOrWhiteSpace(l2) && !string.Equals(l2, text, StringComparison.Ordinal))
				{
					return l2;
				}
			}
			catch
			{
			}
		}
		return (args.Length == 4) ? $"Year {args[0]}, Month {args[1]}, Day {args[2]}, {args[3]}h" : fallback;
	}

	private static string? TryBuiltInDateTranslation(string languageCode, string key, object[] args)
	{
		string text = languageCode.Split('-', '_')[0].ToLowerInvariant();
		string result;
		if (key == "date.unknown")
		{
			if (1 == 0)
			{
			}
			result = text switch
			{
				"fr" => "Date inconnue", 
				"de" => "Unbekanntes Datum", 
				"ru" => "Неизвестная дата", 
				"es" => "Fecha desconocida", 
				"pt" => "Data desconhecida", 
				"pl" => "Nieznana data", 
				_ => null, 
			};
			if (1 == 0)
			{
			}
			return result;
		}
		if (key != "date.ingame-format" || args.Length != 4)
		{
			return null;
		}
		if (1 == 0)
		{
		}
		result = text switch
		{
			"fr" => $"Année {args[0]}, Mois {args[1]}, Jour {args[2]}, {args[3]} h", 
			"de" => $"Jahr {args[0]}, Monat {args[1]}, Tag {args[2]}, {args[3]}h", 
			"ru" => $"Год {args[0]}, Месяц {args[1]}, День {args[2]}, {args[3]} ч", 
			"es" => $"Año {args[0]}, Mes {args[1]}, Día {args[2]}, {args[3]} h", 
			"pt" => $"Ano {args[0]}, Mês {args[1]}, Dia {args[2]}, {args[3]} h", 
			"pl" => $"Rok {args[0]}, Miesiąc {args[1]}, Dzień {args[2]}, {args[3]} h", 
			_ => null, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	public static string FormatNewfoundlandTimestamp(DateTime timestampUtc)
	{
		try
		{
			DateTime dateTime = ((timestampUtc.Kind == DateTimeKind.Utc) ? timestampUtc : DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc));
			return TimeZoneInfo.ConvertTimeFromUtc(dateTime, NewfoundlandTimeZone).ToString("yyyy-MM-dd HH:mm:ss zzz");
		}
		catch
		{
			return timestampUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
		}
	}

	private static TimeZoneInfo ResolveNewfoundlandTimeZone()
	{
		string[] array = new string[3] { "America/St_Johns", "Canada/Newfoundland", "Newfoundland Standard Time" };
		string[] array2 = array;
		foreach (string id in array2)
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
