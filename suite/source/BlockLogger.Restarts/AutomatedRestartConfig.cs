using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.Restarts;

public sealed class AutomatedRestartConfig
{
	public const string ConfigFile = "NewfiesAutomatedRestarts.json";

	public const int CurrentSchemaVersion = 1;

	public int SchemaVersion { get; set; } = 1;

	public bool Enabled { get; set; } = false;

	public string RestartTimeUtc { get; set; } = "00:00:00";

	public List<string> RestartTimesUtc { get; set; } = new List<string>();

	public List<int> WarningMinutes { get; set; } = new List<int> { 1, 2, 3, 4, 5, 10, 20, 30 };

	public bool AnnounceInGame { get; set; } = true;

	public bool AnnounceToDiscord { get; set; } = true;

	public string Reason { get; set; } = "Scheduled server restart";

	public void Clamp()
	{
		SchemaVersion = Math.Max(1, SchemaVersion);
		RestartTimeUtc = NormalizeTime(RestartTimeUtc, "00:00:00");
		if (RestartTimesUtc == null)
		{
			List<string> list = (RestartTimesUtc = new List<string>());
		}
		RestartTimesUtc = (from x in RestartTimesUtc
			select TryNormalizeTime(x, out string normalized) ? normalized : "" into x
			where !string.IsNullOrWhiteSpace(x)
			select x).Distinct(StringComparer.Ordinal).OrderBy((string x) => x, StringComparer.Ordinal).ToList();
		if (WarningMinutes == null)
		{
			List<int> list3 = (WarningMinutes = new List<int>());
		}
		WarningMinutes = (from x in WarningMinutes.Where((int x) => x > 0 && x <= 1440).Distinct()
			orderby x
			select x).ToList();
		Reason = (string.IsNullOrWhiteSpace(Reason) ? "Scheduled server restart" : Reason.Trim());
		if (Reason.Length > 240)
		{
			Reason = Reason.Substring(0, 240);
		}
	}

	public IReadOnlyList<string> EffectiveTimesUtc()
	{
		IReadOnlyList<string> result;
		if (RestartTimesUtc.Count <= 0)
		{
			IReadOnlyList<string> readOnlyList = new string[1] { RestartTimeUtc };
			result = readOnlyList;
		}
		else
		{
			IReadOnlyList<string> readOnlyList = RestartTimesUtc;
			result = readOnlyList;
		}
		return result;
	}

	public static AutomatedRestartConfig Load(ICoreServerAPI api)
	{
		AutomatedRestartConfig automatedRestartConfig = ModConfigRecovery.LoadOrNull<AutomatedRestartConfig>(api, "NewfiesAutomatedRestarts.json", "AutomatedRestarts") ?? new AutomatedRestartConfig();
		automatedRestartConfig.Clamp();
		SuiteConfigStorage.Store(api, automatedRestartConfig, "NewfiesAutomatedRestarts.json");
		return automatedRestartConfig;
	}

	public static bool TryNormalizeTime(string? raw, out string normalized)
	{
		normalized = "";
		string s = (raw ?? "").Trim();
		if (DateTime.TryParseExact(s, new string[4] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
		{
			normalized = result.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
			return true;
		}
		return false;
	}

	private static string NormalizeTime(string? raw, string fallback)
	{
		string normalized;
		return TryNormalizeTime(raw, out normalized) ? normalized : fallback;
	}
}
