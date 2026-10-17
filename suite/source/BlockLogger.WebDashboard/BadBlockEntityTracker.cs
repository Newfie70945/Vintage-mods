using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.WebDashboard;

public sealed class BadBlockEntityTracker
{
	private const int MaxEntries = 500;

	private const int MaxText = 600;

	private static readonly Regex TickErrorRegex = new Regex("At position\\s+(-?\\d+)\\s*,\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s+for block\\s+(\\S+)\\s+a\\s+(\\S+)\\s+threw an error when ticked", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ExceptionRegex = new Regex("Exception:\\s*([^\\r\\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex StackRegex = new Regex("(?:^|\\n)\\s*at\\s+([^\\r\\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	private readonly object sync = new object();

	private readonly Dictionary<string, BadBlockEntityRecord> entries = new Dictionary<string, BadBlockEntityRecord>(StringComparer.Ordinal);

	private readonly string statePath;

	public BadBlockEntityTracker(ICoreServerAPI api)
	{
		string text = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "diagnostics");
		Directory.CreateDirectory(text);
		statePath = Path.Combine(text, "bad-block-entities.json");
		LoadState();
	}

	public void Observe(string line, string context, bool archived, long unixMilliseconds)
	{
		if (archived || string.IsNullOrWhiteSpace(line))
		{
			return;
		}
		Match match = TickErrorRegex.Match(line);
		if (!match.Success || !int.TryParse(match.Groups[1].Value, out var result) || !int.TryParse(match.Groups[2].Value, out var result2) || !int.TryParse(match.Groups[3].Value, out var result3))
		{
			return;
		}
		string blockCode = Trim(match.Groups[4].Value, 240);
		string blockEntityType = Trim(match.Groups[5].Value, 240).TrimEnd(':');
		string key = BuildKey(result, result2, result3, 0, blockCode, blockEntityType);
		string value = Extract(ExceptionRegex, context, "Unknown exception");
		string value2 = Extract(StackRegex, context, "");
		lock (sync)
		{
			if (!entries.TryGetValue(key, out BadBlockEntityRecord value3))
			{
				EnsureCapacityUnsafe();
				value3 = new BadBlockEntityRecord
				{
					Key = key,
					X = result,
					Y = result2,
					Z = result3,
					Dimension = 0,
					BlockCode = blockCode,
					BlockEntityType = blockEntityType,
					FirstSeenUnixMilliseconds = unixMilliseconds,
					LastSeenUnixMilliseconds = unixMilliseconds,
					Tracking = true
				};
				entries[key] = value3;
			}
			if (value3.Tracking)
			{
				value3.Occurrences++;
				value3.FirstSeenUnixMilliseconds = ((value3.FirstSeenUnixMilliseconds <= 0) ? unixMilliseconds : Math.Min(value3.FirstSeenUnixMilliseconds, unixMilliseconds));
				value3.LastSeenUnixMilliseconds = Math.Max(value3.LastSeenUnixMilliseconds, unixMilliseconds);
				value3.LastException = Trim(value, 600);
				value3.LastTopFrame = Trim(value2, 600);
			}
		}
	}

	public BadBlockEntitySnapshot GetSnapshot()
	{
		lock (sync)
		{
			List<BadBlockEntityRecord> list = new List<BadBlockEntityRecord>(entries.Count);
			int num = 0;
			long num2 = 0L;
			foreach (BadBlockEntityRecord value in entries.Values)
			{
				list.Add(Clone(value));
				if (value.Tracking)
				{
					num++;
				}
				num2 += value.Occurrences;
			}
			list.Sort((BadBlockEntityRecord a, BadBlockEntityRecord b) =>
			{
				int num3 = b.Tracking.CompareTo(a.Tracking);
				if (num3 != 0)
				{
					return num3;
				}
				num3 = b.LastSeenUnixMilliseconds.CompareTo(a.LastSeenUnixMilliseconds);
				return (num3 != 0) ? num3 : b.Occurrences.CompareTo(a.Occurrences);
			});
			return new BadBlockEntitySnapshot
			{
				GeneratedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Total = list.Count,
				Tracking = num,
				Stopped = list.Count - num,
				TotalOccurrences = num2,
				Entries = list
			};
		}
	}

	public BadBlockEntitySnapshot GetConsoleAlertSnapshot(int minimumOccurrences = 3, long recentWindowMilliseconds = 300000L)
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long num2 = num - Math.Max(15000L, recentWindowMilliseconds);
		minimumOccurrences = Math.Max(2, minimumOccurrences);
		lock (sync)
		{
			List<BadBlockEntityRecord> list = new List<BadBlockEntityRecord>();
			long num3 = 0L;
			foreach (BadBlockEntityRecord value in entries.Values)
			{
				if (value.Tracking && value.Occurrences >= minimumOccurrences && value.LastSeenUnixMilliseconds >= num2)
				{
					list.Add(Clone(value));
					num3 += value.Occurrences;
				}
			}
			list.Sort((BadBlockEntityRecord a, BadBlockEntityRecord b) =>
			{
				int num4 = b.LastSeenUnixMilliseconds.CompareTo(a.LastSeenUnixMilliseconds);
				return (num4 != 0) ? num4 : b.Occurrences.CompareTo(a.Occurrences);
			});
			return new BadBlockEntitySnapshot
			{
				GeneratedUnixMilliseconds = num,
				Total = list.Count,
				Tracking = list.Count,
				Stopped = 0,
				TotalOccurrences = num3,
				Entries = list
			};
		}
	}

	public bool TryGet(string key, out BadBlockEntityRecord record)
	{
		lock (sync)
		{
			if (entries.TryGetValue(key ?? "", out BadBlockEntityRecord value))
			{
				record = Clone(value);
				return true;
			}
		}
		record = new BadBlockEntityRecord();
		return false;
	}

	public bool SetTracking(string key, bool tracking, string actor, out string message)
	{
		lock (sync)
		{
			if (!entries.TryGetValue(key ?? "", out BadBlockEntityRecord value))
			{
				message = "Tracked block entity was not found.";
				return false;
			}
			value.Tracking = tracking;
			value.LastAction = (tracking ? "Tracking resumed" : "Tracking stopped");
			value.LastActionBy = Trim(actor, 120);
			value.LastActionUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			message = (tracking ? "Block entity tracking resumed." : "Block entity tracking stopped.");
		}
		SaveState();
		return true;
	}

	public void RecordAction(string key, string actor, string action, string detail)
	{
		lock (sync)
		{
			if (!entries.TryGetValue(key ?? "", out BadBlockEntityRecord value))
			{
				return;
			}
			value.LastAction = Trim(string.IsNullOrWhiteSpace(detail) ? action : (action + ": " + detail), 600);
			value.LastActionBy = Trim(actor, 120);
			value.LastActionUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
		SaveState();
	}

	public int ClearAll(string actor, out string message)
	{
		int count;
		lock (sync)
		{
			count = entries.Count;
			entries.Clear();
		}
		SaveState();
		message = ((count == 0) ? "Bad Block Entity Tracker is already empty." : $"Removed {count} tracked block-entity record(s). World blocks and block entities were not modified.");
		return count;
	}

	public void SaveState()
	{
		try
		{
			BadBlockEntityPersistedState value;
			lock (sync)
			{
				BadBlockEntityPersistedState badBlockEntityPersistedState = new BadBlockEntityPersistedState();
				badBlockEntityPersistedState.Entries = entries.Values.OrderByDescending((BadBlockEntityRecord x) => x.LastSeenUnixMilliseconds).Take(500).Select(Clone)
					.ToList();
				value = badBlockEntityPersistedState;
			}
			string text = statePath + ".tmp";
			File.WriteAllText(text, JsonSerializer.Serialize(value, JsonOptions));
			File.Move(text, statePath, overwrite: true);
		}
		catch
		{
		}
	}

	private void LoadState()
	{
		try
		{
			if (!File.Exists(statePath))
			{
				return;
			}
			BadBlockEntityPersistedState badBlockEntityPersistedState = JsonSerializer.Deserialize<BadBlockEntityPersistedState>(File.ReadAllText(statePath), JsonOptions);
			if (badBlockEntityPersistedState == null)
			{
				return;
			}
			lock (sync)
			{
				entries.Clear();
				foreach (BadBlockEntityRecord item in (from x in badBlockEntityPersistedState.Entries
					where !string.IsNullOrWhiteSpace(x.Key)
					orderby x.LastSeenUnixMilliseconds descending
					select x).Take(500))
				{
					entries[item.Key] = Clone(item);
				}
			}
		}
		catch
		{
		}
	}

	private void EnsureCapacityUnsafe()
	{
		if (entries.Count >= 500)
		{
			string text = (from x in entries.Values
				orderby x.Tracking ? 1 : 0, x.LastSeenUnixMilliseconds
				select x.Key).FirstOrDefault();
			if (text != null)
			{
				entries.Remove(text);
			}
		}
	}

	private static string BuildKey(int x, int y, int z, int dimension, string blockCode, string blockEntityType)
	{
		return $"{dimension}:{x},{y},{z}|{blockCode}|{blockEntityType}";
	}

	private static string Extract(Regex regex, string text, string fallback)
	{
		Match match = regex.Match(text ?? "");
		return match.Success ? match.Groups[1].Value.Trim() : fallback;
	}

	private static string Trim(string? value, int max)
	{
		string text = (value ?? "").Trim();
		return (text.Length <= max) ? text : text.Substring(0, max);
	}

	private static BadBlockEntityRecord Clone(BadBlockEntityRecord x)
	{
		return new BadBlockEntityRecord
		{
			Key = x.Key,
			X = x.X,
			Y = x.Y,
			Z = x.Z,
			Dimension = x.Dimension,
			BlockCode = x.BlockCode,
			BlockEntityType = x.BlockEntityType,
			Occurrences = x.Occurrences,
			FirstSeenUnixMilliseconds = x.FirstSeenUnixMilliseconds,
			LastSeenUnixMilliseconds = x.LastSeenUnixMilliseconds,
			LastException = x.LastException,
			LastTopFrame = x.LastTopFrame,
			Tracking = x.Tracking,
			LastAction = x.LastAction,
			LastActionBy = x.LastActionBy,
			LastActionUnixMilliseconds = x.LastActionUnixMilliseconds
		};
	}
}
