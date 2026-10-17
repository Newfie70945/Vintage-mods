using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlockLogger.Services;
using Newfies.Storage;
using NewfiesAntiCheat.Shared.Models;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace BlockLogger.AntiCheat;

internal sealed class AntiCheatHistoryStore : IDisposable
{
	private const int SchemaVersion = 1;

	private readonly ICoreServerAPI api;

	private readonly AntiCheatConfig config;

	private readonly object gate = new object();

	private readonly string statePath;

	private readonly DeferredJsonFileWriter writer;

	private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	private AntiCheatHistoryState state = new AntiCheatHistoryState();

	private bool dirty;

	private bool storageReady;

	private bool disposed;

	private long saveListenerId = -1L;

	public string StorageError { get; private set; } = "";

	public string RecoveryFile { get; private set; } = "";

	public int Count
	{
		get
		{
			lock (gate)
			{
				return state.Incidents.Count;
			}
		}
	}

	public AntiCheatHistoryStore(ICoreServerAPI api, AntiCheatConfig config)
	{
		this.api = api;
		this.config = config;
		string text = Path.Combine(SuiteDataPaths.GetWorldRoot(api), "anticheat");
		Directory.CreateDirectory(text);
		statePath = Path.Combine(text, "incident-history.json");
		writer = new DeferredJsonFileWriter(statePath, (string error) =>
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Incident persistence failed; snapshot retained for retry: {0}", error);
		});
	}

	public void Start()
	{
		Load();
		Trim();
		api.Event.GameWorldSave += OnWorldSave;
		saveListenerId = api.Event.RegisterGameTickListener((float _) =>
		{
			SaveIfDirty();
		}, 60000);
		api.Logger.Notification("[ServerEssentials/AntiCheat] Persistent incident history ready: enabled={0}, records={1}, retention={2}d, max={3}.", config.HistoryEnabled, Count, config.HistoryRetentionDays, config.HistoryMaxRecords);
	}

	public void Record(IServerPlayer player, string code, int severity, string summary, string subject, string evidence, int riskBefore, int riskAfter, string statusAfter, string consequence)
	{
		Record(player, "ServerBehavior", code, severity, summary, subject, evidence, riskBefore, riskAfter, statusAfter, consequence, 0L);
	}

	public void RecordScanFinding(IServerPlayer player, ScanFindingDto finding, int riskBefore, int riskAfter, string statusAfter, string consequence)
	{
		if (finding != null)
		{
			long dedupeWindowMs = (string.Equals(finding.Code, "CROSS_DETECTOR_INCIDENT", StringComparison.OrdinalIgnoreCase) ? ((long)Math.Clamp(config.IncidentCorrelationWindowSeconds, 10, 600) * 1000L) : 600000);
			Record(player, finding.Scanner ?? "Client", finding.Code ?? "", finding.Severity, finding.Summary ?? "", finding.Subject ?? "", finding.Evidence ?? "", riskBefore, riskAfter, statusAfter, consequence, dedupeWindowMs);
		}
	}

	private void Record(IServerPlayer player, string scanner, string code, int severity, string summary, string subject, string evidence, int riskBefore, int riskAfter, string statusAfter, string consequence, long dedupeWindowMs)
	{
		if (!storageReady || !config.HistoryEnabled || player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		long num = NowMs();
		EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
		AntiCheatIncidentRecord row = new AntiCheatIncidentRecord
		{
			Id = 0L,
			UnixMilliseconds = num,
			PlayerUid = (player.PlayerUID ?? ""),
			PlayerName = (player.PlayerName ?? "Unknown"),
			Scanner = Clean(scanner, 64),
			Code = Clean(code, 96),
			Severity = Math.Clamp(severity, 0, 4),
			Summary = Clean(summary, 500),
			Subject = Clean(subject, 160),
			Evidence = Clean(evidence, 1600),
			RiskBefore = Math.Clamp(riskBefore, 0, 100),
			RiskAfter = Math.Clamp(riskAfter, 0, 100),
			StatusAfter = Clean(statusAfter, 40),
			Consequence = Clean(consequence, 500),
			X = (pos?.X ?? 0.0),
			Y = (pos?.Y ?? 0.0),
			Z = (pos?.Z ?? 0.0),
			Dimension = (pos?.Dimension ?? 0)
		};
		lock (gate)
		{
			if (dedupeWindowMs > 0)
			{
				long cutoff = num - dedupeWindowMs;
				if (state.Incidents.Any((AntiCheatIncidentRecord x) => x.UnixMilliseconds >= cutoff && string.Equals(x.PlayerUid, row.PlayerUid, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Scanner, row.Scanner, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Code, row.Code, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Subject, row.Subject, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Evidence, row.Evidence, StringComparison.Ordinal)))
				{
					return;
				}
			}
			row.Id = Math.Max(1L, state.NextId++);
			state.Incidents.Add(row);
			TrimUnsafe(num);
			dirty = true;
		}
	}

	public AntiCheatHistoryWebSnapshot GetSnapshot(string? playerFilter, int limit, string? findingFilter = null)
	{
		lock (gate)
		{
			TrimUnsafe(NowMs());
			string filter = (playerFilter ?? "").Trim();
			string finding = (findingFilter ?? "").Trim();
			IEnumerable<AntiCheatIncidentRecord> source = state.Incidents;
			if (filter.Length > 0)
			{
				source = source.Where((AntiCheatIncidentRecord x) => (x.PlayerUid ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) || (x.PlayerName ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase));
			}
			List<string> findingOptions = (from x in source
				select (x.Code ?? "").Trim() into x
				where x.Length > 0
				select x).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList();
			if (finding.Length > 0)
			{
				source = source.Where((AntiCheatIncidentRecord x) => string.Equals((x.Code ?? "").Trim(), finding, StringComparison.OrdinalIgnoreCase));
			}
			List<AntiCheatIncidentRecord> source2 = source.OrderByDescending((AntiCheatIncidentRecord x) => x.UnixMilliseconds).Take(Math.Clamp(limit, 1, 1000)).ToList();
			Dictionary<string, IReadOnlyList<AntiCheatIncidentRecord>> byPlayer = state.Incidents.Where((AntiCheatIncidentRecord x) => !string.IsNullOrWhiteSpace(x.PlayerUid)).GroupBy((AntiCheatIncidentRecord x) => x.PlayerUid, StringComparer.OrdinalIgnoreCase).ToDictionary((Func<IGrouping<string, AntiCheatIncidentRecord>, string>)((IGrouping<string, AntiCheatIncidentRecord> g) => g.Key), (Func<IGrouping<string, AntiCheatIncidentRecord>, IReadOnlyList<AntiCheatIncidentRecord>>)((IGrouping<string, AntiCheatIncidentRecord> g) => g.OrderBy((AntiCheatIncidentRecord x) => x.UnixMilliseconds).ToList()), (IEqualityComparer<string>?)StringComparer.OrdinalIgnoreCase);
			List<AntiCheatIncidentWebSnapshot> incidents = source2.Select((AntiCheatIncidentRecord row) => ToWeb(row, byPlayer, config.IncidentCorrelationWindowSeconds)).ToList();
			long dayAgo = NowMs() - 86400000;
			return new AntiCheatHistoryWebSnapshot
			{
				StorageReady = storageReady,
				StorageError = ((writer.LastError.Length > 0) ? writer.LastError : StorageError),
				Enabled = config.HistoryEnabled,
				RetentionDays = config.HistoryRetentionDays,
				MaxRecords = config.HistoryMaxRecords,
				TotalRecords = state.Incidents.Count,
				UniquePlayers = (from x in state.Incidents
					select x.PlayerUid into x
					where x.Length > 0
					select x).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
				Last24Hours = state.Incidents.Count((AntiCheatIncidentRecord x) => x.UnixMilliseconds >= dayAgo),
				PlayerFilter = filter,
				FindingFilter = finding,
				FindingOptions = findingOptions,
				Incidents = incidents
			};
		}
	}

	public AntiCheatHistorySummaryWebSnapshot GetSummary()
	{
		lock (gate)
		{
			TrimUnsafe(NowMs());
			long dayAgo = NowMs() - 86400000;
			return new AntiCheatHistorySummaryWebSnapshot
			{
				Enabled = config.HistoryEnabled,
				RetentionDays = config.HistoryRetentionDays,
				MaxRecords = config.HistoryMaxRecords,
				TotalRecords = state.Incidents.Count,
				UniquePlayers = (from x in state.Incidents
					select x.PlayerUid into x
					where x.Length > 0
					select x).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
				Last24Hours = state.Incidents.Count((AntiCheatIncidentRecord x) => x.UnixMilliseconds >= dayAgo)
			};
		}
	}

	public int ClearAll()
	{
		int count;
		lock (gate)
		{
			count = state.Incidents.Count;
			state.Incidents.Clear();
			state.NextId = 1L;
			dirty = true;
		}
		SaveIfDirty();
		return count;
	}

	public void ApplyRetentionNow()
	{
		lock (gate)
		{
			int count = state.Incidents.Count;
			TrimUnsafe(NowMs());
			if (state.Incidents.Count != count)
			{
				dirty = true;
			}
		}
		SaveIfDirty();
	}

	private static AntiCheatIncidentWebSnapshot ToWeb(AntiCheatIncidentRecord row, IReadOnlyDictionary<string, IReadOnlyList<AntiCheatIncidentRecord>> byPlayer, int correlationWindowSeconds)
	{
		long num = (long)Math.Clamp(correlationWindowSeconds, 10, 600) * 1000L;
		List<AntiCheatIncidentRecord> list = new List<AntiCheatIncidentRecord>(16);
		if (byPlayer.TryGetValue(row.PlayerUid ?? "", out IReadOnlyList<AntiCheatIncidentRecord> value) && value.Count > 0)
		{
			long num2 = row.UnixMilliseconds - num;
			long num3 = row.UnixMilliseconds + num;
			int num4 = 0;
			int num5 = value.Count;
			while (num4 < num5)
			{
				int num6 = num4 + (num5 - num4 >> 1);
				if (value[num6].UnixMilliseconds < num2)
				{
					num4 = num6 + 1;
				}
				else
				{
					num5 = num6;
				}
			}
			for (int i = num4; i < value.Count && value[i].UnixMilliseconds <= num3; i++)
			{
				list.Add(value[i]);
			}
			if (list.Count > 16)
			{
				list = list.OrderBy((AntiCheatIncidentRecord x) => Math.Abs(x.UnixMilliseconds - row.UnixMilliseconds)).Take(16).ToList();
			}
		}
		List<ScanFindingDto> list2 = list.Select((AntiCheatIncidentRecord x) => new ScanFindingDto
		{
			Scanner = (x.Scanner ?? ""),
			Code = (x.Code ?? ""),
			Severity = x.Severity,
			Summary = (x.Summary ?? ""),
			Subject = (x.Subject ?? ""),
			Evidence = (x.Evidence ?? "")
		}).ToList();
		ScanFindingDto finding = list2.FirstOrDefault((ScanFindingDto x) => string.Equals(x.Code, row.Code, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Evidence, row.Evidence, StringComparison.Ordinal)) ?? new ScanFindingDto
		{
			Scanner = (row.Scanner ?? ""),
			Code = (row.Code ?? ""),
			Severity = row.Severity,
			Summary = (row.Summary ?? ""),
			Subject = (row.Subject ?? ""),
			Evidence = (row.Evidence ?? "")
		};
		AntiCheatFindingInsight antiCheatFindingInsight = AntiCheatFindingIntelligence.Analyze(finding, list2);
		return new AntiCheatIncidentWebSnapshot
		{
			Id = row.Id,
			UnixMilliseconds = row.UnixMilliseconds,
			PlayerUid = (row.PlayerUid ?? ""),
			PlayerName = (row.PlayerName ?? ""),
			Scanner = (row.Scanner ?? ""),
			Code = (row.Code ?? ""),
			Severity = row.Severity,
			Summary = (row.Summary ?? ""),
			Subject = (row.Subject ?? ""),
			Evidence = (row.Evidence ?? ""),
			RiskBefore = row.RiskBefore,
			RiskAfter = row.RiskAfter,
			StatusAfter = (row.StatusAfter ?? ""),
			Consequence = (row.Consequence ?? ""),
			X = row.X,
			Y = row.Y,
			Z = row.Z,
			Dimension = row.Dimension,
			Category = (antiCheatFindingInsight.Category ?? ""),
			Confidence = (antiCheatFindingInsight.Confidence ?? "Low"),
			ConfidenceScore = antiCheatFindingInsight.ConfidenceScore,
			CorroboratingSignals = antiCheatFindingInsight.CorroboratingSignals,
			Why = (antiCheatFindingInsight.Why ?? ""),
			RecommendedAction = (antiCheatFindingInsight.RecommendedAction ?? "")
		};
	}

	private void Load()
	{
		try
		{
			if (!File.Exists(statePath))
			{
				state = new AntiCheatHistoryState
				{
					SchemaVersion = 1
				};
				storageReady = true;
				return;
			}
			AntiCheatHistoryState antiCheatHistoryState = JsonStateRecovery.Read<AntiCheatHistoryState>(statePath, jsonOptions);
			if (antiCheatHistoryState.SchemaVersion > 1 || antiCheatHistoryState.SchemaVersion < 1)
			{
				throw new NotSupportedException("Unsupported incident state schema; original file was retained.");
			}
			if (antiCheatHistoryState.Incidents == null)
			{
				throw new InvalidDataException("Incident collection is null.");
			}
			bool flag = antiCheatHistoryState.Incidents.Any((AntiCheatIncidentRecord x) => x == null || x.Id <= 0 || x.Id == long.MaxValue || x.PlayerUid == null || x.PlayerName == null || x.Scanner == null || x.Code == null || x.Summary == null || x.Subject == null || x.Evidence == null || x.StatusAfter == null || x.Consequence == null) || (from x in antiCheatHistoryState.Incidents
				where x != null
				group x by x.Id).Any((IGrouping<long, AntiCheatIncidentRecord> g) => g.Count() > 1);
			if (flag)
			{
				RecoveryFile = JsonStateRecovery.Preserve(statePath);
			}
			antiCheatHistoryState.Incidents = (from x in antiCheatHistoryState.Incidents
				where x != null && x.Id > 0 && x.Id < long.MaxValue
				group x by x.Id into g
				select g.OrderByDescending((AntiCheatIncidentRecord x) => x.UnixMilliseconds).First()).Select(Clone).ToList();
			antiCheatHistoryState.NextId = Math.Max(antiCheatHistoryState.NextId, (antiCheatHistoryState.Incidents.Count == 0) ? 1 : (antiCheatHistoryState.Incidents.Max((AntiCheatIncidentRecord x) => x.Id) + 1));
			if (antiCheatHistoryState.NextId <= 0 || antiCheatHistoryState.NextId == long.MaxValue)
			{
				throw new NotSupportedException("Incident IDs cannot safely advance; original file was retained.");
			}
			state = antiCheatHistoryState;
			storageReady = true;
			StorageError = (flag ? "Recovered valid incidents; original file preserved in recovery/." : "");
		}
		catch (Exception ex)
		{
			storageReady = false;
			StorageError = ex.Message;
			if (ex is JsonException || ex is InvalidDataException)
			{
				try
				{
					RecoveryFile = JsonStateRecovery.Preserve(statePath);
					state = new AntiCheatHistoryState
					{
						SchemaVersion = 1
					};
					storageReady = true;
					StorageError = "Damaged incident file preserved in recovery/; a new history is available.";
				}
				catch (Exception ex2)
				{
					StorageError = StorageError + " Recovery failed: " + ex2.Message;
				}
			}
			api.Logger.Warning("[ServerEssentials/AntiCheat] Incident recovery: writable={0}; {1}", storageReady, StorageError);
		}
	}

	private void Trim()
	{
		lock (gate)
		{
			int count = state.Incidents.Count;
			TrimUnsafe(NowMs());
			if (count != state.Incidents.Count)
			{
				dirty = true;
			}
		}
	}

	private void TrimUnsafe(long now)
	{
		AntiCheatHistoryState antiCheatHistoryState = state;
		if (antiCheatHistoryState.Incidents == null)
		{
			List<AntiCheatIncidentRecord> list = (antiCheatHistoryState.Incidents = new List<AntiCheatIncidentRecord>());
		}
		long cutoff = now - (long)config.HistoryRetentionDays * 86400000L;
		if (cutoff > 0)
		{
			state.Incidents.RemoveAll((AntiCheatIncidentRecord x) => x.UnixMilliseconds > 0 && x.UnixMilliseconds < cutoff);
		}
		int num = Math.Max(100, config.HistoryMaxRecords);
		if (state.Incidents.Count > num)
		{
			state.Incidents = (from x in state.Incidents.OrderByDescending((AntiCheatIncidentRecord x) => x.UnixMilliseconds).Take(num)
				orderby x.UnixMilliseconds
				select x).ToList();
		}
	}

	private void OnWorldSave()
	{
		SaveIfDirty();
		if (!writer.Flush())
		{
			api.Logger.Warning("[ServerEssentials/AntiCheat] Incident world-save flush did not complete; pending snapshot remains queued.");
		}
	}

	private void SaveIfDirty()
	{
		if (dirty && !disposed)
		{
			Save();
		}
	}

	private void Save()
	{
		if (!storageReady)
		{
			return;
		}
		AntiCheatHistoryState snapshot;
		lock (gate)
		{
			snapshot = new AntiCheatHistoryState
			{
				SchemaVersion = 1,
				NextId = state.NextId,
				Incidents = state.Incidents.Select(Clone).ToList()
			};
			dirty = false;
		}
		if (writer.Queue(() => JsonSerializer.SerializeToUtf8Bytes(snapshot, jsonOptions)))
		{
			return;
		}
		lock (gate)
		{
			dirty = true;
		}
	}

	private static AntiCheatIncidentRecord Clone(AntiCheatIncidentRecord x)
	{
		return new AntiCheatIncidentRecord
		{
			Id = x.Id,
			UnixMilliseconds = x.UnixMilliseconds,
			PlayerUid = (x.PlayerUid ?? ""),
			PlayerName = (x.PlayerName ?? ""),
			Scanner = (x.Scanner ?? ""),
			Code = (x.Code ?? ""),
			Severity = x.Severity,
			Summary = (x.Summary ?? ""),
			Subject = (x.Subject ?? ""),
			Evidence = (x.Evidence ?? ""),
			RiskBefore = x.RiskBefore,
			RiskAfter = x.RiskAfter,
			StatusAfter = (x.StatusAfter ?? ""),
			Consequence = (x.Consequence ?? ""),
			X = x.X,
			Y = x.Y,
			Z = x.Z,
			Dimension = x.Dimension
		};
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		if (dirty)
		{
			try
			{
				Save();
			}
			catch
			{
			}
		}
		disposed = true;
		writer.Dispose();
		api.Event.GameWorldSave -= OnWorldSave;
		if (saveListenerId >= 0)
		{
			api.Event.UnregisterGameTickListener(saveListenerId);
			saveListenerId = -1L;
		}
	}

	private static string Clean(string? value, int max)
	{
		string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
		return (text.Length <= max) ? text : text.Substring(0, max);
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}
}
