using System;
using System.Collections.Generic;
using System.Linq;
using NewfiesAntiCheat.Shared;
using NewfiesAntiCheat.Shared.Models;

namespace BlockLogger.AntiCheat;

internal sealed class AntiCheatIncidentCorrelator
{
	private sealed class Signal
	{
		public long Utc;

		public string Category = "Other";

		public string Domain = "Other";

		public string Code = "";

		public string Scanner = "";

		public int Severity;

		public string Subject = "";
	}

	private readonly Dictionary<string, List<Signal>> byPlayer = new Dictionary<string, List<Signal>>(StringComparer.OrdinalIgnoreCase);

	public void Clear(string? playerUid)
	{
		if (!string.IsNullOrWhiteSpace(playerUid))
		{
			byPlayer.Remove(playerUid);
		}
	}

	public void ClearAll()
	{
		byPlayer.Clear();
	}

	public void Observe(string playerUid, ScanFindingDto finding, long nowUtc)
	{
		if (string.IsNullOrWhiteSpace(playerUid) || finding == null || finding.Severity <= 0 || AntiCheatObservationPolicy.IsReviewOnly(finding.Code) || string.Equals(finding.Scanner, "CorrelationPolicy", StringComparison.OrdinalIgnoreCase) || string.Equals(finding.Code, "CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		string category = AntiCheatFindingIntelligence.GetCategory(finding.Code, finding.Scanner);
		bool flag;
		switch (category)
		{
		case "Other":
		case "Server Behavior":
		case "Correlated Incident":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return;
		}
		string evidenceDomain = AntiCheatFindingIntelligence.GetEvidenceDomain(finding.Code, finding.Scanner, category);
		if (!string.Equals(evidenceDomain, "Other", StringComparison.OrdinalIgnoreCase))
		{
			if (!byPlayer.TryGetValue(playerUid, out List<Signal> value))
			{
				value = new List<Signal>();
				byPlayer[playerUid] = value;
			}
			value.Add(new Signal
			{
				Utc = nowUtc,
				Category = category,
				Domain = evidenceDomain,
				Code = (finding.Code ?? ""),
				Scanner = (finding.Scanner ?? ""),
				Severity = Math.Clamp(finding.Severity, 0, 4),
				Subject = (finding.Subject ?? "")
			});
			if (value.Count > 192)
			{
				value.RemoveRange(0, value.Count - 192);
			}
		}
	}

	public CorrelationResult Evaluate(string playerUid, long nowUtc, AntiCheatConfig config)
	{
		if (!config.IncidentCorrelationEnabled || string.IsNullOrWhiteSpace(playerUid))
		{
			return CorrelationResult.None;
		}
		if (!byPlayer.TryGetValue(playerUid, out List<Signal> value))
		{
			return CorrelationResult.None;
		}
		long cutoff = nowUtc - (long)config.IncidentCorrelationWindowSeconds * 1000L;
		value.RemoveAll((Signal x) => x.Utc < cutoff);
		if (value.Count == 0)
		{
			byPlayer.Remove(playerUid);
			return CorrelationResult.None;
		}
		List<Signal> list = (from g in value.GroupBy((Signal x) => x.Category, StringComparer.OrdinalIgnoreCase)
			select (from x in g
				orderby x.Severity descending, x.Utc descending
				select x).First() into x
			orderby x.Severity descending
			select x).ThenBy((Signal x) => x.Category, StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count < config.IncidentCorrelationRequiredCategories)
		{
			return CorrelationResult.None;
		}
		List<Signal> list2 = (from g in list.GroupBy((Signal x) => IndependentUnitKey(x), StringComparer.OrdinalIgnoreCase)
			select (from x in g
				orderby x.Severity descending, x.Utc descending
				select x).First() into x
			orderby x.Severity descending
			select x).ToList();
		if (list2.Count < config.IncidentCorrelationRequiredCategories)
		{
			return CorrelationResult.None;
		}
		bool flag = list.Any((Signal x) =>
		{
			switch (x.Category)
			{
			case "Movement":
			case "Reach":
			case "Mining":
				return true;
			default:
				return false;
			}
		});
		bool flag2 = list.Any((Signal x) =>
		{
			bool flag3;
			switch (x.Domain)
			{
			case "StaticIntegrity":
			case "ClientIntegrity":
			case "RuntimeIntegrity":
			case "PatchOwnership":
				flag3 = true;
				break;
			default:
				flag3 = false;
				break;
			}
			return flag3 && x.Severity >= 3;
		});
		if (list.Count == 2 && !flag)
		{
			return CorrelationResult.None;
		}
		int count = list2.Count;
		if (1 == 0)
		{
		}
		int num = ((count >= 5) ? 20 : (count switch
		{
			4 => 16, 
			3 => 11, 
			_ => 6, 
		}));
		if (1 == 0)
		{
		}
		int num2 = num;
		if (flag & flag2)
		{
			num2 += 4;
		}
		num2 = Math.Clamp(num2, 0, config.IncidentCorrelationMaxRiskBonus);
		if (num2 <= 0)
		{
			return CorrelationResult.None;
		}
		int num3 = 45 + (count - 2) * 14 + list2.Sum((Signal x) => Math.Max(0, x.Severity - 1) * 4);
		if (flag & flag2)
		{
			num3 += 10;
		}
		num3 = Math.Clamp(num3, 0, 99);
		string[] array = list.Select((Signal x) => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		string[] array2 = list2.Select((Signal x) => x.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		string evidence = $"windowSeconds={config.IncidentCorrelationWindowSeconds};categories={string.Join(",", array)};categoryCount={list.Count};independentUnits={count};domains={string.Join(",", array2)};riskBonus={num2};confidence={num3};signals={string.Join("|", list.Select((Signal x) => $"{x.Category}/{x.Domain}:{x.Code}:{x.Severity}"))}";
		AntiCheatSeverity severity;
		if (num3 >= 90)
		{
			severity = AntiCheatSeverity.Critical;
		}
		else
		{
			severity = ((num3 >= 75) ? AntiCheatSeverity.High : ((num3 < 55) ? AntiCheatSeverity.Low : AntiCheatSeverity.Moderate));
		}
		return new CorrelationResult(Active: true, num2, num3, severity, array, array2, evidence);
	}

	private static string IndependentUnitKey(Signal signal)
	{
		return string.Equals(signal.Domain, "ServerBehavior", StringComparison.OrdinalIgnoreCase) ? ("ServerBehavior:" + signal.Category) : signal.Domain;
	}
}
